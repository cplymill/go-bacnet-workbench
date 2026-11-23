using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BACnetExplorer;

internal static class OperationAlertChecks
{
    internal static void Run()
    {
        var error = new OperationFailure("range",
            "This device does not recognize requests to read a range of list or log entries (ReadRange).",
            "Use Read properties for point values. Reading stored history requires a device that supports ReadRange.",
            "remote BACnet Reject reason 9 (invoke 115)");
        var alert = new OperationAlert("Read range", error, "Example device at 192.168.68.50 / Trend Log 1 / log-buffer");
        var details = (Expander)alert.FindName("DetailsExpander");
        if (details.IsExpanded || ((TextBlock)alert.FindName("Summary")).Text.Contains("invoke")
            || !((TextBlock)alert.FindName("Guidance")).Text.Contains("Read properties")
            || !((TextBox)alert.FindName("DetailsText")).Text.Contains("reason 9 (invoke 115)"))
            throw new InvalidOperationException("Alert mixes user guidance with protocol details or loses diagnostics.");
        var outer = new IOException("Stopped after 1/3 acknowledgements. Earlier changes remain applied.", error);
        var wrapped = new OperationAlert("Bulk write", outer, "Example device");
        if (!((TextBlock)wrapped.FindName("Summary")).Text.Contains("1/3")
            || !OperationAlert.Details("Bulk write", outer).Contains("invoke 115"))
            throw new InvalidOperationException("Partial-result context was lost.");
        var content = (FrameworkElement)alert.Content;
        content.Measure(new Size(580, double.PositiveInfinity));
        content.Arrange(new Rect(new Point(), content.DesiredSize));
        content.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(content);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(241,245,250)), null, new Rect(new Size(content.ActualWidth + 40, content.ActualHeight + 40)));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(20, 20, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((content.ActualWidth + 40) * dpi.DpiScaleX), (int)Math.Ceiling((content.ActualHeight + 40) * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.GetFullPath("bin/alert-preview-0.2.17.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        encoder.Save(file);
        Console.WriteLine("PASS: alert separates meaning/next step from collapsed technical details, preserves wrapped partial results; hidden preview rendered.");
    }
}
