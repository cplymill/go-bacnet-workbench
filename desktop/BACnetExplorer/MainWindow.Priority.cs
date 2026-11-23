using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace BACnetExplorer;

public partial class MainWindow
{
    private sealed record PriorityChange(string Type, string Value);
    private readonly SortedDictionary<int, PriorityChange> priorityChanges = [];
    private Peer? priorityChangePeer;
    private string priorityChangeObject = "";
    private string priorityEditingValue = "", priorityEditingType = "", priorityValueType = "";

    private void RenderPriorityChanges()
    {
        var values = prioritySnapshot.ToArray();
        if (priorityChangePeer?.Key == priorityPeer?.Key && priorityChangeObject == priorityObject)
            foreach (var (slot, change) in priorityChanges)
                if (values.Length > slot - 1) values[slot - 1] = change.Value + " *";
        PriorityGrid.ItemsSource = values.Length == 0 ? null : new[] { values };
        WritePriorityButton.IsEnabled = priorityChanges.Count > 0;
        PriorityPendingSummary.Text = priorityChanges.Count == 0 ? "No pending writes." : $"{priorityChanges.Count} pending change(s) · * marks an unsent value · click Write changes to send";
    }
    private void ClearPriorityChanges()
    {
        priorityChanges.Clear(); priorityChangePeer = null; priorityChangeObject = "";
        RenderPriorityChanges();
    }
    private bool CanLeaveInspector()
    {
        if (objectEditor != null && !objectEditor.CanLeave()) return false;
        return (priorityChanges.Count == 0 || MessageBox.Show(this, "Discard the pending PV bar writes?", "Pending priority changes", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) && CanLeavePropertyDraft();
    }
    private void PreparePriorityEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is not TextBox editor) return;
        var value = editor.Text;
        if (priorityChanges.TryGetValue(e.Column.DisplayIndex + 1, out var pending))
        {
            editor.Text = pending.Value;
            priorityEditingValue = pending.Value;
            priorityEditingType = pending.Type;
            editor.SelectAll();
            return;
        }
        if (value.StartsWith("enumerated:", StringComparison.Ordinal)) value = value[11..];
        else if (value.StartsWith('"')) value = JsonSerializer.Deserialize<string>(value) ?? value;
        else if (value == "null") value = "";
        editor.Text = value;
        priorityEditingValue = value;
        priorityEditingType = priorityValueType;
        var original = value;
        var slot = e.Column.DisplayIndex + 1;
        editor.TextChanged += (_, _) =>
        {
            var edited = editor.Text != original;
            WritePriorityButton.IsEnabled = edited || priorityChanges.Count > 0;
            if (edited) PriorityPendingSummary.Text = $"Editing PV{slot} · click Write changes to send";
            else if (priorityChanges.Count == 0) PriorityPendingSummary.Text = "No pending writes.";
        };
        editor.SelectAll();
    }

    private async void PriorityCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Column.DisplayIndex >= 16 || e.EditingElement is not TextBox editor) return;
        e.Cancel = true;
        if (priorityPeer == null) return;
        var slot = e.Column.DisplayIndex + 1;
        var value = editor.Text;
        var type = priorityValueType;
        if (type.Length == 0 || type == "null") { Status.Text = "Read the present value type before editing a priority."; return; }
        if (value != priorityEditingValue || type != priorityEditingType)
        {
            priorityChangePeer = priorityPeer;
            priorityChangeObject = priorityObject;
            priorityChanges[slot] = new PriorityChange(type, value);
        }
        await Dispatcher.InvokeAsync(() =>
        {
            PriorityGrid.CancelEdit(DataGridEditingUnit.Cell);
            PriorityGrid.CancelEdit(DataGridEditingUnit.Row);
            RenderPriorityChanges();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void WritePriorityChanges(object sender, RoutedEventArgs e)
    {
        PriorityGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        await Run("Write priority changes", async () =>
        {
            await Dispatcher.InvokeAsync(() =>
            {
                PriorityGrid.CancelEdit(DataGridEditingUnit.Cell);
                PriorityGrid.CancelEdit(DataGridEditingUnit.Row);
            }, System.Windows.Threading.DispatcherPriority.Background);
            var peer = priorityChangePeer;
            var id = priorityChangeObject;
            if (peer == null || !connected) throw new InvalidOperationException("Select a connected commandable object first.");
            var changes = priorityChanges.ToArray();
            if (changes.Length == 0) return;
            foreach (var (slot, _) in changes) PropertyEditPolicy.ValidatePriority(slot);
            var preview = string.Join("\n", changes.Select(c => $"PV{c.Key}: {c.Value.Type} {c.Value.Value}"));
            if (changes.Length > 1 && !ConfirmPriority(peer, id, preview + "\nEach priority is an independent write.")) return;
            string? failure = null;
            foreach (var (slot, change) in changes)
            {
                if (cancelRequested) break;
                try
                {
                    var result = await Engine.Call("write", new { device = peer.Key, @object = id, property = "present-value", valueType = change.Type, value = change.Value, priority = slot, confirmed = true });
                    priorityChanges.Remove(slot);
                    Log("Priority command", $"{peer.Caption} / {id} / PV{slot}", result.TryGetProperty("readbackError", out var error) ? "ACK; " + error.GetString() : "Acknowledged with readback");
                }
                catch (Exception ex)
                {
                    failure = $"PV{slot}: {ex.Message}. Remaining changes are unsent; refresh before retrying an uncertain result.";
                    Log("Priority command", $"{peer.Caption} / {id} / PV{slot}", failure);
                    break;
                }
            }
            await LoadProperties(peer, id);
            Status.Text = failure ?? $"Priority writes complete · {priorityChanges.Count} pending";
        });
    }

    private bool ConfirmPriority(Peer peer, string id, string detail) => MessageBox.Show(this,
        $"{peer.Caption} at {peer.Address}\nRoute {peer.Route}\nObject {id} / present-value\n\n{detail}\n\nContinue?",
        "Priority command", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async void RelinquishPriority(object sender, RoutedEventArgs e)
    {
        var slot = PriorityGrid.SelectedCells.Count == 0 ? -1 : PriorityGrid.SelectedCells[0].Column.DisplayIndex;
        if (slot == 5) { Status.Text = "PV6 is reserved for device timing and cannot be relinquished by this tool."; return; }
        if (slot < 0 || slot > 15)
        {
            Status.Text = "Select one of the PV1–PV16 cells to relinquish.";
            return;
        }
        await ReleasePriorities([slot + 1]);
    }

    private async void RelinquishAllPriorities(object sender, RoutedEventArgs e) => await ReleasePriorities(Enumerable.Range(1, 16).ToArray());

    private async Task ReleasePriorities(int[] slots) => await Run("Relinquish priorities", async () =>
    {
        var peer = priorityPeer ?? throw new InvalidOperationException("Select a commandable object first.");
        var id = priorityObject;
        slots = slots.Where(slot => slot != 6).ToArray();
        if (slots.Length == 0) return;
        if (slots.Length > 1 && !ConfirmPriority(peer, id, $"Write NULL at {(slots.Length == 15 ? "all fifteen operator priorities (PV6 is reserved)" : $"PV{slots[0]}")}.\nThe device chooses its resulting value from the remaining commands and RD. Each slot is an independent request.")) return;
        foreach (var slot in slots) priorityChanges.Remove(slot);
        var released = 0;
        var failures = new List<string>();
        foreach (var slot in slots)
        {
            if (cancelRequested) break;
            Status.Text = $"Relinquishing PV{slot} · {released} acknowledged";
            try
            {
                await Engine.Call("write", new { device = peer.Key, @object = id, property = "present-value", valueType = "null", value = "", priority = slot, confirmed = true });
                released++;
                Log("Relinquish", $"{peer.Caption} / {id} / PV{slot}", "NULL acknowledged");
            }
            catch (Exception ex)
            {
                failures.Add($"PV{slot}: {ex.Message}");
                Log("Relinquish", $"{peer.Caption} / {id} / PV{slot}", "Failed / outcome may be uncertain: " + ex.Message);
            }
        }
        await LoadProperties(peer, id);
        Status.Text = $"Relinquish · {released}/{slots.Length} acknowledged" + (failures.Count == 0 ? "" : " · " + string.Join("; ", failures));
    });
}
