namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

/// <summary>
/// Servicio de interacción de cuestionarios (ADR-0045 §3, §4, §7): publicación y resolución
/// DURABLES y EXACTAMENTE-UNA un cuestionario perdurable en el journal. El modelo nunca crea
/// eventos ni decide si su respuesta es válida: el Host valida el schema al publicar
/// (<c>InteractionRequested</c>, schema como artifact content-addressed) y valida la respuesta
/// contra el schema vigente al resolver (<c>InteractionResolved</c>, respuesta como artifact).
///
/// <para>Propiedades garantizadas y verificables por la vertical (ADR-0045 §9):</para>
/// <list>
/// <item><b>exactamente una aceptación</b> — una interacción ya resuelta rechaza cualquier
/// respuesta posterior (duplicado/tardía/para otra interacción).</item>
/// <item><b>inmutabilidad</b> — schema y respuesta viven como artifacts content-addressed; el
/// journal no duplica texto libre voluminoso (ADR-0045 §7).</item>
/// <item><b>privacidad</b> — todo texto pasa por <c>RedactionPolicy</c> antes de persistir;
/// el evento solo lleva hash y ref (ADR-0018, ADR-0045 §7).</item>
/// <item><b>replay/resume</b> — <c>Pending(sessionId)</c> repite el journal y devuelve las
/// interacciones Question pendientes (Requested sin Resolved) para republicarlas tras un
/// reinicio; las ya resueltas no se vuelven a preguntar.</item>
/// </list>
/// </summary>
public sealed class QuestionnaireInteractionService
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly IArtifactStore _artifacts;

    private readonly QuestionnaireLimits _limits;

    private readonly RedactionPolicy _redaction;

    public QuestionnaireInteractionService(IEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts) : this(store, codecs, artifacts, QuestionnaireLimits.Default(),
            new RedactionPolicy())
    {
    }

    public QuestionnaireInteractionService(IEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts, QuestionnaireLimits limits, RedactionPolicy redaction)
    {
        _store = store;
        _codecs = codecs;
        _artifacts = artifacts;
        _limits = limits;
        _redaction = redaction;
    }

    /// <summary>Resultado de publicar un cuestionario (InteractionRequested durable).</summary>
    public sealed class PublishResult
    {
        public bool Published { get; }

        public InteractionId? InteractionId { get; }

        public IReadOnlyList<QuestionnaireError>? Errors { get; }

        private PublishResult(bool published, InteractionId? id, IReadOnlyList<QuestionnaireError>? errors)
        {
            Published = published;
            InteractionId = id;
            Errors = errors;
        }

        public static PublishResult Ok(InteractionId id) => new(true, id, null);

        public static PublishResult Invalid(IReadOnlyList<QuestionnaireError> errors) =>
            new(false, null, errors);
    }

    /// <summary>
    /// Valida el schema contra los límites (autoridad del Host, ADR-0045 §4) y, si es válido,
    /// redacta + persiste el schema como artifact y emite <c>InteractionRequested</c>. Devuelve
    /// <c>Invalid</c> sin tocar el journal si el schema no pasa la validación.
    /// </summary>
    public PublishResult Publish(EventStream stream, QuestionnaireSchema schema,
        InteractionId interactionId, LaneId? lane, string? toolCallJson)
    {
        var validation = QuestionnaireValidator.ValidateSchema(schema, _limits);
        if (!validation.Valid)
        {
            return PublishResult.Invalid(validation.Errors);
        }

        var safe = _redaction.Redact(QuestionnaireCodec.EncodeSchema(schema));
        var artifact = _artifacts.PutText(safe, "application/json", ArtifactKind.Other, Sensitivity.Sensitive);
        stream.Append(new InteractionRequested(interactionId, InteractionKind.Question,
            "{\"operation\":\"user.ask.questionnaire\"}", "[]", "", null, lane, null, null, 0, 1,
            artifact, toolCallJson));
        return PublishResult.Ok(interactionId);
    }

    /// <summary>Resultado de resolver una interacción de cuestionario.</summary>
    public sealed class ResolveResult
    {
        public bool Accepted { get; }

        public bool AlreadyResolved { get; }

        public bool UnknownInteraction { get; }

        public IReadOnlyList<QuestionnaireError>? Errors { get; }

        private ResolveResult(bool accepted, bool alreadyResolved, bool unknownInteraction,
            IReadOnlyList<QuestionnaireError>? errors)
        {
            Accepted = accepted;
            AlreadyResolved = alreadyResolved;
            UnknownInteraction = unknownInteraction;
            Errors = errors;
        }

        public static ResolveResult Ok() => new(true, false, false, null);

        public static ResolveResult Duplicate() => new(false, true, false, null);

        public static ResolveResult Unknown() => new(false, false, true, null);

        public static ResolveResult Invalid(IReadOnlyList<QuestionnaireError> errors) =>
            new(false, false, false, errors);
    }

    /// <summary>
    /// Valida la respuesta contra el schema vigente y, si es válida, redacta + persiste la
    /// respuesta como artifact y emite <c>InteractionResolved</c> EXACTAMENTE UNA VEZ. Una
    /// respuesta duplicada/tardía/para otra interacción se rechaza sin emitir nada. Cancelar
    /// (ADR-0045 §5) es válido como resultado estructurado (<c>State=Cancelled</c>), nunca se
    /// escoge ni se aplica Deny.
    /// </summary>
    public ResolveResult Resolve(EventStream stream, InteractionId interactionId, QuestionnaireSchema schema,
        IReadOnlyList<QuestionAnswer> answers, bool cancelled, string? toolCallJson)
    {
        var already = IsResolved(stream, interactionId);
        if (already == ResolutionStatus.Resolved)
        {
            return ResolveResult.Duplicate();
        }

        if (already == ResolutionStatus.Unknown)
        {
            return ResolveResult.Unknown();
        }

        var validation = QuestionnaireValidator.ValidateResponse(schema, answers, cancelled, _limits);
        if (!validation.Valid)
        {
            return ResolveResult.Invalid(validation.Errors);
        }

        var safe = _redaction.Redact(QuestionnaireCodec.EncodeAnswers(answers));
        var artifact = _artifacts.PutText(safe, "application/json", ArtifactKind.Other, Sensitivity.Sensitive);
        var state = cancelled ? "cancelled" : "submitted";
        stream.Append(new InteractionResolved(interactionId, "", InteractionCause.User, artifact, state,
            toolCallJson));
        return ResolveResult.Ok();
    }

    private enum ResolutionStatus
    {
        Pending,
        Resolved,
        Unknown,
    }

    /// <summary>Replay del journal: ¿la interacción está pendiente, resuelta o ausente?</summary>
    private ResolutionStatus IsResolved(EventStream stream, InteractionId interactionId)
    {
        var id = interactionId.ToString();
        var foundRequested = false;
        foreach (var evt in stream.EventsSince(1))
        {
            var payload = _codecs.Decode(evt);
            if (payload is InteractionRequested req
                && req.InteractionId.ToString().Equals(id, StringComparison.Ordinal))
            {
                foundRequested = true;
            }
            else if (payload is InteractionResolved res
                && res.InteractionId.ToString().Equals(id, StringComparison.Ordinal))
            {
                return ResolutionStatus.Resolved;
            }
        }

        return foundRequested ? ResolutionStatus.Pending : ResolutionStatus.Unknown;
    }

    /// <summary>Una interacción pending es un Requested sin Resolved (para republicación).</summary>
    public sealed class PendingInteraction
    {
        public InteractionId InteractionId { get; }

        public ArtifactRef? SchemaRef { get; }

        public LaneId? Lane { get; }

        public string? ToolCallJson { get; }

        public PendingInteraction(InteractionId interactionId, ArtifactRef? schemaRef, LaneId? lane,
            string? toolCallJson)
        {
            InteractionId = interactionId;
            SchemaRef = schemaRef;
            Lane = lane;
            ToolCallJson = toolCallJson;
        }
    }

    /// <summary>
    /// Replay del journal: interacciones Question Requested sin Resolved (ADR-0045 §6, §7).
    /// Devuelve las pendientes para republicarlas tras un reinicio; las resueltas NO aparecen.
    /// </summary>
    public IReadOnlyList<PendingInteraction> Pending(SessionId sessionId)
    {
        var pending = new Dictionary<string, PendingInteraction>(StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in _store.ReadFrom(sessionId, 1))
        {
            var payload = _codecs.Decode(evt);
            if (payload is InteractionRequested req && req.Kind == InteractionKind.Question)
            {
                pending[req.InteractionId.ToString()] = new PendingInteraction(req.InteractionId,
                    req.QuestionnaireSchemaRef, req.Lane, req.ToolCallJson);
            }
            else if (payload is InteractionResolved res)
            {
                resolved.Add(res.InteractionId.ToString());
            }
        }

        var result = new List<PendingInteraction>();
        foreach (var kv in pending)
        {
            if (!resolved.Contains(kv.Key))
            {
                result.Add(kv.Value);
            }
        }

        return result.ToArray();
    }

    /// <summary>Recupera el schema (artifact) de una interacción; null si no se encuentra.</summary>
    public QuestionnaireSchema? SchemaFor(EventStream stream, InteractionId interactionId)
    {
        var id = interactionId.ToString();
        foreach (var evt in stream.EventsSince(1))
        {
            var payload = _codecs.Decode(evt);
            if (!(payload is InteractionRequested req) || req.Kind != InteractionKind.Question
                || !req.InteractionId.ToString().Equals(id, StringComparison.Ordinal)
                || req.QuestionnaireSchemaRef is null)
            {
                continue;
            }

            var json = _artifacts.GetText(req.QuestionnaireSchemaRef!.Hash);
            return json is null ? null : QuestionnaireCodec.DecodeSchema(json!);
        }

        return null;
    }
}