namespace BACnetExplorer;

public sealed record PropertyEditAccess(string Status, string Reason, bool CanEdit = false, bool CanAttempt = false, bool UseEditor = false, bool Commandable = false);

// Desktop affordances, not a remote-device permission oracle. R/O properties
// can be implementation-writable. CanEdit offers an editor, not a permission guarantee.
public static class PropertyEditPolicy
{
    private static readonly HashSet<string> DeviceReadOnly = new(StringComparer.Ordinal)
    {
        "firmware-revision", "protocol-version", "protocol-revision",
        "protocol-object-types-supported", "database-revision", "active-vt-sessions",
        "active-cov-multiple-subscriptions", "last-restart-reason", "time-of-device-restart",
        "last-restore-time", "backup-and-restore-state"
    };
    public static bool? FileUploadRecordMode(string device, string id, IEnumerable<PropertyRow> rows)
    {
        var observed = rows.Where(row => row.Device == device && row.Object == id && row.Error.Length == 0).ToList();
        if (observed.Any(row => row.Property == "read-only" && row.Value == "true"))
            throw new InvalidOperationException("The device reports this file as read-only. Upload is unavailable.");
        // BACnetFileAccessMethod property values: record=0, stream=1.
        // These differ from the AtomicFile service CHOICE indexes.
        return observed.FirstOrDefault(row => row.Property == "file-access-method")?.Value switch
        {
            "enumerated:0" => true,
            "enumerated:1" => false,
            _ => null
        };
    }
    public static void ValidatePriority(int priority)
    {
        if (priority is < 0 or > 16) throw new InvalidOperationException("Priority must be omitted (0) or 1–16.");
        if (priority == 6) throw new InvalidOperationException("Priority 6 is reserved for the device's minimum on/off algorithm.");
    }
    public static PropertyEditAccess RequireWrite(PropertyRow row, IEnumerable<PropertyRow> context, int priority, bool advancedAttempt, bool relinquish)
    {
        ValidatePriority(priority);
        var access = Assess(row, context);
        if (relinquish && (priority == 0 || !access.Commandable || row.Property != "present-value" || row.Index.HasValue))
            throw new InvalidOperationException("Relinquishment requires a commandable Present Value and priority 1–16, excluding 6.");
        if (!access.CanEdit && !(relinquish && access.Commandable) && !(advancedAttempt && access.CanAttempt && !relinquish))
            throw new InvalidOperationException(access.Status + ". " + access.Reason);
        if (!access.Commandable && priority != 0)
            throw new InvalidOperationException("A command priority applies only to a commandable Present Value. Read and select the target property again.");
        return access;
    }
    public static bool HasSimpleEditor(PropertyRow row) => row.ArrayValues.Length > 0 ||
        ((PropertyValueEditor.Kind(row) is "boolean" or "unsigned" or "signed" or "real" or "double" or "enumerated" or "character-string" or "object-identifier" or "octet-string" or "date" or "time" or "bit-string") &&
        (row.EditorValue != null || !row.Value.StartsWith("raw:", StringComparison.Ordinal) && !row.Value.StartsWith("json:", StringComparison.Ordinal)));


    public static void Apply(IEnumerable<PropertyRow> rows)
    {
        foreach (var group in rows.GroupBy(row => (row.Device, row.Object)))
        {
            var commandable = group.Any(row => row.Error.Length == 0 && row.Property == "priority-array" && row.Index == null && row.Elements.Length == 16);
            var outOfService = group.Any(row => row.Error.Length == 0 && row.Property == "out-of-service" && row.Index == null && row.Value == "true");
            foreach (var row in group)
            {
                row.EditAccess = Assess(row, commandable, outOfService);
                row.FriendlyValue = null;
                var current = row.EditorValue ?? (row.Type == "enumerated" ? row.Value.Replace("enumerated:", "", StringComparison.Ordinal) : row.Value);
                row.FriendlyValue = PropertyValueEditor.Choices(row, group).FirstOrDefault(choice => choice.Value == current)?.Label;
            }
        }
    }
    public static PropertyEditAccess Assess(PropertyRow row, IEnumerable<PropertyRow> context)
    {
        var peers = context.Where(item => item.Device == row.Device && item.Object == row.Object && item.Error.Length == 0).ToList();
        var commandable = peers.Any(item => item.Property == "priority-array" && item.Index == null && item.Elements.Length == 16);
        var outOfService = peers.Any(item => item.Property == "out-of-service" && item.Index == null && item.Value == "true");
        return Assess(row, commandable, outOfService);
    }
    private static PropertyEditAccess Assess(PropertyRow row, bool commandable, bool outOfService)
    {
        if (row.Error.Length > 0 || row.Raw.Length == 0)
            return new("Read unavailable", "Read this property successfully before requesting a change.");
        var typeText = row.Object.Split(':')[0];
        if (!uint.TryParse(typeText, out var type)) return new("Unknown object", "Read the object's numeric identity first.");
        if (row.Property is "object-type" or "property-list" or "priority-array" or "current-command-priority")
            return new("Read only", row.Property == "priority-array" ? "Priority slots are changed by commanding Present Value at a priority, not by writing this array." : "This is object identity, property inventory or derived command state; no direct write is offered.");
        if (PropertyReadOnlyRules.Contains(type, row.Property) || (type == 8 && DeviceReadOnly.Contains(row.Property)))
            return new("Read only", "The object's property definition prohibits a direct write.");
        if (type == 6 && row.Property == "present-value")
            return new("Calculated", "Calendar Present Value is calculated from its date list. Use Calendar view to edit dates.");
        if (type == 17 && row.Property == "schedule-default" && HasSimpleEditor(row))
            return new("Editable", "Edit the default value using its existing schedule datatype.", CanEdit: true);
        if ((type == 6 && row.Property == "date-list") ||
            (type == 17 && row.Property is "weekly-schedule" or "exception-schedule" or "effective-period" or "schedule-default"))
            return new("Visual editor", "Use the Schedule or Calendar editor. Local edits are allowed; the device decides whether a requested save is accepted.", UseEditor: true);
        if (row.Index == 0)
            return new("Array size", "Edit the array length where the device permits resizing.", CanEdit: HasSimpleEditor(row), CanAttempt: !HasSimpleEditor(row));
        if (row.Property == "present-value" && commandable && type is not (0 or 3 or 13))
            return new("Commandable", "A 16-slot priority array was read. Commands still depend on device authorization and priority arbitration.", CanEdit: HasSimpleEditor(row), CanAttempt: !HasSimpleEditor(row), Commandable: true);
        if (row.Property == "relinquish-default" && commandable && HasSimpleEditor(row))
            return new("Command default", "This is the fallback when all commands are relinquished. Request a change without a command priority.", CanEdit: true);
        if (row.Property == "present-value" && type is 0 or 2 or 3 or 5 or 13 or 17 or 19 or 39 or 40 or 41 or 42 or 43 or 44 or 45 or 46 or 47 or 48 or 49 or 50)
        {
            if (outOfService)
                return new("Out-of-service override", "Out of Service was read as true. Its state is checked again before sending an override.", CanEdit: HasSimpleEditor(row), CanAttempt: !HasSimpleEditor(row));
            if (type is 0 or 3 or 13 or 17)
                return new("Requires out of service", "Read Out of Service as true before overriding this input. The app does not change Out of Service automatically.");
            return new("Editable", "Edit this value; the device validates its write permissions and operating rules.", CanEdit: HasSimpleEditor(row), CanAttempt: !HasSimpleEditor(row));
        }
        return new("Editable", "The property may be writable under its object definition. Save sends a validated request to the device.", CanEdit: HasSimpleEditor(row), CanAttempt: !HasSimpleEditor(row));
    }
}
