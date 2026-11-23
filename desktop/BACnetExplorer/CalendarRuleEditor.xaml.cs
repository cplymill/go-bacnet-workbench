using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BACnetExplorer;

public partial class CalendarRuleEditor : UserControl
{
    private CalendarRule? boundRule;
    public event Action? Edited;
    public CalendarRuleEditor()
    {
        InitializeComponent();
        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(Changed));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(Changed));
        DataContextChanged += (_, _) =>
        {
            if (boundRule != null) boundRule.PropertyChanged -= RuleChanged;
            boundRule = DataContext as CalendarRule;
            if (boundRule != null) boundRule.PropertyChanged += RuleChanged;
            UpdateFields();
        };
    }
    private void RuleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateFields();
    private void Changed(object sender, RoutedEventArgs e)
    {
        UpdateFields();
        if (e.OriginalSource is FrameworkElement element && element.IsKeyboardFocusWithin) Edited?.Invoke();
    }
    private void UpdateFields()
    {
        if (RuleKind == null) return;
        var kind = (DataContext as CalendarRule)?.Kind ?? "date";
        StartField.Visibility = kind == "week" ? Visibility.Collapsed : Visibility.Visible;
        EndField.Visibility = kind == "range" ? Visibility.Visible : Visibility.Collapsed;
        MonthField.Visibility = WeekField.Visibility = kind == "week" ? Visibility.Visible : Visibility.Collapsed;
        WeekdayField.Visibility = kind == "range" ? Visibility.Collapsed : Visibility.Visible;
        RuleHelp.Text = kind == "date" ? "Use * for any year, month or day; last, odd and even are supported day patterns. Example: *-12-25."
            : kind == "range" ? "Inclusive start and end dates. Use * for an open end."
            : "Weeks 1–5 select days 1–7, 8–14, 15–21, 22–28, 29–31; 6 selects the last seven days; 7–9 select preceding weeks.";
    }
}
