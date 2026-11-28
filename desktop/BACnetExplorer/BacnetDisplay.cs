
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BACnetExplorer;

// Presentation only. Selectors, comparisons and command values retain their original encoding.
internal static class BacnetDisplay
{
    private const string Enumerated = "enumerated:";
    private static readonly Lazy<Dictionary<string, Dictionary<ulong, string>>> Catalog = new(LoadCatalog);
    private static readonly Regex ProtocolReason = new(@"BACnet (?<kind>Reject|Abort) reason (?<code>\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ProtocolError = new(@"BACnet error class (?<class>\d+), code (?<code>\d+)", RegexOptions.CultureInvariant);
    private static readonly string[] Weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    private static readonly Dictionary<string, string[]> FlagNames = new(StringComparer.Ordinal)
    {
        ["status-flags"] = ["In alarm", "Fault", "Overridden", "Out of service"],
        ["limit-enable"] = ["Low limit", "High limit"],
        ["event-enable"] = ["To off-normal", "To fault", "To normal"],
        ["acked-transitions"] = ["To off-normal", "To fault", "To normal"],
        ["ack-required"] = ["To off-normal", "To fault", "To normal"],
        ["days-of-week"] = Weekdays,
        ["valid-days"] = Weekdays
    };

    private static Dictionary<string, Dictionary<ulong, string>> LoadCatalog()
    {
        using var stream = typeof(BacnetDisplay).Assembly.GetManifestResourceStream("Workbench.BacnetLabels")
            ?? throw new InvalidDataException("BACnet display labels are missing.");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<ulong, string>>>(stream)
            ?? throw new InvalidDataException("BACnet display labels are invalid.");
    }

    private static string? Label(string domain, ulong code) =>
        Catalog.Value.TryGetValue(domain, out var names) && names.TryGetValue(code, out var name) ? Humanize(name) : null;

    internal static string Humanize(string value)
    {
        var text = value.Replace('-', ' ').Replace('_', ' ');
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static bool Number(string text, out ulong number) => ulong.TryParse(
        text.StartsWith(Enumerated, StringComparison.Ordinal) ? text[Enumerated.Length..] : text,
        NumberStyles.None, CultureInfo.InvariantCulture, out number);

    internal static string PropertyName(string property) => Number(property, out var code)
        ? Label("property-identifier", code) is { } name ? $"{name} ({code})" : $"Property ID {code}"
        : property.Length == 0 ? "Property" : Humanize(property);

    internal static string ObjectReference(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2 || !Number(parts[0], out var type) || !Number(parts[1], out var instance))
            return ObjectLabels.Display(value);
        return $"{Label("object-type", type) ?? $"Object type {type}"} {instance} ({value})";
    }

    internal static string Error(string error) => ProtocolReason.Replace(ProtocolError.Replace(error, match =>
    {
        if (!Number(match.Groups["class"].Value, out var category) || !Number(match.Groups["code"].Value, out var code))
            return match.Value;
        var reason = Label("error-code", code) ?? "Unknown error";
        var group = Label("error-class", category) ?? "Unknown error class";
        return $"{reason} (code {code}, {group} class {category})";
    }), match =>
    {
        var kind = match.Groups["kind"].Value;
        return $"BACnet {kind}: {ChoiceValue(kind.ToLowerInvariant() + "-reason", match.Groups["code"].Value)}";
    });

    internal static string ChoiceValue(string domain, string value) => Number(value, out var number)
        ? Label(domain, number) is { } label ? $"{label} ({number})" : $"Unknown {Humanize(domain).ToLowerInvariant()} code {number}"
        : value;

    internal static string Value(PropertyRow row, IEnumerable<PropertyRow>? context = null)
    {
        if (row.Error.Length > 0)
            return "";
        if (row.Index == 0 && row.Type == "unsigned")
            return row.Value;
        if (row.Property == "priority-array" && row.Index == null && row.Elements.Length == 16)
            return string.Join("; ", row.Elements.Select((value, index) => $"P{index + 1}={Priority(row, index, context)}"));
        if (row.ArrayValues.Length > 0)
        {
            var shown = string.Join("; ", row.ArrayValues.Take(3).Select((value, index) =>
                Scalar(row, value, index < row.ArrayTypes.Length ? row.ArrayTypes[index] : "", context)));
            return $"{row.ArrayValues.Length} items · {shown}" + (row.ArrayValues.Length > 3 ? " · …" : "");
        }
        if (row.Property == "object-identifier" && row.Type == "object-identifier")
            return (PropertyValueEditor.IsDevice(row) ? "Device " : "Instance ") + row.Value.Split(':').Last();
        var text = Scalar(row, row.Value, row.Type, context, row.EditorValue);
        if (row.Property is "present-value" or "relinquish-default" &&
            Text(context, "units") is { } unit)
            return WithUnit(text, unit);
        return text;
    }

    internal static string WithUnit(string value, string unit)
    {
        if (unit.Length == 0 || unit is "no-units" or "No units" ||
            unit.StartsWith("No units (", StringComparison.OrdinalIgnoreCase) ||
            unit.StartsWith("Unknown ", StringComparison.Ordinal) || unit == "Structured data" ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number))
            return value;
        var label = Number(unit, out var code) ? Label("units", code) : Humanize(unit);
        return label == null ? value : $"{value} · {label}";
    }

    internal static string Priority(PropertyRow row, int index, IEnumerable<PropertyRow>? context = null)
    {
        var value = row.Elements[index];
        if (value == "null")
            return "Released (NULL)";
        return Scalar(row, value, index < row.ArrayTypes.Length ? row.ArrayTypes[index] : "", context);
    }

    private static string Scalar(PropertyRow row, string value, string type, IEnumerable<PropertyRow>? context, string? editor = null)
    {
        if (value.Length == 0)
            return "";
        if (type == "boolean" && value is "true" or "false")
            return row.Property == "out-of-service" ? value == "true" ? "Out of service" : "In service" : value == "true" ? "Yes" : "No";
        if (row.Property == "units" && value is not ("null" or "<empty>"))
        {
            if (Number(value, out var code))
                return ChoiceValue("units", value);
            if (!value.StartsWith("raw:", StringComparison.Ordinal) && !value.StartsWith("json:", StringComparison.Ordinal))
                return Humanize(value) + (editor != null && Number(editor, out var number) ? $" ({number})" : "");
        }
        if (type == "object-identifier")
            return ObjectReference(value);
        if (row.Property == "vendor-identifier" && Number(value, out var vendor))
            return Text(context, "vendor-name") is { } vendorName ? $"{vendorName} ({vendor})" : $"Vendor ID {vendor}";
        var domain = Domain(row);
        if ((type == "enumerated" || value.StartsWith(Enumerated, StringComparison.Ordinal) || domain == "multistate" ||
             domain == "property-identifier" || row.Property == "priority-for-writing") && Number(value, out var enumCode))
            return Choice(row, domain, enumCode, context);
        if (type == "bit-string" || value.StartsWith("bits:", StringComparison.Ordinal))
            return Flags(row.Property, editor ?? value);
        if (type == "date" && editor != null)
        {
            var parts = editor.Split('|', 2);
            return parts.Length == 2 && Number(parts[1], out var day) && day is >= 1 and <= 7
                ? $"{parts[0]} · {FlagNames["days-of-week"][(int)day - 1]}" : parts[0];
        }
        if (type == "time" && editor != null)
            return editor;
        if (type == "octet-string")
        {
            var octets = value.StartsWith("octets:", StringComparison.Ordinal) ? value[7..] : value;
            if (row.Property is "ip-address" or "ip-subnet-mask" or "ip-default-gateway" && octets.Length == 8)
            {
                try
                {
                    return new IPAddress(Convert.FromHexString(octets)).ToString();
                }
                catch (FormatException) { }
            }
            return octets;
        }
        if (value.StartsWith("raw:", StringComparison.Ordinal) || value.StartsWith("json:", StringComparison.Ordinal))
            return "Structured data";
        if (type == "character-string" && value.StartsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? "";
            }
            catch (JsonException) { }
        }
        return value;
    }

    private static string Domain(PropertyRow row)
    {
        var type = row.Object.Split(':', 2)[0];
        if (row.Property is "present-value" or "relinquish-default" or "priority-array" or "alarm-value" or "alarm-values"
            or "fault-values" or "feedback-value" or "tracking-value")
            return type switch
            {
                "3" or "4" or "5" or "55" => "binary-pv",
                "13" or "14" or "19" => "multistate",
                "21" or "22" => "life-safety-state",
                "28" => "shed-state",
                "30" => "door-value",
                _ => row.Property
            };
        return row.Property switch
        {
            "mode" or "accepted-modes" when type is "21" or "22" => "life-safety-mode",
            "operation-expected" when type is "21" or "22" => "life-safety-operation",
            "in-progress" when type == "54" => "lighting-in-progress",
            "command" when type == "56" => "network-port-command",
            "sc-direct-connect-connection-status" or "sc-failover-hub-connection-status" or
                "sc-hub-function-connection-status" => "sc-primary-hub-connection-status",
            "property-list" => "property-identifier",
            _ => row.Property
        };
    }

    private static string? Text(IEnumerable<PropertyRow>? context, string property)
    {
        var row = context?.FirstOrDefault(item => item.Property == property && item.Index == null && item.Error.Length == 0);
        var text = row?.DisplayValue;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string[] States(IEnumerable<PropertyRow>? context) =>
        context?.FirstOrDefault(item => item.Property == "state-text" && item.Index == null && item.Error.Length == 0)?.ArrayValues ?? [];

    private static string Choice(PropertyRow row, string domain, ulong number, IEnumerable<PropertyRow>? context)
    {
        if (domain == "binary-pv" && number <= 1)
            return $"{(number == 0 ? Text(context, "inactive-text") ?? "Inactive" : Text(context, "active-text") ?? "Active")} ({number})";
        if (domain == "multistate")
        {
            var states = States(context);
            return number > 0 && number <= (ulong)states.Length && !string.IsNullOrWhiteSpace(states[(int)number - 1])
                ? $"{states[(int)number - 1]} ({number})" : $"State {number}";
        }
        if (row.Property == "priority-for-writing")
            return $"Priority {number}" + (number == 6 ? " (reserved)" : "");
        if (Label(domain, number) is { } label)
            return $"{label} ({number})";
        return row.Property.Length > 0 ? $"Unknown {Humanize(row.Property).ToLowerInvariant()} code {number}" : $"Unknown choice ({number})";
    }

    internal static IReadOnlyList<PropertyChoice> Choices(PropertyRow row, IEnumerable<PropertyRow> context)
    {
        if (row.Type == "boolean")
            return row.Property == "out-of-service" ? [new("false", "In service"), new("true", "Out of service")]
                : [new("false", "No"), new("true", "Yes")];
        if (row.Index == 0)
            return [];
        if (row.Property == "priority-for-writing")
            return Enumerable.Range(1, 16).Where(number => number != 6).Select(number =>
                new PropertyChoice(number.ToString(CultureInfo.InvariantCulture), $"Priority {number}")).ToArray();
        var domain = Domain(row);
        // Units already use the engine's dedicated editor choices, including site-specific units.
        if (row.Property == "units" || row.Type != "enumerated" && domain != "multistate")
            return [];
        if (domain == "binary-pv")
            return [new("0", Choice(row, domain, 0, context)), new("1", Choice(row, domain, 1, context))];
        if (domain == "multistate")
            return States(context).Take(1024).Select((_, i) =>
                new PropertyChoice((i + 1).ToString(CultureInfo.InvariantCulture), Choice(row, domain, (ulong)i + 1, context))).ToArray();
        return Catalog.Value.TryGetValue(domain, out var values) ? values.Keys.Order().Select(number =>
            new PropertyChoice(number.ToString(CultureInfo.InvariantCulture), Choice(row, domain, number, context))).ToArray() : [];
    }

    internal static string[] FlagsFor(string property) => FlagNames.TryGetValue(property, out var names) ? names : [];

    internal static string Flags(string property, string value)
    {
        if (value.StartsWith("bits:", StringComparison.Ordinal))
        {
            var parts = value.Split(':', 3);
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count > 4096 || parts[2].Length != (count + 7) / 8 * 2)
                return "Bit string";
            try
            {
                var bytes = Convert.FromHexString(parts[2]);
                value = new string(Enumerable.Range(0, count).Select(index =>
                    (bytes[index / 8] & (0x80 >> (index % 8))) == 0 ? '0' : '1').ToArray());
            }
            catch (FormatException) { return "Unrecognized bit string"; }
        }
        if (value.Length == 0 || value.Any(bit => bit is not ('0' or '1')))
            return "Unrecognized bit string";
        var names = FlagsFor(property);
        var set = Enumerable.Range(0, value.Length).Where(index => value[index] == '1').Select(index =>
            index < names.Length ? names[index] : property switch
            {
                "protocol-object-types-supported" => Label("object-type", (ulong)index) ?? $"Object type {index}",
                "protocol-services-supported" => Label("service-supported", (ulong)index) ?? $"Service {index}",
                _ => $"Bit {index + 1}"
            }).ToArray();
        return $"{(set.Length == 0 ? "No flags set" : string.Join(", ", set))} ({value})";
    }
}
