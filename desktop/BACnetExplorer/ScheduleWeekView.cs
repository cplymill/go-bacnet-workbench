using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BACnetExplorer;

public sealed class ScheduleWeekView : FrameworkElement
{
    public ScheduleDocument? Document { get; set; }
    public ScheduleBlock? Selected { get; set; }
    public double FirstMinute { get; set; } = 360;
    public double LastMinute { get; set; } = 1200;
    public event Action<ScheduleBlock>? BlockSelected;
    public event Action<ScheduleBlock, ScheduleBlock?>? BlockChanged;
    private readonly List<(Rect Bounds, ScheduleBlock Block)> hitBlocks = [];
    private Point? dragStart;
    private ScheduleBlock? dragged;
    private bool resize;
    private const double Left = 66, Top = 54, Bottom = 16;
    private double DayWidth => Math.Max(1, (ActualWidth - Left - 8) / 7);
    private double PlotHeight => Math.Max(1, ActualHeight - Top - Bottom);
    private double Y(double minute) => Top + (minute - FirstMinute) / (LastMinute - FirstMinute) * PlotHeight;
    private double Minute(double y) => Math.Clamp(Math.Round((FirstMinute + (y - Top) / PlotHeight * (LastMinute - FirstMinute)) / 15) * 15, 0, 1440);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        hitBlocks.Clear();
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (Document == null) return;
        var ink = new SolidColorBrush(Color.FromRgb(32, 51, 77));
        var muted = new SolidColorBrush(Color.FromRgb(101, 120, 143));
        void Text(string text, double x, double y, double width, Brush brush, double size = 12)
        {
            var line = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, width), MaxTextHeight = 45, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(line, new Point(x, y));
        }
        var monday = DateTime.Today.AddDays(-((int)DateTime.Today.DayOfWeek + 6) % 7);
        for (var minute = Math.Ceiling(FirstMinute / 120) * 120; minute <= LastMinute; minute += 120)
            Text(DateTime.Today.AddMinutes(minute).ToString("h:mm tt"), 0, Y(minute) - 7, Left - 5, muted, 10);
        for (var day = 0; day < 7; day++)
        {
            var x = Left + day * DayWidth;
            var bounds = new Rect(x + 3, Top, DayWidth - 6, PlotHeight);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(241, 245, 250)), new Pen(new SolidColorBrush(Color.FromRgb(217, 227, 238)), 1), bounds, 5, 5);
            Text(monday.AddDays(day).ToString("ddd").ToUpperInvariant(), x + 8, 6, DayWidth - 16, day == ((int)DateTime.Today.DayOfWeek + 6) % 7 ? Brushes.Teal : ink);
            Text(monday.AddDays(day).ToString("MMM d"), x + 8, 25, DayWidth - 16, muted, 11);
            for (var minute = Math.Ceiling(FirstMinute / 60) * 60; minute < LastMinute; minute += 60)
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(224, 232, 240)), 1), new Point(x + 4, Y(minute)), new Point(x + DayWidth - 4, Y(minute)));
            Text("Default: " + Document.DefaultLabel, x + 10, Top + 8, DayWidth - 20, muted, 10);
            var hasBlocks = false;
            foreach (var block in Document.Blocks(day))
            {
                if (block.End <= FirstMinute || block.Start >= LastMinute) continue;
                hasBlocks = true;
                var rect = new Rect(x + 9, Y(Math.Max(block.Start, FirstMinute)), DayWidth - 18, Math.Max(5, Y(Math.Min(block.End, LastMinute)) - Y(Math.Max(block.Start, FirstMinute))));
                var selected = Selected == block;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(210, 237, 235)), new Pen(selected ? Brushes.DarkSlateGray : Brushes.Teal, selected ? 2 : 1), rect, 4, 4);
                dc.PushClip(new RectangleGeometry(rect));
                Text(Document.Label(block.Type, block.Value), rect.X + 7, rect.Y + 7, rect.Width - 14, ink);
                Text($"{ScheduleEntry.Clock(block.Start)[..5]} – {ScheduleEntry.Clock(block.End)[..5]}", rect.X + 7, rect.Y + 25, rect.Width - 14, muted, 10);
                dc.Pop();
                hitBlocks.Add((rect, block));
            }
            if (!hasBlocks) Text("No blocks", x + 12, Top + PlotHeight / 2, DayWidth - 24, muted);
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Document == null) return;
        var point = e.GetPosition(this);
        if (point.X < Left || point.Y < Top || point.Y > ActualHeight - Bottom) return;
        dragStart = point;
        var hit = hitBlocks.LastOrDefault(b => b.Bounds.Contains(point));
        dragged = hit.Block;
        resize = dragged != null && point.Y >= hit.Bounds.Bottom - 10;
        var day = Math.Clamp((int)((point.X - Left) / DayWidth), 0, 6);
        Selected = dragged ?? new ScheduleBlock(day, Minute(point.Y), Math.Min(1440, Minute(point.Y) + 60), Document.ValueType, Document.DefaultType == "boolean" ? "true" : "1");
        BlockSelected?.Invoke(Selected);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragStart is not Point start || Selected == null) return;
        var end = e.GetPosition(this);
        var delta = Minute(end.Y) - Minute(start.Y);
        if ((end - start).Length > 5)
        {
            ScheduleBlock next;
            if (dragged == null) next = Selected with { Start = Math.Min(Minute(start.Y), Minute(end.Y)), End = Math.Max(Minute(start.Y), Minute(end.Y)) };
            else if (resize) next = dragged with { End = Math.Clamp(dragged.End + delta, dragged.Start + 15, 1440) };
            else
            {
                var duration = dragged.End - dragged.Start;
                var minute = Math.Clamp(dragged.Start + delta, 0, 1440 - duration);
                next = dragged with { Day = Math.Clamp((int)((end.X - Left) / DayWidth), 0, 6), Start = minute, End = minute + duration };
            }
            if (next.End > next.Start) BlockChanged?.Invoke(next, dragged);
        }
        dragStart = null;
        dragged = null;
        ReleaseMouseCapture();
    }
}
