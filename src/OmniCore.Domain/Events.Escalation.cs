namespace OmniCore.Domain;

/// <summary>Causa de una escalación de modelo (spec §73).</summary>
public enum EscalationCause { CapabilityMissing, ContextLimit, RepeatedFailure, Uncertainty, ToolReliability, ManualRequest }

/// <summary>ModelEscalationRequested: se solicita subir de modelo dentro de un Run.</summary>
public record ModelEscalationRequested(RunId RunId, string FromModel, string ToModel, EscalationCause Cause) : DomainEventPayload
{
    public EventType Type() => EventType.Of("model.escalation_requested");

    public int SchemaVersion() => 1;
}

/// <summary>ModelEscalationApproved: la escalación fue aprobada.</summary>
public record ModelEscalationApproved(RunId RunId, string ToModel, string ApprovedBy) : DomainEventPayload
{
    public EventType Type() => EventType.Of("model.escalation_approved");

    public int SchemaVersion() => 1;
}

/// <summary>ModelEscalationCompleted: la escalación se aplicó y el Run continúa con el nuevo modelo.</summary>
public record ModelEscalationCompleted(RunId RunId, string ToModel) : DomainEventPayload
{
    public EventType Type() => EventType.Of("model.escalation_completed");

    public int SchemaVersion() => 1;
}
