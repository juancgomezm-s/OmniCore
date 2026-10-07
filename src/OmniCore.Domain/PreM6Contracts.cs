namespace OmniCore.Domain;

/// <summary>Declarative join policy; evaluating readiness belongs to M6.</summary>
public enum JoinKind { All, Any, Quorum, Explicit }
/// <summary>The accepted default. Additional failure actions require a versioned decision.</summary>
public enum SupervisionFailurePolicy { Wait }
public enum ResultDispositionOutcome { Accepted, Rejected, ReworkRequested }
public enum EvidenceKind { Claim, ToolBacked }
public enum ValidationLevel { Local, Integration, Project, EndToEnd }
public enum ValidationStatus { Unknown, Pending, Passed, Failed }
public enum IntegrationStatus { Unknown, NotIntegrated, Integrated, Verified }

public sealed record JoinPolicy(JoinKind Kind, int? RequiredCount = null,
    IReadOnlyList<ExecutionId>? RequiredExecutionIds = null)
{
    private IReadOnlyList<ExecutionId>? _required = ContractCopies.Optional(RequiredExecutionIds);
    public IReadOnlyList<ExecutionId>? RequiredExecutionIds
    { get => _required; init => _required = ContractCopies.Optional(value); }

    public void Validate(IReadOnlyList<ExecutionId> members)
    {
        ContractCopies.Known(Kind);
        ContractCopies.UniqueExecutions(members);
        if (Kind == JoinKind.Quorum)
        {
            if (RequiredCount is null or <= 0 || RequiredCount > members.Count || RequiredExecutionIds is not null)
                throw new ArgumentException("Invalid quorum policy.");
        }
        else if (Kind == JoinKind.Explicit)
        {
            if (RequiredCount is not null || RequiredExecutionIds is null)
                throw new ArgumentException("Explicit join requires member identities.");
            ContractCopies.UniqueExecutions(RequiredExecutionIds);
            if (RequiredExecutionIds.Any(id => !members.Contains(id)))
                throw new ArgumentException("Required execution is not a join member.");
        }
        else if (RequiredCount is not null || RequiredExecutionIds is not null)
            throw new ArgumentException("Join policy contains fields for another kind.");
    }
}

public sealed record Delegation(DelegationId DelegationId, ExecutionId ParentExecutionId,
    TaskId ChildTaskId, LaneId ChildLaneId, ProfileId ProfileId, ArtifactRef PacketRef,
    ExecutionRelation Relation, ExecutionSupervision Supervision);

public sealed record ExecutionJoin(JoinId JoinId, ExecutionId OwnerExecutionId,
    IReadOnlyList<ExecutionId> MemberExecutionIds, JoinPolicy Policy)
{
    private IReadOnlyList<ExecutionId> _members = ContractCopies.Required(MemberExecutionIds);
    public IReadOnlyList<ExecutionId> MemberExecutionIds
    { get => _members; init => _members = ContractCopies.Required(value); }
}

public sealed record SupervisionBinding(BindingId BindingId, ExecutionId SubjectExecutionId,
    ExecutionId SupervisorExecutionId, int PolicyRevision,
    SupervisionFailurePolicy FailurePolicy = SupervisionFailurePolicy.Wait);

public sealed record ExecutionMailbox(MailboxId MailboxId, ExecutionId OwnerExecutionId);
public sealed record ExecutionMailboxMessage(MailboxMessageId MessageId, MailboxId MailboxId,
    ExecutionId? SenderExecutionId, ArtifactRef ContentRef, EvidenceEventRef? CorrelationEvent = null);
public sealed record EvidenceEventRef(SessionId SessionId, EventId EventId);
public sealed record WakeRequest(WakeRequestId WakeRequestId, ExecutionId TargetExecutionId,
    EvidenceEventRef SourceEvent, string Reason, int PolicyRevision, MailboxMessageId? MessageId = null);

/// <summary>A concrete receipt reference, not a declaration that it has been verified.</summary>
public sealed record EvidenceReceiptRef(SessionId SessionId, EventId EventId, ToolCallId ToolCallId);
/// <summary>ToolBacked requires a receipt. Consumers must resolve it, never trust a model claim.</summary>
public sealed record EvidenceRef(EvidenceKind Kind, string Summary, ArtifactRef? ArtifactRef = null,
    EvidenceReceiptRef? ReceiptRef = null)
{
    public void Validate()
    {
        ContractCopies.Known(Kind);
        if (string.IsNullOrWhiteSpace(Summary)) throw new ArgumentException("Evidence summary is required.");
        if (Kind == EvidenceKind.ToolBacked && ReceiptRef is null)
            throw new ArgumentException("Tool-backed evidence requires a receipt reference.");
        if (Kind == EvidenceKind.Claim && ReceiptRef is not null)
            throw new ArgumentException("A claim cannot impersonate a tool receipt.");
        if (ReceiptRef is { } receipt)
        {
            ContractCopies.Id(receipt.SessionId?.Value);
            ContractCopies.Id(receipt.EventId?.Value);
            ContractCopies.Id(receipt.ToolCallId?.Value);
        }
    }
}

public sealed record ResultDisposition(DispositionId DispositionId, ExecutionId ExecutionId,
    ArtifactRef ResultRef, ResultDispositionOutcome Outcome, ExecutionId? EvaluatorExecutionId,
    string Reason, IReadOnlyList<EvidenceRef> EvidenceRefs)
{
    private IReadOnlyList<EvidenceRef> _evidence = ContractCopies.Required(EvidenceRefs);
    public IReadOnlyList<EvidenceRef> EvidenceRefs
    { get => _evidence; init => _evidence = ContractCopies.Required(value); }
}

/// <summary>Typed scope identity; an optional artifact describes checks/files, never grants filesystem authority.</summary>
public sealed record ValidationScope(SessionId SessionId, RunId? RunId = null, TaskId? TaskId = null,
    LaneId? LaneId = null, WorkspaceId? WorkspaceId = null, ArtifactRef? SpecificationRef = null)
{
    public void Validate()
    {
        ContractCopies.Id(SessionId?.Value);
        if (RunId is {} run) ContractCopies.Id(run.Value);
        if (TaskId is {} task) ContractCopies.Id(task.Value);
        if (LaneId is {} lane) ContractCopies.Id(lane.Value);
        if (WorkspaceId is {} workspace && string.IsNullOrWhiteSpace(workspace.ToString()))
            throw new ArgumentException("Workspace scope identity is empty.");
        if (LaneId is not null && TaskId is null || TaskId is not null && RunId is null)
            throw new ArgumentException("Validation scope lacks its enclosing identity.");
    }
}

/// <summary>Records the declared status for this scope/level only; no language policy or gate evaluator.</summary>
public sealed record ValidationState(ValidationScope Scope, ValidationLevel Level, ValidationStatus Status,
    IReadOnlyList<string> RequiredChecks, IReadOnlyList<EvidenceRef> EvidenceRefs)
{
    private IReadOnlyList<string> _checks = ContractCopies.Required(RequiredChecks);
    private IReadOnlyList<EvidenceRef> _evidence = ContractCopies.Required(EvidenceRefs);
    public IReadOnlyList<string> RequiredChecks
    { get => _checks; init => _checks = ContractCopies.Required(value); }
    public IReadOnlyList<EvidenceRef> EvidenceRefs
    { get => _evidence; init => _evidence = ContractCopies.Required(value); }
}

public sealed record ValidationDebt(ValidationDebtId DebtId, ExecutionId SubjectExecutionId,
    ValidationScope Scope, ValidationLevel RequiredLevel, IReadOnlyList<string> MissingChecks,
    IReadOnlyList<EvidenceRef> EvidenceRefs)
{
    private IReadOnlyList<string> _checks = ContractCopies.Required(MissingChecks);
    private IReadOnlyList<EvidenceRef> _evidence = ContractCopies.Required(EvidenceRefs);
    public IReadOnlyList<string> MissingChecks
    { get => _checks; init => _checks = ContractCopies.Required(value); }
    public IReadOnlyList<EvidenceRef> EvidenceRefs
    { get => _evidence; init => _evidence = ContractCopies.Required(value); }
}

public sealed record IntegrationStatusRecord(ValidationScope Scope, IntegrationStatus Status,
    IReadOnlyList<EvidenceRef> EvidenceRefs)
{
    private IReadOnlyList<EvidenceRef> _evidence = ContractCopies.Required(EvidenceRefs);
    public IReadOnlyList<EvidenceRef> EvidenceRefs
    { get => _evidence; init => _evidence = ContractCopies.Required(value); }
}

internal static class ContractCopies
{
    internal static void Reason(string text)
    { if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A record reason is required."); }
    internal static void Revision(int revision)
    { if (revision <= 0) throw new ArgumentException("Revision must be positive."); }
    internal static void Checks(IReadOnlyList<string> checks)
    { foreach (var check in checks) Reason(check); }
    internal static void Evidence(IReadOnlyList<EvidenceRef> evidence)
    { foreach (var item in evidence) { ArgumentNullException.ThrowIfNull(item); item.Validate(); } }
    internal static IReadOnlyList<ArtifactRef> EvidenceArtifacts(IReadOnlyList<EvidenceRef> evidence) =>
        evidence.Where(item => item.ArtifactRef is not null).Select(item => item.ArtifactRef!).ToArray();
    internal static void Artifacts(IReadOnlyList<ArtifactRef> artifacts)
    {
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            Id(artifact.Id?.Value);
            ArgumentNullException.ThrowIfNull(artifact.Hash);
            if (artifact.Hash.Algorithm != "sha256" || artifact.Hash.Value is not { Length: 64 }
                || artifact.Hash.Value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || artifact.Size < 0 || string.IsNullOrWhiteSpace(artifact.MediaType))
                throw new ArgumentException("Invalid artifact reference.");
            Known(artifact.Kind); Known(artifact.Sensitivity);
        }
    }
    internal static IReadOnlyList<T> Required<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.ToArray());
    }
    internal static IReadOnlyList<T>? Optional<T>(IReadOnlyList<T>? values) => values is null ? null : Required(values);
    internal static void Id(Guid? id)
    { if (id is null || id == Guid.Empty) throw new ArgumentException("A durable identity is required."); }
    internal static void Known<T>(T value) where T : struct, Enum
    { if (!Enum.IsDefined(value)) throw new ArgumentException("Unknown contract enum value."); }
    internal static void UniqueExecutions(IReadOnlyList<ExecutionId> ids)
    {
        if (ids.Count == 0) throw new ArgumentException("Join members must not be empty.");
        foreach (var id in ids) Id(id?.Value);
        if (ids.Distinct().Count() != ids.Count) throw new ArgumentException("Duplicate execution identity.");
    }
}
