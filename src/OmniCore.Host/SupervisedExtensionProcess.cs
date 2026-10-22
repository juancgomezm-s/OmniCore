using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmniCore.Protocol;

namespace OmniCore.Host;

/// <summary>One trusted executable, bound to one identity and session. Not a sandbox.</summary>
internal sealed class SupervisedExtensionProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _outgoing = new(4, 4);
    private readonly SemaphoreSlim _incoming = new(4, 4);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonElement>> _services;
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private readonly Task _reader;
    private readonly Task _stderr;
    private readonly Task _revocation;
    private long _id;
    private int _stopped;
    public string State { get; private set; } = "activating";
    public IReadOnlyList<string> Diagnostics => _diagnostics.ToArray();

    internal static async Task VerifyNodeLtsAsync(string executable, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.ArgumentList.Add("-p"); start.ArgumentList.Add("JSON.stringify({major:Number(process.versions.node.split('.')[0]),lts:!!process.release.lts})");
        using var process = Process.Start(start) ?? throw new IOException("extension_node_missing");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var output = await ReadFrameAsync(process.StandardOutput.BaseStream, deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(output ?? "null");
            if (process.ExitCode != 0 || parsed.RootElement.GetProperty("major").GetInt32() < 22
                || !parsed.RootElement.GetProperty("lts").GetBoolean()) throw new IOException("extension_node_lts_required");
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    internal SupervisedExtensionProcess(string executable, IReadOnlyList<string> args, string directory,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> services, Func<bool>? stillGranted = null)
    {
        _services = services;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR", "LANG" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var argument in args) start.ArgumentList.Add(argument);
        try { _process = Process.Start(start) ?? throw new IOException("extension_process_start_failed"); }
        catch (System.ComponentModel.Win32Exception error) { throw new IOException("extension_process_start_failed", error); }
        _reader = Task.Run(ReadAsync); _stderr = Task.Run(ReadErrorsAsync);
        _revocation = stillGranted is null ? Task.CompletedTask : WatchGrantAsync(stillGranted);
    }

    private async Task WatchGrantAsync(Func<bool> stillGranted)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(500, _lifetime.Token).ConfigureAwait(false);
                if (!stillGranted()) { Stop("extension_authority_revoked"); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Stop("extension_authority_unavailable"); }
    }

    public async Task InitializeAsync(ExtensionInitialize initialize, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToElement(initialize, ExtensionJson.Default.ExtensionInitialize);
        var result = await InvokeAsync("initialize", data, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != ExtensionProtocol.Version
            || !result.TryGetProperty("extensionId", out var identity) || identity.ValueKind != JsonValueKind.String || identity.GetString() != initialize.ExtensionId
            || !result.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array
            || capabilities.GetArrayLength() > 64
            || capabilities.EnumerateArray().Any(cap => cap.ValueKind != JsonValueKind.String || !initialize.Capabilities.Contains(cap.GetString()!, StringComparer.Ordinal))
            || !capabilities.EnumerateArray().Select(cap => cap.GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(initialize.Capabilities))
        { Stop("extension_handshake_incompatible"); throw new InvalidDataException("extension_handshake_incompatible"); }
        State = "active";
    }

    public async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, TimeSpan timeout, CancellationToken token)
    {
        if (Volatile.Read(ref _stopped) != 0) throw new IOException("extension_process_stopped");
        if (!_outgoing.Wait(0)) throw new IOException("extension_concurrency_limit");
        var id = Interlocked.Increment(ref _id).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(timeout);
        try
        {
            await WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":" + Quote(id) + ",\"method\":" + Quote(method)
                + ",\"params\":" + parameters.GetRawText() + "}", deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A killed/timed-out invocation has unknown external outcome. Never retry it automatically.
            using var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try { await WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":" + Quote(id) + "}}", grace.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            Stop("extension_cancelled_outcome_unknown"); throw;
        }
        catch (IOException) { Stop("extension_transport_failed_outcome_unknown"); throw; }
        finally { _pending.TryRemove(id, out _); _outgoing.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            using var stream = new BufferedStream(_process.StandardOutput.BaseStream, 4096);
            while (await ReadFrameAsync(stream, _lifetime.Token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line); var root = document.RootElement;
                if (root.GetProperty("jsonrpc").GetString() != "2.0") throw new InvalidDataException("extension_rpc_invalid");
                if (root.TryGetProperty("method", out var method))
                {
                    if (!root.TryGetProperty("id", out var incomingId)) throw new InvalidDataException("extension_notification_not_supported");
                    if (!_incoming.Wait(0)) throw new InvalidDataException("extension_request_queue_limit");
                    var boundId = incomingId.Clone(); var name = method.GetString()!;
                    var parameters = root.TryGetProperty("params", out var supplied) ? supplied.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                    _ = ServeAsync(boundId, name, parameters);
                }
                else
                {
                    var id = root.GetProperty("id").GetString()!;
                    if (!_pending.TryGetValue(id, out var completion)) throw new InvalidDataException("extension_rpc_unknown_id");
                    if (root.TryGetProperty("error", out _)) completion.TrySetException(new IOException("extension_remote_error"));
                    else completion.TrySetResult(root.GetProperty("result").Clone());
                }
            }
            Stop("extension_process_exited");
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or OperationCanceledException or DecoderFallbackException or KeyNotFoundException)
        { Stop("extension_protocol_failed:" + error.GetType().Name); }
    }
    private async Task ServeAsync(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            string body;
            try
            {
                if (State != "active") throw new UnauthorizedAccessException("extension_handshake_required");
                var result = await _services(method, parameters, deadline.Token).ConfigureAwait(false);
                body = "\"result\":" + result.GetRawText();
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OperationCanceledException or JsonException or KeyNotFoundException)
            { body = "\"error\":{\"code\":-32001,\"message\":\"extension_service_denied_or_failed\"}"; }
            await WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + "," + body + "}", _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { _incoming.Release(); }
    }
    private async Task ReadErrorsAsync()
    {
        try
        {
            using var stream = new BufferedStream(_process.StandardError.BaseStream, 4096);
            while (await ReadFrameAsync(stream, _lifetime.Token).ConfigureAwait(false) is { } line)
            {
                // Only structured severity/code, never arbitrary secret-bearing extension output.
                using var document = JsonDocument.Parse(line); var root = document.RootElement;
                var level = root.GetProperty("level").GetString(); var code = root.GetProperty("code").GetString();
                if (level is not ("info" or "warning" or "error") || code is null || code.Length > 128
                    || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))) continue;
                _diagnostics.Enqueue(level + ":" + code); while (_diagnostics.Count > 64) _diagnostics.TryDequeue(out _);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException) { }
    }
    private async Task WriteAsync(string json, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(json) > ExtensionProtocol.MaximumFrameBytes) throw new IOException("extension_frame_limit");
        await _writer.WaitAsync(token).ConfigureAwait(false);
        try { await _process.StandardInput.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false); await _process.StandardInput.FlushAsync(token).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }
    internal static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        using var frame = new MemoryStream(); var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) > 0)
        {
            if (buffer[0] == 10) return new UTF8Encoding(false, true).GetString(frame.ToArray()).TrimEnd('\r');
            if (frame.Length >= ExtensionProtocol.MaximumFrameBytes) throw new IOException("extension_frame_limit");
            frame.WriteByte(buffer[0]);
        }
        if (frame.Length > 0) throw new IOException("extension_partial_frame"); return null;
    }
    private void Stop(string diagnostic)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        State = "failed"; _diagnostics.Enqueue(diagnostic); _lifetime.Cancel();
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException(diagnostic));
        try { if (!_process.HasExited) _process.Kill(true); } catch (InvalidOperationException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _stopped) == 0)
        {
            try { using var value = JsonDocument.Parse("{}"); await InvokeAsync("shutdown", value.RootElement, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
        Stop("extension_disabled"); await Task.WhenAll(_reader, _stderr, _revocation).ConfigureAwait(false);
        State = "inactive"; _process.Dispose();
    }
    private static string Quote(string text) => "\"" + JsonEncodedText.Encode(text) + "\"";
}
