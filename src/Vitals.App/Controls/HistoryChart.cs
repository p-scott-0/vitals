using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Vitals.App.Controls;

public sealed record ChartSeries(string Name, Color Color, double[] UnixTimes, double[] Values);

/// <summary>Scrolling multi-series time chart (right edge = now), drawn directly.</summary>
public sealed class HistoryChart : FrameworkElement
{
    public int WindowSeconds { get; set; } = 600;
    public double? FixedYMin { get; set; }
    public double? FixedYMax { get; set; }
    public string UnitSuffix { get; set; } = "";

    private IReadOnlyList<ChartSeries> _series = [];

    private static readonly Brush GridBrush = Make(0x20, 0x26, 0x31);
    private static readonly Brush TextLo = Make(0x8A, 0x93, 0xA6);
    private static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

    private static Brush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public void SetSeries(IReadOnlyList<ChartSeries> series)
    {
        _series = series;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 40) return;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        const double left = 4, right = 40, top = 22, bottom = 6;
        double plotW = w - left - right, plotH = h - top - bottom;
        if (plotW < 20 || plotH < 20) return;

        double now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        double t0 = now - WindowSeconds;

        // collect visible values for auto-scaling
        double min = double.MaxValue, max = double.MinValue;
        bool any = false;
        foreach (var s in _series)
            for (int i = 0; i < s.UnixTimes.Length; i++)
                if (s.UnixTimes[i] >= t0)
                {
                    any = true;
                    if (s.Values[i] < min) min = s.Values[i];
                    if (s.Values[i] > max) max = s.Values[i];
                }

        double yMin = FixedYMin ?? (any ? min : 0);
        double yMax = FixedYMax ?? (any ? max : 100);
        if (FixedYMin is null || FixedYMax is null)
        {
            double pad = Math.Max((yMax - yMin) * 0.12, 2);
            if (FixedYMin is null) yMin -= pad;
            if (FixedYMax is null) yMax += pad;
        }
        if (yMax - yMin < 1) yMax = yMin + 1;

        // horizontal grid + labels
        for (int i = 0; i <= 3; i++)
        {
            double frac = i / 3.0;
            double y = top + plotH * frac;
            dc.DrawLine(new Pen(GridBrush, 1), new Point(left, y), new Point(left + plotW, y));
            double val = yMax - (yMax - yMin) * frac;
            var ft = new FormattedText(Math.Round(val).ToString(CultureInfo.InvariantCulture) + UnitSuffix,
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, TextLo, ppd);
            dc.DrawText(ft, new Point(left + plotW + 6, y - ft.Height / 2));
        }

        // series lines
        foreach (var s in _series)
        {
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            var pen = new Pen(brush, 1.8)
            { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

            var geo = new StreamGeometry();
            bool started = false;
            Point last = default;
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < s.UnixTimes.Length; i++)
                {
                    if (s.UnixTimes[i] < t0) continue;
                    double x = left + (s.UnixTimes[i] - t0) / WindowSeconds * plotW;
                    double y = top + (1 - (s.Values[i] - yMin) / (yMax - yMin)) * plotH;
                    y = Math.Clamp(y, top, top + plotH);
                    var p = new Point(x, y);
                    if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                    else ctx.LineTo(p, true, true);
                    last = p;
                }
            }
            geo.Freeze();
            if (started)
            {
                dc.DrawGeometry(null, pen, geo);
                dc.DrawEllipse(brush, null, last, 2.6, 2.6);
            }
        }

        // legend with current values
        double lx = left + 2;
        foreach (var s in _series)
        {
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            dc.DrawEllipse(brush, null, new Point(lx + 4, 8), 3.5, 3.5);
            string latest = s.Values.Length > 0
                ? " " + Math.Round(s.Values[^1]).ToString(CultureInfo.InvariantCulture) + UnitSuffix
                : "";
            var ft = new FormattedText(s.Name + latest, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10.5, TextLo, ppd);
            dc.DrawText(ft, new Point(lx + 11, 8 - ft.Height / 2));
            lx += 11 + ft.Width + 14;
        }

        if (!any)
        {
            var ft = new FormattedText("waiting for data…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, TextLo, ppd);
            dc.DrawText(ft, new Point(left + plotW / 2 - ft.Width / 2, top + plotH / 2 - ft.Height / 2));
        }
    }
}
