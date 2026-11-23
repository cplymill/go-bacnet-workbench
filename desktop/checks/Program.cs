using System.IO;
using BACnetExplorer;

if (Environment.GetEnvironmentVariable("WORKBENCH_SNAPSHOT_FIXTURE") == "1")
{
    SnapshotChecks.Serve();
    return;
}

if (Environment.GetEnvironmentVariable("BACNET_WORKBENCH_ENGINE_FIXTURE") == "1")
{
    EngineClientChecks.Serve();
    return;
}
if (args.Contains("--engine-checks-only"))
{
    try { EngineClientChecks.Run().GetAwaiter().GetResult(); }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    return;
}
if (Environment.GetEnvironmentVariable("BACNET_WORKBENCH_INVENTORY_FIXTURE") == "1")
{
    InventoryChecks.Serve();
    return;
}
if (Environment.GetEnvironmentVariable("BACNET_WORKBENCH_MONITOR_FIXTURE") == "1")
{
    MonitorChecks.Serve();
    return;
}

if (!args.Contains("--production-engine-only"))
{
    EngineClientChecks.Run().GetAwaiter().GetResult();
    SnapshotChecks.Run().GetAwaiter().GetResult();
    PropertyEditChecks.Run();
    MonitorChecks.Run();
}

static void Equal(string expected, string actual)
{
    if (actual != expected) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

foreach (var numeric in new[] { "0:0", "1:12", "2:4194303", "3:6", "4:1", "5:8", "8:16", "13:3", "17:1", "19:2", "56:1", "512:123", "1023:4194303" })
    Equal(numeric, ObjectLabels.Numeric(ObjectLabels.Display(numeric)));
foreach (var type in Enumerable.Range(0, 63).Where(type => type != 38))
{
    var numeric = type + ":62";
    var label = ObjectLabels.Display(numeric);
    if (label == numeric) throw new InvalidOperationException("Missing object label: " + numeric);
    Equal(numeric, ObjectLabels.Numeric(label));
}
Equal("CSV:62", ObjectLabels.Display("40:62"));
Equal("CSV:63", ObjectLabels.Display("40:63"));
Equal("AI:1", ObjectLabels.Display("0:1"));
Equal("AI:2", ObjectLabels.Display("0:2"));
Equal("BI:6", ObjectLabels.Display("3:6"));
Equal("3:6", ObjectLabels.Numeric(" bi : 6 "));
foreach (var invalid in new[] { "BI:-1", "BI:4194304", "1024:1", "UNKNOWN:6", "BI", "BI:6:7" })
{
    try { ObjectLabels.Numeric(invalid); }
    catch (FormatException) { continue; }
    throw new InvalidOperationException("Accepted invalid identifier: " + invalid);
}
Equal("3", new PropertyRow { Type = "enumerated", Value = "enumerated:3" }.DisplayValue);
Equal("Supply air", new PropertyRow { Type = "character-string", Value = "\"Supply air\"" }.DisplayValue);
Equal("", new PropertyRow { Type = "character-string", Value = "\"\"" }.DisplayValue);
Equal("false", new PropertyRow { Type = "boolean", Value = "false" }.DisplayValue);
Equal("", new PropertyRow { Error = "Read failed", Value = "enumerated:3" }.DisplayValue);
Equal("Structured data", new PropertyRow { Value = "raw:91709179" }.DisplayValue);
Equal("BI:6", new ObjectRow { Object = "3:6" }.ObjectLabel);
Console.WriteLine("PASS: object labels preserve numeric identity and bounds; property values separate type, text, structured data and errors.");

// A transport continuation must never post parsing or pipe reads to the UI context.
if (args.Length > 0)
{
    var context = new RecordingContext();
    var previous = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(context);
    try
    {
        var client = new EngineClient(Path.GetFullPath(args[0]));
        try
        {
            for (var request = 0; request < 30; request++)
            {
                var result = client.Call("system").GetAwaiter().GetResult();
                if (!result.TryGetProperty("version", out _)) throw new InvalidOperationException("Missing engine response.");
            }
            var selections = Enumerable.Range(0, 64).Select(instance => new { @object = "3:" + instance, property = "object-name" }).ToArray();
            void Batch(int count)
            {
                for (var request = 0; request < count; request++)
                {
                    var rows = client.Call("normalize", new { selections }).GetAwaiter().GetResult();
                    if (rows.GetArrayLength() != 64) throw new InvalidOperationException("Normalization response mismatch.");
                }
            }
            Batch(50);
            var before = GC.GetTotalMemory(true);
            Batch(800);
            var after = GC.GetTotalMemory(true);
            var growth = after - before;
            if (growth > 4 * 1024 * 1024) throw new InvalidOperationException($"Retained managed memory grew by {growth} bytes.");
            Console.WriteLine($"PASS: 800 repeated 64-selector requests; retained managed memory delta {growth:N0} bytes (4 MiB guard).");
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        if (context.Posts != 0) throw new InvalidOperationException($"IPC posted {context.Posts} continuations to the UI context.");
        Console.WriteLine("PASS: 30 production engine requests and disposal completed without posting IPC work to the UI context.");
    }
    finally { SynchronizationContext.SetSynchronizationContext(previous); }
}

sealed class RecordingContext : SynchronizationContext
{
    public int Posts;
    public override void Post(SendOrPostCallback callback, object? state)
    {
        Interlocked.Increment(ref Posts);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
    }
}
