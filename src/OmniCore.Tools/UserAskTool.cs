namespace OmniCore.Tools;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Tool <c>user.ask</c> (ADR-0045 §3): el modelo solicita información al humano mediante un
/// cuestionario estructurado. <c>EffectClass.None</c>: no toca filesystem, procesos ni red.
///
/// <para>El modelo nunca emite el evento: esta tool valida el formato del schema (puro, en
/// <c>Prepare</c>) y, si es válido, devuelve el schema codificado para que el Host publique la
/// interacción durable (<c>InteractionRequested</c> con el schema como artifact). El Host es la
/// autoridad: re-valida el schema contra los límites configurables (ADR-0045 §4) y valida la
/// respuesta contra el schema vigente. Esta tool no decide si su propia respuesta es válida.</para>
/// </summary>
public sealed class UserAskTool : ITool
{
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
            "Pide información al usuario mediante un cuestionario estructurado (single/multi/texto/Otro).",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},"
                + "\"description\":{\"type\":\"string\"},\"questions\":{\"type\":\"array\"}},"
                + "\"required\":[\"title\",\"questions\"]}"),
            new string[] { "interaction" }, true, false, ToolRisk.Low, ComponentSource.Core(),
            ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        var schema = QuestionnaireCodec.DecodeSchema(call.NormalizedArgumentsJson);
        if (schema is null)
        {
            return new PreparationRejected("user.ask: el payload no es un cuestionario válido " +
                "(falta title o questions, o el JSON no tiene la forma { title, description?, questions[] })",
                null);
        }

        var validation = QuestionnaireValidator.ValidateSchema(schema!, _limits);
        if (!validation.Valid)
        {
            return new PreparationRejected("user.ask: el cuestionario no pasa la validación del Host: "
                + Describe(validation), null);
        }

        // EffectClass.None: declaración honesta del flujo. Sin claims (no toca recursos).
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(IAuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        // El Host captura el schema publicado (Preview) para crear el artifact y emitir la
        // interacción. No hay I/O aquí: ExecuteAsync devuelve el questionnaire listo para un
        // cliente interactivo; la publicación la hace el Host (ADR-0045 §3, §7).
        var schemaJson = intent.Intent.NormalizedArgumentsJson;
        var result = new ToolResult("user.ask: solicitud de información", schemaJson, null,
            (long) schemaJson.Length, false, EffectOutcome.None);
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