using OmniCore.Engine;

namespace OmniCore.Tests;

public sealed class ReconcilerTests
{
    /// <summary>Plan con un item vinculado a una task (Pending), sin dependencias.</summary>
    private static PlanProjection PlanLinkedTo(OmniCore.Domain.TaskId taskId, OmniCore.Domain.PlanItemState state)
    {
        var link = new OmniCore.Domain.PlanItemLink(taskId, true, OmniCore.Domain.LinkRole.Implements);
        var items = new OmniCore.Domain.PlanItem[] {
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "P", state, 1, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[] { link }, true, null,
                new Dictionary<string, string>()),
        };
        return PlanProjection.FromItems(OmniCore.Domain.PlanId.New(), OmniCore.Domain.RunId.New(), items);
    }

    /// <summary>Plan con dos items en orden, sin links a tasks.</summary>
    private static PlanProjection PlanTwoItems(OmniCore.Domain.PlanItemState first,
        OmniCore.Domain.PlanItemState second)
    {
        var items = new OmniCore.Domain.PlanItem[] {
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "A", first, 1, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "B", second, 2, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
        };
        return PlanProjection.FromItems(OmniCore.Domain.PlanId.New(), OmniCore.Domain.RunId.New(), items);
    }

    private static TaskGraphProjection EmptyTasks() =>
        TaskGraphProjection.Replay(Registry(), new OmniCore.Domain.DomainEvent[0]);

    private static OmniCore.Infrastructure.EventCodecs Registry() => OmniCore.Infrastructure.EventCodecs.Create();

    [Fact]
    public async Task R1_linked_task_running_starts_item()
    {
        var taskId = OmniCore.Domain.TaskId.New();
        var plan = PlanLinkedTo(taskId, OmniCore.Domain.PlanItemState.Pending);
        var tasks = WithTask(taskId, OmniCore.Domain.TaskState.Running);
        var reconciler = new ProgressReconciler();

        var mutations = reconciler.Reconcile(plan, tasks, LaneProjection.Empty());

        Assert.Single(mutations);
        Assert.Equal(OmniCore.Domain.PlanMutationKind.Start, mutations[0].Kind);
        Assert.Equal(OmniCore.Domain.MutationCause.Reconciler, mutations[0].Cause);
    }

    [Fact]
    public async Task R6_dependencies_satisfied_ready()
    {
        var aId = OmniCore.Domain.PlanItemId.New();
        var bId = OmniCore.Domain.PlanItemId.New();
        var items = new OmniCore.Domain.PlanItem[] {
            new OmniCore.Domain.PlanItem(aId, "A", OmniCore.Domain.PlanItemState.Completed, 1, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
            new OmniCore.Domain.PlanItem(bId, "B", OmniCore.Domain.PlanItemState.Pending, 2, null,
                new OmniCore.Domain.PlanItemId[] { aId }, new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
        };
        var plan = PlanProjection.FromItems(OmniCore.Domain.PlanId.New(), OmniCore.Domain.RunId.New(), items);
        var reconciler = new ProgressReconciler();

        var mutations = reconciler.Reconcile(plan, EmptyTasks(), LaneProjection.Empty());

        var kinds = new List<string>();
        foreach (var m in mutations)
        {
            kinds.Add(m.Kind.ToString() + ":" + (m.ItemId is null ? "?" : m.ItemId.ToString()));
        }

        Console.WriteLine("mutations=[" + string.Join(",", kinds) + "]");
        Assert.True(mutations.Count > 0);
        Assert.Equal(OmniCore.Domain.PlanMutationKind.Start, mutations[0].Kind);
        Assert.True(mutations[0].ItemId!.Equals(bId));
    }

    [Fact]
    public async Task R3_blocked_task_blocks_item()
    {
        var taskId = OmniCore.Domain.TaskId.New();
        var plan = PlanLinkedTo(taskId, OmniCore.Domain.PlanItemState.InProgress);
        var tasks = WithTask(taskId, OmniCore.Domain.TaskState.Blocked);
        var reconciler = new ProgressReconciler();

        var mutations = reconciler.Reconcile(plan, tasks, LaneProjection.Empty());

        Assert.Single(mutations);
        Assert.Equal(OmniCore.Domain.PlanMutationKind.Block, mutations[0].Kind);
    }

    [Fact]
    public async Task R7_current_item_checks_in_progress_then_ready()
    {
        var pending = PlanTwoItems(OmniCore.Domain.PlanItemState.Ready, OmniCore.Domain.PlanItemState.Pending);
        var reconciler = new ProgressReconciler();

        Assert.NotNull(reconciler.CurrentItem(pending));

        var inProgress = PlanTwoItems(OmniCore.Domain.PlanItemState.Completed,
            OmniCore.Domain.PlanItemState.InProgress);
        Assert.NotNull(reconciler.CurrentItem(inProgress));
    }

    [Fact]
    public async Task Terminal_items_produce_no_mutation()
    {
        var plan = PlanTwoItems(OmniCore.Domain.PlanItemState.Completed, OmniCore.Domain.PlanItemState.Completed);
        var reconciler = new ProgressReconciler();
        var mutations = reconciler.Reconcile(plan, EmptyTasks(), LaneProjection.Empty());
        Assert.Empty(mutations);
    }

    private static TaskGraphProjection WithTask(OmniCore.Domain.TaskId taskId, OmniCore.Domain.TaskState state)
    {
        var store = new OmniCore.Infrastructure.InMemoryEventStore();
        var registry = Registry();
        var sessionId = OmniCore.Domain.SessionId.New();
        var stream = new EventStream(store, registry, sessionId);
        var runId = OmniCore.Domain.RunId.New();
        stream.Append(new OmniCore.Domain.TaskCreated(taskId, runId, "T",
            new OmniCore.Domain.TaskDependency[0], new OmniCore.Domain.TaskBudget(null, null, null, null)));
        stream.Append(new OmniCore.Domain.TaskReady(taskId));
        if (state == OmniCore.Domain.TaskState.Running)
        {
            stream.Append(new OmniCore.Domain.TaskStarted(taskId, OmniCore.Domain.LaneId.New()));
        }
        else if (state == OmniCore.Domain.TaskState.Blocked)
        {
            stream.Append(new OmniCore.Domain.TaskStarted(taskId, OmniCore.Domain.LaneId.New()));
            stream.Append(new OmniCore.Domain.TaskBlocked(taskId, "block"));
        }

        return TaskGraphProjection.Replay(registry, store.ReadFrom(sessionId, 1));
    }
}