namespace OmniCore.Domain;

/// <summary>Record-only pre-M6 events. Shape validation grants no authority and executes no policy.</summary>
public interface IPreM6ContractEvent : DomainEventPayload
{
    ExecutionId ExecutionId { get; }
    void Validate();
    IReadOnlyList<ArtifactRef> RecordArtifacts();
}

public sealed record DelegationCreated(ExecutionId ExecutionId, Delegation Delegation) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("delegation.created");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [Delegation.PacketRef];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Delegation);
        ContractCopies.Id(Delegation.DelegationId?.Value); ContractCopies.Id(Delegation.ParentExecutionId?.Value); ContractCopies.Id(Delegation.ChildTaskId?.Value); ContractCopies.Id(Delegation.ChildLaneId?.Value); ContractCopies.Id(Delegation.ProfileId?.Value); ContractCopies.Known(Delegation.Relation); ContractCopies.Known(Delegation.Supervision); if (ExecutionId != Delegation.ParentExecutionId) throw new ArgumentException("Delegation owner mismatch.");
        ContractCopies.Artifacts(RecordArtifacts());
    }
}
public sealed record DelegationAccepted(ExecutionId ExecutionId, DelegationId DelegationId, ExecutionId ChildExecutionId) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("delegation.accepted");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(DelegationId?.Value); ContractCopies.Id(ChildExecutionId?.Value);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record DelegationReturned(ExecutionId ExecutionId, DelegationId DelegationId, ArtifactRef ResultRef) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("delegation.returned");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [ResultRef];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(DelegationId?.Value);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record DelegationFailed(ExecutionId ExecutionId, DelegationId DelegationId, string Reason) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("delegation.failed");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(DelegationId?.Value); ContractCopies.Reason(Reason);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ExecutionJoinCreated(ExecutionId ExecutionId, ExecutionJoin Join) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("execution_join.created");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Join);
        ArgumentNullException.ThrowIfNull(Join.Policy);
        ContractCopies.Id(Join.JoinId?.Value); if (ExecutionId != Join.OwnerExecutionId) throw new ArgumentException("Join owner mismatch."); Join.Policy.Validate(Join.MemberExecutionIds);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ExecutionJoinResolved(ExecutionId ExecutionId, JoinId JoinId, IReadOnlyList<ExecutionId> SatisfyingExecutionIds) : IPreM6ContractEvent
{
    private IReadOnlyList<ExecutionId> _items = ContractCopies.Required(SatisfyingExecutionIds);
    public IReadOnlyList<ExecutionId> SatisfyingExecutionIds { get => _items; init => _items = ContractCopies.Required(value); }
    public EventType Type() => EventType.Of("execution_join.resolved");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(JoinId?.Value); ContractCopies.UniqueExecutions(SatisfyingExecutionIds);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ExecutionJoinFailed(ExecutionId ExecutionId, JoinId JoinId, string Reason) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("execution_join.failed");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(JoinId?.Value); ContractCopies.Reason(Reason);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record SupervisionBindingCreated(ExecutionId ExecutionId, SupervisionBinding Binding) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("supervision_binding.created");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Binding);
        ContractCopies.Id(Binding.BindingId?.Value); ContractCopies.Id(Binding.SupervisorExecutionId?.Value); if (ExecutionId != Binding.SubjectExecutionId) throw new ArgumentException("Binding subject mismatch."); ContractCopies.Revision(Binding.PolicyRevision); ContractCopies.Known(Binding.FailurePolicy);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record SupervisionBindingAccepted(ExecutionId ExecutionId, BindingId BindingId) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("supervision_binding.accepted");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(BindingId?.Value);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record SupervisionBindingFailed(ExecutionId ExecutionId, BindingId BindingId, string Reason) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("supervision_binding.failed");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(BindingId?.Value); ContractCopies.Reason(Reason);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ExecutionMailboxCreated(ExecutionId ExecutionId, ExecutionMailbox Mailbox) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("execution_mailbox.created");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Mailbox);
        ContractCopies.Id(Mailbox.MailboxId?.Value); if (ExecutionId != Mailbox.OwnerExecutionId) throw new ArgumentException("Mailbox owner mismatch.");
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ExecutionMailboxMessageReceived(ExecutionId ExecutionId, ExecutionMailboxMessage Message) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("execution_mailbox.message_received");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [Message.ContentRef];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Message);
        ContractCopies.Id(Message.MessageId?.Value); ContractCopies.Id(Message.MailboxId?.Value); if (Message.SenderExecutionId is {} sender) ContractCopies.Id(sender.Value); if (Message.CorrelationEvent is {} cause) { ContractCopies.Id(cause.SessionId?.Value); ContractCopies.Id(cause.EventId?.Value); }
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ExecutionMailboxMessageAcknowledged(ExecutionId ExecutionId, MailboxId MailboxId, MailboxMessageId MessageId) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("execution_mailbox.message_acknowledged");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(MailboxId?.Value); ContractCopies.Id(MessageId?.Value);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record WakeRequestCreated(ExecutionId ExecutionId, WakeRequest Request) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("wake_request.created");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Request);
        ArgumentNullException.ThrowIfNull(Request.SourceEvent);
        ContractCopies.Id(Request.WakeRequestId?.Value); ContractCopies.Id(Request.SourceEvent.SessionId?.Value); ContractCopies.Id(Request.SourceEvent.EventId?.Value); if (ExecutionId != Request.TargetExecutionId) throw new ArgumentException("Wake target mismatch."); ContractCopies.Reason(Request.Reason); ContractCopies.Revision(Request.PolicyRevision); if (Request.MessageId is {} message) ContractCopies.Id(message.Value);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record WakeRequestAccepted(ExecutionId ExecutionId, WakeRequestId WakeRequestId) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("wake_request.accepted");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(WakeRequestId?.Value);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record WakeRequestResolved(ExecutionId ExecutionId, WakeRequestId WakeRequestId) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("wake_request.resolved");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(WakeRequestId?.Value);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record WakeRequestFailed(ExecutionId ExecutionId, WakeRequestId WakeRequestId, string Reason) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("wake_request.failed");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(WakeRequestId?.Value); ContractCopies.Reason(Reason);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record AgentResultProduced(ExecutionId ExecutionId, ArtifactRef ResultRef, int ResultRevision, string ResultSchemaId, ArtifactRef? SupersedesResultRef = null) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("agent_result.produced");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => SupersedesResultRef is {} prior ? [ResultRef, prior] : [ResultRef];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Revision(ResultRevision);
        ContractCopies.Reason(ResultSchemaId);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ResultDispositionRecorded(ExecutionId ExecutionId, ResultDisposition Disposition) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("result_disposition.recorded");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => new[] { Disposition.ResultRef }.Concat(ContractCopies.EvidenceArtifacts(Disposition.EvidenceRefs)).ToArray();
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Disposition);
        ContractCopies.Id(Disposition.DispositionId?.Value); if (ExecutionId != Disposition.ExecutionId) throw new ArgumentException("Result owner mismatch."); ContractCopies.Known(Disposition.Outcome); ContractCopies.Reason(Disposition.Reason); if (Disposition.EvaluatorExecutionId is {} evaluator) ContractCopies.Id(evaluator.Value); ContractCopies.Evidence(Disposition.EvidenceRefs);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ValidationStateRecorded(ExecutionId ExecutionId, ValidationState State) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("validation_state.recorded");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => (State.Scope.SpecificationRef is {} scope ? new[] { scope } : Array.Empty<ArtifactRef>()).Concat(ContractCopies.EvidenceArtifacts(State.EvidenceRefs)).ToArray();
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(State);
        ArgumentNullException.ThrowIfNull(State.Scope);
        ContractCopies.Known(State.Level); ContractCopies.Known(State.Status); ContractCopies.Checks(State.RequiredChecks); ContractCopies.Evidence(State.EvidenceRefs);
        State.Scope.Validate();
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ValidationDebtCreated(ExecutionId ExecutionId, ValidationDebt Debt) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("validation_debt.created");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => (Debt.Scope.SpecificationRef is {} scope ? new[] { scope } : Array.Empty<ArtifactRef>()).Concat(ContractCopies.EvidenceArtifacts(Debt.EvidenceRefs)).ToArray();
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Debt);
        ArgumentNullException.ThrowIfNull(Debt.Scope);
        ContractCopies.Id(Debt.DebtId?.Value); if (ExecutionId != Debt.SubjectExecutionId) throw new ArgumentException("Debt subject mismatch."); ContractCopies.Known(Debt.RequiredLevel); ContractCopies.Checks(Debt.MissingChecks); if (Debt.MissingChecks.Count == 0) throw new ArgumentException("Debt requires missing checks."); ContractCopies.Evidence(Debt.EvidenceRefs);
        Debt.Scope.Validate();
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record ValidationDebtResolved(ExecutionId ExecutionId, ValidationDebtId DebtId, IReadOnlyList<EvidenceRef> EvidenceRefs) : IPreM6ContractEvent
{
    private IReadOnlyList<EvidenceRef> _items = ContractCopies.Required(EvidenceRefs);
    public IReadOnlyList<EvidenceRef> EvidenceRefs { get => _items; init => _items = ContractCopies.Required(value); }
    public EventType Type() => EventType.Of("validation_debt.resolved");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => ContractCopies.EvidenceArtifacts(EvidenceRefs);
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ContractCopies.Id(DebtId?.Value); ContractCopies.Evidence(EvidenceRefs);
        ContractCopies.Artifacts(RecordArtifacts());
    }
}

public sealed record IntegrationStatusRecorded(ExecutionId ExecutionId, IntegrationStatusRecord Status) : IPreM6ContractEvent
{
    public EventType Type() => EventType.Of("integration_status.recorded");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => (Status.Scope.SpecificationRef is {} scope ? new[] { scope } : Array.Empty<ArtifactRef>()).Concat(ContractCopies.EvidenceArtifacts(Status.EvidenceRefs)).ToArray();
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value);
        ArgumentNullException.ThrowIfNull(Status);
        ArgumentNullException.ThrowIfNull(Status.Scope);
        ContractCopies.Known(Status.Status); ContractCopies.Evidence(Status.EvidenceRefs);
        Status.Scope.Validate();
        ContractCopies.Artifacts(RecordArtifacts());
    }
}
