namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;
using System.Text;
using System.Text.Json;

/// <summary>
/// Escritor de eventos del Engine: serializa un payload tipado con el codec de su EventType,
/// construye el envelope y lo persiste asignando la secuencia (ADR-0001 §3, §5). Un solo
/// escritor por sesión (ADR-0002 §1).
///
/// Envelope (ADR-0001 §3, ADR-0013 §3):
/// <list type="bullet">
/// <item><c>CorrelationId</c> = <c>RunId</c> del Run al que pertenece el evento: el del propio
/// payload si lo lleva, si no el Run en curso de la sesión. Nulo solo antes del primer Run.</item>
/// <item><c>CausationId</c> = la causa explícita en <see cref="CausationScope"/>.
/// Sin scope no se inventa una relación causal por orden de escritura (ADR-0046 §4).</item>
/// <item>Ids de entidad leídos del payload (que tiene prioridad); los ids ausentes se completan
/// desde <see cref="ExecutionScope"/> cuando existe. Así se indexan eventos tool sin repetir sus
/// ids de ejecución en cada payload.</item>
/// <item><c>ExecutionId</c> también se toma primero del payload y, si falta, del scope ambiental.</item>
/// </list>
/// Antes de persistir, cada evento se valida contra las máquinas de estado canónicas (ADR-0036,
/// <see cref="CanonicalStateTracker"/>): una transición inválida lanza
/// <see cref="InvalidStateTransitionException"/> y no se escribe nada. El stream se pone al día
/// con lo que otros escritores hayan añadido a la sesión antes de validar.
/// </summary>
public sealed class EventStream
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly SessionId _sessionId;

    private readonly CanonicalStateTracker _tracker = new();

    /// <summary>Eventos propios ya aplicados al estado: al releerlos del store se saltan.</summary>
    private readonly HashSet<EventId> _appliedLocally = new();

    /// <summary>Payloads escritos por este stream, en orden, tal como se crearon en memoria.</summary>
    private readonly List<DomainEventPayload> _written = new();

    private long _trackedThrough;

    private RunId? _runId;

    public EventStream(IEventStore store, IEventCodecRegistry codecs, SessionId sessionId)
    {
        _store = store;
        _codecs = codecs;
        _sessionId = sessionId;
    }

    /// <summary>Persiste un evento con durabilidad Standard (comportamiento existente).</summary>
    public void Append(DomainEventPayload payload) => Append(payload, DurabilityClass.Standard);

    /// <summary>
    /// Persiste un evento con la clase de durabilidad pedida (ADR-0002 §2). Usa
    /// <see cref="DurabilityClass.Barrier"/> para confirmar con commit Barrier todo evento que
    /// precede a un efecto lateral (p. ej. ToolCallStarted con EffectClass ≠ None).
    /// </summary>
    public void Append(DomainEventPayload payload, DurabilityClass durability)
    {
        CatchUp();
        _tracker.Clone().Apply(payload);
        var pendingRunId = _runId;
        var envelope = BuildEnvelope(payload, ref pendingRunId, null, CausationScope.Current);
        _store.Append(_sessionId, envelope, durability, CancellationToken.None);
        _runId = pendingRunId;
        _tracker.Apply(payload);
        _appliedLocally.Add(envelope.EventId);
        _written.Add(payload);
    }

    /// <summary>
    /// Estado vivo: el que este stream fue aplicando en memoria a medida que escribía, sin releer
    /// ni decodificar el journal. La golden rule lo compara con la reconstrucción desde el journal.
    /// </summary>
    public CanonicalStateTracker LiveState() => _tracker.Clone();

    /// <summary>Payloads escritos por este stream, en orden (objetos en memoria, no decodificados).</summary>
    public IReadOnlyList<DomainEventPayload> WrittenPayloads => _written;

    /// <summary>
    /// Persiste varios eventos en un solo commit atómico (ADR-0002 §1): o se escriben todos o
    /// ninguno, así un crash nunca deja una cadena a medias (p. ej. el outcome de una ToolCall).
    /// Los scopes opcionales atribuyen cada payload por separado, sin cambiar su causación
    /// ambiental ni dividir el commit. Un scope nulo conserva el comportamiento ambiental.
    /// </summary>
    public void AppendBatch(IReadOnlyList<DomainEventPayload> payloads, DurabilityClass durability) =>
        AppendBatch(payloads, durability, null);

    /// <summary>Atomic batch with optional per-item attribution; payload identities remain authoritative.</summary>
    public void AppendBatch(IReadOnlyList<DomainEventPayload> payloads, DurabilityClass durability,
        IReadOnlyList<ExecutionScopeState?>? executionScopes) =>
        AppendBatch(payloads, durability, executionScopes, null);

    /// <summary>
    /// Atomic batch with explicit per-item causes. An omitted list uses CausationScope;
    /// a null item in a supplied list means no cause, rather than an ambient fallback.
    /// </summary>
    public void AppendBatch(IReadOnlyList<DomainEventPayload> payloads, DurabilityClass durability,
        IReadOnlyList<ExecutionScopeState?>? executionScopes, IReadOnlyList<CausationId?>? causations)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (executionScopes is not null && executionScopes.Count != payloads.Count)
            throw new ArgumentException("Each batch payload requires a corresponding execution scope.", nameof(executionScopes));
        if (causations is not null && causations.Count != payloads.Count)
            throw new ArgumentException("Each batch payload requires a corresponding cause.", nameof(causations));
        if (payloads.Count == 0)
        {
            return;
        }

        CatchUp();
        var validation = _tracker.Clone();
        foreach (var payload in payloads)
        {
            validation.Apply(payload); // todo el lote es válido o no se escribe nada
        }

        var pendingRunId = _runId;
        var envelopes = new DomainEvent[payloads.Count];
        for (var i = 0; i < payloads.Count; i++)
        {
            envelopes[i] = BuildEnvelope(payloads[i], ref pendingRunId, executionScopes?[i],
                causations is null ? CausationScope.Current : causations[i]);
        }

        _store.AppendBatch(_sessionId, envelopes, durability, CancellationToken.None);
        _runId = pendingRunId;
        for (var i = 0; i < payloads.Count; i++)
        {
            _tracker.Apply(payloads[i]);
            _appliedLocally.Add(envelopes[i].EventId);
            _written.Add(payloads[i]);
        }
    }

    /// <summary>Replay de todos los eventos de la sesión desde la secuencia dada (1-based inclusive).</summary>
    public IReadOnlyList<DomainEvent> EventsSince(long fromSequenceInclusive) =>
        _store.ReadFrom(_sessionId, fromSequenceInclusive);

    // Only successful persistence publishes the candidate run cursor. Sequence is not causation.
    private DomainEvent BuildEnvelope(DomainEventPayload payload, ref RunId? pendingRunId,
        ExecutionScopeState? executionScope, CausationId? causation)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var type = payload.Type();
        var version = payload.SchemaVersion();
        var current = _codecs.CurrentVersion(type);
        if (version != current)
        {
            throw new InvalidOperationException("el payload " + type + " declara v" + version
                + " pero el codec registrado escribe v" + current);
        }

        var json = RedactPayload(_codecs.CodecFor(type).Encode(payload));
        var ids = EnvelopeIds.From(json);
        var scope = executionScope ?? ExecutionScope.Current;
        var runId = ids.RunId ?? scope?.RunId;
        var taskId = ids.TaskId ?? scope?.TaskId;
        var laneId = ids.LaneId ?? scope?.LaneId;
        var turnId = ids.TurnId ?? scope?.TurnId;
        var toolCallId = ids.ToolCallId ?? scope?.ToolCallId;
        var executionId = ids.ExecutionId ?? scope?.ExecutionId;
        if (runId is not null)
        {
            pendingRunId = runId;
        }

        var run = runId ?? pendingRunId;
        var artifactRefs = ArtifactRefExtractor.Extract(payload);
        var envelope = DomainEvent.Create(_sessionId, type, version, causation, run, run, taskId,
            laneId, turnId, ids.PlanItemId, toolCallId, artifactRefs, json, executionId);
        return envelope;
    }

    /// <summary>
    /// Aplica al estado canónico los eventos que la sesión recibió desde la última vez (propios o
    /// de otros escritores) y actualiza el Run en curso. La primera vez reconstruye la sesión entera.
    /// </summary>
    private void CatchUp()
    {
        var fresh = _store.ReadFrom(_sessionId, _trackedThrough + 1);
        foreach (var evt in fresh)
        {
            var payload = _codecs.Decode(evt);
            if (!_appliedLocally.Remove(evt.EventId))
            {
                _tracker.Apply(payload);
            }

            if (payload is RunCreated created)
            {
                _runId = created.RunId;
            }
            else if (evt.CorrelationId is not null)
            {
                _runId = evt.CorrelationId;
            }

            // Un store que no asigne secuencia (dobles de test) avanza por posición.
            _trackedThrough = evt.Sequence > _trackedThrough ? evt.Sequence : _trackedThrough + 1;
        }
    }

    private static string RedactPayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
            WriteRedacted(writer, document.RootElement, new PiiRedactor());
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element, PiiRedactor redactor)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value, redactor);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteRedacted(writer, item, redactor);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? "";
                // Algunas propiedades contienen JSON serializado (p. ej. ArgumentsJson).
                // Se redactan sus valores conservando el JSON interno válido para replay.
                if ((value.StartsWith('{') || value.StartsWith('[')) && TryRedactNested(value, redactor, out var nested))
                    writer.WriteStringValue(nested);
                else
                    writer.WriteStringValue(redactor.Redact(value));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool TryRedactNested(string value, PiiRedactor redactor, out string redacted)
    {
        try
        {
            using var nested = JsonDocument.Parse(value);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
                WriteRedacted(writer, nested.RootElement, redactor);
            redacted = Encoding.UTF8.GetString(output.ToArray());
            return true;
        }
        catch (JsonException)
        {
            redacted = "";
            return false;
        }
    }
}

/// <summary>
/// Causación ambiental del comando en curso (ADR-0013 §3): el Host abre un scope al recibir un
/// command y todo evento escrito mientras dura lleva ese <c>CommandId</c> como causa.
/// </summary>
public static class CausationScope
{
    private static readonly AsyncLocal<CausationId?> _current = new();

    public static CausationId? Current => _current.Value;

    public static IDisposable Begin(CausationId causation)
    {
        ArgumentNullException.ThrowIfNull(causation);
        var previous = _current.Value;
        _current.Value = causation;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly CausationId? _previous;

        private bool _disposed;

        public Restore(CausationId? previous) => _previous = previous;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _current.Value = _previous;
            }
        }
    }
}

/// <summary>
/// Ids de entidad de un payload, leídos del JSON ya codificado (sin reflexión): propiedades de
/// primer nivel <c>RunId</c>, <c>TaskId</c>, <c>LaneId</c>, <c>TurnId</c>, <c>PlanItemId</c> y
/// <c>ToolCallId</c> y <c>ExecutionId</c>, serializadas como <c>{"Value":"guid"}</c>.
/// </summary>
internal readonly record struct EnvelopeIds(RunId? RunId, TaskId? TaskId, LaneId? LaneId, TurnId? TurnId,
    PlanItemId? PlanItemId, ToolCallId? ToolCallId, ExecutionId? ExecutionId)
{
    public static EnvelopeIds From(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return new EnvelopeIds(
            Guid(root, "RunId") is { } run ? new RunId(run) : null,
            Guid(root, "TaskId") is { } task ? new TaskId(task) : null,
            Guid(root, "LaneId") is { } lane ? new LaneId(lane) : null,
            Guid(root, "TurnId") is { } turn ? new TurnId(turn) : null,
            Guid(root, "PlanItemId") is { } item ? new PlanItemId(item) : null,
            Guid(root, "ToolCallId") is { } call ? new ToolCallId(call) : null,
            Guid(root, "ExecutionId") is { } execution ? new ExecutionId(execution) : null);
    }

    private static Guid? Guid(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("Value", out var inner))
        {
            value = inner;
        }

        return value.ValueKind == JsonValueKind.String && System.Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;
    }
}
