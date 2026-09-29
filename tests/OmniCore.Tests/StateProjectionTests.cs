using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using DomainTask = OmniCore.Domain.Task;

namespace OmniCore.Tests;

/// <summary>
/// EPIC-003: las proyecciones calculan el estado con las máquinas de ADR-0036 y rechazan un
/// journal inválido; la actividad de Lane y los items contenedor se derivan, nunca se persisten.
/// </summary>
public sealed class StateProjectionTests
{
    private static readonly EventCodecs Codecs = EventCodecs.Create();

    /// <summary>Escribe el envelope directamente, saltándose la validación de EventStream.</summary>
    private static void Raw(IEventStore store, SessionId session, DomainEventPayload payload) =>
        store.Append(session, DomainEvent.Create(session, payload.Type(), payload.SchemaVersion(), null, null,
            null, null, null, null, null, null, Array.Empty<ArtifactRef>(), Codecs.CodecFor(payload.Type()).Encode(payload)),
            DurabilityClass.Standard, CancellationToken.None);

    private static RunCreated NewRun(SessionId session, RunId run) =>
        new(run, session, "objetivo", RunMode.Act, ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
            new TaskBudget(null, null, null, null), TaskId.New(), DateTimeOffset.UtcNow);

    // ── Proyecciones ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_projection_rejects_a_journal_with_an_invalid_transition()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = RunId.New();
        Raw(store, session, NewRun(session, run));
        Raw(store, session, new RunCompleted(run, RunOutcome.Completed)); // Created → Completed

        Assert.Throws<InvalidStateTransitionException>(() =>
            RunProjection.Replay(session, run, Codecs, store.ReadFrom(session, 1)));
    }

    [Fact]
    public void Task_and_lane_projections_reject_events_for_entities_that_do_not_exist()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        Raw(store, session, new TaskReady(TaskId.New()));
        Assert.Throws<InvalidStateTransitionException>(() =>
            TaskGraphProjection.Replay(Codecs, store.ReadFrom(session, 1)));

        var laneStore = new InMemoryEventStore();
        Raw(laneStore, session, new LaneStarted(LaneId.New()));
        Assert.Throws<InvalidStateTransitionException>(() =>
            LaneProjection.Replay(Codecs, laneStore.ReadFrom(session, 1)));
    }

    [Fact]
    public void Each_run_projection_only_sees_its_own_run()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var first = RunId.New();
        var second = RunId.New();
        var stream = new EventStream(store, Codecs, session);
        stream.Append(NewRun(session, first));
        stream.Append(new RunStarted(first));
        stream.Append(NewRun(session, second));
        stream.Append(new RunStarted(second));
        stream.Append(new RunCancelled(second));

        var events = store.ReadFrom(session, 1);
        Assert.Equal(RunState.Running, RunProjection.Replay(session, first, Codecs, events).State);
        Assert.Equal(RunState.Cancelled, RunProjection.Replay(session, second, Codecs, events).State);
    }

    // ── Items contenedor ─────────────────────────────────────────────────────────────────

    private static (PlanProjection Plan, PlanItemId Parent) PlanWithChildren(params PlanItemState[] childStates)
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var opened = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var plan = PlanId.New();
        var root = PlanItemId.New();
        var parent = PlanItemId.New();
        stream.Append(new PlanCreated(plan, opened.RunId, root, "objetivo"));
        stream.Append(new PlanItemAdded(parent, plan, "contenedor", 2, null, Array.Empty<PlanItemId>(), true,
            new Dictionary<string, string>()));
        var order = 3;
        foreach (var state in childStates)
        {
            var child = PlanItemId.New();
            stream.Append(new PlanItemAdded(child, plan, "hijo", order++, parent, Array.Empty<PlanItemId>(), true,
                new Dictionary<string, string>()));
            if (state == PlanItemState.Pending)
            {
                continue;
            }

            stream.Append(new PlanItemStarted(child));
            if (state == PlanItemState.Completed) stream.Append(new PlanItemCompleted(child, null));
            if (state == PlanItemState.Failed) stream.Append(new PlanItemFailed(child, "falló"));
        }

        return (PlanProjection.Replay(Codecs, store.ReadFrom(session, 1)), parent);
    }

    [Fact]
    public void A_container_is_completed_when_all_its_required_children_are()
    {
        var (plan, parent) = PlanWithChildren(PlanItemState.Completed, PlanItemState.Completed);
        Assert.Equal(PlanItemState.Completed, plan.Item(parent)!.State);
    }

    [Fact]
    public void A_container_fails_when_a_required_child_fails()
    {
        var (plan, parent) = PlanWithChildren(PlanItemState.Completed, PlanItemState.Failed);
        Assert.Equal(PlanItemState.Failed, plan.Item(parent)!.State);
    }

    [Fact]
    public void A_container_is_in_progress_while_a_child_works()
    {
        var (plan, parent) = PlanWithChildren(PlanItemState.InProgress, PlanItemState.Pending);
        Assert.Equal(PlanItemState.InProgress, plan.Item(parent)!.State);
    }

    [Fact]
    public void Only_leaves_count_for_the_completion_gate()
    {
        var (plan, parent) = PlanWithChildren(PlanItemState.Completed);
        var leaves = PlanCompletionGate.Leaves(plan).Select(item => item.Id).ToArray();
        Assert.DoesNotContain(parent, leaves);
    }

    // ── Actividad de Lane ────────────────────────────────────────────────────────────────

    private sealed class LaneFixture
    {
        public InMemoryEventStore Store { get; } = new();

        public SessionId Session { get; } = SessionId.New();

        public TestRun.Opened Run { get; }

        public EventStream Stream { get; }

        public LaneFixture()
        {
            Run = TestRun.Open(Store, Session);
            Stream = new EventStream(Store, Codecs, Session);
        }

        public LaneActivity Activity() =>
            LaneActivityProjection.Derive(Codecs, Store.ReadFrom(Session, 1), Run.RootLane);
    }

    [Fact]
    public void Lane_is_waiting_for_the_model_after_a_turn_starts()
    {
        var fx = new LaneFixture();
        Assert.Equal(LaneActivity.None, fx.Activity());

        var turn = TurnId.New();
        fx.Stream.Append(new TurnStarted(turn, fx.Run.RootLane));
        Assert.Equal(LaneActivity.WaitingForModel, fx.Activity());

        fx.Stream.Append(new ModelCompleted(turn, null));
        fx.Stream.Append(new TurnCompleted(turn));
        Assert.Equal(LaneActivity.None, fx.Activity());
    }

    [Fact]
    public void Lane_is_waiting_for_a_tool_while_it_runs()
    {
        var fx = new LaneFixture();
        var turn = TurnId.New();
        var call = ToolCallId.New();
        fx.Stream.Append(new TurnStarted(turn, fx.Run.RootLane));
        fx.Stream.AppendBatch(new DomainEventPayload[] {
            new ToolCallRequested(call, "pc", "fake.read", "{}"),
            new ToolCallPrepared(call, "{}"),
            new PermissionEvaluated(call, PermissionDecision.Allow, "[]", null),
            new ToolCallAuthorized(call),
            new ToolCallStarted(call, EffectClass.None, null),
        }, DurabilityClass.Standard);
        Assert.Equal(LaneActivity.WaitingForTool, fx.Activity());

        fx.Stream.Append(new ToolCallSucceeded(call, "{}"));
        Assert.NotEqual(LaneActivity.WaitingForTool, fx.Activity());
    }

    [Fact]
    public void Lane_is_waiting_for_permission_until_the_interaction_is_resolved()
    {
        var fx = new LaneFixture();
        var interaction = InteractionId.New();
        fx.Stream.Append(new InteractionRequested(interaction, InteractionKind.Permission, "{}", "[]", "deny", null,
            fx.Run.RootLane, null, null, 0, 1));
        Assert.Equal(LaneActivity.WaitingForPermission, fx.Activity());

        fx.Stream.Append(new InteractionResolved(interaction, "allow_once", InteractionCause.User));
        Assert.Equal(LaneActivity.None, fx.Activity());
    }

    [Fact]
    public void Lane_is_waiting_for_input_until_the_user_answers()
    {
        var fx = new LaneFixture();
        fx.Stream.Append(new RunAwaitingInput(fx.Run.RunId, fx.Run.RootLane));
        Assert.Equal(LaneActivity.WaitingForInput, fx.Activity());

        fx.Stream.Append(new UserInputReceived(fx.Run.RunId, "[]", null));
        Assert.Equal(LaneActivity.None, fx.Activity());
    }

    [Fact]
    public void Lane_is_stalled_until_a_progress_signal()
    {
        var fx = new LaneFixture();
        var plan = PlanId.New();
        var root = PlanItemId.New();
        fx.Stream.Append(new PlanCreated(plan, fx.Run.RunId, root, "objetivo"));
        fx.Stream.Append(new ProgressStalled(root, 6, DateTimeOffset.UtcNow));
        Assert.Equal(LaneActivity.Stalled, fx.Activity());

        fx.Stream.Append(new PlanItemStarted(root));
        Assert.Equal(LaneActivity.None, fx.Activity());
    }

    [Fact]
    public void A_finished_lane_has_no_activity()
    {
        var fx = new LaneFixture();
        fx.Stream.Append(new TurnStarted(TurnId.New(), fx.Run.RootLane));
        fx.Stream.Append(new LaneCompleted(fx.Run.RootLane, null));
        Assert.Equal(LaneActivity.None, fx.Activity());
    }

    [Fact]
    public void Lane_activity_is_never_persisted_as_an_event()
    {
        // INV-027: no existe un tipo de evento para la actividad derivada.
        var registered = typeof(DomainEventPayload).Assembly.GetTypes()
            .Where(t => typeof(DomainEventPayload).IsAssignableFrom(t) && !t.IsInterface)
            .Select(t => t.Name)
            .ToArray();
        Assert.DoesNotContain(registered, name => name.StartsWith("Lane", StringComparison.Ordinal)
            && (name.Contains("Waiting", StringComparison.Ordinal) || name.Contains("Stalled", StringComparison.Ordinal)));
        _ = typeof(DomainTask);
    }
}
