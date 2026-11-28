
using System.Text.Json;
using BACnetExplorer;

internal static class DisplayChecks
{
    public static void Run()
    {
        static PropertyRow Row(string property, string type, string value, string id = "2:21") =>
            new()
            {
                Device = "device-binding",
                Object = id,
                Property = property,
                Type = type,
                Value = value,
                Raw = "910c"
            };

        var reliability = Row("reliability", "enumerated", "enumerated:12");
        Check(reliability.DisplayValue == "Communication Failure (12)", "Reliability code 12 is unnamed.");
        Check(Row("reliability", "enumerated", "enumerated:11").DisplayValue == "Unknown reliability code 11",
            "Reserved Reliability code was treated as an assigned choice.");
        Check(Row("status-flags", "bit-string", "bits:4:40").DisplayValue == "Fault (0100)", "Wrong order for status flags.");
        Check(Row("status-flags", "bit-string", "bits:4:00").DisplayValue == "No flags set (0000)", "Empty status flags are unclear.");
        Check(Row("object-type", "enumerated", "enumerated:2").DisplayValue == "Analog value (2)", "Object type is abbreviated.");
        Check(Row("units", "enumerated", "degrees-celsius").DisplayValue == "Degrees celsius", "Named units are unreadable.");
        var error = Row("present-value", "", "");
        error.Error = "BACnet error class 2, code 32";
        Check(error.DisplayError.Contains("Unknown property", StringComparison.Ordinal) && error.DisplayError.Contains("code 32", StringComparison.Ordinal),
            "Property error lost its name or protocol code.");

        var priority = Row("priority-array", "null", "", "5:1");
        priority.Elements = Enumerable.Repeat("null", 16).ToArray();
        priority.Elements[7] = "enumerated:1";
        var binary = Row("present-value", "enumerated", "enumerated:1", "5:1");
        var active = Row("active-text", "character-string", "\"Fan running\"", "5:1");
        var context = new[] { priority, binary, active };
        PropertyEditPolicy.Apply(context);
        Check(binary.DisplayValue == "Fan running (1)" && priority.DisplayValue.Contains("P8=Fan running (1)", StringComparison.Ordinal),
            "Device-specific binary text is missing from current or priority values.");
        Check(PropertyValueEditor.EditText(binary) == "1" && PropertyValueEditor.EditText(reliability) == "12",
            "Display text leaked into a write editor.");
        Check(PropertyValueEditor.Choices(reliability, [reliability]).All(choice => choice.Value != "11"),
            "Reserved reliability code was offered for writing.");

        var multi = Row("present-value", "unsigned", "2", "19:7");
        var states = Row("state-text", "character-string", "", "19:7");
        states.ArrayValues = ["Off", "High"];
        var indexLength = Row("property-list", "unsigned", "2");
        indexLength.Index = 0;
        Check(indexLength.DisplayValue == "2", "Array size was interpreted as a property ID.");
        var workspace = new Workspace { Baseline = [reliability, error, multi, states] };
        var saved = WorkbenchExport.SaveDocument(workspace, workspace.Baseline);
        using var json = JsonDocument.Parse(saved);
        var rows = json.RootElement.GetProperty("Baseline");
        Check(rows[0].GetProperty("Value").GetString() == "enumerated:12" &&
            rows[0].GetProperty("DisplayValue").GetString() == "Communication Failure (12)"
            && rows[2].GetProperty("DisplayValue").GetString() == "High (2)", "Saved JSON lost raw fields or readable labels.");
        var loaded = JsonSerializer.Deserialize<Workspace>(saved, EngineClient.Json)!;
        Check(loaded.Version == 1 && loaded.Baseline[0].Raw == reliability.Raw && loaded.Baseline[0].Object == "2:21"
            && loaded.Baseline[2].Value == "2", "Labeled workspace cannot be loaded with existing selectors and raw observations.");
        var legacy = JsonSerializer.Serialize(workspace, EngineClient.Json);
        Check(JsonSerializer.Deserialize<Workspace>(legacy, EngineClient.Json)!.Baseline[0].DisplayValue == reliability.DisplayValue,
            "Legacy workspace requires a new display field.");
        using var wire = JsonDocument.Parse("""{"rpm":1,"rows":[{"device":"device","object":"2:21","property":"reliability","type":"enumerated","value":"enumerated:12","raw":"910c"}]}""");
        var result = WorkbenchExport.PropertyResult(wire.RootElement)!.AsObject();
        Check(result["rpm"]!.GetValue<int>() == 1 && result["rows"]![0]!["raw"]!.GetValue<string>() == "910c" &&
            result["rows"]![0]!["displayValue"]!.GetValue<string>() == "Communication Failure (12)", "Database export did not retain data with labels.");
        var watch = new Watch();
        watch.Observe(reliability, "Poll");
        Check(watch.Value == reliability.DisplayValue && watch.Samples[0].Value == reliability.Value,
            "Watch labels replaced stored values.");
        Console.WriteLine("PASS: BACnet labels resolve known values, flags and errors while preserving raw values in editors and saved/exported files.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
