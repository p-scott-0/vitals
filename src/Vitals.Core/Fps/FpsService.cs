using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Vitals.Core.Fps;

/// <summary>
/// FPS capture via Intel PresentMon (console build): passive ETW frame telemetry, no game
/// injection. Watches the foreground process and attaches PresentMon to it; aggregates
/// frame times into a once-per-second FPS reading and optionally logs sessions to CSV.
/// Requires admin (ETW), which Vitals already runs as.
///
/// Threading rules: the lock only ever guards field updates — never process start/kill,
/// file I/O, or sorting — and both timers skip a tick if the previous one is still running,
/// so nothing here can pile up threadpool threads.
/// </summary>
public sealed class FpsService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "searchhost", "searchapp", "startmenuexperiencehost",
        "shellexperiencehost", "textinputhost", "lockapp", "taskmgr", "systemsettings",
        "vitals", "vitals.app", "vitals.probe", "fancontrol", "hwinfo64",
    };

    private readonly string _presentMonPath;
    private readonly string _logDir;
    private readonly System.Threading.Timer _foregroundTimer;
    private readonly System.Threading.Timer _aggregateTimer;
    private readonly object _lock = new();
    private int _foregroundBusy;
    private int _aggregateBusy;

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
    private DateTime _sessionStart;
    private int _emptyTicks;

    public bool Enabled { get; set; }
    public bool LoggingEnabled { get; set; } = true;
    public double? CurrentFps { get; private set; }
    public string? CurrentProcess { get; private set; }
    public string StateText { get; private set; } = "idle";
    public event EventHandler? Tick;

    /// <summary>Time since capture attached to the current game, or null when idle.</summary>
    public TimeSpan? Playtime
    {
        get
        {
            var start = _sessionStart;
            return _proc != null ? DateTime.Now - start : null;
        }
    }

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

    private void CheckForeground()
    {
        if (!Enabled || !PresentMonAvailable)
        {
            if (_proc != null) StopCapture();
            StateText = PresentMonAvailable ? "idle" : "PresentMon missing";
            return;
        }

        GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
        int pid = (int)fgPid;
        if (pid <= 4) return;

        string name;
        try { name = Process.GetProcessById(pid).ProcessName; }
        catch { return; }

        if (IgnoredProcesses.Contains(name))
            return; // keep the current capture while e.g. alt-tabbed to the desktop

        if (pid == _targetPid) return;

        // require the same foreground pid twice in a row before switching (debounce)
        if (pid == _candidatePid) _candidateSeen++;
        else { _candidatePid = pid; _candidateSeen = 1; }
        if (_candidateSeen < 2) return;

        StopCapture();
        StartCapture(pid, name);
    }

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
            if (proc == null) { StateText = "failed to start PresentMon"; return; }
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
            StateText = "error: " + ex.Message;
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
            _sessionStart = DateTime.Now;
            _emptyTicks = 0;
            _log = log;
            _proc = proc;
        }

        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => StopCapture();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        CurrentProcess = name;
        StateText = $"watching {name}";
    }

    private void StopCapture()
    {
        Process? proc;
        StreamWriter? log;
        List<double> frames;
        string name;
        DateTime started;

        lock (_lock)
        {
            proc = _proc;
            log = _log;
            frames = _sessionFrameTimes;
            name = _targetName;
            started = _sessionStart;
            _proc = null;
            _log = null;
            _sessionFrameTimes = new List<double>();
            _targetPid = 0;
            _targetName = "";
        }
        if (proc == null && log == null) return;

        CurrentFps = null;
        CurrentProcess = null;
        if (StateText.StartsWith("watching")) StateText = "idle";

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
        }
        if (log != null && logLine != null)
        {
            try { log.WriteLine(logLine); } catch { }
        }
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _foregroundTimer.Dispose();
        _aggregateTimer.Dispose();
        StopCapture();
    }
}
