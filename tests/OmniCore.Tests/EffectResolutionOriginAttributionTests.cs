using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class EffectResolutionOriginAttributionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Human_resolution_targets_old_effect_but_keeps_waiting_run_and_command_causation(bool sqlite, bool activeWaitingRun)
    {
        using var fixture = new Fixture(sqlite);
        var store = fixture.Store;
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var originRun = TestRun.Open(store, session, "terminal effect origin");
        var originTurn = TurnId.New();
        var originCall = ToolCallId.New();
        var originStream = new EventStream(store, codecs, session);
        using (ExecutionScope.Begin(new ExecutionScopeState(originRun.RunId, originRun.RootTask,
            originRun.RootLane, originTurn)))
        {
            originStream.Append(new TurnStarted(originTurn, originRun.RootLane));
            originStream.Append(new ToolCallRequested(originCall, "old-provider-call", "test.effect", "{}"));
            originStream.Append(new ToolCallPrepared(originCall, "{}"));
            originStream.Append(new PermissionEvaluated(originCall, PermissionDecision.Allow, "{}", null));
            originStream.Append(new ToolCallAuthorized(originCall));
            originStream.Append(new ToolCallStarted(originCall, EffectClass.Reconcilable, "{}"), DurabilityClass.Barrier);
            originStream.Append(new ToolCallEffectUnknown(originCall, EffectClass.Reconcilable));
        }
        new RunControlService(store, codecs).CancelRun(session, originRun.RunId);
        var originUnknown = Assert.Single(store.ReadFrom(session, 1), evt =>
            evt.RunId == originRun.RunId && codecs.Decode(evt) is ToolCallEffectUnknown unknown
                && unknown.ToolCallId == originCall);

        // TestRun bypasses the user-facing unresolved-effect start guard deliberately: it builds
        // the already-persisted second Run whose continuation recovery must hold for A's effect.
        var waitingRun = TestRun.Open(store, session, "waiting run");
        if (!activeWaitingRun) new RunControlService(store, codecs).CancelRun(session, waitingRun.RunId);
        var stateFile = fixture.StateFile;
        File.WriteAllText(stateFile, session + "\n" + waitingRun.RunId);
        try
        {
            var audit = new InMemoryAuditSink();
            var server = new OmniServer(store, codecs, audit, stateFile);
            var events = store.ReadFrom(session, 1);
            Assert.Equal(waitingRun.RunId, server.LastRunId());
            Assert.Equal(activeWaitingRun ? RunState.AwaitingInput : RunState.Cancelled,
                RunProjection.Replay(session, waitingRun.RunId, codecs, events).State);
            Assert.Equal(RunState.Cancelled, RunProjection.Replay(session, originRun.RunId, codecs, events).State);

            var requestEnvelope = Assert.Single(events, evt =>
                codecs.Decode(evt) is InteractionRequested request
                    && request.Kind == InteractionKind.ReconciliationConflict);
            var request = Assert.IsType<InteractionRequested>(codecs.Decode(requestEnvelope));
            var requestRun = activeWaitingRun ? waitingRun.RunId : originRun.RunId;
            Assert.Equal(requestRun, requestEnvelope.RunId);
            Assert.Equal(originCall, ReadToolCall(request.ToolCallJson));
            var initialEffectEnvelope = Assert.Single(events, evt =>
                codecs.Decode(evt) is ToolCallReconciled reconciled
                    && reconciled.ToolCallId == originCall && reconciled.Cause != InteractionCause.User);
            Assert.Equal(originRun.RunId, initialEffectEnvelope.RunId);
            Assert.Equal(originRun.RootTask, initialEffectEnvelope.TaskId);
            Assert.Equal(originRun.RootLane, initialEffectEnvelope.LaneId);
            Assert.Equal(originTurn, initialEffectEnvelope.TurnId);
            Assert.Equal(new EventCausation(originUnknown.EventId), initialEffectEnvelope.Causation);

            var commandId = CommandId.New();
            var command = WireEnvelope.Command(commandId.ToString(), JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond",
                interactionId = request.InteractionId.ToString(),
                optionId = "resolution_applied",
            }));
            var ack = server.Send(command, CancellationToken.None);
            Assert.Equal("ok", ack.Status);
            Assert.Equal(commandId.ToString(), ack.CommandId);

            events = store.ReadFrom(session, 1);
            var resolvedEnvelope = Assert.Single(events, evt =>
                codecs.Decode(evt) is InteractionResolved resolved && resolved.InteractionId == request.InteractionId);
            Assert.Equal(requestRun, resolvedEnvelope.RunId);
            Assert.Equal(new CommandCausation(commandId), resolvedEnvelope.Causation);

            var effectEnvelope = Assert.Single(events, evt =>
                codecs.Decode(evt) is ToolCallReconciled reconciled
                    && reconciled.ToolCallId == originCall && reconciled.Cause == InteractionCause.User);
            var effect = Assert.IsType<ToolCallReconciled>(codecs.Decode(effectEnvelope));
            Assert.Equal(ReconciliationOutcome.Applied, effect.Outcome);
            Assert.Equal(originCall, effect.ToolCallId);
            Assert.Equal(originRun.RunId, effectEnvelope.RunId);
            Assert.Equal(originRun.RunId, effectEnvelope.CorrelationId);
            Assert.Equal(originRun.RootTask, effectEnvelope.TaskId);
            Assert.Equal(originRun.RootLane, effectEnvelope.LaneId);
            Assert.Equal(originTurn, effectEnvelope.TurnId);
            Assert.Equal(new CommandCausation(commandId), effectEnvelope.Causation);
            var auditRecord = Assert.Single(audit.Records(), item => item.EventName == "effect.human_resolution");
            Assert.Equal(originRun.RunId, auditRecord.Run);

            var resumed = events.Where(evt => codecs.Decode(evt) is UserInputReceived input
                && input.RunId == waitingRun.RunId).ToArray();
            if (activeWaitingRun) Assert.Equal(waitingRun.RunId, Assert.Single(resumed).RunId);
            else Assert.Empty(resumed);
            Assert.Equal(activeWaitingRun ? RunState.Running : RunState.Cancelled,
                RunProjection.Replay(session, waitingRun.RunId, codecs, events).State);
            Assert.Equal(RunState.Cancelled, RunProjection.Replay(session, originRun.RunId, codecs, events).State);
            Assert.Empty(new RunControlService(store, codecs).UnreconciledEffects(session));
        }
        finally
        {
            File.Delete(stateFile);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-effect-origin-" + Guid.NewGuid().ToString("N"));
        private readonly string _journal;
        public IEventStore Store { get; }
        public string StateFile => Path.Combine(_root, "session.state");
        public Fixture(bool sqlite)
        {
            Directory.CreateDirectory(_root);
            _journal = Path.Combine(_root, "journal.db");
            Store = sqlite ? new SqliteEventStore(_journal) : new InMemoryEventStore();
        }
        public void Dispose()
        {
            if (Store is SqliteEventStore sqlite)
            {
                sqlite.Close();
                using var connection = new SqliteConnection("DataSource=" + _journal);
                SqliteConnection.ClearPool(connection);
            }
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ToolCallId ReadToolCall(string? json)
    {
        using var document = JsonDocument.Parse(json!);
        return ToolCallId.Parse(document.RootElement.GetProperty("toolCallId").GetString()!);
    }
}
