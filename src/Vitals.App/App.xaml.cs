using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Vitals.Core;
using Vitals.Core.Config;
using Vitals.Core.Fps;
using Vitals.Core.Net;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Vitals.App;

public static class UiBrushes
{
    public static readonly Brush TextHi = Frozen(0xE8, 0xEC, 0xF4);
    public static readonly Brush TextLo = Frozen(0x8A, 0x93, 0xA6);
    public static readonly Brush Accent = Frozen(0x22, 0xD3, 0xEE);
    public static readonly Brush Warn = Frozen(0xF5, 0xA6, 0x23);
    public static readonly Brush Hot = Frozen(0xEF, 0x44, 0x44);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

/// <summary>One resolved overlay/dashboard metric. Zone is non-null only when a temp is in warn/hot territory.</summary>
public readonly record struct OverlayValue(string Group, string Label, string Text, Brush? Zone);

public partial class App : Application
{
    public SensorHub Hub { get; private set; } = null!;
    public FpsService Fps { get; private set; } = null!;
    public NetMonitor Net { get; private set; } = null!;
    public VitalsSettings Settings { get; private set; } = null!;
    public RingBuffer FpsHistory { get; } = new(3600);
    public bool IsExiting { get; private set; }

    private const string MutexName = "VitalsAppSingleton";
    private const string ExitEventName = "VitalsExitRequest";

    private Mutex? _mutex;
    private EventWaitHandle? _exitEvent;
    private WF.NotifyIcon? _tray;
    private MainWindow? _main;
    private OverlayWindow? _overlay;
    private DispatcherTimer? _overlayTimer;
    private WF.ToolStripMenuItem? _trayOverlayItem, _trayLockItem, _trayFpsItem;

    /// <summary>Every metric the overlay can show: role key, display group, short label.</summary>
    public static readonly (string Role, string Group, string Label)[] MetricCatalog =
    [
        ("fps", "FPS", "FPS"),
        ("cpu.temp", "CPU", "Temp"), ("cpu.load", "CPU", "Load"), ("cpu.clock", "CPU", "Clock"),
        ("gpu.temp", "GPU", "Temp"), ("gpu.hotspot", "GPU", "VRAM temp"), ("gpu.load", "GPU", "Load"), ("gpu.power", "GPU", "Power"),
        ("ram.load", "RAM", "Used"),
        ("board.vrm", "BOARD", "VRM"), ("board.system", "BOARD", "Sys"), ("ssd.temp", "BOARD", "SSD"),
        ("fan.cpu", "FANS", "CPU"), ("fan.pump", "FANS", "Pump"), ("fan.case", "FANS", "Case"), ("fan.gpu", "FANS", "GPU"),
        ("net.down", "NET", "↓"), ("net.up", "NET", "↑"),
        ("playtime", "PLAY", "Session"),
        ("time", "TIME", "Clock"),
    ];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase)))
        {
            RunInstaller();
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // A newer build replacing a running one: ask the old instance to exit, then take over.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ExitEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { }

            bool acquired = false;
            for (int i = 0; i < 40 && !acquired; i++)
            {
                Thread.Sleep(250);
                try { acquired = _mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
            }
            if (!acquired)
            {
                MessageBox.Show("Vitals is already running — check the tray.", "Vitals");
                Shutdown();
                return;
            }
        }

        _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName, out bool newEvent);
        if (!newEvent) _exitEvent.Reset();
        new Thread(() =>
        {
            _exitEvent.WaitOne();
            Dispatcher.Invoke(ExitApp);
        })
        { IsBackground = true, Name = "Vitals.ExitWatch" }.Start();

        Settings = SettingsStore.Load();
        new Thread(RepairStartupTaskIfMoved) { IsBackground = true, Name = "Vitals.StartupTask" }.Start();

        Hub = new SensorHub(Settings.StorageSensors)
        {
            PollInterval = TimeSpan.FromSeconds(Math.Clamp(Settings.PollSeconds, 1, 10)),
        };
        try
        {
            Hub.Open();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Sensor initialisation failed:\n\n" + ex.Message,
                "Vitals", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        Hub.Start();

        Net = new NetMonitor();

        Fps = new FpsService(FindPresentMon(), Path.Combine(SettingsStore.Dir, "logs"))
        {
            Enabled = Settings.FpsEnabled,
            LoggingEnabled = Settings.FpsLogging,
        };
        Fps.Tick += (_, _) =>
        {
            var f = Fps.CurrentFps;
            if (f.HasValue) FpsHistory.Add(DateTime.UtcNow, f.Value);
        };

        BuildTray();

        _main = new MainWindow(this);
        _main.Show();

        if (Settings.OverlayEnabled)
        {
            Settings.OverlayEnabled = false; // ShowOverlay flips it back on
            ShowOverlay();
        }

        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _overlayTimer.Tick += (_, _) => _overlay?.UpdateValues(ResolveMetric);
        _overlayTimer.Start();
    }

    public static string VersionText
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v is null ? "dev" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static string FindPresentMon()
    {
        // dev layouts: a copy beside the exe, or the repo's tools/ folder
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "presentmon", "PresentMon.exe"),
            Path.Combine(AppContext.BaseDirectory, "PresentMon.exe"),
        };
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "tools", "presentmon", "PresentMon.exe"));
        var found = candidates.FirstOrDefault(File.Exists);
        if (found != null) return found;

        // release build: unpack the embedded copy once into %LOCALAPPDATA%\Vitals\presentmon
        string target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitals", "presentmon", "PresentMon.exe");
        try
        {
            using var res = typeof(App).Assembly.GetManifestResourceStream("PresentMon.exe");
            if (res != null)
            {
                var existing = new FileInfo(target);
                if (!existing.Exists || existing.Length != res.Length)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var fs = File.Create(target);
                    res.CopyTo(fs);
                }
            }
        }
        catch
        {
            // the FPS service reports "PresentMon missing" if this failed
        }
        return target;
    }

    // ---- metric resolution ------------------------------------------------

    public TrackedSensor? Resolve(string role) =>
        Settings.SensorMap.TryGetValue(role, out var id) && Hub.Get(id) is { } mapped
            ? mapped
            : Hub.AutoPick(role);

    public OverlayValue? ResolveMetric(string role)
    {
        var entry = MetricCatalog.FirstOrDefault(m => m.Role == role);
        string group = entry.Group ?? "";
        string label = entry.Label ?? role;

        switch (role)
        {
            case "fps":
            {
                double? f = Fps.CurrentFps;
                return new OverlayValue(group, label, f.HasValue ? Math.Round(f.Value).ToString() : "--", null);
            }
            case "time":
            {
                string text;
                try { text = DateTime.Now.ToString(string.IsNullOrWhiteSpace(Settings.TimeFormat) ? "HH:mm" : Settings.TimeFormat); }
                catch { text = DateTime.Now.ToString("HH:mm"); }
                return new OverlayValue(group, label, text, null);
            }
            case "playtime":
            {
                var t = Fps.Playtime;
                return new OverlayValue(group, label,
                    t.HasValue ? $"{(int)t.Value.TotalHours}:{t.Value.Minutes:00}:{t.Value.Seconds:00}" : "--", null);
            }
            case "net.down":
                return new OverlayValue(group, label, NetMonitor.Format(Net.DownBytesPerSec), null);
            case "net.up":
                return new OverlayValue(group, label, NetMonitor.Format(Net.UpBytesPerSec), null);
        }

        var tracked = Resolve(role);
        if (role == "gpu.hotspot" && tracked != null
            && tracked.Info.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
            label = "Hot Spot";
        if (tracked?.Value is not double v)
            return new OverlayValue(group, label, "--", null);

        string valueText;
        if (role.StartsWith("fan."))
        {
            // fan roles resolve to the RPM sensor; its duty-% twin sits at the same index under /control/
            string rpm = Math.Round(v).ToString();
            double? pct = Hub.Get(tracked.Info.Id.Replace("/fan/", "/control/"))?.Value;
            string? pctText = pct.HasValue ? Math.Round(pct.Value) + "%" : null;
            valueText = Settings.OverlayFanMode switch
            {
                "pct" => pctText ?? rpm,
                "both" => pctText != null ? $"{rpm} · {pctText}" : rpm,
                _ => rpm,
            };
        }
        else
        {
            valueText = role switch
            {
                "gpu.power" => Math.Round(v) + " W",
                "cpu.clock" => (v / 1000).ToString("0.0") + " GHz",
                _ when role.EndsWith(".load") => Math.Round(v) + "%",
                _ => Math.Round(v) + "°",
            };
        }
        return new OverlayValue(group, label, valueText, ZoneBrush(role, v));
    }

    /// <summary>Amber/red brush when a temperature is in warn/hot territory, otherwise null.</summary>
    public static Brush? ZoneBrush(string role, double v)
    {
        (double warn, double hot)? zone = role switch
        {
            "cpu.temp" => (70, 85),
            "gpu.temp" => (70, 83),
            "gpu.hotspot" => (85, 95),
            "board.vrm" => (80, 95),
            "board.system" => (45, 55),
            "ssd.temp" => (60, 70),
            _ => null,
        };
        if (zone is null) return null;
        return v >= zone.Value.hot ? UiBrushes.Hot
             : v >= zone.Value.warn ? UiBrushes.Warn
             : null;
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            string h = hex.Trim();
            if (!h.StartsWith('#')) h = "#" + h;
            return (Color)ColorConverter.ConvertFromString(h)!;
        }
        catch
        {
            return fallback;
        }
    }

    // ---- tray -------------------------------------------------------------

    private void BuildTray()
    {
        _tray = new WF.NotifyIcon { Icon = MakeIcon(), Text = "Vitals", Visible = true };
        var menu = new WF.ContextMenuStrip();
        var open = new WF.ToolStripMenuItem("Open Vitals", null, (_, _) => ShowMain());
        open.Font = new SD.Font(open.Font, SD.FontStyle.Bold);
        _trayOverlayItem = new WF.ToolStripMenuItem("Overlay", null, (_, _) => ToggleOverlay())
        { Checked = Settings.OverlayEnabled };
        _trayLockItem = new WF.ToolStripMenuItem("Overlay click-through", null, (_, _) => SetOverlayLocked(!Settings.OverlayLocked))
        { Checked = Settings.OverlayLocked };
        _trayFpsItem = new WF.ToolStripMenuItem("FPS capture", null, (_, _) => SetFpsEnabled(!Settings.FpsEnabled))
        { Checked = Settings.FpsEnabled };
        menu.Items.Add(open);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(_trayOverlayItem);
        menu.Items.Add(_trayLockItem);
        menu.Items.Add(_trayFpsItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(new WF.ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private static SD.Icon MakeIcon()
    {
        // the exe's own icon (Assets\vitals.ico); fall back to a drawn one for odd hosts
        try
        {
            if (Environment.ProcessPath is { } exe && SD.Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch { }

        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new SD.Pen(SD.Color.FromArgb(0x22, 0xD3, 0xEE), 5f);
            g.DrawArc(pen, 5, 5, 22, 22, 135, 270);
            using var dot = new SD.SolidBrush(SD.Color.FromArgb(0x22, 0xD3, 0xEE));
            g.FillEllipse(dot, 12, 12, 8, 8);
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    // ---- app-level actions ------------------------------------------------

    public void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ToggleOverlay()
    {
        if (Settings.OverlayEnabled) HideOverlay();
        else ShowOverlay();
    }

    public void ShowOverlay()
    {
        Settings.OverlayEnabled = true;
        _overlay ??= new OverlayWindow(this);
        _overlay.Rebuild(Settings.OverlayMetrics);
        _overlay.Show();
        _overlay.UpdateValues(ResolveMetric);
        SyncUi();
        SaveSettings();
    }

    public void HideOverlay()
    {
        Settings.OverlayEnabled = false;
        _overlay?.Hide();
        SyncUi();
        SaveSettings();
    }

    public void SetOverlayLocked(bool locked)
    {
        Settings.OverlayLocked = locked;
        _overlay?.ApplyClickThrough();
        _overlay?.ApplyVisuals();
        SyncUi();
        SaveSettings();
    }

    public void SetFpsEnabled(bool on)
    {
        Settings.FpsEnabled = on;
        Fps.Enabled = on;
        SyncUi();
        SaveSettings();
    }

    /// <summary>Re-apply overlay layout/metrics after a settings change.</summary>
    public void RefreshOverlayConfig()
    {
        if (_overlay == null) return;
        _overlay.Rebuild(Settings.OverlayMetrics);
        _overlay.UpdateValues(ResolveMetric);
    }

    /// <summary>Cheap re-apply for colour/opacity/scale changes (no layout rebuild).</summary>
    public void RefreshOverlayVisuals() => _overlay?.ApplyVisuals();

    public void SyncUi()
    {
        if (_trayOverlayItem != null) _trayOverlayItem.Checked = Settings.OverlayEnabled;
        if (_trayLockItem != null) _trayLockItem.Checked = Settings.OverlayLocked;
        if (_trayFpsItem != null) _trayFpsItem.Checked = Settings.FpsEnabled;
        _main?.SyncFromSettings();
    }

    public void SaveSettings()
    {
        try { SettingsStore.Save(Settings); } catch { }
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        _overlayTimer?.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }

        // tear services down off the UI thread with a deadline, so a wedged driver call or
        // child process can never turn "Exit" into a hang
        var cleanup = new Thread(() =>
        {
            try { Fps?.Dispose(); } catch { }
            try { Net?.Dispose(); } catch { }
            try { Hub?.Dispose(); } catch { }
        })
        { IsBackground = true, Name = "Vitals.Cleanup" };
        cleanup.Start();
        cleanup.Join(TimeSpan.FromSeconds(4));

        new Thread(() => { Thread.Sleep(3000); Environment.Exit(0); }) { IsBackground = true }.Start();
        Shutdown();
    }

    // ---- "Vitals.exe --install" ----------------------------------------------

    /// <summary>
    /// Copies this exe to %LOCALAPPDATA%\Programs\Vitals, refreshes the Start Menu shortcut, and
    /// launches the installed copy, which takes over from any running instance. This process is
    /// already elevated, so the launched copy inherits that: one UAC prompt for the whole update.
    /// </summary>
    private static void RunInstaller()
    {
        try
        {
            string src = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the exe path.");
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Vitals");
            string dst = Path.Combine(dir, "Vitals.exe");
            bool alreadyInstalled = string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase);

            if (!alreadyInstalled)
            {
                // ask the running instance to exit so its exe is no longer locked
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ExitEventName, out var running))
                    {
                        running.Set();
                        running.Dispose();
                    }
                }
                catch { }

                Directory.CreateDirectory(dir);
                IOException? lastError = null;
                for (int i = 0; i < 80; i++)
                {
                    try { File.Copy(src, dst, overwrite: true); lastError = null; break; }
                    catch (IOException ex) { lastError = ex; Thread.Sleep(250); }
                }
                if (lastError != null) throw lastError;
            }

            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Vitals.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(lnk);
                shortcut.TargetPath = dst;
                shortcut.WorkingDirectory = dir;
                shortcut.IconLocation = dst + ",0";
                shortcut.Description = "Vitals - system monitor";
                shortcut.Save();
            }

            if (alreadyInstalled)
                MessageBox.Show("Vitals is already installed here. Start Menu shortcut refreshed.", "Vitals");
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dst) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Install failed:\n\n" + ex.Message, "Vitals", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- start-with-Windows (elevated scheduled task, avoids a UAC at logon) ----

    public static bool StartupTaskExists() => RunSchtasks("/Query /TN Vitals") == 0;

    /// <summary>The exe path the startup task currently launches, or null if there is no task.</summary>
    public static string? StartupTaskCommand()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Query /TN Vitals /XML")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (p == null) return null;
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            var m = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>");
            return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>If the app moved (new install location or exe name), re-point the startup task at it.</summary>
    private static void RepairStartupTaskIfMoved()
    {
        string? cmd = StartupTaskCommand();
        if (cmd != null && Environment.ProcessPath is { } exe
            && !string.Equals(cmd, exe, StringComparison.OrdinalIgnoreCase))
            SetStartupTask(true);
    }

    public static void SetStartupTask(bool enable)
    {
        if (enable)
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Vitals.App.exe");
            RunSchtasks($"/Create /F /RL HIGHEST /SC ONLOGON /TN Vitals /TR \"\\\"{exe}\\\"\"");
        }
        else
        {
            RunSchtasks("/Delete /F /TN Vitals");
        }
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p == null) return -1;
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
