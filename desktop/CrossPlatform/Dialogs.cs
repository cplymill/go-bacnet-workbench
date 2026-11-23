using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace BACnetExplorer.CrossPlatform;

internal static class Dialogs
{
    internal static async Task<bool> Confirm(Window owner, string title, string text)
    {
        var window = new Window { Title = title, Width = 610, Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var no = new Button { Content = "Cancel", IsCancel = true };
        var yes = new Button { Content = "Continue" };
        no.Click += (_, _) => window.Close(false);
        yes.Click += (_, _) => window.Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        window.Content = new DockPanel { Margin = new Thickness(20), LastChildFill = true,
            Children = { buttons, new ScrollViewer { Content = new TextBlock {
                Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0,0,0,16)
            } } } };
        return await window.ShowDialog<bool>(owner);
    }

    internal static async Task Error(Window owner, string title, Exception error)
    {
        var detail = error is OperationFailure failure ? failure.TechnicalDetails : error.ToString();
        var window = new Window { Title = title, Width = 680, Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = {
            new TextBlock { Text = error.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new Expander { Header = "Technical details", Content = new TextBox {
                Text = detail, IsReadOnly = true, AcceptsReturn = true, Height = 230 } }, close
        } };
        await window.ShowDialog(owner);
    }
}
