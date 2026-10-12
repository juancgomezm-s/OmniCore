namespace OmniCore.Tools;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Tool <c>user.ask</c> (ADR-0045 §3): el modelo solicita información al humano mediante un
/// cuestionario estructurado. <c>EffectClass.None</c>: no toca filesystem, procesos ni red.
///
/// <para>El modelo nunca emite el evento: esta tool valida el formato del schema (puro, en
/// <c>Prepare</c>). El Host publica la interacción durable (<c>InteractionRequested</c> con el
/// schema como artifact), re-valida los límites (ADR-0045 §4) y valida la respuesta contra el
/// schema vigente. Esta tool no decide si su propia respuesta es válida.</para>
/// </summary>
public sealed class UserAskTool : ITool
{
    private const string InputSchemaJson = """
        {
          "type":"object",
          "additionalProperties":false,
          "properties":{
            "title":{"type":"string","minLength":1,"maxLength":500},
            "description":{"type":["string","null"],"maxLength":500},
            "questions":{"type":"array","minItems":1,"maxItems":5,"items":{
              "type":"object",
              "additionalProperties":false,
              "properties":{
                "id":{"type":"string","minLength":1},
                "prompt":{"type":"string","minLength":1,"maxLength":500},
                "helpText":{"type":["string","null"]},
                "kind":{"type":"string","enum":["SingleChoice","MultipleChoice","FreeText"]},
                "options":{"type":["array","null"],"maxItems":8,"items":{
                  "type":"object",
                  "additionalProperties":false,
                  "properties":{
                    "id":{"type":"string","minLength":1},
                    "label":{"type":"string","minLength":1,"maxLength":120},
                    "description":{"type":["string","null"]}
                  },
                  "required":["id","label"]
                }},
                "other":{"type":["object","null"],
                  "additionalProperties":false,
                  "properties":{
                    "optionId":{"type":"string","minLength":1},
                    "label":{"type":"string","minLength":1,"maxLength":120},
                    "placeholder":{"type":["string","null"]},
                    "textRequired":{"type":["boolean","null"]},
                    "maxTextLength":{"type":["integer","null"],"minimum":1,"maximum":2000}
                  },
                  "required":["optionId","label"]
                },
                "required":{"type":["boolean","null"]},
                "minSelections":{"type":["integer","null"],"minimum":1,"maximum":9},
                "maxSelections":{"type":["integer","null"],"minimum":1,"maximum":9},
                "maxTextLength":{"type":["integer","null"],"minimum":1,"maximum":2000}
              },
              "required":["id","prompt","kind"]
            }}
          },
          "required":["title","questions"]
        }
        """;

    private readonly ToolDescriptor _descriptor;

    private readonly QuestionnaireLimits _limits;

    public UserAskTool() : this(QuestionnaireLimits.Default())
    {
    }

    /// <summary>Límites configurables por request (ADR-0045 §3): cambiar uno cambia el rechazo.</summary>
    public UserAskTool(QuestionnaireLimits limits)
    {
        _limits = limits;
        _descriptor = new ToolDescriptor(
            new ToolId("user.ask"),
            "Ask the user with a structured questionnaire. Use 1-5 questions and non-empty, unique question and option IDs. "
            + "SingleChoice and MultipleChoice need at least one non-empty option or a non-empty Other option; FreeText must have an empty options array and other=null. "
            + "For SingleChoice, minSelections and maxSelections must each be omitted, null, or 1. For MultipleChoice, each non-null bound must be between 1 and the number of options plus Other, and minSelections must not exceed maxSelections. For FreeText, options must be empty, other must be null, and minSelections/maxSelections must be null or omitted. "
            + "Other is omitted or null when unused; otherwise give it a unique non-empty optionId, non-empty label, and maxTextLength from 1 to 2000. "
            + "Use null (not zero, an empty object, or an empty string) for optional description/helpText/Other/bounds/text lengths when not applicable. "
            + "Do not ask for credentials, secrets, or approval of actions.",
            new InputSchema(InputSchemaJson),
            new string[] { "interaction" }, true, false, ToolRisk.Low, ComponentSource.Core(),
            ToolProtection.None, EffectClass.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        var schema = QuestionnaireCodec.DecodeSchema(call.NormalizedArgumentsJson);
        if (schema is null)
        {
            return new PreparationRejected("user.ask: el payload no es un cuestionario válido " +
                "(falta title o questions, o el JSON no tiene la forma { title, description?, questions[] })",
                null, ToolErrorCode.InvalidArguments);
        }

        var validation = QuestionnaireValidator.ValidateSchema(schema!, _limits);
        if (!validation.Valid)
        {
            return new PreparationRejected("user.ask: el cuestionario no pasa la validación del Host: "
                + Describe(validation), null, ToolErrorCode.InvalidArguments);
        }

        // EffectClass.None: declaración honesta del flujo. Sin claims (no toca recursos).
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        // El Host conserva el schema directamente desde el ToolCall y lo publica como artifact.
        // El resultado de la tool no duplica el texto del cuestionario en el journal (ADR-0045 §7).
        var result = new ToolResult("user.ask: solicitud de información", "cuestionario publicado por el Host",
            null, 0, false, EffectOutcome.None);
        return System.Threading.Tasks.Task.FromResult(result);
    }

    private static string Describe(QuestionnaireValidation validation)
    {
        var parts = new List<string>();
        foreach (var error in validation.Errors)
        {
            parts.Add(error.Code + (error.QuestionId is null ? "" : "(" + error.QuestionId + ")"));
            if (parts.Count >= 4)
            {
                break;
            }
        }

        return string.Join(", ", parts.ToArray());
    }
}
