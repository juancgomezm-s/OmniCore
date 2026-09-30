using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;

namespace OmniCore.Tests;

public sealed class ReconcilerWatchdogTests
{
    [Fact]
    public void R6_marks_pending_ready_only_when_all_dependencies_are_completed_or_skipped()
    {
        var completedDependency = PlanItemId.New();
        var completedDependent = PlanItemId.New();
        var completedPlan = PlanOf(
            Item(completedDependency, "done", PlanItemState.Completed, 1),
            Item(completedDependent, "next", PlanItemState.Pending, 2, dependsOn: new[] { completedDependency }));
        AssertMutation(new ProgressReconciler().Reconcile(completedPlan, EmptyTasks(), LaneProjection.Empty()),
            PlanMutationKind.Ready, completedDependent);

        var skippedDependency = PlanItemId.New();
        var skippedDependent = PlanItemId.New();
        var skippedPlan = PlanOf(
            Item(skippedDependency, "skipped", PlanItemState.Skipped, 1),
            Item(skippedDependent, "next", PlanItemState.Pending, 2, dependsOn: new[] { skippedDependency }));
        AssertMutation(new ProgressReconciler().Reconcile(skippedPlan, EmptyTasks(), LaneProjection.Empty()),
            PlanMutationKind.Ready, skippedDependent);

        var unfinishedDependency = PlanItemId.New();
        var unfinishedDependent = PlanItemId.New();
        var unfinished = PlanOf(
            Item(unfinishedDependency, "not done", PlanItemState.Ready, 1),
            Item(unfinishedDependent, "wait", PlanItemState.Pending, 2, dependsOn: new[] { unfinishedDependency }));
        Assert.Empty(new ProgressReconciler().Reconcile(unfinished, EmptyTasks(), LaneProjection.Empty()));
    }

    [Theory]
    [InlineData(PlanItemState.Pending)]
    [InlineData(PlanItemState.Ready)]
    public void R1_starts_pending_or_ready_item_when_linked_task_is_running(PlanItemState state)
    {
        var task = TaskFixture.Create(TaskState.Running);
        var item = PlanItemId.New();
        var plan = PlanOf(Item(item, "work", state, 1, links: new[] { new PlanItemLink(task.TaskId, true, LinkRole.Implements) }));
        AssertMutation(new ProgressReconciler().Reconcile(plan, task.Tasks, task.Lanes), PlanMutationKind.Start, item);
    }

    [Fact]
    public void R2_completes_in_progress_item_when_required_tasks_complete_but_never_completes_unlinked_item()
    {
        var task = TaskFixture.Create(TaskState.Completed);
        var item = PlanItemId.New();
        var linkedPlan = PlanOf(Item(item, "work", PlanItemState.InProgress, 1,
            links: new[] { new PlanItemLink(task.TaskId, true, LinkRole.Verifies) }));
        AssertMutation(new ProgressReconciler().Reconcile(linkedPlan, task.Tasks, task.Lanes),
            PlanMutationKind.Complete, item);

        var unlinked = PlanOf(Item(PlanItemId.New(), "manual work", PlanItemState.InProgress, 1));
        Assert.Empty(new ProgressReconciler().Reconcile(unlinked, task.Tasks, task.Lanes));
    }

    [Fact]
    public void R3_and_R4_block_and_unblock_for_required_task_state()
    {
        var blockedTask = TaskFixture.Create(TaskState.Blocked);
        var blockedItem = PlanItemId.New();
        var inProgress = PlanOf(Item(blockedItem, "work", PlanItemState.InProgress, 1,
            links: new[] { new PlanItemLink(blockedTask.TaskId, true, LinkRole.Implements) }));
        AssertMutation(new ProgressReconciler().Reconcile(inProgress, blockedTask.Tasks, blockedTask.Lanes),
            PlanMutationKind.Block, blockedItem);

        var runningTask = TaskFixture.Create(TaskState.Running);
        var blocked = PlanOf(Item(blockedItem, "work", PlanItemState.Blocked, 1,
            links: new[] { new PlanItemLink(runningTask.TaskId, true, LinkRole.Implements) }));
        AssertMutation(new ProgressReconciler().Reconcile(blocked, runningTask.Tasks, runningTask.Lanes),
            PlanMutationKind.Unblock, blockedItem);
    }

    [Theory]
    [InlineData(FailurePolicy.BlockDependents, PlanMutationKind.Block)]
    [InlineData(FailurePolicy.FailRun, PlanMutationKind.Fail)]
    [InlineData(FailurePolicy.AllowPartial, PlanMutationKind.Fail)]
    public void R5_uses_run_failure_policy(FailurePolicy policy, PlanMutationKind expected)
    {
        var task = TaskFixture.Create(TaskState.Failed);
        var item = PlanItemId.New();
        var plan = PlanOf(Item(item, "work", PlanItemState.InProgress, 1,
            links: new[] { new PlanItemLink(task.TaskId, true, LinkRole.Implements) }));
        AssertMutation(new ProgressReconciler().Reconcile(plan, task.Tasks, task.Lanes, policy), expected, item);
    }

    [Fact]
    public void Terminal_items_and_containers_are_not_reconciled_directly()
    {
        var task = TaskFixture.Create(TaskState.Running);
        var parent = PlanItemId.New();
        var child = PlanItemId.New();
        var terminal = PlanItemId.New();
        var plan = PlanOf(
            Item(parent, "container", PlanItemState.Pending, 1),
            Item(child, "leaf", PlanItemState.Pending, 2, parent: parent,
                links: new[] { new PlanItemLink(task.TaskId, true, LinkRole.Implements) }),
            Item(terminal, "finished", PlanItemState.Completed, 3,
                links: new[] { new PlanItemLink(task.TaskId, true, LinkRole.Implements) }));

        var mutations = new ProgressReconciler().Reconcile(plan, task.Tasks, task.Lanes);
        Assert.DoesNotContain(mutations, mutation => mutation.ItemId!.Equals(parent));
        Assert.DoesNotContain(mutations, mutation => mutation.ItemId!.Equals(terminal));
        Assert.Contains(mutations, mutation => mutation.ItemId!.Equals(child)
            && mutation.Kind == PlanMutationKind.Start);
    }

    [Fact]
    public void Current_item_prefers_first_in_progress_leaf_then_first_ready_leaf_by_order()
    {
        var parent = PlanItemId.New();
        var earlyReady = PlanItemId.New();
        var laterActive = PlanItemId.New();
        var otherActive = PlanItemId.New();
        var plan = PlanOf(
            Item(parent, "container", PlanItemState.Pending, 1),
            Item(earlyReady, "ready", PlanItemState.Ready, 2, parent: parent),
            Item(laterActive, "first active", PlanItemState.InProgress, 3, parent: parent),
            Item(otherActive, "later active", PlanItemState.InProgress, 4, parent: parent),
            Item(PlanItemId.New(), "other ready", PlanItemState.Ready, 5));
        var reconciler = new ProgressReconciler();
        Assert.Equal(laterActive, reconciler.CurrentItem(plan));

        var noActive = PlanOf(Item(earlyReady, "ready", PlanItemState.Ready, 2),
            Item(PlanItemId.New(), "earlier ready", PlanItemState.Ready, 1));
        Assert.Equal(noActive.Items().OrderBy(item => item.Order).First().Id, reconciler.CurrentItem(noActive));

        var noAvailable = PlanOf(Item(PlanItemId.New(), "pending", PlanItemState.Pending, 1));
        Assert.Null(reconciler.CurrentItem(noAvailable));
    }

    [Fact]
    public void Watchdog_counts_turns_only_for_the_selected_lanes()
    {
        var fixture = WatchFixture.Create();
        fixture.AppendTurn(fixture.RootLane);
        fixture.AppendTurn(fixture.OtherLane);
        fixture.AppendTurn(fixture.RootLane);
        fixture.AppendTurn(fixture.OtherLane);
        fixture.AppendTurn(fixture.RootLane);
        Assert.Equal(3, fixture.TurnsWithoutProgress(new[] { fixture.RootLane }));
        Assert.Equal(2, fixture.TurnsWithoutProgress(new[] { fixture.OtherLane }));
    }

    [Fact]
    public void Watchdog_resets_on_task_lane_and_plan_transitions_and_on_effect_or_fresh_resource()
    {
        var transition = WatchFixture.Create();
        transition.AppendTurn(transition.RootLane);
        transition.AppendTurn(transition.RootLane);
        var newTask = transition.CreateTask(TaskState.Pending);
        transition.Stream.Append(new TaskReady(newTask));
        transition.AppendTurn(transition.RootLane);
        Assert.Equal(1, transition.TurnsWithoutProgress(new[] { transition.RootLane }));

        var laneTransition = WatchFixture.Create();
        laneTransition.AppendTurn(laneTransition.RootLane);
        laneTransition.AppendTurn(laneTransition.RootLane);
        laneTransition.Stream.Append(new LaneBlocked(laneTransition.RootLane, "waiting"));
        laneTransition.Stream.Append(new LaneUnblocked(laneTransition.RootLane));
        laneTransition.AppendTurn(laneTransition.RootLane);
        Assert.Equal(1, laneTransition.TurnsWithoutProgress(new[] { laneTransition.RootLane }));

        var planTransition = WatchFixture.Create();
        planTransition.AppendTurn(planTransition.RootLane);
        planTransition.AppendTurn(planTransition.RootLane);
        planTransition.Stream.Append(new PlanItemStarted(planTransition.RootItem));
        planTransition.AppendTurn(planTransition.RootLane);
        Assert.Equal(1, planTransition.TurnsWithoutProgress(new[] { planTransition.RootLane }));

        var effect = WatchFixture.Create();
        effect.AppendTurn(effect.RootLane);
        effect.AppendTurn(effect.RootLane);
        effect.AppendToolSuccess("write", "{\"path\":\"a\"}", EffectClass.Reconcilable);
        effect.AppendTurn(effect.RootLane);
        Assert.Equal(1, effect.TurnsWithoutProgress(new[] { effect.RootLane }));

        var resource = WatchFixture.Create();
        resource.AppendTurn(resource.RootLane);
        resource.AppendTurn(resource.RootLane);
        resource.AppendToolSuccess("read", "{\"path\":\"first\"}", EffectClass.None);
        resource.AppendTurn(resource.RootLane);
        Assert.Equal(1, resource.TurnsWithoutProgress(new[] { resource.RootLane }));
    }

    [Fact]
    public void Repeating_same_no_effect_tool_and_arguments_does_not_reset_counter()
    {
        var fixture = WatchFixture.Create();
        fixture.AppendTurn(fixture.RootLane);
        fixture.AppendTurn(fixture.RootLane);
        fixture.AppendToolSuccess("read", "{\"path\":\"same\"}", EffectClass.None);
        fixture.AppendTurn(fixture.RootLane);
        fixture.AppendTurn(fixture.RootLane);
        fixture.AppendToolSuccess("read", "{\"path\":\"same\"}", EffectClass.None);
        Assert.Equal(2, fixture.TurnsWithoutProgress(new[] { fixture.RootLane }));
    }

    [Fact]
    public void Watchdog_threshold_must_be_positive_and_stall_is_inclusive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgressWatchdog(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgressWatchdog(-1));
        var watchdog = new ProgressWatchdog(3);
        Assert.False(watchdog.IsStalled(2));
        Assert.True(watchdog.IsStalled(3));
        Assert.True(watchdog.IsStalled(4));
    }

    [Fact]
    public void Yaml_stall_threshold_changes_simulation_and_rejects_invalid_values()
    {
        const string prefix = "scenario: 1\nname: stall-threshold\nsession: { mode: act }\ninput: objective\n"
            + "plan:\n  - add: { id: P1, text: work, dependsOn: [] }\nturns:\n  root:\n"
            + "    - { tool: plan.propose, effect: read }\n"
            + "    - { complete: one }\n    - { complete: two }\n    - { complete: three }\n";
        var low = RunScenario(prefix + "stallThresholdTurns: 1\n");
        var defaultThreshold = RunScenario(prefix);

        Assert.Contains("progress.stalled", low.Events.Select(evt => evt.Type.ToString()));
        Assert.DoesNotContain("progress.stalled", defaultThreshold.Events.Select(evt => evt.Type.ToString()));

        Assert.Throws<ScenarioFormatException>(() => ScenarioLoader.Parse(prefix + "stallThresholdTurns: 0\n"));
        Assert.Throws<ScenarioFormatException>(() => ScenarioLoader.Parse(prefix + "stallThresholdTurns: nope\n"));
    }

    private static (SimulationEngine.RunResult Result, IReadOnlyList<DomainEvent> Events) RunScenario(string yaml)
    {
        var scenario = ScenarioLoader.Parse(yaml);
        var store = new InMemoryEventStore();
        var tools = OmniHost.CreateHostTools();
        var executor = ScriptedToolExecutor.WithCoreTools(tools.Catalog(),
            ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow));
        var engine = new SimulationEngine(store, EventCodecs.Create(), new InMemoryAuditSink(), executor);
        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);
        return (result, store.ReadFrom(result.SessionId, 1));
    }

    private static void AssertMutation(IReadOnlyList<PlanMutation> mutations, PlanMutationKind kind, PlanItemId item)
    {
        var mutation = Assert.Single(mutations);
        Assert.Equal(kind, mutation.Kind);
        Assert.Equal(item, mutation.ItemId);
        Assert.Equal(MutationCause.Reconciler, mutation.Cause);
    }

    private static PlanProjection PlanOf(params PlanItem[] items) =>
        PlanProjection.FromItems(PlanId.New(), RunId.New(), items);

    private static PlanItem Item(PlanItemId id, string description, PlanItemState state, int order,
        PlanItemId? parent = null, PlanItemId[]? dependsOn = null, PlanItemLink[]? links = null) =>
        new(id, description, state, order, parent, dependsOn ?? Array.Empty<PlanItemId>(),
            links ?? Array.Empty<PlanItemLink>(), true, null, new Dictionary<string, string>());

    private static TaskGraphProjection EmptyTasks() =>
        TaskGraphProjection.Replay(EventCodecs.Create(), Array.Empty<DomainEvent>());

    private sealed record TaskView(TaskId TaskId, TaskGraphProjection Tasks, LaneProjection Lanes);

    private static class TaskFixture
    {
        public static TaskView Create(TaskState state)
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session).RunId;
            var task = TaskId.New();
            stream.Append(new TaskCreated(task, run, "task", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null)));
            if (state != TaskState.Pending)
            {
                stream.Append(new TaskReady(task));
            }

            if (state is TaskState.Running or TaskState.Blocked or TaskState.Completed or TaskState.Failed)
            {
                var lane = LaneId.New();
                stream.Append(new LaneCreated(lane, task, ProfileId.New()));
                stream.Append(new LaneStarted(lane));
                stream.Append(new TaskStarted(task, lane));
                if (state == TaskState.Blocked) stream.Append(new TaskBlocked(task, "blocked"));
                if (state == TaskState.Completed) stream.Append(new TaskCompleted(task, null));
                if (state == TaskState.Failed) stream.Append(new TaskFailed(task, "failed"));
            }

            var events = store.ReadFrom(session, 1);
            return new TaskView(task, TaskGraphProjection.Replay(codecs, events), LaneProjection.Replay(codecs, events));
        }
    }

    private sealed class WatchFixture
    {
        private readonly InMemoryEventStore _store;
        private readonly EventCodecs _codecs;
        private readonly SessionId _session;
        private readonly RunId _run;
        private readonly PlanId _plan;
        public EventStream Stream { get; }
        public LaneId RootLane { get; }
        public LaneId OtherLane { get; }
        public PlanItemId RootItem { get; }
        public IReadOnlyList<DomainEvent> Events => _store.ReadFrom(_session, 1);

        private WatchFixture(InMemoryEventStore store, EventCodecs codecs, SessionId session, RunId run,
            PlanId plan, EventStream stream, LaneId rootLane, LaneId otherLane, PlanItemId rootItem)
        {
            _store = store;
            _codecs = codecs;
            _session = session;
            _run = run;
            _plan = plan;
            Stream = stream;
            RootLane = rootLane;
            OtherLane = otherLane;
            RootItem = rootItem;
        }

        public static WatchFixture Create()
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var opened = TestRun.Open(stream, session);
            var plan = PlanId.New();
            var item = PlanItemId.New();
            stream.Append(new PlanCreated(plan, opened.RunId, item, "objective"));

            var otherTask = TaskId.New();
            stream.Append(new TaskCreated(otherTask, opened.RunId, "other", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null)));
            stream.Append(new TaskReady(otherTask));
            var otherLane = LaneId.New();
            stream.Append(new LaneCreated(otherLane, otherTask, ProfileId.New()));
            stream.Append(new LaneStarted(otherLane));
            stream.Append(new TaskStarted(otherTask, otherLane));
            return new WatchFixture(store, codecs, session, opened.RunId, plan, stream, opened.RootLane, otherLane, item);
        }

        public void AppendTurn(LaneId lane)
        {
            var turn = TurnId.New();
            Stream.Append(new TurnStarted(turn, lane));
            Stream.Append(new TurnCompleted(turn));
        }

        public TaskId CreateTask(TaskState state)
        {
            var task = TaskId.New();
            Stream.Append(new TaskCreated(task, _run, "watchdog task", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null)));
            if (state != TaskState.Pending)
            {
                Stream.Append(new TaskReady(task));
            }

            if (state == TaskState.Running)
            {
                var lane = LaneId.New();
                Stream.Append(new LaneCreated(lane, task, ProfileId.New()));
                Stream.Append(new LaneStarted(lane));
                Stream.Append(new TaskStarted(task, lane));
            }

            return task;
        }

        public void AppendToolSuccess(string toolName, string args, EffectClass effect)
        {
            var call = ToolCallId.New();
            Stream.Append(new ToolCallRequested(call, "provider-call", toolName, args));
            Stream.Append(new ToolCallPrepared(call, "{}"));
            Stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "[]", null));
            Stream.Append(new ToolCallAuthorized(call));
            Stream.Append(new ToolCallStarted(call, effect, null),
                effect == EffectClass.None ? DurabilityClass.Standard : DurabilityClass.Barrier);
            Stream.Append(new ToolCallSucceeded(call, "{}"));
        }

        public int TurnsWithoutProgress(IReadOnlyCollection<LaneId> lanes) =>
            ProgressWatchdog.TurnsWithoutProgress(_codecs, Events, lanes);
    }
}
