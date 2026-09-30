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
    /// <summary>
    /// Ejecuta el pipeline con escritura en vivo del journal. Cuando el intent declara una clase
    /// de efecto ≠ None, <c>ToolCallStarted</c> se persiste sincrónicamente con
    /// <c>DurabilityClass.Barrier</c> ANTES de llamar <c>ITool.ExecuteAsync</c> (INV-014, ADR-0002
    /// §2, ADR-0004 §2), junto con los eventos previos del pipeline (Standard, en orden) para que
    /// la secuencia del Started nunca preceda a la de sus predecesores. Los outcomes quedan en
    /// <c>ToolOutcome.Events</c> para que el Engine los persista tras la ejecución, en un solo
    /// lote atómico, sin duplicar lo ya escrito. El stream es obligatorio: el Engine no puede
    /// ejecutar una tool sin journal y perder el Barrier sin darse cuenta.
    /// </summary>
    ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream stream);
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

    public InteractionId? PendingInteractionId { get; }

    public IReadOnlyList<DomainEventPayload> Events { get; }

    public ToolOutcome(bool succeeded, string? summary, string? preview, EffectOutcome? effect,
        ToolCallState finalState, IReadOnlyList<DomainEventPayload> events,
        InteractionId? pendingInteractionId = null)
    {
        Succeeded = succeeded;
        Summary = summary;
        Preview = preview;
        Effect = effect;
        FinalState = finalState;
        Events = events;
        PendingInteractionId = pendingInteractionId;
    }

    public static ToolOutcome Ok(string summary, string? preview, EffectOutcome effect, ToolCallState state,
        IReadOnlyList<DomainEventPayload> events) => new(true, summary, preview, effect, state, events);

    public static ToolOutcome Failed(string reason, string? preview, ToolCallState state,
        IReadOnlyList<DomainEventPayload> events) => new(false, reason, preview, null, state, events);
}