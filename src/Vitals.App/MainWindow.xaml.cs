using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vitals.App.Controls;
using Vitals.Core;

namespace Vitals.App;

public partial class MainWindow : Window
{
    private static readonly Color CpuColor = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color GpuColor = Color.FromRgb(0xA7, 0x8B, 0xFA);
    private static readonly Color HotColor = Color.FromRgb(0xF4, 0x72, 0xB6);
    private static readonly Color VrmColor = Color.FromRgb(0xF5, 0xA6, 0x23);
    private static readonly Color PumpColor = Color.FromRgb(0x38, 0xBD, 0xF8);
    private static readonly Color CaseColor = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color FpsColor = Color.FromRgb(0x34, 0xD3, 0x99);

    private static readonly string[] TextPresets = ["#22D3EE", "#E8ECF4", "#F5A623", "#34D399", "#F472B6", "#A78BFA", "#8A93A6"];
    private static readonly string[] BgPresets = ["#000000", "#0F1216", "#1E293B", "#334155", "#FFFFFF"];

    private readonly App _app;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<FanVm> _fans = new();
    private readonly Dictionary<string, FanVm> _fanById = new();
    private readonly HashSet<string> _everSpun = new();
    private int _fanRowCount = -1;
    private bool _init = true;
    private bool _syncing;
    private bool _labelsFixed;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        FanItems.ItemsSource = _fans;
        SubtitleText.Text = "system monitor · " + App.VersionText;

        CpuGauge.WarnFrom = 70; CpuGauge.HotFrom = 85;
        GpuGauge.WarnFrom = 70; GpuGauge.HotFrom = 83;
        HotspotGauge.WarnFrom = 85; HotspotGauge.HotFrom = 95; HotspotGauge.Maximum = 110;
        VrmGauge.WarnFrom = 80; VrmGauge.HotFrom = 95; VrmGauge.Maximum = 110;

        TempChart.FixedYMin = 20;
        FanChart.FixedYMin = 0;
        FpsChart.FixedYMin = 0;

        BuildSettingsPanel();
        SyncFromSettings();
        _init = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    // ---- borderless window: maximise into the work area, not over the taskbar ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
            }
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "▢";
    }

    // ---- per-second refresh ----------------------------------------------

    private void Refresh()
    {
        if (!_labelsFixed && _app.Resolve("gpu.hotspot") is { } hotSensor)
        {
            // this gauge is Hot Spot on GPUs that report it, VRAM junction on RTX 50-series
            if (hotSensor.Info.Name.Contains("Memory Junction", StringComparison.OrdinalIgnoreCase))
                HotspotGauge.Label = "GPU VRAM";
            _labelsFixed = true;
        }

        double? cpuT = Val("cpu.temp"), gpuT = Val("gpu.temp"), hotT = Val("gpu.hotspot"), vrmT = Val("board.vrm");
        double? cpuL = Val("cpu.load"), gpuL = Val("gpu.load"), gpuW = Val("gpu.power");
        double? ram = Val("ram.load"), sysT = Val("board.system"), ssdT = Val("ssd.temp");

        CpuGauge.SetValueAnimated(cpuT);
        GpuGauge.SetValueAnimated(gpuT);
        HotspotGauge.SetValueAnimated(hotT);
        VrmGauge.SetValueAnimated(vrmT);

        CpuGauge.SubText = cpuL.HasValue ? $"{Math.Round(cpuL.Value)}% load" : "";
        GpuGauge.SubText = gpuL.HasValue || gpuW.HasValue
            ? $"{(gpuL.HasValue ? Math.Round(gpuL.Value) + "%" : "--")} • {(gpuW.HasValue ? Math.Round(gpuW.Value) + " W" : "--")}"
            : "";
        HotspotGauge.SubText = hotT.HasValue && gpuT.HasValue ? $"Δ {Math.Round(hotT.Value - gpuT.Value)}° vs core" : "";
        VrmGauge.SubText = sysT.HasValue ? $"sys {Math.Round(sysT.Value)}°" : "";

        CpuLoadTileV.Text = Fmt(cpuL, "%");
        GpuLoadTileV.Text = Fmt(gpuL, "%");
        GpuPowerTileV.Text = Fmt(gpuW, " W");
        RamTileV.Text = Fmt(ram, "%");
        SysTempTileV.Text = Fmt(sysT, "°");
        SsdTileV.Text = Fmt(ssdT, "°");

        RefreshFans();
        RefreshCharts();

        var pollAge = _app.Hub.SinceLastPoll;
        string storageNote = !_app.Hub.StorageEnabled ? ""
            : _app.Hub.StorageError is { } se ? " • storage: " + se
            : _app.Hub.SinceLastStoragePoll > TimeSpan.FromSeconds(90) ? " • storage poll stalled (disk asleep?)"
            : "";
        StatusLeft.Text = pollAge > TimeSpan.FromSeconds(5)
            ? $"⚠ sensor poll stalled for {pollAge.TotalSeconds:0}s" + (_app.Hub.LastError is { } e1 ? " — " + e1 : "")
            : _app.Hub.LastError is { } err
                ? "sensor error: " + err
                : $"{_app.Hub.Sensors.Count} sensors • polling {(int)_app.Hub.PollInterval.TotalSeconds}s{storageNote}";
        StatusMid.Text = "FPS: " + _app.Fps.StateText;
        StatusRight.Text = _app.Settings.OverlayEnabled
            ? (_app.Settings.OverlayLocked ? "overlay on • click-through" : "overlay on • draggable")
            : "overlay off";
    }

    private double? Val(string role) => _app.Resolve(role)?.Value;

    private static string Fmt(double? v, string suffix) =>
        v.HasValue ? Math.Round(v.Value) + suffix : "--";

    // ---- fans -------------------------------------------------------------

    private void RefreshFans()
    {
        var pairs = _app.Hub.FanPairs();
        if (pairs.Count != _fanRowCount)
        {
            _fanRowCount = pairs.Count;
            RebuildFanRows();
        }

        foreach (var (rpm, pct) in pairs)
        {
            string id = rpm.Info.Id;
            if ((rpm.Value ?? 0) > 1) _everSpun.Add(id);
            bool visible = IsFanVisible(id, rpm.Info);
            _fanById.TryGetValue(id, out var vm);

            if (!visible)
            {
                if (vm != null)
                {
                    _fans.Remove(vm);
                    _fanById.Remove(id);
                }
                continue;
            }

            if (vm == null)
            {
                vm = new FanVm { Name = FanName(rpm.Info) };
                _fanById[id] = vm;
                _fans.Add(vm);
            }
            vm.Rpm = rpm.Value.HasValue ? Math.Round(rpm.Value.Value).ToString() : "--";
            vm.PctText = pct?.Value is double p ? Math.Round(p) + "%" : "";
            vm.Pct = pct?.Value ?? 0;
        }
    }

    /// <summary>Explicit user choice wins; otherwise GPU fans always show, board headers only once seen spinning.</summary>
    private bool IsFanVisible(string id, SensorInfo info) =>
        _app.Settings.FanVisibility.TryGetValue(id, out bool v)
            ? v
            : info.HardwareType == "GpuNvidia" || _everSpun.Contains(id);

    private string FanName(SensorInfo info)
    {
        if (_app.Settings.FanNames.TryGetValue(info.Id, out var nick) && !string.IsNullOrWhiteSpace(nick))
            return nick;
        if (info.HardwareType == "GpuNvidia" && !info.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
            return "GPU " + info.Name;
        return info.Name;
    }

    private void RebuildFanRows()
    {
        FanRows.Children.Clear();
        foreach (var (rpm, pct) in _app.Hub.FanPairs())
        {
            string id = rpm.Info.Id;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = false };

            var cb = new CheckBox
            {
                IsChecked = IsFanVisible(id, rpm.Info),
                Style = (Style)FindResource("SettingCheck"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = id,
            };
            cb.Checked += (_, _) => SetFanVisibility(id, true);
            cb.Unchecked += (_, _) => SetFanVisibility(id, false);

            var box = new TextBox
            {
                Text = FanName(rpm.Info),
                Width = 160,
                Style = (Style)FindResource("SettingBox"),
                ToolTip = "Sensor name: " + rpm.Info.Name,
            };
            box.TextChanged += (_, _) =>
            {
                if (_init) return;
                string n = box.Text.Trim();
                if (n.Length == 0 || n == rpm.Info.Name) _app.Settings.FanNames.Remove(id);
                else _app.Settings.FanNames[id] = n;
                _app.SaveSettings();
                if (_fanById.TryGetValue(id, out var vm)) vm.Name = FanName(rpm.Info);
            };

            var hint = new TextBlock
            {
                Text = rpm.Value.HasValue ? $"{Math.Round(rpm.Value.Value)} RPM" : "--",
                Foreground = UiBrushes.TextLo,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };

            DockPanel.SetDock(cb, Dock.Left);
            DockPanel.SetDock(box, Dock.Left);
            DockPanel.SetDock(hint, Dock.Left);
            row.Children.Add(cb);
            row.Children.Add(box);
            row.Children.Add(hint);
            FanRows.Children.Add(row);
        }
    }

    private void SetFanVisibility(string id, bool visible)
    {
        if (_init) return;
        _app.Settings.FanVisibility[id] = visible;
        _app.SaveSettings();
        RefreshFans();
    }

    // ---- charts -------------------------------------------------------------

    private void ApplyChartWindow()
    {
        int minutes = Math.Clamp(_app.Settings.ChartWindowMinutes, 1, 60);
        int seconds = minutes * 60;
        TempChart.WindowSeconds = seconds;
        FanChart.WindowSeconds = seconds;
        FpsChart.WindowSeconds = seconds;
        TempChartTitle.Text = $"TEMPERATURES — LAST {minutes} MIN";
        FanChartTitle.Text = $"FAN SPEEDS (RPM) — LAST {minutes} MIN";
    }

    private void RefreshCharts()
    {
        var window = TimeSpan.FromSeconds(TempChart.WindowSeconds);

        TempChart.SetSeries(BuildSeries(window,
            ("CPU", "cpu.temp", CpuColor),
            ("GPU", "gpu.temp", GpuColor),
            ("VRAM", "gpu.hotspot", HotColor),
            ("VRM", "board.vrm", VrmColor)));

        FanChart.SetSeries(BuildSeries(window,
            ("CPU", "fan.cpu", CpuColor),
            ("Pump", "fan.pump", PumpColor),
            ("Case", "fan.case", CaseColor),
            ("GPU", "fan.gpu", GpuColor)));

        var (ft, fv) = _app.FpsHistory.Snapshot(window);
        IReadOnlyList<ChartSeries> fpsSeries = ft.Length > 0
            ? new[] { new ChartSeries("FPS", FpsColor, ft, fv) }
            : Array.Empty<ChartSeries>();
        FpsChart.SetSeries(fpsSeries);
    }

    private List<ChartSeries> BuildSeries(TimeSpan window, params (string Name, string Role, Color Color)[] defs)
    {
        var list = new List<ChartSeries>();
        foreach (var d in defs)
        {
            var t = _app.Resolve(d.Role);
            if (t == null) continue;
            if (d.Role.StartsWith("fan.") && !IsFanVisible(t.Info.Id, t.Info)) continue;
            var (times, vals) = t.History.Snapshot(window);
            if (times.Length == 0) continue;
            if (d.Role.StartsWith("fan.") && vals.All(v => v < 1)) continue; // parked/empty header
            list.Add(new ChartSeries(d.Name, d.Color, times, vals));
        }
        return list;
    }

    // ---- settings flyout --------------------------------------------------

    private void BuildSettingsPanel()
    {
        BuildChips(PollChips, new (string, int)[] { ("1 s", 1), ("2 s", 2), ("3 s", 3) },
            () => _app.Settings.PollSeconds,
            v => { _app.Settings.PollSeconds = v; _app.Hub.PollInterval = TimeSpan.FromSeconds(v); _app.SaveSettings(); });

        BuildChips(ChartChips, new (string, int)[] { ("1 min", 1), ("2", 2), ("5", 5), ("10", 10), ("30", 30), ("60", 60) },
            () => _app.Settings.ChartWindowMinutes,
            v => { _app.Settings.ChartWindowMinutes = v; ApplyChartWindow(); _app.SaveSettings(); });

        BuildChips(LayoutChips, new (string, bool)[] { ("Horizontal strip", true), ("Vertical stack", false) },
            () => _app.Settings.OverlayHorizontal,
            v => { _app.Settings.OverlayHorizontal = v; _app.SaveSettings(); _app.RefreshOverlayConfig(); });

        BuildChips(FanModeChips, new (string, string)[] { ("RPM", "rpm"), ("Duty %", "pct"), ("RPM + %", "both") },
            () => _app.Settings.OverlayFanMode,
            v => { _app.Settings.OverlayFanMode = v; _app.SaveSettings(); _app.RefreshOverlayConfig(); });

        BuildChips(TimeChips, new (string, string)[] { ("24h", "HH:mm"), ("24h + sec", "HH:mm:ss"), ("12h", "h:mm tt"), ("12h + sec", "h:mm:ss tt") },
            () => _app.Settings.TimeFormat,
            v => { _app.Settings.TimeFormat = v; _app.SaveSettings(); _app.RefreshOverlayConfig(); });

        foreach (var m in App.MetricCatalog)
        {
            var cb = new CheckBox
            {
                Content = m.Group + " · " + m.Label,
                Tag = m.Role,
                Width = 175,
                IsChecked = _app.Settings.OverlayMetrics.Contains(m.Role),
                Style = (Style)FindResource("SettingCheck"),
            };
            cb.Checked += OnMetricToggled;
            cb.Unchecked += OnMetricToggled;
            MetricChecks.Children.Add(cb);
        }

        BuildColourRow("Header text", () => _app.Settings.OverlayHeaderColor, v => _app.Settings.OverlayHeaderColor = v, TextPresets);
        BuildColourRow("Value text", () => _app.Settings.OverlayValueColor, v => _app.Settings.OverlayValueColor = v, TextPresets);
        BuildColourRow("Background", () => _app.Settings.OverlayBackgroundColor, v => _app.Settings.OverlayBackgroundColor = v, BgPresets);

        StartupCheck.IsChecked = App.StartupTaskExists();
        RebuildFanRows();
        ApplyChartWindow();
    }

    private void BuildChips<T>(Panel panel, (string Label, T Value)[] options, Func<T> current, Action<T> apply)
    {
        panel.Children.Clear();
        var buttons = new List<(ToggleButton Btn, T Val)>();
        foreach (var (label, value) in options)
        {
            var tb = new ToggleButton
            {
                Content = label,
                Style = (Style)FindResource("ChipToggle"),
                Margin = new Thickness(0, 0, 4, 0),
                IsChecked = EqualityComparer<T>.Default.Equals(value, current()),
            };
            tb.Click += (_, _) =>
            {
                apply(value);
                foreach (var (b, v) in buttons)
                    b.IsChecked = EqualityComparer<T>.Default.Equals(v, value);
            };
            buttons.Add((tb, value));
            panel.Children.Add(tb);
        }
    }

    private void BuildColourRow(string label, Func<string> get, Action<string> set, string[] presets)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = false };
        var name = new TextBlock
        {
            Text = label,
            Width = 84,
            FontSize = 11,
            Foreground = UiBrushes.TextLo,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var swatch = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(App.ParseColor(get(), Colors.Gray)),
        };
        var box = new TextBox { Text = get(), Width = 78, Style = (Style)FindResource("SettingBox") };
        box.TextChanged += (_, _) =>
        {
            if (_init) return;
            string hex = box.Text.Trim();
            var parsed = App.ParseColor(hex, Colors.Transparent);
            if (parsed == Colors.Transparent && !hex.Equals("#00000000", StringComparison.OrdinalIgnoreCase)) return;
            swatch.Background = new SolidColorBrush(parsed);
            set(hex.StartsWith('#') ? hex : "#" + hex);
            _app.SaveSettings();
            _app.RefreshOverlayVisuals();
        };

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (string preset in presets)
        {
            var chip = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 5, 0),
                Background = new SolidColorBrush(App.ParseColor(preset, Colors.Gray)),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = preset,
            };
            chip.MouseLeftButtonDown += (_, _) => box.Text = preset;
            chips.Children.Add(chip);
        }

        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(swatch, Dock.Left);
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(chips, Dock.Left);
        row.Children.Add(name);
        row.Children.Add(swatch);
        row.Children.Add(box);
        row.Children.Add(chips);
        ColourRows.Children.Add(row);
    }

    public void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            var s = _app.Settings;
            OverlayBtn.IsChecked = s.OverlayEnabled;
            OverlayCheck.IsChecked = s.OverlayEnabled;
            LockCheck.IsChecked = s.OverlayLocked;
            FpsCheck.IsChecked = s.FpsEnabled;
            FpsLogCheck.IsChecked = s.FpsLogging;
            TrayCheck.IsChecked = s.CloseToTray;
            StorageCheck.IsChecked = s.StorageSensors;
            BgOpacitySlider.Value = s.OverlayBackgroundOpacity;
            OpacitySlider.Value = s.OverlayOpacity;
            ScaleSlider.Value = s.OverlayScale;
            UpdateSliderLabels();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateSliderLabels()
    {
        BgOpacityLabel.Text = $"Background opacity — {BgOpacitySlider.Value:P0}";
        OpacityLabel.Text = $"Overall opacity (text included) — {OpacitySlider.Value:P0}";
        ScaleLabel.Text = $"Size — {ScaleSlider.Value:0.00}×  (or drag the overlay's corner)";
    }

    private bool Suppressed => _init || _syncing;

    private void OnMetricToggled(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        _app.Settings.OverlayMetrics = MetricChecks.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag)
            .ToList();
        _app.SaveSettings();
        _app.RefreshOverlayConfig();
    }

    private void OnOverlaySliders(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Suppressed) return;
        _app.Settings.OverlayBackgroundOpacity = BgOpacitySlider.Value;
        _app.Settings.OverlayOpacity = OpacitySlider.Value;
        _app.Settings.OverlayScale = Math.Round(ScaleSlider.Value, 2);
        UpdateSliderLabels();
        _app.SaveSettings();
        _app.RefreshOverlayVisuals();
    }

    private void OnOverlayCheck(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        if (OverlayCheck.IsChecked == true) _app.ShowOverlay();
        else _app.HideOverlay();
    }

    private void OnLockChanged(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        _app.SetOverlayLocked(LockCheck.IsChecked == true);
    }

    private void OnFpsChanged(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        _app.SetFpsEnabled(FpsCheck.IsChecked == true);
    }

    private void OnFpsLogChanged(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        _app.Settings.FpsLogging = FpsLogCheck.IsChecked == true;
        _app.Fps.LoggingEnabled = _app.Settings.FpsLogging;
        _app.SaveSettings();
    }

    private void OnTrayChanged(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        _app.Settings.CloseToTray = TrayCheck.IsChecked == true;
        _app.SaveSettings();
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        App.SetStartupTask(StartupCheck.IsChecked == true);
    }

    private void OnStorageChanged(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        _app.Settings.StorageSensors = StorageCheck.IsChecked == true;
        _app.SaveSettings();
    }

    private void OnResetOverlayPos(object sender, RoutedEventArgs e)
    {
        _app.Settings.OverlayX = 40;
        _app.Settings.OverlayY = 40;
        _app.Settings.OverlayScale = 1.0;
        _app.SaveSettings();
        SyncFromSettings();
        _app.RefreshOverlayVisuals();
    }

    // ---- window chrome ----------------------------------------------------

    private void OnOverlayBtn(object sender, RoutedEventArgs e)
    {
        if (Suppressed) return;
        _app.ToggleOverlay();
    }

    private void OnSettingsBtn(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Visibility = SettingsBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseBtn(object sender, RoutedEventArgs e) => Close();

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_app.IsExiting || !_app.Settings.CloseToTray) return;
        e.Cancel = true;
        Hide();
    }
}

public sealed class FanVm : INotifyPropertyChanged
{
    private string _name = "";
    private string _rpm = "--";
    private string _pctText = "";
    private double _pct;

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } }
    }

    public string Rpm
    {
        get => _rpm;
        set { if (_rpm != value) { _rpm = value; OnChanged(nameof(Rpm)); } }
    }

    public string PctText
    {
        get => _pctText;
        set { if (_pctText != value) { _pctText = value; OnChanged(nameof(PctText)); } }
    }

    public double Pct
    {
        get => _pct;
        set { if (Math.Abs(_pct - value) > 0.5) { _pct = value; OnChanged(nameof(Pct)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
