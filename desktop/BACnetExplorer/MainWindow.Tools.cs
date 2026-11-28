using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace BACnetExplorer;

public partial class MainWindow
{
    private async void CreateDeviceObject(object sender, RoutedEventArgs e) => await Run("Create object", async () =>
    {
        var peer = CurrentPeer;
        using var initial = JsonDocument.Parse(InitialProperties.Text);
        if (initial.RootElement.ValueKind != JsonValueKind.Array || initial.RootElement.GetArrayLength() > 64)
            throw new InvalidDataException("Enter up to 64 initial properties as a JSON array.");
        var id = ManagementObject.Text.Trim();
        var allocate = AllocateInstance.IsChecked == true;
        var type = Integer(CreationType, 0, 1023);
        if (!ConfirmAction("Create object", $"{(allocate ? $"Allocate an instance of {BacnetDisplay.ChoiceValue("object-type", type.ToString(System.Globalization.CultureInfo.InvariantCulture))}" : $"Create {BacnetDisplay.ObjectReference(id)}")}\nInitial properties:\n{InitialProperties.Text}"))
            return;
        var result = await Engine.Call("create-object", new
        {
            device = peer.Key,
            @object = id,
            allocateInstance = allocate,
            objectType = type,
            initialValues = initial.RootElement,
            confirmed = true
        });
        ManagementObject.Text = result.GetProperty("object").GetString()!;
        ManagementResult.Text = "Creation acknowledged · " + ManagementObject.Text;
        await LoadInventory(peer, false);
    });

    private async void DeleteDeviceObject(object sender, RoutedEventArgs e) => await Run("Delete object", async () =>
    {
        var peer = CurrentPeer;
        var id = ManagementObject.Text.Trim();
        if (!ConfirmAction("Delete object", $"Permanently delete object {id} from this device."))
            return;
        await Engine.Call("delete-object", new
        {
            device = peer.Key,
            @object = id,
            confirmed = true
        });
        ManagementResult.Text = "Deletion acknowledged · " + id;
        await LoadInventory(peer, false);
    });

    private async void ControlCommunications(object sender, RoutedEventArgs e) => await Run("Communication control", async () =>
    {
        var mode = CommunicationMode.SelectedIndex == 0 ? 0 : 2;
        var minutes = Integer(CommunicationMinutes, 1, 65535);
        if (!ConfirmAction("Communication control", $"{(mode == 0 ? "Enable communication" : "Disable initiation")} for {minutes} minutes."))
            return;
        try
        {
            await Engine.Call("communication-control", new
            {
                device = CurrentPeer.Key,
                state = mode,
                minutes,
                password = CommunicationPassword.Password,
                confirmed = true
            });
            ManagementResult.Text = "Communication control acknowledged.";
        }
        finally { CommunicationPassword.Clear(); }
    });

    private async void ExportDeviceDatabase(object sender, RoutedEventArgs e) => await Run("Export device database", async () =>
    {
        var peer = CurrentPeer;
        var path = SavePath($"device-{peer.Instance}.json", "JSON|*.json");
        if (path == null)
            return;
        var inventory = await LoadInventory(peer, false);
        var ids = Decode<List<ObjectRow>>(inventory.GetProperty("objects"));
        var data = new List<System.Text.Json.Nodes.JsonNode?>();
        long bytes = 0;
        foreach (var item in ids)
        {
            if (cancelRequested)
                throw new OperationCanceledException("Database export canceled.");
            var result = await Engine.Call("properties", new
            {
                device = peer.Key,
                @object = item.Object
            });
            bytes += Encoding.UTF8.GetByteCount(result.GetRawText());
            if (bytes > 16 * 1024 * 1024)
                throw new IOException("Database export exceeds 16 MiB.");
            data.Add(WorkbenchExport.PropertyResult(result));
            ManagementResult.Text = $"Reading database · {data.Count} / {ids.Count} objects";
        }
        var output = JsonSerializer.Serialize(new
        {
            captured = DateTimeOffset.UtcNow,
            device = peer.Device,
            peer.Address,
            peer.Route,
            objects = data
        }, WorkbenchExport.CamelCase);
        if (Encoding.UTF8.GetByteCount(output) > Snapshot.MaximumFileBytes)
            throw new IOException("Readable database export exceeds 16 MiB.");
        SaveAtomic(path, output);
        ManagementResult.Text = $"Exported {ids.Count} objects with properties to {path}";
    });
    private bool ConfirmAction(string action, string detail) => MessageBox.Show(this,
        $"{CurrentPeer.Caption} at {CurrentPeer.Address}\nRoute {CurrentPeer.Route}\n\n{detail}\n\nContinue?", action,
        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async void ReadRouters(object sender, RoutedEventArgs e) => await Run("Discover routers", async () => Diagnostics.Text = Pretty(await Engine.Call("routers")));
    private async void InspectBbmd(object sender, RoutedEventArgs e) => await Run("Inspect BBMD", async () => Diagnostics.Text = Pretty(await Engine.Call("bbmd", new { target = Bbmd.Text.Trim().Length > 0 ? Bbmd.Text.Trim() : DiscoveryTarget.Text.Trim() })));
    private async void ReadTraffic(object sender, RoutedEventArgs e) => await Run("Received APDU metadata", async () => Diagnostics.Text = Pretty(await Engine.Call("traffic")));
    private async void SaveDiagnostics(object sender, RoutedEventArgs e) => await Run("Save diagnostics", () => { var path = SavePath("diagnostics.json", "JSON|*.json"); if (path != null) SaveAtomic(path, Diagnostics.Text); return Task.CompletedTask; });
    private static string Pretty(JsonElement result) => JsonSerializer.Serialize(WorkbenchExport.Diagnostic(result), WorkbenchExport.CamelCase);

    private async void AcknowledgeEvent(object sender, RoutedEventArgs e) => await Run("Acknowledge alarm", async () =>
    {
        if (EventGrid.SelectedItem is not EventRow row || row.Timestamps.Length != 3)
            throw new InvalidOperationException("Read events and select one first.");
        var transition = AckTransition.SelectedIndex;
        var source = AckSource.Text.Trim();
        var state = new uint[] { 2, 1, 0 }[transition];
        if (!ConfirmAction("Acknowledge alarm", $"Object {row.Object}\nTransition: {new[] { "TO_OFFNORMAL", "TO_FAULT", "TO_NORMAL" }[transition]}\nSource: {source}\nEvent timestamp: {row.Timestamps[transition]}"))
            return;
        await Engine.Call("acknowledge", new
        {
            device = CurrentPeer.Key,
            @object = row.Object,
            eventState = state,
            eventTime = row.Timestamps[transition],
            source,
            confirmed = true
        });
        Log("Alarm ACK", row.Object, "Acknowledged by device");
        Status.Text = "Alarm acknowledgment accepted. Read events again to refresh state.";
    });

    private async void SynchronizeTime(object sender, RoutedEventArgs e) => await Run("UTC synchronization", async () =>
    {
        if (!ConfirmAction("Synchronize device time", $"Send current host UTC ({DateTime.UtcNow:O}) to this device. This is an unconfirmed service; no device acknowledgment will be available."))
            return;
        Diagnostics.Text = Pretty(await Engine.Call("time-sync", new
        {
            device = CurrentPeer.Key,
            confirmed = true
        }));
        Status.Text = "UTC synchronization sent (unconfirmed). Read the device clock to verify.";
    });
    private async void RestartDevice(object sender, RoutedEventArgs e) => await Run("Restart device", async () =>
    {
        var state = RestartKind.SelectedIndex == 0 ? 1 : 0;
        if (!ConfirmAction("Restart device", $"Request {(state == 1 ? "warm" : "cold")} restart. This can interrupt controls and communications."))
            return;
        var password = RestartPassword.Password;
        try
        {
            await Engine.Call("restart", new
            {
                device = CurrentPeer.Key,
                state,
                password,
                confirmed = true
            });
            Status.Text = "Restart acknowledged. Rediscover after the device returns.";
        }
        finally { RestartPassword.Clear(); }
    });

    private async void DownloadFile(object sender, RoutedEventArgs e) => await Run("Download BACnet file", async () =>
    {
        var peer = CurrentPeer;
        var id = ObjectLabels.Numeric(FileObject.Text);
        var offset = (long)Integer(FileOffset, 0, int.MaxValue);
        var recordMode = RecordMode.IsChecked == true;
        var path = SavePath(recordMode ? "bacnet-records.json" : "bacnet-file.bin", recordMode ? "Base64 records JSON|*.json" : "Binary|*.bin|All files|*.*");
        if (path == null)
            return;
        using var stream = new MemoryStream();
        var records = new List<string>();
        long total = 0;
        for (var blocks = 0; blocks < 65536; blocks++)
        {
            if (cancelRequested)
                throw new OperationCanceledException("File download canceled. No output file was committed.");
            var result = await Engine.Call("file-read", new
            {
                device = peer.Key,
                @object = id,
                offset,
                recordMode,
                fileCount = recordMode ? 1 : Math.Clamp(peer.MaxApdu - 80, 32, 1024)
            });
            if (result.GetProperty("offset").GetInt64() != offset)
                throw new IOException("File response offset did not match the request.");
            long progress;
            if (recordMode)
            {
                var data = result.GetProperty("records");
                progress = data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : 0;
                if (progress > 0)
                    foreach (var record in data.EnumerateArray())
                    {
                        var text = record.GetString() ?? "";
                        total += Convert.FromBase64String(text).Length;
                        records.Add(text);
                    }
            }
            else
            {
                var data = Convert.FromBase64String(result.GetProperty("data").GetString() ?? "");
                progress = data.Length;
                total += data.Length;
                stream.Write(data);
            }
            if (total > 16 * 1024 * 1024)
                throw new IOException("Download exceeds the 16 MiB transfer limit.");
            FileProgress.Text = $"Received {total:N0} bytes · offset {offset} · block {blocks + 1}";
            if (result.GetProperty("eof").GetBoolean())
            {
                if (recordMode)
                    SaveAtomic(path, JsonSerializer.Serialize(records));
                else
                    SaveBytesAtomic(path, stream.ToArray());
                FileProgress.Text = $"Saved {total:N0} bytes to {path}. Download is a sequence of reads; a changing peer file is not an atomic snapshot.";
                return;
            }
            if (progress == 0)
                throw new IOException("File read made no progress before EOF.");
            offset += progress;
        }
        throw new IOException("Download exceeds 65,536 blocks.");
    });

    private async void UploadFile(object sender, RoutedEventArgs e) => await Run("Upload BACnet file", async () =>
    {
        var peer = CurrentPeer;
        var id = ObjectLabels.Numeric(FileObject.Text);
        var offset = (long)Integer(FileOffset, 0, int.MaxValue);
        var recordMode = RecordMode.IsChecked == true;
        var access = await Engine.Call("read", new
        {
            device = peer.Key,
            selections = new[] {
            new { @object = id, property = "read-only" }, new { @object = id, property = "file-access-method" } }
        });
        var reportedMode = PropertyEditPolicy.FileUploadRecordMode(peer.Key, id, Rows(access));
        if (reportedMode.HasValue)
        {
            recordMode = reportedMode.Value;
            RecordMode.IsChecked = recordMode;
        }
        var dialog = new OpenFileDialog { Filter = recordMode ? "Base64 records JSON|*.json" : "All files|*.*" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (new FileInfo(dialog.FileName).Length > 16 * 1024 * 1024)
            throw new IOException("Upload exceeds 16 MiB.");
        var data = recordMode ? [] : File.ReadAllBytes(dialog.FileName);
        var records = recordMode ? JsonSerializer.Deserialize<List<string>>(ReadBounded(dialog.FileName)) ?? throw new InvalidDataException("Expected a JSON array of base64 records.") : [];
        long total = recordMode ? records.Sum(r => (long)Convert.FromBase64String(r).Length) : data.Length;
        if (total > 16 * 1024 * 1024 || records.Count > 65536)
            throw new IOException("Record input exceeds transfer limits.");
        if (!ConfirmAction("Upload file", $"Object {id}\nSource: {dialog.FileName}\nBytes: {total:N0}\nStart {(recordMode ? "record" : "byte")}: {offset}\nOverwrite at this offset. Existing data after the upload is retained. Multiple blocks are not atomic; cancellation or failure can leave a partial upload."))
            return;
        var blockSize = Math.Clamp(peer.MaxApdu - 80, 32, 1024);
        long completed = 0;
        for (var position = 0; position < (recordMode ? records.Count : data.Length);)
        {
            if (cancelRequested)
                throw new OperationCanceledException($"Upload stopped after {completed:N0} bytes. Previously acknowledged blocks remain written.");
            var count = recordMode ? 1 : Math.Min(blockSize, data.Length - position);
            var block = recordMode ? null : Convert.ToBase64String(data, position, count);
            await Engine.Call("file-write", new
            {
                device = peer.Key,
                @object = id,
                offset,
                recordMode,
                data = block,
                records = recordMode ? new[] { records[position] } : null,
                confirmed = true
            });
            completed += recordMode ? Convert.FromBase64String(records[position]).Length : count;
            position += count;
            offset += count;
            FileProgress.Text = $"Acknowledged {completed:N0} / {total:N0} bytes";
        }
        FileProgress.Text = $"Upload acknowledged: {completed:N0} bytes. Download and compare to verify contents. Existing trailing bytes are retained.";
        Log("File upload", $"{peer.Caption} / {id}", FileProgress.Text);
    });

    private static void SaveBytesAtomic(string path, byte[] data)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
