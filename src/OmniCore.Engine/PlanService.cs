namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Resultado de aplicar una mutación del Plan (ADR-0016 §3): aceptada con los eventos a emitir
/// (en orden), o rechazada con la razón exacta (que también es canónica).
/// </summary>
public sealed class PlanServiceResult
{
    public bool Accepted { get; }

    public string? Reason { get; }

    public IReadOnlyList<DomainEventPayload> Events { get; }

    public int RevisionDelta { get; }

    private PlanServiceResult(bool accepted, string? reason, IReadOnlyList<DomainEventPayload> events,
        int revisionDelta)
    {
        Accepted = accepted;
        Reason = reason;
        Events = events;
        RevisionDelta = revisionDelta;
    }

    public static PlanServiceResult AcceptedOne(DomainEventPayload evt) =>
        new(true, null, new DomainEventPayload[] { evt }, 0);

    public static PlanServiceResult AcceptedMany(IReadOnlyList<DomainEventPayload> events) =>
        new(true, null, events, 0);

    public static PlanServiceResult AcceptedStructural(IReadOnlyList<DomainEventPayload> events) =>
        new(true, null, events, 1);

    public static PlanServiceResult Rejected(string reason) => new(false, reason, new DomainEventPayload[0], 0);
}

/// <summary>
/// PlanService: valida las mutaciones propuestas contra la máquina de estados, verifica la
/// coherencia con el TaskGraph y aplica la política de impacto (ADR-0016 §3, §6). Puro:
/// devuelve los eventos canónicos; el Engine los persiste.
/// </summary>
public sealed class PlanService
{
    /// <summary>Procesa una mutación y devuelve el resultado + los eventos a emitir (en orden).</summary>
    public PlanServiceResult Apply(PlanProjection plan, TaskGraphProjection tasks, LaneProjection lanes,
        PlanMutation mutation)
    {
        if (mutation.Kind == PlanMutationKind.Add)
        {
            return ApplyAdd(plan, mutation);
        }

        if (mutation.Kind == PlanMutationKind.Reorder)
        {
            return Plans.Reordered(mutation.Target.ReorderList);
        }

        if (mutation.ItemId is null)
        {
            return PlanServiceResult.Rejected("La mutación requiere un item: " + mutation.Kind);
        }

        var item = plan.Item(mutation.ItemId);
        if (item is null)
        {
            return PlanServiceResult.Rejected("El item " + mutation.ItemId + " no existe en el Plan");
        }

        switch (mutation.Kind)
        {
            case PlanMutationKind.Start:
                if (item.State == PlanItemState.Pending || item.State == PlanItemState.Ready)
                {
                    return PlanServiceResult.AcceptedOne(new PlanItemStarted(mutation.ItemId));
                }

                return PlanServiceResult.Rejected("No se puede iniciar un item en " + item.State);
            case PlanMutationKind.Complete:
                return ApplyComplete(item, tasks);
            case PlanMutationKind.Block:
                if (item.State == PlanItemState.InProgress)
                {
                    return PlanServiceResult.AcceptedOne(
                        new PlanItemBlocked(mutation.ItemId, mutation.Reason ?? "bloqueado"));
                }

                return PlanServiceResult.Rejected("No se puede bloquear un item en " + item.State);
            case PlanMutationKind.Unblock:
                if (item.State == PlanItemState.Blocked)
                {
                    return PlanServiceResult.AcceptedOne(new PlanItemUnblocked(mutation.ItemId));
                }

                return PlanServiceResult.Rejected("No se puede desbloquear un item en " + item.State);
            case PlanMutationKind.Fail:
                if (item.State == PlanItemState.InProgress || item.State == PlanItemState.Blocked)
                {
                    return PlanServiceResult.AcceptedOne(
                        new PlanItemFailed(mutation.ItemId, mutation.Reason ?? "fallo declarado"));
                }

                return PlanServiceResult.Rejected("No se puede marcar como fallido un item en " + item.State);
            case PlanMutationKind.Skip:
                return PlanServiceResult.Rejected("Saltar un item requiere aprobación (Ask); sin cliente → Deny");
            case PlanMutationKind.Cancel:
                return PlanServiceResult.Rejected("Cancelar un item requiere aprobación (Ask); sin cliente → Deny");
            case PlanMutationKind.Revise:
                if (item.State == PlanItemState.Failed)
                {
                    return PlanServiceResult.AcceptedStructural(
                        [new PlanItemReopened(mutation.ItemId, mutation.Reason ?? item.Description)]);
                }

                return PlanServiceResult.Rejected("Revise solo aplica a items Failed");
            case PlanMutationKind.Update:
                return PlanServiceResult.AcceptedOne(new PlanItemUpdated(mutation.ItemId, mutation.Reason, null));
            default:
                return PlanServiceResult.Rejected("Mutación no soportada en M1: " + mutation.Kind);
        }
    }

    private PlanServiceResult ApplyComplete(PlanItem item, TaskGraphProjection tasks)
    {
        var pending = new List<DomainEventPayload>();
        foreach (var link in item.LinkedTasks)
        {
            if (!link.Required)
            {
                continue;
            }

            var taskState = tasks.StateOf(link.TaskId);
            if (taskState is null || taskState != TaskState.Completed)
            {
                return PlanServiceResult.Rejected(
                    "La Task " + link.TaskId + " vinculada (requerida) aún no está Completed");
            }
        }

        return PlanServiceResult.AcceptedOne(new PlanItemCompleted(item.Id, null));
    }

    private PlanServiceResult ApplyAdd(PlanProjection plan, PlanMutation mutation)
    {
        var target = mutation.Target;
        return PlanServiceResult.AcceptedStructural(new DomainEventPayload[] { new PlanItemAdded(
            PlanItemId.New(),
            plan.Id!,
            target.AddText,
            target.AddOrder,
            target.AddParent,
            target.AddDependsOn,
            true,
            new Dictionary<string, string>()) });
    }
}

/// <summary>Fábrica de eventos de Plan con tipo derivado por el payload concreto.</summary>
public sealed class Plans
{
    public static PlanServiceResult Reordered(IReadOnlyList<PlanItemId> newOrder) =>
        PlanServiceResult.AcceptedStructural(new DomainEventPayload[] { new PlanItemReordered(newOrder) });
}