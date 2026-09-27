namespace OmniCore.Domain;

/// <summary>Plan canónico de un Run: progreso lógico con revisiones (ADR-0016).</summary>
public record Plan(
    PlanId Id,
    RunId RunId,
    int Revision,
    IReadOnlyList<PlanItem> Items) { }

/// <summary>Outcome de un PlanItem: resumen y evidencia al terminar, o razón de skip/cancel.</summary>
public record PlanItemOutcome(
    string? Summary,
    IReadOnlyList<ArtifactRef> EvidenceRefs,
    string? Reason) { }

/// <summary>Item del plan (ADR-0016 §2, ADR-0036 §4).</summary>
public record PlanItem(
    PlanItemId Id,
    string Description,
    PlanItemState State,
    int Order,
    PlanItemId? ParentId,
    IReadOnlyList<PlanItemId> DependsOn,
    IReadOnlyList<PlanItemLink> LinkedTasks,
    bool Required,
    PlanItemOutcome? Outcome,
    Dictionary<string, string> Metadata) { }

/// <summary>Vínculo PlanItem ↔ Task (ADR-0016 §2).</summary>
public record PlanItemLink(TaskId TaskId, bool Required, LinkRole Role) { }

/// <summary>Proyección WorkingState del plan (ADR-0016 §7). Se reconstruye por Turn.</summary>
public record WorkingState(
    string RunObjective,
    int PlanRevision,
    IReadOnlyList<WorkingStateItem> Items,
    PlanItemId? CurrentPlanItem,
    TaskId? CurrentTask,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Blockers,
    string? NextExpectedWork) { }

/// <summary>Item compacto del WorkingState (estado, descripción, task vinculada).</summary>
public record WorkingStateItem(PlanItemId Id, PlanItemState State, string Description, TaskId? Task) { }