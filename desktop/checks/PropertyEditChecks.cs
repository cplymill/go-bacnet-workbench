using BACnetExplorer;

internal static class PropertyEditChecks
{
    internal static void Run()
    {
        static PropertyRow Row(string id, string property, string type = "enumerated", string value = "enumerated:1", string device = "peer-A", uint? index = null) => new() { Device = device, Object = id, Property = property, Type = type, Value = value, Raw = "9101", Index = index };
        static PropertyRow Priorities(string id, string device = "peer-A", int count = 16) => new() { Device = device, Object = id, Property = "priority-array", Type = "null", Value = "null", Raw = "00", Elements = Enumerable.Repeat("null", count).ToArray() };
        static PropertyEditAccess Access(PropertyRow row, params PropertyRow[] context) => PropertyEditPolicy.Assess(row, context.Prepend(row));
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        Check(!new PropertyRow { Raw = "9101" }.CanEdit, "Readable must not imply editable.");
        foreach (var property in new[] { "object-type", "property-list", "priority-array", "current-command-priority" })
        {
            var access = Access(Row("4:1", property));
            Check(!access.CanEdit && !access.CanAttempt, property + " must not expose a direct write.");
        }
        foreach (var property in new[] { "firmware-revision", "protocol-revision", "database-revision", "last-restart-reason" })
            Check(!Access(Row("8:1", property)).CanAttempt, property + " must remain read only.");
        foreach (var property in new[] { "object-name", "description", "units", "out-of-service", "600" })
        {
            var access = Access(Row("2:1", property));
            Check(access.CanEdit, property + " must offer a direct editor where not prohibited.");
        }
        foreach (var id in new[] { "0:1", "3:1", "13:1" })
        {
            var input = Row(id, "present-value");
            Check(!Access(input).CanEdit && !Access(input).CanAttempt, "Input requires observed out-of-service state.");
            Check(!Access(input, Row(id, "out-of-service", "boolean", "false")).CanEdit, "In-service input must not be overridden.");
            Check(Access(input, Row(id, "out-of-service", "boolean", "true")).CanEdit, "Out-of-service input override is available.");
            Check(!Access(input, Row(id, "out-of-service", "boolean", "true", "peer-B")).CanEdit, "Other peer state must not authorize a write.");
            Check(!Access(input, Priorities(id)).CanEdit, "A stray array must not bypass input OOS rules.");
        }
        var output = Row("4:1", "present-value");
        Check(Access(output, Priorities("4:1")).CanEdit, "Observed commandable output must be available.");
        Check(!Access(output, Priorities("4:2")).Commandable, "Other object's priorities must not authorize a write.");
        Check(!Access(output, Priorities("4:1", "peer-B")).Commandable, "Other peer's priorities must not authorize a write.");
        Check(!Access(output, Priorities("4:1", count: 15)).Commandable, "Incomplete priority array must not imply commandable.");
        Check(Access(Row("4:1", "relinquish-default"), Priorities("4:1")).CanEdit, "Command default can request a change.");
        var stringValue = Row("40:62", "present-value", "character-string", "\"Text\"");
        Check(Access(stringValue).CanEdit, "Writable-by-implementation values remain explicit attempts.");
        Check(Access(Row("17:1", "weekly-schedule")).UseEditor, "Weekly schedule uses visual editor.");
        Check(Access(Row("17:1", "exception-schedule")).UseEditor, "Exceptions use visual editor.");
        Check(Access(Row("17:1", "schedule-default")).CanEdit, "Schedule default also has a direct value editor without a datatype choice.");
        Check(Access(Row("6:1", "date-list")).UseEditor, "Calendar uses visual editor.");
        Check(!Access(Row("6:1", "present-value")).CanEdit, "Calendar PV is calculated.");
        Check(!Access(Row("4:1", "priority-array", index: 1)).CanAttempt, "Priority-array elements cannot be written directly.");
        Check(!Access(Row("4:1", "priority-array", index: 0)).CanAttempt, "Priority-array length cannot be written.");
        Check(Access(Row("2:1", "state-text", index: 0)).CanEdit, "A supported array size can be edited where resizing is allowed.");
        var failed = Row("4:1", "present-value"); failed.Error = "Read denied";
        Check(!Access(failed, Priorities("4:1")).CanEdit && !Access(failed).CanAttempt, "Read error disables write affordances.");
        var batchInput = Row("3:6", "present-value");
        var otherInput = Row("3:6", "present-value", device: "peer-B");
        PropertyEditPolicy.Apply([batchInput, otherInput, Row("3:6", "out-of-service", "boolean", "true")]);
        Check(batchInput.CanEdit && !otherInput.CanEdit, "Batch policy must preserve per-peer state isolation.");
        static void Rejected(Action attempt)
        {
            try { attempt(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Write preflight unexpectedly allowed the request.");
        }
        var name = Row("8:1", "object-name", "character-string", "\"Name\"");
        Check(Access(name).CanEdit, "Device name must offer a direct text editor.");
        var identifier = Row("8:1", "object-identifier", "object-identifier", "8:1");
        Check(Access(identifier).CanEdit, "Device identifier must offer a direct instance editor.");
        Check(PropertyValueEditor.Name(identifier) == "Device instance number", "Device identifier label must be meaningful.");
        Check(PropertyValueEditor.EditText(identifier) == "1", "Show only the editable instance number.");
        Check(PropertyValueEditor.WireValue(identifier, "1001") == "8:1001", "Keep Device type fixed while changing the instance.");
        Rejected(() => PropertyValueEditor.WireValue(identifier, "4194303"));
        Rejected(() => PropertyValueEditor.WireValue(name, ""));
        Rejected(() => PropertyValueEditor.WireValue(name, "bad\nname"));
        Check(!Access(Row("8:1", "vendor-identifier")).CanEdit, "Device Vendor Identifier is explicitly read-only.");
        Check(!Access(Row("8:1", "model-name")).CanEdit, "Device Model Name is explicitly read-only.");
        Check(!Access(Row("43:1", "object-name")).CanEdit, "Honor object-specific read-only definitions, not a global name rule.");
        var labels = Row("19:1", "state-text", "raw", "raw:encoded"); labels.ArrayValues = ["Off", "On"]; labels.ArrayTypes = ["character-string", "character-string"];
        Check(Access(labels).CanEdit && PropertyValueEditor.WireType(labels) == "primitive-array-json", "State labels have a typed list editor.");
        Check(PropertyValueEditor.WireValue(labels, "", arrayValues: ["Stopped", "Running"]) == "[\"Stopped\",\"Running\"]", "Send list text without BACnet tag hex.");
        Rejected(() => PropertyValueEditor.WireValue(labels, "", arrayValues: ["Wrong count"]));
        PropertyEditPolicy.RequireWrite(name, [name], 0, false, false);
        PropertyEditPolicy.RequireWrite(name, [name], 0, true, false);
        var immutable = Row("8:1", "object-type");
        Rejected(() => PropertyEditPolicy.RequireWrite(immutable, [immutable], 0, true, false));
        PropertyEditPolicy.RequireWrite(output, [output, Priorities("4:1")], 8, false, false);
        PropertyEditPolicy.RequireWrite(output, [output, Priorities("4:1")], 8, false, true);
        Rejected(() => PropertyEditPolicy.RequireWrite(output, [output], 8, false, true));
        Rejected(() => PropertyEditPolicy.RequireWrite(output, [output, Priorities("4:1")], 0, false, true));
        Rejected(() => PropertyEditPolicy.RequireWrite(name, [name], 8, true, false));
        var inputToRecheck = Row("3:6", "present-value");
        PropertyEditPolicy.RequireWrite(inputToRecheck, [inputToRecheck, Row("3:6", "out-of-service", "boolean", "true")], 0, false, false);
        Rejected(() => PropertyEditPolicy.RequireWrite(inputToRecheck, [inputToRecheck, Row("3:6", "out-of-service", "boolean", "false")], 0, true, false));
        Rejected(() => PropertyEditPolicy.FileUploadRecordMode("peer-A", "10:1", [Row("10:1", "read-only", "boolean", "true")]));
        Check(PropertyEditPolicy.FileUploadRecordMode("peer-A", "10:1", [Row("10:1", "file-access-method", "enumerated", "enumerated:0")]) == true, "File metadata 0 means record access, not the service CHOICE index.");
        Check(PropertyEditPolicy.FileUploadRecordMode("peer-A", "10:1", [Row("10:1", "file-access-method", "enumerated", "enumerated:1")]) == false, "File metadata 1 means stream access.");
        Check(PropertyEditPolicy.FileUploadRecordMode("peer-A", "10:1", [Row("10:1", "read-only", "boolean", "true", "peer-B")]) == null, "Other peer file flags cannot establish this file's mode or rights.");
        foreach (var priority in new[] { -1, 6, 17 })
        {
            try { PropertyEditPolicy.ValidatePriority(priority); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("Invalid/reserved priority accepted.");
        }
        foreach (var priority in new[] { 0, 1, 5, 7, 16 }) PropertyEditPolicy.ValidatePriority(priority);
        Console.WriteLine("PASS: property access audit matrix (readability, object/peer isolation, OOS, commandability, dedicated editors, advanced attempts reserved priority and file access).");
    }
}
