using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BACnetExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep this desktop UI off the WPF hardware rendering path.
        // This is process-local and does not change Windows graphics settings.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Go BACnet Workbench", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
