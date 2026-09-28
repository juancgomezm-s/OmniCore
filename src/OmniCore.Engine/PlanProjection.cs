namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Proyección del Plan canónico con sus revisiones (ADR-0016 §2, §6).</summary>
public sealed class PlanProjection
{
    private readonly Dictionary<PlanId, Plan> _revisions = new();

    public PlanId? Id => IdValue;

    public RunId? RunId => RunIdValue;

    private PlanProjection(PlanId? id, RunId? runId)
    {
        IdValue = id;
        RunIdValue = runId;
    }

    public static PlanProjection Empty() => new(null, null);

    /// <summary>Construye una proyección desde items ya conocidos (para el reconciliar en M1).</summary>
    public static PlanProjection FromItems(PlanId? id, RunId? runId, IReadOnlyList<PlanItem> items)
    {
        var projection = new PlanProjection(id, runId);
        if (id is not null && runId is not null)
        {
            projection._revisions[id] = new Plan(id, runId!, 1, items);
        }

        return projection;
    }

    public static PlanProjection Replay(IEventCodecRegistry registry, IReadOnlyList<DomainEvent> evts)
    {
        var projection = new PlanProjection(null, null);
        foreach (var evt in evts)
        {
            projection.Apply(registry.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson));
        }

        return projection;
    }

    public Plan? Latest()
    {
        Plan? best = null;
        foreach (var plan in _revisions.Values)
        {
            if (best is null || plan.Revision > best.Revision)
            {
                best = plan;
            }
        }

        return best;
    }

    public int Revision() => Latest() is null ? 0 : Latest()!.Revision;

    public IReadOnlyList<PlanItem> Items() => Latest() is null ? new PlanItem[0] : Latest()!.Items;

    public PlanItem? Item(PlanItemId id)
    {
        foreach (var item in Items())
        {
            if (item.Id.Equals(id))
            {
                return item;
            }
        }

        return null;
    }

    public Dictionary<PlanItemId, PlanItemState> ItemsByState()
    {
        var map = new Dictionary<PlanItemId, PlanItemState>();
        foreach (var item in Items())
        {
            map[item.Id] = item.State;
        }

        return map;
    }

    private void Apply(DomainEventPayload payload)
    {
        if (payload is PlanCreated created)
        {
            var root = new PlanItem(created.RootItemId, created.Objective, PlanItemState.Pending, 1, null,
                new PlanItemId[0], new PlanItemLink[0], true, null, new Dictionary<string, string>());
            var plan = new Plan(created.PlanId, created.RunId, 1, [root]);
            _revisions[plan.Id] = plan;
            IdValue = created.PlanId;
            RunIdValue = created.RunId;
        }
        else if (payload is PlanItemAdded added)
        {
            var plan = Latest();
            if (plan is null)
            {
                return;
            }

            var newItem = new PlanItem(added.PlanItemId, added.Description, PlanItemState.Pending, added.Order,
                added.ParentId, added.DependsOn, new PlanItemLink[0], added.Required, null, added.Metadata);
            var items = new PlanItem[plan.Items.Count + 1];
            for (var i = 0; i < plan.Items.Count; i++)
            {
                items[i] = plan.Items[i];
            }

            items[plan.Items.Count] = newItem;
            _revisions[plan.Id] = new Plan(plan.Id, plan.RunId, plan.Revision, items);
        }
        else if (payload is PlanItemLinked linked)
        {
            ApplyLink(linked.PlanItemId, linked.Link);
        }
        else if (payload is PlanItemUnlinked unlinked)
        {
            ApplyUnlink(unlinked.PlanItemId, unlinked.TaskId);
        }
        else if (payload is PlanRevised revised)
        {
            var plan = Latest();
            if (plan is not null)
            {
                _revisions[plan.Id] = new Plan(plan.Id, plan.RunId, revised.Revision, plan.Items);
            }
        }
        else if (payload is PlanItemStarted s)
        {
            ReplaceState(s.PlanItemId, PlanItemState.InProgress);
        }
        else if (payload is PlanItemReady r)
        {
            ReplaceState(r.PlanItemId, PlanItemState.Ready);
        }
        else if (payload is PlanItemBlocked b)
        {
            ReplaceState(b.PlanItemId, PlanItemState.Blocked);
        }
        else if (payload is PlanItemUnblocked u)
        {
            ReplaceState(u.PlanItemId, PlanItemState.InProgress);
        }
        else if (payload is PlanItemReopened ro)
        {
            ReplaceState(ro.PlanItemId, PlanItemState.Ready);
        }
        else if (payload is PlanItemCompleted c)
        {
            ReplaceState(c.PlanItemId, PlanItemState.Completed);
        }
        else if (payload is PlanItemFailed f)
        {
            ReplaceState(f.PlanItemId, PlanItemState.Failed);
        }
        else if (payload is PlanItemSkipped k)
        {
            ReplaceState(k.PlanItemId, PlanItemState.Skipped);
        }
        else if (payload is PlanItemCancelled x)
        {
            ReplaceState(x.PlanItemId, PlanItemState.Cancelled);
        }
        else if (payload is PlanItemReordered o)
        {
            Reorder(o.NewOrder);
        }
    }

    private void ApplyLink(PlanItemId itemId, PlanItemLink link)
    {
        var item = Item(itemId);
        var plan = Latest();
        if (item is null || plan is null)
        {
            return;
        }

        var links = new PlanItemLink[item.LinkedTasks.Count + 1];
        for (var i = 0; i < item.LinkedTasks.Count; i++)
        {
            links[i] = item.LinkedTasks[i];
        }

        links[item.LinkedTasks.Count] = link;
        ReplaceItem(new PlanItem(item.Id, item.Description, item.State, item.Order, item.ParentId, item.DependsOn,
            links, item.Required, item.Outcome, item.Metadata));
    }

    private void ApplyUnlink(PlanItemId itemId, TaskId taskId)
    {
        var item = Item(itemId);
        var plan = Latest();
        if (item is null || plan is null)
        {
            return;
        }

        var links = new List<PlanItemLink>();
        foreach (var l in item.LinkedTasks)
        {
            if (!l.TaskId.Equals(taskId))
            {
                links.Add(l);
            }
        }

        ReplaceItem(new PlanItem(item.Id, item.Description, item.State, item.Order, item.ParentId, item.DependsOn,
            links.ToArray(), item.Required, item.Outcome, item.Metadata));
    }

    private void Reorder(IReadOnlyList<PlanItemId> newOrder)
    {
        var plan = Latest();
        if (plan is null)
        {
            return;
        }

        var nextOrder = 1;
        var reordered = new PlanItem[plan.Items.Count];
        for (var i = 0; i < newOrder.Count; i++)
        {
            var item = Item(newOrder[i]);
            if (item is not null)
            {
                reordered[i] = new PlanItem(item.Id, item.Description, item.State, nextOrder++, item.ParentId,
                    item.DependsOn, item.LinkedTasks, item.Required, item.Outcome, item.Metadata);
            }
        }

        _revisions[plan.Id] = new Plan(plan.Id, plan.RunId, plan.Revision, reordered);
    }

    private void ReplaceState(PlanItemId itemId, PlanItemState newState)
    {
        var item = Item(itemId);
        if (item is null)
        {
            return;
        }

        ReplaceItem(new PlanItem(item.Id, item.Description, newState, item.Order, item.ParentId, item.DependsOn,
            item.LinkedTasks, item.Required, item.Outcome, item.Metadata));
    }

    private void ReplaceItem(PlanItem newItem)
    {
        var plan = Latest();
        if (plan is null)
        {
            return;
        }

        var items = new PlanItem[plan.Items.Count];
        for (var i = 0; i < plan.Items.Count; i++)
        {
            items[i] = plan.Items[i]!.Id.Equals(newItem.Id) ? newItem : plan.Items[i];
        }

        _revisions[plan.Id] = new Plan(plan.Id, plan.RunId, plan.Revision, items);
    }

    private PlanId? IdValue;

    private RunId? RunIdValue;
}
