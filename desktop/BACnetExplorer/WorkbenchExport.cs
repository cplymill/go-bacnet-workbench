
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace BACnetExplorer;

internal static class WorkbenchExport
{
    private static readonly JsonSerializerOptions Indented = Options(true, null);
    private static readonly JsonSerializerOptions Compact = Options(false, null);
    internal static readonly JsonSerializerOptions CamelCase = Options(true, JsonNamingPolicy.CamelCase);

    private static JsonSerializerOptions Options(bool indented, JsonNamingPolicy? naming)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type == typeof(PropertyRow))
            {
                Add<PropertyRow>(type, nameof(PropertyRow.DisplayName), row => row.DisplayName);
                Add<PropertyRow>(type, nameof(PropertyRow.DisplayType), row => row.DisplayType);
                Add<PropertyRow>(type, nameof(PropertyRow.DisplayValue), row => row.DisplayValue);
                Add<PropertyRow>(type, nameof(PropertyRow.DisplayError), row => row.DisplayError);
                Add<PropertyRow>(type, "ObjectDescription", row => BacnetDisplay.ObjectReference(row.Object));
            }
            else if (type.Type == typeof(Watch))
            {
                Add<Watch>(type, "DisplayProperty", row => BacnetDisplay.PropertyName(row.Property));
                Add<Watch>(type, "ObjectDescription", row => BacnetDisplay.ObjectReference(row.Object));
            }
            else if (type.Type == typeof(ActivityRow))
                Add<ActivityRow>(type, nameof(ActivityRow.DisplayResult), row => row.DisplayResult);
            else if (type.Type == typeof(Difference))
            {
                Add<Difference>(type, "DisplayProperty", row => Selector(row.Property));
                Add<Difference>(type, "ObjectDescription", row => BacnetDisplay.ObjectReference(row.Object));
            }
        });
        return new JsonSerializerOptions(EngineClient.Json)
        {
            WriteIndented = indented,
            PropertyNamingPolicy = naming,
            TypeInfoResolver = resolver
        };
    }

    private static void Add<T>(JsonTypeInfo type, string name, Func<T, string> get)
    {
        var jsonName = type.Options.PropertyNamingPolicy?.ConvertName(name) ?? name;
        var existing = type.Properties.FirstOrDefault(property => property.Name == jsonName);
        if (existing != null)
            type.Properties.Remove(existing);
        var property = type.CreateJsonPropertyInfo(typeof(string), jsonName);
        property.Get = item => get((T)item);
        type.Properties.Add(property);
    }

    internal static string Selector(string selector)
    {
        var bracket = selector.IndexOf('[');
        return bracket < 0 ? BacnetDisplay.PropertyName(selector) :
            BacnetDisplay.PropertyName(selector[..bracket]) + " " + selector[bracket..];
    }

    internal static void DescribeRows(IEnumerable<PropertyRow> rows)
    {
        foreach (var group in rows.GroupBy(row => (row.Device, row.Object)))
            foreach (var row in group)
                row.FriendlyValue = BacnetDisplay.Value(row, group);
    }

    internal static string SaveDocument<T>(T document, IEnumerable<PropertyRow> rows, bool camelCase = false)
    {
        DescribeRows(rows);
        var text = JsonSerializer.Serialize(document, camelCase ? CamelCase : Indented);
        if (Encoding.UTF8.GetByteCount(text) > Snapshot.MaximumFileBytes)
            text = JsonSerializer.Serialize(document, Compact);
        if (Encoding.UTF8.GetByteCount(text) > Snapshot.MaximumFileBytes)
        {
            // Preserve the ability to save an older document at the existing size limit.
            text = JsonSerializer.Serialize(document, EngineClient.Json);
            if (Encoding.UTF8.GetByteCount(text) > Snapshot.MaximumFileBytes)
                throw new InvalidDataException("Saved document exceeds 16 MiB.");
        }
        return text;
    }

    internal static JsonNode? Diagnostic(JsonElement result)
    {
        var root = PropertyResult(result);
        if (root is JsonArray entries)
        {
            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (entry.ContainsKey("sourceNetwork") && entry.ContainsKey("sourceMAC") &&
                    entry["type"] is JsonValue type && type.TryGetValue<string>(out var text))
                    entry["displayType"] = BacnetDisplay.ChoiceValue("apdu-type", text);
                if (entry.ContainsKey("acknowledged") && entry.ContainsKey("priorities") && entry.ContainsKey("timestamps"))
                {
                    var row = entry.Deserialize<EventRow>(EngineClient.Json);
                    if (row != null)
                    {
                        entry["displayState"] = row.DisplayState;
                        entry["displayNotifyType"] = row.DisplayNotifyType;
                        entry["displayAcknowledged"] = row.DisplayAcknowledged;
                        entry["displayPriorities"] = row.PriorityDisplay;
                    }
                }
            }
        }
        if (root is JsonObject range && range.ContainsKey("firstSequence") && range.ContainsKey("raw") &&
            range["flags"] is JsonObject flags)
        {
            var meanings = new Dictionary<string, string>
            {
                ["FirstItem"] = "Includes first item",
                ["LastItem"] = "Includes last item",
                ["MoreItems"] = "More items available"
            };
            var active = meanings.Where(pair => flags[pair.Key] is JsonValue bit && bit.TryGetValue<bool>(out var set) && set)
                .Select(pair => pair.Value).ToArray();
            range["displayFlags"] = active.Length == 0 ? "No result flags set" : string.Join(" · ", active);
        }
        return root;
    }

    internal static JsonNode? PropertyResult(JsonElement result)
    {
        var root = JsonNode.Parse(result.GetRawText());
        var entries = root as JsonArray ?? (root as JsonObject)?["rows"] as JsonArray;
        if (entries == null || entries.Count == 0 ||
            entries.Any(entry => entry is not JsonObject item || !item.ContainsKey("object") || !item.ContainsKey("property")))
            return root;
        var rows = entries.Deserialize<List<PropertyRow>>(EngineClient.Json) ?? [];
        DescribeRows(rows);
        for (var index = 0; index < rows.Count; index++)
        {
            var entry = entries[index]!.AsObject();
            var row = rows[index];
            entry["displayName"] = row.DisplayName;
            entry["displayType"] = row.DisplayType;
            entry["displayValue"] = row.DisplayValue;
            entry["displayError"] = row.DisplayError;
            entry["objectDescription"] = BacnetDisplay.ObjectReference(row.Object);
        }
        return root;
    }
}
