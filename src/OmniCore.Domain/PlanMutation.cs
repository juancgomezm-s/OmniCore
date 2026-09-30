namespace OmniCore.Domain;

/// <summary>
/// Mutación propuesta al Plan canónico (ADR-0016 §3). El modelo nunca modifica el Plan:
/// propone mutaciones y PlanService decide.
/// </summary>
public sealed class PlanMutation
{
    public PlanMutationKind Kind { get; }

    public PlanItemId? ItemId { get; }

    public MutationCause Cause { get; }

    public string? Reason { get; }

    public MutationTarget Target { get; }

    private PlanMutation(PlanMutationKind kind, PlanItemId? itemId, MutationCause cause, string? reason, MutationTarget target)
    {
        Kind = kind;
        ItemId = itemId;
        Cause = cause;
        Reason = reason;
        Target = target;
    }

    public static PlanMutation Start(PlanItemId item, MutationCause cause) =>
        new(PlanMutationKind.Start, item, cause, null, MutationTarget.None());

    public static PlanMutation Complete(PlanItemId item, MutationCause cause, string summary) =>
        new(PlanMutationKind.Complete, item, cause, summary, MutationTarget.None());

    public static PlanMutation Block(PlanItemId item, MutationCause cause, string reason) =>
        new(PlanMutationKind.Block, item, cause, reason, MutationTarget.None());

    public static PlanMutation Unblock(PlanItemId item, MutationCause cause, string reason) =>
        new(PlanMutationKind.Unblock, item, cause, reason, MutationTarget.None());

    public static PlanMutation Fail(PlanItemId item, MutationCause cause, string reason) =>
        new(PlanMutationKind.Fail, item, cause, reason, MutationTarget.None());

    public static PlanMutation Cancel(PlanItemId item, MutationCause cause, string reason) =>
        new(PlanMutationKind.Cancel, item, cause, reason, MutationTarget.None());

    public static PlanMutation Add(int order, string text, PlanItemId? parent, IReadOnlyList<PlanItemId> dependsOn,
        MutationCause cause) =>
        new(PlanMutationKind.Add, null, cause, text, MutationTarget.Add(order, text, parent, dependsOn));

    public static PlanMutation Skip(PlanItemId item, MutationCause cause, string reason) =>
        new(PlanMutationKind.Skip, item, cause, reason, MutationTarget.None());

    public static PlanMutation Revise(PlanItemId item, MutationCause cause, string newText) =>
        new(PlanMutationKind.Revise, item, cause, newText, MutationTarget.None());

    public static PlanMutation Reorder(IReadOnlyList<PlanItemId> newOrder, MutationCause cause) =>
        new(PlanMutationKind.Reorder, null, cause, null, MutationTarget.Reorder(newOrder));

    /// <summary>Divide un item en pasos hijos: el item pasa a ser un contenedor (ADR-0036 §4).</summary>
    public static PlanMutation Split(PlanItemId item, IReadOnlyList<string> parts, MutationCause cause) =>
        new(PlanMutationKind.Split, item, cause, null, MutationTarget.None() with { SplitParts = parts });

    /// <summary>Vincula una Task del TaskGraph a un item (relación N:M, ADR-0016 §1).</summary>
    public static PlanMutation Link(PlanItemId item, TaskId task, LinkRole role, bool required, MutationCause cause) =>
        new(PlanMutationKind.Link, item, cause, null,
            MutationTarget.None() with { LinkTask = task, LinkRole = role, LinkRequired = required });

    public static PlanMutation Unlink(PlanItemId item, TaskId task, MutationCause cause) =>
        new(PlanMutationKind.Unlink, item, cause, null, MutationTarget.None() with { LinkTask = task });

    /// <summary>Cambia el texto de un item sin cambiar su estado.</summary>
    public static PlanMutation Update(PlanItemId item, MutationCause cause, string newText) =>
        new(PlanMutationKind.Update, item, cause, newText, MutationTarget.None());

    /// <summary>Pending → Ready cuando sus dependencias terminaron (R6).</summary>
    public static PlanMutation Ready(PlanItemId item, MutationCause cause) =>
        new(PlanMutationKind.Ready, item, cause, null, MutationTarget.None());
}

/// <summary>Objetivo de una mutación estructural (Add/Reorder). Para las demás es vacío.</summary>
public record MutationTarget(
    bool IsAdd,
    int AddOrder,
    string AddText,
    PlanItemId? AddParent,
    IReadOnlyList<PlanItemId> AddDependsOn,
    IReadOnlyList<PlanItemId> ReorderList)
{
    /// <summary>Textos de los pasos hijos de un Split.</summary>
    public IReadOnlyList<string> SplitParts { get; init; } = Array.Empty<string>();

    /// <summary>Task de un Link/Unlink.</summary>
    public TaskId? LinkTask { get; init; }

    public LinkRole LinkRole { get; init; } = LinkRole.Implements;

    public bool LinkRequired { get; init; } = true;

    public static MutationTarget None() => new(false, 0, string.Empty, null, new PlanItemId[0], new PlanItemId[0]);

    public static MutationTarget Add(int order, string text, PlanItemId? parent, IReadOnlyList<PlanItemId> dependsOn) =>
        new(true, order, text, parent, dependsOn, new PlanItemId[0]);

    public static MutationTarget Reorder(IReadOnlyList<PlanItemId> newOrder) =>
        new(false, 0, string.Empty, null, new PlanItemId[0], newOrder);
}