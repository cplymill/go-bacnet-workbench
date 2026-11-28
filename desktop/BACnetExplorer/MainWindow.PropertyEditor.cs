using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace BACnetExplorer;

public partial class MainWindow
{
    private sealed record PropertyDraft(string Value, string Priority, string? Weekday, string[] Items, bool Advanced, int Start, int Length);
    private PropertyRow? editingProperty;
    private string propertyBaseline = "";
    private bool replacingPropertyRows, restoringPropertySelection;

    private PropertyDraft CapturePropertyDraft() => new(WriteValue.Text, Priority.Text,
        PropertyWeekday.SelectedValue as string, propertyArrayItems.Select(item => item.Value).ToArray(),
        AdvancedPropertyAttempt.IsChecked == true, WriteValue.SelectionStart, WriteValue.SelectionLength);
    private static string DraftSignature(PropertyDraft draft) => JsonSerializer.Serialize(new { draft.Value, draft.Priority, draft.Weekday, draft.Items, draft.Advanced });
    private bool HasPropertyDraft => editingProperty != null && propertyBaseline != DraftSignature(CapturePropertyDraft());
    private bool CanLeavePropertyDraft() => !HasPropertyDraft || MessageBox.Show(this,
        "Discard the unsaved change to " + editingProperty!.DisplayName + "?", "Unsaved property change",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private void RestorePropertyDraft(PropertyDraft draft)
    {
        WriteValue.Text = draft.Value;
        BooleanWriteValue.SelectedValue = draft.Value;
        Priority.Text = draft.Priority;
        PropertyWeekday.SelectedValue = draft.Weekday;
        for (var index = 0; index < Math.Min(propertyArrayItems.Count, draft.Items.Length); index++)
            propertyArrayItems[index].Value = draft.Items[index];
        if (propertyArrayItems.Count > 0)
            PropertyArrayGrid.Items.Refresh();
        for (var index = 0; index < Math.Min(propertyFlagItems.Count, draft.Value.Length); index++)
            propertyFlagItems[index].Enabled = draft.Value[index] == '1';
        AdvancedPropertyAttempt.IsChecked = draft.Advanced;
        WriteValue.Text = draft.Value;
        WriteValue.Select(draft.Start, draft.Length);
    }

    private List<PropertyArrayItem> propertyArrayItems = [];
    private List<PropertyFlagItem> propertyFlagItems = [];
    private Task<List<PropertyChoice>>? unitChoices;
    private GridLength propertyEditorWidth = new(1, GridUnitType.Star);
    private void PropertyEditorLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PropertyEditor))
            UpdatePropertyEditorLayout();
    }
    private void UpdatePropertyEditorLayout()
    {
        var open = PropertyEditor.IsExpanded && PropertyEditor.Visibility == Visibility.Visible;
        if (open)
        {
            PropertyEditColumn.MinWidth = 260;
            PropertyEditColumn.Width = propertyEditorWidth;
        }
        else
        {
            if (PropertyEditColumn.Width.Value > 0)
                propertyEditorWidth = PropertyEditColumn.Width;
            PropertyEditColumn.MinWidth = 0;
            PropertyEditColumn.Width = new GridLength(0);
        }
        PropertyEditDivider.Width = new GridLength(open ? 8 : 0);
        PropertyEditSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureValueEditor(PropertyRow row)
    {
        WriteValue.Text = PropertyValueEditor.EditText(row);
        PropertyValueLabel.Text = row.Property == "object-identifier" ? PropertyValueEditor.Name(row) : row.ArrayValues.Length > 0 ? "Items" : "New value";
        PropertyEditHint.Text = row.CanEdit ? PropertyValueEditor.Hint(row) : "";
        var choices = PropertyValueEditor.Choices(row, properties);
        SetPropertyChoices(choices);
        WriteValue.Width = double.NaN;
        WriteValue.Visibility = row.Type == "boolean" || row.ArrayValues.Length > 0 || row.Type == "bit-string" && (row.EditorValue?.Length ?? 0) is > 0 and <= 128 ? Visibility.Collapsed : Visibility.Visible;
        PropertyDatePicker.Visibility = PropertyWeekday.Visibility = row.Type == "date" ? Visibility.Visible : Visibility.Collapsed;
        PropertyDatePicker.SelectedDate = DateTime.TryParseExact(WriteValue.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        PropertyWeekday.ItemsSource = new[] { new PropertyChoice("255", "Any weekday"), new("1", "Monday"), new("2", "Tuesday"), new("3", "Wednesday"), new("4", "Thursday"), new("5", "Friday"), new("6", "Saturday"), new("7", "Sunday") };
        PropertyWeekday.SelectedValue = row.EditorValue?.Split('|').ElementAtOrDefault(1) ?? "255";
        propertyArrayItems = row.ArrayValues.Select((value, index) => new PropertyArrayItem { Index = index + 1, Value = row.ArrayTypes.ElementAtOrDefault(index) == "object-identifier" ? ObjectLabels.Display(value) : value }).ToList();
        PropertyArrayGrid.ItemsSource = propertyArrayItems;
        PropertyArrayGrid.Visibility = propertyArrayItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var bits = row.Type == "bit-string" ? row.EditorValue ?? "" : "";
        var names = BacnetDisplay.FlagsFor(row.Property);
        propertyFlagItems = bits.Length <= 128 ? bits.Select((bit, index) => new PropertyFlagItem { Label = index < names.Length ? names[index] : "Bit " + (index + 1), Enabled = bit == '1' }).ToList() : [];
        PropertyFlags.ItemsSource = propertyFlagItems;
        PropertyFlags.Visibility = propertyFlagItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetPropertyChoices(IReadOnlyList<PropertyChoice> choices)
    {
        var value = WriteValue.Text;
        var items = choices.ToList();
        if (items.Count > 0 && items.All(item => item.Value != value))
            items.Add(new(value, "Current: " + value));
        BooleanWriteValue.ItemsSource = items;
        WriteValue.Text = value;
        BooleanWriteValue.SelectedValue = value;
        BooleanWriteValue.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task LoadPropertyUnitChoices(PropertyRow row)
    {
        if (row.Property != "units" || !row.CanEdit)
            return;
        try
        {
            unitChoices ??= ReadUnits();
            var choices = await unitChoices;
            if (ReferenceEquals(PropertyGrid.SelectedItem, row))
            {
                SetPropertyChoices(choices);
            }
        }
        catch (Exception ex) { Status.Text = "Unit names unavailable; enter the unit code. " + ex.Message; unitChoices = null; }
    }
    private async Task<List<PropertyChoice>> ReadUnits() => (await Engine.Call("unit-choices")).Deserialize<List<PropertyChoice>>(EngineClient.Json) ?? [];
    private void PropertyDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PropertyDatePicker.SelectedDate is DateTime date)
        {
            WriteValue.Text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (PropertyWeekday.ItemsSource != null)
                PropertyWeekday.SelectedValue = (((int)date.DayOfWeek + 6) % 7 + 1).ToString(CultureInfo.InvariantCulture);
        }
    }
    private void PropertyFlagChanged(object sender, RoutedEventArgs e) => WriteValue.Text = string.Concat(propertyFlagItems.Select(item => item.Enabled ? '1' : '0'));
    private void ApplyPropertyWriteResult(Peer peer, JsonElement result)
    {
        if (result.TryGetProperty("identity", out var identity))
        {
            var oldKey = identity.GetProperty("oldKey").GetString()!;
            var oldObject = identity.GetProperty("oldObject").GetString()!;
            var newObject = identity.GetProperty("newObject").GetString()!;
            var changed = Decode<Peer>(identity.GetProperty("peer"));
            peer.Key = changed.Key;
            peer.Device = changed.Device;
            peer.Instance = changed.Instance;
            peer.RefreshIdentity();
            dirty = true;
            foreach (var item in peer.Children.OfType<ObjectRow>().Concat(objects).Distinct().Where(item => item.Object == oldObject))
                item.Object = newObject;
            foreach (var watch in watches.Where(watch => watch.Device == oldKey).ToList())
                watch.Rebind(peer.Key, watch.Object == oldObject ? newObject : watch.Object, peer.Caption);
            if (ObjectId.Text == oldObject || ObjectId.Text.Equals(ObjectLabels.Display(oldObject), StringComparison.OrdinalIgnoreCase))
                ObjectId.Text = ObjectLabels.Display(newObject);
            if (priorityObject == oldObject)
                priorityObject = newObject;
            ObjectGrid.Items.Refresh();
            NavigationTitle.Text = focusedPeer?.Caption ?? "Devices";
        }
        SelectedDeviceTitle.Text = inspectedObject != null ? inspectedObject.Name + " · " + inspectedObject.ObjectLabel : peer.Caption + " · " + peer.Address;
        if (!result.TryGetProperty("readback", out var readback))
            return;
        foreach (var row in Rows(readback).Where(row => row.Error.Length == 0 && row.Property == "object-name"))
        {
            foreach (var item in peer.Children.OfType<ObjectRow>().Concat(objects).Distinct().Where(item => item.Object == row.Object))
                item.Name = row.EditorValue ?? row.DisplayValue;
            if (row.Object == peer.Device)
            {
                peer.Name = row.EditorValue ?? row.DisplayValue;
                dirty = true;
            }
            foreach (var watch in watches.Where(watch => watch.Device == peer.Key))
                watch.Rebind(peer.Key, watch.Object, peer.Caption);
            NavigationTitle.Text = focusedPeer?.Caption ?? "Devices";
            SelectedDeviceTitle.Text = inspectedObject != null ? inspectedObject.Name + " · " + inspectedObject.ObjectLabel : peer.Caption + " · " + peer.Address;
        }
    }

}
