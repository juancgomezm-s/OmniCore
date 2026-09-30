namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Resultado de aplicar una mutación del Plan (ADR-0016 §3): aceptada con los eventos a emitir
/// (en orden), rechazada con la razón exacta (que también es canónica), o pendiente de la
/// aprobación del usuario por su impacto (§6).
/// </summary>
public sealed class PlanServiceResult
{
    public bool Accepted { get; }

    public string? Reason { get; }

    public IReadOnlyList<DomainEventPayload> Events { get; }

    public int RevisionDelta { get; }

    /// <summary>Impacto calculado de la mutación (ADR-0016 §6).</summary>
    public MutationImpact Impact { get; }

    /// <summary>La mutación es válida pero su impacto exige <c>Ask</c>: sin aprobación no se aplica.</summary>
    public bool NeedsApproval { get; }

    private PlanServiceResult(bool accepted, string? reason, IReadOnlyList<DomainEventPayload> events,
        int revisionDelta, MutationImpact impact, bool needsApproval)
    {
        Accepted = accepted;
        Reason = reason;
        Events = events;
        RevisionDelta = revisionDelta;
        Impact = impact;
        NeedsApproval = needsApproval;
    }

    public static PlanServiceResult AcceptedOne(DomainEventPayload evt) =>
        new(true, null, new[] { evt }, 0, MutationImpact.Minor, false);

    public static PlanServiceResult AcceptedMany(IReadOnlyList<DomainEventPayload> events) =>
        new(true, null, events, 0, MutationImpact.Minor, false);

    /// <summary>Mutación estructural aceptada: los eventos empiezan por el <c>PlanRevised</c>.</summary>
    public static PlanServiceResult AcceptedStructural(IReadOnlyList<DomainEventPayload> events, MutationImpact impact) =>
        new(true, null, events, 1, impact, false);

    public static PlanServiceResult Rejected(string reason) =>
        new(false, reason, Array.Empty<DomainEventPayload>(), 0, MutationImpact.Minor, false);

    public static PlanServiceResult RequiresApproval(MutationImpact impact, string reason) =>
        new(false, reason, Array.Empty<DomainEventPayload>(), 0, impact, true);
}

/// <summary>
/// PlanService (ADR-0016 §3, §6; ADR-0036 §4): valida cada mutación contra la máquina de estados y
/// el TaskGraph, calcula su impacto y devuelve los eventos canónicos (el Engine los persiste).
/// <list type="bullet">
/// <item>Estructurales (<c>Add</c>, <c>Split</c>, <c>Reorder</c>, <c>Revise</c>, <c>Skip</c> de un
/// requerido) crean <c>Plan rev.N+1</c> con <c>PlanRevised { revision, mutations, impact }</c>.</item>
/// <item><c>ScopeExpansion</c> (cambiar el objetivo del item raíz), <c>RequiredSkip</c> y
/// <c>RequiredCancel</c> exigen aprobación (<c>Ask</c>) y razón; sin aprobación no se aplican
/// (ADR-0003: sin cliente → Deny, se mantiene la revisión anterior).</item>
/// </list>
/// Puro: no escribe nada.
/// </summary>
public sealed class PlanService
{
    public PlanServiceResult Apply(PlanProjection plan, TaskGraphProjection tasks, LaneProjection lanes,
        PlanMutation mutation) => Apply(plan, tasks, lanes, mutation, approved: false);

    /// <summary>
    /// Igual, indicando si el usuario ya aprobó la mutación (respuesta a su InteractionRequest).
    /// Solo levanta el <c>Ask</c> del impacto; nunca una transición inválida.
    /// </summary>
    public PlanServiceResult Apply(PlanProjection plan, TaskGraphProjection tasks, LaneProjection lanes,
        PlanMutation mutation, bool approved)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (plan.Id is null)
        {
            return PlanServiceResult.Rejected("El Run no tiene Plan");
        }

        switch (mutation.Kind)
        {
            case PlanMutationKind.Add:
                return ApplyAdd(plan, mutation);
            case PlanMutationKind.Reorder:
                return ApplyReorder(plan, mutation);
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

        var isRoot = IsRoot(plan, item);
        switch (mutation.Kind)
        {
            case PlanMutationKind.Ready:
                return Transition(plan, item, new PlanItemReady(item.Id), "No se puede marcar listo un item en ");
            case PlanMutationKind.Start:
                return Transition(plan, item, new PlanItemStarted(item.Id), "No se puede iniciar un item en ");
            case PlanMutationKind.Complete:
                return ApplyComplete(plan, item, tasks, mutation);
            case PlanMutationKind.Block:
                return Transition(plan, item, new PlanItemBlocked(item.Id, mutation.Reason ?? "bloqueado"),
                    "No se puede bloquear un item en ");
            case PlanMutationKind.Unblock:
                return Transition(plan, item, new PlanItemUnblocked(item.Id), "No se puede desbloquear un item en ");
            case PlanMutationKind.Fail:
                return Transition(plan, item, new PlanItemFailed(item.Id, mutation.Reason ?? "fallo declarado"),
                    "No se puede marcar como fallido un item en ");
            case PlanMutationKind.Skip:
                return ApplySkip(plan, item, mutation, approved);
            case PlanMutationKind.Cancel:
                return ApplyCancel(plan, item, mutation, approved);
            case PlanMutationKind.Revise:
                return ApplyRevise(plan, item, mutation, isRoot, approved);
            case PlanMutationKind.Update:
                return ApplyUpdate(item, mutation, isRoot, approved);
            case PlanMutationKind.Split:
                return ApplySplit(plan, item, mutation);
            case PlanMutationKind.Link:
                return ApplyLink(item, tasks, mutation);
            case PlanMutationKind.Unlink:
                return ApplyUnlink(item, mutation);
            default:
                return PlanServiceResult.Rejected("Mutación no soportada: " + mutation.Kind);
        }
    }

    // ── Transiciones de estado ─────────────────────────────────────────────────────────────

    /// <summary>La máquina de estados de ADR-0036 decide si la transición existe.</summary>
    private static PlanServiceResult Transition(PlanProjection plan, PlanItem item, DomainEventPayload evt,
        string rejection)
    {
        if (IsContainer(plan, item))
        {
            return PlanServiceResult.Rejected("El item " + item.Id + " es un contenedor: su estado sale de sus hijos");
        }

        try
        {
            StateMachines.ApplyPlanItem(item.State, evt);
            return PlanServiceResult.AcceptedOne(evt);
        }
        catch (InvalidStateTransitionException)
        {
            return PlanServiceResult.Rejected(rejection + item.State);
        }
    }

    private static PlanServiceResult ApplyComplete(PlanProjection plan, PlanItem item, TaskGraphProjection tasks,
        PlanMutation mutation)
    {
        foreach (var link in item.LinkedTasks)
        {
            if (link.Required && tasks.StateOf(link.TaskId) != TaskState.Completed)
            {
                return PlanServiceResult.Rejected(
                    "La Task " + link.TaskId + " vinculada (requerida) aún no está Completed");
            }
        }

        return Transition(plan, item, new PlanItemCompleted(item.Id, mutation.Reason), "No se puede completar un item en ");
    }

    private static PlanServiceResult ApplySkip(PlanProjection plan, PlanItem item, PlanMutation mutation, bool approved)
    {
        var evt = new PlanItemSkipped(item.Id, mutation.Reason ?? "");
        var valid = Transition(plan, item, evt, "No se puede saltar un item en ");
        if (!valid.Accepted)
        {
            return valid;
        }

        if (!item.Required)
        {
            return PlanServiceResult.AcceptedOne(evt); // Moderate: automático y visible
        }

        var gate = RequireApproval(MutationImpact.RequiredSkip, mutation, approved, "Saltar un item requerido");
        return gate ?? Structural(plan, mutation, MutationImpact.RequiredSkip, evt);
    }

    private static PlanServiceResult ApplyCancel(PlanProjection plan, PlanItem item, PlanMutation mutation,
        bool approved)
    {
        var evt = new PlanItemCancelled(item.Id, mutation.Reason ?? "");
        var valid = Transition(plan, item, evt, "No se puede cancelar un item en ");
        if (!valid.Accepted || !item.Required)
        {
            return valid;
        }

        return RequireApproval(MutationImpact.RequiredCancel, mutation, approved, "Cancelar un item requerido")
            ?? valid;
    }

    // ── Estructurales ──────────────────────────────────────────────────────────────────────

    private static PlanServiceResult ApplyAdd(PlanProjection plan, PlanMutation mutation)
    {
        var target = mutation.Target;
        if (string.IsNullOrWhiteSpace(target.AddText))
        {
            return PlanServiceResult.Rejected("Un item nuevo necesita descripción");
        }

        if (target.AddParent is not null)
        {
            var parent = plan.Item(target.AddParent);
            if (parent is null || StateMachines.IsPlanItemTerminal(parent.State))
            {
                return PlanServiceResult.Rejected("El padre " + target.AddParent + " no existe o ya terminó");
            }
        }

        foreach (var dependency in target.AddDependsOn)
        {
            if (plan.Item(dependency) is null)
            {
                return PlanServiceResult.Rejected("La dependencia " + dependency + " no existe en el Plan");
            }
        }

        return Structural(plan, mutation, MutationImpact.Minor, new PlanItemAdded(PlanItemId.New(), plan.Id!,
            target.AddText, target.AddOrder, target.AddParent, target.AddDependsOn, true,
            new Dictionary<string, string>()));
    }

    private static PlanServiceResult ApplySplit(PlanProjection plan, PlanItem item, PlanMutation mutation)
    {
        var parts = mutation.Target.SplitParts;
        if (parts.Count < 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return PlanServiceResult.Rejected("Split necesita al menos dos pasos con descripción");
        }

        if (StateMachines.IsPlanItemTerminal(item.State))
        {
            return PlanServiceResult.Rejected("No se puede dividir un item terminado (" + item.State + ")");
        }

        var nextOrder = plan.Items().Count == 0 ? 1 : plan.Items().Max(i => i.Order) + 1;
        var added = parts.Select((text, i) => (DomainEventPayload) new PlanItemAdded(PlanItemId.New(), plan.Id!, text,
            nextOrder + i, item.Id, Array.Empty<PlanItemId>(), item.Required, new Dictionary<string, string>()));
        return Structural(plan, mutation, MutationImpact.Minor, added.ToArray());
    }

    private static PlanServiceResult ApplyReorder(PlanProjection plan, PlanMutation mutation)
    {
        var order = mutation.Target.ReorderList;
        var ids = plan.Items().Select(i => i.Id).ToHashSet();
        if (order.Count != ids.Count || order.Distinct().Count() != order.Count || !order.All(ids.Contains))
        {
            return PlanServiceResult.Rejected("Reorder debe incluir cada item del Plan exactamente una vez");
        }

        // Solo se reordena lo pendiente: los items en curso o terminados conservan su posición relativa.
        var current = plan.Items().OrderBy(i => i.Order).Select(i => i.Id).ToArray();
        for (var i = 0; i < current.Length; i++)
        {
            var item = plan.Item(current[i])!;
            if (item.State is not (PlanItemState.Pending or PlanItemState.Ready) && !order[i].Equals(current[i]))
            {
                return PlanServiceResult.Rejected("No se puede mover el item " + item.Id + " (" + item.State + ")");
            }
        }

        return Structural(plan, mutation, MutationImpact.Minor, new PlanItemReordered(order));
    }

    private static PlanServiceResult ApplyRevise(PlanProjection plan, PlanItem item, PlanMutation mutation,
        bool isRoot, bool approved)
    {
        var text = mutation.Reason ?? item.Description;
        if (item.State == PlanItemState.Failed)
        {
            // Failed → Ready solo por revisión (ADR-0016 §2).
            return Structural(plan, mutation, MutationImpact.Moderate, new PlanItemReopened(item.Id, text));
        }

        if (StateMachines.IsPlanItemTerminal(item.State))
        {
            return PlanServiceResult.Rejected("No se puede revisar un item " + item.State);
        }

        var impact = isRoot ? MutationImpact.ScopeExpansion : MutationImpact.Minor;
        var gate = isRoot ? RequireApproval(impact, mutation, approved, "Cambiar el objetivo del Run") : null;
        return gate ?? Structural(plan, mutation, impact, new PlanItemUpdated(item.Id, text, null));
    }

    private static PlanServiceResult ApplyUpdate(PlanItem item, PlanMutation mutation, bool isRoot, bool approved)
    {
        if (isRoot)
        {
            var gate = RequireApproval(MutationImpact.ScopeExpansion, mutation, approved, "Cambiar el objetivo del Run");
            if (gate is not null)
            {
                return gate;
            }
        }

        return PlanServiceResult.AcceptedOne(new PlanItemUpdated(item.Id, mutation.Reason, null));
    }

    private static PlanServiceResult ApplyLink(PlanItem item, TaskGraphProjection tasks, PlanMutation mutation)
    {
        var task = mutation.Target.LinkTask;
        if (task is null || tasks.Get(task) is null)
        {
            return PlanServiceResult.Rejected("La Task " + task + " no existe en el TaskGraph");
        }

        if (StateMachines.IsPlanItemTerminal(item.State))
        {
            return PlanServiceResult.Rejected("No se puede vincular un item terminado (" + item.State + ")");
        }

        if (item.LinkedTasks.Any(link => link.TaskId.Equals(task)))
        {
            return PlanServiceResult.Rejected("La Task " + task + " ya está vinculada al item");
        }

        return PlanServiceResult.AcceptedOne(new PlanItemLinked(item.Id,
            new PlanItemLink(task, mutation.Target.LinkRequired, mutation.Target.LinkRole)));
    }

    private static PlanServiceResult ApplyUnlink(PlanItem item, PlanMutation mutation)
    {
        var task = mutation.Target.LinkTask;
        if (task is null || !item.LinkedTasks.Any(link => link.TaskId.Equals(task)))
        {
            return PlanServiceResult.Rejected("La Task " + task + " no está vinculada al item");
        }

        return PlanServiceResult.AcceptedOne(new PlanItemUnlinked(item.Id, task));
    }

    // ── Auxiliares ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Impactos que exigen Ask (ADR-0016 §6): sin aprobación la mutación queda pendiente; con ella,
    /// la razón sigue siendo obligatoria.
    /// </summary>
    private static PlanServiceResult? RequireApproval(MutationImpact impact, PlanMutation mutation, bool approved,
        string what)
    {
        if (string.IsNullOrWhiteSpace(mutation.Reason))
        {
            return PlanServiceResult.Rejected(what + " exige una razón");
        }

        return approved
            ? null
            : PlanServiceResult.RequiresApproval(impact, what + " requiere aprobación (Ask); sin cliente → Deny");
    }

    /// <summary>Plan rev.N+1: <c>PlanRevised</c> seguido de los eventos de la mutación.</summary>
    private static PlanServiceResult Structural(PlanProjection plan, PlanMutation mutation, MutationImpact impact,
        params DomainEventPayload[] events)
    {
        var revised = new PlanRevised(plan.Id!, plan.RunId!, plan.Revision() + 1, MutationJson(mutation), impact);
        return PlanServiceResult.AcceptedStructural(new DomainEventPayload[] { revised }.Concat(events).ToArray(), impact);
    }

    private static string MutationJson(PlanMutation mutation)
    {
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("kind", mutation.Kind.ToString());
            writer.WriteString("cause", mutation.Cause.ToString());
            if (mutation.ItemId is not null)
            {
                writer.WriteString("item", mutation.ItemId.ToString());
            }

            if (mutation.Reason is not null)
            {
                writer.WriteString("text", mutation.Reason);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>El item raíz es el objetivo del Run: el de menor orden sin padre (Plan rev.1, ADR-0035 §3).</summary>
    private static bool IsRoot(PlanProjection plan, PlanItem item) =>
        item.ParentId is null && plan.Items().Where(i => i.ParentId is null).MinBy(i => i.Order)?.Id.Equals(item.Id) == true;

    /// <summary>Un item con hijos es un contenedor derivado: no admite transiciones propias (ADR-0036 §4).</summary>
    private static bool IsContainer(PlanProjection plan, PlanItem item) =>
        plan.Items().Any(other => item.Id.Equals(other.ParentId));
}
