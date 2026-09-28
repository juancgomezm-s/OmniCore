namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;
using System.Text;
using System.Text.Json;

/// <summary>
/// Escritor de eventos del Engine: serializa un payload tipado con el codec de su EventType,
/// construye el envelope y lo persiste asignando la secuencia (ADR-0001 §3, §5). Un solo
/// escritor por sesión (ADR-0002 §1).
/// </summary>
public sealed class EventStream
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly SessionId _sessionId;

    public EventStream(IEventStore store, IEventCodecRegistry codecs, SessionId sessionId)
    {
        _store = store;
        _codecs = codecs;
        _sessionId = sessionId;
    }

    public void Append(DomainEventPayload payload)
    {
        var type = payload.Type();
        var codec = _codecs.CodecFor(type);
        var json = RedactPayload(codec.Encode(payload));
        var envelope = DomainEvent.Create(_sessionId, type, payload.SchemaVersion(), null, null,
            ExtractRunId(payload), ExtractTaskId(payload), ExtractLaneId(payload), ExtractTurnId(payload),
            ExtractPlanItemId(payload), ExtractToolCallId(payload), new ArtifactRef[0], json);
        _store.Append(_sessionId, envelope, DurabilityClass.Standard, CancellationToken.None);
    }

    /// <summary>Replay de todos los eventos de la sesión desde la secuencia dada (1-based inclusive).</summary>
    public IReadOnlyList<DomainEvent> EventsSince(long fromSequenceInclusive) =>
        _store.ReadFrom(_sessionId, fromSequenceInclusive);

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

    private static RunId? ExtractRunId(DomainEventPayload payload)
    {
        if (payload is RunCreated r)
        {
            return r.RunId;
        }

        return null;
    }

    private static TaskId? ExtractTaskId(DomainEventPayload payload)
    {
        if (payload is TaskCreated t)
        {
            return t.TaskId;
        }

        if (payload is TaskReady r)
        {
            return null;
        }

        return null;
    }

    private static LaneId? ExtractLaneId(DomainEventPayload payload)
    {
        if (payload is LaneCreated l)
        {
            return l.LaneId;
        }

        return null;
    }

    private static TurnId? ExtractTurnId(DomainEventPayload payload)
    {
        if (payload is TurnStarted t)
        {
            return t.TurnId;
        }

        if (payload is TurnCompleted c)
        {
            return null;
        }

        return null;
    }

    private static PlanItemId? ExtractPlanItemId(DomainEventPayload payload)
    {
        if (payload is PlanItemAdded a)
        {
            return a.PlanItemId;
        }

        if (payload is PlanItemStarted s)
        {
            return s.PlanItemId;
        }

        if (payload is PlanItemCompleted c)
        {
            return c.PlanItemId;
        }

        if (payload is PlanItemBlocked b)
        {
            return b.PlanItemId;
        }

        if (payload is PlanItemFailed f)
        {
            return f.PlanItemId;
        }

        return null;
    }

    private static ToolCallId? ExtractToolCallId(DomainEventPayload payload)
    {
        if (payload is ToolCallRequested r)
        {
            return r.ToolCallId;
        }

        if (payload is ToolCallPrepared pc)
        {
            return pc.ToolCallId;
        }

        if (payload is ToolCallRejected re)
        {
            return re.ToolCallId;
        }

        if (payload is PermissionEvaluated pe)
        {
            return pe.ToolCallId;
        }

        if (payload is PermissionRequested preq)
        {
            return preq.ToolCallId;
        }

        if (payload is PermissionGranted pg)
        {
            return pg.ToolCallId;
        }

        if (payload is PermissionDenied pd)
        {
            return pd.ToolCallId;
        }

        if (payload is ToolCallAuthorized a)
        {
            return a.ToolCallId;
        }

        if (payload is ToolCallStarted s)
        {
            return s.ToolCallId;
        }

        if (payload is ToolCallSucceeded sc)
        {
            return sc.ToolCallId;
        }

        if (payload is ToolCallFailed f)
        {
            return f.ToolCallId;
        }

        return null;
    }
}
