using System.Windows;
using System.Windows.Media;

namespace Vitals.App.Controls;

/// <summary>Thin horizontal 0–100% meter.</summary>
public sealed class BarMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BarMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private static readonly Brush Track = Make(0x24, 0x2B, 0x38);
    private static readonly Brush Fill = Make(0x22, 0xD3, 0xEE);

    private static Brush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 2) return;
        double radius = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        double frac = Math.Clamp(Value / 100.0, 0, 1);
        if (frac > 0.01)
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(h, w * frac), h), radius, radius);
    }
}
