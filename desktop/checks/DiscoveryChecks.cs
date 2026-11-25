using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BACnetExplorer;

internal static class DiscoveryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static void Set(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    internal static async Task Run()
    {
        foreach (var action in new[] { "restart", "disconnect", "close", "close-unresponsive-watch" })
        {
            await using var engine = EngineClientChecks.Start();
            var window = new MainWindow();
            Set(window, "engine", engine);
            Set(window, "connected", true);
            var target = (TextBox)window.FindName("DiscoveryTarget");
            target.Text = "192.168.47.10";
            var scan = (Task)Invoke(window, "Run", "Discover", (Func<Task>)(() =>
                (Task)Invoke(window, "DiscoverDevices")!))!;
            var peers = Field<ObservableCollection<Peer>>(window, "peers");
            await Until(() => peers.Count == 1);
            Check(Field<bool>(window, "busy"), "Discovery fixture completed unexpectedly.");
            Check(((Button)window.FindName("DisconnectButton")).IsEnabled,
                "Discovery disabled Disconnect.");
            if (action == "restart")
            {
                target.Text = "192.168.47.11";
                Invoke(window, "Discover", null, new RoutedEventArgs());
                await scan.WaitAsync(TimeSpan.FromSeconds(5));
                // The replacement target is published while the earlier result stays available.
                await Until(() => peers.Any(peer => peer.Address == "192.168.47.11"));
                Check(Field<bool>(window, "discovering"), "Replacement search did not start.");
                await (Task)Invoke(window, "StopActiveOperation")!;
                Check(!Field<bool>(window, "busy") && peers.Count == 2,
                    "Cancel did not release navigation and retain discovered results.");
                Check(((TreeView)window.FindName("DeviceList")).IsEnabled,
                    "Cancel left device selection disabled.");
            }
            else if (action == "disconnect")
            {
                Invoke(window, "Disconnect", null, new RoutedEventArgs());
                await scan.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => !Field<bool>(window, "connected") && !Field<bool>(window, "busy"));
                Check(((Button)window.FindName("ConnectButton")).IsEnabled,
                    "Disconnect did not enable another connection.");
            }
            else
            {
                if (action == "close-unresponsive-watch")
                    Field<ObservableCollection<Watch>>(window, "watches").Add(new Watch
                    {
                        Device = "fixture",
                        Object = "2:1",
                        Process = 7
                    });
                var args = new CancelEventArgs();
                Invoke(window, "WindowClosing", window, args);
                Check(args.Cancel, "Window closed before the active operation was drained.");
                await Until(() => Field<bool>(window, "shutdownComplete"));
                await scan.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!engine.IsAvailable, "Close left its private engine available.");
            }
        }
        Console.WriteLine("PASS: active directed search can be replaced, canceled with results retained, disconnected, and closed; an unresponsive COV cleanup cannot hold shutdown.");
    }
}
