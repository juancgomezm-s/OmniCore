namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Pipeline Tool → Permission → Execution (ADR-0014). Ejecutar sin autorización es imposible
/// por tipos: ExecuteAsync solo acepta AuthorizedToolIntent, y solo Security lo construye.
/// </summary>
public interface ITool
{
    ToolDescriptor Descriptor { get; }

    /// <summary>PURA: sin I/O ni efectos, síncrona. Normaliza y declara el intent.</summary>
    ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context);

    /// <summary>Solo acepta intents autorizados. Nunca ve ni decide su autorización.</summary>
    Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Marks a protected Core tool as callable only through a server-admitted native
/// human action. It does not alter capability or Permission evaluation.</summary>
public interface INativeUserActionOnlyTool { }

/// <summary>
/// Tool con efecto que describe cómo reconciliarlo tras un crash (ADR-0004 §4). El ToolRuntime la
/// consulta DESPUÉS de la autorización y ANTES de emitir el <c>ToolCallStarted</c> (Barrier), para
/// que los metadatos viajen en ese evento. Es el único punto con I/O previo al efecto: Prepare
/// sigue siendo puro (INV-013) y aquí solo se lee lo que Security ya autorizó. Devuelve null si no
/// puede predecir el efecto (reconciliación conservadora).
/// </summary>
public interface IReconcilableTool
{
    ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Effect tool whose reconciliation manifest is mandatory. A missing/failed pre-image
/// must stop before the durable start barrier, never fall through to an untracked effect.</summary>
public interface IRequiresReconciliationTool : IReconcilableTool { }
/// <summary>Core-owned batch publisher whose journal envelope is validated against the exact
/// authorized targets and its protected source artifact before the durable effect barrier.</summary>
public interface ITrustedBatchPublicationTool : IRequiresReconciliationTool
{
    bool ValidatePublicationEnvelope(ToolIntent intent, IReadOnlyList<ToolIntent> effectiveIntents,
        ToolExecutionContext context, ReconciliationSpec reconciliation);
}

/// <summary>Core-owned native lane disposition with a mandatory protected, exact-snapshot receipt.</summary>
public interface ITrustedWorktreeDispositionTool : IRequiresReconciliationTool
{
    bool ValidateWorktreeDispositionEnvelope(ToolIntent intent, ToolExecutionContext context,
        ReconciliationSpec reconciliation);
}

/// <summary>Core-owned native exact-reference retention with a protected review receipt.</summary>
public interface ITrustedWorktreeRetentionTool : IRequiresReconciliationTool
{
    bool ValidateWorktreeRetentionEnvelope(ToolIntent intent, ToolExecutionContext context,
        ReconciliationSpec reconciliation);
}

/// <summary>Host-provided publisher for a protected immutable extension write proposal.</summary>
public interface IExtensionBatchPublicationCoordinator
{
    IReadOnlyList<ToolIntent> ResolveAuthorizedIntents(ToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
    ReconciliationSpec PrepareExtensionBatch(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
    Task<ToolResult> ApplyExtensionBatchAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Host-resolved operation intents whose exact targets are unavailable to pure Prepare.
/// The runtime resolves them after Prepare and before permission/model-boundary evaluation.</summary>
public interface IAutomaticExtensionBatchAuthority
{
    bool AllowsAutomaticExtensionBatch(ToolIntent intent);
    bool AllowsAutomaticExtensionBatchCurrently(ToolIntent intent);
    bool AllowsAutomaticExtensionBatchPublicationCurrently();
    PermissionDecision EvaluateCurrentPermission(ToolIntent intent);
}

public interface IStagedExtensionBatchProducer
{
    bool IsTrustedPrivateWorkspaceWriter(ToolIntent intent);
}

/// <summary>Core process control bound by Host to a private, scoped process handle. The claim
/// describes input/stop authority; it never grants direct checkout writes.</summary>
public interface IHostScopedPrivateProcessControl
{
    bool IsTrustedPrivateProcessControl(ToolIntent intent);
    bool TryBindProcessControlIntent(ToolIntent intent, ToolExecutionContext execution, out ToolIntent boundIntent);
}

/// <summary>Core process tool that resolved an executable against its explicit workspace policy in Prepare.</summary>
public interface IHostResolvedProcessExecutable
{
    bool IsHostResolvedProcessExecutable(ToolIntent intent);
}
public interface IEffectiveToolAuthorizationIntents
{
    IReadOnlyList<ToolIntent> ResolveEffectiveAuthorizationIntents(ToolIntent intent,
        ToolPreparationContext preparation, ToolExecutionContext execution,
        CancellationToken cancellationToken);
}

/// <summary>Resolves one exact Host-owned non-filesystem resource claim before permission
/// evaluation. The intent still traverses every active capability and Permission boundary.</summary>
public interface IHostResolvedAuthorizationIntent
{
    ToolIntent ResolveHostAuthorizationIntent(ToolIntent intent, ToolPreparationContext preparation,
        ToolExecutionContext execution, CancellationToken cancellationToken);
}

/// <summary>
/// A process-backed tool may request one explicit, per-call sandbox downgrade before the
/// durable effect-start barrier. Implementations must return a request only when they can prove
/// that Strong is unavailable before any launcher was invoked. This interface never grants the
/// downgrade; the host still needs a native, scope-bound human choice.
/// </summary>
public interface IWeakSandboxConsentPreflightTool
{
    WeakSandboxConsentPreflight? CheckWeakSandboxConsentPreflight(ToolIntent intent,
        ToolPreparationContext preparation, CancellationToken cancellationToken);
}

/// <summary>Bounded display details for a pre-effect, one-call Weak sandbox choice.</summary>
public sealed record WeakSandboxConsentPreflight(string Executable, string WorkingDirectory,
    string ReasonCode);

/// <summary>Workspace-scoped exclusion for every authorized tool that declares write claims.
/// Host implementations key by the canonical physical workspace root and share across sessions.</summary>
public interface IWorkspaceWriteLeaseProvider
{
    IDisposable Acquire(string workspaceRoot, CancellationToken cancellationToken);
}

/// <summary>Host-composed coordinator for a single authenticated worktree integration.</summary>
public interface IWorktreeIntegrationCoordinator
{
    ReconciliationSpec Prepare(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
    Task<ToolResult> ApplyAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Host-owned terminal lane cleanup/discard. The Host resolves lane ownership and
/// reviewed snapshot claims; apply still runs only after ordinary Security authorization.</summary>
public interface IWorktreeDispositionCoordinator
{
    ToolIntent ResolveAuthorizationIntent(ToolIntent intent, ToolPreparationContext preparation,
        ToolExecutionContext execution, CancellationToken cancellationToken);
    ReconciliationSpec Prepare(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
    bool ValidateEnvelope(ToolIntent intent, ToolExecutionContext context, ReconciliationSpec reconciliation);
    Task<ToolResult> ApplyAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Host-resolved retention for one terminal historical Run under current-Run authority.</summary>
public interface IWorktreeRetentionCoordinator
{
    ToolIntent ResolveAuthorizationIntent(ToolIntent intent, ToolPreparationContext preparation,
        ToolExecutionContext execution, CancellationToken cancellationToken);
    ReconciliationSpec Prepare(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
    bool ValidateEnvelope(ToolIntent intent, ToolExecutionContext context, ReconciliationSpec reconciliation);
    Task<ToolResult> ApplyAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken);
}
/// <summary>Descripción de una tool (spec §32, ADR-0027).</summary>
public sealed class ToolDescriptor
{
    public ToolId Id { get; }

    public string Description { get; }

    public InputSchema InputSchema { get; }

    public IReadOnlyList<string> Tags { get; }

    public bool ReadOnly { get; }

    public bool Destructive { get; }

    public ToolRisk Risk { get; }

    public ComponentSource Source { get; }

    public ToolProtection Protection { get; }

    /// <summary>Clase máxima de efecto que la tool puede declarar (ADR-0004).</summary>
    public EffectClass EffectClass { get; }

    public ToolDescriptor(ToolId id, string description, InputSchema inputSchema, IReadOnlyList<string> tags,
        bool readOnly, bool destructive, ToolRisk risk, ComponentSource source, ToolProtection protection,
        EffectClass effectClass = EffectClass.None)
    {
        Id = id;
        Description = description;
        InputSchema = inputSchema;
        Tags = tags;
        ReadOnly = readOnly;
        Destructive = destructive;
        Risk = risk;
        Source = source;
        Protection = protection;
        EffectClass = effectClass;
    }
}

/// <summary>Id canónico de una tool con namespace por origen (ADR-0027).</summary>
public sealed class ToolId
{
    private readonly string _value;

    public ToolId(string value) => _value = value;

    public override string ToString() => _value;

    public override bool Equals(object? other) => other is ToolId t && t._value.Equals(_value, StringComparison.Ordinal);

    public override int GetHashCode() => _value.GetHashCode();
}

/// <summary>Schema de entrada declarativo (spec §32; ADR-0006 en M2).</summary>
public sealed class InputSchema
{
    private readonly string _json;

    public InputSchema(string json) => _json = json;

    public override string ToString() => _json;
}

/// <summary>Origen de la tool: kind · scope · trust · owner · version (ADR-0023).</summary>
public sealed class ComponentSource
{
    public SourceKind Kind { get; }

    public ScopeLevel Scope { get; }

    public TrustLevel Trust { get; }

    public string Owner { get; }

    public string Version { get; }

    /// <summary>Optional verified package/content digest for extension-owned components.</summary>
    public string? ContentIdentity { get; }

    public ComponentSource(SourceKind kind, ScopeLevel scope, TrustLevel trust, string owner, string version,
        string? contentIdentity = null)
    {
        Kind = kind;
        Scope = scope;
        Trust = trust;
        Owner = owner;
        Version = version;
        ContentIdentity = contentIdentity;
    }

    public static ComponentSource Core() => new(SourceKind.BuiltIn, ScopeLevel.BuiltIn, TrustLevel.Core, "core", "1");
}

/// <summary>Protección de una tool frente a sustitución implícita (ADR-0027).</summary>
public enum ToolProtection
{
    None,
    Protected,
}

/// <summary>Riesgo declarado de una tool.</summary>
public enum ToolRisk
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>Resultado de Prepare: intent listo o rechazo con la razón exacta.</summary>
public interface ToolPreparation { }

/// <summary>Prepare aceptó: contiene el intent que el Permission Engine autorizará.</summary>
public sealed class Prepared : ToolPreparation
{
    public ToolIntent Intent { get; }

    public Prepared(ToolIntent intent) => Intent = intent;
}

/// <summary>Prepare rechazó por esquema o semántica (alimenta el repair loop).
/// <c>ErrorCode</c> es el código tipado del rechazo (spec §71): null si el productor no lo fijó
/// (el runtime lo normaliza a TOOL_FAILURE al persistir: todo evento de rechazo lleva código).</summary>
public sealed class PreparationRejected : ToolPreparation
{
    public string Reason { get; }

    public string? DetailsJson { get; }

    /// <summary>Código tipado del rechazo (spec §71); null si el productor no lo fijó.</summary>
    public ToolErrorCode? ErrorCode { get; }

    public PreparationRejected(string reason, string? detailsJson) : this(reason, detailsJson, null)
    {
    }

    public PreparationRejected(string reason, string? detailsJson, ToolErrorCode? errorCode)
    {
        Reason = reason;
        DetailsJson = detailsJson;
        ErrorCode = errorCode;
    }
}

/// <summary>Input ya validado contra el schema antes de Prepare (ADR-0014 §1).</summary>
public sealed class ValidatedToolCall
{
    public ToolCallId ToolCallId { get; }

    public ToolId ToolId { get; }

    public string ProviderCallId { get; }

    public string NormalizedArgumentsJson { get; }

    public ValidatedToolCall(ToolCallId toolCallId, ToolId toolId, string providerCallId, string normalizedArgumentsJson)
    {
        ToolCallId = toolCallId;
        ToolId = toolId;
        ProviderCallId = providerCallId;
        NormalizedArgumentsJson = normalizedArgumentsJson;
    }
}

/// <summary>Contexto puro de Prepare: sin servicios, sin I/O (ADR-0014 §3).</summary>
public sealed class ToolPreparationContext
{
    public string WorkspaceRoot { get; }

    public DateTimeOffset FrozenClock { get; }

    public bool IsNativeUserAction { get; }

    public ToolPreparationContext(string workspaceRoot, DateTimeOffset frozenClock,
        bool isNativeUserAction = false)
    {
        WorkspaceRoot = workspaceRoot;
        FrozenClock = frozenClock;
        IsNativeUserAction = isNativeUserAction;
    }
}

/// <summary>
/// Contexto de ejecución ya autorizado. <c>ReadRegistry</c> es el registro de lecturas
/// efectivas por-Run (ADR-0044 §5): lo cablea el pipeline cuando una frontera de capacidad
/// del modelo está activa y lo consultan las tools (filesystem.read registra, filesystem.patch
/// exige lectura previa). null = uso directo/primitivo de la tool sin política de modelo
/// activa (equivalente al comportamiento de M2).
/// </summary>
public sealed class ToolExecutionContext
{
    public string WorkspaceRoot { get; }

    public FileReadRegistry? ReadRegistry { get; }

    /// <summary>Emite eventos canónicos durante herramientas que requieren interacción.</summary>
    public Action<DomainEventPayload>? EmitEvent { get; }

    /// <summary>Resolver de una interacción publicada; null significa que no hay cliente.</summary>
    public Func<InteractionRequested, string?>? ResolveInteraction { get; }

    public IAuditSink? Audit { get; }

    public WeakSandboxConsentState? WeakSandboxConsent { get; }

    public bool IsInteractive { get; }

    /// <summary>True only for a native user action admitted by the Host protocol.</summary>
    public bool IsNativeUserAction { get; }

    /// <summary>True only for a trusted native conversation connection that negotiated the
    /// server-defined Permission choice contract. Legacy/headless callers retain NoClient Deny.</summary>
    public bool AllowDeferredInteraction { get; }

    /// <summary>Only a trusted native workflow Verify lane may suspend a proven Strong-unavailable
    /// launch before ToolCallStarted and expose a one-call Weak consent interaction.</summary>
    public bool AllowDeferredWeakSandboxConsent { get; }

    /// <summary>Set only by rehydrating the exact accepted WeakSandboxConsent interaction for this
    /// ToolCall. It authorizes one launch attempt and is never persisted as a grant.</summary>
    public bool WeakSandboxConsentForThisCall { get; }

    /// <summary>Host durability hook after authorization, before any tool effect; never authorizes.</summary>
    public Action<ToolIntent>? BeforeEffect { get; }

    /// <summary>Host-owned CAS for faithful filesystem pre-images; not exposed in tool arguments.</summary>
    public IArtifactStore? Artifacts { get; }

    /// <summary>Host-owned, cancellable receive operation for a delegated execution's durable mailbox.</summary>
    public Func<ToolCallId, CancellationToken, Task<string?>>? ReceiveMailbox { get; }

    public IWorkspaceWriteLeaseProvider? WorkspaceWriteLeases { get; }

    /// <summary>Host-owned workspace file identity/receipt store; null only in primitive test compositions.</summary>
    public IWorkspaceMutationHistoryStore? MutationHistory { get; }

    /// <summary>Durable mutation attribution. Empty only for primitive compositions without a Host run.</summary>
    public string MutationRunId { get; }
    public string MutationTurnId { get; }
    public string MutationLaneId { get; }

    /// <summary>Model capability filter for catalog discovery; it discloses no grants.</summary>
    public Func<string, bool>? IsModelToolVisible { get; }

    /// <summary>Serializes the final capability recheck and durable ToolCallStarted Barrier
    /// with Session security policy changes. It is held only through effect admission, never
    /// while the tool performs the external effect.</summary>
    public Func<IDisposable>? AcquireSessionSecurityAdmission { get; }

    /// <summary>Requires a human Ask for a protected tool substitution; it never grants permissions.</summary>
    public bool RequiresSubstitutionAsk { get; }

    /// <summary>Identity-bound protected substitution shown in the explicit consent Ask.</summary>
    public ProtectedToolSubstitution? ProtectedSubstitution { get; }

    public ToolExecutionContext(string workspaceRoot)
    {
        WorkspaceRoot = workspaceRoot;
        MutationRunId = string.Empty;
        MutationTurnId = string.Empty;
        MutationLaneId = string.Empty;
    }

    public ToolExecutionContext(string workspaceRoot, FileReadRegistry? readRegistry,
        Action<DomainEventPayload>? emitEvent = null, Func<InteractionRequested, string?>? resolveInteraction = null,
        IAuditSink? audit = null, bool isInteractive = false,
        WeakSandboxConsentState? weakSandboxConsent = null, Action<ToolIntent>? beforeEffect = null,
        IArtifactStore? artifacts = null,
        Func<ToolCallId, CancellationToken, Task<string?>>? receiveMailbox = null,
        IWorkspaceWriteLeaseProvider? workspaceWriteLeases = null,
        Func<string, bool>? isModelToolVisible = null,
        bool requiresSubstitutionAsk = false,
        ProtectedToolSubstitution? protectedSubstitution = null,
        Func<IDisposable>? acquireSessionSecurityAdmission = null,
        bool allowDeferredInteraction = false,
        bool allowDeferredWeakSandboxConsent = false,
        bool weakSandboxConsentForThisCall = false,
        IWorkspaceMutationHistoryStore? mutationHistory = null,
        string? mutationRunId = null, string? mutationTurnId = null, string? mutationLaneId = null,
        bool isNativeUserAction = false)
    {
        WorkspaceRoot = workspaceRoot;
        ReadRegistry = readRegistry;
        EmitEvent = emitEvent;
        ResolveInteraction = resolveInteraction;
        Audit = audit;
        IsInteractive = isInteractive;
        IsNativeUserAction = isNativeUserAction;
        WeakSandboxConsent = weakSandboxConsent;
        BeforeEffect = beforeEffect;
        Artifacts = artifacts;
        ReceiveMailbox = receiveMailbox;
        WorkspaceWriteLeases = workspaceWriteLeases;
        MutationHistory = mutationHistory;
        MutationRunId = mutationRunId ?? string.Empty;
        MutationTurnId = mutationTurnId ?? string.Empty;
        MutationLaneId = mutationLaneId ?? string.Empty;
        IsModelToolVisible = isModelToolVisible;
        AcquireSessionSecurityAdmission = acquireSessionSecurityAdmission;
        AllowDeferredInteraction = allowDeferredInteraction;
        AllowDeferredWeakSandboxConsent = allowDeferredWeakSandboxConsent;
        WeakSandboxConsentForThisCall = weakSandboxConsentForThisCall;
        ProtectedSubstitution = protectedSubstitution;
        RequiresSubstitutionAsk = requiresSubstitutionAsk || protectedSubstitution is not null;
    }
}

/// <summary>Bounded, canonical identity of one eligible source-to-target preference.</summary>
public sealed record ProtectedToolSubstitution(string SourceToolId, string TargetToolId, string IdentityHash);

/// <summary>
/// Intent declarado por Prepare (ADR-0014 §2): efecto, claims, riesgo y reconciliación.
/// </summary>
public sealed class ToolIntent
{
    public ToolCallId ToolCallId { get; }

    public ToolId ToolId { get; }

    public string NormalizedArgumentsJson { get; }

    public EffectClass Effect { get; }

    public ResourceClaims Claims { get; }

    public ToolRisk Risk { get; }

    public ReconciliationSpec? Reconciliation { get; }

    /// <summary>Optional runtime-derived operation used only while authorizing a normalized
    /// existing-file write. It is never accepted from model arguments.</summary>
    public ModelToolCapability? EffectiveMutationOperation { get; }

    /// <summary>The logical target is a tombstone being recreated, so the physical create
    /// capability is required in addition to its logical edit capability.</summary>
    public bool RequiresPhysicalCreateCapability { get; }

    /// <summary>Durable workspace-history revision bound to a mutation approval when one exists.</summary>
    public long? EffectiveMutationHistoryRevision { get; }

    /// <summary>Host-derived proposal for atomic multi-file mutation budget preflight.</summary>
    public MutationProposal? EffectiveMutationProposal { get; }

    public ToolIntent(ToolCallId toolCallId, ToolId toolId, string normalizedArgumentsJson, EffectClass effect,
        ResourceClaims claims, ToolRisk risk, ReconciliationSpec? reconciliation,
        ModelToolCapability? effectiveMutationOperation = null,
        bool requiresPhysicalCreateCapability = false,
        long? effectiveMutationHistoryRevision = null,
        MutationProposal? effectiveMutationProposal = null)
    {
        ToolCallId = toolCallId;
        ToolId = toolId;
        NormalizedArgumentsJson = normalizedArgumentsJson;
        Effect = effect;
        Claims = claims;
        Risk = risk;
        Reconciliation = reconciliation;
        EffectiveMutationOperation = effectiveMutationOperation;
        RequiresPhysicalCreateCapability = requiresPhysicalCreateCapability;
        EffectiveMutationHistoryRevision = effectiveMutationHistoryRevision;
        EffectiveMutationProposal = effectiveMutationProposal;
    }
}

/// <summary>Especificación de reconciliación de un efecto (ADR-0004 §4).</summary>
public sealed class ReconciliationSpec
{
    public ArtifactRef? BeforeStateRef { get; init; }

    /// <summary>Strict, tool-specific canonical envelope for a batch effect. Only a core tool
    /// implementing IRequiresReconciliationTool may set it; generic callers cannot opt out of parsing.</summary>
    public string? DurableMetadataJson { get; init; }

    /// <summary>Optional typed entity event committed atomically with ToolCallStarted's Barrier.</summary>
    public DomainEventPayload? StartedEntityEvent { get; init; }

    public Reversibility Reversibility { get; init; } = Reversibility.Unknown;

    public string? ExpectedPreHash { get; }

    public string? ExpectedPostHash { get; }

    public string? IdempotencyKey { get; }

    public ReconciliationSpec(string? expectedPreHash, string? expectedPostHash, string? idempotencyKey)
    {
        ExpectedPreHash = expectedPreHash;
        ExpectedPostHash = expectedPostHash;
        IdempotencyKey = idempotencyKey;
    }
}

/// <summary>Typed, side-effect-free refusal discovered while preparing an authorized effect.</summary>
public sealed class ToolPreflightException(ToolErrorCode errorCode, string message) : Exception(message)
{
    public ToolErrorCode ErrorCode { get; } = errorCode;
}
