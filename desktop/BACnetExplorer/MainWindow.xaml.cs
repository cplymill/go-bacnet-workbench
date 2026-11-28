using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BACnetExplorer;

public partial class MainWindow : Window
{
    private EngineClient? engine;
    private readonly ObservableCollection<Peer> peers = [];
    private readonly ObservableCollection<Peer> treeRoots = [];
    private readonly ObservableCollection<ObjectRow> objects = [];
    private readonly ObservableCollection<PropertyRow> properties = [];
    private readonly ObservableCollection<Watch> watches = [];
    private readonly ObservableCollection<ActivityRow> activity = [];
    private List<PropertyRow> baseline = [];
    private List<Difference> differences = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Peer? selectedPeer;
    private bool connected, busy, polling, closing, dirty, cancelRequested;
    private bool discovering;
    private bool loadingNames;
    private bool monitorReading;
    private TaskCompletionSource? operationDone;
    private TaskCompletionSource? monitorDone;
    private Peer? priorityPeer;
    private string priorityObject = "";
    private string[] prioritySnapshot = [];
    private string[] priorityDisplaySnapshot = [];
    private IObjectEditor? objectEditor;
    private Peer? focusedPeer;
    private ObjectRow? inspectedObject;
    private bool restoringObjectSelection;
    private DateTimeOffset nextPoll;
    private ulong notificationSequence, notificationDrops;
    private uint nextProcess = 1;
    private string? workspacePath;
    private readonly Dictionary<string, string> interfaceBroadcasts = new();
    private readonly Dictionary<int, string> linkNetworks = new() { [0] = "0", [1] = "2001", [2] = "0", [3] = "0" };
    private int shownTransport;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Go BACnet Workbench " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        for (var slot = 0; slot < 17; slot++)
            PriorityGrid.Columns.Add(new DataGridTextColumn { Header = slot == 5 ? new TextBlock { Text = "PV6", ToolTip = "Reserved for device minimum on/off timing; operator writes disabled" } : slot < 16 ? $"PV{slot + 1}" : "RD", Binding = new Binding($"[{slot}]"), IsReadOnly = slot is 5 or 16, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 55 });
        ScUuid.Text = Guid.NewGuid().ToString("N");
        var randomMac = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6);
        randomMac[0] = 2;
        Vmac.Text = Convert.ToHexString(randomMac).ToLowerInvariant();
        DeviceList.ItemsSource = treeRoots;
        ObjectGrid.ItemsSource = objects;
        PropertyGrid.ItemsSource = properties;
        WatchGrid.ItemsSource = watches;
        ActivityGrid.ItemsSource = activity;
        timer.Tick += TimerTick;
        Loaded += async (_, _) => await Run("Initialize", async () =>
        {
            engine = new EngineClient();
            var system = await engine.Call("system");
            foreach (var address in system.GetProperty("interfaces").EnumerateArray())
                Bind.Items.Add(address.GetString());
            foreach (var profile in system.GetProperty("interfaceProfiles").EnumerateArray())
                interfaceBroadcasts[profile.GetProperty("bind").GetString()!] = profile.GetProperty("broadcast").GetString()!;
            if (interfaceBroadcasts.Count > 0)
            {
                Bind.Text = interfaceBroadcasts.Keys.OrderBy(ip => ip.StartsWith("192.168.", StringComparison.Ordinal) ? 0 : ip.StartsWith("10.", StringComparison.Ordinal) ? 1 : 2).First();
                Broadcast.Text = interfaceBroadcasts[Bind.Text];
            }
            Bind.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (!connected && interfaceBroadcasts.TryGetValue(Bind.Text, out var broadcast)) Broadcast.Text = broadcast; }));
            Transport.SelectionChanged += (_, _) => UpdateTransportFields();
            UpdateTransportFields();
            foreach (var port in system.GetProperty("serialPorts").EnumerateArray())
                SerialPort.Items.Add(port.GetString());
            Status.Text = "Ready · Offline. Choose an interface and Connect.";
            foreach (var field in new[] { Broadcast, Mac, MaxMaster, Network, Timeout, Retries, Bbmd, ForeignTtl, Ipv6Bind, Ipv6Interface, Vmac, Hub, FailoverHub, Certificate, PrivateKey, SiteCa, ScUuid })
                field.TextChanged += (_, _) => dirty = true;
            Bind.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => dirty = true));
            SerialPort.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => dirty = true));
            Baud.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => dirty = true));
            Transport.SelectionChanged += (_, _) => dirty = true;
        });
    }

    private async Task Run(string operation, Func<Task> action)
    {
        if (busy || closing || objectEditor?.IsBusy == true)
            return;
        busy = true;
        operationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelRequested = false;
        Progress.Visibility = Visibility.Visible;
        ConnectButton.IsEnabled = DisconnectButton.IsEnabled = false;
        Transport.IsEnabled = false;
        DeviceList.IsEnabled = ObjectGrid.IsEnabled = false;
        PriorityGrid.IsEnabled = PriorityActions.IsEnabled = false;
        PropertyGrid.IsEnabled = PropertyEditor.IsEnabled = false;
        Status.Text = operation + "…";
        try
        {
            // A foreground action owns the UI immediately. Let the current
            // background exchange finish; interrupt a pending read-only poll.
            if (monitorDone is { } monitoring)
            {
                // Only interrupt read-only watch polling. Subscription renewal
                // drains normally, and a foreground write is never auto-canceled.
                if (monitorReading)
                    await Engine.Call("cancel");
                await monitoring.Task;
            }
            await action();
            Log(operation, selectedPeer?.Caption ?? "Desktop", "Completed");
        }
        catch (Exception ex)
        {
            Status.Text = operation + ": " + ex.Message;
            Log(operation, selectedPeer?.Caption ?? "Desktop", ex.Message + "\n\n" + OperationAlert.Details(operation, ex));
            if (engine is { IsAvailable: false })
                MarkEngineUnavailable(ex.Message);
            if (!cancelRequested && !closing)
            {
                var target = selectedPeer == null ? "" : $"{selectedPeer.Caption} at {selectedPeer.Address} · Route {selectedPeer.Route}";
                new OperationAlert(operation, ex, target) { Owner = this }.ShowDialog();
            }
        }
        finally
        {
            busy = false;
            Progress.Visibility = Visibility.Collapsed;
            ConnectButton.IsEnabled = !connected;
            DisconnectButton.IsEnabled = connected;
            Transport.IsEnabled = !connected;
            ConnectionFields.IsEnabled = !connected;
            DeviceList.IsEnabled = connected && objectEditor?.IsBusy != true;
            ObjectGrid.IsEnabled = objectEditor?.IsBusy != true;
            PriorityGrid.IsEnabled = PriorityActions.IsEnabled = true;
            PropertyGrid.IsEnabled = PropertyEditor.IsEnabled = objectEditor?.IsBusy != true;
            operationDone.TrySetResult();
        }
    }

    private EngineClient Engine => engine ?? throw new InvalidOperationException("BACnet engine is unavailable. Build and reopen the packaged app.");
    private Peer CurrentPeer => connected && selectedPeer != null ? selectedPeer : throw new InvalidOperationException("Connect and select a discovered device first.");
    private static T Decode<T>(JsonElement value) => value.Deserialize<T>(EngineClient.Json) ?? throw new InvalidDataException("Empty engine result.");
    private static int Integer(TextBox box, int low, int high)
    {
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < low || value > high)
            throw new InvalidDataException($"{box.Name} must be {low}..{high}.");
        return value;
    }
    private uint? IndexValue => string.IsNullOrWhiteSpace(ArrayIndex.Text) ? null : uint.Parse(ArrayIndex.Text, CultureInfo.InvariantCulture);
    private static string ComboValue(ComboBox box) => box.SelectedItem is ComboBoxItem item ? item.Content.ToString()! : box.Text;
    private ConnectionProfile Profile() => new()
    {
        Transport = new[] { "ip", "mstp", "ipv6", "sc" }[Transport.SelectedIndex],
        Bind = Bind.Text.Trim(),
        Broadcast = Broadcast.Text.Trim(),
        Serial = SerialPort.Text.Trim(),
        Baud = Transport.SelectedIndex == 1 ? int.Parse(ComboValue(Baud), CultureInfo.InvariantCulture) : 38400,
        Mac = Transport.SelectedIndex == 1 ? Integer(Mac, 0, 127) : 18,
        MaxMaster = Transport.SelectedIndex == 1 ? Integer(MaxMaster, 0, 127) : 127,
        Network = (ushort)Integer(Network, 0, 65534),
        TimeoutMs = Integer(Timeout, 100, 30000),
        Retries = Integer(Retries, 0, 5),
        ConfirmJoin = Transport.SelectedIndex == 1,
        Bbmd = Transport.SelectedIndex == 0 ? Bbmd.Text.Trim() : "",
        Ttl = Transport.SelectedIndex == 0 ? (ushort)Integer(ForeignTtl, 30, 3600) : (ushort)120,
        Ipv6Bind = Ipv6Bind.Text.Trim(),
        InterfaceIndex = Transport.SelectedIndex == 2 ? Integer(Ipv6Interface, 1, int.MaxValue) : 1,
        Vmac = Vmac.Text.Trim(),
        Hub = Hub.Text.Trim(),
        FailoverHub = FailoverHub.Text.Trim(),
        Certificate = Certificate.Text.Trim(),
        PrivateKey = PrivateKey.Text.Trim(),
        SiteCa = SiteCa.Text.Trim(),
        Uuid = ScUuid.Text.Trim(),
        VerifyServerName = VerifyServerName.IsChecked == true
    };

    private void ApplyProfile(ConnectionProfile profile)
    {
        Transport.SelectedIndex = Array.IndexOf(new[] { "ip", "mstp", "ipv6", "sc" }, profile.Transport);
        if (Transport.SelectedIndex < 0)
            throw new InvalidDataException("Unknown workspace transport.");
        Bind.Text = profile.Bind;
        Broadcast.Text = profile.Broadcast;
        SerialPort.Text = profile.Serial;
        Baud.Text = profile.Baud.ToString(CultureInfo.InvariantCulture);
        Mac.Text = profile.Mac.ToString(CultureInfo.InvariantCulture);
        MaxMaster.Text = profile.MaxMaster.ToString(CultureInfo.InvariantCulture);
        Network.Text = profile.Network.ToString(CultureInfo.InvariantCulture);
        Timeout.Text = profile.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        Retries.Text = profile.Retries.ToString(CultureInfo.InvariantCulture);
        Bbmd.Text = profile.Bbmd;
        ForeignTtl.Text = profile.Ttl.ToString(CultureInfo.InvariantCulture);
        Ipv6Bind.Text = profile.Ipv6Bind;
        Ipv6Interface.Text = profile.InterfaceIndex.ToString(CultureInfo.InvariantCulture);
        Vmac.Text = profile.Vmac;
        Hub.Text = profile.Hub;
        FailoverHub.Text = profile.FailoverHub;
        Certificate.Text = profile.Certificate;
        PrivateKey.Text = profile.PrivateKey;
        SiteCa.Text = profile.SiteCa;
        ScUuid.Text = profile.Uuid;
        VerifyServerName.IsChecked = profile.VerifyServerName;
    }

    private async void Connect(object sender, RoutedEventArgs e) => await Run("Connect", ConnectAndDiscover);

    private async Task ConnectAndDiscover()
    {
        if (!CanLeaveInspector())
            return;
        if (engine == null || !engine.IsAvailable)
        {
            if (engine != null)
                await engine.DisposeAsync();
            engine = new EngineClient();
        }
        var result = await Engine.Call("connect", Profile());
        peers.Clear();
        treeRoots.Clear();
        SetConnectionState(true);
        ResetDeviceView();
        timer.Start();
        notificationSequence = notificationDrops = 0;
        ConnectionSettings.IsExpanded = false;
        Status.Text = "Connected · " + result.GetProperty("address").GetString();
        dirty = true;
        await DiscoverDevices();
    }

    private async void Disconnect(object sender, RoutedEventArgs e)
    {
        await InterruptBackgroundRead();
        await Run("Disconnect", DisconnectSession);
    }

    private async Task DisconnectSession()
    {
        if (!CanLeaveInspector())
            return;
        if (connected)
        {
            await CancelAllSubscriptions();
            await Engine.Call("disconnect");
        }
        SetOffline();
    }

    private void MarkEngineUnavailable(string message)
    {
        timer.Stop();
        polling = false;
        MonitorButton.Content = "Start polling";
        SetConnectionState(false);
        foreach (var watch in watches)
        {
            watch.Process = 0;
            watch.Mark("Connection lost");
        }
        // Keep observations and unsent drafts available for review. Connect creates a fresh engine.
        Status.Text = message + " · Connect to recover. Read back before retrying any write.";
    }

    private void SetOffline()
    {
        timer.Stop();
        polling = false;
        MonitorButton.Content = "Start polling";
        SetConnectionState(false);
        ConnectionSettings.IsExpanded = true;
        ResetDeviceView();
        foreach (var watch in watches)
        {
            watch.Process = 0;
            watch.Mark("Disconnected");
        }
        Status.Text = "Disconnected. Connect to discover devices.";
    }

    private void SetConnectionState(bool online)
    {
        connected = online;
        ConnectionBadge.Text = online ? "●  CONNECTED" : "●  OFFLINE";
        DiscoverButton.Content = online ? "Discover devices" : "Connect and discover";
        DeviceList.IsEnabled = online && !busy && objectEditor?.IsBusy != true;
        UpdateNavigationSummary();
    }

    private async void CancelOperation(object sender, RoutedEventArgs e)
    {
        cancelRequested = true;
        var wasDiscovering = discovering;
        try
        {
            await StopActiveOperation();
            Status.Text = wasDiscovering ? $"Discovery stopped · {peers.Count} devices retained"
                : "Operation stopped. A transmitted write may already have taken effect; read back before retrying.";
        }
        catch (Exception ex) { MarkEngineUnavailable(ex.Message); }
    }

    private async void Discover(object sender, RoutedEventArgs e)
    {
        if (!connected)
        {
            await Run("Connect", ConnectAndDiscover);
            return;
        }

        await InterruptBackgroundRead();
        if (connected)
            await Run("Discover", DiscoverDevices);
    }
    private async Task DiscoverDevices()
    {
        if (!connected)
            throw new InvalidOperationException("Connect first.");
        uint? low = string.IsNullOrWhiteSpace(LowLimit.Text) ? null : uint.Parse(LowLimit.Text, CultureInfo.InvariantCulture);
        uint? high = string.IsNullOrWhiteSpace(HighLimit.Text) ? null : uint.Parse(HighLimit.Text, CultureInfo.InvariantCulture);
        discovering = true;
        DeviceList.IsEnabled = true;
        DisconnectButton.IsEnabled = true;
        try
        {
            var result = await Engine.Call("discover", new
            {
                target = DiscoveryTarget.Text.Trim(),
                seconds = Integer(DiscoverySeconds, 5, 120),
                low,
                high,
                remoteNetwork = string.IsNullOrWhiteSpace(DiscoveryTarget.Text) ? Integer(RemoteNetwork, 0, 65535) : 0
            }, update => Dispatcher.InvokeAsync(() => MergeDiscovered(update), DispatcherPriority.Background).Task);
            MergeDiscovered(result);
            Status.Text = $"Discovery complete · {peers.Count} devices · {peers.Count(p => p.Router)} routers";
        }
        finally { discovering = false; }
    }

    private void MergeDiscovered(JsonElement result)
    {
        var known = peers.ToDictionary(p => p.Key);
        foreach (var incoming in Decode<List<Peer>>(result))
        {
            if (known.TryGetValue(incoming.Key, out var existing))
                existing.UpdateRoute(incoming);
            else
            {
                incoming.IsExpanded = incoming.Router;
                peers.Add(incoming);
                known[incoming.Key] = incoming;
            }
        }
        foreach (var peer in peers)
        {
            var parent = peer.RemoteNetwork == 0 ? null : peers.FirstOrDefault(p => p.Router && p.Address == peer.Address && p.Networks.Contains(peer.RemoteNetwork));
            foreach (var router in peers.Where(p => p != parent && p.Children.Contains(peer)))
                router.Children.Remove(peer);
            if (parent == null)
            {
                if (!treeRoots.Contains(peer))
                    treeRoots.Add(peer);
            }
            else
            {
                treeRoots.Remove(peer);
                if (!parent.Children.Contains(peer))
                    parent.Children.Add(peer);
            }
        }
        if (focusedPeer == null)
            UpdateNavigationSummary();
        Status.Text = $"Discovering · {peers.Count} devices · {peers.Count(p => p.Router)} routers · Cancel operation stops the scan and keeps these results";
    }

    private void UpdateTransportFields()
    {
        linkNetworks[shownTransport] = Network.Text;
        shownTransport = Transport.SelectedIndex;
        Network.Text = linkNetworks[shownTransport];
        var serial = Transport.SelectedIndex == 1;
        var ipv4 = Transport.SelectedIndex == 0;
        var ipv6 = Transport.SelectedIndex == 2;
        var secure = Transport.SelectedIndex == 3;
        static void Fields(bool visible, params FrameworkElement[] fields)
        {
            foreach (var field in fields)
                ((FrameworkElement)field.Parent).Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        Fields(serial, SerialPort, Baud, Mac, MaxMaster);
        Fields(ipv4, Bind, Broadcast, Bbmd, ForeignTtl);
        Fields(ipv6, Ipv6Bind, Ipv6Interface);
        Fields(ipv6 || secure, Vmac);
        Fields(secure, Hub, FailoverHub, ScUuid, Certificate, PrivateKey, SiteCa);
        VerifyServerName.Visibility = secure ? Visibility.Visible : Visibility.Collapsed;
        AdvancedConnection.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;
        AdvancedConnection.Header = ipv4 ? "Foreign device registration" : ipv6 ? "IPv6 settings" : "Secure Connect settings";
        TransportHelp.Text = ipv4 ? "Optional BBMD endpoint and registration lifetime."
            : ipv6 ? "Choose the IPv6 interface and an unused VMAC."
            : "Select the hub and site credentials. Private keys are never stored in workspaces.";
    }

    private async void DeviceTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!connected || e.NewValue == null)
            return;
        await InterruptBackgroundRead();
        if (!connected || busy)
            return;
        if (DeviceList.SelectedItem is ObjectRow row && row.Owner is Peer owner)
        {
            // Admit navigation before changing the current device/write target.
            await InspectObject(owner, row);
            return;
        }
        if (DeviceList.SelectedItem is not Peer peer)
            return;
        if (!CanLeaveInspector())
            return;
        ResetObjectEditor();
        selectedPeer = peer;
        focusedPeer = peer;
        ObjectFilter.Text = "";
        ObjectTypeFilter.SelectedIndex = 0;
        SetNavigationView();
        SelectedDeviceTitle.Text = $"{peer.Caption} · {peer.Address}";
        replacingPropertyRows = true;
        try
        {
            properties.Clear();
        }
        finally { replacingPropertyRows = false; }
        objects.Clear();
        await Run("Read device", async () =>
        {
            ObjectId.Text = ObjectLabels.Display(peer.Device);
            if (!peer.ObjectsLoaded)
                await LoadInventory(peer, false);
            else
            {
                objects.Clear();
                foreach (var item in peer.Children.OfType<ObjectRow>())
                    objects.Add(item);
            }
            await LoadProperties(peer, peer.Device);
            await ReadObjectNames(peer);
        });
    }

    private async Task InterruptBackgroundRead()
    {
        if ((!loadingNames && !discovering) || operationDone == null)
            return;
        try
        {
            await StopActiveOperation();
        }
        catch (IOException ex) { MarkEngineUnavailable(ex.Message); }
    }

    private async Task StopActiveOperation()
    {
        var completed = busy ? operationDone?.Task : null;
        cancelRequested = true;
        try
        {
            await Engine.Call("cancel");
        }
        finally { if (completed != null) await completed; }
    }

    private async Task ReadObjectNames(Peer peer)
    {
        var lookup = peer.Children.OfType<ObjectRow>().ToDictionary(o => o.Object);
        if (lookup.Count == 0)
            return;
        loadingNames = true;
        DeviceList.IsEnabled = ObjectGrid.IsEnabled = true;
        var received = lookup.Values.Count(o => o.Name.Length > 0);
        void ApplyNames(JsonElement update)
        {
            foreach (var row in Rows(update))
            {
                if (lookup.TryGetValue(row.Object, out var item) && row.Error.Length == 0 && row.Property == "object-name")
                {
                    var name = row.Value;
                    if (name.StartsWith('"'))
                        name = JsonSerializer.Deserialize<string>(name) ?? name;
                    item.Name = name;
                }
                if (item != null && row.Error.Length == 0 && row.Property == "present-value")
                    item.Value = row.Value;
                if (row.Property == "object-name")
                    received++;
            }
            InventorySummary.Text = $"{lookup.Count} objects · names {Math.Min(received, lookup.Count)} / {lookup.Count}";
            RefreshObjectSearch();
            UpdateNavigationSummary();
        }
        try
        {
            var selections = lookup.Values.Where(o => o.Name.Length == 0).Select(o => new { @object = o.Object, property = "object-name" })
                .Concat(lookup.Keys.Select(id => new { @object = id, property = "present-value" }));
            await Engine.Call("read", new
            {
                device = peer.Key,
                selections
            }, update => Dispatcher.InvokeAsync(() => ApplyNames(update), DispatcherPriority.Background).Task);
            Status.Text = $"{lookup.Count} objects · names and present values loaded · select an object to inspect its properties";
        }
        finally { loadingNames = false; }
    }

    private async Task<JsonElement> LoadInventory(Peer peer, bool enrich = true)
    {
        if (!enrich)
        {
            objects.Clear();
            for (var index = peer.Children.Count - 1; index >= 0; index--)
                if (peer.Children[index] is ObjectRow)
                    peer.Children.RemoveAt(index);
        }
        var result = await Engine.Call(enrich ? "inventory" : "object-list", new
        {
            device = peer.Key
        }, enrich ? null : update => Dispatcher.InvokeAsync(() =>
        {
            foreach (var item in Decode<List<ObjectRow>>(update.GetProperty("objects")))
            {
                item.Owner = peer;
                objects.Add(item);
                peer.Children.Add(item);
            }
            peer.IsExpanded = true;
            InventorySummary.Text = $"Reading Object_List · {update.GetProperty("completed")} / {update.GetProperty("total")} objects";
            Status.Text = InventorySummary.Text;
            NavigationSummary.Text = $"{update.GetProperty("completed")} / {update.GetProperty("total")} objects · {peer.Address}";
        }, DispatcherPriority.Background).Task);
        var items = Decode<List<ObjectRow>>(result.GetProperty("objects"));
        var rows = result.TryGetProperty("enrichment", out var enriched) ? Rows(enriched) : [];
        // Index once instead of scanning all property results three times per
        // object. Keep the first result for each field, including explicit errors.
        var fields = new Dictionary<(string Object, string Property), PropertyRow>();
        foreach (var row in rows)
            fields.TryAdd((row.Object, row.Property), row);
        var existing = peer.Children.OfType<ObjectRow>().ToDictionary(item => item.Object);
        for (var index = 0; index < items.Count; index++)
        {
            var incoming = items[index];
            var item = existing.GetValueOrDefault(incoming.Object) ?? incoming;
            if (enrich)
            {
                string Field(string name) => fields.TryGetValue((item.Object, name), out var row) ? row.Result : "";
                item.Name = Field("object-name");
                item.Value = Field("present-value");
                item.Units = Field("units");
            }
            item.Owner = peer;
            items[index] = item;
        }
        // The final Object_List response repeats pages already displayed. Avoid
        // another collection reset and retain row identity during enrichment.
        if (!objects.SequenceEqual(items))
        {
            objects.Clear();
            foreach (var item in items)
                objects.Add(item);
        }
        if (!peer.Children.OfType<ObjectRow>().SequenceEqual(items))
        {
            for (var index = peer.Children.Count - 1; index >= 0; index--)
                if (peer.Children[index] is ObjectRow)
                    peer.Children.RemoveAt(index);
            foreach (var item in items)
                peer.Children.Add(item);
        }
        RefreshObjectSearch();
        peer.ObjectsLoaded = true;
        peer.IsExpanded = true;
        UpdateNavigationSummary();
        var fallback = result.GetProperty("indexedFallback").GetBoolean() ? "indexed Object_List" : "whole Object_List";
        InventorySummary.Text = $"{items.Count} objects · {fallback}" + (enrich ? $" · {ReadStats(enriched)}" : " · Select an object to inspect; Read names & values enriches the list");
        if (result.TryGetProperty("enrichmentError", out var error))
            InventorySummary.Text += " · INCOMPLETE: " + error.GetString();
        Status.Text = InventorySummary.Text;
        return result;
    }

    private async void RefreshInventory(object sender, RoutedEventArgs e) => await Run("Read object list", async () => { await LoadInventory(CurrentPeer, false); await ReadObjectNames(CurrentPeer); });
    private async void ReadObjectValues(object sender, RoutedEventArgs e) => await Run("Read names & values", async () => await LoadInventory(CurrentPeer));
    private async void ObjectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (restoringObjectSelection || ObjectGrid.SelectedItem is not ObjectRow item)
            return;
        await InterruptBackgroundRead();
        if (busy || !ReferenceEquals(ObjectGrid.SelectedItem, item))
            return;
        ObjectId.Text = item.ObjectLabel;
        await InspectObject(CurrentPeer, item);
    }
    private async Task InspectObject(Peer peer, ObjectRow item)
    {
        if (objectEditor?.BindingKey == peer.Key + "/" + item.Object)
        {
            ShowEditorView();
            return;
        }
        if (!CanLeaveInspector())
        {
            restoringObjectSelection = true;
            ObjectGrid.SelectedItem = inspectedObject;
            ObjectId.Text = ObjectLabels.Display(inspectedObject?.Object ?? peer.Device);
            restoringObjectSelection = false;
            return;
        }
        if (selectedPeer?.Key != peer.Key)
        {
            selectedPeer = peer;
            objects.Clear();
            foreach (var child in peer.Children.OfType<ObjectRow>())
                objects.Add(child);
        }
        ObjectId.Text = item.ObjectLabel;
        ResetObjectEditor();
        inspectedObject = item;
        SelectedDeviceTitle.Text = item.Name.Length > 0 ? $"{item.Name} · {item.ObjectLabel}" : item.Caption;
        await Run("Inspect object", async () =>
        {
            if (item.Object.StartsWith("17:", StringComparison.Ordinal))
            {
                var editor = new ScheduleEditorControl(Engine, peer, item.Object, item.Name);
                ShowObjectEditor(editor, editor);
            }
            else if (item.Object.StartsWith("6:", StringComparison.Ordinal))
            {
                var editor = new CalendarEditorControl(Engine, peer, item.Object, item.Name);
                ShowObjectEditor(editor, editor);
            }
            else
                await LoadProperties(peer, item.Object);
        });
    }

    private void ShowObjectEditor(FrameworkElement content, IObjectEditor editor)
    {
        objectEditor = editor;
        editor.WorkingChanged += working =>
        {
            if (!ReferenceEquals(objectEditor, editor))
                return;
            DeviceList.IsEnabled = connected && !working && !busy;
            ObjectGrid.IsEnabled = !working && !busy;
            DisconnectButton.IsEnabled = connected && !working && !busy;
            Progress.Visibility = working || busy ? Visibility.Visible : Visibility.Collapsed;
        };
        ObjectEditorHost.Content = content;
        VisualEditorButton.Content = editor is ScheduleEditorControl ? "Schedule view" : "Calendar view";
        VisualEditorButton.Visibility = Visibility.Visible;
        ShowEditorView();
    }

    private void ShowEditorView()
    {
        ObjectEditorHost.Visibility = Visibility.Visible;
        ExplorerHeader.Visibility = PropertyInspectorContent.Visibility = PropertyEditor.Visibility = Visibility.Collapsed;
        UpdatePropertyEditorLayout();
    }
    private void ViewObjectEditor(object sender, RoutedEventArgs e)
    {
        if (objectEditor != null)
            ShowEditorView();
        else
            Status.Text = "Select a Schedule or Calendar to open its editor.";
    }
    private async void ViewObjectProperties(object sender, RoutedEventArgs e) => await Run("Read properties", async () =>
    {
        var id = inspectedObject?.Object ?? (ObjectGrid.SelectedItem as ObjectRow)?.Object ?? CurrentPeer.Device;
        ObjectEditorHost.Visibility = Visibility.Collapsed;
        ExplorerHeader.Visibility = PropertyInspectorContent.Visibility = PropertyEditor.Visibility = Visibility.Visible;
        UpdatePropertyEditorLayout();
        ObjectId.Text = ObjectLabels.Display(id);
        await LoadProperties(CurrentPeer, id);
    });

    private void ResetObjectEditor()
    {
        objectEditor = null;
        ClearPriorityChanges();
        inspectedObject = null;
        VisualEditorButton.Visibility = Visibility.Collapsed;
        ObjectEditorHost.Content = null;
        ObjectEditorHost.Visibility = Visibility.Collapsed;
        ExplorerHeader.Visibility = Visibility.Visible;
        PropertyInspectorContent.Visibility = PropertyEditor.Visibility = Visibility.Visible;
        UpdatePropertyEditorLayout();
    }

    private void SetNavigationView()
    {
        var focused = focusedPeer != null;
        DeviceList.Visibility = DeviceFilter.Visibility = DiscoveryControls.Visibility = focused ? Visibility.Collapsed : Visibility.Visible;
        ObjectPane.Visibility = ObjectFilter.Visibility = BackButton.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        FocusedActions.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        NavigationTitle.Text = focusedPeer?.Caption ?? "Devices";
        NavigationSearchLabel.Text = focused ? "Search point names, types, IDs or PV" : "Search devices";
        UpdateNavigationSummary();
    }

    private void UpdateNavigationSummary()
    {
        DeviceCount.Text = (focusedPeer == null ? peers.Count : objects.Count).ToString(CultureInfo.InvariantCulture);
        if (focusedPeer != null)
        {
            NavigationSummary.Text = $"{objects.Count} objects · {objects.Count(o => o.Name.Length > 0)} named · {objects.Count(o => o.Value.Length > 0)} present values\n{focusedPeer.Address} · route {focusedPeer.Route}";
            return;
        }

        var counts = $"{peers.Count} devices · {peers.Count(p => p.Router)} routers";
        NavigationSummary.Text = connected ? counts : peers.Count == 0 ? "Offline" : $"Offline · Last discovery · {counts}";
    }

    private async void BackToDevices(object sender, RoutedEventArgs e)
    {
        await InterruptBackgroundRead();
        if (busy || objectEditor?.IsBusy == true || !CanLeaveInspector())
            return;
        ResetDeviceView();
    }

    private void ResetDeviceView()
    {
        focusedPeer = selectedPeer = null;
        DeviceList.ItemsSource = null;
        DeviceList.ItemsSource = treeRoots;
        DeviceFilter.Clear();
        objects.Clear();
        replacingPropertyRows = true;
        try
        {
            properties.Clear();
        }
        finally
        {
            replacingPropertyRows = false;
        }
        ResetObjectEditor();
        PriorityPanel.Visibility = Visibility.Collapsed;
        PriorityGrid.ItemsSource = null;
        priorityPeer = null;
        SelectedDeviceTitle.Text = "Select a device";
        InventorySummary.Text = connected ? "Choose a device to browse its objects." : "Connect and discover to begin.";
        SetNavigationView();
    }
    private async Task LoadProperties(Peer peer, string id)
    {
        if (priorityChanges.Count > 0 && (priorityChangePeer?.Key != peer.Key || priorityChangeObject != id))
        {
            if (!CanLeaveInspector())
                return;
            ClearPriorityChanges();
        }
        var result = await Engine.Call("properties", new
        {
            device = peer.Key,
            @object = id
        });
        ShowProperties(result);
        var errors = properties.Count(row => row.Error.Length > 0);
        Status.Text = $"{properties.Count} properties loaded · {id}" + (errors > 0 ? $" · {errors} property read errors (see rows)" : "");
        if (result.TryGetProperty("fallbackReason", out var reason))
            Log("Property read fallback", $"{peer.Caption} / {id}", reason.GetString() ?? "Explicit property reads");
    }
    private async void RefreshProperties(object sender, RoutedEventArgs e) => await Run("Read properties", async () => await LoadProperties(CurrentPeer, ObjectLabels.Numeric(ObjectId.Text)));
    private static string PropertyWriteType(PropertyRow row) => PropertyValueEditor.WireType(row);
    private static string EditablePropertyValue(PropertyRow row) => PropertyValueEditor.EditText(row);
    private async void PropertySelected(object sender, SelectionChangedEventArgs e)
    {
        if (restoringPropertySelection)
            return;
        if (!replacingPropertyRows && (PropertyGrid.SelectedItem as PropertyRow)?.Identity != editingProperty?.Identity && !CanLeavePropertyDraft())
        {
            restoringPropertySelection = true;
            try
            {
                PropertyGrid.SelectedItem = editingProperty;
            }
            finally { restoringPropertySelection = false; }
            return;
        }
        editingProperty = PropertyGrid.SelectedItem as PropertyRow;
        if (editingProperty is not PropertyRow row)
        {
            PropertyWriteInputs.Visibility = UsePropertyEditorButton.Visibility = Visibility.Collapsed;
            AdvancedPropertyAttempt.IsChecked = false;
            AdvancedPropertyAttempt.IsEnabled = false;
            PropertyAccessSummary.Text = "Select a property to inspect its access status.";
            PropertyEditTitle.Text = "Select a property above";
            PropertyCurrentValue.Text = PropertyEditHint.Text = "";
            return;
        }
        ObjectId.Text = row.ObjectLabel;
        PropertyId.Text = row.Property;
        ArrayIndex.Text = row.Index?.ToString(CultureInfo.InvariantCulture) ?? "";
        PropertyEditTitle.Text = row.DisplayName + " · " + row.ObjectLabel;
        PropertyCurrentValue.Text = "Current value: " + row.DisplayValue;
        ConfigureValueEditor(row);
        PropertyAccessSummary.Text = row.AccessStatus + " · " + row.AccessReason;
        AdvancedPropertyAttempt.IsChecked = false;
        AdvancedPropertyAttempt.IsEnabled = row.EditAccess.CanAttempt;
        UsePropertyEditorButton.Visibility = row.EditAccess.UseEditor ? Visibility.Visible : Visibility.Collapsed;
        var commandable = row.EditAccess.Commandable;
        PropertyPriorityOptions.Visibility = PropertyRelinquishButton.Visibility = commandable ? Visibility.Visible : Visibility.Collapsed;
        Priority.Text = commandable ? "8" : "0";
        if (!row.CanEdit)
            PropertyEditHint.Text = "";
        PropertyWriteInputs.Visibility = row.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        PropertyEditor.IsExpanded = true;
        propertyBaseline = DraftSignature(CapturePropertyDraft());
        await LoadPropertyUnitChoices(row);
    }
    private void AdvancedAttemptChanged(object sender, RoutedEventArgs e)
    {
        if (PropertyGrid.SelectedItem is not PropertyRow row)
            return;
        PropertyWriteInputs.Visibility = row.CanEdit || row.EditAccess.CanAttempt && AdvancedPropertyAttempt.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void OpenVisualPropertyEditor(object sender, RoutedEventArgs e)
    {
        if (busy || objectEditor?.IsBusy == true || PropertyGrid.SelectedItem is not PropertyRow row || !row.EditAccess.UseEditor)
            return;
        await InspectObject(CurrentPeer, objects.FirstOrDefault(item => item.Object == row.Object) ?? new ObjectRow { Object = row.Object });
    }
    private void FocusPropertyEditor()
    {
        if (PropertyGrid.SelectedItem is not PropertyRow row)
            return;
        PropertyEditor.IsExpanded = true;
        if (!row.CanEdit && !(row.EditAccess.CanAttempt && AdvancedPropertyAttempt.IsChecked == true))
            return;
        if (BooleanWriteValue.Visibility == Visibility.Visible)
            BooleanWriteValue.Focus();
        else
        {
            WriteValue.Focus();
            WriteValue.SelectAll();
        }
    }
    private void BeginPropertyEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PropertyRow row } || !row.CanEdit)
            return;
        PropertyGrid.SelectedItem = row;
        FocusPropertyEditor();
    }
    private void EditSelectedProperty(object sender, System.Windows.Input.MouseButtonEventArgs e) => FocusPropertyEditor();
    private static List<PropertyRow> Rows(JsonElement result) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rows", out var rows) ? Decode<List<PropertyRow>>(rows) : [];
    private static string ReadStats(JsonElement result) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rpm", out var rpm) ? $"{rpm} RPM / {result.GetProperty("rp")} RP / {result.GetProperty("splits")} splits" : "no enrichment";
    private void ShowProperties(JsonElement result) => ShowPropertyRows(Rows(result));
    private void ShowPropertyRows(IEnumerable<PropertyRow> rows)
    {
        var previous = PropertyGrid.SelectedItem as PropertyRow;
        var draft = HasPropertyDraft ? CapturePropertyDraft() : null;
        var snapshot = rows.ToList();
        // A partial read of this object must not silently remove its edited property.
        if (draft != null && previous != null && snapshot.Count > 0
            && snapshot.All(row => row.Device == previous.Device && row.Object == previous.Object)
            && snapshot.All(row => row.Identity != previous.Identity))
            snapshot.Add(previous);
        PropertyEditPolicy.Apply(snapshot);
        PropertyChangeColumn.Visibility = snapshot.Any(row => row.CanEdit) ? Visibility.Visible : Visibility.Collapsed;
        replacingPropertyRows = true;
        try
        {
            properties.Clear();
            foreach (var row in snapshot)
                properties.Add(row);
            PropertyGrid.SelectedItem = snapshot.FirstOrDefault(row => row.Identity == previous?.Identity);
            if (draft != null && PropertyGrid.SelectedItem is PropertyRow selected && selected.Identity == previous?.Identity)
                RestorePropertyDraft(draft);
        }
        finally { replacingPropertyRows = false; }
        var present = properties.FirstOrDefault(r => r.Property == "present-value" && r.Error.Length == 0);
        var priorities = properties.FirstOrDefault(r => r.Property == "priority-array" && r.Elements?.Length == 16);
        var fallback = properties.FirstOrDefault(r => r.Property == "relinquish-default");
        PriorityPanel.Visibility = present != null || priorities != null ? Visibility.Visible : Visibility.Collapsed;
        PresentValueSummary.Text = present == null ? "Priority values" : $"Present value · {present.DisplayValue}";
        PriorityGrid.Visibility = priorities == null ? Visibility.Collapsed : Visibility.Visible;
        var commandable = present?.CanEdit == true && present.EditAccess.Commandable && priorities != null;
        PriorityActions.Visibility = commandable ? Visibility.Visible : Visibility.Collapsed;
        PriorityGrid.IsReadOnly = !commandable;
        priorityObject = (priorities ?? present)?.Object ?? "";
        priorityPeer = peers.FirstOrDefault(p => p.Key == (priorities ?? present)?.Device);
        priorityValueType = present?.Type ?? fallback?.Type ?? "";
        prioritySnapshot = priorities?.Elements ?? [];
        priorityDisplaySnapshot = priorities == null ? [] : Enumerable.Range(0, priorities.Elements.Length)
            .Select(index => BacnetDisplay.Priority(priorities, index, snapshot))
            .Append(fallback == null ? "Not read" : fallback.Error.Length == 0 ? fallback.DisplayValue : fallback.DisplayError)
            .ToArray();
        RenderPriorityChanges();
    }
    private object Selector(string? id = null) => new { @object = id ?? ObjectLabels.Numeric(ObjectId.Text), property = PropertyId.Text.Trim(), index = IndexValue };
    private async void ReadPresentValue(object sender, RoutedEventArgs e) => await Run("Read present value", async () =>
    {
        var peer = CurrentPeer;
        var id = ObjectLabels.Numeric(ObjectId.Text);
        if (priorityChanges.Count > 0 && (priorityChangePeer?.Key != peer.Key || priorityChangeObject != id))
        {
            if (!CanLeaveInspector())
                return;
            ClearPriorityChanges();
        }
        var retained = properties.Where(row => row.Device == peer.Key && row.Object == id).ToList();
        var readProperties = new[] { "present-value" }
            .Concat(retained.Where(row => row.Property is "priority-array" or "relinquish-default" or "out-of-service").Select(row => row.Property)).Distinct().ToArray();
        var selections = readProperties.Select(property => new { @object = id, property }).ToArray();
        var result = await Engine.Call("read", new
        {
            device = peer.Key,
            selections
        });
        var fresh = Rows(result);
        ShowPropertyRows(retained.Where(row => !readProperties.Contains(row.Property)).Concat(fresh));
        PropertyId.Text = "present-value";
        ArrayIndex.Clear();
        var present = properties.FirstOrDefault(row => row.Property == "present-value" && row.Index == null);
        PropertyGrid.SelectedItem = present;
        if (present != null)
            PropertyGrid.ScrollIntoView(present);
        var point = objects.FirstOrDefault(row => row.Owner?.Key == peer.Key && row.Object == id);
        if (point != null && present?.Error.Length == 0)
            point.Value = present.Value;
        Status.Text = present == null ? "No present-value result returned." : present.Error.Length > 0
            ? $"{ObjectLabels.Display(id)} · Present value: {present.DisplayError}"
            : $"{ObjectLabels.Display(id)} · Present value: {present.DisplayValue}";
    });
    private async void ReadCustom(object sender, RoutedEventArgs e) => await Run("Read property", async () =>
    {
        var result = await Engine.Call("read", new
        {
            device = CurrentPeer.Key,
            selections = new[] { Selector() }
        });
        ShowProperties(result);
        Status.Text = "Read complete · " + ReadStats(result);
    });

    private async void WriteProperty(object sender, RoutedEventArgs e) => await Write(false, false);
    private async void Relinquish(object sender, RoutedEventArgs e) => await Write(true, false);
    private async void BulkWrite(object sender, RoutedEventArgs e) => await Write(false, true);
    private async Task Write(bool relinquish, bool bulk) => await Run(relinquish ? "Relinquish" : "Write property", async () =>
    {
        var peer = CurrentPeer;
        var ids = bulk ? ObjectGrid.SelectedItems.Cast<ObjectRow>().Where(o => o.Owner?.Key == peer.Key).Select(o => o.Object).ToList() : [ObjectLabels.Numeric(ObjectId.Text)];
        if (ids.Count == 0 || ids.Count > 256)
            throw new InvalidOperationException("Select 1..256 objects for a bulk write.");
        var property = PropertyId.Text.Trim();
        var index = IndexValue;
        var priority = Integer(Priority, 0, 16);
        if (relinquish && priority == 0)
            throw new InvalidOperationException("Choose an explicit priority 1..16 to relinquish.");
        PropertyEditPolicy.ValidatePriority(priority);
        var advancedAttempt = AdvancedPropertyAttempt.IsChecked == true;
        if (!PropertyArrayGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !PropertyArrayGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("Finish or cancel the current list edit first.");
        var value = relinquish ? "" : WriteValue.Text;
        var arrayValues = propertyArrayItems.Select(item => item.Value).ToArray();
        var weekday = PropertyWeekday.SelectedValue as string;
        if (property == "object-identifier" && priorityChanges.Count > 0)
        {
            if (!CanLeaveInspector())
                return;
            ClearPriorityChanges();
        }
        var targetTypes = new Dictionary<string, string>();
        var targetValues = new Dictionary<string, string>();
        var targetRows = new Dictionary<string, PropertyRow>();
        foreach (var id in ids)
        {
            var read = await Engine.Call("read", new
            {
                device = peer.Key,
                selections = new[] { new { @object = id, property, index } }
            });
            var row = Rows(read).SingleOrDefault();
            if (row == null || row.Error.Length > 0 || row.Type.Length == 0 || row.Type == "null" && !advancedAttempt && !relinquish)
                throw new InvalidOperationException($"Read the value type for {id} / {property} before writing. {row?.Error}");
            if (ids.Count > 1 && row.Property is "object-name" or "object-identifier")
                throw new InvalidOperationException("Rename objects/devices one at a time so names and identifiers remain unique.");
            var context = new List<PropertyRow> { row };
            if (row.Property is "present-value" or "relinquish-default")
            {
                var state = await Engine.Call("read", new
                {
                    device = peer.Key,
                    selections = new[] {
                    new { @object = id, property = "out-of-service" }, new { @object = id, property = "priority-array" } }
                });
                context.AddRange(Rows(state));
            }
            try
            {
                PropertyEditPolicy.RequireWrite(row, context, priority, advancedAttempt, relinquish);
            }
            catch (InvalidOperationException ex) { throw new InvalidOperationException($"{ObjectLabels.Display(id)} / {row.Property}: {ex.Message}", ex); }
            targetRows[id] = row;
            targetTypes[id] = relinquish ? "null" : PropertyWriteType(row);
            targetValues[id] = relinquish ? "" : PropertyValueEditor.WireValue(row, value, weekday, arrayValues);
        }
        var firstTarget = targetRows[ids[0]];
        var shownValue = firstTarget.ArrayValues.Length > 0 ? string.Join("; ", arrayValues.Take(12).Select((item, index) => $"{index + 1}: {item}")) : value;
        if (firstTarget.Type == "date" && PropertyWeekday.SelectedItem is PropertyChoice weekdayChoice)
            shownValue += " · " + weekdayChoice.Label;
        var targets = string.Join("\n", ids.Take(20).Select(id => $"{ObjectLabels.Display(id)}: {targetRows[id].DisplayValue} → {(relinquish ? "Relinquish" : shownValue)}"));
        var preview = $"{peer.Caption} at {peer.Address}\nRoute: {peer.Route}\nProperty: {firstTarget.DisplayName}\n\n{targets}\n\nPriority: {(priority == 0 ? "none" : priority)}\n\n{ids.Count} independent write(s). A timeout can leave an uncertain outcome. Save this change?";
        if (ids.Count > 1 && MessageBox.Show(this, preview, "Confirm bulk BACnet write", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var succeeded = 0;
        foreach (var id in ids)
        {
            if (cancelRequested)
                throw new OperationCanceledException("Bulk write canceled between objects. Earlier acknowledgements remain recorded in Activity.");
            try
            {
                var result = await Engine.Call("write", new
                {
                    device = peer.Key,
                    @object = id,
                    property,
                    index,
                    valueType = targetTypes[id],
                    value = targetValues[id],
                    priority,
                    confirmed = true
                });
                succeeded++;
                ApplyPropertyWriteResult(peer, result);
                var detail = result.TryGetProperty("readbackError", out var error) ? "ACK; readback failed: " + error.GetString() : "ACK; readback: " + string.Join("; ", Rows(result.GetProperty("readback")).Select(r => r.Result));
                Log("Write " + id, peer.Address, detail);
            }
            catch (Exception ex)
            {
                Log("Write " + id, peer.Address, "FAILED / outcome may be uncertain: " + ex.Message);
                // Stop immediately; never retry a potentially accepted write.
                throw new IOException($"Stopped after {succeeded}/{ids.Count} acknowledgements. {id}: {ex.Message}. Review Activity and read back before retrying.", ex);
            }
        }
        if (property == "object-identifier" || ids.Contains(ObjectLabels.Numeric(ObjectId.Text)))
        {
            // The acknowledged draft is no longer unsent; display the fresh readback.
            propertyBaseline = DraftSignature(CapturePropertyDraft());
            await LoadProperties(peer, ObjectLabels.Numeric(ObjectId.Text));
            PropertyGrid.SelectedItem = properties.FirstOrDefault(row => row.Property == property && row.Index == index);
        }
        Status.Text = $"{succeeded} write(s) acknowledged. Readback is recorded in Activity; effective values can differ due to command priority.";
    });

    private async Task AddWatch(Peer peer, string id, string property, uint? index)
    {
        var normalized = Decode<List<PropertyRow>>(await Engine.Call("normalize", new
        {
            selections = new[] { new { @object = id, property, index } }
        }))[0];
        var watch = new Watch { Device = peer.Key, Caption = peer.Caption, Object = normalized.Object, Property = normalized.Property, Index = normalized.Index };
        if (watches.Any(w => w.Identity == watch.Identity))
            return;
        if (watches.Count >= 64)
            throw new InvalidOperationException("The watch limit is 64.");
        watches.Add(watch);
        dirty = true;
    }
    private async void WatchObjects(object sender, RoutedEventArgs e) => await Run("Add watches", async () =>
    {
        foreach (var item in ObjectGrid.SelectedItems.Cast<ObjectRow>().ToList())
            await AddWatch(CurrentPeer, item.Object, "present-value", null);
        Status.Text = $"{watches.Count} watches configured.";
    });
    private async void WatchProperty(object sender, RoutedEventArgs e) => await Run("Add watch", async () => await AddWatch(CurrentPeer, ObjectLabels.Numeric(ObjectId.Text), PropertyId.Text.Trim(), IndexValue));
    private void WatchSelected(object sender, SelectionChangedEventArgs e)
    {
        Chart.Watch = WatchGrid.SelectedItem as Watch;
        Chart.InvalidateVisual();
    }
    private async void RemoveWatch(object sender, RoutedEventArgs e) => await Run("Remove watch", async () =>
    {
        if (WatchGrid.SelectedItem is not Watch watch)
            return;
        if (watch.Process != 0 && connected)
            await SetSubscription(watch, 0);
        watches.Remove(watch);
        dirty = true;
        Chart.Watch = null;
        Chart.InvalidateVisual();
    });
    private async void ToggleMonitor(object sender, RoutedEventArgs e) => await Run("Polling", () =>
    {
        if (!connected)
            throw new InvalidOperationException("Connect first.");
        _ = Integer(PollInterval, 1, 3600);
        polling = !polling;
        nextPoll = DateTimeOffset.MinValue;
        MonitorButton.Content = polling ? "Stop polling" : "Start polling";
        return Task.CompletedTask;
    });
    private async void PollNow(object sender, RoutedEventArgs e) => await Run("Poll watches", () => Poll());
    private bool MonitorCanContinue => connected && !busy && !closing && objectEditor?.IsBusy != true;

    private async Task Poll(bool background = false)
    {
        if (!connected)
            throw new InvalidOperationException("Connect first.");
        var sampled = false;
        // Freeze each batch across awaits. User actions may be queued while a read is pending.
        var groups = watches.GroupBy(w => w.Device).Select(group => group.ToArray()).ToArray();
        foreach (var group in groups)
        {
            if (background && !MonitorCanContinue)
                return;
            try
            {
                JsonElement result;
                try
                {
                    monitorReading = background;
                    result = await Engine.Call("read", new
                    {
                        device = group[0].Device,
                        selections = group.Select(w => new { @object = w.Object, property = w.Property, index = w.Index }).ToArray()
                    });
                }
                finally { monitorReading = false; }
                if (background && !MonitorCanContinue)
                    return;
                var rows = Rows(result);
                foreach (var watch in group)
                {
                    var row = rows.FirstOrDefault(r => r.Identity == watch.Identity);
                    watch.Observe(row ?? new PropertyRow { At = DateTimeOffset.UtcNow, Error = "No result returned for this watch." }, "Poll", properties);
                    sampled = true;
                }
            }
            catch (Exception ex)
            {
                if (engine is { IsAvailable: false })
                    throw;
                if (background && !MonitorCanContinue)
                    return;
                foreach (var watch in group)
                    watch.Observe(new PropertyRow { At = DateTimeOffset.UtcNow, Error = ex.Message }, "Poll");
                sampled = true;
                Log("Poll failed", group[0].Caption, ex.Message);
            }
        }
        if (sampled)
            Chart.InvalidateVisual();
        if (!background)
            Status.Text = $"Watch cycle complete · {watches.Count} watches · {DateTime.Now:HH:mm:ss}. Errors remain visible in Quality and Activity.";
    }

    private async void TimerTick(object? sender, EventArgs e) => await Monitor();

    private async Task Monitor()
    {
        if (connected && engine is { IsAvailable: false })
        {
            MarkEngineUnavailable("The BACnet engine connection was lost");
            return;
        }
        if (!MonitorCanContinue || monitorDone != null)
            return;
        var subscriptions = watches.Where(w => w.Process != 0).ToArray();
        // An interval can be temporarily empty while the operator types it.
        var interval = 0;
        var pollDue = polling && watches.Count > 0 && nextPoll <= DateTimeOffset.UtcNow
            && int.TryParse(PollInterval.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval)
            && interval is >= 1 and <= 3600;
        if (subscriptions.Length == 0 && !pollDue)
            return;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitorDone = completed;
        try
        {
            foreach (var watch in subscriptions.Where(w => w.RenewAt <= DateTimeOffset.UtcNow))
            {
                if (!MonitorCanContinue)
                    return;
                try
                {
                    await SetSubscription(watch, 120);
                }
                catch (Exception ex) { watch.Mark("COV renewal failed"); watch.RenewAt = DateTimeOffset.UtcNow.AddSeconds(15); Log("COV renewal", watch.Caption, ex.Message); }
            }
            if (!MonitorCanContinue)
                return;
            if (subscriptions.Length > 0)
            {
                var result = await Engine.Call("notifications", new
                {
                    after = notificationSequence
                });
                if (!MonitorCanContinue)
                    return;
                notificationSequence = result.GetProperty("sequence").GetUInt64();
                var drops = result.GetProperty("dropped").GetUInt64();
                if (drops != notificationDrops)
                {
                    Log("COV queue", "Connection", $"{drops} notifications dropped; reconcile with polling.");
                    notificationDrops = drops;
                }
                var sampled = false;
                foreach (var item in result.GetProperty("events").EnumerateArray())
                {
                    var process = item.GetProperty("process").GetUInt32();
                    foreach (var row in Decode<List<PropertyRow>>(item.GetProperty("rows")))
                        foreach (var watch in subscriptions.Where(w => w.Process == process && w.Identity == row.Identity))
                        {
                            watch.Observe(row, "COV", properties);
                            sampled = true;
                        }
                }
                if (sampled)
                    Chart.InvalidateVisual();
            }
            if (pollDue && MonitorCanContinue)
            {
                await Poll(true);
                // Schedule after completion, so slow peers cannot cause back-to-back catch-up polls.
                nextPoll = DateTimeOffset.UtcNow.AddSeconds(interval);
            }
        }
        catch (Exception ex)
        {
            // Background failures belong in Activity; do not reset the editor or its status.
            Log("Monitor", "Connection", ex.Message);
            if (engine is { IsAvailable: false })
                MarkEngineUnavailable(ex.Message);
        }
        finally
        {
            monitorDone = null;
            completed.TrySetResult();
        }
    }
    private async Task SetSubscription(Watch watch, uint lifetime)
    {
        var process = watch.Process == 0 ? nextProcess++ : watch.Process;
        await Engine.Call("subscribe", new
        {
            device = watch.Device,
            @object = watch.Object,
            process,
            lifetime
        });
        watch.Process = lifetime == 0 ? 0 : process;
        watch.RenewAt = DateTimeOffset.UtcNow.AddSeconds(45);
        watch.Mark(lifetime == 0 ? "COV canceled" : "COV subscribed");
    }
    private async void SubscribeCov(object sender, RoutedEventArgs e) => await Run("Subscribe COV", async () =>
    {
        if (WatchGrid.SelectedItem is not Watch watch)
            throw new InvalidOperationException("Select a watch first. Object COV delivers the properties supported by the peer.");
        await SetSubscription(watch, 120);
    });
    private async void CancelCov(object sender, RoutedEventArgs e) => await Run("Cancel COV", async () => { if (WatchGrid.SelectedItem is Watch watch && watch.Process != 0) await SetSubscription(watch, 0); });
    private async Task CancelAllSubscriptions()
    {
        foreach (var watch in watches.Where(w => w.Process != 0))
        {
            try
            {
                await SetSubscription(watch, 0);
            }
            catch (Exception ex) { Log("Cancel COV", watch.Caption, ex.Message + "; finite subscription expires on the peer."); watch.Process = 0; }
        }
    }

    private async Task<List<PropertyRow>> Capture()
    {
        var selected = CurrentPeer;
        var result = await LoadInventory(selected);
        return await Snapshot.Capture(Engine, selected.Key, result, () => cancelRequested);
    }
    private async void CaptureBaseline(object sender, RoutedEventArgs e) => await Run("Capture baseline", async () =>
    {
        baseline = await Capture();
        dirty = true;
        SnapshotSummary.Text = $"Baseline: {baseline.Count} observations · {baseline.Count(row => row.Error.Length > 0)} read errors (including unsupported properties) · {DateTime.Now:g}";
    });
    private async void CompareLive(object sender, RoutedEventArgs e) => await Run("Compare live", async () =>
    {
        if (baseline.Count == 0)
            throw new InvalidOperationException("Capture or load a baseline first.");
        var live = await Capture();
        differences = Snapshot.Compare(baseline, live);
        DifferenceGrid.ItemsSource = differences;
        SnapshotSummary.Text = $"{differences.Count} differences · {live.Count} live observations" +
            (Snapshot.HasPriorities(baseline) ? "" : " · Older baseline: priority observations were not captured.");
    });

    private async void ReadEvents(object sender, RoutedEventArgs e) => await Run("Read events", async () =>
    {
        var result = await Engine.Call("events", new
        {
            device = CurrentPeer.Key
        });
        EventGrid.ItemsSource = Decode<List<EventRow>>(result);
        Status.Text = $"{result.GetArrayLength()} active events returned.";
    });
    private async void ReadStatus(object sender, RoutedEventArgs e) => await Run("Connection counters", async () => Diagnostics.Text = JsonSerializer.Serialize(await Engine.Call("status"), new JsonSerializerOptions { WriteIndented = true }));
    private async void ReadRange(object sender, RoutedEventArgs e) => await Run("Read range", async () =>
    {
        var result = await Engine.Call("range", new
        {
            device = CurrentPeer.Key,
            @object = ObjectLabels.Numeric(ObjectId.Text),
            property = PropertyId.Text.Trim(),
            index = IndexValue,
            start = (ulong)Integer(RangeStart, 1, int.MaxValue),
            count = Integer(RangeCount, 1, 256)
        });
        Diagnostics.Text = Pretty(result);
    });

    private void Log(string operation, string target, string result)
    {
        activity.Insert(0, new ActivityRow(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), operation, target, result));
        if (activity.Count > 1000)
            activity.RemoveAt(activity.Count - 1);
    }
    private void FilterDevices(object sender, TextChangedEventArgs e)
    {
        if (DeviceList?.ItemsSource == null)
            return;
        var query = DeviceFilter.Text.Trim();
        CollectionViewSource.GetDefaultView(treeRoots).Filter = item => item is Peer peer && ($"{peer.Caption} {peer.Address} {peer.Vendor} {peer.Route}".Contains(query, StringComparison.OrdinalIgnoreCase) || peer.Children.OfType<Peer>().Any(c => $"{c.Caption} {c.Address} {c.Route}".Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
    private void FilterObjects(object sender, TextChangedEventArgs e) => ApplyObjectFilter();
    private void ApplyObjectFilter()
    {
        if (ObjectGrid?.ItemsSource == null)
            return;
        var query = ObjectFilter.Text.Trim();
        var type = (ObjectTypeFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        CollectionViewSource.GetDefaultView(objects).Filter = item => item is ObjectRow row && row.Kind.Contains(type, StringComparison.OrdinalIgnoreCase) && $"{row.ObjectLabel} {row.Object} {row.Name} {row.Kind} {row.Value}".Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    private void RefreshObjectSearch()
    {
        // WPF does not re-evaluate a collection filter when a row's name/value changes.
        if (!string.IsNullOrWhiteSpace(ObjectFilter.Text))
            CollectionViewSource.GetDefaultView(objects).Refresh();
    }
    private void FilterObjectType(object sender, SelectionChangedEventArgs e) => ApplyObjectFilter();

    private static string ReadBounded(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("File exceeds 16 MiB.");
        return File.ReadAllText(path);
    }
    private static void SaveAtomic(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var data = Encoding.UTF8.GetBytes(content);
                file.Write(data);
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string? SavePath(string name, string filter)
    {
        var dialog = new SaveFileDialog { FileName = name, Filter = filter, AddExtension = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private bool DiscardChanges() => !dirty || MessageBox.Show(this, "The workspace has unsaved configuration, watches or a baseline. Discard these changes?", "Unsaved workspace", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private async void SaveWorkspace(object sender, RoutedEventArgs e) => await Run("Save workspace", () =>
    {
        var path = SavePath(workspacePath ?? "workspace.bacnet.json", "BACnet workspace|*.bacnet.json");
        if (path == null)
            return Task.CompletedTask;
        var workspace = new Workspace { Connection = Profile(), Watches = watches.Select(w => new Watch { Device = w.Device, Caption = w.Caption, Object = w.Object, Property = w.Property, Index = w.Index }).ToList(), Baseline = baseline };
        workspace.Connection.ConfirmJoin = false;
        SaveAtomic(path, WorkbenchExport.SaveDocument(workspace, workspace.Baseline));
        workspacePath = path;
        dirty = false;
        Status.Text = "Workspace saved · " + path;
        return Task.CompletedTask;
    });
    private async void OpenWorkspace(object sender, RoutedEventArgs e) => await Run("Open workspace", async () =>
    {
        if (connected)
            throw new InvalidOperationException("Disconnect before loading a workspace.");
        if (!DiscardChanges())
            return;
        var dialog = new OpenFileDialog { Filter = "BACnet workspace|*.bacnet.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        var workspace = JsonSerializer.Deserialize<Workspace>(ReadBounded(dialog.FileName), EngineClient.Json) ?? throw new InvalidDataException("Invalid workspace.");
        if (workspace.Version != 1 || workspace.Watches == null || workspace.Connection == null || workspace.Baseline == null || workspace.Watches.Count > 64 || workspace.Baseline.Count > Snapshot.MaximumObservations)
            throw new InvalidDataException("Workspace version or limits are invalid.");
        if (workspace.Watches.Select(w => w.Identity).Distinct().Count() != workspace.Watches.Count || workspace.Baseline.Select(r => r.Identity).Distinct().Count() != workspace.Baseline.Count)
            throw new InvalidDataException("Workspace contains duplicate selectors.");
        var normalized = Decode<List<PropertyRow>>(await Engine.Call("normalize", new
        {
            selections = workspace.Watches.Select(w => new { @object = w.Object, property = w.Property, index = w.Index }).ToArray()
        }));
        for (var i = 0; i < workspace.Watches.Count; i++)
        {
            workspace.Watches[i].Object = normalized[i].Object;
            workspace.Watches[i].Property = normalized[i].Property;
            workspace.Watches[i].Index = normalized[i].Index;
        }
        ApplyProfile(workspace.Connection);
        watches.Clear();
        foreach (var watch in workspace.Watches)
        {
            watch.Process = 0;
            watch.Samples.Clear();
            watch.Mark("Disconnected");
            watches.Add(watch);
        }
        baseline = workspace.Baseline;
        SnapshotSummary.Text = $"Loaded baseline: {baseline.Count} observations";
        workspacePath = dialog.FileName;
        dirty = false;
        Status.Text = "Workspace loaded offline. Connect and rediscover to refresh bindings.";
    });
    private async void SaveBaseline(object sender, RoutedEventArgs e) => await Run("Save baseline", () => { var path = SavePath("baseline.json", "JSON|*.json"); if (path != null) SaveAtomic(path, WorkbenchExport.SaveDocument(baseline, baseline)); return Task.CompletedTask; });
    private async void LoadBaseline(object sender, RoutedEventArgs e) => await Run("Load baseline", () =>
    {
        var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) != true)
            return Task.CompletedTask;
        var rows = JsonSerializer.Deserialize<List<PropertyRow>>(ReadBounded(dialog.FileName), EngineClient.Json) ?? throw new InvalidDataException("Invalid baseline.");
        Snapshot.Validate(rows);
        baseline = rows;
        dirty = true;
        SnapshotSummary.Text = $"Loaded {baseline.Count} observations";
        return Task.CompletedTask;
    });
    private static string CsvCell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@') || text.StartsWith('\t') || text.StartsWith('\r'))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
    private static void ExportCsv(string name, IEnumerable<IEnumerable<object?>> rows)
    {
        var path = SavePath(name, "CSV|*.csv");
        if (path == null)
            return;
        SaveAtomic(path, string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(CsvCell)))) + "\r\n");
    }
    private async void ExportProperties(object sender, RoutedEventArgs e) => await Run("Export properties", () => { ExportCsv("properties.csv", new[] { new object?[] { "Device", "Object", "Property", "Index", "Value", "Type", "Error", "Raw", "Observed UTC", "Readable value", "Readable error", "Object description", "Property name" } }.Concat(properties.Select(r => new object?[] { r.Device, r.Object, r.Property, r.Index, r.Value, r.Type, r.Error, r.Raw, r.At.ToString("O"), r.DisplayValue, r.DisplayError, BacnetDisplay.ObjectReference(r.Object), r.DisplayName }))); return Task.CompletedTask; });
    private async void ExportActivity(object sender, RoutedEventArgs e) => await Run("Export activity", () => { ExportCsv("activity.csv", new[] { new object?[] { "Time", "Operation", "Target", "Result", "Readable result" } }.Concat(activity.Select(r => new object?[] { r.Time, r.Operation, r.Target, r.Result, r.DisplayResult }))); return Task.CompletedTask; });
    private async void ExportDifferences(object sender, RoutedEventArgs e) => await Run("Export differences", () => { ExportCsv("differences.csv", new[] { new object?[] { "Object", "Property", "Baseline", "Live", "Change", "Readable baseline", "Readable live", "Object description", "Property name" } }.Concat(differences.Select(r => new object?[] { r.Object, r.Property, r.RawBefore, r.RawAfter, r.Change, r.Before, r.After, BacnetDisplay.ObjectReference(r.Object), WorkbenchExport.Selector(r.Property) }))); return Task.CompletedTask; });
    private async void ExportHistory(object sender, RoutedEventArgs e) => await Run("Export history", () => { ExportCsv("watch-history.csv", new[] { new object?[] { "Device", "Object", "Property", "Index", "UTC", "Value", "Error", "Source", "Readable value", "Readable error", "Object description", "Property name" } }.Concat(watches.SelectMany(w => w.Samples.Select(s => new object?[] { w.Device, w.Object, w.Property, w.Index, s.At.ToString("O"), s.Value, s.Error, s.Source, s.DisplayValue, s.DisplayError, BacnetDisplay.ObjectReference(w.Object), BacnetDisplay.PropertyName(w.Property) })))); return Task.CompletedTask; });

    private bool shutdownComplete;
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete)
            return;
        e.Cancel = true;
        if (closing)
            return;
        if (objectEditor?.IsBusy == true)
        {
            MessageBox.Show(this, "Cancel the active editor operation before closing.", "Operation active");
            return;
        }
        if (!CanLeaveInspector())
            return;
        if (!DiscardChanges())
            return;
        closing = true;
        timer.Stop();
        Task? subscriptionCleanup = null;
        try
        {
            if (busy)
                await StopActiveOperation();
            if (monitorDone != null)
            {
                if (engine is { IsAvailable: true })
                    await engine.Call("cancel");
                await monitorDone.Task;
            }
            if (engine is { IsAvailable: true } && connected)
            {
                subscriptionCleanup = CancelAllSubscriptions();
                await subscriptionCleanup.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex) { Log("Shutdown", "Engine", ex.Message); }
        finally
        {
            try
            {
                if (engine != null)
                    await engine.DisposeAsync();
            }
            catch (Exception ex) { Log("Shutdown", "Engine", ex.Message); }
            finally
            {
                // Disposal settles a timed-out unsubscribe and later cleanup
                // requests locally. Never leave work using a closed window.
                try
                {
                    if (subscriptionCleanup != null)
                        await subscriptionCleanup;
                }
                catch (Exception ex) { Log("Shutdown", "Subscriptions", ex.Message); }
                finally { shutdownComplete = true; Close(); }
            }
        }
    }
}
