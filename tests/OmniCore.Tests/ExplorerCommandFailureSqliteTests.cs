using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Real SQLite persistence/reopen with injected callback/read faults; no provider calls.</summary>
public sealed class ExplorerCommandFailureSqliteTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Partial_failure_retains_exact_causation_and_reopens_without_inventing_unknown_ranges(
        bool unreadable, bool cancelled)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-explorer-command-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "journal.db");
        var sqlite = new SqliteEventStore(path);
        try
        {
            var store = new ReadFaultStore(sqlite);
            var codecs = EventCodecs.Create();
            var server = new OmniServer(store, codecs, new InMemoryAuditSink());
            var started = server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"act\",\"objective\":\"fault fixture\",\"workspace\":"
                + System.Text.Json.JsonSerializer.Serialize(root) + "}"), TestContext.Current.CancellationToken);
            Assert.Equal("ok", started.Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var before = store.CurrentSequence(session);
            using var cancellation = new CancellationTokenSource();
            Exception failure = cancelled ? new OperationCanceledException(cancellation.Token)
                : new IOException("callback failure");
            var execution = server.ExecuteExplorerTurn(session, run, token =>
            {
                Assert.Equal(cancellation.Token, token);
                new EventStream(store, codecs, session).Append(
                    new UserInputReceived(run, "[\"retained input\"]", null), DurabilityClass.Barrier);
                if (cancelled) cancellation.Cancel();
                store.FailReads = unreadable;
                throw failure;
            }, cancellation.Token);

            Assert.Same(failure, execution.Failure);
            Assert.Null(execution.Result);
            Assert.Equal("error", execution.Ack.Status);
            Assert.Equal(unreadable ? RuntimeCommandOutcomeKind.Deferred : RuntimeCommandOutcomeKind.Accepted,
                execution.Ack.Outcome?.Kind);
            Assert.Equal(unreadable ? "JournalOutcomeUnavailable" : null, execution.Ack.Outcome?.Reason);
            Assert.Equal(unreadable ? (long?)null : before + 1, execution.Ack.FirstSeq);
            Assert.Equal(unreadable ? (long?)null : before + 1, execution.Ack.LastSeq);
            Assert.DoesNotContain("private read fault", execution.Ack.Error ?? "", StringComparison.Ordinal);
            Assert.Null(CausationScope.Current);
            Assert.Null(ExecutionScope.Current);
            store.FailReads = false;
            Assert.Equal(session, server.LastSessionId());
            Assert.Equal(run, server.LastRunId());
            Close(sqlite);
            sqlite = new SqliteEventStore(path);
            var retained = Assert.Single(sqlite.ReadFrom(session, before + 1));
            Assert.Equal(before + 1, retained.Sequence);
            Assert.Equal(new CommandCausation(new CommandId(Guid.Parse(execution.Ack.CommandId))), retained.Causation);
            Assert.Equal("[\"retained input\"]", Assert.IsType<UserInputReceived>(codecs.Decode(retained)).InputPartsJson);
            Assert.Equal(RunState.Running, RunProjection.Replay(session, run, codecs, sqlite.ReadFrom(session, 1)).State);
            CanonicalStateTracker.Replay(codecs, sqlite.ReadFrom(session, 1));
        }
        finally
        {
            Close(sqlite);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Pre_cancelled_admission_does_not_invoke_callback_or_write_partial_effects()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-explorer-command-cancel-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "journal.db");
        var sqlite = new SqliteEventStore(path);
        try
        {
            var store = new ReadFaultStore(sqlite);
            var codecs = EventCodecs.Create();
            var server = new OmniServer(store, codecs, new InMemoryAuditSink());
            var started = server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"act\",\"objective\":\"cancel admission fixture\",\"workspace\":"
                + System.Text.Json.JsonSerializer.Serialize(root) + "}"), TestContext.Current.CancellationToken);
            Assert.Equal("ok", started.Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var before = store.CurrentSequence(session);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var calls = 0;

            var execution = server.ExecuteExplorerTurn(session, run, _ =>
            {
                calls++;
                new EventStream(store, codecs, session).Append(
                    new UserInputReceived(run, "[\"must not be written\"]", null), DurabilityClass.Barrier);
                return new ExplorerTurn.TurnResult("should not run", StopReason.EndTurn, 0,
                    new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ExplorerTurn.ToolUseTrace>(), null);
            }, cancellation.Token);

            Assert.Null(execution.Result);
            Assert.IsType<OperationCanceledException>(execution.Failure);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, execution.Ack.Outcome?.Kind);
            Assert.Equal(0, calls);
            Assert.Equal(before, store.CurrentSequence(session));
            Assert.Empty(store.ReadFrom(session, before + 1));
        }
        finally
        {
            Close(sqlite);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completion_gate_failure_reopens_validation_prefix_without_false_completion(bool unreadable)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-completion-command-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "journal.db");
        var sqlite = new SqliteEventStore(path);
        try
        {
            var store = new ReadFaultStore(sqlite);
            var codecs = EventCodecs.Create();
            var server = new OmniServer(store, codecs, new InMemoryAuditSink());
            var started = server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"act\",\"objective\":\"completion fault fixture\",\"workspace\":"
                + System.Text.Json.JsonSerializer.Serialize(root) + "}"), TestContext.Current.CancellationToken);
            Assert.Equal("ok", started.Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var before = store.CurrentSequence(session);
            var failure = new IOException("gate failure after validation started");
            var calls = 0;
            var result = server.CheckRunCompletionAndGate(session, run, _ =>
            {
                calls++;
                store.FailReads = unreadable;
                throw failure;
            }, null);
            Assert.Equal(1, calls);
            Assert.Same(failure, result.Failure);
            Assert.Null(result.Completed);
            Assert.Equal("error", result.Ack.Status);
            Assert.Equal(unreadable ? RuntimeCommandOutcomeKind.Deferred : RuntimeCommandOutcomeKind.Accepted,
                result.Ack.Outcome?.Kind);
            Assert.Equal(unreadable ? "JournalOutcomeUnavailable" : null, result.Ack.Outcome?.Reason);
            Assert.Equal(session, server.LastSessionId());
            Assert.Equal(run, server.LastRunId());
            Assert.Null(CausationScope.Current);
            Assert.Null(ExecutionScope.Current);
            Close(sqlite);
            sqlite = new SqliteEventStore(path);
            var prefix = sqlite.ReadFrom(session, before + 1);
            Assert.NotEmpty(prefix);
            Assert.All(prefix, evt => Assert.Equal(
                new CommandCausation(new CommandId(Guid.Parse(result.Ack.CommandId))), evt.Causation));
            Assert.Contains(prefix, evt => codecs.Decode(evt) is RunValidationStarted);
            Assert.DoesNotContain(prefix, evt => codecs.Decode(evt) is RunCompleted or RunFailed);
            Assert.Equal(unreadable ? (long?)null : prefix.Min(evt => evt.Sequence), result.Ack.FirstSeq);
            Assert.Equal(unreadable ? (long?)null : prefix.Max(evt => evt.Sequence), result.Ack.LastSeq);
            var retained = sqlite.ReadFrom(session, 1);
            Assert.Equal(RunState.Validating, RunProjection.Replay(session, run, codecs, retained).State);
            CanonicalStateTracker.Replay(codecs, retained);
        }
        finally
        {
            Close(sqlite);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Close(SqliteEventStore store)
    {
        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        store.Connection.Dispose();
    }

    private sealed class ReadFaultStore(IEventStore inner) : IEventStore
    {
        public bool FailReads { get; set; }
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken ct)
            => inner.Append(session, evt, durability, ct);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken ct) => inner.AppendBatch(session, events, durability, ct);
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => FailReads
            ? throw new IOException("private read fault") : inner.ReadFrom(session, from);
    }
}
