using System.IO;
using System.Reflection;
using System.Text.Json;
using BACnetExplorer;

internal static class SnapshotChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run()
    {
        var previous = Environment.GetEnvironmentVariable("WORKBENCH_SNAPSHOT_FIXTURE");
        EngineClient engine;
        try
        {
            Environment.SetEnvironmentVariable("WORKBENCH_SNAPSHOT_FIXTURE", "1");
            var executable = Assembly.GetEntryAssembly()!.GetName().Name + (OperatingSystem.IsWindows() ? ".exe" : "");
            engine = new EngineClient(Path.Combine(AppContext.BaseDirectory, executable));
        }
        finally { Environment.SetEnvironmentVariable("WORKBENCH_SNAPSHOT_FIXTURE", previous); }
        await using var owned = engine;
        async Task<List<PropertyRow>> Capture() => await Snapshot.Capture(engine, "fixture",
            await engine.Call("inventory"), () => false);

        var baseline = await Capture();
        Require(baseline.Count == 9 && baseline.Count(row => row.Error.Length > 0) == 3,
            "Snapshot lost observations or optional-property errors.");
        Require(baseline.Single(row => row.Object == "2:1" && row.Property == "priority-array").Elements.Length == 16,
            "Snapshot lost priority slot boundaries.");
        baseline = JsonSerializer.Deserialize<List<PropertyRow>>(JsonSerializer.Serialize(baseline, EngineClient.Json), EngineClient.Json)!;
        var workspace = JsonSerializer.Deserialize<Workspace>(JsonSerializer.Serialize(new Workspace { Baseline = baseline }, EngineClient.Json), EngineClient.Json)!;
        Require(Snapshot.Compare(baseline, workspace.Baseline).Count == 0, "Workspace changed the saved priority baseline.");

        await engine.Call("fixture-mode", new { mode = "override" });
        var live = await Capture();
        var changes = Snapshot.Compare(baseline, live);
        Require(changes.Count == 2 && changes.Single(row => row.Property == "priority-array[8]").After == "12.5"
            && changes.Single(row => row.Property == "priority-array[16]").After == "3",
            "New priority 8/16 overrides were not isolated by slot.");
        Require(Snapshot.Compare(live, baseline).All(row => row.After == "null"), "Released priorities did not retain NULL.");
        var legacy = baseline.Where(row => row.Property is not ("priority-array" or "relinquish-default")).ToList();
        Require(Snapshot.Compare(legacy, live).All(row => row.Change == "Not captured"),
            "An older baseline invented historical priority values.");

        await engine.Call("fixture-mode", new { mode = "default" });
        Require(Snapshot.Compare(live, await Capture()).Single().Property == "relinquish-default",
            "Relinquish default change was lost.");
        await engine.Call("fixture-mode", new { mode = "denied" });
        Require(Snapshot.Compare(baseline, await Capture()).Any(row => row.Property == "priority-array" && row.Change == "Read error"),
            "A failed priority read appeared as released slots.");
        await engine.Call("fixture-mode", new { mode = "malformed" });
        Require((await Capture()).Any(row => row.Object == "2:1" && row.Property == "priority-array" && row.Error.Contains("sixteen")),
            "Malformed priority data was accepted as a complete array.");

        await engine.Call("fixture-mode", new { mode = "missing" });
        try { _ = await Capture(); throw new Exception("Missing priority result was silently accepted."); }
        catch (InvalidDataException) { }
        var cancellationChecks = 0;
        await engine.Call("fixture-mode", new { mode = "normal" });
        try
        {
            baseline = await Snapshot.Capture(engine, "fixture", await engine.Call("inventory"), () => ++cancellationChecks > 1);
            throw new Exception("Canceled snapshot replaced the baseline.");
        }
        catch (OperationCanceledException) { }
        Require(baseline.Count == 9, "Cancellation changed the previous baseline.");
        var other = live.Select(row => new PropertyRow { Device = "different-address", Object = row.Object, Property = row.Property }).ToList();
        try { Snapshot.Compare(baseline, other); throw new Exception("Cross-device baseline was accepted."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("different device binding")) { }
        await engine.Call("fixture-mode", new { mode = "large" });
        var readsBefore = (await engine.Call("fixture-counts")).GetInt32();
        Require((await Capture()).Count == 390 && (await engine.Call("fixture-counts")).GetInt32() - readsBefore == 2,
            "A snapshot spanning batches lost observations or exceeded its read-batch bound.");
        Console.WriteLine("PASS: saved snapshots compare individual priorities 8/16, NULL releases and defaults; legacy coverage, read failures, malformed/missing results, cancellation and device identity remain explicit.");
    }

    internal static void Serve()
    {
        var mode = "normal";
        var reads = 0;
        PropertyRow Row(string id, string property) => new() { Device = "fixture", Object = id, Property = property };
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            var operation = request.GetProperty("op").GetString();
            var args = request.GetProperty("args");
            object result;
            if (operation == "fixture-mode") { mode = args.GetProperty("mode").GetString()!; result = true; }
            else if (operation == "fixture-counts") result = reads;
            else if (operation == "inventory")
            {
                var ids = mode == "large" ? Enumerable.Range(1, 130).Select(index => $"2:{index}").ToArray()
                    : new[] { "2:1", "2:2", "8:1" };
                result = new { objects = ids.Select(id => new { @object = id }),
                    enrichment = new { rows = ids.Select(id => Row(id, "object-name")) } };
            }
            else if (operation == "read")
            {
                reads++;
                if (args.GetProperty("selections").GetArrayLength() > 256)
                    throw new InvalidOperationException("Snapshot read exceeded its batch size.");
                var rows = new List<PropertyRow>();
                foreach (var selection in args.GetProperty("selections").EnumerateArray())
                {
                    var row = Row(selection.GetProperty("object").GetString()!, selection.GetProperty("property").GetString()!);
                    if (row.Object == "8:1" || row.Object == "2:2" && row.Property == "priority-array") row.Error = "Unsupported property";
                    else if (row.Property == "priority-array")
                    {
                        row.Elements = Enumerable.Repeat("null", mode == "malformed" ? 15 : 16).ToArray();
                        if (mode is "override" or "default") { row.Elements[7] = "12.5"; row.Elements[15] = "3"; }
                        row.Raw = mode is "override" or "default" ? "01" : "00";
                        if (mode == "denied") { row.Error = "Read access denied"; row.Elements = []; row.Raw = ""; }
                    }
                    else { row.Value = mode == "default" && row.Object == "2:1" ? "6" : "5"; row.Raw = row.Value; }
                    rows.Add(row);
                }
                if (mode == "missing") rows.RemoveAt(0);
                result = new { rows };
            }
            else throw new InvalidOperationException("Snapshot attempted an unexpected or mutating operation: " + operation);
            Console.WriteLine(JsonSerializer.Serialize(new { id = request.GetProperty("id").GetInt64(), result }, EngineClient.Json));
        }
    }
}
