using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace BACnetExplorer;

public partial class CalendarEditorControl : UserControl, IObjectEditor
{
    private readonly EngineClient engine;
    private readonly Peer peer;
    private readonly string id;
    private CalendarDocument? document;
    private bool busy, dirty;
    public bool IsBusy => busy;
    public string BindingKey => peer.Key + "/" + id;
    public event Action<bool>? WorkingChanged;
    private Window OwnerWindow => Window.GetWindow(this) ?? throw new InvalidOperationException("The calendar is not attached to a window.");
    internal CalendarEditorControl(EngineClient engine, Peer peer, string id, string name)
    {
        InitializeComponent();
        this.engine = engine; this.peer = peer; this.id = id;
        CalendarTitle.Text = name.Length > 0 ? name : "Calendar " + id;
        CalendarTarget.Text = $"{peer.Caption} · {peer.Address} · {id}";
        RuleEditor.Edited += MarkDirty;
        Loaded += async (_, _) => { if (document == null && !busy) await Refresh(); };
    }
    private void SetBusy(bool value)
    {
        busy = value;
        EditingArea.IsEnabled = RefreshButton.IsEnabled = SaveButton.IsEnabled = !value;
        WorkingChanged?.Invoke(value);
    }
    private void MarkDirty()
    {
        if (busy || document == null) return;
        dirty = true;
        CalendarSummary.Text = $"{document.Rules.Count} entries · present value {document.PresentValue}";
        CalendarStatus.Text = "Unsaved local changes · Save changes sends the date list for device acceptance.";
    }
    private async Task Refresh()
    {
        if (busy || dirty && !CanLeave()) return;
        SetBusy(true);
        try
        {
            var result = await engine.Call("calendar-read", new { device = peer.Key, @object = id });
            document = result.Deserialize<CalendarDocument>(EngineClient.Json) ?? throw new InvalidOperationException("No calendar returned.");
            RuleGrid.ItemsSource = document.Rules;
            RuleEditor.DataContext = null;
            RuleEditor.IsEnabled = false;
            dirty = false;
            CalendarSummary.Text = $"{document.Rules.Count} entries · present value {document.PresentValue}";
            CalendarStatus.Text = "Loaded from device · select a rule to edit its dates or recurrence.";
        }
        catch (Exception ex) { CalendarStatus.Text = ex.Message; CalendarStatus.ToolTip = OperationAlert.Details("Calendar editor", ex); }
        finally { SetBusy(false); }
    }
    private async void RefreshCalendar(object sender, RoutedEventArgs e) => await Refresh();
    private void SelectRule(object sender, SelectionChangedEventArgs e)
    {
        RuleEditor.DataContext = RuleGrid.SelectedItem as CalendarRule;
        RuleEditor.IsEnabled = RuleEditor.DataContext != null;
    }
    private void AddRule(CalendarRule rule)
    {
        if (document == null || busy) return;
        if (document.Rules.Count >= 1024) { CalendarStatus.Text = "Calendar limit: 1,024 entries."; return; }
        document.Rules.Add(rule);
        RuleGrid.SelectedItem = rule;
        RuleGrid.ScrollIntoView(rule);
        MarkDirty();
    }
    private void AddSelectedDates(object sender, RoutedEventArgs e)
    {
        foreach (var date in DatePicker.SelectedDates.ToList()) AddRule(new CalendarRule { Start = date.ToString("yyyy-MM-dd") });
        if (DatePicker.SelectedDates.Count == 0) CalendarStatus.Text = "Select at least one date first.";
    }
    private void AddSelectedRange(object sender, RoutedEventArgs e)
    {
        var dates = DatePicker.SelectedDates.OrderBy(d => d).ToList();
        if (dates.Count == 0) { CalendarStatus.Text = "Select the range in the calendar first."; return; }
        AddRule(new CalendarRule { Kind = "range", Start = dates[0].ToString("yyyy-MM-dd"), End = dates[^1].ToString("yyyy-MM-dd") });
    }
    private void AddRecurringRule(object sender, RoutedEventArgs e) => AddRule(new CalendarRule { Kind = "week", Weekday = "Mon" });
    private void RemoveRules(object sender, RoutedEventArgs e)
    {
        if (document == null || busy) return;
        foreach (var rule in RuleGrid.SelectedItems.Cast<CalendarRule>().ToList()) document.Rules.Remove(rule);
        MarkDirty();
    }
    private async void SaveCalendar(object sender, RoutedEventArgs e)
    {
        if (document == null || busy) return;
        var preview = string.Join("\n", document.Rules.Take(30).Select(r => r.Kind + ": " + r.Summary));
        if (MessageBox.Show(OwnerWindow, $"{peer.Caption} at {peer.Address}\nRoute {peer.Route}\nObject {id} / date-list\n\n{document.Rules.Count} entries\n{preview}\n\nSend this date list?", "Save calendar", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            await engine.Call("calendar-write", new { device = peer.Key, @object = id, rules = document.Rules, expectedListRaw = document.Raw, confirmed = true });
            dirty = false;
        }
        catch (Exception ex) { CalendarStatus.Text = ex.Message; CalendarStatus.ToolTip = OperationAlert.Details("Calendar editor", ex); return; }
        finally { SetBusy(false); }
        await Refresh();
    }
    private async void CancelOperation(object sender, RoutedEventArgs e)
    {
        if (!busy) return;
        try { await engine.Call("cancel"); CalendarStatus.Text = "Cancellation requested. Refresh before retrying a transmitted save."; }
        catch (Exception ex) { CalendarStatus.Text = ex.Message; CalendarStatus.ToolTip = OperationAlert.Details("Calendar editor", ex); }
    }
    public bool CanLeave()
    {
        if (busy) return false;
        return !dirty || MessageBox.Show(OwnerWindow, "Discard unsaved calendar edits?", "Leave calendar", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
