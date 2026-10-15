namespace OmniCore.Client;

using OmniCore.Protocol;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

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
        => SubscribeSince(sessionId: null, fromSequence);

    public IReadOnlyList<WireEnvelope> SubscribeSince(string? sessionId, long fromSequence)
    {
        if (!Capabilities.Contains(StdioProtocol.ReplayPageOperation))
        {
            if (!Capabilities.Contains("events.replay"))
                throw new NotSupportedException("The stdio Host did not negotiate replay support.");
            if (sessionId is not null)
                throw new NotSupportedException("This stdio Host supports legacy replay only for its current Session.");
            var legacyPayload = "{\"operation\":\"subscribe\",\"fromSequence\":"
                + fromSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            var legacyResponse = Exchange(WireEnvelope.Command(Ids.NewV7(), legacyPayload), CancellationToken.None);
            using var legacyDocument = JsonDocument.Parse(legacyResponse.PayloadJson);
            return legacyDocument.RootElement.GetProperty("events").EnumerateArray()
                .Select(item => WireEnvelope.FromJson(item.GetRawText())).ToArray();
        }
        var result = new List<WireEnvelope>();
        var cursor = fromSequence;
        long? highWater = null;
        while (true)
        {
            var page = ReadReplayPage(cursor, sessionId, highWater: highWater,
                cancellationToken: CancellationToken.None);
            if (page.HasGap) throw new InvalidDataException("The requested replay cursor has a retained-history gap.");
            result.AddRange(page.Events);
            if (page.Complete) return result;
            if (page.NextSequence <= cursor) throw new InvalidDataException("Replay cursor did not advance.");
            cursor = page.NextSequence;
            sessionId = page.SessionId;
            highWater = page.HighWater;
        }
    }

    public StdioReplayPage ReadReplayPage(long fromSequence, string? sessionId = null, int pageSize = 128,
        long? highWater = null, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Contains(StdioProtocol.ReplayPageOperation))
            throw new NotSupportedException("The stdio Host did not negotiate paged replay support.");
        if (fromSequence < 0) throw new ArgumentOutOfRangeException(nameof(fromSequence));
        if (pageSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var payload = "{\"operation\":" + QuoteJson(StdioProtocol.ReplayPageOperation) + ",\"fromSequence\":"
            + fromSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"pageSize\":" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (sessionId is null ? "" : ",\"sessionId\":" + QuoteJson(sessionId))
            + (highWater is null ? "" : ",\"highWater\":" + highWater.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "}";
        var response = Exchange(WireEnvelope.Command(Ids.NewV7(), payload), cancellationToken);
        using var document = JsonDocument.Parse(response.PayloadJson);
        var root = document.RootElement;
        var events = root.GetProperty("events").EnumerateArray()
            .Select(item => WireEnvelope.FromJson(item.GetRawText())).ToArray();
        return new StdioReplayPage(root.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String
                ? session.GetString() : null, root.GetProperty("requestedSequence").GetInt64(),
            root.GetProperty("nextSequence").GetInt64(), root.GetProperty("highWater").GetInt64(),
            root.GetProperty("hasGap").GetBoolean(), root.GetProperty("complete").GetBoolean(), events);
    }

    /// <summary>Looks up a prior command receipt after reconnect; this method never resends the command.</summary>
    public StdioCommandResult LookupCommandResult(WireEnvelope command, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Contains("command.result.lookup"))
            throw new NotSupportedException("The stdio Host did not negotiate command-result lookup.");
        ArgumentNullException.ThrowIfNull(command);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
        var payload = "{\"operation\":\"command.result\",\"commandId\":"
            + QuoteJson(command.MessageId) + ",\"requestHash\":" + QuoteJson(hash) + "}";
        var response = Exchange(WireEnvelope.Command(Ids.NewV7(), payload), cancellationToken);
        using var document = JsonDocument.Parse(response.PayloadJson);
        var root = document.RootElement;
        var state = root.GetProperty("state").GetString();
        if (state != "completed")
            throw new InvalidDataException("Command result is not recoverable: " + state + ". No command was resent.");
        var ack = root.GetProperty("ack");
        var outcomeKind = ack.GetProperty("outcome").GetString();
        var reason = ack.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
            ? reasonElement.GetString() : null;
        var outcome = outcomeKind switch
        {
            "Accepted" => RuntimeCommandOutcome.Accepted(),
            "Rejected" => RuntimeCommandOutcome.Rejected(),
            "NoOp" => RuntimeCommandOutcome.NoOp(),
            "Deferred" => RuntimeCommandOutcome.Deferred(reason ?? "receipt_result_unknown"),
            _ => RuntimeCommandOutcome.Deferred("receipt_result_unknown"),
        };
        var first = ack.GetProperty("firstSeq").ValueKind == JsonValueKind.Number ? ack.GetProperty("firstSeq").GetInt64() : (long?)null;
        var last = ack.GetProperty("lastSeq").ValueKind == JsonValueKind.Number ? ack.GetProperty("lastSeq").GetInt64() : (long?)null;
        return new StdioCommandResult(new CommandAck(ack.GetProperty("commandId").GetString()!,
            ack.GetProperty("status").GetString()!, null, outcome, first, last),
            root.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String
                ? session.GetString() : null);
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

    private static string QuoteJson(string value) => "\"" + JsonObj.Escape(value) + "\"";

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

public sealed record StdioReplayPage(string? SessionId, long RequestedSequence, long NextSequence, long HighWater,
    bool HasGap, bool Complete, IReadOnlyList<WireEnvelope> Events);

public sealed record StdioCommandResult(CommandAck Ack, string? SessionId);
