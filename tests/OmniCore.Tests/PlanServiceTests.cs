using OmniCore.Engine;

namespace OmniCore.Tests;

public sealed class PlanServiceTests
{
    private static PlanProjection SingleItemPlan()
    {
        var items = new OmniCore.Domain.PlanItem[] {
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "P", OmniCore.Domain.PlanItemState.Pending,
                1, null, new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
        };
        return PlanProjection.FromItems(OmniCore.Domain.PlanId.New(), OmniCore.Domain.RunId.New(), items);
    }

    [Fact]
    public async Task Start_pending_item_emits_started()
    {
        var plan = SingleItemPlan();
        var item = plan.Items()[0];
        var service = new PlanService();
        var result = service.Apply(plan, TaskEmpty(), LaneProjection.Empty(),
            OmniCore.Domain.PlanMutation.Start(item.Id, OmniCore.Domain.MutationCause.Model));

        Assert.True(result.Accepted);
        Assert.Single(result.Events);
        Assert.True(result.Events[0] is OmniCore.Domain.PlanItemStarted);
    }

    [Fact]
    public async Task Complete_with_running_linked_task_is_rejected()
    {
        // Un item vinculado a una task Running no puede completarse (ADR-0016 §3).
        var taskId = OmniCore.Domain.TaskId.New();
        var planItems = new OmniCore.Domain.PlanItem[] {
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "P",
                OmniCore.Domain.PlanItemState.InProgress, 1, null, new OmniCore.Domain.PlanItemId[0],
                new OmniCore.Domain.PlanItemLink[] { new OmniCore.Domain.PlanItemLink(taskId, true,
                    OmniCore.Domain.LinkRole.Implements) }, true, null, new Dictionary<string, string>()),
        };
        var plan = PlanProjection.FromItems(OmniCore.Domain.PlanId.New(), OmniCore.Domain.RunId.New(), planItems);
        var tasks = TaskGraphWith(taskId, OmniCore.Domain.TaskState.Running);
        var service = new PlanService();

        var result = service.Apply(plan, tasks, LaneProjection.Empty(),
            OmniCore.Domain.PlanMutation.Complete(planItems[0].Id, OmniCore.Domain.MutationCause.Model, "hecho"));

        Assert.False(result.Accepted);
        Assert.Contains("aún no está Completed", result.Reason!);
    }

    [Fact]
    public async Task Skip_required_item_requires_approval()
    {
        var plan = SingleItemPlan();
        var item = plan.Items()[0];
        var service = new PlanService();
        var result = service.Apply(plan, TaskEmpty(), LaneProjection.Empty(),
            OmniCore.Domain.PlanMutation.Skip(item.Id, OmniCore.Domain.MutationCause.Model, "no aplica"));

        Assert.False(result.Accepted, "Skip de item requerido exige Ask (ADR-0016 §6)");
    }

    [Fact]
    public async Task Add_structural_mutation_is_accepted()
    {
        var plan = SingleItemPlan();
        var service = new PlanService();
        var mutation = OmniCore.Domain.PlanMutation.Add(2, "Nuevo paso", null, new OmniCore.Domain.PlanItemId[0],
            OmniCore.Domain.MutationCause.User);

        var result = service.Apply(plan, TaskEmpty(), LaneProjection.Empty(), mutation);

        Assert.True(result.Accepted);
        Assert.True(result.Events[0] is OmniCore.Domain.PlanItemAdded);
        Assert.Equal(1, result.RevisionDelta);
    }

    [Fact]
    public async Task Unknown_item_is_rejected()
    {
        var plan = SingleItemPlan();
        var service = new PlanService();
        var result = service.Apply(plan, TaskEmpty(), LaneProjection.Empty(),
            OmniCore.Domain.PlanMutation.Block(OmniCore.Domain.PlanItemId.New(),
                OmniCore.Domain.MutationCause.Model, "x"));

        Assert.False(result.Accepted);
        Assert.Contains("no existe", result.Reason!);
    }

    private static TaskGraphProjection TaskEmpty() =>
        TaskGraphProjection.Replay(Registry(), new OmniCore.Domain.DomainEvent[0]);

    private static TaskGraphProjection TaskGraphWith(OmniCore.Domain.TaskId taskId,
        OmniCore.Domain.TaskState state)
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
        else if (state == OmniCore.Domain.TaskState.Completed)
        {
            stream.Append(new OmniCore.Domain.TaskStarted(taskId, OmniCore.Domain.LaneId.New()));
            stream.Append(new OmniCore.Domain.TaskCompleted(taskId, null));
        }

        return TaskGraphProjection.Replay(registry, store.ReadFrom(sessionId, 1));
    }

    private static OmniCore.Infrastructure.EventCodecs Registry() => OmniCore.Infrastructure.EventCodecs.Create();
}