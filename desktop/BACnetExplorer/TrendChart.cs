using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BACnetExplorer;

public sealed class TrendChart : FrameworkElement
{
    public Watch? Watch { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var muted = new SolidColorBrush(Color.FromRgb(94, 115, 139));
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        var samples = Watch?.Samples ?? [];
        var numbers = samples.Where(s => s.Number.HasValue).ToList();
        if (numbers.Count < 2) { Label("Select a watch with at least two numeric observations to draw a trend.", 15, 30); return; }
        double left = 75, top = 25, width = Math.Max(1, ActualWidth - 95), height = Math.Max(1, ActualHeight - 60);
        var low = numbers.Min(s => s.Number!.Value);
        var high = numbers.Max(s => s.Number!.Value);
        if (low == high) { low -= Math.Max(1, Math.Abs(low) * .05); high += Math.Max(1, Math.Abs(high) * .05); }
        var start = samples[0].At;
        var span = Math.Max(.001, (samples[^1].At - start).TotalSeconds);
        for (var step = 0; step <= 4; step++)
        {
            var y = top + height * step / 4;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(222, 232, 242)), 1), new Point(left, y), new Point(left + width, y));
            Label((high - (high - low) * step / 4).ToString("G5", CultureInfo.InvariantCulture), 0, y - 7);
        }
        Point? previous = null;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(17, 125, 133)), 2);
        foreach (var sample in samples)
        {
            if (!sample.Number.HasValue) { previous = null; continue; }
            var point = new Point(left + (sample.At - start).TotalSeconds / span * width, top + (high - sample.Number.Value) / (high - low) * height);
            if (previous.HasValue) dc.DrawLine(pen, previous.Value, point);
            previous = point;
        }
        Label(start.ToLocalTime().ToString("HH:mm:ss"), left, top + height + 8);
        Label(samples[^1].At.ToLocalTime().ToString("HH:mm:ss"), left + width - 55, top + height + 8);
        Label($"{Watch!.Caption} / {Watch.Object} / {Watch.Property} · {samples.Count} samples", left, 0);
    }
}
