using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BACnetExplorer;

internal static class MonitorChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, arguments);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application();
            app.Resources["Panel"] = System.Windows.Media.Brushes.White;
            app.Resources["Ink"] = System.Windows.Media.Brushes.Black;
            app.Resources["Muted"] = System.Windows.Media.Brushes.Gray;
            app.Resources[typeof(Button)] = new Style(typeof(Button));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try { OperationAlertChecks.Run(); await CheckMonitoring(); CheckRefreshDraft(); await CheckEngineLoss(); await CheckSingleWrite(); await InventoryChecks.Run(); await DiscoveryChecks.Run(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Monitor checks did not finish.");
        if (failure != null) throw new InvalidOperationException("Monitor regression failed.", failure);
        Console.WriteLine("PASS: idle/COV/poll monitoring preserves editor controls, drafts and selection; foreground work yields, failures recover, and polls do not overlap.");
    }

    private static async Task CheckMonitoring()
    {
        // Construct the real controls without showing a window or firing Loaded/network initialization.
        var window = new MainWindow();
        Set(window, "connected", true);
        Set(window, "polling", true);
        Set(window, "nextPoll", DateTimeOffset.UtcNow.AddMinutes(10));
        var controls = new[] { "DeviceList", "ObjectGrid", "PropertyGrid", "PropertyEditor", "PriorityGrid", "PriorityActions" }
            .Select(name => (UIElement)window.FindName(name)).ToArray();
        var enableChanges = 0;
        foreach (var control in controls) control.IsEnabledChanged += (_, _) => enableChanges++;
        for (var tick = 0; tick < 20; tick++) Invoke(window, "TimerTick", null, EventArgs.Empty);
        Check(enableChanges == 0, $"Idle monitoring toggled editing/navigation controls {enableChanges} times.");

        var previous = Environment.GetEnvironmentVariable("BACNET_WORKBENCH_MONITOR_FIXTURE");
        EngineClient engine;
        try
        {
            Environment.SetEnvironmentVariable("BACNET_WORKBENCH_MONITOR_FIXTURE", "1");
            engine = new EngineClient(Path.Combine(AppContext.BaseDirectory, "DesktopChecks.exe"));
        }
        finally { Environment.SetEnvironmentVariable("BACNET_WORKBENCH_MONITOR_FIXTURE", previous); }
        Set(window, "engine", engine);
        try
        {
            var rows = new[] { new PropertyRow { Device = "peer", Object = "2:1", Property = "description", Type = "character-string", Value = "original" } };
            Invoke(window, "ShowPropertyRows", (object)rows);
            var grid = (DataGrid)window.FindName("PropertyGrid");
            grid.SelectedItem = rows[0];
            var input = (TextBox)window.FindName("WriteValue");
            input.Text = "unsaved operator draft";
            input.Select(4, 7);
            var priorities = (DataGrid)window.FindName("PriorityGrid");
            priorities.ItemsSource = new[] { new[] { "unsent PV draft" } };
            var prioritySource = priorities.ItemsSource;
            var watches = Field<ObservableCollection<Watch>>(window, "watches");
            var watch = new Watch { Device = "peer", Object = "2:1", Process = 7, RenewAt = DateTimeOffset.MinValue };
            watches.Add(watch);
            Set(window, "polling", false);
            var status = (TextBlock)window.FindName("Status");
            status.Text = "Operator is editing";
            Task Tick() => (Task)Invoke(window, "Monitor")!;
            void CheckEditor()
            {
                Check(controls.All(control => control.IsEnabled), "Background work disabled an editor/navigation control.");
                Check(((FrameworkElement)window.FindName("Progress")).Visibility == Visibility.Collapsed, "Background work displayed the foreground progress bar.");
                Check(ReferenceEquals(grid.SelectedItem, rows[0]), "Monitor replaced the selected property.");
                Check(input.Text == "unsaved operator draft" && input.SelectionStart == 4 && input.SelectionLength == 7, "Monitor changed draft text/caret selection.");
                Check(ReferenceEquals(priorities.ItemsSource, prioritySource), "Monitor rebuilt the priority editor.");
                Check(status.Text == "Operator is editing", "Monitor overwrote operator status.");
            }

            var monitoring = Tick();
            Check(!monitoring.IsCompleted, "Fixture must exercise a pending COV response.");
            CheckEditor();
            var overlap = Tick();
            Check(overlap.IsCompleted, "A second monitor cycle overlapped the first.");
            await monitoring;
            CheckEditor();
            Check(watch.RenewAt > DateTimeOffset.UtcNow, "COV was not renewed while editing.");
            Check(enableChanges == 0 && watch.Samples.Count == 1 && watch.Quality == "COV", "COV did not update its watch independently of editing.");

            watch.Process = 0;
            ((TextBox)window.FindName("PollInterval")).Text = "1";
            Set(window, "polling", true);
            Set(window, "nextPoll", DateTimeOffset.MinValue);
            monitoring = Tick();
            CheckEditor();
            await monitoring;
            CheckEditor();
            Check(watch.Samples.Count == 2 && watch.Quality == "Poll", "Polling did not deliver a sample while editing.");
            Check(Field<DateTimeOffset>(window, "nextPoll") > DateTimeOffset.UtcNow, "Polling scheduled a catch-up storm.");
            ((TextBox)window.FindName("PollInterval")).Text = "";
            Set(window, "nextPoll", DateTimeOffset.MinValue);
            await Tick();
            CheckEditor();
            ((TextBox)window.FindName("PollInterval")).Text = "3";

            watch.Process = 7;
            Set(window, "polling", false);
            await engine.Call("fail-next");
            await Tick();
            CheckEditor();
            Check(Field<object?>(window, "monitorDone") == null && !Field<bool>(window, "busy"), "Monitor failure left an operation locked.");
            monitoring = Tick();
            await monitoring;
            Check(watch.Samples.Count == 3, "Monitoring failed to recover after an error.");

            // Queue a real foreground action during a delayed background read. It must run once, after drain.
            monitoring = Tick();
            var ran = false;
            var foreground = (Task)Invoke(window, "Run", "Test foreground", (Func<Task>)(() =>
            {
                Check(monitoring.IsCompleted, "Foreground work overlapped the monitor.");
                ran = true;
                return Task.CompletedTask;
            }))!;
            Check(!ran, "Foreground work did not wait for the current background exchange.");
            await foreground;
            Check(ran && !Field<bool>(window, "busy") && controls.All(control => control.IsEnabled), "Foreground action was dropped or controls remained disabled.");

            var samples = watch.Samples.Count;
            monitoring = Tick();
            Set(window, "closing", true);
            await monitoring;
            Check(watch.Samples.Count == samples && Field<object?>(window, "monitorDone") == null, "Closing did not drain monitoring without applying a late response.");
            Check(Tick().IsCompleted, "Monitoring restarted during shutdown.");
        }
        finally { await engine.DisposeAsync(); }
    }

    private static void CheckRefreshDraft()
    {
        var window = new MainWindow();
        PropertyRow Row(string value) => new() { Device = "peer", Object = "2:1", Property = "description", Type = "character-string", Value = value };
        var original = Row("original");
        Invoke(window, "ShowPropertyRows", (object)new[] { original });
        var grid = (DataGrid)window.FindName("PropertyGrid");
        grid.SelectedItem = original;
        var input = (TextBox)window.FindName("WriteValue");
        input.Text = "unsaved draft across refresh";
        input.Select(3, 4);
        var fresh = Row("new remote value");
        Invoke(window, "ShowPropertyRows", (object)new[] { fresh });
        Check(ReferenceEquals(grid.SelectedItem, fresh), "Property refresh lost the selected property.");
        Check(input.Text == "unsaved draft across refresh" && input.SelectionStart == 3 && input.SelectionLength == 4, "Property refresh discarded the operator draft or caret.");
        Console.WriteLine("PASS: property refresh updates observations while preserving the selected property's unsaved draft.");
    }

    private static async Task CheckEngineLoss()
    {
        await using var engine = EngineClientChecks.Start();
        var window = new MainWindow();
        Set(window, "engine", engine);
        Set(window, "connected", true);
        var row = new PropertyRow { Device = "peer", Object = "2:1", Property = "description", Type = "character-string", Value = "original" };
        Invoke(window, "ShowPropertyRows", (object)new[] { row });
        ((DataGrid)window.FindName("PropertyGrid")).SelectedItem = row;
        var input = (TextBox)window.FindName("WriteValue");
        input.Text = "retain this draft after engine loss";
        try { await engine.Call("corrupt").WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (IOException) { }
        await (Task)Invoke(window, "Monitor")!;
        Check(!Field<bool>(window, "connected"), "Engine loss left the UI claiming it was connected.");
        Check(input.Text == "retain this draft after engine loss", "Engine loss cleared an unsent draft.");
        Check(((TextBlock)window.FindName("ConnectionBadge")).Text.Contains("OFFLINE"), "Missing engine-loss status.");
        Console.WriteLine("PASS: failed engine leaves the UI offline with observations and drafts preserved for recovery.");
    }

    private static async Task CheckSingleWrite()
    {
        var previous = Environment.GetEnvironmentVariable("BACNET_WORKBENCH_MONITOR_FIXTURE");
        EngineClient engine;
        try
        {
            Environment.SetEnvironmentVariable("BACNET_WORKBENCH_MONITOR_FIXTURE", "1");
            engine = new EngineClient(Path.Combine(AppContext.BaseDirectory, "DesktopChecks.exe"));
        }
        finally { Environment.SetEnvironmentVariable("BACNET_WORKBENCH_MONITOR_FIXTURE", previous); }
        await using (engine)
        {
            var window = new MainWindow();
            Set(window, "engine", engine); Set(window, "connected", true);
            var peer = new Peer { Key = "peer", Device = "8:1", Name = "Pipe-only fixture" };
            Set(window, "selectedPeer", peer);
            var row = new PropertyRow { Device = "peer", Object = "2:1", Property = "description", Type = "character-string", Value = "original" };
            Invoke(window, "ShowPropertyRows", (object)new[] { row });
            ((DataGrid)window.FindName("PropertyGrid")).SelectedItem = row;
            ((TextBox)window.FindName("WriteValue")).Text = "local fixture write";
            await ((Task)Invoke(window, "Write", false, false)!).WaitAsync(TimeSpan.FromSeconds(6));
            var result = await engine.Call("write-count");
            Check(result.GetInt32() == 1, "One Save must send exactly one write through the pipe fixture.");
            Check(((TextBlock)window.FindName("Status")).Text.StartsWith("1 write(s) acknowledged"), "Single-property Save did not finish with acknowledged readback.");
            Console.WriteLine("PASS: single-property Save preflights, sends exactly once and reads back without a second confirmation (pipe fixture only).");
        }
    }

    // Disposable pipe-only peer. It implements no BACnet and opens no network sockets.
    internal static void Serve()
    {
        var fail = false;
        var writes = 0;
        var savedValue = "original";
        while (Console.ReadLine() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var operation = root.GetProperty("op").GetString();
            if (operation == "fail-next")
            {
                fail = true;
                Console.WriteLine(JsonSerializer.Serialize(new { id, result = true }));
                continue;
            }
            if (operation == "write-count")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { id, result = writes }));
                continue;
            }
            if (operation == "write")
            {
                writes++;
                savedValue = root.GetProperty("args").GetProperty("value").GetString()!;
                Console.WriteLine(JsonSerializer.Serialize(new { id, result = new { acknowledged = true, readback = new { rows = new[] { new PropertyRow { Device = "peer", Object = "2:1", Property = "description", Type = "character-string", Raw = savedValue == "original" ? "7509006f726967696e616c" : "7514006c6f63616c2066697874757265207772697465", Value = savedValue } } } } }));
                continue;
            }
            if (operation == "properties" || operation == "read" && root.GetProperty("args").GetProperty("selections")[0].GetProperty("property").GetString() == "description")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { id, result = new { rows = new[] { new PropertyRow { Device = "peer", Object = "2:1", Property = "description", Type = "character-string", Raw = savedValue == "original" ? "7509006f726967696e616c" : "7514006c6f63616c2066697874757265207772697465", Value = savedValue } } } }));
                continue;
            }
            Thread.Sleep(operation == "read" ? 1100 : 120);
            if (fail)
            {
                fail = false;
                Console.WriteLine(JsonSerializer.Serialize(new { id, error = "Simulated monitoring failure" }));
                continue;
            }
            var row = new PropertyRow { Device = "peer", Object = "2:1", Property = "present-value", Value = "5", Type = "real", At = DateTimeOffset.UtcNow };
            object result = operation == "notifications"
                ? new { sequence = root.GetProperty("args").GetProperty("after").GetUInt64() + 1, dropped = 0UL, events = new[] { new { process = 7U, rows = new[] { row } } } }
                : new { rows = new[] { row } };
            Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
        }
    }
}
