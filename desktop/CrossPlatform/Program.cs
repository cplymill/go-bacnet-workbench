using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Media;

namespace BACnetExplorer.CrossPlatform;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildApp() => AppBuilder.Configure<WorkbenchApp>().UsePlatformDetect().WithInterFont();
}

public sealed class WorkbenchApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://GoBACnetWorkbench/")) {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
        Styles.Add(new Style(x => x.OfType<Button>()) {
            Setters = {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(13, 8)),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.White),
                new Setter(TemplatedControl.ForegroundProperty, Brush.Parse("#28415E")),
                new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#CDDAE8")),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(5)),
                new Setter(TemplatedControl.FontSizeProperty, 13d),
            }
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class("primary")) {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brush.Parse("#117D85")),
                new Setter(TemplatedControl.ForegroundProperty, Brushes.White),
                new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#117D85")) }
        });
        Styles.Add(new Style(x => x.OfType<TextBox>()) {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brush.Parse("#F8FAFD")),
                new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#CDD9E7")),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(9, 6)),
                new Setter(TemplatedControl.FontSizeProperty, 13d) }
        });
        Styles.Add(new Style(x => x.OfType<TabItem>()) {
            Setters = { new Setter(TemplatedControl.FontSizeProperty, 13d),
                new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 10)) }
        });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new WorkbenchWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
