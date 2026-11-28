using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BACnetExplorer;

public sealed class CalendarRule : INotifyPropertyChanged
{
    private string kind = "date", start = DateTime.Today.ToString("yyyy-MM-dd"), end = DateTime.Today.ToString("yyyy-MM-dd"), weekday = "*", month = "*", week = "*";
    public string Kind
    {
        get => kind; set => Set(ref kind, value);
    }
    public string Start
    {
        get => start; set => Set(ref start, value);
    }
    public string End
    {
        get => end; set => Set(ref end, value);
    }
    public string Weekday
    {
        get => weekday; set => Set(ref weekday, value);
    }
    public string Month
    {
        get => month; set => Set(ref month, value);
    }
    public string Week
    {
        get => week; set => Set(ref week, value);
    }
    [JsonIgnore]
    public string Summary => Kind switch
    {
        "week" => $"{MonthName(Month)} · {WeekName(Week)} · {WeekdayName(Weekday)}",
        "range" => $"{Start} → {End}",
        _ => $"{Start} · {WeekdayName(Weekday)}"
    };

    private static string MonthName(string month) =>
        int.TryParse(month, out var number) && number is >= 1 and <= 12
            ? $"{System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(number)} ({number})"
            : month switch
            {
                "*" => "Any month",
                "odd" => "Odd months",
                "even" => "Even months",
                _ => $"Month {month}"
            };

    private static string WeekName(string week) => week switch
    {
        "*" => "Any week",
        "1" => "Days 1–7 (1)",
        "2" => "Days 8–14 (2)",
        "3" => "Days 15–21 (3)",
        "4" => "Days 22–28 (4)",
        "5" => "Days 29–31 (5)",
        "6" => "Last 7 days (6)",
        "7" => "Second-last 7 days (7)",
        "8" => "Third-last 7 days (8)",
        "9" => "Fourth-last 7 days (9)",
        _ => $"Week code {week}"
    };

    private static string WeekdayName(string weekday) => weekday == "*" ? "Any weekday" :
        System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.DayNames.FirstOrDefault(
            day => weekday.Length > 0 && day.StartsWith(weekday, StringComparison.OrdinalIgnoreCase)) ?? weekday;
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
    public string CalendarReference
    {
        get => calendarReference; set
        {
            calendarReference = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CalendarReference)));
        }
    }
    public byte Priority
    {
        get => priority; set
        {
            priority = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority)));
        }
    }
    public ObservableCollection<ScheduleEntry> Times { get; set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CalendarDocument
{
    public ObservableCollection<CalendarRule> Rules { get; set; } = [];
    public string Raw { get; set; } = "";
    public string PresentValue { get; set; } = "";
}
