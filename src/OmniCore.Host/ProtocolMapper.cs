namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;

/// <summary>
/// Traduce los eventos de dominio del journal a eventos del protocolo (ADR-0013 §1): el cliente
/// nunca ve los tipos del Engine. La tabla es explícita —qué eventos se exponen y con qué campos—
/// para que un cambio interno del dominio no rompa el wire. Un evento sin entrada no se expone.
/// Todos los campos son strings; los textos de usuario se redactan (ADR-0018).
/// </summary>
public sealed class ProtocolMapper
{
    private readonly IEventCodecRegistry _codecs;

    private readonly IArtifactStore? _artifacts;

    private readonly RedactionPolicy _redaction = new();

    public ProtocolMapper(IEventCodecRegistry codecs, IArtifactStore? artifacts = null)
    {
        _codecs = codecs;
        _artifacts = artifacts;
    }

    /// <summary>Eventos del protocolo para los eventos de dominio dados, en orden.</summary>
    public IReadOnlyList<WireEnvelope> Map(IReadOnlyList<DomainEvent> events)
    {
        var result = new List<WireEnvelope>();
        foreach (var evt in events)
        {
            var fields = Fields(_codecs.Decode(evt));
            if (fields is null)
            {
                continue;
            }

            var parts = new List<string>
            {
                JsonObj.Field("type", evt.Type.ToString()),
                JsonObj.Field("seq", evt.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                JsonObj.Field("sessionId", evt.SessionId.ToString()),
            };
            if (evt.CorrelationId is not null)
            {
                parts.Add(JsonObj.Field("runId", evt.CorrelationId.ToString()));
            }

            parts.AddRange(fields.Select(kv => JsonObj.Field(kv.Key, kv.Value)));
            result.Add(WireEnvelope.Event(evt.EventId.ToString(), "{" + string.Join(",", parts) + "}"));
        }

        return result;
    }

    private Dictionary<string, string>? Fields(DomainEventPayload payload) => payload switch
    {
        RunCreated e => new() { ["objective"] = _redaction.Redact(e.Objective), ["mode"] = Mode(e.Mode) },
        RunStarted => new(),
        RunAwaitingInput => new(),
        RunValidationRejected e => new() { ["missing"] = string.Join("; ", e.Missing) },
        RunCompleted e => new() { ["outcome"] = e.Outcome.ToString() },
        RunFailed e => new() { ["cause"] = _redaction.Redact(e.Cause) },
        RunCancelled => new(),
        RunModeChanged e => new() { ["from"] = Mode(e.From), ["to"] = Mode(e.To) },
        UserInputReceived e => new() { ["text"] = _redaction.Redact(InputText(e.InputPartsJson)) },
        AssistantMessageRecorded e => new() { ["text"] = _redaction.Redact(ArtifactText(e.ContentRef)) },
        TurnStarted e => new() { ["turnId"] = e.TurnId.ToString(), ["laneId"] = e.LaneId.ToString() },
        TurnCompleted e => new() { ["turnId"] = e.TurnId.ToString() },
        TurnInterrupted e => new() { ["turnId"] = e.TurnId.ToString() },
        TurnAbandoned e => new() { ["turnId"] = e.TurnId.ToString(), ["reason"] = _redaction.Redact(e.Reason) },
        ToolCallRequested e => new() { ["toolCallId"] = e.ToolCallId.ToString(), ["tool"] = e.ToolName },
        ToolCallSucceeded e => new() { ["toolCallId"] = e.ToolCallId.ToString() },
        ToolCallFailed e => new()
        {
            ["toolCallId"] = e.ToolCallId.ToString(), ["cause"] = _redaction.Redact(e.Cause),
            ["effect"] = e.EffectOutcome.ToString(),
        },
        ToolCallRejected e => new() { ["toolCallId"] = e.ToolCallId.ToString(), ["cause"] = _redaction.Redact(e.Reason) },
        PermissionDenied e => new() { ["toolCallId"] = e.ToolCallId.ToString(), ["cause"] = _redaction.Redact(e.Cause) },
        ToolCallCancelled e => new() { ["toolCallId"] = e.ToolCallId.ToString(), ["cause"] = _redaction.Redact(e.Cause) },
        ToolCallReconciled e => new() { ["toolCallId"] = e.ToolCallId.ToString(), ["outcome"] = e.Outcome.ToString() },
        InteractionRequested e => new()
        {
            ["interactionId"] = e.InteractionId.ToString(), ["kind"] = e.Kind.ToString(),
            ["options"] = string.Join(",", OptionIds(e.OptionsJson)), ["defaultOption"] = e.DefaultOptionId,
        },
        InteractionResolved e => new() { ["interactionId"] = e.InteractionId.ToString(), ["optionId"] = e.OptionId },
        InteractionExpired e => new() { ["interactionId"] = e.InteractionId.ToString() },
        PlanItemAdded e => new() { ["planItemId"] = e.PlanItemId.ToString(), ["description"] = e.Description },
        PlanItemStarted e => new() { ["planItemId"] = e.PlanItemId.ToString() },
        PlanItemCompleted e => new() { ["planItemId"] = e.PlanItemId.ToString() },
        PlanItemBlocked e => new() { ["planItemId"] = e.PlanItemId.ToString(), ["reason"] = e.Reason },
        PlanItemFailed e => new() { ["planItemId"] = e.PlanItemId.ToString(), ["reason"] = e.Reason },
        ProgressStalled e => new()
        {
            ["planItemId"] = e.PlanItemId.ToString(),
            ["turns"] = e.TurnsWithoutProgress.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        _ => null,
    };

    private static string Mode(RunMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>Texto de las partes del input (array JSON de strings, o un string suelto).</summary>
    private static string InputText(string partsJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(partsJson);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return root.GetString() ?? "";
            }

            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return string.Join(" ", root.EnumerateArray()
                    .Where(part => part.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(part => part.GetString()));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // partes ilegibles: se muestra tal cual
        }

        return partsJson;
    }

    private string ArtifactText(ArtifactRef? content)
    {
        if (content is null || _artifacts is null)
        {
            return "";
        }

        try
        {
            return _artifacts.GetText(content.Hash) ?? "";
        }
        catch (InvalidDataException)
        {
            return ""; // artifact corrupto: no se muestra (el verify lo reporta aparte)
        }
    }

    private static IEnumerable<string> OptionIds(string optionsJson)
    {
        var ids = new List<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var option in document.RootElement.EnumerateArray())
                {
                    if (option.ValueKind == System.Text.Json.JsonValueKind.Object
                        && option.TryGetProperty("id", out var id) && id.GetString() is { } text)
                    {
                        ids.Add(text);
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // opciones ilegibles: ninguna
        }

        return ids;
    }
}
