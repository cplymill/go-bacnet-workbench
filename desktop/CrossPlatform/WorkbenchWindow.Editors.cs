using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace BACnetExplorer.CrossPlatform;

public sealed partial class WorkbenchWindow
{
    private ScheduleDocument? schedule;
    private CalendarDocument? calendar;
    private string structuredDevice = "", structuredObject = "", structuredBaseline = "";
    private readonly ComboBox day = new() { ItemsSource = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }, SelectedIndex = 0 };
    private readonly DataGrid transitions = Table(("Time", "Time"), ("Type", "Type"), ("Value", "Value"), ("Meaning", "Label"));
    private readonly DataGrid calendarGrid = Table(("Kind", "Kind"), ("Start", "Start"), ("End", "End"), ("Weekday", "Weekday"), ("Month", "Month"), ("Week", "Week"), ("Meaning", "Summary"));
    private readonly TextBox transitionTime = Input("08:00:00.00", "Time"), transitionType = Input("boolean", "Type"), transitionValue = Input("true", "Value");
    private readonly TextBox blockStart = Input("08:00", "Block start"), blockEnd = Input("17:00", "Block end");
    private readonly TextBox defaultType = Input("null", "Default type"), defaultValue = Input("", "Default value");
    private readonly TextBox periodStart = Input("*", "Effective start"), periodEnd = Input("*", "Effective end");
    private readonly TextBox exceptionJson = JsonBox("[]");
    private readonly ComboBox ruleKind = new() { ItemsSource = new[] { "date", "range", "week" }, SelectedIndex = 0 };
    private readonly TextBox ruleStart = Input(DateTime.Today.ToString("yyyy-MM-dd"), "Date or range start"), ruleEnd = Input(DateTime.Today.ToString("yyyy-MM-dd"), "Range end");
    private readonly TextBox ruleWeekday = Input("*", "Weekday"), ruleMonth = Input("*", "Month"), ruleWeek = Input("*", "Week");
    private readonly TextBlock structuredStatus = new() { Text = "Select a Schedule or Calendar object, then read it." };

    private Control StructuredEditors()
    {
        day.SelectionChanged += (_, _) => ShowDay();
        transitions.SelectionChanged += (_, _) =>
        {
            if (transitions.SelectedItem is not ScheduleEntry entry)
                return;
            transitionTime.Text = entry.Time;
            transitionType.Text = entry.Type;
            transitionValue.Text = entry.Value;
        };
        calendarGrid.SelectionChanged += (_, _) =>
        {
            if (calendarGrid.SelectedItem is not CalendarRule rule)
                return;
            ruleKind.SelectedItem = rule.Kind;
            ruleStart.Text = rule.Start;
            ruleEnd.Text = rule.End;
            ruleWeekday.Text = rule.Weekday;
            ruleMonth.Text = rule.Month;
            ruleWeek.Text = rule.Week;
        };
        var weekly = Page(Row(Button("Read schedule", ReadSchedule), day, Button("Save schedule", SaveSchedule)),
            Row(Label("Time", transitionTime), Label("Value type", transitionType), Label("Value", transitionValue),
                Button("Add / update transition", SetTransition), Button("Remove transition", RemoveTransition)),
            Row(Label("Block start", blockStart), Label("Block end", blockEnd), Button("Set time block", SetBlock),
                Button("Copy day to all weekdays", CopyWeekdays)),
            Row(Label("Default type", defaultType), Label("Default value", defaultValue),
                Label("Effective start", periodStart), Label("Effective end", periodEnd)),
            new Expander { Header = "Exception schedule · typed rules and transitions (JSON)", Content = exceptionJson }, transitions);
        var dates = Page(Row(Button("Read calendar", ReadCalendar), Button("Save calendar", SaveCalendar)),
            Row(ruleKind, ruleStart, ruleEnd, ruleWeekday, ruleMonth, ruleWeek),
            Row(Button("Add rule", AddRule), Button("Update selected rule", UpdateRule), Button("Remove rule", RemoveRule)), calendarGrid);
        return Page(structuredStatus, new TabControl
        {
            Items = {
            new TabItem { Header = "Weekly schedule", Content = weekly },
            new TabItem { Header = "Calendar dates", Content = dates }
        }
        });
    }

    private async Task<bool> AdmitStructuredDraft()
    {
        if (structuredBaseline.Length == 0 || StructuredIdentity() == structuredBaseline)
            return true;
        return await Dialogs.Confirm(this, "Unsaved schedule / calendar", "Discard unsent schedule or calendar changes and continue?");
    }
    private string StructuredIdentity() => Pretty(new
    {
        schedule,
        calendar,
        defaultType = defaultType.Text,
        defaultValue = defaultValue.Text,
        start = periodStart.Text,
        end = periodEnd.Text,
        exceptions = exceptionJson.Text
    });
    private void BindStructured()
    {
        structuredDevice = CurrentPeer.Key;
        structuredObject = objectId;
        structuredStatus.Text = $"{CurrentPeer.Caption} · {ObjectLabels.Display(objectId)} · changes remain local until Save";
        structuredBaseline = StructuredIdentity();
    }
    private async Task ReadSchedule()
    {
        if (!await AdmitStructuredDraft())
            return;
        schedule = Decode<ScheduleDocument>(await Engine.Call("schedule-read", new
        {
            device = CurrentPeer.Key,
            @object = objectId
        }));
        calendar = null;
        calendarGrid.ItemsSource = null;
        defaultType.Text = schedule.DefaultType;
        defaultValue.Text = schedule.DefaultValue;
        periodStart.Text = schedule.PeriodStart;
        periodEnd.Text = schedule.PeriodEnd;
        exceptionJson.Text = Pretty(schedule.Exceptions);
        ShowDay();
        BindStructured();
    }
    private void ShowDay()
    {
        transitions.ItemsSource = schedule != null && day.SelectedIndex is >= 0 and < 7
            ? schedule.Days[day.SelectedIndex].OrderBy(entry => entry.Minute).ToList() : null;
    }
    private ScheduleDocument LoadedSchedule() => schedule ?? throw new InvalidOperationException("Read a Schedule object first.");
    private CalendarDocument LoadedCalendar() => calendar ?? throw new InvalidOperationException("Read a Calendar object first.");
    private Task SetTransition()
    {
        var document = LoadedSchedule();
        var time = transitionTime.Text ?? "";
        _ = ScheduleEntry.ParseMinute(time);
        var entries = document.Days[day.SelectedIndex];
        var selected = transitions.SelectedItem as ScheduleEntry;
        var entry = selected ?? new ScheduleEntry();
        entry.Time = time;
        entry.Type = transitionType.Text ?? "null";
        entry.Value = transitionValue.Text ?? "";
        if (selected == null)
            entries.Add(entry);
        ShowDay();
        return Task.CompletedTask;
    }
    private Task RemoveTransition()
    {
        if (transitions.SelectedItem is ScheduleEntry entry)
            LoadedSchedule().Days[day.SelectedIndex].Remove(entry);
        ShowDay();
        return Task.CompletedTask;
    }
    private Task SetBlock()
    {
        LoadedSchedule().SetBlock(day.SelectedIndex, ScheduleEntry.ParseMinute(blockStart.Text ?? ""),
            ScheduleEntry.ParseMinute(blockEnd.Text ?? ""), transitionType.Text ?? "boolean", transitionValue.Text ?? "true");
        ShowDay();
        return Task.CompletedTask;
    }
    private Task CopyWeekdays()
    {
        var document = LoadedSchedule();
        var source = document.Days[day.SelectedIndex];
        for (var index = 0; index < 5; index++)
            if (index != day.SelectedIndex)
                document.Days[index] = source.Select(entry => entry.Copy(entry.Minute)).ToList();
        ShowDay();
        return Task.CompletedTask;
    }
    private async Task SaveSchedule()
    {
        var document = LoadedSchedule();
        RequireStructuredTarget();
        foreach (var entry in document.Days.SelectMany(entries => entries))
            _ = entry.Minute;
        var days = document.Days.Select(entries => entries.OrderBy(entry => entry.Minute)
            .Select(entry => new { time = entry.Time, type = entry.Type, value = entry.Value }).ToArray()).ToArray();
        var exceptions = JsonSerializer.Deserialize<ObservableCollection<ScheduleExceptionEntry>>(exceptionJson.Text ?? "[]", EngineClient.Json)
            ?? throw new InvalidDataException("Invalid exception list.");
        if (!await ConfirmTarget("Save weekly schedule", $"{days.Sum(entries => entries.Length)} weekly transitions; {exceptions.Count} exceptions.\nEffective {periodStart.Text} → {periodEnd.Text}"))
            return;
        await Engine.Call("schedule-write", new
        {
            device = structuredDevice,
            @object = structuredObject,
            expectedRaw = document.Raw,
            value = JsonSerializer.Serialize(days),
            confirmed = true,
            defaultType = document.DefaultError == null ? defaultType.Text : "",
            defaultValue = defaultValue.Text,
            expectedDefaultRaw = document.DefaultRaw,
            exceptions = document.ExceptionsRaw != null ? exceptions : null,
            expectedExceptionsRaw = document.ExceptionsRaw,
            periodStart = periodStart.Text,
            periodEnd = periodEnd.Text,
            expectedPeriodRaw = document.PeriodRaw
        });
        structuredBaseline = StructuredIdentity();
        await ReadSchedule();
        status.Text = "Schedule acknowledged and refreshed.";
    }
    private async Task ReadCalendar()
    {
        if (!await AdmitStructuredDraft())
            return;
        calendar = Decode<CalendarDocument>(await Engine.Call("calendar-read", new
        {
            device = CurrentPeer.Key,
            @object = objectId
        }));
        schedule = null;
        transitions.ItemsSource = null;
        calendarGrid.ItemsSource = calendar.Rules;
        BindStructured();
    }
    private CalendarRule EditedRule() => new()
    {
        Kind = ruleKind.SelectedItem as string ?? "date",
        Start = ruleStart.Text ?? "*",
        End = ruleEnd.Text ?? "*",
        Weekday = ruleWeekday.Text ?? "*",
        Month = ruleMonth.Text ?? "*",
        Week = ruleWeek.Text ?? "*"
    };
    private Task AddRule()
    {
        if (LoadedCalendar().Rules.Count >= 1024)
            throw new InvalidOperationException("Calendar limit is 1024 entries.");
        LoadedCalendar().Rules.Add(EditedRule());
        return Task.CompletedTask;
    }
    private Task UpdateRule()
    {
        if (calendarGrid.SelectedItem is CalendarRule selected)
        {
            var rules = LoadedCalendar().Rules;
            rules[rules.IndexOf(selected)] = EditedRule();
        }
        return Task.CompletedTask;
    }
    private Task RemoveRule()
    {
        if (calendarGrid.SelectedItem is CalendarRule selected)
            LoadedCalendar().Rules.Remove(selected);
        return Task.CompletedTask;
    }
    private async Task SaveCalendar()
    {
        var document = LoadedCalendar();
        RequireStructuredTarget();
        if (!await ConfirmTarget("Save calendar", string.Join('\n', document.Rules.Take(30).Select(rule => rule.Summary)) + $"\n{document.Rules.Count} total rules"))
            return;
        await Engine.Call("calendar-write", new
        {
            device = structuredDevice,
            @object = structuredObject,
            rules = document.Rules,
            expectedListRaw = document.Raw,
            confirmed = true
        });
        structuredBaseline = StructuredIdentity();
        await ReadCalendar();
        status.Text = "Calendar acknowledged and refreshed.";
    }
    private void RequireStructuredTarget()
    {
        if (CurrentPeer.Key != structuredDevice || objectId != structuredObject)
            throw new InvalidOperationException("The loaded editor belongs to a different device/object. Read the selected object before saving.");
    }
    private Task<bool> ConfirmTarget(string operation, string detail) => Dialogs.Confirm(this, operation,
        $"{CurrentPeer.Caption} at {CurrentPeer.Address}\nRoute {CurrentPeer.Route}\nObject {objectId}\n\n{detail}\n\nContinue?");
}
