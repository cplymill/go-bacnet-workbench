using System.Globalization;

namespace BACnetExplorer;

public sealed class ScheduleEntry
{
    public string Time { get; set; } = "08:00";
    public string Type { get; set; } = "boolean";
    public string Value { get; set; } = "true";
    [System.Text.Json.Serialization.JsonIgnore]
    public double Minute => ParseMinute(Time);
    public static double ParseMinute(string text)
    {
        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time) || time.TotalMinutes < 0 || time.TotalMinutes > 1440)
            throw new FormatException("Use a time between 00:00 and 24:00.");
        return time.TotalMinutes;
    }
    public static string Clock(double minute) => minute >= 1440 ? "24:00" : TimeSpan.FromMinutes(minute).ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);
    public ScheduleEntry Copy(double minute) => new() { Time = Clock(minute), Type = Type, Value = Value };
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Type == "null" ? "Default" : Type == "boolean" ? (Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "Occupied" : "Unoccupied") : Value;
}

public sealed record ScheduleBlock(int Day, double Start, double End, string Type, string Value)
{
    public string Label => new ScheduleEntry { Type = Type, Value = Value }.Label;
}

public sealed class ScheduleDocument
{
    public List<List<ScheduleEntry>> Days { get; set; } = Enumerable.Range(0, 7).Select(_ => new List<ScheduleEntry>()).ToList();
    public string Raw { get; set; } = "";
    public string DefaultType { get; set; } = "null";
    public string DefaultValue { get; set; } = "";
    public string DefaultDisplay { get; set; } = "Not read";
    public string DefaultRaw { get; set; } = "";
    public string? DefaultError { get; set; }
    public System.Collections.ObjectModel.ObservableCollection<ScheduleExceptionEntry> Exceptions { get; set; } = [];
    public string? ExceptionsRaw { get; set; }
    public string? ExceptionsError { get; set; }
    public string PeriodStart { get; set; } = "*";
    public string PeriodEnd { get; set; } = "*";
    public string? PeriodRaw { get; set; }
    public string? PeriodError { get; set; }
    public bool OccupancyLabels { get; set; }
    public string Label(string type, string value)
    {
        if (OccupancyLabels && type is "enumerated" or "unsigned" or "signed" && value is "0" or "1") return value == "0" ? "Unoccupied (0)" : "Occupied (1)";
        return new ScheduleEntry { Type = type, Value = value }.Label;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ValueType => DefaultType != "null" ? DefaultType : Days.SelectMany(day => day).Concat(Exceptions.SelectMany(e => e.Times)).FirstOrDefault(e => e.Type != "null")?.Type ?? "null";
    public string DefaultLabel => DefaultError != null ? "Not read" : DefaultType == "null" ? "null" : Label(DefaultType, DefaultValue);
    public ScheduleEntry StateAt(int day, double minute) => Days[day].Where(e => e.Minute <= minute).OrderBy(e => e.Minute).LastOrDefault() ?? new ScheduleEntry { Type = "null", Value = "" };
    public IEnumerable<ScheduleBlock> Blocks(int day)
    {
        var ordered = Days[day].OrderBy(e => e.Minute).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            if (item.Type == "null" || item.Type == DefaultType && item.Value == DefaultValue) continue;
            var end = i + 1 < ordered.Count ? ordered[i + 1].Minute : 1440;
            if (end > item.Minute) yield return new ScheduleBlock(day, item.Minute, end, item.Type, item.Value);
        }
    }
    public void SetBlock(int day, double start, double end, string type, string value)
    {
        if (day < 0 || day > 6 || start < 0 || end > 1440 || start >= end) throw new InvalidOperationException("Choose a day and an end time later than the start time.");
        var restore = StateAt(day, end).Copy(end);
        Days[day].RemoveAll(e => e.Minute >= start && e.Minute <= end);
        Days[day].Add(new ScheduleEntry { Time = ScheduleEntry.Clock(start), Type = type, Value = type == "null" ? "" : value });
        if (end < 1440) Days[day].Add(restore);
        Days[day] = Days[day].OrderBy(e => e.Minute).ToList();
    }
}
