namespace OmniCore.Domain;

using System.Text.Json.Serialization;

/// <summary>PlanCreated: Plan rev.1 con un item único = objetivo del Run (ADR-0016 §11).</summary>
public record PlanCreated(PlanId PlanId, RunId RunId, PlanItemId RootItemId, string Objective)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan.created");

    public int SchemaVersion() => 1;
}

/// <summary>PlanRevised: plan rev.N+1 con mutaciones (ADR-0016 §6).</summary>
public record PlanRevised(PlanId PlanId, RunId RunId, int Revision, string MutationsJson, MutationImpact Impact)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan.revised");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemAdded: un item entra en Pending (ADR-0036 §4).</summary>
public record PlanItemAdded(PlanItemId PlanItemId, PlanId PlanId, string Description, int Order,
    PlanItemId? ParentId, IReadOnlyList<PlanItemId> DependsOn, bool Required, Dictionary<string, string> Metadata)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.added");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemUpdated: solo texto o metadata, sin cambio de estado (ADR-0036 §4).</summary>
public record PlanItemUpdated(PlanItemId PlanItemId, string? Description, Dictionary<string, string>? Metadata)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.updated");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemStarted: → InProgress (R1 o mutación Start).</summary>
[JsonSerializable(typeof(PlanItemStarted))]
public record PlanItemStarted(PlanItemId PlanItemId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.started");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemReady: → Ready (R6).</summary>
[JsonSerializable(typeof(PlanItemReady))]
public record PlanItemReady(PlanItemId PlanItemId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.ready");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemBlocked: → Blocked (R3 o mutación Block).</summary>
[JsonSerializable(typeof(PlanItemBlocked))]
public record PlanItemBlocked(PlanItemId PlanItemId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.blocked");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemUnblocked: → InProgress (R4 o Unblock).</summary>
[JsonSerializable(typeof(PlanItemUnblocked))]
public record PlanItemUnblocked(PlanItemId PlanItemId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.unblocked");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemCompleted: → Completed (R2 o Complete validado).</summary>
[JsonSerializable(typeof(PlanItemCompleted))]
public record PlanItemCompleted(PlanItemId PlanItemId, string? Summary) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.completed");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemFailed: → Failed (R5 o Fail).</summary>
[JsonSerializable(typeof(PlanItemFailed))]
public record PlanItemFailed(PlanItemId PlanItemId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.failed");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemSkipped: → Skipped (Skip; requerido → Ask).</summary>
[JsonSerializable(typeof(PlanItemSkipped))]
public record PlanItemSkipped(PlanItemId PlanItemId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.skipped");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemCancelled: → Cancelled (Cancel; requerido → Ask).</summary>
[JsonSerializable(typeof(PlanItemCancelled))]
public record PlanItemCancelled(PlanItemId PlanItemId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.cancelled");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemReopened: Failed → Ready (Revise).</summary>
[JsonSerializable(typeof(PlanItemReopened))]
public record PlanItemReopened(PlanItemId PlanItemId, string NewDescription) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.reopened");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemReordered: nuevo orden de siblings (ADR-0016).</summary>
[JsonSerializable(typeof(PlanItemReordered))]
public record PlanItemReordered(IReadOnlyList<PlanItemId> NewOrder) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.reordered");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemLinked: vínculo PlanItem ↔ Task.</summary>
[JsonSerializable(typeof(PlanItemLinked))]
public record PlanItemLinked(PlanItemId PlanItemId, PlanItemLink Link) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.linked");

    public int SchemaVersion() => 1;
}

/// <summary>PlanItemUnlinked: quita un vínculo.</summary>
[JsonSerializable(typeof(PlanItemUnlinked))]
public record PlanItemUnlinked(PlanItemId PlanItemId, TaskId TaskId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_item.unlinked");

    public int SchemaVersion() => 1;
}

/// <summary>PlanMutationRejected: PlanService rechazó la mutación proposada (también canónico).</summary>
public record PlanMutationRejected(PlanItemId? PlanItemId, PlanMutationKind Kind, string Reason)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("plan_mutation.rejected");

    public int SchemaVersion() => 1;
}