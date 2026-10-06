using Microsoft.Data.Sqlite;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Durable run-control terminal events keep the scope of the work they close.</summary>
public sealed class CancelledToolCallScopeRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_command_attributes_started_and_permission_waiting_work_to_original_run(
        bool installForeignAmbientScope)
    {
        var root = NewPrivateRoot();
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var stateFile = Path.Combine(root, "last-session.txt");
        SqliteEventStore? store = null;
        try
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile);
            Assert.Equal("ok", Send(server, JsonSerializer.Serialize(new
            {
                cmd = "session.input", text = "start run", mode = "act",
            })).Status);

            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var original = RunScope(store, codecs, session, run);
            var turn = TurnId.New();
            var awaitingCall = ToolCallId.New();
            var startedCall = ToolCallId.New();
            var interaction = InteractionId.New();
            AppendAwaitingPermissionAndStartedCalls(store, codecs, session, original, turn,
                awaitingCall, interaction, startedCall);
            var fixtureEvents = store.ReadFrom(session, 1);
            AssertOriginalScope(Assert.Single(fixtureEvents, evt => codecs.Decode(evt) is InteractionRequested request
                && request.InteractionId == interaction), original, turn);
            AssertOriginalScope(Assert.Single(fixtureEvents, evt => codecs.Decode(evt) is ToolCallStarted started
                && started.ToolCallId == startedCall), original, turn);

            var cancelMessageId = Ids.NewV7();
            var foreign = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
            CommandAck ack;
            if (installForeignAmbientScope)
            {
                using (ExecutionScope.Begin(foreign))
                {
                    ack = Send(server, "{\"cmd\":\"run.cancel\"}", cancelMessageId);
                    Assert.Same(foreign, ExecutionScope.Current);
                }
            }
            else
            {
                Assert.Null(ExecutionScope.Current);
                ack = Send(server, "{\"cmd\":\"run.cancel\"}", cancelMessageId);
                Assert.Null(ExecutionScope.Current);
            }
            Assert.Equal("ok", ack.Status);

            // A later Run in the same session makes accidental "latest cursor" attribution visible.
            Assert.Equal("ok", Send(server, JsonSerializer.Serialize(new
            {
                cmd = "session.input", text = "next run", mode = "act",
            })).Status);
            Assert.NotEqual(run, server.LastRunId());

            var beforeReopen = store.ReadFrom(session, 1).Select(Snapshot).ToArray();
            CloseAndClearPool(store);
            store = new SqliteEventStore(journal);
            var reopened = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile);
            var events = store.ReadFrom(session, 1).ToArray();
            Assert.Equal(beforeReopen, events.Select(Snapshot).ToArray());
            Assert.Equal(RunState.Cancelled,
                RunProjection.Replay(session, run, codecs, events).State);

            var commandCause = new CommandCausation(new CommandId(Guid.Parse(cancelMessageId)));
            var cancelled = Assert.Single(events, evt => codecs.Decode(evt) is ToolCallCancelled payload
                && payload.ToolCallId == awaitingCall);
            var expired = Assert.Single(events, evt => codecs.Decode(evt) is InteractionExpired payload
                && payload.InteractionId == interaction);
            var failed = Assert.Single(events, evt => codecs.Decode(evt) is ToolCallFailed payload
                && payload.ToolCallId == startedCall);
            var interrupted = Assert.Single(events, evt => codecs.Decode(evt) is TurnInterrupted payload
                && payload.TurnId == turn);

            AssertOriginalAttribution(cancelled, original, turn, commandCause);
            AssertOriginalAttribution(expired, original, turn, commandCause);
            AssertOriginalAttribution(failed, original, turn, commandCause);
            AssertOriginalAttribution(interrupted, original, turn, commandCause);
            Assert.Equal(ToolErrorCode.Cancellation,
                Assert.IsType<ToolCallFailed>(codecs.Decode(failed)).ErrorCode);
            Assert.Equal("OmniCore.Engine.EventStream", cancelled.Source);
            Assert.Equal("OmniCore.Engine.EventStream", expired.Source);
            Assert.Equal("OmniCore.Engine.EventStream", failed.Source);
            Assert.Equal("OmniCore.Engine.EventStream", interrupted.Source);
            Assert.Equal(server.LastRunId(), reopened.LastRunId());
        }
        finally
        {
            CloseAndClearPool(store);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupt_attributes_started_tool_and_turn_terminal_events_to_their_original_scope(
        bool installForeignAmbientScope)
    {
        var root = NewPrivateRoot();
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var stateFile = Path.Combine(root, "last-session.txt");
        SqliteEventStore? store = null;
        try
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile);
            Assert.Equal("ok", Send(server, JsonSerializer.Serialize(new
            {
                cmd = "session.input", text = "start interrupt run", mode = "act",
            })).Status);

            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var original = RunScope(store, codecs, session, run);
            var turn = TurnId.New();
            var call = ToolCallId.New();
            AppendStartedCall(store, codecs, session, original, turn, call);
            var fixtureEvents = store.ReadFrom(session, 1);
            AssertOriginalScope(Assert.Single(fixtureEvents, evt => codecs.Decode(evt) is ToolCallStarted started
                && started.ToolCallId == call), original, turn);

            var interruptMessageId = Ids.NewV7();
            var foreign = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
            CommandAck ack;
            if (installForeignAmbientScope)
            {
                using (ExecutionScope.Begin(foreign))
                {
                    ack = Send(server, "{\"cmd\":\"run.interrupt\"}", interruptMessageId);
                    Assert.Same(foreign, ExecutionScope.Current);
                }
            }
            else
            {
                Assert.Null(ExecutionScope.Current);
                ack = Send(server, "{\"cmd\":\"run.interrupt\"}", interruptMessageId);
                Assert.Null(ExecutionScope.Current);
            }
            Assert.Equal("ok", ack.Status);

            // Interrupt leaves the Run awaiting input. A second interrupt cancels it, allowing a
            // subsequent Run to prove that the old terminal envelopes do not inherit a new cursor.
            Assert.Equal("ok", Send(server, "{\"cmd\":\"run.interrupt\"}").Status);
            Assert.Equal("ok", Send(server, JsonSerializer.Serialize(new
            {
                cmd = "session.input", text = "another run", mode = "act",
            })).Status);
            Assert.NotEqual(run, server.LastRunId());

            var beforeReopen = store.ReadFrom(session, 1).Select(Snapshot).ToArray();
            CloseAndClearPool(store);
            store = new SqliteEventStore(journal);
            var reopened = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile);
            var events = store.ReadFrom(session, 1).ToArray();
            Assert.Equal(beforeReopen, events.Select(Snapshot).ToArray());
            Assert.Equal(RunState.Cancelled,
                RunProjection.Replay(session, run, codecs, events).State);

            var commandCause = new CommandCausation(new CommandId(Guid.Parse(interruptMessageId)));
            var failed = Assert.Single(events, evt => codecs.Decode(evt) is ToolCallFailed payload
                && payload.ToolCallId == call);
            var interrupted = Assert.Single(events, evt => codecs.Decode(evt) is TurnInterrupted payload
                && payload.TurnId == turn);
            AssertOriginalAttribution(failed, original, turn, commandCause);
            AssertOriginalAttribution(interrupted, original, turn, commandCause);
            Assert.Equal(ToolErrorCode.Cancellation,
                Assert.IsType<ToolCallFailed>(codecs.Decode(failed)).ErrorCode);
            Assert.Equal("OmniCore.Engine.EventStream", failed.Source);
            Assert.Equal("OmniCore.Engine.EventStream", interrupted.Source);
            Assert.Equal(server.LastRunId(), reopened.LastRunId());
        }
        finally
        {
            CloseAndClearPool(store);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AppendAwaitingPermissionAndStartedCalls(SqliteEventStore store,
        IEventCodecRegistry codecs, SessionId session, ExecutionScopeState original, TurnId turn,
        ToolCallId awaitingCall, InteractionId interaction, ToolCallId startedCall)
    {
        var stream = new EventStream(store, codecs, session);
        using (ExecutionScope.Begin(original with { TurnId = turn }))
        {
            stream.Append(new TurnStarted(turn, Assert.IsType<LaneId>(original.LaneId)));
            stream.AppendBatch(new DomainEventPayload[]
            {
                new ToolCallRequested(awaitingCall, "provider-awaiting", "filesystem.write", "{}"),
                new ToolCallPrepared(awaitingCall, "{}"),
                new PermissionEvaluated(awaitingCall, PermissionDecision.Ask, "{}", null),
                new PermissionRequested(awaitingCall, interaction),
                new InteractionRequested(interaction, InteractionKind.Permission, "{}",
                    "[{\"id\":\"allow_once\"},{\"id\":\"deny\"}]", "deny", null,
                    original.LaneId, null, null, 0, 1),
                new ToolCallRequested(startedCall, "provider-started", "filesystem.write", "{}"),
                new ToolCallPrepared(startedCall, "{}"),
                new PermissionEvaluated(startedCall, PermissionDecision.Allow, "{}", null),
                new ToolCallAuthorized(startedCall),
                new ToolCallStarted(startedCall, EffectClass.NonIdempotent, null),
            }, DurabilityClass.Standard);
        }
    }

    private static void AppendStartedCall(SqliteEventStore store, IEventCodecRegistry codecs,
        SessionId session, ExecutionScopeState original, TurnId turn, ToolCallId call)
    {
        var stream = new EventStream(store, codecs, session);
        using (ExecutionScope.Begin(original with { TurnId = turn }))
        {
            stream.Append(new TurnStarted(turn, Assert.IsType<LaneId>(original.LaneId)));
            stream.AppendBatch(new DomainEventPayload[]
            {
                new ToolCallRequested(call, "provider-started", "filesystem.write", "{}"),
                new ToolCallPrepared(call, "{}"),
                new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null),
                new ToolCallAuthorized(call),
                new ToolCallStarted(call, EffectClass.NonIdempotent, null),
            }, DurabilityClass.Standard);
        }
    }

    private static ExecutionScopeState RunScope(SqliteEventStore store, IEventCodecRegistry codecs,
        SessionId session, RunId run)
    {
        var events = store.ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, codecs, events);
        var task = Assert.IsType<TaskId>(projection.RootTask);
        var lane = Assert.Single(LaneProjection.Replay(codecs, events).ForTask(task)).Id;
        return new ExecutionScopeState(run, task, lane, null, ExecutionId: ExecutionId.New());
    }

    private static void AssertOriginalAttribution(DomainEvent evt, ExecutionScopeState original,
        TurnId turn, CausationId cause)
    {
        AssertOriginalScope(evt, original, turn);
        Assert.Equal(cause, evt.Causation);
        Assert.Equal(TimeSpan.Zero, evt.Timestamp.Offset);
    }

    private static void AssertOriginalScope(DomainEvent evt, ExecutionScopeState original, TurnId turn)
    {
        Assert.Equal(original.RunId, evt.RunId);
        Assert.Equal(original.RunId, evt.CorrelationId);
        Assert.Equal(original.TaskId, evt.TaskId);
        Assert.Equal(original.LaneId, evt.LaneId);
        Assert.Equal(turn, evt.TurnId);
        Assert.Equal(original.ExecutionId, evt.ExecutionId);
    }

    private static CommandAck Send(OmniServer server, string payload, string? messageId = null) =>
        server.Send(WireEnvelope.Command(messageId ?? Ids.NewV7(), payload),
            TestContext.Current.CancellationToken);

    private static string NewPrivateRoot() => Path.Combine(Path.GetTempPath(),
        "omni-m55-cancel-scope-" + Guid.NewGuid().ToString("N"));

    private static string Snapshot(DomainEvent evt) => JsonSerializer.Serialize(new
    {
        evt.EventId, evt.SessionId, evt.Sequence, evt.Type, evt.SchemaVersion, evt.Timestamp,
        evt.Causation, evt.CorrelationId, evt.RunId, evt.TaskId, evt.LaneId, evt.TurnId,
        evt.ToolCallId, evt.ExecutionId, evt.Source, evt.ArtifactRefs, evt.PayloadJson,
    });

    private static void CloseAndClearPool(SqliteEventStore? store)
    {
        if (store is null) return;
        var connection = (SqliteConnection)store.Connection;
        store.Close();
        SqliteConnection.ClearPool(connection);
    }
}
