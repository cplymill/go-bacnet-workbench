using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace BACnetExplorer;

public partial class ScheduleEditorControl : UserControl, IObjectEditor
{
    private readonly EngineClient engine;
    private readonly Peer peer;
    private readonly string objectId;
    private ScheduleDocument? document;
    private bool busy, dirty;
    private string? previousTime;
    public bool IsBusy => busy;
    public string BindingKey => peer.Key + "/" + objectId;
    public event Action<bool>? WorkingChanged;
    private Window OwnerWindow => Window.GetWindow(this) ?? throw new InvalidOperationException("The editor is not attached to a window.");
    private static readonly string[] DayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    internal ScheduleEditorControl(EngineClient engine, Peer peer, string id, string name)
    {
        InitializeComponent();
        this.engine = engine; this.peer = peer; objectId = id;
        ScheduleTitle.Text = name.Length > 0 ? name : "Weekly schedule " + id;
        OccupancyLabels.IsChecked = name.Contains("occupan", StringComparison.OrdinalIgnoreCase);
        ScheduleTarget.Text = $"{peer.Caption} · {peer.Address} · {id}";
        BlockDay.ItemsSource = TransitionDay.ItemsSource = DayNames;
        BlockDay.SelectedIndex = TransitionDay.SelectedIndex = 0;
        DefaultValueBox.TextChanged += DefaultEdited;
        ExceptionPriority.ItemsSource = Enumerable.Range(1, 16).Select(n => (byte)n).ToArray();
        ExceptionRule.Edited += () => { if (!busy) { dirty = true; EditorStatus.Text = "Unsaved exception changes."; } };
        WeekView.BlockSelected += SelectBlock;
        WeekView.BlockChanged += (next, previous) => Edit(() =>
        {
            if (previous != null) document!.SetBlock(previous.Day, previous.Start, previous.End, "null", "");
            document!.SetBlock(next.Day, next.Start, next.End, next.Type, next.Value);
            SelectBlock(next);
        });
        Loaded += async (_, _) => { if (document == null && !busy) await Refresh(); };
    }
    private void SetBusy(bool value)
    {
        busy = value;
        RefreshButton.IsEnabled = SaveButton.IsEnabled = !value;
        WeekView.IsEnabled = !value;
        ScheduleTabs.IsEnabled = BlockControls.IsEnabled = !value;
        DefaultValueBox.IsEnabled = !value && document != null && document.DefaultError == null;
        WorkingChanged?.Invoke(value);
    }
    private async Task Refresh()
    {
        if (busy) return;
        if (dirty && MessageBox.Show(OwnerWindow, "Discard local schedule edits and refresh?", "Refresh schedule", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            var result = await engine.Call("schedule-read", new { device = peer.Key, @object = objectId });
            document = result.Deserialize<ScheduleDocument>(EngineClient.Json) ?? throw new InvalidOperationException("No schedule returned.");
            WeekView.Document = document; WeekView.Selected = null;
            document.OccupancyLabels = OccupancyLabels.IsChecked == true;
            DefaultSummary.Text = "Default: " + document.DefaultLabel + " · Weekly program repeats Monday through Sunday";
            dirty = false;
            UpdateView();
            EditorStatus.Text = "Loaded from device · edit blocks locally, then Save changes. Exception schedules and the effective period also participate in device scheduling.";
            BlockValue.Text = document.DefaultType == "boolean" ? "true" : document.DefaultValue == "0" ? "1" : document.DefaultValue;
            DefaultValueBox.Text = document.DefaultValue;
            ExceptionGrid.ItemsSource = document.Exceptions;
            ExceptionMetadata.DataContext = ExceptionRule.DataContext = null;
            ExceptionTimes.ItemsSource = null;
            ExceptionsTab.IsEnabled = document.ExceptionsRaw != null && document.ExceptionsError == null;
            PeriodTab.IsEnabled = document.PeriodRaw != null && document.PeriodError == null;
            NoPeriodStart.IsChecked = document.PeriodStart is "*" or "*-*-*";
            NoPeriodEnd.IsChecked = document.PeriodEnd is "*" or "*-*-*";
            PeriodStartPicker.SelectedDate = DateTime.TryParse(document.PeriodStart, out var startDate) ? startDate : null;
            PeriodEndPicker.SelectedDate = DateTime.TryParse(document.PeriodEnd, out var endDate) ? endDate : null;
            PeriodStartPicker.IsEnabled = NoPeriodStart.IsChecked != true;
            PeriodEndPicker.IsEnabled = NoPeriodEnd.IsChecked != true;
            if (document.ExceptionsError != null || document.PeriodError != null) EditorStatus.Text = string.Join(" · ", new[] { document.ExceptionsError, document.PeriodError }.Where(s => s != null));
        }
        catch (Exception ex) { EditorStatus.Text = ex.Message; EditorStatus.ToolTip = OperationAlert.Details("Schedule editor", ex); }
        finally { SetBusy(false); }
    }
    private async void RefreshSchedule(object sender, RoutedEventArgs e) => await Refresh();
    private async void CancelOperation(object sender, RoutedEventArgs e)
    {
        if (!busy) return;
        try { await engine.Call("cancel"); EditorStatus.Text = "Cancellation requested. Refresh before retrying a transmitted save."; }
        catch (Exception ex) { EditorStatus.Text = ex.Message; EditorStatus.ToolTip = OperationAlert.Details("Schedule editor", ex); }
    }
    private void SelectBlock(ScheduleBlock block)
    {
        WeekView.Selected = block;
        BlockDay.SelectedIndex = block.Day;
        BlockStart.Text = ScheduleEntry.Clock(block.Start);
        BlockEnd.Text = ScheduleEntry.Clock(block.End);
        BlockValue.Text = block.Value;
        WeekView.InvalidateVisual();
    }
    private void Edit(Action action)
    {
        if (document == null || busy) return;
        try { action(); dirty = true; UpdateView(); EditorStatus.Text = "Unsaved local changes · Save changes sends the weekly schedule to the device."; }
        catch (Exception ex) { EditorStatus.Text = ex.Message; EditorStatus.ToolTip = OperationAlert.Details("Schedule editor", ex); }
    }
    private void ApplyBlock(object sender, RoutedEventArgs e) => Edit(() =>
    {
        var block = new ScheduleBlock(BlockDay.SelectedIndex, ScheduleEntry.ParseMinute(BlockStart.Text), ScheduleEntry.ParseMinute(BlockEnd.Text), document!.ValueType, BlockValue.Text);
        document!.SetBlock(block.Day, block.Start, block.End, block.Type, block.Value);
        SelectBlock(block);
    });
    private void RemoveBlock(object sender, RoutedEventArgs e) => Edit(() =>
    {
        var block = WeekView.Selected ?? throw new InvalidOperationException("Select a block first.");
        document!.SetBlock(block.Day, block.Start, block.End, "null", "");
        WeekView.Selected = null;
    });
    private void CopyWeekdays(object sender, RoutedEventArgs e) => Edit(() =>
    {
        var day = BlockDay.SelectedIndex;
        var source = document!.Days[day].Select(e => e.Copy(e.Minute)).ToList();
        for (var target = 0; target < 5; target++) document.Days[target] = source.Select(e => e.Copy(e.Minute)).ToList();
    });
    private void UpdateView()
    {
        WeekView.InvalidateVisual();
        Transitions.ItemsSource = document?.Days.ElementAtOrDefault(TransitionDay.SelectedIndex);
    }
    private void ChangeTransitionDay(object sender, SelectionChangedEventArgs e) => UpdateView();
    private void EditorTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BlockControls != null) BlockControls.Visibility = ScheduleTabs.SelectedIndex < 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SelectException(object sender, SelectionChangedEventArgs e)
    {
        var row = ExceptionGrid.SelectedItem as ScheduleExceptionEntry;
        ExceptionMetadata.DataContext = row;
        ExceptionMetadata.IsEnabled = row != null;
        ExceptionRule.DataContext = row?.Rule;
        ExceptionRule.IsEnabled = row != null && row.CalendarReference.Length == 0;
        ExceptionTimes.ItemsSource = row?.Times;
    }
    private void ExceptionFieldEdited(object sender, RoutedEventArgs e)
    {
        if (ExceptionRule == null || document == null || busy) return;
        if (ExceptionGrid.SelectedItem is ScheduleExceptionEntry row) ExceptionRule.IsEnabled = row.CalendarReference.Length == 0;
        if (e.OriginalSource is FrameworkElement source && source.IsKeyboardFocusWithin) { dirty = true; EditorStatus.Text = "Unsaved exception changes."; }
    }
    private void AddException(object sender, RoutedEventArgs e)
    {
        if (document == null || busy) return;
        if (document.Exceptions.Count >= 256) { EditorStatus.Text = "Exception limit: 256 entries."; return; }
        var row = new ScheduleExceptionEntry();
        row.Times.Add(new ScheduleEntry { Time = "08:00", Type = document.ValueType, Value = document.DefaultType == "boolean" ? "true" : "1" });
        row.Times.Add(new ScheduleEntry { Time = "17:00", Type = "null", Value = "" });
        document.Exceptions.Add(row);
        ExceptionGrid.SelectedItem = row;
        dirty = true;
        EditorStatus.Text = "New exception · choose its date/range/recurrence or Calendar reference and time values, then Save changes.";
    }
    private void RemoveException(object sender, RoutedEventArgs e)
    {
        if (document == null || busy || ExceptionGrid.SelectedItem is not ScheduleExceptionEntry row) return;
        document.Exceptions.Remove(row); dirty = true;
    }
    private void AddExceptionTime(object sender, RoutedEventArgs e)
    {
        if (busy || ExceptionGrid.SelectedItem is not ScheduleExceptionEntry row) return;
        row.Times.Add(new ScheduleEntry { Type = document!.ValueType, Value = document.ValueType == "boolean" ? "true" : "1" }); dirty = true;
    }
    private void RemoveExceptionTime(object sender, RoutedEventArgs e)
    {
        if (busy || ExceptionGrid.SelectedItem is not ScheduleExceptionEntry row) return;
        foreach (var time in ExceptionTimes.SelectedItems.Cast<ScheduleEntry>().ToList()) row.Times.Remove(time);
        dirty = true;
    }
    private void PeriodEdited(object sender, RoutedEventArgs e)
    {
        if (PeriodStartPicker == null || PeriodEndPicker == null) return;
        PeriodStartPicker.IsEnabled = NoPeriodStart.IsChecked != true;
        PeriodEndPicker.IsEnabled = NoPeriodEnd.IsChecked != true;
        if (document != null && !busy) { dirty = true; EditorStatus.Text = "Unsaved effective-period changes."; }
    }
    private void BeginTransitionEdit(object sender, DataGridBeginningEditEventArgs e) => previousTime = (e.Row.Item as ScheduleEntry)?.Time;
    private void TransitionEdited(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Column.DisplayIndex == 0 && e.EditingElement is TextBox editor)
        {
            try
            {
                if (ScheduleEntry.ParseMinute(editor.Text) >= 1440) throw new FormatException("A transition must occur before 24:00.");
            }
            catch (FormatException ex)
            {
                e.Cancel = true;
                if (e.Row.Item is ScheduleEntry entry && previousTime != null) entry.Time = previousTime;
                EditorStatus.Text = ex.Message; EditorStatus.ToolTip = OperationAlert.Details("Schedule editor", ex);
                return;
            }
        }
        dirty = true;
        Dispatcher.InvokeAsync(() => WeekView.InvalidateVisual());
    }
    private void ToggleHours(object sender, RoutedEventArgs e)
    {
        WeekView.FirstMinute = WeekView.FirstMinute == 0 ? 360 : 0;
        WeekView.LastMinute = WeekView.FirstMinute == 0 ? 1440 : 1200;
        WeekView.InvalidateVisual();
    }
    private void ToggleLabels(object sender, RoutedEventArgs e)
    {
        if (document == null) return;
        document.OccupancyLabels = OccupancyLabels.IsChecked == true;
        DefaultSummary.Text = "Default: " + document.DefaultLabel + " · Weekly program repeats Monday through Sunday";
        WeekView.InvalidateVisual();
    }
    private void DefaultEdited(object sender, RoutedEventArgs e)
    {
        if (document == null || busy) return;
        document.DefaultValue = DefaultValueBox.Text;
        dirty = true;
        DefaultSummary.Text = "Default: " + document.DefaultLabel + " · Unsaved local change";
        WeekView.InvalidateVisual();
    }
    private async void SaveSchedule(object sender, RoutedEventArgs e)
    {
        if (document == null || busy) return;
        if (!Transitions.CommitEdit(DataGridEditingUnit.Cell, true) || !Transitions.CommitEdit(DataGridEditingUnit.Row, true) || !ExceptionTimes.CommitEdit(DataGridEditingUnit.Cell, true) || !ExceptionTimes.CommitEdit(DataGridEditingUnit.Row, true))
        {
            EditorStatus.Text = "Finish or cancel the current transition edit before saving.";
            return;
        }
        try { foreach (var day in document.Days) foreach (var entry in day) _ = entry.Minute; }
        catch (FormatException ex) { EditorStatus.Text = ex.Message; EditorStatus.ToolTip = OperationAlert.Details("Schedule editor", ex); return; }
        var days = document.Days.Select(day => day.OrderBy(e => e.Minute).Select(e => new { time = e.Time, type = e.Type, value = e.Value }).ToArray()).ToArray();
        if (document.PeriodRaw != null && (NoPeriodStart.IsChecked != true && PeriodStartPicker.SelectedDate == null || NoPeriodEnd.IsChecked != true && PeriodEndPicker.SelectedDate == null)) { EditorStatus.Text = "Select valid effective dates or choose an unbounded end."; return; }
        var periodStart = NoPeriodStart.IsChecked == true ? "*" : PeriodStartPicker.SelectedDate?.ToString("yyyy-MM-dd");
        var periodEnd = NoPeriodEnd.IsChecked == true ? "*" : PeriodEndPicker.SelectedDate?.ToString("yyyy-MM-dd");
        var preview = $"Default: {document.DefaultType} {document.DefaultValue}\nEffective: {periodStart} → {periodEnd}\nExceptions: {document.Exceptions.Count}\n" + string.Join("\n", document.Exceptions.Take(15).Select(x => $"Priority {x.Priority} · {(x.CalendarReference.Length > 0 ? x.CalendarReference : x.Rule.Summary)} · {x.Times.Count} values")) + "\n" + string.Join("\n", days.Select((day, index) => DayNames[index] + ": " + (day.Length == 0 ? "Default" : string.Join("; ", day.Select(e => $"{e.time} {e.type} {e.value}")))));
        if (preview.Length > 4000) preview = preview[..4000] + "\n… Review the complete program in the editor.";
        if (MessageBox.Show(OwnerWindow, $"{peer.Caption} at {peer.Address}\nRoute {peer.Route}\nObject {objectId} / weekly-schedule\n\n{preview}\n\nSend this weekly program?", "Save weekly schedule", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            await engine.Call("schedule-write", new { device = peer.Key, @object = objectId, expectedRaw = document.Raw, value = JsonSerializer.Serialize(days), defaultType = document.DefaultError == null ? document.DefaultType : "", defaultValue = document.DefaultValue, expectedDefaultRaw = document.DefaultRaw, exceptions = document.ExceptionsRaw != null ? document.Exceptions : null, expectedExceptionsRaw = document.ExceptionsRaw, periodStart, periodEnd, expectedPeriodRaw = document.PeriodRaw, confirmed = true });
            dirty = false;
            EditorStatus.Text = "Weekly schedule acknowledged. Refreshing device readback…";
        }
        catch (Exception ex) { EditorStatus.Text = ex.Message; EditorStatus.ToolTip = OperationAlert.Details("Schedule editor", ex); return; }
        finally { SetBusy(false); }
        await Refresh();
    }
    public bool CanLeave()
    {
        if (busy) { EditorStatus.Text = "Wait for the device operation to finish before navigating away."; return false; }
        return !dirty || MessageBox.Show(OwnerWindow, "Discard unsaved schedule edits?", "Leave schedule", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
