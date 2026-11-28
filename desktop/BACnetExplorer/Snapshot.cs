using System.IO;
using System.Text.Json;

namespace BACnetExplorer;

// Shared desktop snapshot owner. All BACnet reads remain in the existing engine.
internal static class Snapshot
{
    public const int MaximumObservations = 50000;
    public const int MaximumFileBytes = 16 * 1024 * 1024;

    public static async Task<List<PropertyRow>> Capture(
        EngineClient engine, string device, JsonElement inventory, Func<bool> canceled)
    {
        if (inventory.TryGetProperty("enrichmentError", out var error))
            throw new IOException("Snapshot incomplete: " + error.GetString());
        var rows = inventory.GetProperty("enrichment").GetProperty("rows")
            .Deserialize<List<PropertyRow>>(EngineClient.Json)
            ?? throw new InvalidDataException("Snapshot observations are missing.");
        var objects = inventory.GetProperty("objects").EnumerateArray()
            .Select(item => item.GetProperty("object").GetString()
                ?? throw new InvalidDataException("Snapshot object identity is missing."))
            .ToArray();
        if (objects.Length > 10000 || objects.Distinct().Count() != objects.Length)
            throw new InvalidDataException("Snapshot object inventory is invalid.");
        // Keep normal inventory enrichment fast; priority reads happen only for snapshots.
        long valueBytes = rows.Sum(row => (long)row.Raw.Length / 2);
        foreach (var batch in objects.Chunk(128))
        {
            if (canceled())
                throw new OperationCanceledException("Snapshot canceled; previous baseline retained.");
            var selections = batch.SelectMany(id => new[] {
                new { @object = id, property = "priority-array" },
                new { @object = id, property = "relinquish-default" }
            }).ToArray();
            var result = await engine.Call("read", new
            {
                device,
                selections
            });
            var observations = result.GetProperty("rows").Deserialize<List<PropertyRow>>(EngineClient.Json)
                ?? throw new InvalidDataException("Snapshot priority results are missing.");
            var expected = selections.Select(item => $"{device}|{item.@object}|{item.property}|").ToHashSet();
            if (observations.Count != expected.Count || observations.Any(row => !expected.Remove(row.Identity)) || expected.Count != 0)
                throw new InvalidDataException("Snapshot priority results are incomplete or contain unexpected selectors.");
            foreach (var row in observations)
            {
                valueBytes += row.Raw.Length / 2;
                if (valueBytes > 4 * 1024 * 1024)
                    throw new InvalidDataException("Snapshot values exceed 4 MiB.");
                // Unsupported properties stay explicit error observations, never empty/released slots.
                if (row.Property == "priority-array" && row.Error.Length == 0 && row.Elements.Length != 16)
                    row.Error = "Priority array did not return sixteen decodable slots.";
                rows.Add(row);
            }
        }
        if (canceled())
            throw new OperationCanceledException("Snapshot canceled; previous baseline retained.");
        Validate(rows);
        if (JsonSerializer.SerializeToUtf8Bytes(rows, EngineClient.Json).Length > MaximumFileBytes)
            throw new InvalidDataException("Snapshot exceeds the 16 MiB baseline file limit.");
        return rows;
    }

    public static void Validate(IReadOnlyList<PropertyRow> rows)
    {
        if (rows.Count > MaximumObservations || rows.Select(row => row.Identity).Distinct().Count() != rows.Count)
            throw new InvalidDataException("Baseline exceeds limits or contains duplicate selectors.");
    }

    public static bool HasPriorities(IEnumerable<PropertyRow> rows) => rows.Any(row => row.Property == "priority-array");

    public static List<Difference> Compare(IReadOnlyList<PropertyRow> baseline, IReadOnlyList<PropertyRow> live)
    {
        Validate(baseline);
        Validate(live);
        // Bindings include address/route. Never compare one device's overrides against another device.
        var devices = baseline.Select(row => row.Device).Distinct().ToArray();
        if (devices.Length != 1 || live.Any(row => row.Device != devices[0]))
            throw new InvalidOperationException("Baseline belongs to a different device binding. Select its original device or capture a new baseline.");
        var baselineContext = baseline.ToLookup(row => row.Object);
        var liveContext = live.ToLookup(row => row.Object);
        var before = baseline.ToDictionary(row => row.Identity);
        var after = live.ToDictionary(row => row.Identity);
        var oldObjects = baseline.Select(row => row.Object).ToHashSet();
        var differences = new List<Difference>();
        foreach (var key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out var old);
            after.TryGetValue(key, out var current);
            var row = current ?? old!;
            if (old != null && current != null && old.Raw == current.Raw && old.Error == current.Error)
                continue;
            if (row.Property == "priority-array" && old is { Error.Length: 0, Elements.Length: 16 }
                && current is { Error.Length: 0, Elements.Length: 16 })
            {
                var changed = false;
                for (var index = 0; index < 16; index++)
                {
                    var typesKnown = old.ArrayTypes.Length == 16 && current.ArrayTypes.Length == 16;
                    var typeChanged = typesKnown && old.ArrayTypes[index] != current.ArrayTypes[index];
                    if (old.Elements[index] == current.Elements[index] && !typeChanged)
                        continue;
                    var oldType = typeChanged ? $" ({old.ArrayTypes[index]})" : "";
                    var currentType = typeChanged ? $" ({current.ArrayTypes[index]})" : "";
                    differences.Add(new Difference(row.Object, $"priority-array[{index + 1}]",
                        BacnetDisplay.Priority(old, index, baselineContext[row.Object]) + oldType,
                        BacnetDisplay.Priority(current, index, liveContext[row.Object]) + currentType, "Changed")
                    {
                        RawBefore = old.Elements[index] + oldType,
                        RawAfter = current.Elements[index] + currentType
                    });
                    changed = true;
                }
                if (changed)
                    continue;
                // Equal display text may still encode a different datatype. Keep the raw change visible.
            }
            var notCaptured = old == null && oldObjects.Contains(row.Object)
                && row.Property is "priority-array" or "relinquish-default";
            var change = notCaptured ? "Not captured" : old == null ? "Added" : current == null ? "Removed"
                : old.Error.Length > 0 || current.Error.Length > 0 ? "Read error" : "Changed";
            differences.Add(new Difference(row.Object, row.Selector,
                notCaptured ? "Not captured in this baseline" : Describe(old, baselineContext[row.Object]),
                Describe(current, liveContext[row.Object]), change)
            {
                RawBefore = notCaptured ? "Not captured in this baseline" : RawDescription(old),
                RawAfter = RawDescription(current)
            });
        }
        return differences;
    }

    private static string RawDescription(PropertyRow? row) => row == null ? "" :
        row.Property == "priority-array" && row.Error.Length == 0 && row.Elements.Length == 16
            ? string.Join("; ", row.Elements.Select((value, index) => $"P{index + 1}={value}")) : row.Result;

    private static string Describe(PropertyRow? row, IEnumerable<PropertyRow> context) => row == null ? "" :
        row.Error.Length > 0 ? row.DisplayError : BacnetDisplay.Value(row, context);
}
