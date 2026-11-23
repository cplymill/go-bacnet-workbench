using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
#if WINDOWS
using System.Windows.Data;
#endif

namespace BACnetExplorer;

public sealed class Peer : INotifyPropertyChanged
{
#if WINDOWS
    public Peer()
    {
        DeviceChildren = CollectionViewSource.GetDefaultView(Children);
        DeviceChildren.Filter = item => item is Peer;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public ICollectionView DeviceChildren { get; }
#endif
    public string Key { get; set; } = "";
    public string Device { get; set; } = "";
    private string name = "";
    public string Name { get => name; set { if (name == value) return; name = value; RefreshIdentity(); } }
    public void RefreshIdentity()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Warning)));
    }
    public uint Instance { get; set; }
    public string Address { get; set; } = "";
    public string Route { get; set; } = "";
    public ushort Vendor { get; set; }
    public int MaxApdu { get; set; }
    public string Segmentation { get; set; } = "";
    public bool Duplicate { get; set; }
    public bool Router { get; set; }
    public ushort[] Networks { get; set; } = [];
    public ushort RemoteNetwork { get; set; }
    public ObservableCollection<object> Children { get; } = [];
    public bool ObjectsLoaded { get; set; }
    private bool isExpanded;
    public bool IsExpanded
    {
        get => isExpanded;
        set { isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void UpdateRoute(Peer incoming)
    {
        if (!Router && incoming.Router) IsExpanded = true;
        Router = incoming.Router;
        Networks = incoming.Networks ?? [];
        RemoteNetwork = incoming.RemoteNetwork;
        Duplicate = incoming.Duplicate;
        Name = incoming.Name;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Warning)));
    }
    public string Caption => (Router ? "Router · " : "") + (Name.Length > 0 ? $"{Name} [{Instance}]" : $"Device {Instance}") + (Router ? $" [networks {string.Join(", ",Networks)}]" : "");
    public string Detail => $"{Address}  ·  Vendor {Vendor}";
    public string Warning => Duplicate ? "Duplicate instance — address selected explicitly" : $"APDU {MaxApdu}  ·  Route {Route}";
}

public class PropertyRow
{
    public string[] Elements { get; set; } = [];
    public string? EditorValue { get; set; }
    public string EditorType { get; set; } = "";
    public string[] ArrayValues { get; set; } = [];
    public string[] ArrayTypes { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => PropertyValueEditor.Name(this) + (Index.HasValue ? Index == 0 ? " (array size)" : $" [item {Index}]" : "");
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayType => PropertyValueEditor.DisplayType(this);
    public string Device { get; set; } = "";
    public string Object { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ObjectLabel => ObjectLabels.Display(Object);
    public string Property { get; set; } = "";
    public uint? Index { get; set; }
    public string Value { get; set; } = "";
    public string Raw { get; set; } = "";
    public string Type { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Selector => Property + (Index.HasValue ? $"[{Index}]" : "");
    public string Identity => $"{Device}|{Object}|{Property}|{Index}";
    public string Result => Error.Length > 0 ? Error : Value;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanEdit => EditAccess.CanEdit;
    [System.Text.Json.Serialization.JsonIgnore]
    public PropertyEditAccess EditAccess { get; set; } = new("Unverified", "Readability does not establish write permission.");
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccessStatus => EditAccess.Status;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccessReason => EditAccess.Reason;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FriendlyValue { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayValue
    {
        get
        {
            if (Error.Length > 0) return "";
            if (FriendlyValue != null) return FriendlyValue;
            if (Property == "units") return Value.Replace('-', ' ');
            if (Property == "object-type" && Value.StartsWith("enumerated:", StringComparison.Ordinal))
            {
                var number = Value[11..];
                var label = ObjectLabels.Display(number + ":0").Split(':')[0];
                return label == number ? "Object type " + number : label + " (" + number + ")";
            }
            if (ArrayValues.Length > 0) return $"{ArrayValues.Length} items · " + string.Join("; ", ArrayValues.Take(3));
            if (Property == "object-identifier" && Type == "object-identifier") return (PropertyValueEditor.IsDevice(this) ? "Device " : "Instance ") + Value.Split(':').Last();
            if (Type == "object-identifier") return ObjectLabels.Display(Value);
            if (Type == "date" && EditorValue != null) return EditorValue.Split('|')[0];
            if (Type is "time" or "bit-string" && EditorValue != null) return EditorValue;
            if (Value.StartsWith("raw:", StringComparison.Ordinal) || Value.StartsWith("json:", StringComparison.Ordinal)) return "Structured data";
            if (Type == "enumerated" && Value.StartsWith("enumerated:", StringComparison.Ordinal)) return Value[11..];
            if (Type == "octet-string" && Value.StartsWith("octets:", StringComparison.Ordinal)) return Value[7..];
            if (Type == "character-string" && Value.StartsWith('"'))
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<string>(Value) ?? ""; }
                catch (System.Text.Json.JsonException) { return Value; }
            }
            return Value;
        }
    }
}

public sealed class ObjectRow : INotifyPropertyChanged
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Peer? Owner { get; set; }
    private string identifier = "";
    public string Object
    {
        get => identifier;
        set { identifier = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Object))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ObjectLabel))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption))); }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ObjectLabel => ObjectLabels.Display(Object);
    public string Kind { get; set; } = "";
    private string name = "";
    public string Name
    {
        get => name;
        set
        {
            if (name == value) return;
            name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private string value = "";
    public string Value
    {
        get => value;
        set
        {
            if (this.value == value) return;
            this.value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
        }
    }
    public string DisplayValue => Value.StartsWith("enumerated:", StringComparison.Ordinal) ? Value[11..] : Value;
    public string Units { get; set; } = "";
    public string Caption => $"{ObjectLabel}  {Kind}" + (Name.Length > 0 ? $" · {Name}" : "");
}

public sealed class Watch : INotifyPropertyChanged
{
    public string Device { get; set; } = "";
    public string Caption { get; set; } = "";
    public string Object { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ObjectLabel => ObjectLabels.Display(Object);
    public string Property { get; set; } = "present-value";
    public uint? Index { get; set; }
    public string Value { get; private set; } = "Awaiting sample";
    public string Quality { get; private set; } = "Unobserved";
    public string Updated { get; private set; } = "";
    public uint Process { get; set; }
    public DateTimeOffset RenewAt { get; set; }
    public List<Sample> Samples { get; } = [];
    public string Identity => $"{Device}|{Object}|{Property}|{Index}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Observe(PropertyRow row, string source)
    {
        Value = row.Result;
        Quality = row.Error.Length > 0 ? "Error" : source;
        Updated = row.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        double? number = double.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && row.Error.Length == 0 ? value : null;
        Samples.Add(new Sample(row.At, number, row.Value, row.Error, source));
        if (Samples.Count > 1000) Samples.RemoveAt(0);
        Notify(nameof(Value)); Notify(nameof(Quality)); Notify(nameof(Updated));
    }
    public void Rebind(string device, string id, string caption)
    {
        Device = device; Object = id; Caption = caption;
        Notify(nameof(ObjectLabel)); Notify(nameof(Caption)); Notify(nameof(Identity));
    }
    public void Mark(string quality) { Quality = quality; Notify(nameof(Quality)); }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed record Sample(DateTimeOffset At, double? Number, string Value, string Error, string Source);
public sealed record ActivityRow(string Time, string Operation, string Target, string Result);
public sealed record Difference(string Object, string Property, string Before, string After, string Change)
{
    public string ObjectLabel => ObjectLabels.Display(Object);
}

public sealed class ConnectionProfile
{
    public string Transport { get; set; } = "ip";
    public string Bind { get; set; } = "127.0.0.1:0";
    public string Broadcast { get; set; } = "255.255.255.255:47808";
    public string Serial { get; set; } = "";
    public int Baud { get; set; } = 38400;
    public int Mac { get; set; } = 18;
    public int MaxMaster { get; set; } = 127;
    public ushort Network { get; set; }
    public int TimeoutMs { get; set; } = 3000;
    public int Retries { get; set; } = 1;
    public bool ConfirmJoin { get; set; }
    public string Bbmd { get; set; } = "";
    public ushort Ttl { get; set; } = 120;
    public string Ipv6Bind { get; set; } = "[::1]:47808";
    public int InterfaceIndex { get; set; } = 1;
    public string Vmac { get; set; } = "";
    public string Hub { get; set; } = "";
    public string FailoverHub { get; set; } = "";
    public string Certificate { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string SiteCa { get; set; } = "";
    public string Uuid { get; set; } = Guid.NewGuid().ToString("N");
    public bool VerifyServerName { get; set; }
}

public sealed class EventRow
{
    public string Object { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ObjectLabel => ObjectLabels.Display(Object);
    public string State { get; set; } = "";
    public string NotifyType { get; set; } = "";
    public string Acknowledged { get; set; } = "";
    public uint[] Priorities { get; set; } = [];
    public System.Text.Json.JsonElement[] Timestamps { get; set; } = [];
    public string PriorityDisplay => string.Join(" / ", Priorities);
}

public sealed class Workspace
{
    public int Version { get; set; } = 1;
    public ConnectionProfile Connection { get; set; } = new();
    public List<Watch> Watches { get; set; } = [];
    public List<PropertyRow> Baseline { get; set; } = [];
}
