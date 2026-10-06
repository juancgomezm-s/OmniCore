namespace OmniCore.Domain;

/// <summary>Modo de ejecución de un Run (spec §7, ADR-0035 §3).</summary>
public enum RunMode
{
    Plan,
    Act,
    Orchestrate,
}

/// <summary>Estrategia de descomposición de un Run (spec §14).</summary>
public enum ExecutionStrategy
{
    Direct,
    Workflow,
    Delegated,
}

/// <summary>Qué hacer cuando una Task requerida falla (ADRs 0035/0016).</summary>
public enum FailurePolicy
{
    FailRun,
    BlockDependents,
    AllowPartial,
}

/// <summary>Estados canónicos del Run (ADR-0036 §1).</summary>
public enum RunState
{
    Created,
    Running,
    AwaitingInput,
    Validating,
    Completed,
    CompletedWithIssues,
    Failed,
    Cancelled,
}

/// <summary>Outcome de un RunCompleted (ADR-0036 §1).</summary>
public enum RunOutcome
{
    Completed,
    CompletedWithIssues,
    Planned,
}

/// <summary>Estados canónicos de Task (ADR-0036 §2).</summary>
public enum TaskState
{
    Pending,
    Ready,
    Running,
    Blocked,
    Completed,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>Estados canónicos de Lane (ADR-0036 §3).</summary>
public enum LaneState
{
    Queued,
    Provisioning,
    Running,
    Blocked,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Actividad derivada de una Lane (ADR-0036 §3); nunca se persiste.</summary>
public enum LaneActivity
{
    None,
    WaitingForModel,
    WaitingForTool,
    WaitingForPermission,
    WaitingForInput,
    WaitingForSubtask,
    Validating,
    Stalled,
}

/// <summary>Estados de Turn (ADR-0036 §6).</summary>
public enum TurnState
{
    Started,
    ModelCompleted,
    Completed,
    Interrupted,
    Abandoned,
}

/// <summary>Etapas del ciclo de vida durable de una ToolCall (ADR-0004, ADR-0036 §5).</summary>
public enum ToolCallState
{
    Requested,
    Prepared,
    AwaitingPermission,
    Authorized,
    Started,
    Succeeded,
    Failed,
    EffectUnknown,
    Reconciled,
    Cancelled,
    Rejected,
}

/// <summary>Clase de efecto lateral declarada en Prepare (ADR-0004 §3).</summary>
public enum EffectClass
{
    None,
    Rerunnable,
    Reconcilable,
    NonIdempotent,
}

/// <summary>Resultado de un efecto tras la ejecución (ADR-0004).</summary>
public enum EffectOutcome
{
    None,
    Applied,
    Partial,
    Unknown,
}

/// <summary>Resultado de la reconciliación de un efecto (ADR-0004 §2).</summary>
public enum ReconciliationOutcome
{
    Applied,
    NotApplied,
    Conflict,
    Unresolvable,
}

/// <summary>Decisión de permisos con orden total Deny &lt; Ask &lt; Allow (ADR-0037 §1).</summary>
public enum PermissionDecision
{
    Deny,
    Ask,
    Allow,
}

/// <summary>Lifetime de un grant (ADR-0037 §5). "Project" pasó a llamarse Workspace.</summary>
public enum GrantLifetime
{
    Once,
    Run,
    Session,
    Workspace,
}

/// <summary>Tipo de interacción humano-en-el-bucle (ADR-0034).</summary>
public enum InteractionKind
{
    Permission,
    ReconciliationConflict,
    PlanScopeChange,
    PlanApproval,
    MemoryPromotion,
    IntegrationConflict,
    WorkspaceTrust,
    WeakSandboxConsent,
    BudgetExceeded,
    Question,
    AcceptanceConfirmation,
    ModelRouteConsent,
}

/// <summary>Estado de un PlanItem (ADR-0036 §4).</summary>
public enum PlanItemState
{
    Pending,
    Ready,
    InProgress,
    Blocked,
    Completed,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>Rol de un vínculo PlanItem ↔ Task (ADR-0016 §2).</summary>
public enum LinkRole
{
    Implements,
    Verifies,
    Supports,
}

/// <summary>Causa de una mutación del Plan (ADR-0016 §3).</summary>
public enum MutationCause
{
    Model,
    User,
    Reconciler,
    Policy,
    Recovery,
}

/// <summary>Impacto de una mutación del Plan (ADR-0016 §6).</summary>
public enum MutationImpact
{
    Minor,
    Moderate,
    ScopeExpansion,
    RequiredSkip,
    RequiredCancel,
}

/// <summary>Tipo de mutación que un modelo o usuario propone (ADR-0016 §3, ADR-0036 §4).</summary>
public enum PlanMutationKind
{
    Start,
    Complete,
    Block,
    Unblock,
    Fail,
    Cancel,
    Add,
    Split,
    Reorder,
    Skip,
    Revise,
    Update,
    Link,
    Unlink,

    /// <summary>Pending → Ready (R6); solo lo emite el reconciler. Al final: los valores previos no cambian.</summary>
    Ready,
}

/// <summary>Política de respuesta ante un item estancado (ADR-0016 §9).</summary>
public enum StallPolicy
{
    Replan,
    Diagnose,
    EscalateModel,
    SplitTask,
    AskUser,
}

/// <summary>Categoría de señal de progreso para el watchdog (ADR-0016 §9).</summary>
public enum ProgressSignalKind
{
    StateTransition,
    ToolEffectApplied,
    ToolSucceeded,
    ValidationResult,
    PlanTransition,
}

/// <summary>Durabilidad exigida a un commit del Event Store (ADR-0002 §2).</summary>
public enum DurabilityClass
{
    Standard,
    Barrier,
}

/// <summary>Resultado de una interacción (ADR-0034 §3).</summary>
public enum InteractionCause
{
    User,
    Timeout,
    NoClient,
}

/// <summary>Intención de una opción de interacción (ADR-0034).</summary>
public enum OptionIntent
{
    Allow,
    Deny,
    Choose,
}
