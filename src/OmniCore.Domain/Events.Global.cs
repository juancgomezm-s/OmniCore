namespace OmniCore.Domain;


/// <summary>
/// SessionCreated: se crea una sesión durable vinculada a un workspace. El workspace viaja como
/// campos planos (WorkspaceId se serializa por defecto como objeto; aquí va su string canónico).
/// </summary>
public record SessionCreated(SessionId SessionId, string WorkspaceId, string WorkspaceDisplayPath,
    ProfileId Profile, DateTimeOffset CreatedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("session.created");

    public int SchemaVersion() => 1;
}

/// <summary>
/// WorkspaceRootEstablished: fija la RAÍZ DURADERA Y VERIFICABLE del workspace al crear la sesión.
/// Es el origen EXPLÍCITO y seguro que usa la recuperación del Host (ADR-0004 §5) para reconciliar
/// efectos tras un crash: NUNCA se acepta una ruta de display (<c>WorkspaceDisplayPath</c>) ni el
/// cwd del proceso como autoridad. Solo la emiten los creadores de sesiones con un workspace real
/// (p.ej. el Explorer); las sesiones de simulación no lo emiten y por tanto no son recuperables por
/// el Host de forma automática (fallan cerrado). Cada sesión emite exactamente uno.
///
/// <para><c>DurableIdentity</c> es el token de identidad de la raíz (ADR-0004 §5): se escribe como
/// marcador dentro del propio workspace ({root}/.omnicore/workspace-id) al establecer la sesión y
/// viaja en el evento para que la recuperación verifique, al reabrir, que la ruta sigue apuntando al
/// MISMO árbol (mismo marcador). Sin él, una ruta reemplazada por symlink/junction pasaría el check
/// de Directory.Exists y la recuperación clasificaría Applied contra un árbol equivocado. Vacío = la
/// identidad no se pudo establecer: la sesión NO es recuperable automáticamente y la recuperación,
/// ante cualquier efecto pendiente, FALLA CERRADO (bloquea).</para>
/// </summary>
public record WorkspaceRootEstablished(SessionId SessionId, string CanonicalRoot, DateTimeOffset CreatedAt,
    string DurableIdentity = "") : DomainEventPayload
{
    public EventType Type() => EventType.Of("workspace.root_established");

    public int SchemaVersion() => 2;
}

/// <summary>UserInputReceived: el usuario envía input al Run (ADR-0035 §1).</summary>
public record UserInputReceived(RunId RunId, string InputPartsJson, ArtifactRef? ContentRef) : DomainEventPayload
{
    public EventType Type() => EventType.Of("user_input.received");

    public int SchemaVersion() => 1;
}

/// <summary>AssistantMessageRecorded: bloques de texto finales de un Turn (ADR-0035 §1).</summary>
public record AssistantMessageRecorded(RunId RunId, LaneId LaneId, TurnId TurnId, ArtifactRef? ContentRef)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("assistant_message.recorded");

    public int SchemaVersion() => 1;
}

/// <summary>
/// InteractionRequested: petición humana (ADR-0034 §3). Para <c>Kind == Question</c> (ADR-0045)
/// el schema del cuestionario viaja como artifact content-addressed (<c>QuestionnaireSchemaRef</c>)
/// y el evento lo referencia; no se duplica el texto voluminoso en el journal. Los campos con
/// default conservan la forma anterior (compatibilidad de protocolo, rev. 1).
/// </summary>
public record InteractionRequested(
    InteractionId InteractionId,
    InteractionKind Kind,
    string SubjectJson,
    string OptionsJson,
    string DefaultOptionId,
    DateTimeOffset? ExpiresAt,
    LaneId? Lane,
    TaskId? Task,
    PlanItemId? PlanItem,
    int QueuePosition,
    int QueueLength,
    ArtifactRef? QuestionnaireSchemaRef = null,
    string? ToolCallJson = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("interaction.requested");

    public int SchemaVersion() => 2;

    /// <summary>Distingue el payload de cuestionario de las forms previas sin tocar el discriminator.</summary>
    public bool IsQuestionnaire => Kind == InteractionKind.Question;
}

/// <summary>
/// InteractionResolved: resolución de una interacción. Para <c>Kind == Question</c> la respuesta
/// tipada (QuestionnaireResponse) viaja como artifact content-addressed (<c>AnswerRef</c>) y el
/// estado/causa se codifica en <c>State</c>/<c>Cause</c> (ADR-0045 §7: Submitted | Cancelled |
/// Expired; User | Timeout | NoClient). Los campos trailing con default conservan la forma anterior.
/// </summary>
public record InteractionResolved(
    InteractionId InteractionId,
    string OptionId,
    InteractionCause Cause,
    ArtifactRef? AnswerRef = null,
    string? State = null,
    string? ToolCallJson = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("interaction.resolved");

    public int SchemaVersion() => 2;

    public bool IsQuestionnaire => State is not null || AnswerRef is not null;
}

/// <summary>InteractionExpired: la interacción venció sin respuesta.</summary>
public record InteractionExpired(InteractionId InteractionId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("interaction.expired");

    public int SchemaVersion() => 1;
}

/// <summary>ProgressStalled: un item sin señal de progreso (ADR-0016 §9, ADR-0036 §7).</summary>
public record ProgressStalled(PlanItemId PlanItemId, int TurnsWithoutProgress, DateTimeOffset LastProgressAt)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("progress.stalled");

    public int SchemaVersion() => 1;
}

/// <summary>RunModeChanged: el Run cambia de modo sin cambiar de estado (ADR-0036 §1).</summary>
public record RunModeChanged(RunId RunId, RunMode From, RunMode To, string Cause) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.mode_changed");

    public int SchemaVersion() => 1;
}