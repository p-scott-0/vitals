using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vitals.App;

/// <summary>
/// Cortex-style overlay: metrics grouped under FPS / CPU / GPU / RAM / … headers, laid out in a
/// single horizontal strip or a vertical stack. Click-through + topmost when locked; draggable and
/// corner-resizable (scale) when unlocked.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

    private sealed class GroupUi
    {
        public string Key = "";
        public FrameworkElement Container = null!;
        public TextBlock? Header;
        public readonly List<(string Role, TextBlock Tb)> Items = new();
    }

    private static readonly string[] GroupOrder = ["FPS", "CPU", "GPU", "RAM", "BOARD", "FANS", "NET", "PLAY", "TIME"];
    private static readonly HashSet<string> LabelledGroups = ["BOARD", "FANS", "NET"];

    private readonly App _app;
    private readonly List<GroupUi> _groups = new();
    private IntPtr _hwnd;
    private Brush _headerBrush = UiBrushes.Accent;
    private Brush _valueBrush = UiBrushes.TextHi;

    private bool _resizing;
    private Point _resizeStart;
    private double _resizeStartScale, _resizeStartWidth;

    public OverlayWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Left = app.Settings.OverlayX;
        Top = app.Settings.OverlayY;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        Root.MouseLeftButtonDown += OnDragStart;
        Grip.MouseLeftButtonDown += OnGripDown;
        Grip.MouseMove += OnGripMove;
        Grip.MouseLeftButtonUp += OnGripUp;
    }

    // ---- move / resize ------------------------------------------------------

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_app.Settings.OverlayLocked || _resizing) return;
        DragMove();
        _app.Settings.OverlayX = Left;
        _app.Settings.OverlayY = Top;
        _app.SaveSettings();
    }

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (_app.Settings.OverlayLocked) return;
        _resizing = true;
        _resizeStart = PointToScreen(e.GetPosition(this));
        _resizeStartScale = _app.Settings.OverlayScale;
        _resizeStartWidth = Math.Max(40, Root.ActualWidth * _resizeStartScale);
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        var p = PointToScreen(e.GetPosition(this));
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double dx = (p.X - _resizeStart.X) / dpi;
        double scale = Math.Clamp(_resizeStartScale * (1 + dx / _resizeStartWidth), 0.5, 3.0);
        _app.Settings.OverlayScale = Math.Round(scale, 2);
        ApplyVisuals();
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        Grip.ReleaseMouseCapture();
        _app.SaveSettings();
        _app.SyncUi();
    }

    // ---- content ------------------------------------------------------------

    public void Rebuild(IReadOnlyList<string> metrics)
    {
        Groups.Children.Clear();
        _groups.Clear();
        bool horizontal = _app.Settings.OverlayHorizontal;
        Groups.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;

        foreach (string groupKey in GroupOrder)
        {
            var roles = App.MetricCatalog
                .Where(m => m.Group == groupKey && metrics.Contains(m.Role))
                .Select(m => m.Role)
                .ToList();
            if (roles.Count == 0) continue;

            var ui = new GroupUi { Key = groupKey };
            var values = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var role in roles)
            {
                var tb = new TextBlock
                {
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                values.Children.Add(tb);
                ui.Items.Add((role, tb));
            }
            if (groupKey != "TIME")
            {
                ui.Header = new TextBlock
                {
                    Text = groupKey,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0),
                };
            }

            if (horizontal)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                if (ui.Header != null) sp.Children.Add(ui.Header);
                sp.Children.Add(values);
                ui.Container = sp;
            }
            else
            {
                var dp = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = false };
                if (ui.Header != null)
                {
                    ui.Header.MinWidth = 44;
                    DockPanel.SetDock(ui.Header, Dock.Left);
                    dp.Children.Add(ui.Header);
                }
                values.Margin = new Thickness(14, 0, 0, 0);
                DockPanel.SetDock(values, Dock.Right);
                dp.Children.Add(values);
                ui.Container = dp;
            }
            Groups.Children.Add(ui.Container);
            _groups.Add(ui);
        }
        if (horizontal && _groups.Count > 0)
            _groups[^1].Container.Margin = new Thickness(0);

        ApplyVisuals();
    }

    public void UpdateValues(Func<string, OverlayValue?> resolver)
    {
        if (!IsVisible) return;
        foreach (var g in _groups)
        {
            int shown = 0;
            foreach (var (role, tb) in g.Items)
            {
                var r = resolver(role);
                if (r is null)
                {
                    tb.Visibility = Visibility.Collapsed;
                    continue;
                }
                shown++;
                tb.Visibility = Visibility.Visible;
                tb.Text = LabelledGroups.Contains(g.Key) ? $"{r.Value.Label} {r.Value.Text}" : r.Value.Text;
                tb.Foreground = r.Value.Zone ?? _valueBrush;
            }
            g.Container.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void ApplyVisuals()
    {
        var s = _app.Settings;
        _headerBrush = Frozen(App.ParseColor(s.OverlayHeaderColor, Color.FromRgb(0x22, 0xD3, 0xEE)));
        _valueBrush = Frozen(App.ParseColor(s.OverlayValueColor, Color.FromRgb(0xE8, 0xEC, 0xF4)));

        var bg = App.ParseColor(s.OverlayBackgroundColor, Color.FromRgb(0x0F, 0x12, 0x16));
        byte alpha = (byte)Math.Round(Math.Clamp(s.OverlayBackgroundOpacity, 0, 1) * 255);
        if (!s.OverlayLocked && alpha < 8) alpha = 8; // keep a hit-testable surface while draggable
        Root.Background = Frozen(Color.FromArgb(alpha, bg.R, bg.G, bg.B));
        Root.BorderBrush = Frozen(Color.FromArgb((byte)(alpha / 2),
            (byte)Math.Min(255, bg.R + 40), (byte)Math.Min(255, bg.G + 40), (byte)Math.Min(255, bg.B + 40)));
        Root.Padding = s.OverlayHorizontal ? new Thickness(12, 6, 12, 6) : new Thickness(12, 8, 12, 8);

        Opacity = Math.Clamp(s.OverlayOpacity, 0.2, 1);
        Root.LayoutTransform = new ScaleTransform(s.OverlayScale, s.OverlayScale);

        foreach (var g in _groups)
        {
            if (g.Header != null) g.Header.Foreground = _headerBrush;
            foreach (var (_, tb) in g.Items) tb.Foreground = _valueBrush;
        }

        Grip.Visibility = s.OverlayLocked ? Visibility.Collapsed : Visibility.Visible;
        if (!_resizing)
        {
            Left = s.OverlayX;
            Top = s.OverlayY;
        }
    }

    public void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong32(_hwnd, GwlExStyle);
        ex |= WsExToolWindow | WsExNoActivate;
        if (_app.Settings.OverlayLocked) ex |= WsExTransparent;
        else ex &= ~WsExTransparent;
        SetWindowLong32(_hwnd, GwlExStyle, ex);
    }

    private static Brush Frozen(Color c)
    {
        var br = new SolidColorBrush(c);
        br.Freeze();
        return br;
    }
}
