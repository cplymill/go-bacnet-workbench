using System.Globalization;

namespace BACnetExplorer;

// UI abbreviations only. The engine continues to receive numeric identifiers.
public static class ObjectLabels
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0] = "AI", [1] = "AO", [2] = "AV",
        [3] = "BI", [4] = "BO", [5] = "BV",
        [6] = "CAL", [7] = "CMD", [8] = "DEV", [9] = "EE",
        [10] = "FILE", [11] = "GRP", [12] = "LOOP",
        [13] = "MSI", [14] = "MSO", [15] = "NC", [16] = "PRG",
        [17] = "SCH", [18] = "AVG", [19] = "MSV", [20] = "TL",
        [21] = "LSP", [22] = "LSZ", [23] = "ACC", [24] = "PC",
        [25] = "EL", [26] = "GG", [27] = "TLM", [28] = "LC",
        [29] = "SV", [30] = "AD", [31] = "TMR",
        [32] = "AC", [33] = "AP", [34] = "AR", [35] = "AU",
        [36] = "AZ", [37] = "CDI", [39] = "BSV", [40] = "CSV",
        [41] = "DPV", [42] = "DV", [43] = "DTPV", [44] = "DTV",
        [45] = "IV", [46] = "LAV", [47] = "OSV", [48] = "PIV",
        [49] = "TPV", [50] = "TV", [51] = "NF", [52] = "AE",
        [53] = "CH", [54] = "LO", [55] = "BLO", [56] = "NP",
        [57] = "EG", [58] = "ESC", [59] = "LIFT", [60] = "STG",
        [61] = "AL", [62] = "ARP"
    };

    public static string Display(string numeric)
    {
        var parts = numeric.Split(':');
        return parts.Length == 2 && uint.TryParse(parts[0], out var type) && Names.TryGetValue(type, out var name)
            ? name + ":" + parts[1] : numeric;
    }

    public static string Numeric(string label)
    {
        var parts = label.Trim().Split(':');
        if (parts.Length != 2 || !uint.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var instance) || instance > 4_194_303)
            throw new FormatException("Enter an object ID such as BI:6, AV:1 or 3:6. The instance must be 0–4194303.");
        var typeText = parts[0].Trim();
        if (!uint.TryParse(typeText, NumberStyles.None, CultureInfo.InvariantCulture, out var type))
        {
            var match = Names.FirstOrDefault(item => item.Value.Equals(typeText, StringComparison.OrdinalIgnoreCase));
            if (match.Value == null) throw new FormatException("Unknown object abbreviation. Use a label such as BI:6 or a numeric type:instance.");
            type = match.Key;
        }
        if (type > 1023) throw new FormatException("The numeric object type must be 0–1023.");
        return type.ToString(CultureInfo.InvariantCulture) + ":" + instance.ToString(CultureInfo.InvariantCulture);
    }
}
