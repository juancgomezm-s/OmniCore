namespace OmniCore.Client;

using OmniCore.Protocol;
using System.Diagnostics;
using System.Text.Json;

/// <summary>Process-backed CLI client for the bounded M9 JSON-lines stdio transport.</summary>
public sealed class StdioOmniClient : IOmniClient, IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _gate = new();
    private int _disposed;
    private int _exchangeActive;

    private StdioOmniClient(Process process, TimeSpan handshakeTimeout, CancellationToken cancellationToken)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        var hello = WireEnvelope.Hello();
        var payload = "{\"supportedVersions\":[\"1.0\"],\"client\":\"omnicore-cli\"}";
        var request = new WireEnvelopeBuilder(hello.MessageId, MessageTypes.Hello, payload).Build();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(handshakeTimeout);
        WireEnvelope response;
        try { response = Exchange(request, handshake.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The stdio Host did not complete protocol negotiation within {handshakeTimeout}.");
        }
        if (response.MessageType != MessageTypes.Response)
            throw new InvalidDataException("Host did not answer protocol negotiation.");
        using var document = JsonDocument.Parse(response.PayloadJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("protocolVersion", out var negotiated)
            || negotiated.GetString() != ProtocolVersions.V1
            || !root.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Host returned an incompatible negotiation response.");
        Capabilities = capabilities.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Capabilities acknowledged by this child process during hello.</summary>
    public IReadOnlySet<string> Capabilities { get; }

    public static StdioOmniClient Start(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan? handshakeTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout), "The stdio handshake timeout must be finite and positive.");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the OmniCore stdio Host process.");
        }
        try { return new StdioOmniClient(process, timeout, cancellationToken); }
        catch
        {
            StopOwnedProcess(process);
            throw;
        }
    }

    public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.MessageType != MessageTypes.Command)
            throw new ArgumentException("A command envelope is required.", nameof(command));
        var payload = "{\"operation\":\"command\",\"command\":" + command.ToJson() + "}";
        var response = Exchange(WireEnvelope.Command(Ids.NewV7(), payload), cancellationToken);
        using var document = JsonDocument.Parse(response.PayloadJson);
        var ack = document.RootElement.GetProperty("ack");
        RuntimeCommandOutcome? outcome = null;
        if (ack.TryGetProperty("outcome", out var outcomeElement)
            && outcomeElement.ValueKind == JsonValueKind.Object)
        {
            var kind = outcomeElement.GetProperty("kind").GetString();
            var reason = outcomeElement.TryGetProperty("reason", out var reasonElement)
                && reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : null;
            outcome = kind switch
            {
                "Accepted" => RuntimeCommandOutcome.Accepted(),
                "Rejected" => RuntimeCommandOutcome.Rejected(),
                "NoOp" => RuntimeCommandOutcome.NoOp(),
                "Deferred" when reason is not null => RuntimeCommandOutcome.Deferred(reason),
                _ => null,
            };
        }
        long? first = ack.TryGetProperty("firstSeq", out var firstElement) && firstElement.ValueKind == JsonValueKind.Number
            ? firstElement.GetInt64() : null;
        long? last = ack.TryGetProperty("lastSeq", out var lastElement) && lastElement.ValueKind == JsonValueKind.Number
            ? lastElement.GetInt64() : null;
        return new CommandAck(ack.GetProperty("commandId").GetString()!,
            ack.GetProperty("status").GetString()!,
            ack.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() : null, outcome, first, last);
    }

    public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence)
    {
        if (fromSequence < 0) throw new ArgumentOutOfRangeException(nameof(fromSequence));
        var payload = "{\"operation\":\"subscribe\",\"fromSequence\":"
            + fromSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        var response = Exchange(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        using var document = JsonDocument.Parse(response.PayloadJson);
        return document.RootElement.GetProperty("events").EnumerateArray()
            .Select(item => WireEnvelope.FromJson(item.GetRawText())).ToArray();
    }

    public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var payload = "{\"operation\":\"query\"," + JsonObj.Field("name", name) + "}";
        var response = Exchange(WireEnvelope.Command(Ids.NewV7(), payload), cancellationToken);
        using var document = JsonDocument.Parse(response.PayloadJson);
        var result = document.RootElement;
        if (!result.TryGetProperty("json", out var json) || json.ValueKind == JsonValueKind.Null) return null;
        return new SessionQueryResult(result.GetProperty("name").GetString()!, json.GetString()!);
    }

    private WireEnvelope Exchange(WireEnvelope request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _exchangeActive);
            try
            {
            using var registration = cancellationToken.Register(static state =>
            {
                try { ((Process)state!).Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }, _process);
            try
            {
                WireFrame.Write(_input, request.ToJson());
                var json = WireFrame.Read(_output)
                    ?? throw new EndOfStreamException("The stdio Host closed without a response.");
                cancellationToken.ThrowIfCancellationRequested();
                var response = WireEnvelope.FromJson(json);
                if (response.Version != ProtocolVersions.V1 || response.MessageId != request.MessageId)
                    throw new InvalidDataException("The stdio Host returned a mismatched protocol response.");
                if (response.MessageType == MessageTypes.Error)
                    throw new InvalidDataException("The stdio Host rejected the request: " + response.PayloadJson);
                if (response.MessageType != MessageTypes.Response)
                    throw new InvalidDataException("The stdio Host returned an unexpected message type.");
                return response;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            }
            finally { Interlocked.Decrement(ref _exchangeActive); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Volatile.Read(ref _exchangeActive) != 0)
            StopOwnedProcess(_process);
        else
        {
            try { _input.Dispose(); } catch (IOException) { }
            if (!_process.WaitForExit(1500)) StopOwnedProcess(_process);
            else _process.Dispose();
        }
    }

    private static void StopOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { process.WaitForExit(1500); } catch (InvalidOperationException) { }
        process.Dispose();
    }

    private sealed class WireEnvelopeBuilder(string messageId, string messageType, string payload)
    {
        public WireEnvelope Build() => WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":"
            + "\"" + JsonObj.Escape(messageType) + "\",\"messageId\":\""
            + JsonObj.Escape(messageId) + "\""
            + ",\"payload\":" + payload + "}");
    }
}
