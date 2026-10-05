using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class TelemetryBoundaryTests
{
    [Fact]
    public void Record_is_UTC_numeric_ID_only_and_local_sink_is_bounded()
    {
        var record = new TelemetryRecord(TelemetryKind.Delta, TelemetrySignal.TextDeltaCharacters, 12,
            DateTimeOffset.UtcNow, RunId.New(), TaskId.New(), LaneId.New(), TurnId.New(), ToolCallId.New());
        Assert.Equal(TimeSpan.Zero, record.TimestampUtc.Offset);
        Assert.All(typeof(TelemetryRecord).GetProperties(), property =>
            Assert.NotEqual(typeof(string), property.PropertyType));
        Assert.False(typeof(DomainEventPayload).IsAssignableFrom(typeof(TelemetryRecord)));
        Assert.Throws<ArgumentException>(() => new TelemetryRecord(TelemetryKind.Sample,
            TelemetrySignal.InputTokenCount, 1, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryRecord(TelemetryKind.Sample,
            TelemetrySignal.InputTokenCount, -1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryRecord((TelemetryKind)99,
            TelemetrySignal.InputTokenCount, 1, DateTimeOffset.UtcNow));

        var local = new InMemoryTelemetrySink(capacity: 2);
        local.Record(record);
        local.Record(record);
        local.Record(record);
        Assert.Equal(2, local.Snapshot().Count);
        NoOpTelemetrySink.Instance.Record(record);
    }

    [Fact]
    public async Task Provider_wrapper_forwards_the_same_events_and_records_only_numeric_dimensions()
    {
        var response = Final("canonical answer");
        var items = new ModelStreamEvent[]
        {
            new ResponseStarted(4),
            new BlockStarted(8, "private kind label"),
            new TextDelta(8, "private text delta"),
            new ReasoningDelta(9, "private reasoning delta"),
            new ToolArgumentsDelta(10, "{\"private\":\"arguments\"}"),
            new BlockCompleted(8, new TextBlock("private completed block")),
            new UsageUpdated(new TokenUsage(2, 3, 4, 5, 6)),
            new ResponseCompleted(response),
        };
        var provider = new ScriptedProvider(items);
        var sink = new InMemoryTelemetrySink();
        var observed = new TelemetryObservingModelProvider(provider, sink);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var turn = TurnId.New();
        var request = Request();
        var forwarded = new List<ModelStreamEvent>();
        using (ExecutionScope.Begin(new ExecutionScopeState(run, task, lane, turn)))
        {
            await foreach (var item in observed.StreamAsync(request, TestContext.Current.CancellationToken))
                forwarded.Add(item);
        }

        Assert.Equal(items.Length, forwarded.Count);
        for (var i = 0; i < items.Length; i++) Assert.Same(items[i], forwarded[i]);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(TestContext.Current.CancellationToken, provider.SeenCancellationToken);

        var records = sink.Snapshot();
        Assert.Contains(records, r => r.Signal == TelemetrySignal.TextDeltaCharacters && r.Value == "private text delta".Length);
        Assert.Contains(records, r => r.Signal == TelemetrySignal.ReasoningDeltaCharacters && r.Value == "private reasoning delta".Length);
        Assert.Contains(records, r => r.Signal == TelemetrySignal.ToolArgumentDeltaCharacters
            && r.Value == "{\"private\":\"arguments\"}".Length);
        Assert.Contains(records, r => r.Signal == TelemetrySignal.InputTokenCount && r.Value == 2);
        Assert.Contains(records, r => r.Signal == TelemetrySignal.ReasoningTokenCount && r.Value == 6);
        Assert.All(records, record =>
        {
            Assert.Equal(TimeSpan.Zero, record.TimestampUtc.Offset);
            Assert.Equal(run, record.RunId);
            Assert.Equal(task, record.TaskId);
            Assert.Equal(lane, record.LaneId);
            Assert.Equal(turn, record.TurnId);
        });
        var serializedRecords = System.Text.Json.JsonSerializer.Serialize(records);
        Assert.DoesNotContain("private", serializedRecords, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical answer", serializedRecords, StringComparison.Ordinal);
    }

    [Fact]
    public void Sink_failure_does_not_change_provider_complete_result_or_call_provider_twice()
    {
        var response = Final("same canonical answer");
        var provider = new ScriptedProvider(new ModelStreamEvent[]
        {
            new TextDelta(0, "discarded telemetry only"),
            new ResponseCompleted(response),
        });
        var wrapped = new TelemetryObservingModelProvider(provider, new ThrowingSink());

        var actual = wrapped.Complete(Request(), TestContext.Current.CancellationToken);

        Assert.Same(response, actual);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Consumer_cancellation_midstream_is_forwarded_without_extra_provider_calls()
    {
        var provider = new BlockingProvider();
        var wrapped = new TelemetryObservingModelProvider(provider, new InMemoryTelemetrySink());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var enumerator = wrapped.StreamAsync(Request(), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<ResponseStarted>(enumerator.Current);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => enumerator.MoveNextAsync().AsTask());

        Assert.Equal(1, provider.Calls);
        Assert.Equal(cancellation.Token, provider.SeenCancellationToken);
    }

    [Fact]
    public async Task Response_failed_is_forwarded_unchanged_without_retaining_error_text()
    {
        var failure = new ResponseFailed("private error type", "private provider message");
        var provider = new ScriptedProvider(new ModelStreamEvent[] { failure });
        var sink = new InMemoryTelemetrySink();
        var wrapped = new TelemetryObservingModelProvider(provider, sink);
        var observed = new List<ModelStreamEvent>();

        await foreach (var item in wrapped.StreamAsync(Request(), TestContext.Current.CancellationToken))
            observed.Add(item);

        Assert.Same(failure, Assert.Single(observed));
        Assert.Equal(1, provider.Calls);
        Assert.Contains(sink.Snapshot(), record => record.Kind == TelemetryKind.Progress
            && record.Signal == TelemetrySignal.ResponseFailedCount && record.Value == 1);
        var telemetryJson = System.Text.Json.JsonSerializer.Serialize(sink.Snapshot());
        Assert.DoesNotContain("private", telemetryJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Explorer_journal_contains_canonical_response_but_never_stream_delta_payloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-telemetry-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var canonicalResponse = Final("canonical final response");
            var provider = new ScriptedProvider(new ModelStreamEvent[]
            {
                new ResponseStarted(0),
                new TextDelta(0, "secret delta must not be journaled"),
                new ReasoningDelta(1, "secret reasoning must not be journaled"),
                new ResponseCompleted(canonicalResponse),
            });
            var telemetry = new InMemoryTelemetrySink();
            var wrapped = new TelemetryObservingModelProvider(provider, telemetry);
            var tools = new FakeCatalog();
            var turn = new ExplorerTurn((request, token) => wrapped.Complete(request, token),
                ScriptedToolExecutor.WithCoreTools(tools, new ScriptedPermissionPolicy(
                    new Dictionary<string, PermissionDecision>())), tools,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null), store,
                codecs, new FileArtifactStore(Path.Combine(root, "blobs")), new InMemoryAuditSink(),
                new RedactionPolicy());

            var result = turn.Ask("question", "system", session, run.RunId, run.RootLane, "",
                TestContext.Current.CancellationToken);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal("canonical final response", result.FinalText);
            Assert.Equal(1, provider.Calls);
            Assert.NotEmpty(telemetry.Snapshot());
            var closedConnection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(closedConnection);
            closedConnection.Dispose();
            store = new SqliteEventStore(journal);
            var events = store.ReadFrom(session, 1);
            Assert.Contains(events, evt => evt.Type.ToString() == "model_step.completed");
            Assert.Contains(events, evt => evt.Type.ToString() == "model.completed");
            Assert.DoesNotContain(events, evt => evt.Type.ToString().Contains("telemetry", StringComparison.OrdinalIgnoreCase)
                || evt.Type.ToString().Contains("delta", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(events, evt => evt.PayloadJson.Contains("secret delta must not be journaled",
                StringComparison.Ordinal) || evt.PayloadJson.Contains("secret reasoning must not be journaled",
                StringComparison.Ordinal));
        }
        finally
        {
            if (store is not null)
            {
                var connection = (SqliteConnection)store.Connection;
                store.Close();
                SqliteConnection.ClearPool(connection);
                connection.Dispose();
            }
            Directory.Delete(root, true);
        }
    }

    private static ModelRequest Request() => new(
        new ModelSelection(new ModelIdValue("scripted"), 1024, ToolMode.Direct, null),
        Array.Empty<ModelMessage>(), null, Array.Empty<ToolDefinition>(), ToolChoice.Auto(), null, null,
        null, null);

    private static ModelResponse Final(string text) => new(new ContentBlock[] { new TextBlock(text) },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));

    private sealed class ScriptedProvider(IReadOnlyList<ModelStreamEvent> events) : IModelProvider
    {
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public int Calls { get; private set; }
        public CancellationToken SeenCancellationToken { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            SeenCancellationToken = cancellationToken;
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public int Calls { get; private set; }
        public CancellationToken SeenCancellationToken { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            SeenCancellationToken = cancellationToken;
            yield return new ResponseStarted(0);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ThrowingSink : ITelemetrySink
    {
        public void Record(TelemetryRecord record) => throw new InvalidOperationException("local telemetry failed");
    }
}
