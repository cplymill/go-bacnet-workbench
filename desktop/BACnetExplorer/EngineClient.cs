using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BACnetExplorer;

// The child has no listening HTTP port. Only this process's inherited pipes
// carry commands; all UI file dialogs and persistence stay in the desktop app.
internal sealed class EngineClient : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };
    private readonly Process process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<long, Func<JsonElement, Task>> progress = new();
    private readonly ConcurrentDictionary<long, string> operationNames = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object state = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly Task reader;
    private readonly Task errors;
    private IOException? failure;
    private Task? disposal;
    private long nextId;
    private bool disposed;

    internal bool IsAvailable { get { lock (state) return !disposed && failure == null; } }

    internal EngineClient(string? enginePath = null)
    {
        var path = enginePath ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "bacnet-explorer-engine.exe" : "bacnet-explorer-engine");
        if (!File.Exists(path)) throw new FileNotFoundException("The BACnet engine is missing. Run desktop/build.ps1 and launch the packaged app.", path);
        process = new Process { StartInfo = new ProcessStartInfo(path) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        }};
        try { process.Start(); }
        catch { process.Dispose(); throw; }
        reader = Task.Run(ReadResponses);
        errors = Task.Run(DrainErrors);
    }

    private void RequireAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failure != null) throw failure;
    }

    private void Fail(IOException error)
    {
        lock (state)
        {
            failure ??= error;
            foreach (var completion in pending.Values) completion.TrySetException(failure);
        }
        stopped.Cancel();
        // An unread reply must never leave a possibly accepted write running unseen.
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal async Task<JsonElement> Call(string operation, object? args = null, Func<JsonElement, Task>? onProgress = null)
    {
        var control = operation == "cancel";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(control ? 3 : 195));
        var token = deadline.Token;
        Task[] canceling = [];
        if (control)
        {
            lock (state)
                canceling = pending.Where(pair => operationNames.GetValueOrDefault(pair.Key) != "cancel")
                    .Select(pair => (Task)pair.Value.Task).ToArray();
        }
        if (!control) await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref nextId);
            var encoded = JsonSerializer.Serialize(new { id, op = operation, args = args ?? new { } }, Json);
            if (Encoding.UTF8.GetByteCount(encoded) + 1 >= 1024 * 1024)
                throw new InvalidDataException("Request exceeds 1 MiB; narrow the selection or transfer block.");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (state)
            {
                RequireAvailable();
                pending[id] = completion;
                operationNames[id] = operation;
                if (onProgress != null) progress[id] = onProgress;
            }
            try
            {
                if (!await writer.WaitAsync(TimeSpan.FromSeconds(control ? 2 : 195), token).ConfigureAwait(false))
                {
                    Fail(new IOException("The engine could not accept the request or cancellation. Reconnect and read back before retrying any write."));
                    throw failure!;
                }
                try
                {
                    lock (state) RequireAvailable();
                    var writing = WriteRequest(encoded, token);
                    try { await writing.WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        // Stop this child before releasing the writer. Merely
                        // abandoning WriteAsync would race StreamWriter.Close.
                        Fail(new IOException("The engine input stopped responding. Connect to recover; read back before retrying any write."));
                        await ObserveCompletion(writing).ConfigureAwait(false);
                        throw failure!;
                    }
                }
                catch (OperationCanceledException) when (stopped.IsCancellationRequested)
                {
                    throw failure ?? new IOException("The engine connection was closed.");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    Fail(new IOException("The engine connection failed. Reconnect and read back before retrying any write.", ex));
                    throw failure!;
                }
                finally { writer.Release(); }
                var result = await completion.Task.WaitAsync(token).ConfigureAwait(false);
                if (control)
                {
                    // Acknowledgement alone is not completion. Drain the request
                    // being canceled before allowing navigation to resume.
                    await Task.WhenAll(canceling.Select(ObserveCompletion)).WaitAsync(token).ConfigureAwait(false);
                }
                return result;
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested)
            {
                throw failure ?? new IOException("The engine connection was closed.");
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
            {
                Fail(new IOException(control
                    ? "The engine did not finish cancellation. Connect to recover; read back before retrying any write."
                    : "The engine stopped responding. Connect to recover; read back before retrying any write.", ex));
                throw failure!;
            }
            catch (TimeoutException ex)
            {
                Fail(new IOException("The engine stopped responding. The outcome of any in-flight write is unknown; reconnect and read the device before retrying.", ex));
                throw failure!;
            }
            finally { pending.TryRemove(id, out _); progress.TryRemove(id, out _); operationNames.TryRemove(id, out _); }
        }
        finally { if (!control) operations.Release(); }
    }

    private async Task WriteRequest(string encoded, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(encoded.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task ObserveCompletion(Task task)
    {
        // A canceled operation may fail normally. Its caller owns that error;
        // cancellation only needs to know that the operation has settled.
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task ReadResponses()
    {
        try
        {
            // Bound while reading, before allocating an arbitrarily long response line.
            var buffer = new char[8192];
            var line = new StringBuilder();
            while (await process.StandardOutput.ReadAsync(buffer.AsMemory(), stopped.Token).ConfigureAwait(false) is var count && count > 0)
            {
                var start = 0;
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] != '\n') continue;
                    Append(index - start);
                    await ApplyResponse(line.ToString()).ConfigureAwait(false);
                    line.Clear();
                    start = index + 1;
                }
                Append(count - start);
                void Append(int length)
                {
                    if (line.Length + length > 32 * 1024 * 1024) throw new IOException("Engine response exceeds 32 MiB.");
                    line.Append(buffer, start, length);
                }
            }
            Fail(new IOException("The BACnet engine exited. Reconnect and read back before retrying any write."));
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(new IOException("The engine returned an invalid response or its update could not be applied. Reconnect and read back before retrying any write.", ex));
        }
    }

    private async Task ApplyResponse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var envelope = document.RootElement;
        var id = envelope.GetProperty("id").GetInt64();
        if (!pending.TryGetValue(id, out var completion)) return;
        if (envelope.TryGetProperty("progress", out var update) && update.GetBoolean())
        {
            if (progress.TryGetValue(id, out var callback))
                await callback(envelope.GetProperty("result").Clone()).WaitAsync(stopped.Token).ConfigureAwait(false);
            return;
        }
        if (envelope.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } message)
        {
            if (envelope.TryGetProperty("failure", out var failure) && failure.GetProperty("message").GetString() is { Length: > 0 } summary)
                completion.TrySetException(new OperationFailure(operationNames.GetValueOrDefault(id, "BACnet operation"), summary, failure.GetProperty("action").GetString() ?? "", message));
            else completion.TrySetException(new IOException(message));
        }
        else completion.TrySetResult(envelope.GetProperty("result").Clone());
    }

    private async Task DrainErrors()
    {
        try
        {
            var buffer = new char[2048];
            while (await process.StandardError.ReadAsync(buffer.AsMemory(), stopped.Token).ConfigureAwait(false) > 0) { }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (IOException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (state)
        {
            if (disposal != null) return new ValueTask(disposal);
            disposed = true;
            failure ??= new IOException("The engine connection was closed. Any in-flight write may need readback.");
            foreach (var completion in pending.Values) completion.TrySetException(failure);
            disposal = DisposeCore();
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCore()
    {
        stopped.Cancel();
        // Serialize Close with writes so StreamWriter never closes during an async write.
        if (!await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
        {
            // A stopped input reader can leave an anonymous-pipe write blocked even
            // after cancellation. Stop only this owned child to release the writer.
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            await writer.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        finally { writer.Release(); }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        await Task.WhenAll(reader, errors).ConfigureAwait(false);
        process.Dispose();
    }
}
