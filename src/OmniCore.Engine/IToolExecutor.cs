namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Puerta del Engine hacia el pipeline de tools (INV-001: el modelo nunca ejecuta tools).
/// El Engine solo conoce Abstractions+Domain (grafo ADR-0009); el Host compone este puerto
/// con la implementación real (OmniCore.Tools.ToolRuntime) o un fake para los tests.
/// Ejecuta el pipeline y devuelve los eventos canónicos generados (ej. ToolCallPrepared,
/// PermissionEvaluated, ToolCallAuthorized, ToolCallStarted, ToolCallSucceeded) para que el
/// Engine los persista en orden.
/// </summary>
public interface IToolExecutor
{
    ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken);
}

/// <summary>Resultado compacto del pipeline de tools para el Engine.</summary>
public sealed class ToolOutcome
{
    public bool Succeeded { get; }

    public string? Summary { get; }

    /// <summary>Contenido real de la herramienta (Preview) que vuelve al modelo en el Turn.</summary>
    public string? Preview { get; }

    public EffectOutcome? Effect { get; }

    public ToolCallState FinalState { get; }

    public IReadOnlyList<DomainEventPayload> Events { get; }

    public ToolOutcome(bool succeeded, string? summary, string? preview, EffectOutcome? effect,
        ToolCallState finalState, IReadOnlyList<DomainEventPayload> events)
    {
        Succeeded = succeeded;
        Summary = summary;
        Preview = preview;
        Effect = effect;
        FinalState = finalState;
        Events = events;
    }

    public static ToolOutcome Ok(string summary, string? preview, EffectOutcome effect, ToolCallState state,
        IReadOnlyList<DomainEventPayload> events) => new(true, summary, preview, effect, state, events);

    public static ToolOutcome Failed(string reason, string? preview, ToolCallState state,
        IReadOnlyList<DomainEventPayload> events) => new(false, reason, preview, null, state, events);
}