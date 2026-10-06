using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class BudgetDenialLifecycleTests
{
    private static readonly IEventCodecRegistry Codecs = EventCodecs.Create();
    private const string Options = "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_plus\",\"intent\":\"allow_plus\",\"value\":10}]";

    private static InteractionId AddBudgetRequest(IEventStore store, SessionId session, string scope = "session",
        decimal baseline = 5m, decimal current = 5m, RunId? runId = null, string? day = null)
    {
        var id = InteractionId.New();
        var offer = new BudgetContinuationOffer(scope, baseline, current, runId?.ToString(), day);
        new EventStream(store, Codecs, session).Append(new InteractionRequested(id,
            InteractionKind.BudgetExceeded, BudgetContinuation.Context("limit", offer), Options, "deny",
            null, null, null, null, 0, 1));
        return id;
    }

    [Fact]
    public void User_deny_fails_only_origin_run_closes_its_work_and_preserves_unknown_effects()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var origin = TestRun.Open(store, session, "origin");
        var originStream = new EventStream(store, Codecs, session);
        var childTask = TaskId.New();
        var childLane = LaneId.New();
        var openTurn = TurnId.New();
        var startedCall = ToolCallId.New();
        var unknownCall = ToolCallId.New();
        var safeToCancelCall = ToolCallId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(origin.RunId)))
        {
            originStream.AppendBatch(new DomainEventPayload[]
            {
                new PlanCreated(PlanId.New(), origin.RunId, PlanItemId.New(), "origin plan"),
                new TaskCreated(childTask, origin.RunId, "child", Array.Empty<TaskDependency>(),
                    new TaskBudget(null, null, null, null)),
                new TaskReady(childTask),
                new LaneCreated(childLane, childTask, ProfileId.New()),
                new LaneStarted(childLane),
                new TaskStarted(childTask, childLane),
                new TurnStarted(openTurn, childLane),
                new ToolCallRequested(startedCall, "started", "write", "{}"),
                new ToolCallPrepared(startedCall, "{}"),
                new PermissionEvaluated(startedCall, PermissionDecision.Allow, "[]", null),
                new ToolCallAuthorized(startedCall),
                new ToolCallStarted(startedCall, EffectClass.NonIdempotent, null),
                new ToolCallRequested(unknownCall, "unknown", "write", "{}"),
                new ToolCallPrepared(unknownCall, "{}"),
                new PermissionEvaluated(unknownCall, PermissionDecision.Allow, "[]", null),
                new ToolCallAuthorized(unknownCall),
                new ToolCallStarted(unknownCall, EffectClass.NonIdempotent, null),
                new ToolCallEffectUnknown(unknownCall, EffectClass.NonIdempotent),
                new ToolCallRequested(safeToCancelCall, "not-started", "read", "{}"),
                new ToolCallPrepared(safeToCancelCall, "{}"),
                new PermissionEvaluated(safeToCancelCall, PermissionDecision.Allow, "[]", null),
                new ToolCallAuthorized(safeToCancelCall),
            }, DurabilityClass.Standard);
        }
        var deniedInteraction = AddBudgetRequest(store, session);
        var siblingInteraction = AddBudgetRequest(store, session);

        // A newer Run is deliberately the ambient/current Run when the old interaction is answered.
        var foreign = TestRun.Open(store, session, "foreign");
        var foreignInteraction = AddBudgetRequest(store, session);
        var otherSession = SessionId.New();
        var otherSessionRun = TestRun.Open(store, otherSession, "other session");
        var otherSessionInteraction = AddBudgetRequest(store, otherSession);

        var before = store.ReadFrom(session, 1);
        var source = Assert.Single(before, evt => Codecs.Decode(evt) is InteractionRequested request
            && request.InteractionId == deniedInteraction);
        Assert.Equal(origin.RunId, source.RunId);

        new RunControlService(store, Codecs).Respond(session, deniedInteraction, "deny");

        var events = store.ReadFrom(session, 1);
        Assert.Equal(RunState.Failed, RunProjection.Replay(session, origin.RunId, Codecs, events).State);
        Assert.Equal(RunState.Running, RunProjection.Replay(session, foreign.RunId, Codecs, events).State);
        Assert.Equal(RunState.Running, RunProjection.Replay(otherSession, otherSessionRun.RunId,
            Codecs, store.ReadFrom(otherSession, 1)).State);

        var tracker = CanonicalStateTracker.Replay(Codecs, events);
        Assert.Equal(TaskState.Cancelled, tracker.Task(origin.RootTask));
        Assert.Equal(TaskState.Cancelled, tracker.Task(childTask));
        Assert.Equal(LaneState.Cancelled, tracker.Lane(origin.RootLane));
        Assert.Equal(LaneState.Cancelled, tracker.Lane(childLane));
        Assert.Equal(LaneState.Running, tracker.Lane(foreign.RootLane));
        Assert.Equal(PlanItemState.Cancelled, tracker.PlanItem(Assert.Single(before
            .Select(Codecs.Decode).OfType<PlanCreated>().Where(plan => plan.RunId == origin.RunId).Select(plan => plan.RootItemId))));
        Assert.Equal(TurnState.Interrupted, tracker.Turn(openTurn));
        Assert.Equal(ToolCallState.EffectUnknown, tracker.ToolCall(startedCall));
        Assert.Equal(ToolCallState.EffectUnknown, tracker.ToolCall(unknownCall));
        Assert.Equal(ToolCallState.Cancelled, tracker.ToolCall(safeToCancelCall));
        Assert.Equal(new[] { startedCall, unknownCall }.OrderBy(id => id.ToString()),
            new RunControlService(store, Codecs).UnreconciledEffects(session).OrderBy(id => id.ToString()));

        var decoded = events.Select(Codecs.Decode).ToArray();
        Assert.Contains(decoded.OfType<RunFailed>(), failed => failed.RunId == origin.RunId
            && failed.Cause == "BudgetExceeded");
        Assert.Contains(events, evt => evt.RunId == origin.RunId && Codecs.Decode(evt) is InteractionResolved resolved
            && resolved.InteractionId == deniedInteraction && resolved.Cause == InteractionCause.User);
        Assert.Contains(decoded.OfType<InteractionExpired>(), expired => expired.InteractionId == siblingInteraction);
        Assert.DoesNotContain(decoded.OfType<InteractionExpired>(), expired => expired.InteractionId == foreignInteraction);
        Assert.DoesNotContain(store.ReadFrom(otherSession, 1).Select(Codecs.Decode).OfType<InteractionExpired>(),
            expired => expired.InteractionId == otherSessionInteraction);

        Assert.Throws<InteractionNotPendingException>(() => new RunControlService(store, Codecs)
            .Respond(session, deniedInteraction, "deny"));
        Assert.Single(store.ReadFrom(session, 1).Select(Codecs.Decode)
            .OfType<RunFailed>(), failed => failed.RunId == origin.RunId);
        CanonicalStateTracker.Replay(Codecs, events);
    }

    [Fact]
    public void No_client_resolves_only_budget_deny_and_never_grants_allow_plus()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var interaction = AddBudgetRequest(store, session, "run", 5m, 5m, run.RunId);

        new RunControlService(store, Codecs).ResolveBudgetWithoutClient(session, interaction);

        var events = store.ReadFrom(session, 1);
        var resolution = Assert.Single(events.Select(Codecs.Decode).OfType<InteractionResolved>());
        Assert.Equal("deny", resolution.OptionId);
        Assert.Equal(InteractionCause.NoClient, resolution.Cause);
        Assert.Equal(RunState.Failed, RunProjection.Replay(session, run.RunId, Codecs, events).State);
        Assert.Equal(5m, BudgetContinuation.Limit(events, Codecs, session, run.RunId,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), "run", 5m));
    }

    [Fact]
    public void Missing_or_terminal_origin_fails_closed_without_touching_a_different_active_run()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var unscopedInteraction = InteractionId.New();
        new EventStream(store, codecs, session).Append(new InteractionRequested(unscopedInteraction,
            InteractionKind.BudgetExceeded, "{}", "[{\"id\":\"deny\"}]", "deny",
            null, null, null, null, 0, 1));
        var active = TestRun.Open(store, session, "active");
        var service = new RunControlService(store, codecs);
        var beforeMissing = store.ReadFrom(session, 1).Count;
        Assert.Throws<InvalidInteractionOptionException>(() => service.Respond(session, unscopedInteraction, "deny"));
        Assert.Equal(beforeMissing, store.ReadFrom(session, 1).Count);

        var terminalInteraction = AddBudgetRequest(store, session);
        new EventStream(store, codecs, session).Append(new RunFailed(active.RunId, "prior failure"));
        var next = TestRun.Open(store, session, "next");
        var beforeTerminal = store.ReadFrom(session, 1).Count;
        Assert.Throws<RunNotActiveException>(() => service.Respond(session, terminalInteraction, "deny"));
        Assert.Equal(beforeTerminal, store.ReadFrom(session, 1).Count);
        Assert.Equal(RunState.Running, RunProjection.Replay(session, next.RunId, codecs,
            store.ReadFrom(session, 1)).State);
    }

    [Fact]
    public void No_client_cannot_resolve_another_interaction_kind()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        TestRun.Open(store, session);
        var interaction = InteractionId.New();
        new EventStream(store, Codecs, session).Append(new InteractionRequested(interaction,
            InteractionKind.PlanApproval, "{}", "[{\"id\":\"reject\"}]", "reject",
            null, null, null, null, 0, 1));
        var before = store.ReadFrom(session, 1).Count;

        Assert.Throws<InvalidInteractionOptionException>(() => new RunControlService(store, Codecs)
            .ResolveBudgetWithoutClient(session, interaction));
        Assert.Equal(before, store.ReadFrom(session, 1).Count);
    }
}
