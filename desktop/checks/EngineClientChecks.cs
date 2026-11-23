using System.IO;
using System.Reflection;
using System.Text.Json;
using BACnetExplorer;

internal static class EngineClientChecks
{
    internal static EngineClient Start()
    {
        var previous = Environment.GetEnvironmentVariable("BACNET_WORKBENCH_ENGINE_FIXTURE");
        try
        {
            Environment.SetEnvironmentVariable("BACNET_WORKBENCH_ENGINE_FIXTURE", "1");
            return new EngineClient(Path.Combine(AppContext.BaseDirectory, "DesktopChecks.exe"));
        }
        finally { Environment.SetEnvironmentVariable("BACNET_WORKBENCH_ENGINE_FIXTURE", previous); }
    }
    private static async Task ExpectFailure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (IOException) { return; }
        catch (JsonException) { return; }
        catch (InvalidDataException) { return; }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Expected an immediate engine failure.");
    }
    private static Task Reader(EngineClient client) => (Task)typeof(EngineClient).GetField("reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
    internal static async Task Run()
    {
        var failures = new List<string>();
        async Task Case(string name, Func<Task> check)
        {
            try { await check(); Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL: " + failures[^1]); }
        }
        await Case("plain-language service errors retain diagnostics and keep the engine usable", async () =>
        {
            await using var client = Start();
            try { await client.Call("friendly-error"); throw new InvalidOperationException("Missing structured error."); }
            catch (OperationFailure ex)
            {
                if (!ex.Message.Contains("ReadRange") || !ex.Message.Contains("Read properties") || ex.Message.Contains("reason 9")
                    || !ex.TechnicalDetails.Contains("reason 9 (invoke 115)")) throw new InvalidOperationException("User explanation or original diagnostics lost.");
            }
            try { await client.Call("plain-error"); throw new InvalidOperationException("Missing validation error."); }
            catch (IOException ex) when (ex is not OperationFailure)
            {
                if (ex.Message != "MAC must be 0..127") throw new InvalidOperationException("Validation wording changed.");
            }
            await client.Call("echo");
            if (!client.IsAvailable) throw new InvalidOperationException("A device service error killed the connection.");
        });
        await Case("engine EOF rejects later requests immediately", async () =>
        {
            await using var client = Start();
            await client.Call("exit");
            await Reader(client).WaitAsync(TimeSpan.FromSeconds(2));
            await ExpectFailure(client.Call("echo"));
        });
        await Case("corrupt response rejects active and later requests", async () =>
        {
            await using var client = Start();
            await ExpectFailure(client.Call("corrupt"));
            await ExpectFailure(client.Call("echo"));
        });
        await Case("progress callback failure does not strand later requests", async () =>
        {
            await using var client = Start();
            await ExpectFailure(client.Call("progress", onProgress: _ => throw new InvalidDataException("Simulated UI callback failure")));
            await ExpectFailure(client.Call("echo"));
        });
        await Case("oversized request rejected before transmission", async () =>
        {
            await using var client = Start();
            try { await client.Call("echo", new { text = new string('x', 1024 * 1024) }).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (InvalidDataException)
            {
                await client.Call("echo").WaitAsync(TimeSpan.FromSeconds(1));
                return;
            }
            throw new InvalidOperationException("Request beyond the engine's 1 MiB envelope was transmitted.");
        });
        await Case("cancel bypasses an active operation and recovers", async () =>
        {
            await using var client = Start();
            var held = client.Call("hold");
            await client.Call("cancel").WaitAsync(TimeSpan.FromSeconds(1));
            await ExpectFailure(held);
            await client.Call("echo").WaitAsync(TimeSpan.FromSeconds(1));
        });
        foreach (var mode in new[] { "ignore-cancel", "ack-ignore-cancel" })
        {
            await Case("cancellation bounds an unresponsive engine: " + mode, async () =>
            {
                await using var client = Start();
                var active = client.Call(mode);
                var queued = client.Call("echo");
                try { await client.Call("cancel").WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (IOException) { }
                await ExpectFailure(active);
                await ExpectFailure(queued);
                if (client.IsAvailable) throw new InvalidOperationException("Unresponsive engine remained usable.");
            });
        }
        await Case("concurrent requests remain serialized across a burst", async () =>
        {
            await using var client = Start();
            var replies = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => client.Call("echo", new { value = i }))).WaitAsync(TimeSpan.FromSeconds(5));
            if (replies.Where((reply, index) => reply.GetProperty("value").GetInt32() != index).Any()) throw new InvalidOperationException("IPC response identity crossed requests.");
        });
        await Case("disposal drains active and queued requests", async () =>
        {
            var client = Start();
            var active = client.Call("hold");
            var queued = client.Call("echo");
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await ExpectFailure(active); await ExpectFailure(queued);
            await ExpectFailure(client.Call("echo"));
            await client.DisposeAsync();
        });
        await Case("shutdown escapes an engine that stops reading its input", async () =>
        {
            var client = Start();
            await client.Call("stop-reading");
            var blocked = client.Call("echo", new { text = new string('x', 700000) });
            await Task.Delay(100);
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            await ExpectFailure(blocked);
        });
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }
    internal static void Serve()
    {
        long held = 0;
        string? cancellationMode = null;
        void Reply(long id, object result) => Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
        while (Console.ReadLine() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var op = root.GetProperty("op").GetString();
            if (op == "exit") { Reply(id, true); return; }
            if (op == "stop-reading") { Reply(id, true); Thread.Sleep(30000); return; }
            if (op == "discover")
            {
                held = id;
                Console.WriteLine(JsonSerializer.Serialize(new { id, progress = true, result = new[] {
                    new { key = root.GetProperty("args").GetProperty("target").GetString(), device = "8:1", address = root.GetProperty("args").GetProperty("target").GetString(), name = "Retained device" }
                } }));
                continue;
            }
            if (op == "subscribe") { held = id; continue; }
            if (op == "hold") { held = id; continue; }
            if (op is "ignore-cancel" or "ack-ignore-cancel") { held = id; cancellationMode = op; continue; }
            if (op == "cancel")
            {
                if (cancellationMode != null)
                {
                    if (cancellationMode == "ack-ignore-cancel") Reply(id, true);
                    continue;
                }
                if (held != 0) Console.WriteLine(JsonSerializer.Serialize(new { id = held, error = "Operation canceled" }));
                held = 0; Reply(id, true); continue;
            }
            if (op == "friendly-error")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { id, error = "remote BACnet Reject reason 9 (invoke 115)", failure = new {
                    message = "This device does not recognize requests to read a range of list or log entries (ReadRange).",
                    action = "Use Read properties for point values. Reading stored history requires a device that supports ReadRange."
                } }));
                continue;
            }
            if (op == "plain-error") { Console.WriteLine(JsonSerializer.Serialize(new { id, error = "MAC must be 0..127" })); continue; }
            if (op == "corrupt") { Console.WriteLine("{invalid JSON}"); continue; }
            if (op == "progress") Console.WriteLine(JsonSerializer.Serialize(new { id, progress = true, result = new { value = 1 } }));
            Reply(id, root.GetProperty("args").Clone());
        }
    }
}
