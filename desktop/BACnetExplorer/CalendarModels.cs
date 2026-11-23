using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BACnetExplorer;

public sealed class CalendarRule : INotifyPropertyChanged
{
    private string kind = "date", start = DateTime.Today.ToString("yyyy-MM-dd"), end = DateTime.Today.ToString("yyyy-MM-dd"), weekday = "*", month = "*", week = "*";
    public string Kind { get => kind; set => Set(ref kind, value); }
    public string Start { get => start; set => Set(ref start, value); }
    public string End { get => end; set => Set(ref end, value); }
    public string Weekday { get => weekday; set => Set(ref weekday, value); }
    public string Month { get => month; set => Set(ref month, value); }
    public string Week { get => week; set => Set(ref week, value); }
    [JsonIgnore]
    public string Summary => Kind == "week" ? $"Month {Month} · week {Week} · weekday {Weekday}" : Kind == "range" ? $"{Start} → {End}" : $"{Start} · weekday {Weekday}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }
}

public sealed class ScheduleExceptionEntry : INotifyPropertyChanged
{
    public CalendarRule Rule { get; set; } = new();
    private string calendarReference = "";
    private byte priority = 8;
    public string CalendarReference { get => calendarReference; set { calendarReference = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CalendarReference))); } }
    public byte Priority { get => priority; set { priority = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority))); } }
    public ObservableCollection<ScheduleEntry> Times { get; set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CalendarDocument
{
    public ObservableCollection<CalendarRule> Rules { get; set; } = [];
    public string Raw { get; set; } = "";
    public string PresentValue { get; set; } = "";
}
