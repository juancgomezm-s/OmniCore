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
/// <item><c>CausationId</c> = el comando en curso (<see cref="CausationScope"/>) o, fuera de un
/// comando, el evento anterior escrito por este stream. El primer evento sin comando es raíz.</item>
/// <item>Ids de entidad (Run, Task, Lane, Turn, PlanItem, ToolCall) leídos del payload, para
/// indexar sin parsearlo.</item>
/// </list>
/// </summary>
public sealed class EventStream
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly SessionId _sessionId;

    private RunId? _runId;

    private bool _runResolved;

    private EventId? _lastEventId;

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
        var envelope = BuildEnvelope(payload);
        _store.Append(_sessionId, envelope, durability, CancellationToken.None);
    }

    /// <summary>
    /// Persiste varios eventos en un solo commit atómico (ADR-0002 §1): o se escriben todos o
    /// ninguno, así un crash nunca deja una cadena a medias (p. ej. el outcome de una ToolCall).
    /// </summary>
    public void AppendBatch(IReadOnlyList<DomainEventPayload> payloads, DurabilityClass durability)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (payloads.Count == 0)
        {
            return;
        }

        var envelopes = new DomainEvent[payloads.Count];
        for (var i = 0; i < payloads.Count; i++)
        {
            envelopes[i] = BuildEnvelope(payloads[i]);
        }

        _store.AppendBatch(_sessionId, envelopes, durability, CancellationToken.None);
    }

    /// <summary>Replay de todos los eventos de la sesión desde la secuencia dada (1-based inclusive).</summary>
    public IReadOnlyList<DomainEvent> EventsSince(long fromSequenceInclusive) =>
        _store.ReadFrom(_sessionId, fromSequenceInclusive);

    private DomainEvent BuildEnvelope(DomainEventPayload payload)
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
        if (ids.RunId is not null)
        {
            _runId = ids.RunId;
            _runResolved = true;
        }

        var run = ids.RunId ?? CurrentRun();
        var causation = CausationScope.Current
            ?? (_lastEventId is null ? null : new EventCausation(_lastEventId));
        var envelope = DomainEvent.Create(_sessionId, type, version, causation, run, run, ids.TaskId,
            ids.LaneId, ids.TurnId, ids.PlanItemId, ids.ToolCallId, Array.Empty<ArtifactRef>(), json);
        _lastEventId = envelope.EventId;
        return envelope;
    }

    /// <summary>
    /// Run en curso de la sesión. Un stream nuevo sobre una sesión existente lo recupera del
    /// último evento correlacionado del journal (una sola lectura por stream).
    /// </summary>
    private RunId? CurrentRun()
    {
        if (!_runResolved)
        {
            _runResolved = true;
            var events = _store.ReadFrom(_sessionId, 1);
            for (var i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].CorrelationId is not null)
                {
                    _runId = events[i].CorrelationId;
                    break;
                }
            }
        }

        return _runId;
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
/// <c>ToolCallId</c>, serializadas como <c>{"Value":"guid"}</c>.
/// </summary>
internal readonly record struct EnvelopeIds(RunId? RunId, TaskId? TaskId, LaneId? LaneId, TurnId? TurnId,
    PlanItemId? PlanItemId, ToolCallId? ToolCallId)
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
            Guid(root, "ToolCallId") is { } call ? new ToolCallId(call) : null);
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
