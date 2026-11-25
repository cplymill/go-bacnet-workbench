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
        foreach (var action in new[] { "restart", "disconnect", "disconnect-focused", "close", "close-unresponsive-watch" })
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
            void FocusDevice(Peer peer)
            {
                Set(window, "selectedPeer", peer);
                Set(window, "focusedPeer", peer);
                Invoke(window, "SetNavigationView");
            }

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
                var list = (TreeView)window.FindName("DeviceList");
                Check(list.IsEnabled, "Cancel left device selection disabled.");
                FocusDevice(peers[0]);
                var back = (Button)window.FindName("BackButton");
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Field<bool>(window, "connected") && list.Visibility == Visibility.Visible && list.Items.Count == 2,
                    "All devices did not return to the live discovery results.");
            }
            else if (action is "disconnect" or "disconnect-focused")
            {
                var retained = peers.Single();
                var list = (TreeView)window.FindName("DeviceList");
                var settings = (Expander)window.FindName("ConnectionSettings");
                settings.IsExpanded = false;
                if (action == "disconnect-focused")
                {
                    await (Task)Invoke(window, "StopActiveOperation")!;
                    FocusDevice(retained);
                    Check(list.Visibility == Visibility.Collapsed, "Fixture did not enter the focused device view.");
                }

                Invoke(window, "Disconnect", null, new RoutedEventArgs());
                await scan.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => !Field<bool>(window, "connected") && !Field<bool>(window, "busy"));
                Check(((Button)window.FindName("ConnectButton")).IsEnabled && settings.IsExpanded,
                    "Disconnect did not restore connection controls.");
                Check(list.Visibility == Visibility.Visible && !list.IsEnabled &&
                    list.Items.Count == 1 && ReferenceEquals(list.Items[0], retained),
                    "Disconnect did not show the last discovery results as offline.");
                Check(((TextBlock)window.FindName("NavigationSummary")).Text.Contains("Last discovery", StringComparison.Ordinal),
                    "The offline device list is missing its last discovery status.");
                var discover = (Button)window.FindName("DiscoverButton");
                Check(((StackPanel)window.FindName("DiscoveryControls")).Visibility == Visibility.Visible &&
                    discover.IsEnabled && Equals(discover.Content, "Connect and discover"),
                    "Disconnect did not offer discovery from the Devices view.");

                if (action == "disconnect-focused")
                {
                    target.Text = "192.168.47.12";
                    discover.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => Field<bool>(window, "connected") && peers.Any(peer => peer.Address == "192.168.47.12"));
                    Check(peers.Count == 1 && list.IsEnabled && Equals(discover.Content, "Discover devices"),
                        "Reconnect did not replace stale routes with the new discovery results.");
                    Invoke(window, "Disconnect", null, new RoutedEventArgs());
                    await Until(() => !Field<bool>(window, "connected") && !Field<bool>(window, "busy"));
                }
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
        Console.WriteLine("PASS: discovery can be replaced and canceled. Disconnect restores the offline device list and offers reconnection and a fresh scan. Close drains discovery and bounds COV cleanup.");
    }
}
