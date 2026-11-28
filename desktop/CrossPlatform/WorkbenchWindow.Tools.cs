using System.Text.Json;
using Avalonia.Controls;

namespace BACnetExplorer.CrossPlatform;

public sealed partial class WorkbenchWindow
{
    private readonly TextBox rangeStart = Input("1", "Start sequence"), rangeCount = Input("50", "Count");
    private readonly ComboBox transition = new() { ItemsSource = new[] { "To offnormal", "To fault", "To normal" }, SelectedIndex = 0 };
    private readonly TextBox ackSource = Input("Go BACnet Workbench", "Acknowledgement source");
    private string eventDevice = "";
    private readonly TextBox fileObject = Input("FILE:1", "File object"), fileOffset = Input("0", "Offset");
    private readonly CheckBox fileRecords = new() { Content = "Record mode" };
    private readonly TextBox managementObject = Input("AV:1", "Object identifier"), creationType = Input("2", "Object type");
    private readonly CheckBox allocateInstance = new() { Content = "Allocate instance" };
    private readonly TextBox initialValues = JsonBox("[]");
    private readonly TextBox devicePassword = new() { PlaceholderText = "Device password (never saved)", PasswordChar = '●', MinWidth = 160 };
    private readonly TextBox communicationMinutes = Input("1", "Minutes");
    private readonly ComboBox advancedOperation = new()
    {
        ItemsSource = new[] {
        "read", "properties", "inventory", "range", "events", "write", "subscribe", "file-read", "file-write",
        "acknowledge", "time-sync", "restart", "create-object", "delete-object", "communication-control",
        "schedule-read", "schedule-write", "calendar-read", "calendar-write", "normalize"
    },
        SelectedIndex = 0
    };
    private readonly TextBox advancedArguments = JsonBox("{\n  \"selections\": [{\"object\": \"8:1\", \"property\": \"object-name\"}]\n}");

    private Control Alarms() => Page(Row(Button("Read active events", ReadEvents), transition, ackSource,
        Button("Acknowledge selected event", Acknowledge)), eventGrid,
        Row(Label("TrendLog starting sequence", rangeStart), Label("Count", rangeCount), Button("Read log entries", ReadRange)),
        diagnostics);

    private Control Tools()
    {
        var sections = new StackPanel
        {
            Spacing = 10,
            Children = {
            Row(Button("Connection status", () => Diagnostic("status")), Button("Routers", () => Diagnostic("routers")),
                Button("BBMD tables", () => Diagnostic("bbmd", new { target = target.Text?.Trim() ?? "" })),
                Button("Received APDU metadata", () => Diagnostic("traffic")),
                Button("Export result", () => ExportJson("diagnostics.json", ParseOutput(output)))),
            new Expander { Header = "BACnet file transfer", Content = new StackPanel { Spacing = 8, Children = {
                Row(fileObject, fileOffset, fileRecords, Button("Download", DownloadFile), Button("Upload", UploadFile)),
                new TextBlock { Text = "Transfers are bounded to 16 MiB. Upload preserves trailing data and is not atomic.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }
            } } },
            new Expander { Header = "Device management", Content = new StackPanel { Spacing = 8, Children = {
                Row(managementObject, creationType, allocateInstance), initialValues,
                Row(Button("Create object", CreateObject), Button("Delete object", DeleteObject),
                    Button("Export device database", ExportDatabase)),
                Row(devicePassword, Button("Warm restart", () => Restart(1)), Button("Cold restart", () => Restart(0)),
                    Button("Synchronize UTC", SynchronizeTime)),
                Row(communicationMinutes, Button("Enable communications", () => Communication(0)),
                    Button("Disable initiation", () => Communication(2)))
            } } },
            new Expander { Header = "Advanced typed operation", Content = new StackPanel { Spacing = 8, Children = {
                new TextBlock { Text = "Arguments use the documented private adapter schema. Device identity is pinned to the selected device. Mutations require a target-specific preview.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                advancedOperation, advancedArguments, Button("Run advanced operation", Advanced)
            } } }
        }
        };
        return Page(new ScrollViewer { Content = sections, MaxHeight = 350 }, output);
    }
    private static JsonElement ParseOutput(TextBox box)
    {
        using var document = JsonDocument.Parse(box.Text ?? "{}");
        return document.RootElement.Clone();
    }
    private async Task Diagnostic(string operation, object? args = null) => output.Text = Pretty(await Engine.Call(operation, args));
    private async Task ReadEvents()
    {
        var selected = CurrentPeer;
        var result = await Engine.Call("events", new
        {
            device = selected.Key
        });
        // Bind acknowledgement to the peer that supplied these timestamps.
        eventDevice = selected.Key;
        events.Clear();
        foreach (var item in Decode<List<EventRow>>(result))
            events.Add(item);
    }
    private async Task Acknowledge()
    {
        var selected = CurrentPeer;
        if (selected.Key != eventDevice || eventGrid.SelectedItem is not EventRow row || row.Timestamps.Length != 3)
            throw new InvalidOperationException("Read events for the currently selected device and select one first.");
        var index = transition.SelectedIndex;
        if (!await ConfirmTarget("Acknowledge alarm", $"Event object {row.Object}\n{transition.SelectedItem}\nSource {ackSource.Text}"))
            return;
        await Engine.Call("acknowledge", new
        {
            device = selected.Key,
            @object = row.Object,
            eventState = new uint[] { 2, 1, 0 }[index],
            eventTime = row.Timestamps[index],
            source = ackSource.Text,
            confirmed = true
        });
        await ReadEvents();
    }
    private async Task ReadRange() => diagnostics.Text = Pretty(await Engine.Call("range", new
    {
        device = CurrentPeer.Key,
        @object = objectId,
        property = "log-buffer",
        start = (ulong)Number(rangeStart, 1, int.MaxValue),
        count = Number(rangeCount, 1, 256)
    }));
    private async Task CreateObject()
    {
        using var initial = JsonDocument.Parse(initialValues.Text ?? "[]");
        if (initial.RootElement.ValueKind != JsonValueKind.Array || initial.RootElement.GetArrayLength() > 64)
            throw new InvalidDataException("Use up to 64 initial values.");
        var id = ObjectLabels.Numeric(managementObject.Text ?? "");
        var allocate = allocateInstance.IsChecked == true;
        var type = Number(creationType, 0, 1023);
        var label = allocate ? BacnetDisplay.ChoiceValue("object-type", type.ToString(System.Globalization.CultureInfo.InvariantCulture)) : BacnetDisplay.ObjectReference(id);
        if (!await ConfirmTarget("Create object", allocate ? $"Allocate an instance of {label}." : $"Create {label}."))
            return;
        output.Text = Pretty(await Engine.Call("create-object", new
        {
            device = CurrentPeer.Key,
            @object = id,
            objectType = type,
            allocateInstance = allocate,
            initialValues = initial.RootElement,
            confirmed = true
        }));
        await ReadInventory(false);
    }
    private async Task DeleteObject()
    {
        var id = ObjectLabels.Numeric(managementObject.Text ?? "");
        if (!await ConfirmTarget("Delete object", "Permanently delete " + id + " from the selected device?"))
            return;
        await Engine.Call("delete-object", new
        {
            device = CurrentPeer.Key,
            @object = id,
            confirmed = true
        });
        await ReadInventory(false);
    }
    private async Task Restart(int state)
    {
        if (!await ConfirmTarget("Restart device", $"Request {(state == 1 ? "warm" : "cold")} restart. This can interrupt controls and communications."))
            return;
        try
        {
            output.Text = Pretty(await Engine.Call("restart", new
            {
                device = CurrentPeer.Key,
                state,
                password = devicePassword.Text ?? "",
                confirmed = true
            }));
        }
        finally { devicePassword.Text = ""; }
    }
    private async Task SynchronizeTime()
    {
        if (!await ConfirmTarget("Synchronize UTC", $"Send current host UTC {DateTime.UtcNow:O}. This service is unconfirmed; read the device clock afterward."))
            return;
        output.Text = Pretty(await Engine.Call("time-sync", new
        {
            device = CurrentPeer.Key,
            confirmed = true
        }));
    }
    private async Task Communication(int state)
    {
        var minutes = Number(communicationMinutes, 1, 65535);
        if (!await ConfirmTarget("Communication control", $"{(state == 0 ? "Enable communication" : "Disable initiation")} for {minutes} minutes."))
            return;
        try
        {
            output.Text = Pretty(await Engine.Call("communication-control", new
            {
                device = CurrentPeer.Key,
                state,
                minutes,
                password = devicePassword.Text ?? "",
                confirmed = true
            }));
        }
        finally { devicePassword.Text = ""; }
    }
    private async Task ExportDatabase()
    {
        var selected = CurrentPeer;
        var path = await SavePath("device-database.json");
        if (path == null)
            return;
        var result = await Engine.Call("object-list", new
        {
            device = selected.Key
        });
        var data = new List<System.Text.Json.Nodes.JsonNode?>();
        long bytes = 0;
        foreach (var item in Decode<List<ObjectRow>>(result.GetProperty("objects")))
        {
            if (canceling || closing)
                throw new OperationCanceledException();
            var rows = await Engine.Call("properties", new
            {
                device = selected.Key,
                @object = item.Object
            });
            bytes += System.Text.Encoding.UTF8.GetByteCount(rows.GetRawText());
            if (bytes > 16 * 1024 * 1024)
                throw new InvalidDataException("Device database exceeds 16 MiB.");
            data.Add(WorkbenchExport.PropertyResult(rows));
        }
        var database = System.Text.Encoding.UTF8.GetBytes(Pretty(new
        {
            device = selected.Device,
            selected.Address,
            selected.Route,
            captured = DateTimeOffset.UtcNow,
            objects = data
        }));
        if (database.Length > Snapshot.MaximumFileBytes)
            throw new System.IO.InvalidDataException("Readable database export exceeds 16 MiB.");
        AtomicSave(path, database);
    }
    private async Task DownloadFile()
    {
        var selected = CurrentPeer;
        var id = ObjectLabels.Numeric(fileObject.Text ?? "");
        var offset = (long)Number(fileOffset, 0, int.MaxValue);
        var recordMode = fileRecords.IsChecked == true;
        var path = await SavePath(recordMode ? "bacnet-records.json" : "bacnet-file.bin");
        if (path == null)
            return;
        using var stream = new MemoryStream();
        var records = new List<string>();
        for (var block = 0; block < 65536; block++)
        {
            if (canceling || closing)
                throw new OperationCanceledException();
            var result = await Engine.Call("file-read", new
            {
                device = selected.Key,
                @object = id,
                offset,
                recordMode,
                fileCount = recordMode ? 1 : Math.Clamp(selected.MaxApdu - 80, 32, 1024)
            });
            if (result.GetProperty("offset").GetInt64() != offset)
                throw new InvalidDataException("File read offset differs from request.");
            var count = 0;
            if (recordMode)
            {
                foreach (var entry in result.GetProperty("records").EnumerateArray())
                {
                    var bytes = Convert.FromBase64String(entry.GetString()!);
                    stream.Write(bytes);
                    records.Add(entry.GetString()!);
                    count++;
                }
            }
            else
            {
                var bytes = Convert.FromBase64String(result.GetProperty("data").GetString()!);
                stream.Write(bytes);
                count = bytes.Length;
            }
            if (stream.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("File exceeds 16 MiB.");
            if (result.GetProperty("eof").GetBoolean())
            {
                AtomicSave(path, recordMode ? System.Text.Encoding.UTF8.GetBytes(Pretty(records)) : stream.ToArray());
                return;
            }
            if (count == 0)
                throw new InvalidDataException("File made no progress.");
            offset += count;
        }
        throw new InvalidDataException("File exceeds the transfer block limit.");
    }
    private async Task UploadFile()
    {
        var selected = CurrentPeer;
        var id = ObjectLabels.Numeric(fileObject.Text ?? "");
        var recordMode = fileRecords.IsChecked == true;
        var observed = Rows(await Engine.Call("properties", new
        {
            device = selected.Key,
            @object = id
        }));
        var access = PropertyEditPolicy.FileUploadRecordMode(selected.Key, id, observed);
        if (access == null || access.Value != recordMode)
            throw new InvalidOperationException("Read the file and choose its observed access method before uploading.");
        var path = await OpenPath();
        if (path == null)
            return;
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Input exceeds 16 MiB.");
        var bytes = File.ReadAllBytes(path);
        var records = recordMode ? JsonSerializer.Deserialize<List<string>>(System.Text.Encoding.UTF8.GetString(bytes))
            ?? throw new InvalidDataException("Expected base64 record list.") : [];
        if (records.Count > 65536)
            throw new InvalidDataException("Too many file records.");
        if (recordMode && records.Sum(record => (long)Convert.FromBase64String(record).Length) > 16 * 1024 * 1024)
            throw new InvalidDataException("Decoded records exceed 16 MiB.");
        if (!await ConfirmTarget("Upload file", $"Overwrite {id} starting at {fileOffset.Text}. Upload is not atomic; trailing data is retained."))
            return;
        long offset = Number(fileOffset, 0, int.MaxValue);
        var blockSize = Math.Clamp(selected.MaxApdu - 80, 32, 1024);
        for (var position = 0; position < (recordMode ? records.Count : bytes.Length);)
        {
            if (canceling || closing)
                throw new OperationCanceledException("Upload stopped; earlier acknowledged blocks remain written.");
            var count = recordMode ? 1 : Math.Min(blockSize, bytes.Length - position);
            await Engine.Call("file-write", new
            {
                device = selected.Key,
                @object = id,
                offset,
                recordMode,
                data = recordMode ? null : Convert.ToBase64String(bytes, position, count),
                records = recordMode ? new[] { records[position] } : null,
                confirmed = true
            });
            offset += count;
            position += count;
        }
        status.Text = "Upload acknowledged. Download and compare to verify content.";
    }
    private async Task Advanced()
    {
        var operation = advancedOperation.SelectedItem as string ?? "read";
        if ((advancedArguments.Text?.Length ?? 0) > 512 * 1024)
            throw new InvalidDataException("Advanced arguments exceed 512 KiB.");
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(advancedArguments.Text ?? "{}")
            ?? throw new InvalidDataException("Expected a JSON argument object.");
        if (operation != "normalize")
            args["device"] = JsonSerializer.SerializeToElement(CurrentPeer.Key);
        var mutating = operation is "write" or "file-write" or "acknowledge" or "time-sync" or "restart" or
            "create-object" or "delete-object" or "communication-control" or "schedule-write" or "calendar-write";
        if (mutating)
        {
            if (args.TryGetValue("priority", out var suppliedPriority))
                PropertyEditPolicy.ValidatePriority(suppliedPriority.GetInt32());
            if (operation == "write")
            {
                if (!args.TryGetValue("object", out var objectArgument) || !args.TryGetValue("property", out var propertyArgument))
                    throw new InvalidDataException("A write requires object and property selectors.");
                var normalized = Rows(await Engine.Call("normalize", new
                {
                    selections = new[] {
                    new { @object = objectArgument.GetString(), property = propertyArgument.GetString(),
                        index = args.TryGetValue("index", out var indexArgument) && indexArgument.ValueKind == JsonValueKind.Number ? (uint?)indexArgument.GetUInt32() : null }
                }
                }));
                var selector = normalized.Single();
                var context = Rows(await Engine.Call("properties", new
                {
                    device = CurrentPeer.Key,
                    @object = selector.Object
                }));
                var row = context.FirstOrDefault(item => item.Object == selector.Object && item.Property == selector.Property && item.Index == selector.Index)
                    ?? throw new InvalidOperationException("Read the selected property before an advanced write.");
                var selectedPriority = args.TryGetValue("priority", out var priorityArgument) ? priorityArgument.GetInt32() : 0;
                var relinquish = args.TryGetValue("valueType", out var typeArgument) && typeArgument.GetString() == "null";
                PropertyEditPolicy.RequireWrite(row, context, selectedPriority, true, relinquish);
            }
            var preview = args.ToDictionary(pair => pair.Key,
                pair => pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase) ? "[redacted]" : pair.Value.ToString());
            if (!await ConfirmTarget("Advanced " + operation, Pretty(preview)))
                return;
            args["confirmed"] = JsonSerializer.SerializeToElement(true);
        }
        output.Text = Pretty(await Engine.Call(operation, args));
        if (mutating)
            status.Text = "Operation returned. Read back the affected state before retrying.";
    }
}
