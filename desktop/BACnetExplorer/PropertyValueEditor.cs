using System.Globalization;
using System.Net;
using System.Text.Json;

namespace BACnetExplorer;

public sealed record PropertyChoice(string Value, string Label);
public sealed class PropertyArrayItem
{
    public int Index
    {
        get; set;
    }
    public string Value { get; set; } = "";
}
public sealed class PropertyFlagItem
{
    public string Label { get; set; } = "";
    public bool Enabled
    {
        get; set;
    }
}

public static class PropertyValueEditor
{
    public static string Kind(PropertyRow row) => row.EditorType.Length > 0 ? row.EditorType : row.Type;
    public static bool IsDevice(PropertyRow row) => row.Object.StartsWith("8:", StringComparison.Ordinal);
    public static string Name(PropertyRow row) => row.Property switch
    {
        "object-name" => IsDevice(row) ? "Device name" : "Object name",
        "object-identifier" => IsDevice(row) ? "Device instance number" : "Object instance number",
        "apdu-timeout" => "Response timeout (milliseconds)",
        "apdu-segment-timeout" => "Segment timeout (milliseconds)",
        "number-of-apdu-retries" => "Retry count",
        "max-master" => "Highest MS/TP manager MAC",
        "max-info-frames" => "Maximum frames per token",
        "utc-offset" => "UTC offset (minutes)",
        _ => BacnetDisplay.PropertyName(row.Property)
    };
    public static string DisplayType(PropertyRow row) => row.Property == "object-identifier" ? "Instance number" : row.ArrayValues.Length > 0 ? "List" : row.Type switch
    {
        "character-string" => "Text",
        "object-identifier" => "Object reference",
        "unsigned" or "signed" => "Whole number",
        "real" or "double" => "Number",
        "boolean" => "Yes / No",
        "enumerated" => "Choice",
        "bit-string" => "Flags",
        "date" => "Date",
        "time" => "Time",
        "octet-string" => "Bytes",
        _ => row.Type
    };
    private static bool IPv4(PropertyRow row) => row.Type == "octet-string" && row.Property is "ip-address" or "ip-subnet-mask" or "ip-default-gateway";
    public static string EditText(PropertyRow row)
    {
        var value = row.EditorValue ?? row.Type switch
        {
            "enumerated" when row.Value.StartsWith("enumerated:", StringComparison.Ordinal) => row.Value[11..],
            "octet-string" when row.Value.StartsWith("octets:", StringComparison.Ordinal) => row.Value[7..],
            "character-string" => BacnetDisplay.Value(row),
            _ => row.Value
        };
        if (row.Property == "object-identifier")
            return (row.EditorValue ?? row.Value).Split(':').Last();
        if (row.Type == "object-identifier")
            return ObjectLabels.Display(row.EditorValue ?? row.Value);
        if (row.Type == "date")
            return value.Split('|')[0];
        if (IPv4(row) && value.Length == 8)
        {
            try
            {
                return new IPAddress(Convert.FromHexString(value)).ToString();
            }
            catch (FormatException) { }
        }
        return value;
    }
    public static string Hint(PropertyRow row) => row.Property switch
    {
        "object-identifier" => "Enter an instance number from 0 to 4194302. The object type stays the same.",
        "object-name" => IsDevice(row) ? "Enter the device name as plain text. It must be unique on the BACnet internetwork." : "Enter a name unique within this device. Quotes are not needed.",
        "priority-for-writing" => "Choose an operator priority from 1 to 16, excluding reserved priority 6.",
        _ when row.ArrayValues.Length > 0 => "Edit each item's value below. The existing item types and item count are preserved.",
        _ when row.Type == "date" => "Choose a date or use YYYY-MM-DD. * means any; odd/even months and last/odd/even days are supported. Weekday is separate.",
        _ when row.Type == "time" => "Enter HH:mm:ss.ff, such as 08:30:00.00. A component can be * when unspecified.",
        _ when row.Type == "bit-string" => "Select the flags that should be enabled.",
        _ when IPv4(row) => "Enter an IPv4 address, for example 192.168.1.20.",
        _ when row.Type == "object-identifier" => "Enter an object reference such as BI:6 or AV:1.",
        _ when row.Type == "character-string" => "Enter plain text. Quotes are not needed.",
        _ => "Edit the value, then Save change. The device validates the request."
    };
    public static IReadOnlyList<PropertyChoice> Choices(PropertyRow row, IEnumerable<PropertyRow> context) =>
        BacnetDisplay.Choices(row, context.Where(item => item.Device == row.Device && item.Object == row.Object));

    public static string WireType(PropertyRow row) => row.ArrayValues.Length > 0 ? "primitive-array-json" : Kind(row) is "boolean" or "unsigned" or "signed" or "real" or "double" or "enumerated" or "character-string" or "object-identifier" or "octet-string" or "date" or "time" or "bit-string" ? Kind(row) : "raw";
    public static string WireValue(PropertyRow row, string text, string? weekday = null, IReadOnlyList<string>? arrayValues = null)
    {
        if (row.ArrayValues.Length > 0)
        {
            if (arrayValues == null || arrayValues.Count != row.ArrayTypes.Length)
                throw new InvalidOperationException("Keep the existing number of list items; change the array size separately if supported.");
            return JsonSerializer.Serialize(arrayValues.Select((value, index) => row.ArrayTypes[index] == "object-identifier" ? ObjectLabels.Numeric(value) : value));
        }
        if (row.Property == "object-identifier")
        {
            if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var instance) || instance >= 4_194_303)
                throw new InvalidOperationException("Enter an instance number from 0 to 4194302.");
            return row.Object.Split(':')[0] + ":" + instance.ToString(CultureInfo.InvariantCulture);
        }
        if (row.Type == "object-identifier")
            return ObjectLabels.Numeric(text);
        if (row.Property == "object-name" && (text.Length == 0 || text.Any(char.IsControl)))
            throw new InvalidOperationException("The name must contain printable text and cannot be empty.");
        if (row.Property == "priority-for-writing")
        {
            if (!int.TryParse(text, out var priority) || priority == 0)
                throw new InvalidOperationException("Choose a priority from 1 to 16, excluding 6.");
            PropertyEditPolicy.ValidatePriority(priority);
        }
        if (row.Type == "date")
            return text + "|" + (weekday ?? "255");
        if (IPv4(row))
        {
            if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("Enter a valid IPv4 address.");
            return Convert.ToHexString(address.GetAddressBytes()).ToLowerInvariant();
        }
        if (row.Type == "unsigned" && !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("Enter a non-negative whole number.");
        if (row.Type == "signed" && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("Enter a whole number.");
        if (row.Type is "real" or "double" && (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)))
            throw new InvalidOperationException("Enter a finite numeric value.");
        if (row.Type == "enumerated" && row.Property != "units" && !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("Choose a value from the list or enter its numeric code.");
        return text;
    }
}
