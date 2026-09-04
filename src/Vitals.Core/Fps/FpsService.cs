using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Vitals.Core.Fps;

/// <summary>
/// FPS capture via Intel PresentMon (console build): passive ETW frame telemetry, no game
/// injection. Requires admin (ETW), which Vitals already runs as.
///
/// Two layers:
///  - Capture: PresentMon is attached to the foreground process (ignoring shells, browsers,
///    launchers, chat apps) and its frame times are aggregated into a per-second FPS reading.
///  - Game session: a captured process is promoted to "the game" when it looks like one —
///    borderless/fullscreen window, a game-library install path, a known name, or two minutes of
///    sustained FPS (which learns the name for next time). Once a game is tracked, alt-tabbing to
///    anything else changes nothing: capture and playtime stay with the game until it exits.
///
/// Threading rules: the lock only ever guards field updates — never process start/kill,
/// file I/O, or sorting — and both timers skip a tick if the previous one is still running.
/// </summary>
public sealed class FpsService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int LearnSecondsNeeded = 120;
    private const double LearnMinFps = 25;

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // shell / system
        "explorer", "dwm", "searchhost", "searchapp", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "lockapp", "taskmgr", "systemsettings", "applicationframehost", "runtimebroker",
        // ourselves + other monitoring tools
        "vitals", "vitals.app", "vitals.probe", "fancontrol", "hwinfo64", "msiafterburner", "rtss",
        // browsers, chat and media: present frames constantly but are never the game
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "discord", "slack", "teams", "ms-teams",
        "spotify", "vlc", "mpc-hc64", "mpc-hc", "mpv", "wmplayer", "video.ui", "netflix",
        // launchers and store clients
        "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient", "upc", "ubisoftconnect", "battle.net",
        "origin", "eadesktop", "riotclientservices", "riotclientux", "gamebar", "xboxapp", "xboxpcapp",
        // dev tools / terminals / misc desktop apps
        "code", "devenv", "rider64", "windowsterminal", "cmd", "powershell", "pwsh", "conhost",
        "notepad", "notepad++", "obs64",
    };

    /// <summary>Install folders that mark an exe as a game regardless of how its window looks.</summary>
    private static readonly Regex GameLibraryPath = new(
        @"[\\/](steamapps[\\/]common|Epic Games|GOG Galaxy[\\/]Games|GOG Games|XboxGames|Ubisoft Game Launcher[\\/]games|EA Games|Origin Games|Riot Games|Games)[\\/]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _presentMonPath;
    private readonly string _logDir;
    private readonly System.Threading.Timer _foregroundTimer;
    private readonly System.Threading.Timer _aggregateTimer;
    private readonly object _lock = new();
    private int _foregroundBusy;
    private int _aggregateBusy;

    // capture
    private Process? _proc;
    private int _targetPid;
    private string _targetName = "";
    private int _candidatePid;
    private int _candidateSeen;
    private int _frameTimeColumn = -1;
    private int _framesSinceTick;
    private double _msSinceTick;
    private List<double> _sessionFrameTimes = new();
    private StreamWriter? _log;
    private DateTime _captureStart;
    private int _emptyTicks;
    private string? _error;

    // game session
    private int _gamePid;
    private string _gameName = "";
    private DateTime _gameStart;
    private int _learnSeconds;
    private double _targetCoverage;   // how much of its monitor the captured app's window covers while foreground

    public bool Enabled { get; set; }
    public bool LoggingEnabled { get; set; } = true;
    public double? CurrentFps { get; private set; }
    public string? CurrentProcess { get; private set; }
    public event EventHandler? Tick;

    /// <summary>Process names treated as games; seeded from settings, grown by auto-learning.</summary>
    public HashSet<string> KnownGames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A captured process held 25+ FPS for two minutes and is now considered a game.</summary>
    public event Action<string>? GameLearned;

    /// <summary>(game, session start, duration) — raised when the tracked game exits.</summary>
    public event Action<string, DateTime, TimeSpan>? GameSessionEnded;

    public string? CurrentGame => _gamePid != 0 ? _gameName : null;

    /// <summary>Time since the tracked game was launched, or null when no game is tracked.</summary>
    public TimeSpan? Playtime => _gamePid != 0 ? DateTime.Now - _gameStart : null;

    public string StateText =>
        !PresentMonAvailable ? "PresentMon missing"
        : !Enabled ? "off"
        : _error != null ? "error: " + _error
        : _gamePid != 0 ? "game: " + _gameName
        : _proc != null ? $"watching {_targetName} (not detected as a game)"
        : "idle";

    public FpsService(string presentMonPath, string logDir)
    {
        _presentMonPath = presentMonPath;
        _logDir = logDir;
        _foregroundTimer = new System.Threading.Timer(_ => Guarded(ref _foregroundBusy, CheckForeground), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _aggregateTimer = new System.Threading.Timer(_ => Guarded(ref _aggregateBusy, Aggregate), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Run a timer body unless the previous run is still in progress.</summary>
    private static void Guarded(ref int busyFlag, Action body)
    {
        if (Interlocked.Exchange(ref busyFlag, 1) == 1) return;
        try { body(); }
        catch { /* never let a timer body take the app down */ }
        finally { Volatile.Write(ref busyFlag, 0); }
    }

    public bool PresentMonAvailable => File.Exists(_presentMonPath);

    // ---- foreground / game detection --------------------------------------

    private void CheckForeground()
    {
        if (!Enabled || !PresentMonAvailable)
        {
            if (_proc != null) StopCapture();
            if (_gamePid != 0) EndGameSession();
            return;
        }

        // the tracked game may have exited without PresentMon noticing (e.g. it was never captured)
        if (_gamePid != 0 && !IsRunning(_gamePid)) EndGameSession();

        IntPtr hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out uint fgPid);
        int pid = (int)fgPid;
        if (pid <= 4 || pid == Environment.ProcessId) return;

        string name;
        try { name = Process.GetProcessById(pid).ProcessName; }
        catch { return; }

        _targetCoverage = pid == _targetPid ? WindowCoverage(hwnd, out _) : 0;

        if (IgnoredProcesses.Contains(name))
            return; // desktop, browser, Discord...: keep whatever we have

        // require the same foreground pid twice in a row before acting (debounce)
        if (pid == _candidatePid) _candidateSeen++;
        else { _candidatePid = pid; _candidateSeen = 1; }
        if (_candidateSeen < 2) return;

        bool isGame = IsLikelyGame(pid, name, hwnd);

        if (_gamePid != 0)
        {
            if (pid == _gamePid) return;
            if (!isGame) return;            // alt-tabbed to some other app: the game session continues
            EndGameSession();               // switched to a different game
            StopCapture();
            StartCapture(pid, name);
            StartGameSession(pid, name);
            return;
        }

        if (pid == _targetPid)
        {
            if (isGame) StartGameSession(pid, name); // e.g. a captured app just went fullscreen
            return;
        }

        StopCapture();
        StartCapture(pid, name);
        if (isGame) StartGameSession(pid, name);
    }

    private bool IsLikelyGame(int pid, string name, IntPtr hwnd)
    {
        if (KnownGames.Contains(name)) return true;
        string? path = TryGetPath(pid);
        if (path != null && GameLibraryPath.IsMatch(path)) return true;
        double coverage = WindowCoverage(hwnd, out bool hasCaption);
        return !hasCaption && coverage >= 0.85; // borderless or exclusive fullscreen
    }

    private static double WindowCoverage(IntPtr hwnd, out bool hasCaption)
    {
        hasCaption = true;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return 0;
        IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return 0;
        long windowArea = Math.Max(0, r.Right - r.Left) * (long)Math.Max(0, r.Bottom - r.Top);
        long monitorArea = (mi.rcMonitor.Right - mi.rcMonitor.Left) * (long)(mi.rcMonitor.Bottom - mi.rcMonitor.Top);
        hasCaption = (GetWindowLong32(hwnd, GwlStyle) & WsCaption) == WsCaption;
        return monitorArea == 0 ? 0 : Math.Min(1.0, (double)windowArea / monitorArea);
    }

    private static string? TryGetPath(int pid)
    {
        try { return Process.GetProcessById(pid).MainModule?.FileName; }
        catch { return null; } // protected / anti-cheat processes refuse this; other signals still apply
    }

    private static bool IsRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    private static DateTime? TryStartTime(int pid)
    {
        try { return Process.GetProcessById(pid).StartTime; }
        catch { return null; }
    }

    private void StartGameSession(int pid, string name)
    {
        _gamePid = pid;
        _gameName = name;
        _gameStart = TryStartTime(pid) ?? DateTime.Now; // playtime counts from launch, like Cortex
        _learnSeconds = 0;
    }

    private void EndGameSession()
    {
        if (_gamePid == 0) return;
        string name = _gameName;
        DateTime start = _gameStart;
        _gamePid = 0;
        _gameName = "";
        try { GameSessionEnded?.Invoke(name, start, DateTime.Now - start); } catch { }
    }

    // ---- PresentMon capture -------------------------------------------------

    private void StartCapture(int pid, string name)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _presentMonPath,
            Arguments = $"--process_id {pid} --output_stdout --stop_existing_session " +
                        "--session_name VitalsPM --terminate_on_proc_exit --no_console_stats",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process? proc;
        StreamWriter? log = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null) { _error = "failed to start PresentMon"; return; }
            if (LoggingEnabled)
            {
                Directory.CreateDirectory(_logDir);
                string file = Path.Combine(_logDir, $"fps-{DateTime.Now:yyyyMMdd-HHmmss}-{name}.csv");
                log = new StreamWriter(file) { AutoFlush = true };
                log.WriteLine("unix_seconds,fps");
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return;
        }

        lock (_lock)
        {
            _targetPid = pid;
            _targetName = name;
            _frameTimeColumn = -1;
            _framesSinceTick = 0;
            _msSinceTick = 0;
            _sessionFrameTimes = new List<double>();
            _captureStart = DateTime.Now;
            _emptyTicks = 0;
            _learnSeconds = 0;
            _log = log;
            _proc = proc;
            _error = null;
        }

        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => StopCapture();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        CurrentProcess = name;
    }

    private void StopCapture()
    {
        Process? proc;
        StreamWriter? log;
        List<double> frames;
        string name;
        DateTime started;
        int stoppedPid;

        lock (_lock)
        {
            proc = _proc;
            log = _log;
            frames = _sessionFrameTimes;
            name = _targetName;
            started = _captureStart;
            stoppedPid = _targetPid;
            _proc = null;
            _log = null;
            _sessionFrameTimes = new List<double>();
            _targetPid = 0;
            _targetName = "";
        }
        if (proc == null && log == null) return;

        CurrentFps = null;
        CurrentProcess = null;

        // the game closed (PresentMon exits with it): close the session too
        if (_gamePid != 0 && _gamePid == stoppedPid) EndGameSession();

        // everything slow happens outside the lock
        if (proc != null)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { proc.Dispose(); } catch { }
        }
        if (log != null)
        {
            try
            {
                if (frames.Count > 10)
                {
                    double avg = 1000.0 / frames.Average();
                    var sorted = frames.OrderByDescending(t => t).ToList();
                    int onePct = Math.Max(1, sorted.Count / 100);
                    double p1Low = 1000.0 / sorted.Take(onePct).Average();
                    double dur = (DateTime.Now - started).TotalSeconds;
                    log.WriteLine($"# process={name} duration_s={dur:0} frames={frames.Count} avg_fps={avg:0.0} one_percent_low={p1Low:0.0}");
                }
            }
            catch { }
            try { log.Dispose(); } catch { }
        }
    }

    private void OnLine(string line)
    {
        var parts = line.Split(',');
        lock (_lock)
        {
            if (_proc == null) return;
            if (_frameTimeColumn < 0)
            {
                // header row: locate the frame-time column across PresentMon 1.x/2.x schemas
                string[] candidates = ["FrameTime", "msBetweenPresents", "MsBetweenPresents"];
                for (int i = 0; i < parts.Length; i++)
                {
                    string col = parts[i].Trim();
                    if (candidates.Any(c => string.Equals(c, col, StringComparison.OrdinalIgnoreCase)))
                    {
                        _frameTimeColumn = i;
                        return;
                    }
                }
                return; // not the header (banner/etc.) — keep looking
            }

            if (_frameTimeColumn >= parts.Length) return;
            if (!double.TryParse(parts[_frameTimeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                return;
            if (ms <= 0 || ms > 10000) return;

            _framesSinceTick++;
            _msSinceTick += ms;
            if (_sessionFrameTimes.Count < 1_000_000)
                _sessionFrameTimes.Add(ms);
        }
    }

    private void Aggregate()
    {
        StreamWriter? log = null;
        string? logLine = null;
        string? learned = null;
        lock (_lock)
        {
            if (_proc == null)
            {
                CurrentFps = null;
            }
            else if (_framesSinceTick > 0)
            {
                CurrentFps = 1000.0 * _framesSinceTick / _msSinceTick;
                _emptyTicks = 0;
                log = _log;
                logLine = string.Create(CultureInfo.InvariantCulture,
                    $"{(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds:0},{CurrentFps:0.0}");
            }
            else
            {
                _emptyTicks++;
                if (_emptyTicks >= 3) CurrentFps = null; // minimised or not presenting
            }
            _framesSinceTick = 0;
            _msSinceTick = 0;

            // auto-learn: a big, foreground window holding game-like frame rates for two minutes is a game
            if (_proc != null && _gamePid == 0)
            {
                if (CurrentFps >= LearnMinFps && _targetCoverage >= 0.5) _learnSeconds++;
                else _learnSeconds = Math.Max(0, _learnSeconds - 2);
                if (_learnSeconds >= LearnSecondsNeeded && !KnownGames.Contains(_targetName))
                {
                    KnownGames.Add(_targetName);
                    learned = _targetName;
                    StartGameSession(_targetPid, _targetName);
                }
            }
        }
        if (log != null && logLine != null)
        {
            try { log.WriteLine(logLine); } catch { }
        }
        if (learned != null)
        {
            try { GameLearned?.Invoke(learned); } catch { }
        }
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _foregroundTimer.Dispose();
        _aggregateTimer.Dispose();
        StopCapture();
        EndGameSession();
    }
}
