namespace OmniCore.Domain;


/// <summary>ToolCallRequested: RawToolCall con ToolCallId propio de OmniCore (ADR-0004 §1).</summary>
public record ToolCallRequested(ToolCallId ToolCallId, string ProviderCallId, string ToolName, string ArgumentsJson)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.requested");

    public int SchemaVersion() => 1;
}

/// <summary>ToolCallPrepared: Prepare dio un ToolIntent (ADR-0014).</summary>
public record ToolCallPrepared(ToolCallId ToolCallId, string ToolIntentJson) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.prepared");

    public int SchemaVersion() => 1;
}

/// <summary>ToolCallRejected: esquema o Prepare inválido (alimenta el repair loop).</summary>
public record ToolCallRejected(ToolCallId ToolCallId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.rejected");

    public int SchemaVersion() => 1;
}

/// <summary>PermissionEvaluated: traza de la decisión para toda ToolCall (ADR-0036 §5).</summary>
public record PermissionEvaluated(ToolCallId ToolCallId, PermissionDecision Decision, string LayersJson,
    GrantId? AppliedGrant) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.permission_evaluated");

    public int SchemaVersion() => 1;
}

/// <summary>PermissionRequested: la decisión fue Ask; abre un InteractionRequest de tipo Permission.</summary>
public record PermissionRequested(ToolCallId ToolCallId, InteractionId InteractionId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.permission_requested");

    public int SchemaVersion() => 1;
}

/// <summary>PermissionGranted: el Ask se resolvió Allow.</summary>
public record PermissionGranted(ToolCallId ToolCallId, GrantId? GrantId, GrantLifetime? Lifetime)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.permission_granted");

    public int SchemaVersion() => 1;
}

/// <summary>PermissionDenied: el Ask se resolvió Deny (incluye causa NoInteractiveClient).</summary>
public record PermissionDenied(ToolCallId ToolCallId, string Cause) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.permission_denied");

    public int SchemaVersion() => 1;
}

/// <summary>ToolCallAuthorized: solo Security construye el AuthorizedToolIntent (ADR-0014).</summary>
public record ToolCallAuthorized(ToolCallId ToolCallId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.authorized");

    public int SchemaVersion() => 1;
}

/// <summary>
/// ToolCallStarted: commit Barrier si EffectClass ≠ None (ADR-0004 §2, ADR-0002 §2).
/// <c>ReconciliationJson</c> (opcional) es la serialización canónica de los metadatos de
/// reconciliación (ruta + hashes pre/post para filesystem; ADR-0004 §4). Al escribirse con
/// commit Barrier antes del efecto, sobrevive a un crash y es la única fuente fiel del estado
/// esperado al reconciliar desde el journal. null = sin metadatos (reconciliación conservadora).
/// </summary>
public record ToolCallStarted(ToolCallId ToolCallId, EffectClass EffectClass, string? ReconciliationJson)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.started");

    public int SchemaVersion() => 2;
}

/// <summary>ToolCallSucceeded: outcome con resultado.</summary>
public record ToolCallSucceeded(ToolCallId ToolCallId, string ResultJson) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.succeeded");

    public int SchemaVersion() => 1;
}

/// <summary>ToolCallFailed: falló con efecto (posiblemente parcial; ADR-0004 §2).</summary>
public record ToolCallFailed(ToolCallId ToolCallId, string Cause, EffectOutcome EffectOutcome)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.failed");

    public int SchemaVersion() => 1;
}

/// <summary>ToolCallEffectUnknown: empezó pero su outcome no se persiguió (solo recovery).</summary>
public record ToolCallEffectUnknown(ToolCallId ToolCallId, EffectClass EffectClass) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.effect_unknown");

    public int SchemaVersion() => 1;
}

/// <summary>ToolCallReconciled: resultado de la reconciliación (ADR-0004 §2).</summary>
public record ToolCallReconciled(ToolCallId ToolCallId, ReconciliationOutcome Outcome, string Detail,
    InteractionCause? Cause = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.reconciled");

    public int SchemaVersion() => 2;
}

/// <summary>ToolCallCancelled: antes de ejecutar (Requested/Prepared/AwaitingPermission/Authorized).</summary>
public record ToolCallCancelled(ToolCallId ToolCallId, string Cause) : DomainEventPayload
{
    public EventType Type() => EventType.Of("toolcall.cancelled");

    public int SchemaVersion() => 1;
}