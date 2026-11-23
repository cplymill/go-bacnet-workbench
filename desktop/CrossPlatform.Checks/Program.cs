using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using BACnetExplorer;
using BACnetExplorer.CrossPlatform;

if (Environment.GetEnvironmentVariable("WORKBENCH_CROSS_FIXTURE") == "1") {
    Fixture.Serve(); return;
}
AppBuilder.Configure<WorkbenchApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions {
    UseHeadlessDrawing = false
}).SetupWithoutStarting();
SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
var check = Checks.Run(args);
while (!check.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
check.GetAwaiter().GetResult();

internal static class Checks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(WorkbenchWindow window, string name) =>
        (T)typeof(WorkbenchWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(WorkbenchWindow window, string name, object value) =>
        typeof(WorkbenchWindow).GetField(name, Private)!.SetValue(window, value);
    private static Task Call(WorkbenchWindow window, string name, params object[] args) =>
        (Task)typeof(WorkbenchWindow).GetMethod(name, Private)!.Invoke(window, args)!;
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
    private static EngineClient Start()
    {
        Environment.SetEnvironmentVariable("WORKBENCH_CROSS_FIXTURE", "1");
        try { return new EngineClient(Path.Combine(AppContext.BaseDirectory, "Workbench.CrossPlatform.Checks.exe")); }
        finally { Environment.SetEnvironmentVariable("WORKBENCH_CROSS_FIXTURE", null); }
    }
    internal static async Task Run(string[] args)
    {
        await using var engine = Start();
        var window = new WorkbenchWindow(engine, false);
        window.Show();
        Set(window, "connected", true);
        var target = Field<TextBox>(window, "target"); target.Text = "192.168.47.10";
        var search = Call(window, "Run", "Discover", (Func<Task>)(() => Call(window, "Discover")));
        var peers = Field<ObservableCollection<Peer>>(window, "peers");
        await Until(() => peers.Count == 1);
        Require(Field<Button>(window, "disconnectButton").IsEnabled, "Discovery disabled Disconnect.");
        target.Text = "192.168.47.11";
        var replacement = Call(window, "StartDiscovery");
        await search;
        await Until(() => peers.Count == 2);
        await Call(window, "Cancel"); await replacement;
        Require(!Field<bool>(window, "busy") && peers.Count == 2, "Search replacement/cancellation stranded the UI.");
        Console.WriteLine("PASS: headless Mac view replaces and cancels discovery with results retained.");

        var peer = peers[0]; Set(window, "peer", peer); Set(window, "objectId", "2:1");
        await Call(window, "ReadInventory", true);
        Require(Field<ObservableCollection<ObjectRow>>(window, "objects").Single().Name == "Room temperature",
            "Inventory enrichment lost object identity.");
        await Call(window, "ReadProperties");
        var rows = Field<ObservableCollection<PropertyRow>>(window, "properties");
        var present = rows.Single(row => row.Property == "present-value");
        Require(present.EditAccess.CanEdit, "Shared write policy failed to offer a commandable PV.");
        Field<DataGrid>(window, "propertyGrid").SelectedItem = present;
        await Until(() => Field<PropertyRow?>(window, "property") == present);
        Field<TextBox>(window, "value").Text = "12.5";
        Field<TextBox>(window, "priority").Text = "8";
        await Call(window, "WriteProperty", false);
        var writes = await engine.Call("fixture-counts");
        Require(writes.GetProperty("writes").GetInt32() == 1, "Single Save did not send exactly once.");
        Field<TextBox>(window, "value").Text = "16";
        await Call(window, "ReadProperties");
        Require(Field<TextBox>(window, "value").Text == "16", "Refresh erased an unsent property draft.");
        Field<TextBox>(window, "value").Text = "12.5";
        Set(window, "propertyDraft", "12.5");
        Console.WriteLine("PASS: shared write preflight, exactly-once Save, readback and refresh draft preservation.");

        await Call(window, "AddWatch");
        await Call(window, "Poll");
        var watch = Field<ObservableCollection<Watch>>(window, "watches").Single();
        Require(watch.Samples.Count == 1 && watch.Value == "12.5", "Watch poll did not observe its selector.");
        Set(window, "objectId", "17:1"); await Call(window, "ReadSchedule");
        var schedule = Field<ScheduleDocument>(window, "schedule");
        Require(schedule.Days.Count == 7 && schedule.Raw == "aa", "Schedule identity and weekly program lost.");
        Field<TextBox>(window, "blockStart").Text = "08:00";
        Field<TextBox>(window, "blockEnd").Text = "17:00";
        await Call(window, "SetBlock");
        Require(schedule.Days[0].Count == 2, "Shared schedule block editor did not create boundaries.");
        Set(window, "structuredBaseline", "");
        Set(window, "objectId", "6:1"); await Call(window, "ReadCalendar");
        Require(Field<CalendarDocument>(window, "calendar").Raw == "bb", "Calendar read identity lost.");
        await Call(window, "AddRule");
        Require(Field<CalendarDocument>(window, "calendar").Rules.Count == 2, "Calendar rule editor failed.");
        Console.WriteLine("PASS: watch samples, weekly block editing and calendar rules use the existing app models.");

        if (args.Length > 0) {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(args[0], Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Console.WriteLine("PASS: offscreen Mac interface render saved (Windows headless backend, not a Mac runtime).");
        }
        Set(window, "structuredBaseline", "");
        watch.Process = 7;
        await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(5));
        window.Close();
        Require(!engine.IsAvailable && Field<bool>(window, "closed"), "Unresponsive unsubscribe prevented shutdown.");
        Console.WriteLine("PASS: close bounds unresponsive subscription cleanup and disposes its owned engine.");
    }
}

internal static class Fixture
{
    internal static void Serve()
    {
        long held = 0; var writes = 0; var value = "10";
        object Row(string property, string type, string text, string raw) => new {
            device = "192.168.47.10", @object = "2:1", property, type, value = text, raw, error = "", at = DateTimeOffset.UtcNow,
            elements = property == "priority-array" ? Enumerable.Repeat("null", 16).ToArray() : Array.Empty<string>()
        };
        object Properties() => new { rows = new[] {
            Row("present-value", "real", value, "44" + value),
            Row("priority-array", "raw", "raw:00", "00"),
            Row("object-name", "character-string", "\"Room temperature\"", "75")
        } };
        void Reply(long id, object result) => Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
        while (Console.ReadLine() is { } line) {
            using var document = JsonDocument.Parse(line); var request = document.RootElement;
            var id = request.GetProperty("id").GetInt64(); var operation = request.GetProperty("op").GetString();
            var args = request.GetProperty("args");
            if (operation == "discover") {
                held = id; var address = args.GetProperty("target").GetString();
                Console.WriteLine(JsonSerializer.Serialize(new { id, progress = true, result = new[] {
                    new { key = address, device = "8:1", name = "Fixture controller", address, maxApdu = 1476 }
                } })); continue;
            }
            if (operation == "cancel") {
                if (held != 0) Console.WriteLine(JsonSerializer.Serialize(new { id = held, error = "Operation canceled" }));
                held = 0; Reply(id, true); continue;
            }
            if (operation == "properties") { Reply(id, Properties()); continue; }
            if (operation == "read") { Reply(id, new { rows = new[] { Row("present-value", "real", value, "44" + value) } }); continue; }
            if (operation is "inventory" or "object-list") {
                Reply(id, new { objects = new[] { new { @object = "2:1", kind = "analog-value" } }, enrichment = Properties() }); continue;
            }
            if (operation == "write") { writes++; value = args.GetProperty("value").GetString()!; Reply(id, new { acknowledged = true, readback = Properties() }); continue; }
            if (operation == "fixture-counts") { Reply(id, new { writes }); continue; }
            if (operation == "subscribe") { held = id; continue; }
            if (operation == "schedule-read") { Reply(id, new { days = Enumerable.Range(0,7).Select(_ => Array.Empty<object>()), raw = "aa", defaultType = "boolean", defaultValue = "false", defaultRaw = "bb", exceptionsRaw = "", exceptions = Array.Empty<object>() }); continue; }
            if (operation == "calendar-read") { Reply(id, new { rules = new[] { new { kind = "date", start = "2026-11-23", weekday = "*" } }, raw = "bb" }); continue; }
            Reply(id, true);
        }
    }
}
