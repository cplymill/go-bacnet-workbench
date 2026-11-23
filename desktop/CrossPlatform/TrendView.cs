using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BACnetExplorer.CrossPlatform;

internal sealed class TrendView : Control
{
    internal Watch? Watch { get; set; }
    public TrendView() { MinHeight = 180; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var area = Bounds.Deflate(16);
        context.DrawRectangle(Brush.Parse("#F8FAFD"), new Pen(Brushes.LightGray), area);
        var samples = Watch?.Samples.TakeLast(120).ToList();
        if (samples == null || samples.Count < 2) return;
        var numbers = samples.Where(sample => sample.Number.HasValue).Select(sample => sample.Number!.Value).ToList();
        if (numbers.Count == 0) return;
        var minimum = numbers.Min(); var span = Math.Max(1, numbers.Max() - minimum);
        Point? previous = null;
        for (var index = 0; index < samples.Count; index++) {
            if (samples[index].Number is not double number) { previous = null; continue; }
            var point = new Point(area.X + area.Width * index / (samples.Count - 1),
                area.Bottom - (number - minimum) / span * area.Height);
            if (previous.HasValue) context.DrawLine(new Pen(Brush.Parse("#117D85"), 2), previous.Value, point);
            previous = point;
        }
    }
}
