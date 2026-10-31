namespace OmniCore.Host;

using System.Text;
using System.Text.Json;
using OmniCore.Sandbox;

/// <summary>One turn-owned Strong duplex process. Failed calls are never automatically replayed.</summary>
internal sealed class McpStdioConnection : IDisposable
{
    private readonly McpServerDefinition _server;
    private readonly ISandboxProcessLauncher _launcher;
    private readonly McpProtocolConnection _protocol;
    private ISandboxDuplexProcessControl? _process;
    private StreamReader? _reader;
    private Task? _stderr;
    private readonly CancellationTokenSource _lifetime = new();
    private long _id;
    private bool _broken;
    internal McpStdioConnection(McpServerDefinition server, ISandboxProcessLauncher launcher)
    { _server = server; _launcher = launcher; _protocol = new(RawAsync); }
    internal Task<string> SendAsync(string method, string args, CancellationToken token) => _protocol.SendAsync(method, args, token);
    private async Task Start(CancellationToken token)
    {
        if (_broken) throw new IOException("MCP process connection ended; in-flight calls cannot be replayed.");
        if (_process is not null) return;
        if (_server.Transport != "stdio" || _server.Command is null) throw new InvalidOperationException("MCP transport mismatch.");
        var executable = Path.GetFullPath(_server.Command);
        var cwd = Path.GetFullPath(_server.WorkingDirectory ?? Path.GetDirectoryName(executable)!);
        var spec = new SandboxLaunchSpec(SandboxStrength.Strong, executable, _server.Args, cwd,
            new Dictionary<string,string>(StringComparer.Ordinal), [], new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []),
            Timeout: null, InitialStandardInput: null) { Duplex = true,
                IsolationKey = "mcp-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(_server.ConnectionKey))) };
        var process = await _launcher.StartAsync(spec, token).ConfigureAwait(false);
        if (process is not ISandboxDuplexProcessControl duplex)
        {
            await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            throw new NotSupportedException("MCP stdio requires a Strong duplex process backend.");
        }
        _process = duplex;
        _reader = new StreamReader(duplex.StandardOutput, new UTF8Encoding(false,true), false, 4096, leaveOpen:true);
        // Drain diagnostic bytes independently, without retaining arbitrary server output.
        _stderr = Task.Run(async () =>
        {
            var bytes = new byte[4096];
            try { while (await duplex.StandardError.ReadAsync(bytes, _lifetime.Token).ConfigureAwait(false) > 0) { } }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
    }
    private async Task<string> RawAsync(string method, string args, string version, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(method is "server/discover" or "initialize" ? _server.StartupTimeoutSeconds : _server.RequestTimeoutSeconds));
        var id = Interlocked.Increment(ref _id);
        try
        {
            await Start(deadline.Token).ConfigureAwait(false);
            var request = McpWire.Request(id, method, args, version);
            var notification = method.StartsWith("notifications/", StringComparison.Ordinal);
            if (notification)
            {
                using var document = JsonDocument.Parse(request);
                request = "{\"jsonrpc\":\"2.0\",\"method\":" + McpWire.Quote(method)
                    + ",\"params\":" + document.RootElement.GetProperty("params").GetRawText() + "}";
            }
            if (Encoding.UTF8.GetByteCount(request) > McpWire.MaximumPayloadBytes) throw new InvalidDataException("MCP request exceeds its limit.");
            await Write(request, deadline.Token).ConfigureAwait(false);
            if (notification) return "{}";
            var notifications = 0;
            while (true)
            {
                var line = await ReadLine(_reader!, deadline.Token).ConfigureAwait(false)
                    ?? throw new IOException("MCP server closed stdout before returning a result.");
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId))
                {
                    if (++notifications > 1024 || !root.TryGetProperty("method", out var note)
                        || note.ValueKind != JsonValueKind.String || !note.GetString()!.StartsWith("notifications/",StringComparison.Ordinal))
                        throw new InvalidDataException("Unexpected MCP stdio message.");
                    continue;
                }
                if (!responseId.TryGetInt64(out var actual) || actual != id) throw new InvalidDataException("MCP stdio response id mismatch.");
                _ = McpWire.Result(line,id);
                // Public transport contract uses id=1. Correlation on the wire remains unique.
                return "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + root.GetProperty("result").GetRawText() + "}";
            }
        }
        catch (McpRpcException) { throw; }
        catch (OperationCanceledException)
        {
            // Cancellation does not imply rollback. Never reuse or replay this channel.
            if (_process is not null)
            {
                try { using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    await Write("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":"+id+"}}",cancel.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            _broken = token.IsCancellationRequested || method != "server/discover";
            if (_process is not null)
            {
                await _process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                await _process.DisposeAsync().ConfigureAwait(false);
                _process = null; _reader?.Dispose(); _reader = null;
            }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("MCP stdio deadline exceeded; execution may be uncertain.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            _broken = true;
            if (_process is not null) await _process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
    private async Task Write(string value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(value+"\n");
        await _process!.StandardInput.WriteAsync(bytes,token).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task<string?> ReadLine(TextReader reader, CancellationToken token)
    {
        var value = new StringBuilder();var one = new char[1];var bytes = 0;
        while (await reader.ReadAsync(one.AsMemory(),token).AsTask().WaitAsync(token).ConfigureAwait(false) > 0)
        {
            if (one[0] == '\n') return value.ToString().TrimEnd('\r');
            value.Append(one[0]);
            bytes += one[0] < 128 ? 1 : one[0] < 2048 ? 2 : 3;
            if (bytes > McpWire.MaximumPayloadBytes) throw new InvalidDataException("MCP stdio line exceeds its limit.");
        }
        return value.Length == 0 ? null : throw new InvalidDataException("MCP stdout ended inside a frame.");
    }
    public void Dispose()
    {
        _lifetime.Cancel();
        if (_process is null) return;
        try
        {
            if (_process is ISandboxPersistentProcessControl persistent)
            {
                persistent.CloseInputAsync().AsTask().GetAwaiter().GetResult();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { persistent.WaitForExitCodeAsync(deadline.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { _process.TerminateAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
            }
            else _process.TerminateAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        finally { _process.DisposeAsync().AsTask().GetAwaiter().GetResult(); _process = null; _reader?.Dispose(); }
    }
}
