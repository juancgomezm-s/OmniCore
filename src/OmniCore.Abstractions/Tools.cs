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

/// <summary>
/// Tool con efecto que describe cómo reconciliarlo tras un crash (ADR-0004 §4). El ToolRuntime la
/// consulta DESPUÉS de la autorización y ANTES de emitir el <c>ToolCallStarted</c> (Barrier), para
/// que los metadatos viajen en ese evento. Es el único punto con I/O previo al efecto: Prepare
/// sigue siendo puro (INV-013) y aquí solo se lee lo que Security ya autorizó. Devuelve null si no
/// puede predecir el efecto (reconciliación conservadora).
/// </summary>
public interface IReconcilableTool
{
    ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context);
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

    public ComponentSource(SourceKind kind, ScopeLevel scope, TrustLevel trust, string owner, string version)
    {
        Kind = kind;
        Scope = scope;
        Trust = trust;
        Owner = owner;
        Version = version;
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

    public ToolPreparationContext(string workspaceRoot, DateTimeOffset frozenClock)
    {
        WorkspaceRoot = workspaceRoot;
        FrozenClock = frozenClock;
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

    /// <summary>Host durability hook after authorization, before any tool effect; never authorizes.</summary>
    public Action<ToolIntent>? BeforeEffect { get; }

    /// <summary>Host-owned CAS for faithful filesystem pre-images; not exposed in tool arguments.</summary>
    public IArtifactStore? Artifacts { get; }

    /// <summary>Host-owned, cancellable receive operation for a delegated execution's durable mailbox.</summary>
    public Func<ToolCallId, CancellationToken, Task<string?>>? ReceiveMailbox { get; }

    public ToolExecutionContext(string workspaceRoot) => WorkspaceRoot = workspaceRoot;

    public ToolExecutionContext(string workspaceRoot, FileReadRegistry? readRegistry,
        Action<DomainEventPayload>? emitEvent = null, Func<InteractionRequested, string?>? resolveInteraction = null,
        IAuditSink? audit = null, bool isInteractive = false,
        WeakSandboxConsentState? weakSandboxConsent = null, Action<ToolIntent>? beforeEffect = null,
        IArtifactStore? artifacts = null,
        Func<ToolCallId, CancellationToken, Task<string?>>? receiveMailbox = null)
    {
        WorkspaceRoot = workspaceRoot;
        ReadRegistry = readRegistry;
        EmitEvent = emitEvent;
        ResolveInteraction = resolveInteraction;
        Audit = audit;
        IsInteractive = isInteractive;
        WeakSandboxConsent = weakSandboxConsent;
        BeforeEffect = beforeEffect;
        Artifacts = artifacts;
        ReceiveMailbox = receiveMailbox;
    }
}

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

    public ToolIntent(ToolCallId toolCallId, ToolId toolId, string normalizedArgumentsJson, EffectClass effect,
        ResourceClaims claims, ToolRisk risk, ReconciliationSpec? reconciliation)
    {
        ToolCallId = toolCallId;
        ToolId = toolId;
        NormalizedArgumentsJson = normalizedArgumentsJson;
        Effect = effect;
        Claims = claims;
        Risk = risk;
        Reconciliation = reconciliation;
    }
}

/// <summary>Especificación de reconciliación de un efecto (ADR-0004 §4).</summary>
public sealed class ReconciliationSpec
{
    public ArtifactRef? BeforeStateRef { get; init; }

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
