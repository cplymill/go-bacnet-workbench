using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Data;
using BACnetExplorer;

internal static class InventoryChecks
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
        if (!condition) throw new InvalidOperationException(message);
    }

    private static EngineClient Start()
    {
        var previous = Environment.GetEnvironmentVariable("BACNET_WORKBENCH_INVENTORY_FIXTURE");
        try
        {
            Environment.SetEnvironmentVariable("BACNET_WORKBENCH_INVENTORY_FIXTURE", "1");
            return new EngineClient(Path.Combine(AppContext.BaseDirectory, "DesktopChecks.exe"));
        }
        finally { Environment.SetEnvironmentVariable("BACNET_WORKBENCH_INVENTORY_FIXTURE", previous); }
    }

    internal static async Task Run()
    {
        await using var engine = Start();
        var window = new MainWindow();
        Set(window, "engine", engine);
        ((DataGrid)window.FindName("ObjectGrid")).CanUserAddRows = false;
        var peer = new Peer { Key = "peer", Address = "offline fixture", Device = "8:1" };
        var objects = Field<ObservableCollection<ObjectRow>>(window, "objects");
        var elapsed = Stopwatch.StartNew();
        await (Task)Invoke(window, "LoadInventory", peer, true)!;
        elapsed.Stop();
        Check(objects.Count == 10000 && objects[9999].Name == "Point 9999" && objects[9999].Value == "9999", "Large inventory lost object/property correlation.");
        Console.WriteLine($"MEASURE: 10,000 objects / 30,000 properties loaded in {elapsed.Elapsed.TotalMilliseconds:F0} ms (includes fixture IPC and WPF updates).");

        ObjectRow? firstPageRow = null;
        var resets = 0;
        void Changed(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
            if (e.NewItems?.Count > 0 && firstPageRow == null) firstPageRow = (ObjectRow)e.NewItems[0]!;
        }
        objects.CollectionChanged += Changed;
        await (Task)Invoke(window, "LoadInventory", peer, false)!;
        objects.CollectionChanged -= Changed;
        Check(objects.Count == 3 && ReferenceEquals(objects[0], firstPageRow) && resets == 1,
            $"Object_List completion rebuilt streamed rows (resets: {resets}).");
        var view = CollectionViewSource.GetDefaultView(objects);
        ((TextBox)window.FindName("ObjectFilter")).Text = "AHU";
        Invoke(window, "ApplyObjectFilter");
        Check(!view.OfType<ObjectRow>().Any(), "Search fixture unexpectedly matched an unnamed row.");
        await (Task)Invoke(window, "ReadObjectNames", peer)!;
        Check(view.OfType<ObjectRow>().Single().Object == "2:0", "Names arriving after search did not update filtered results.");
        ((TextBox)window.FindName("ObjectFilter")).Text = "";
        Invoke(window, "ApplyObjectFilter");
        var original = objects[0];
        await (Task)Invoke(window, "LoadInventory", peer, true)!;
        Check(ReferenceEquals(original, objects[0]), "Enrichment replaced an existing object row.");
        Check(objects[0].Name == "Point 0", "Enrichment did not refresh the retained row.");
        Console.WriteLine("PASS: inventory keeps streamed/enriched row identity and search follows arriving names.");

        Set(window, "connected", true);
        Set(window, "selectedPeer", peer);
        objects[0].Name = "";
        objects[0].Value = "prior sample";
        ((TextBox)window.FindName("ObjectFilter")).Text = "prior sample";
        Invoke(window, "ApplyObjectFilter");
        Set(window, "busy", true);
        ((DataGrid)window.FindName("ObjectGrid")).SelectedItem = objects[0];
        Set(window, "busy", false);
        await (Task)Invoke(window, "Run", "Search fixture", (Func<Task>)(() => (Task)Invoke(window, "ReadObjectNames", peer)!))!;
        Check((await engine.Call("cancel-count")).GetInt32() == 0, "A filter removing a selected row canceled background enrichment.");
        Check(!view.OfType<ObjectRow>().Any(), "Changed present values did not update search results.");
        Console.WriteLine("PASS: automatic filtering does not issue navigation cancellation.");
        ((TextBox)window.FindName("ObjectFilter")).Text = "";
        Invoke(window, "ApplyObjectFilter");
        Set(window, "polling", true);
        Set(window, "nextPoll", DateTimeOffset.MinValue);
        ((TextBox)window.FindName("PollInterval")).Text = "1";
        var watch = new Watch { Device = "peer", Object = "2:0" };
        Field<ObservableCollection<Watch>>(window, "watches").Add(watch);
        await engine.Call("arm-slow-poll");
        var monitoring = (Task)Invoke(window, "Monitor")!;
        var ran = false;
        elapsed.Restart();
        await (Task)Invoke(window, "Run", "Foreground fixture", (Func<Task>)(() =>
        {
            Check(monitoring.IsCompleted, "Foreground operation overlapped background read.");
            ran = true;
            return Task.CompletedTask;
        }))!;
        Check(ran && elapsed.Elapsed < TimeSpan.FromSeconds(2), "Foreground action waited for the slow background polling deadline.");
        Console.WriteLine($"PASS: foreground action interrupted a read-only poll and drained it in {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
        watch.Observe(new PropertyRow { At = DateTimeOffset.UtcNow, Value = "42" }, "Poll");
        Set(window, "nextPoll", DateTimeOffset.MinValue);
        await engine.Call("arm-missing-result");
        await (Task)Invoke(window, "Monitor")!;
        Check(watch.Quality == "Error" && watch.Samples[^1].Number == null && watch.Samples[^1].Error.Length > 0,
            "A missing poll result retained a misleading continuous numeric trend.");
        Console.WriteLine("PASS: missing polling results record an explicit quality error and chart gap.");
        Set(window, "nextPoll", DateTimeOffset.MinValue);
        await engine.Call("arm-engine-exit");
        await (Task)Invoke(window, "Monitor")!;
        Check(!Field<bool>(window, "connected") && watch.Quality == "Connection lost", "Engine failure during polling was not surfaced immediately.");
        Console.WriteLine("PASS: terminal engine loss during polling marks the connection offline in the same cycle.");
    }

    internal static void Serve()
    {
        var output = new object();
        void Reply(object value)
        {
            lock (output) Console.WriteLine(JsonSerializer.Serialize(value));
        }
        var large = true;
        var slow = false;
        var missing = false;
        var exit = false;
        var cancels = 0;
        CancellationTokenSource? readStop = null;
        Task? active = null;
        while (Console.ReadLine() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var op = root.GetProperty("op").GetString();
            if (op == "cancel") { cancels++; readStop?.Cancel(); Reply(new { id, result = true }); continue; }
            if (op == "cancel-count") { Reply(new { id, result = cancels }); continue; }
            if (op == "arm-engine-exit") { exit = true; Reply(new { id, result = true }); continue; }
            if (op == "read" && exit) break;
            if (op == "arm-slow-poll") { slow = true; Reply(new { id, result = true }); continue; }
            if (op == "arm-missing-result") { missing = true; Reply(new { id, result = true }); continue; }
            if (op == "object-list")
            {
                var objects = Enumerable.Range(0, 3).Select(i => new { @object = $"2:{i}", kind = "analog-value" }).ToArray();
                Reply(new { id, progress = true, result = new { objects, completed = 3, total = 3 } });
                Reply(new { id, result = new { objects, indexedFallback = true } });
            }
            else if (op == "inventory")
            {
                var count = large ? 10000 : 3;
                large = false;
                var objects = Enumerable.Range(0, count).Select(i => new { @object = $"2:{i}", kind = "analog-value" }).ToArray();
                var rows = Enumerable.Range(0, count).SelectMany(i => new[] {
                    new PropertyRow { Device = "peer", Object = $"2:{i}", Property = "object-name", Value = $"Point {i}" },
                    new PropertyRow { Device = "peer", Object = $"2:{i}", Property = "present-value", Value = i.ToString() },
                    new PropertyRow { Device = "peer", Object = $"2:{i}", Property = "units", Value = "degrees-celsius" }
                }).ToArray();
                Reply(new { id, result = new { objects, enrichment = new { rows, rpm = 1, rp = 0, splits = 0 }, indexedFallback = false } });
            }
            else if (op == "read" && slow)
            {
                slow = false;
                readStop?.Dispose();
                readStop = new CancellationTokenSource();
                var token = readStop.Token;
                active = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(10), token); Reply(new { id, result = new { rows = Array.Empty<PropertyRow>() } }); }
                    catch (OperationCanceledException) { Reply(new { id, error = "Read canceled" }); }
                });
            }
            else if (op == "read")
            {
                var rows = missing ? Array.Empty<PropertyRow>() : new[] {
                    new PropertyRow { Device = "peer", Object = "2:0", Property = "object-name", Value = "AHU" },
                    new PropertyRow { Device = "peer", Object = "2:0", Property = "present-value", Value = "5" }
                };
                Reply(new { id, progress = true, result = new { rows } });
                Reply(new { id, result = new { rows } });
            }
            else throw new InvalidOperationException("Unexpected inventory fixture operation.");
        }
        readStop?.Cancel();
        active?.GetAwaiter().GetResult();
        readStop?.Dispose();
    }
}
