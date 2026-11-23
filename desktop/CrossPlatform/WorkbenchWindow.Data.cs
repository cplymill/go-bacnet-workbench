using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace BACnetExplorer.CrossPlatform;

public sealed partial class WorkbenchWindow
{
    private Task AddWatch()
    {
        var row = property ?? throw new InvalidOperationException("Select a property first.");
        if (watches.Any(watch => watch.Identity == row.Identity)) return Task.CompletedTask;
        if (watches.Count >= 64) throw new InvalidOperationException("Watch limit is 64 selectors.");
        watches.Add(new Watch { Device = CurrentPeer.Key, Caption = CurrentPeer.Caption + " / " + ObjectLabels.Display(row.Object),
            Object = row.Object, Property = row.Property, Index = row.Index });
        return Task.CompletedTask;
    }
    private async Task Subscription(uint lifetime)
    {
        if (watchGrid.SelectedItem is not Watch watch) throw new InvalidOperationException("Select a watch first.");
        await SetSubscription(watch, lifetime);
    }
    private async Task SetSubscription(Watch watch, uint lifetime)
    {
        var process = watch.Process == 0 ? processId++ : watch.Process;
        await Engine.Call("subscribe", new { device = watch.Device, @object = watch.Object, process, lifetime });
        watch.Process = lifetime == 0 ? 0 : process;
        watch.RenewAt = DateTimeOffset.UtcNow.AddSeconds(45);
        watch.Mark(lifetime == 0 ? "COV canceled" : "COV subscribed");
    }
    private async Task RemoveWatch()
    {
        if (watchGrid.SelectedItem is not Watch watch) return;
        if (watch.Process != 0) await SetSubscription(watch, 0);
        watches.Remove(watch); chart.Watch = null; chart.InvalidateVisual();
    }
    private async Task CancelSubscriptions()
    {
        foreach (var watch in watches.Where(watch => watch.Process != 0).ToArray()) {
            try { await SetSubscription(watch, 0); }
            catch (Exception error) { watch.Process = 0; Log("Cancel COV", error.Message + "; finite subscription expires on the peer."); }
        }
    }
    private async Task Poll()
    {
        foreach (var group in watches.GroupBy(watch => watch.Device).ToArray()) {
            if (canceling || closing) break;
            var result = Rows(await Engine.Call("read", new { device = group.Key,
                selections = group.Select(watch => new { @object = watch.Object, property = watch.Property, index = watch.Index }) }));
            foreach (var watch in group) watch.Observe(result.FirstOrDefault(row => row.Identity == watch.Identity)
                ?? new PropertyRow { At = DateTimeOffset.UtcNow, Error = "No result returned for this watch." }, "Poll");
        }
        chart.InvalidateVisual();
    }
    private async Task Monitor()
    {
        if (!connected || busy || closing || monitoring != null) return;
        if (engine is not { IsAvailable: true }) { connected = polling = false; UpdateControls(); return; }
        var due = polling && nextPoll <= DateTimeOffset.UtcNow;
        var subscriptions = watches.Where(watch => watch.Process != 0).ToArray();
        if (!due && subscriptions.Length == 0) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitoring = done.Task;
        try {
            foreach (var watch in subscriptions.Where(watch => watch.RenewAt <= DateTimeOffset.UtcNow)) {
                if (busy || closing) return;
                await SetSubscription(watch, 120);
            }
            if (busy || closing) return;
            if (subscriptions.Length > 0) {
                var result = await Engine.Call("notifications", new { after = sequence });
                sequence = result.GetProperty("sequence").GetUInt64();
                foreach (var item in result.GetProperty("events").EnumerateArray()) {
                    var process = item.GetProperty("process").GetUInt32();
                    foreach (var row in Rows(item.GetProperty("rows")))
                        foreach (var watch in subscriptions.Where(watch => watch.Process == process && watch.Identity == row.Identity))
                            watch.Observe(row, "COV");
                }
            }
            if (due && !busy && !closing) {
                monitorReading = true;
                try { await Poll(); }
                finally { monitorReading = false; }
                nextPoll = DateTimeOffset.UtcNow.AddSeconds(Number(pollSeconds, 1, 3600));
            }
            chart.InvalidateVisual();
        }
        catch (Exception error) {
            if (!busy && !closing) {
                foreach (var watch in watches) watch.Mark("Monitor error");
                Log("Monitor", error.Message);
                if (engine is not { IsAvailable: true }) { connected = polling = false; UpdateControls(); }
            }
        }
        finally { monitoring = null; done.TrySetResult(); }
    }

    private async Task<string?> SavePath(string name)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "Save " + name, SuggestedFileName = name, ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }
    private async Task<string?> OpenPath()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private static string ReadBounded(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("File exceeds 16 MiB.");
        return File.ReadAllText(path);
    }
    private static void AtomicSave(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task ExportJson(string name, object data)
    {
        var path = await SavePath(name);
        if (path != null) AtomicSave(path, Encoding.UTF8.GetBytes(Pretty(data)));
    }
    private async Task ExportSamples()
    {
        var path = await SavePath("watch-samples.csv"); if (path == null) return;
        static string Cell(string text) {
            if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "device,object,property,time,value,error,source" };
        lines.AddRange(watches.SelectMany(watch => watch.Samples.Select(sample => string.Join(',',
            new[] { watch.Device, watch.Object, watch.Property, sample.At.ToString("O"), sample.Value, sample.Error, sample.Source }.Select(Cell)))));
        AtomicSave(path, Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }
    private async Task SaveWorkspace()
    {
        var profile = SelectedProfile();
        // A workspace may reference certificates but never persists its private key or an active join grant.
        profile.PrivateKey = ""; profile.ConfirmJoin = false;
        await ExportJson("workspace.bacnet.json", new Workspace {
            Connection = profile, Baseline = baseline.ToList(),
            Watches = watches.Select(watch => new Watch { Device = watch.Device, Object = watch.Object,
                Property = watch.Property, Index = watch.Index, Caption = watch.Caption }).ToList()
        });
    }
    private async Task OpenWorkspace()
    {
        if (connected) throw new InvalidOperationException("Disconnect before loading a workspace.");
        if (!await AdmitDraft()) return;
        var path = await OpenPath(); if (path == null) return;
        var data = JsonSerializer.Deserialize<Workspace>(ReadBounded(path), EngineClient.Json)
            ?? throw new InvalidDataException("Invalid workspace.");
        if (data.Version != 1 || data.Watches == null || data.Baseline == null || data.Connection == null ||
            data.Watches.Count > 64 || data.Baseline.Count > 30000 ||
            data.Watches.Select(watch => watch.Identity).Distinct().Count() != data.Watches.Count ||
            data.Baseline.Select(row => row.Identity).Distinct().Count() != data.Baseline.Count)
            throw new InvalidDataException("Workspace version, selectors or limits are invalid.");
        var normalized = Rows(await Engine.Call("normalize", new { selections = data.Watches.Select(watch =>
            new { @object = watch.Object, property = watch.Property, index = watch.Index }) }));
        if (normalized.Count != data.Watches.Count) throw new InvalidDataException("Workspace normalization is incomplete.");
        ApplyProfile(data.Connection); watches.Clear(); baseline.Clear(); baseline.AddRange(data.Baseline);
        for (var i = 0; i < data.Watches.Count; i++) {
            var watch = data.Watches[i]; watch.Object = normalized[i].Object; watch.Property = normalized[i].Property; watch.Index = normalized[i].Index;
            watch.Process = 0; watch.Samples.Clear(); watch.Mark("Disconnected"); watches.Add(watch);
        }
        status.Text = "Workspace loaded offline. Connect and rediscover before using its device bindings.";
    }
    private async Task CaptureBaseline()
    {
        var result = await Engine.Call("inventory", new { device = CurrentPeer.Key });
        if (result.TryGetProperty("enrichmentError", out var error)) throw new IOException("Snapshot incomplete: " + error.GetString());
        baseline.Clear(); baseline.AddRange(Rows(result.GetProperty("enrichment")));
        status.Text = $"Captured {baseline.Count} observations.";
    }
    private async Task CompareBaseline()
    {
        if (baseline.Count == 0) throw new InvalidOperationException("Capture or load a baseline first.");
        var result = await Engine.Call("inventory", new { device = CurrentPeer.Key });
        if (result.TryGetProperty("enrichmentError", out var error)) throw new IOException("Comparison incomplete: " + error.GetString());
        var before = baseline.ToDictionary(row => row.Identity);
        var after = Rows(result.GetProperty("enrichment")).ToDictionary(row => row.Identity);
        output.Text = Pretty(before.Keys.Union(after.Keys).Select(key => {
            before.TryGetValue(key, out var old); after.TryGetValue(key, out var current);
            var row = current ?? old!;
            return new Difference(row.Object, row.Selector, old?.Result ?? "", current?.Result ?? "",
                old == null ? "Added" : current == null ? "Removed" : old.Raw != current.Raw || old.Error != current.Error ? "Changed" : "Unchanged");
        }));
        status.Text = "Comparison ready in Tools & diagnostics.";
    }
}
