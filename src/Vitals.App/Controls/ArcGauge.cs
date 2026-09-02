using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vitals.App.Controls;

/// <summary>270° arc gauge with warn/hot colour zones, drawn directly for a crisp modern look.</summary>
public sealed class ArcGauge : FrameworkElement
{
    private const double StartDeg = 135;
    private const double SweepDeg = 270;
    private const double LabelBand = 24;   // reserved strip above the arc for the title
    private const double SubBand = 18;     // reserved strip below the arc for the subtext

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ArcGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(ArcGauge),
        new FrameworkPropertyMetadata("°C", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SubTextProperty = DependencyProperty.Register(
        nameof(SubText), typeof(string), typeof(ArcGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }

    public double Minimum { get; set; } = 20;
    public double Maximum { get; set; } = 100;
    public double WarnFrom { get; set; } = 70;
    public double HotFrom { get; set; } = 85;

    private static readonly Brush TrackBrush = Frozen(0x24, 0x2B, 0x38);
    private static readonly Brush TickBrush = Frozen(0x3A, 0x43, 0x56);
    private static readonly Brush AccentBrush = Frozen(0x22, 0xD3, 0xEE);
    private static readonly Brush WarnBrush = Frozen(0xF5, 0xA6, 0x23);
    private static readonly Brush HotBrush = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush TextHi = Frozen(0xE8, 0xEC, 0xF4);
    private static readonly Brush TextLo = Frozen(0x8A, 0x93, 0xA6);
    private static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

    private bool _hasValue;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public void SetValueAnimated(double? value)
    {
        if (value is null)
        {
            BeginAnimation(ValueProperty, null);
            Value = double.NaN;
            _hasValue = false;
            return;
        }
        if (!_hasValue || double.IsNaN(Value))
        {
            BeginAnimation(ValueProperty, null);
            Value = value.Value;
        }
        else
        {
            var anim = new DoubleAnimation(value.Value, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            BeginAnimation(ValueProperty, anim);
        }
        _hasValue = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 60) return;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // geometry: the arc lives strictly between the label band and the subtext band
        double thickness = Math.Clamp(Math.Min(w, h) * 0.075, 7, 14);
        double availH = h - LabelBand - SubBand;
        // a 270° arc spans r above its centre and ~0.71r below it
        double radius = Math.Min((availH - thickness) / 1.72, (w - 12 - thickness) / 2);
        if (radius < 20) return;
        var center = new Point(w / 2, LabelBand + thickness / 2 + radius);

        var trackPen = new Pen(TrackBrush, thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DrawArc(dc, trackPen, center, radius, StartDeg, SweepDeg);

        double value = Value;
        bool has = !double.IsNaN(value);
        if (has)
        {
            double frac = Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
            Brush zone = value >= HotFrom ? HotBrush : value >= WarnFrom ? WarnBrush : AccentBrush;
            if (frac > 0.005)
            {
                var valuePen = new Pen(zone, thickness)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                DrawArc(dc, valuePen, center, radius, StartDeg, SweepDeg * frac);
            }
        }

        var tickPen = new Pen(TickBrush, 2);
        foreach (double t in new[] { WarnFrom, HotFrom })
        {
            double deg = StartDeg + SweepDeg * Math.Clamp((t - Minimum) / (Maximum - Minimum), 0, 1);
            dc.DrawLine(tickPen, Polar(center, radius - thickness * 0.9, deg), Polar(center, radius + thickness * 0.9, deg));
        }

        DrawCentered(dc, Label.ToUpperInvariant(), 11, FontWeights.SemiBold, TextLo, new Point(w / 2, LabelBand / 2), ppd);
        string valText = has ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : "--";
        DrawCentered(dc, valText, Math.Clamp(radius * 0.6, 20, 44), FontWeights.SemiBold, TextHi, new Point(center.X, center.Y - 2), ppd);
        DrawCentered(dc, Unit, 12, FontWeights.Normal, TextLo, new Point(center.X, center.Y + radius * 0.42), ppd);
        if (!string.IsNullOrEmpty(SubText))
            DrawCentered(dc, SubText, 11, FontWeights.Normal, TextLo, new Point(w / 2, h - SubBand / 2), ppd);
    }

    private static void DrawCentered(DrawingContext dc, string text, double size, FontWeight weight, Brush brush, Point at, double ppd)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Font, FontStyles.Normal, weight, FontStretches.Normal), size, brush, ppd);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        if (sweepDeg <= 0) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(Polar(c, r, startDeg), false, false);
            ctx.ArcTo(Polar(c, r, startDeg + sweepDeg), new Size(r, r), 0,
                sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static Point Polar(Point c, double r, double deg)
    {
        double rad = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
