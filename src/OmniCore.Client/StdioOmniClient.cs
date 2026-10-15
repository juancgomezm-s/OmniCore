namespace OmniCore.Client;

using OmniCore.Protocol;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Collections.Generic;

/// <summary>Process-backed CLI client for the bounded M9 JSON-lines stdio transport.</summary>
public sealed class StdioOmniClient : IOmniClient, IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WireEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly Channel<StdioStreamEvent> _streamEvents = Channel.CreateBounded<StdioStreamEvent>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true });
    private readonly Task _readerTask;
    private string? _activeStreamId;
    private string? _activeStreamSession;
    private Exception? _readerFailure;
    private int _disposed;
    private int _exchangeActive;
    private int _streamActive;

    private StdioOmniClient(Process process, TimeSpan handshakeTimeout, CancellationToken cancellationToken)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        _readerTask = Task.Run(ReadLoop);
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
        catch (Exception error) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The stdio Host protocol negotiation was cancelled.", error, cancellationToken);
        }
        catch (Exception error) when (handshake.IsCancellationRequested)
        {
            throw new TimeoutException($"The stdio Host did not complete protocol negotiation within {handshakeTimeout}.", error);
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

    /// <summary>Consumes durable events in-order. ACK is written only after the caller returns from applying the event.</summary>
    public async Task ConsumeEventStreamAsync(string sessionId, long fromSequence, int window,
        Func<StdioStreamEvent, CancellationToken, ValueTask> consume, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(consume);
        if (!Capabilities.Contains("events.stream.v1")) throw new NotSupportedException("The stdio Host did not negotiate event streaming.");
        if (fromSequence < 1 || window is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(fromSequence));
        if (Interlocked.CompareExchange(ref _streamActive, 1, 0) != 0)
            throw new InvalidOperationException("An event stream is already active.");
        string? streamId = null;
        try
        {
            var start = Exchange(WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"events.stream\",\"sessionId\":"
                + QuoteJson(sessionId) + ",\"fromSequence\":" + fromSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"window\":" + window.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"), cancellationToken);
            using var startDocument = JsonDocument.Parse(start.PayloadJson);
            var startPayload = startDocument.RootElement;
            streamId = startPayload.GetProperty("streamId").GetString();
            if (string.IsNullOrWhiteSpace(streamId)) throw new InvalidDataException("Host returned an empty event stream ID.");
            _activeStreamId = streamId;
            _activeStreamSession = sessionId;
            if (startPayload.GetProperty("sessionId").GetString() != sessionId
                || startPayload.GetProperty("cursor").GetInt64() != fromSequence
                || startPayload.GetProperty("window").GetInt32() != window)
                throw new InvalidDataException("Host returned a mismatched event stream subscription.");
            _ = Exchange(WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"events.ready\",\"streamId\":"
                + QuoteJson(streamId) + "}"), cancellationToken);
            while (await _streamEvents.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_streamEvents.Reader.TryRead(out var item))
                {
                    if (item.StreamId != streamId || item.SessionId != sessionId) throw new InvalidDataException("Event stream scope changed.");
                    await consume(item, cancellationToken).ConfigureAwait(false);
                    if (item.IsGap) return;
                    var ack = Exchange(WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"events.ack\",\"streamId\":"
                        + QuoteJson(streamId) + ",\"throughSequence\":"
                        + item.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"), cancellationToken);
                    using var ackDocument = JsonDocument.Parse(ack.PayloadJson);
                    if (!ackDocument.RootElement.GetProperty("accepted").GetBoolean())
                        throw new InvalidDataException("Host rejected an event stream ACK.");
                }
            }
        }
        finally
        {
            if (streamId is not null)
            {
                try
                {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    _ = Exchange(WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"events.stop\",\"streamId\":"
                        + QuoteJson(streamId) + "}"), stopTimeout.Token);
                }
                catch (Exception)
                {
                    if (Volatile.Read(ref _disposed) == 0 && !_process.HasExited) Dispose();
                }
            }
            else if (Volatile.Read(ref _disposed) == 0 && !_process.HasExited) Dispose();
            _activeStreamId = null;
            _activeStreamSession = null;
            while (_streamEvents.Reader.TryRead(out _)) { }
            Interlocked.Exchange(ref _streamActive, 0);
        }
    }

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
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var diagnosticGate = new object();
        var diagnostics = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (diagnosticGate)
            {
                diagnostics.AppendLine(args.Data);
                if (diagnostics.Length > 4096) diagnostics.Remove(0, diagnostics.Length - 4096);
            }
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the OmniCore stdio Host process.");
        }
        process.BeginErrorReadLine();
        try { return new StdioOmniClient(process, timeout, cancellationToken); }
        catch (Exception error)
        {
            string diagnosticText;
            lock (diagnosticGate) diagnosticText = diagnostics.ToString().Trim();
            StopOwnedProcess(process);
            if (error is TimeoutException or OperationCanceledException)
            {
                if (diagnosticText.Length > 0) error.Data["HostStderr"] = diagnosticText;
                throw;
            }
            throw new IOException(diagnosticText.Length == 0 ? error.Message : error.Message + " Host stderr: " + diagnosticText, error);
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

    private void ReadLoop()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                var json = WireFrame.Read(_output);
                if (json is null) throw new EndOfStreamException("The stdio Host closed the protocol stream.");
                var message = WireEnvelope.FromJson(json);
                if (message.Version != ProtocolVersions.V1) throw new InvalidDataException("The stdio Host changed protocol version.");
                if (message.MessageType == MessageTypes.Event)
                {
                    using var document = JsonDocument.Parse(message.PayloadJson);
                    var payload = document.RootElement;
                    var streamId = payload.GetProperty("streamId").GetString()!;
                    var kind = payload.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
                    if (kind == "gap")
                    {
                        if (streamId != _activeStreamId) throw new InvalidDataException("The stdio Host reported a gap outside the active stream.");
                        var requested = payload.GetProperty("requestedSequence").GetInt64();
                        var next = payload.GetProperty("nextSequence").GetInt64();
                        var highWater = payload.GetProperty("highWater").GetInt64();
                        if (requested < 1 || next < requested || highWater < 0)
                            throw new InvalidDataException("The stdio Host returned an invalid stream gap.");
                        if (!_streamEvents.Writer.TryWrite(new StdioStreamEvent(streamId, _activeStreamSession, 0,
                            null, IsGap: true, RequestedSequence: requested, NextSequence: next, HighWater: highWater)))
                            throw new InvalidDataException("The event consumer exceeded its negotiated bounded window.");
                        continue;
                    }
                    if (kind != "event") throw new InvalidDataException("The stdio Host reported an unsupported stream control frame.");
                    var sequence = payload.GetProperty("sequence").GetInt64();
                    var envelope = WireEnvelope.FromJson(payload.GetProperty("event").GetRawText());
                    var session = envelope.PayloadJson.Length == 0 ? null
                        : JsonDocument.Parse(envelope.PayloadJson);
                    using (session)
                    {
                        var sessionId = session?.RootElement.TryGetProperty("sessionId", out var id) == true
                            ? id.GetString() : null;
                        if (streamId != _activeStreamId || sessionId != _activeStreamSession
                            || envelope.MessageType != MessageTypes.Event || sequence < 1
                            || !session!.RootElement.TryGetProperty("seq", out var rawSequence)
                            || rawSequence.GetString() != sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            throw new InvalidDataException("The stdio Host returned an event outside the active stream scope.");
                        if (!_streamEvents.Writer.TryWrite(new StdioStreamEvent(streamId, sessionId, sequence, envelope)))
                            throw new InvalidDataException("The event consumer exceeded its negotiated bounded window.");
                    }
                    continue;
                }
                if (message.MessageType != MessageTypes.Response && message.MessageType != MessageTypes.Error)
                    throw new InvalidDataException("The stdio Host returned an unexpected message type.");
                if (_pending.TryRemove(message.MessageId, out var pending)) pending.TrySetResult(message);
            }
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _readerFailure, ex, null);
            foreach (var item in _pending.ToArray())
                if (_pending.TryRemove(item.Key, out var pending)) pending.TrySetException(ex);
            _streamEvents.Writer.TryComplete(ex);
        }
    }

    private WireEnvelope Exchange(WireEnvelope request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _readerFailure) is { } readerFailure)
            throw new IOException("The stdio Host protocol reader has stopped.", readerFailure);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _exchangeActive);
        var pending = new TaskCompletionSource<WireEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.MessageId, pending))
        {
            Interlocked.Decrement(ref _exchangeActive);
            throw new InvalidOperationException("Duplicate stdio request ID.");
        }
        try
        {
            if (Volatile.Read(ref _readerFailure) is { } observedFailure)
                throw new IOException("The stdio Host protocol reader has stopped.", observedFailure);
            lock (_writeGate) WireFrame.Write(_input, request.ToJson());
            var response = pending.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            if (response.Version != ProtocolVersions.V1 || response.MessageId != request.MessageId)
                throw new InvalidDataException("The stdio Host returned a mismatched protocol response.");
            if (response.MessageType == MessageTypes.Error)
                throw new InvalidDataException("The stdio Host rejected the request: " + response.PayloadJson);
            return response;
        }
        finally
        {
            _pending.TryRemove(request.MessageId, out _);
            Interlocked.Decrement(ref _exchangeActive);
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

public sealed record StdioStreamEvent(string StreamId, string? SessionId, long Sequence, WireEnvelope? Event,
    bool IsGap = false, long? RequestedSequence = null, long? NextSequence = null, long? HighWater = null);

public sealed record StdioCommandResult(CommandAck Ack, string? SessionId);
