using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace BACnetExplorer.CrossPlatform;

public sealed partial class WorkbenchWindow : Window
{
    private EngineClient? engine;
    private bool connected, busy, discovering, closing, closed, polling, monitorReading;
    private bool restoringSelection;
    private Task? active, monitoring;
    private Peer? peer;
    private string objectId = "", propertyDraft = "";
    private PropertyRow? property;
    private readonly ObservableCollection<Peer> peers = [];
    private readonly ObservableCollection<ObjectRow> objects = [];
    private readonly ObservableCollection<PropertyRow> properties = [];
    private readonly ObservableCollection<Watch> watches = [];
    private readonly ObservableCollection<ActivityRow> activity = [];
    private readonly ObservableCollection<EventRow> events = [];
    private readonly List<PropertyRow> baseline = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset nextPoll;
    private ulong sequence;
    private uint processId = 1;
    private readonly StackPanel connectionPanel = new() { Spacing = 8 };
    private readonly Expander connectionSettings = new() { Header = "Connection settings", IsExpanded = true };
    private readonly ListBox devices = new();
    private readonly DataGrid objectGrid = Table(("Object", "ObjectLabel"), ("Name", "Name"), ("Value", "DisplayValue"));
    private readonly DataGrid propertyGrid = Table(("Property", "DisplayName"), ("Value", "DisplayValue"), ("Type", "DisplayType"), ("Access", "AccessStatus"), ("Error", "Error"));
    private readonly DataGrid watchGrid = Table(("Point", "Caption"), ("Value", "Value"), ("Quality", "Quality"), ("Updated", "Updated"));
    private readonly DataGrid eventGrid = Table(("Object", "ObjectLabel"), ("State", "State"), ("Acknowledged", "Acknowledged"), ("Priority", "PriorityDisplay"));
    private readonly TextBlock status = new() { Text = "Offline · choose a connection and Connect.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock selection = new() { Text = "Select a discovered device", FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly TextBox target = Input("", "IPv4 address or address:port; empty broadcasts");
    private readonly TextBox seconds = Input("30", "Discovery seconds (1–120)");
    private readonly TextBox remoteNetwork = Input("65535", "Remote network; 0 is local");
    private readonly TextBox low = Input("", "Low device instance (optional)");
    private readonly TextBox high = Input("", "High device instance (optional)");
    private readonly TextBox objectFilter = Input("", "Search object names, types, IDs or values");
    private readonly TextBox value = Input("", "Select a property to edit");
    private readonly ComboBox choices = new() { MinWidth = 220 };
    private readonly TextBox priority = Input("0", "Priority: 0 or 1–16, excluding 6");
    private readonly TextBlock valueHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox diagnostics = JsonBox("{}");
    private readonly TextBox output = JsonBox("{}");
    private readonly TextBox pollSeconds = Input("5", "Poll interval (1–3600 seconds)");
    private readonly TrendView chart = new();
    private readonly Button connectButton = new() { Content = "Connect" };
    private readonly Button disconnectButton = new() { Content = "Disconnect", IsEnabled = false };
    private readonly TextBlock platformNotice = new() { Text = "macOS preview · not runtime-tested on a Mac · unsigned and not notarized",
        Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap };
    private static readonly JsonSerializerOptions HumanJson = new(EngineClient.Json) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
    };

    public WorkbenchWindow() : this(null, true) { }

    internal WorkbenchWindow(EngineClient? injectedEngine, bool initialize)
    {
        engine = injectedEngine;
        Title = "Go BACnet Workbench 0.2.20 · Cross-platform preview";
        Width = 1530; Height = 920; MinWidth = 1120; MinHeight = 760;
        FontSize = 13; Foreground = Brush.Parse("#20334D");
        FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");
        Background = Brush.Parse("#F1F5FA");
        devices.ItemsSource = peers;
        devices.ItemTemplate = new FuncDataTemplate<Peer>((item, _) => new StackPanel { Spacing = 5, Margin = new Thickness(2,7), Children = {
            new TextBlock { Text = item?.Caption ?? "", FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = item?.Detail ?? "", FontSize = 11, Foreground = Brush.Parse("#65788F") },
            new TextBlock { Text = item?.Warning ?? "", FontSize = 11, Foreground = Brush.Parse("#65788F"), TextWrapping = TextWrapping.Wrap }
        } });
        objectGrid.ItemsSource = objects; propertyGrid.ItemsSource = properties;
        watchGrid.ItemsSource = watches; eventGrid.ItemsSource = events;
        var tabs = new TabControl { Items = {
            new TabItem { Header = "Objects & properties", Content = Inspector() },
            new TabItem { Header = "Watch & trends", Content = Watches() },
            new TabItem { Header = "Schedule & calendar", Content = StructuredEditors() },
            new TabItem { Header = "Alarms & history", Content = Alarms() },
            new TabItem { Header = "Tools & diagnostics", Content = Tools() },
            new TabItem { Header = "Activity", Content = Activity() },
        } };
        var heading = new DockPanel { Margin = new Thickness(3,0,3,9) };
        var workspaceButtons = Row(
            Button("Open workspace", OpenWorkspace), Button("Save workspace", SaveWorkspace));
        DockPanel.SetDock(workspaceButtons, Dock.Right);
        heading.Children.Add(workspaceButtons);
        using var logoStream = AssetLoader.Open(new Uri("avares://GoBACnetWorkbench/Assets/Logo.png"));
        heading.Children.Add(new Image { Source = new Bitmap(logoStream), Width = 220, Height = 86,
            HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform });
        connectButton.Classes.Add("primary");
        var controls = Row(new TextBlock { Text = "CONNECTION", VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold, Margin = new Thickness(0,0,12,0) }, transport,
            connectButton, disconnectButton, Button("Cancel operation", Cancel));
        connectButton.Click += async (_, _) => await Run("Connect", Connect);
        disconnectButton.Click += async (_, _) => { await InterruptRead(); await Run("Disconnect", Disconnect); };
        target.Width = 215; seconds.Width = 65; remoteNetwork.Width = 85;
        low.Width = high.Width = 105;
        connectionPanel.Children.Add(ConnectionFields());
        connectionSettings.Content = connectionPanel;
        var top = new StackPanel { Spacing = 8, Children = { heading,
            new Border { Background = Brush.Parse("#FFF6E5"), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10,6), Child = platformNotice },
            Card(new StackPanel { Spacing = 6, Children = { controls, connectionSettings } }, 12)
        } };
        DockPanel.SetDock(top, Dock.Top);
        var bottom = new Border { Padding = new Thickness(10), Background = Brush.Parse("#E5EEF6"), Child = status };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("270,12,*"), Margin = new Thickness(0,12,0,8) };
        var discovery = new StackPanel { Spacing = 8, Children = {
            new TextBlock { Text = "DEVICES", FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#536B86") },
            Label("Directed target", target),
            Row(Label("Duration (s)", seconds), Label("Network", remoteNetwork)),
            new Expander { Header = "Device instance limits", Content = Row(low, high) },
            Button("Discover devices", StartDiscovery)
        } };
        var sidebar = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(discovery, Dock.Top); sidebar.Children.Add(discovery); sidebar.Children.Add(devices);
        body.Children.Add(Card(sidebar, 12));
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Width = 6 };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        var workspaceCard = Card(tabs, 0); Grid.SetColumn(workspaceCard, 2); body.Children.Add(workspaceCard);
        Content = new DockPanel { Margin = new Thickness(12), Children = { top, bottom, body } };
        devices.SelectionChanged += async (_, _) => await SelectDevice();
        objectGrid.SelectionChanged += async (_, _) => await SelectObject();
        propertyGrid.SelectionChanged += async (_, _) => await SelectProperty();
        objectFilter.TextChanged += (_, _) => FilterObjects();
        choices.SelectionChanged += (_, _) => {
            if (choices.SelectedItem is PropertyChoice choice) value.Text = choice.Value;
        };
        choices.ItemTemplate = new FuncDataTemplate<PropertyChoice>((item, _) => new TextBlock { Text = item?.Label ?? "" });
        watchGrid.SelectionChanged += (_, _) => { chart.Watch = watchGrid.SelectedItem as Watch; chart.InvalidateVisual(); };
        timer.Tick += async (_, _) => await Monitor();
        Closing += async (_, args) => {
            if (closed) return;
            args.Cancel = true;
            if (closing || !await AdmitDraft()) return;
            if (!await Dialogs.Confirm(this, "Close Workbench", "Close the app and stop its connection? Save any workspace you want to keep before closing. An interrupted write may already have taken effect.")) return;
            await Shutdown();
            Close();
        };
        if (initialize) Opened += async (_, _) => await Run("Initialize", Initialize);
    }

    private Control Inspector()
    {
        var buttons = Row(Button("Refresh inventory", () => ReadInventory(true)),
            Button("Refresh properties", ReadProperties), Button("Watch property", AddWatch),
            Button("Capture baseline", CaptureBaseline), Button("Compare", CompareBaseline),
            Button("Export properties", () => ExportJson("properties.json", properties)));
        var edit = new StackPanel { Spacing = 8, Children = {
            new TextBlock { Text = "Property editor", FontSize = 18, FontWeight = FontWeight.SemiBold },
            choices, value, Label("Command priority", priority), valueHint,
            Row(Button("Save change", () => WriteProperty(false)), Button("Relinquish", () => WriteProperty(true))),
            Button("Inspect priority array", ReadPriority),
            new TextBlock { Text = "The device validates all writes. Reserved priority 6 is never offered.", TextWrapping = TextWrapping.Wrap },
        } };
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,310") };
        var left = new Grid { RowDefinitions = new RowDefinitions("2*,8,3*") };
        left.Children.Add(objectGrid); Grid.SetRow(propertyGrid, 2); left.Children.Add(propertyGrid);
        split.Children.Add(left); Grid.SetColumn(edit, 2); split.Children.Add(new ScrollViewer { Content = edit });
        Grid.SetColumn(split.Children[^1], 2);
        return Page(selection, buttons, objectFilter, split);
    }

    private Control Watches() => Page(Row(
        Button("Poll once", Poll), Button("Start / stop polling", () => { polling = !polling; nextPoll = DateTimeOffset.MinValue; return Task.CompletedTask; }),
        Label("Seconds", pollSeconds), Button("Subscribe COV", () => Subscription(120)),
        Button("Cancel COV", () => Subscription(0)), Button("Remove", RemoveWatch),
        Button("Export samples", ExportSamples)), watchGrid, chart);

    private Control Activity()
    {
        var grid = Table(("Time", "Time"), ("Operation", "Operation"), ("Target", "Target"), ("Result", "Result"));
        grid.ItemsSource = activity;
        return Page(Button("Export activity", () => ExportJson("activity.json", activity)), grid);
    }

    private static Control Page(params Control[] controls)
    {
        var panel = new Grid { Margin = new Thickness(10), RowDefinitions = new RowDefinitions(
            string.Join(',', controls.Select((_, i) => i == controls.Length - 1 ? "*" : "Auto"))) };
        for (var i = 0; i < controls.Length; i++) {
            Grid.SetRow(controls[i], i); controls[i].Margin = new Thickness(0,0,0,8); panel.Children.Add(controls[i]);
        }
        return panel;
    }

    private static Border Card(Control content, int padding) => new() {
        Background = Brushes.White, BorderBrush = Brush.Parse("#DDE5EE"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(padding), Child = content
    };

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        if (text is "Save change" or "Save schedule" or "Save calendar") button.Classes.Add("primary");
        button.Click += async (_, _) => {
            if (action == Cancel || action == StartDiscovery) { await action(); return; }
            await Run(text, action);
        };
        return button;
    }

    private static WrapPanel Row(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) { control.Margin = new Thickness(0,0,8,6); panel.Children.Add(control); }
        return panel;
    }
    private static StackPanel Label(string label, Control field) => new() { Spacing = 4, Children = {
        new TextBlock { Text = label, FontSize = 11 }, field
    } };
    private static TextBox Input(string text, string hint) => new() { Text = text, PlaceholderText = hint, MinWidth = 90 };
    private static TextBox JsonBox(string text) => new() { Text = text, AcceptsReturn = true, AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("monospace"), MinHeight = 160 };
    private static DataGrid Table(params (string Header, string Path)[] fields)
    {
        var table = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, MinHeight = 100,
            SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        foreach (var field in fields) table.Columns.Add(new DataGridTextColumn {
            Header = field.Header, Binding = new Binding(field.Path), Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        return table;
    }
    private static string Pretty(object data) => JsonSerializer.Serialize(data, HumanJson);
    private static T Decode<T>(JsonElement json) => json.Deserialize<T>(EngineClient.Json) ?? throw new InvalidDataException("Empty engine response.");
    private static List<PropertyRow> Rows(JsonElement json) => json.ValueKind == JsonValueKind.Array
        ? Decode<List<PropertyRow>>(json)
        : json.TryGetProperty("rows", out var rows) ? Decode<List<PropertyRow>>(rows) : [];
    private static int Number(TextBox field, int min, int max)
    {
        if (!int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            throw new InvalidDataException($"Enter a number from {min} to {max}.");
        return number;
    }
    private EngineClient Engine => engine ?? throw new InvalidOperationException("The bundled engine is unavailable.");
    private Peer CurrentPeer => connected && peer != null ? peer : throw new InvalidOperationException("Connect and select a device first.");
    private void Log(string operation, string detail)
    {
        activity.Insert(0, new ActivityRow(DateTime.Now.ToString("HH:mm:ss"), operation, peer?.Caption ?? "Desktop", detail));
        if (activity.Count > 2000) activity.RemoveAt(activity.Count - 1);
    }

    private async Task Run(string name, Func<Task> action)
    {
        if (closing) return;
        if (busy) { status.Text = "An operation is active. Use Cancel operation to stop it."; return; }
        busy = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        active = done.Task;
        UpdateControls(); status.Text = name + "…";
        try {
            if (monitoring is { } monitor) {
                if (monitorReading) await Engine.Call("cancel");
                await monitor;
            }
            await action();
            Log(name, "Completed");
        }
        catch (Exception error) {
            status.Text = name + ": " + error.Message;
            Log(name, error.Message);
            if (engine is not { IsAvailable: true }) connected = polling = false;
            if (!closing && error is not OperationCanceledException && !canceling)
                await Dialogs.Error(this, name, error);
        }
        finally { busy = false; UpdateControls(); done.TrySetResult(); }
    }

    private bool canceling;
    private async Task Cancel()
    {
        canceling = true;
        try {
            var settling = active;
            await Engine.Call("cancel");
            if (settling != null) await settling;
            status.Text = $"Operation stopped · {peers.Count} discovered devices retained. Read back interrupted writes before retrying.";
        }
        catch (Exception error) { connected = polling = false; status.Text = error.Message + " · Connect to recover."; }
        finally { canceling = false; UpdateControls(); }
    }

    private async Task InterruptRead()
    {
        if (discovering) await Cancel();
    }

    private void UpdateControls()
    {
        connectButton.IsEnabled = !connected && !busy && !closing;
        disconnectButton.IsEnabled = connected && (!busy || discovering) && !closing;
        connectionPanel.IsEnabled = !connected && !busy;
        transport.IsEnabled = !connected && !busy;
        if (connected) connectionSettings.IsExpanded = false;
        devices.IsEnabled = !busy || discovering;
        objectGrid.IsEnabled = propertyGrid.IsEnabled = !busy;
    }

    private async Task Initialize()
    {
        engine ??= new EngineClient();
        var system = await Engine.Call("system");
        if (system.TryGetProperty("interfaceProfiles", out var interfaces) && interfaces.GetArrayLength() > 0) {
            var selected = interfaces.EnumerateArray().FirstOrDefault();
            ApplyProfile(new ConnectionProfile {
                Bind = selected.GetProperty("bind").GetString()!, Broadcast = selected.GetProperty("broadcast").GetString()!
            });
        }
        status.Text = "Ready · Offline. Choose the connection profile, then Connect.";
    }

    private async Task Connect()
    {
        if (!await AdmitDraft()) return;
        var profile = SelectedProfile();
        if (profile.Transport == "mstp" && !await Dialogs.Confirm(this, "Join MS/TP trunk",
            $"Open {profile.Serial} at {profile.Baud} baud as manager MAC {profile.Mac}? Confirm the baud, network and unused MAC before joining.")) return;
        profile.ConfirmJoin = profile.Transport == "mstp";
        if (engine is not { IsAvailable: true }) {
            if (engine != null) await engine.DisposeAsync();
            engine = new EngineClient();
        }
        await Engine.Call("connect", profile);
        connected = true; sequence = 0; peers.Clear(); objects.Clear(); properties.Clear();
        peer = null; property = null; objectId = ""; propertyDraft = ""; value.Text = "";
        schedule = null; calendar = null; structuredBaseline = "";
        timer.Start(); await Discover();
    }

    private async Task Disconnect()
    {
        if (!await AdmitDraft()) return;
        await Engine.Call("disconnect");
        connected = polling = false; timer.Stop();
        foreach (var watch in watches) { watch.Process = 0; watch.Mark("Disconnected"); }
        status.Text = "Disconnected · observations and drafts remain available.";
    }

    private async Task StartDiscovery()
    {
        await InterruptRead();
        await Run("Discover", Discover);
    }

    private async Task Discover()
    {
        if (!connected) throw new InvalidOperationException("Connect first.");
        uint? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : (uint)Number(box, 0, 4194302);
        var directed = target.Text?.Trim() ?? "";
        discovering = true; UpdateControls();
        try {
            var result = await Engine.Call("discover", new { target = directed,
                seconds = Number(seconds, 1, 120), low = Optional(low), high = Optional(high),
                remoteNetwork = directed.Length > 0 ? 0 : Number(remoteNetwork, 0, 65535)
            }, json => Dispatcher.UIThread.InvokeAsync(() => MergePeers(json)).GetTask());
            MergePeers(result); status.Text = $"Discovery complete · {peers.Count} devices.";
        }
        finally { discovering = false; UpdateControls(); }
    }

    private void MergePeers(JsonElement result)
    {
        foreach (var incoming in Decode<List<Peer>>(result)) {
            var existing = peers.FirstOrDefault(item => item.Key == incoming.Key);
            if (existing != null) existing.UpdateRoute(incoming); else peers.Add(incoming);
        }
        status.Text = $"Discovering · {peers.Count} devices · Cancel stops the scan and keeps results.";
    }

    private async Task SelectDevice()
    {
        if (restoringSelection || devices.SelectedItem is not Peer selected) return;
        await InterruptRead();
        await Run("Read device", async () => {
            if (!await AdmitDraft()) { RestoreSelection(); return; }
            peer = selected; objectId = selected.Device; property = null;
            propertyDraft = ""; value.Text = "";
            selection.Text = selected.Caption + " · " + selected.Address;
            await ReadInventory(true); await ReadProperties();
        });
    }
    private void RestoreSelection()
    {
        restoringSelection = true;
        try { devices.SelectedItem = peer; }
        finally { restoringSelection = false; }
    }

    private readonly List<ObjectRow> allObjects = [];
    private async Task ReadInventory(bool enrich)
    {
        var selected = CurrentPeer;
        var result = await Engine.Call(enrich ? "inventory" : "object-list", new { device = selected.Key },
            json => Dispatcher.UIThread.InvokeAsync(() => ApplyInventory(json, selected)).GetTask());
        ApplyInventory(result, selected);
    }
    private void ApplyInventory(JsonElement result, Peer owner)
    {
        var list = Decode<List<ObjectRow>>(result.GetProperty("objects"));
        foreach (var row in list) row.Owner = owner;
        if (result.TryGetProperty("enrichment", out var enriched)) {
            var observations = Rows(enriched).GroupBy(row => row.Object).ToDictionary(group => group.Key, group => group.ToList());
            foreach (var row in list) if (observations.TryGetValue(row.Object, out var fields)) {
                row.Name = fields.FirstOrDefault(item => item.Property == "object-name")?.DisplayValue ?? row.Name;
                row.Value = fields.FirstOrDefault(item => item.Property == "present-value")?.Result ?? row.Value;
            }
        }
        allObjects.Clear(); allObjects.AddRange(list); FilterObjects();
    }
    private void FilterObjects()
    {
        var filter = objectFilter.Text ?? "";
        var filtered = allObjects.Where(item => (item.Caption + " " + item.Value).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        objects.Clear(); foreach (var item in filtered) objects.Add(item);
    }
    private async Task SelectObject()
    {
        if (objectGrid.SelectedItem is not ObjectRow selected || busy) return;
        await Run("Read object", async () => {
            if (!await AdmitDraft()) return;
            objectId = selected.Object; property = null; propertyDraft = ""; value.Text = "";
            await ReadProperties();
        });
    }
    private async Task ReadProperties()
    {
        var result = await Engine.Call("properties", new { device = CurrentPeer.Key, @object = objectId });
        var rows = Rows(result); PropertyEditPolicy.Apply(rows);
        // Retain an unsent property draft when refreshing observations.
        var identity = property?.Identity;
        properties.Clear(); foreach (var row in rows) properties.Add(row);
        property = rows.FirstOrDefault(row => row.Identity == identity);
        restoringSelection = true;
        try { propertyGrid.SelectedItem = property; }
        finally { restoringSelection = false; }
        status.Text = $"Read {ObjectLabels.Display(objectId)} · {properties.Count} properties.";
    }
    private async Task SelectProperty()
    {
        if (restoringSelection || propertyGrid.SelectedItem is not PropertyRow selected) return;
        if (!await AdmitDraft()) {
            restoringSelection = true; propertyGrid.SelectedItem = property; restoringSelection = false; return;
        }
        property = selected;
        choices.ItemsSource = PropertyValueEditor.Choices(selected, properties);
        value.Text = selected.ArrayValues.Length > 0 ? string.Join('\n', selected.ArrayValues) : PropertyValueEditor.EditText(selected);
        propertyDraft = value.Text ?? "";
        valueHint.Text = selected.AccessStatus + " · " + selected.AccessReason + "\n" + PropertyValueEditor.Hint(selected);
    }
    private async Task<bool> AdmitDraft()
    {
        if (property != null && value.Text != propertyDraft && !await Dialogs.Confirm(this, "Unsaved property",
            "Discard the unsent property value and continue?")) return false;
        return await AdmitStructuredDraft();
    }

    private async Task WriteProperty(bool relinquish)
    {
        var selectedPeer = CurrentPeer;
        var selected = property ?? throw new InvalidOperationException("Select and read a property first.");
        var commandPriority = Number(priority, 0, 16);
        var context = Rows(await Engine.Call("properties", new { device = selectedPeer.Key, @object = selected.Object }));
        var refreshed = context.FirstOrDefault(row => row.Identity == selected.Identity)
            ?? throw new InvalidOperationException("The selected property was not returned by the device.");
        PropertyEditPolicy.RequireWrite(refreshed, context, commandPriority, false, relinquish);
        if (refreshed.Raw != selected.Raw) throw new InvalidOperationException("The property changed. Refresh and review before saving.");
        var wireType = relinquish ? "null" : PropertyValueEditor.WireType(selected);
        var wireValue = relinquish ? "" : PropertyValueEditor.WireValue(selected, value.Text ?? "", arrayValues:
            selected.ArrayValues.Length > 0 ? (value.Text ?? "").Split('\n') : null);
        var result = await Engine.Call("write", new { device = selectedPeer.Key, @object = selected.Object,
            property = selected.Property, index = selected.Index, valueType = wireType, value = wireValue, priority = commandPriority, confirmed = true });
        ApplyIdentity(result, selectedPeer);
        propertyDraft = value.Text ?? "";
        await ReadProperties();
        status.Text = "Write acknowledged; current observations refreshed. A higher priority may control the effective value.";
    }
    private void ApplyIdentity(JsonElement result, Peer selected)
    {
        if (!result.TryGetProperty("identity", out var identity)) return;
        var oldKey = identity.GetProperty("oldKey").GetString();
        var oldObject = identity.GetProperty("oldObject").GetString();
        var replacement = Decode<Peer>(identity.GetProperty("peer"));
        var newObject = identity.GetProperty("newObject").GetString()!;
        selected.Key = replacement.Key; selected.Device = replacement.Device; selected.Instance = replacement.Instance; selected.RefreshIdentity();
        foreach (var item in allObjects.Where(item => item.Object == oldObject)) item.Object = newObject;
        foreach (var watch in watches.Where(item => item.Device == oldKey))
            watch.Rebind(selected.Key, watch.Object == oldObject ? newObject : watch.Object, selected.Caption);
        if (objectId == oldObject) objectId = newObject;
    }
    private async Task ReadPriority()
    {
        var rows = Rows(await Engine.Call("read", new { device = CurrentPeer.Key,
            selections = new[] { new { @object = objectId, property = "priority-array" }, new { @object = objectId, property = "relinquish-default" } } }));
        output.Text = Pretty(rows); status.Text = "Priority observations are available in Tools & diagnostics.";
    }

    internal async Task Shutdown()
    {
        closing = true; canceling = true; timer.Stop();
        Task? cleanup = null;
        try {
            if (engine is { IsAvailable: true }) await engine.Call("cancel");
            if (active != null) await active;
            if (monitoring != null) await monitoring;
            if (engine is { IsAvailable: true } && connected) {
                cleanup = CancelSubscriptions(); await cleanup.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception error) { Log("Shutdown", error.Message); }
        finally {
            try { if (engine != null) await engine.DisposeAsync(); }
            finally { if (cleanup != null) try { await cleanup; } catch (Exception) { }
                closed = true; connected = false; }
        }
    }
}
