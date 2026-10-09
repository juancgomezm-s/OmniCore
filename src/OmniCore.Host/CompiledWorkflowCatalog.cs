namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;

public enum WorkflowStage { Explore, Implement, Verify }

/// <summary>Built-in, versioned orchestration contract; each gate is durable in its Plan item.</summary>
public static class CompiledWorkflowCatalog
{
    public static CompiledWorkflowDescriptor ExploreImplementVerify { get; } =
        new(new WorkflowRef("core:explore-implement-verify", "1"), ComponentSource.Core());

    private static readonly IReadOnlyDictionary<WorkflowStage, WorkflowStageDefinition> Stages =
        new Dictionary<WorkflowStage, WorkflowStageDefinition>
        {
            [WorkflowStage.Explore] = new(WorkflowStage.Explore, "Explore",
                "Inspect the real repository path relevant to the requested integration. Read the actual entry point and identify where the new module must connect. Do not modify files. Return concrete file and symbol references, and do not claim integration is verified.",
                ["filesystem.read", "filesystem.search", "filesystem.list"],
                [ModelToolCapability.WorkspaceRead, ModelToolCapability.Search,
                    ModelToolCapability.ReferenceResolve, ModelToolCapability.AgentMailboxWait]),
            [WorkflowStage.Implement] = new(WorkflowStage.Implement, "Implement",
                "Implement the requested integration in the workspace. Connect the module through the real entry point identified by Explore. Use only authorized filesystem tools and report remaining gaps. Do not claim validation.",
                ["filesystem.write", "filesystem.patch"],
                [ModelToolCapability.WorkspaceRead, ModelToolCapability.Search,
                    ModelToolCapability.ReferenceResolve, ModelToolCapability.PatchExisting,
                    ModelToolCapability.CreateFile, ModelToolCapability.ReplaceFile,
                    ModelToolCapability.AgentMailboxWait]),
            [WorkflowStage.Verify] = new(WorkflowStage.Verify, "Verify",
                "Run the exact integration check declared when this workflow was invoked. The command is enforced by a workflow-owned dynamic tool and runs through the ordinary permission, sandbox, fingerprint, and budget pipeline. Report the exact checks and failures.",
                ["dyn.core.verify_integration"],
                [ModelToolCapability.WorkspaceRead, ModelToolCapability.Search,
                    ModelToolCapability.ReferenceResolve, ModelToolCapability.ValidationProcess,
                    ModelToolCapability.AgentMailboxWait]),
        };

    public static WorkflowStageDefinition Stage(WorkflowStage stage) => Stages[stage];

    public static bool TryResolve(WorkflowRef reference, out WorkflowStageDefinition[] stages)
    {
        if (reference == ExploreImplementVerify.Reference)
        {
            stages = Enum.GetValues<WorkflowStage>().Select(Stage).ToArray();
            return true;
        }
        stages = [];
        return false;
    }

    internal static string InstanceKey(RunId run, WorkflowRef workflow, WorkflowRequestSpecification specification)
    {
        var value = run + "|" + workflow.Id + "@" + workflow.Version + "|"
            + specification.Objective.Trim().Normalize(NormalizationForm.FormC) + "|"
            + specification.Executable.Normalize(NormalizationForm.FormC) + "|"
            + specification.ArgvJson + "|" + specification.WorkingDirectory;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

public sealed record WorkflowStageDefinition
{
    private readonly IReadOnlyList<string> _requiredSuccessfulToolIds;
    private readonly IReadOnlyList<ModelToolCapability> _capabilities;

    public WorkflowStage Stage { get; }
    public string Name { get; }
    public string Instruction { get; }
    public IReadOnlyList<string> RequiredSuccessfulToolIds => _requiredSuccessfulToolIds;
    public IReadOnlyList<ModelToolCapability> Capabilities => _capabilities;
    public bool RequiresEveryTool { get; }

    public WorkflowStageDefinition(WorkflowStage stage, string name, string instruction,
        IReadOnlyList<string> requiredSuccessfulToolIds, IReadOnlyList<ModelToolCapability> capabilities,
        bool requiresEveryTool = false)
    {
        Stage = stage;
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Stage name is required.") : name;
        Instruction = string.IsNullOrWhiteSpace(instruction) ? throw new ArgumentException("Stage instruction is required.") : instruction;
        _requiredSuccessfulToolIds = Array.AsReadOnly(requiredSuccessfulToolIds?.ToArray()
            ?? throw new ArgumentNullException(nameof(requiredSuccessfulToolIds)));
        _capabilities = Array.AsReadOnly(capabilities?.ToArray()
            ?? throw new ArgumentNullException(nameof(capabilities)));
        if (_requiredSuccessfulToolIds.Count == 0 || _requiredSuccessfulToolIds.Any(string.IsNullOrWhiteSpace)
            || _capabilities.Count == 0 || _capabilities.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("Workflow stage requirements are invalid.");
        RequiresEveryTool = requiresEveryTool;
    }

    public IReadOnlyDictionary<string, string> Metadata(WorkflowRef workflow, string instance,
        TaskId task, LaneId lane, string objective, string profileId,
        string? verifyExecutable = null, string? verifyArgvJson = null,
        string? verifyCwd = null, long minimumSequenceExclusive = 0,
        string? previousResultEventId = null, string? previousResultArtifactId = null) => new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkflowStageContract.WorkflowIdKey] = workflow.Id,
            [WorkflowStageContract.WorkflowVersionKey] = workflow.Version,
            [WorkflowStageContract.WorkflowInstanceKey] = instance,
            [WorkflowStageContract.StageKey] = Stage.ToString(),
            [WorkflowStageContract.ContractVersionKey] = "1",
            [WorkflowStageContract.RequiredToolsKey] = string.Join(",", RequiredSuccessfulToolIds),
            [WorkflowStageContract.GateModeKey] = RequiresEveryTool ? "All" : "Any",
            [WorkflowStageContract.TaskKey] = task.ToString(),
            [WorkflowStageContract.LaneKey] = lane.ToString(),
            [WorkflowStageContract.ObjectiveKey] = objective,
            [WorkflowStageContract.ProfileKey] = profileId,
            [WorkflowStageContract.VerifyExecutableKey] = verifyExecutable ?? "",
            [WorkflowStageContract.VerifyArgvKey] = verifyArgvJson ?? "[]",
            [WorkflowStageContract.VerifyCwdKey] = verifyCwd ?? "",
            [WorkflowStageContract.MinimumSequenceKey] = minimumSequenceExclusive.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [WorkflowStageContract.PreviousResultEventKey] = previousResultEventId ?? "",
            [WorkflowStageContract.PreviousResultArtifactKey] = previousResultArtifactId ?? "",
        });
}

/// <summary>Rehydrates a stage only when durable Plan metadata exactly matches its compiled contract.</summary>
public sealed record WorkflowStageContract(WorkflowRef Workflow, string Instance,
    WorkflowStageDefinition Definition, TaskId Task, LaneId Lane, string? VerifyExecutable,
    string? VerifyArgvJson, string? VerifyCwd, string Objective, string ProfileId,
    ExecutionId? Execution, long MinimumSequenceExclusive, string PreviousResultEventId,
    string PreviousResultArtifactId)
{
    internal const string WorkflowIdKey = "workflow.id";
    internal const string WorkflowVersionKey = "workflow.version";
    internal const string WorkflowInstanceKey = "workflow.instance";
    internal const string StageKey = "workflow.stage";
    internal const string ContractVersionKey = "workflow.contractVersion";
    internal const string RequiredToolsKey = "workflow.requiredTools";
    internal const string GateModeKey = "workflow.gateMode";
    internal const string TaskKey = "workflow.taskId";
    internal const string LaneKey = "workflow.laneId";
    internal const string ObjectiveKey = "workflow.objective";
    internal const string ProfileKey = "workflow.profileId";
    internal const string VerifyExecutableKey = "workflow.verify.executable";
    internal const string VerifyArgvKey = "workflow.verify.argv";
    internal const string VerifyCwdKey = "workflow.verify.cwd";
    internal const string ExecutionKey = "workflow.executionId";
    internal const string MinimumSequenceKey = "workflow.minimumSequenceExclusive";
    internal const string PreviousResultEventKey = "workflow.previousResultEventId";
    internal const string PreviousResultArtifactKey = "workflow.previousResultArtifactId";

    public static WorkflowStageContract FromPlanItem(PlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.Metadata.TryGetValue(WorkflowIdKey, out var id)
            || !item.Metadata.TryGetValue(WorkflowVersionKey, out var version)
            || !item.Metadata.TryGetValue(WorkflowInstanceKey, out var instance)
            || !item.Metadata.TryGetValue(StageKey, out var stageValue)
            || !item.Metadata.TryGetValue(ContractVersionKey, out var contractVersion)
            || !item.Metadata.TryGetValue(RequiredToolsKey, out var tools)
            || !item.Metadata.TryGetValue(GateModeKey, out var gateMode)
            || !item.Metadata.TryGetValue(TaskKey, out var taskValue)
            || !item.Metadata.TryGetValue(LaneKey, out var laneValue)
            || !item.Metadata.TryGetValue(ObjectiveKey, out var objective)
            || !item.Metadata.TryGetValue(ProfileKey, out var profileId)
            || !item.Metadata.TryGetValue(MinimumSequenceKey, out var minimumSequenceValue)
            || !item.Metadata.TryGetValue(PreviousResultEventKey, out var previousResultEventId)
            || !item.Metadata.TryGetValue(PreviousResultArtifactKey, out var previousResultArtifactId)
            || !long.TryParse(minimumSequenceValue, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var minimumSequence) || minimumSequence < 0
            || !Guid.TryParse(taskValue, out var taskGuid) || taskGuid == Guid.Empty
            || !Guid.TryParse(laneValue, out var laneGuid) || laneGuid == Guid.Empty
            || contractVersion != "1" || instance.Length != 64
            || !instance.All(Uri.IsHexDigit) || !Enum.TryParse<WorkflowStage>(stageValue, false, out var stage))
            throw new InvalidDataException("Plan item has incomplete workflow-stage contract metadata.");
        var workflow = new WorkflowRef(id, version);
        if (!CompiledWorkflowCatalog.TryResolve(workflow, out var definitions))
            throw new InvalidDataException("Plan item references an unavailable compiled workflow.");
        var definition = definitions.SingleOrDefault(value => value.Stage == stage)
            ?? throw new InvalidDataException("Plan item references an unknown workflow stage.");
        if (!string.Equals(tools, string.Join(",", definition.RequiredSuccessfulToolIds), StringComparison.Ordinal))
            throw new InvalidDataException("Durable stage requirements differ from the compiled workflow contract.");
        if (!string.Equals(gateMode, definition.RequiresEveryTool ? "All" : "Any", StringComparison.Ordinal))
            throw new InvalidDataException("Durable stage gate mode differs from the compiled workflow contract.");
        if (item.LinkedTasks.Count != 1 || item.LinkedTasks[0].TaskId.Value != taskGuid)
            throw new InvalidDataException("Workflow stage is not bound to exactly its durable Task.");
        var task = new TaskId(taskGuid);
        var lane = new LaneId(laneGuid);
        var verifyExecutable = item.Metadata.GetValueOrDefault(VerifyExecutableKey);
        var verifyArgv = item.Metadata.GetValueOrDefault(VerifyArgvKey);
        var verifyCwd = item.Metadata.GetValueOrDefault(VerifyCwdKey);
        if (string.IsNullOrWhiteSpace(objective) || objective.Length > 4096
            || !Guid.TryParse(profileId, out var profileGuid) || profileGuid == Guid.Empty)
            throw new InvalidDataException("Workflow stage is missing its original objective or profile binding.");
        if (stage == WorkflowStage.Verify && (string.IsNullOrWhiteSpace(verifyExecutable)
            || string.IsNullOrWhiteSpace(verifyArgv) || string.IsNullOrWhiteSpace(verifyCwd)
            || minimumSequence <= 0))
            throw new InvalidDataException("Verify stage is missing its declared executable and argv.");
        if (stage == WorkflowStage.Verify)
        {
            try
            {
                using var argv = JsonDocument.Parse(verifyArgv!);
                if (argv.RootElement.ValueKind != JsonValueKind.Array || argv.RootElement.GetArrayLength() is 0 or > 64
                    || argv.RootElement.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException("Verify stage argv is malformed.");
            }
            catch (JsonException failure) { throw new InvalidDataException("Verify stage argv is malformed.", failure); }
        }
        ExecutionId? execution = null;
        if (item.Metadata.TryGetValue(ExecutionKey, out var executionValue))
        {
            if (!Guid.TryParse(executionValue, out var executionGuid) || executionGuid == Guid.Empty)
                throw new InvalidDataException("Workflow stage has an invalid execution binding.");
            execution = new ExecutionId(executionGuid);
        }
        return new(workflow, instance, definition, task, lane, verifyExecutable, verifyArgv, verifyCwd,
            objective, profileId,
            execution, minimumSequence, previousResultEventId, previousResultArtifactId);
    }

    public static bool TryFromPlanItem(PlanItem item, out WorkflowStageContract? contract)
    {
        if (!item.Metadata.ContainsKey(WorkflowIdKey)) { contract = null; return false; }
        contract = FromPlanItem(item);
        return true;
    }

    /// <summary>Confirms the contract came from the same canonical, bounded delegation admission.</summary>
    public static bool IsCanonicalAdmission(WorkflowStageContract contract, PlanItem item,
        SessionId session, RunId run, IReadOnlyList<DomainEvent> journal, IEventCodecRegistry codecs,
        IArtifactStore? artifacts = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(codecs);
        try
        {
            using var argvDocument = JsonDocument.Parse(contract.VerifyArgvJson!);
            var argv = argvDocument.RootElement.EnumerateArray().Select(value => value.GetString()!).ToArray();
            var specification = new WorkflowRequestSpecification(contract.Objective,
                contract.VerifyExecutable!, Array.AsReadOnly(argv), Path.GetFullPath(contract.VerifyCwd!));
            if (CompiledWorkflowCatalog.InstanceKey(run, contract.Workflow, specification) != contract.Instance)
                return false;
            var own = journal.Where(evt => evt.SessionId == session && evt.RunId == run).ToArray();
            var facts = own.Select(evt => (Event: evt, Payload: codecs.Decode(evt))).ToArray();
            var root = facts.Select(item => item.Payload).OfType<RunCreated>().SingleOrDefault();
            if (root is null) return false;
            var currentPlan = PlanProjection.Replay(codecs, own);
            WorkflowPriorResult? priorResult = null;
            var expectedPreviousStage = contract.Definition.Stage switch
            {
                WorkflowStage.Explore => (WorkflowStage?)null,
                WorkflowStage.Implement => WorkflowStage.Explore,
                WorkflowStage.Verify => WorkflowStage.Implement,
                _ => null,
            };
            if (expectedPreviousStage is null ? item.DependsOn.Count != 0 : item.DependsOn.Count != 1)
                return false;
            if (expectedPreviousStage is { } previousStage)
            {
                var previous = currentPlan.Item(item.DependsOn[0]);
                if (previous is null || previous.State != PlanItemState.Completed
                    || !WorkflowStageContract.TryFromPlanItem(previous, out var previousContract)
                    || previousContract!.Workflow != contract.Workflow || previousContract.Instance != contract.Instance
                    || previousContract.Definition.Stage != previousStage
                    || !IsCanonicalAdmission(previousContract, previous, session, run, own, codecs, artifacts))
                    return false;
                priorResult = ReadAcceptedPriorResult(previous, previousContract, session, run, own, codecs, artifacts);
                if (priorResult is null || contract.PreviousResultEventId != priorResult.Event.EventId.ToString()
                    || contract.PreviousResultArtifactId != priorResult.Result.ResultRef.Id.ToString()) return false;
                var previousTaskCompleted = facts.SingleOrDefault(pair => pair.Payload is TaskCompleted completed
                    && completed.TaskId == previousContract.Task).Event;
                if (previousTaskCompleted is null || contract.MinimumSequenceExclusive != previousTaskCompleted.Sequence)
                    return false;
            }
            else if (contract.MinimumSequenceExclusive != 0 || contract.PreviousResultEventId.Length != 0
                || contract.PreviousResultArtifactId.Length != 0) return false;
            var taskFact = facts.SingleOrDefault(pair => pair.Payload is TaskCreated created && created.TaskId == contract.Task);
            var laneFact = facts.SingleOrDefault(pair => pair.Payload is LaneCreated created && created.LaneId == contract.Lane);
            var delegationFacts = facts.Where(pair => pair.Payload is DelegationCreated created
                && created.Delegation.ChildTaskId == contract.Task && created.Delegation.ChildLaneId == contract.Lane).ToArray();
            var addFact = facts.SingleOrDefault(pair => pair.Payload is PlanItemAdded added && added.PlanItemId == item.Id);
            var linkFacts = facts.Where(pair => pair.Payload is PlanItemLinked linked && linked.PlanItemId == item.Id
                && linked.Link.TaskId == contract.Task && linked.Link.Required && linked.Link.Role == LinkRole.Implements).ToArray();
            if (taskFact.Payload is not TaskCreated task || laneFact.Payload is not LaneCreated lane
                || delegationFacts.Length != 1 || delegationFacts[0].Payload is not DelegationCreated delegation
                || addFact.Payload is not PlanItemAdded added || linkFacts.Length != 1)
                return false;
            if (task.ParentTaskId != root.RootTask || task.Objective != OmniServer.WorkflowStageObjective(
                    contract.Objective, contract.Definition, priorResult?.ContextText)
                || lane.TaskId != task.TaskId || lane.AgentProfile.ToString() != contract.ProfileId
                || delegation.Delegation.Priority != 0 || delegation.Delegation.Relation != ExecutionRelation.Awaited
                || delegation.Delegation.Supervision != ExecutionSupervision.Managed
                || delegation.Delegation.ProfileId != lane.AgentProfile
                || task.Budget.MaxTurns != 1 || task.Budget.MaxToolCalls is not (>= 1 and <= 4)
                || task.Budget.MaxTokens is not (>= 8_448) || task.Budget.MaxCostUsd is null or < 0)
                return false;
            var rootLane = facts.Select(pair => pair.Payload).OfType<LaneCreated>()
                .SingleOrDefault(value => value.TaskId == root.RootTask);
            var rootExecution = facts.Select(pair => pair.Payload).OfType<AgentExecutionStarted>()
                .SingleOrDefault(value => value.ParentExecutionId is null && value.LaneId == rootLane?.LaneId);
            var rootPlanItem = currentPlan.Items().SingleOrDefault(value => value.ParentId is null);
            if (rootLane is null || rootExecution is null || rootPlanItem is null
                || delegation.Delegation.ParentExecutionId != rootExecution.ExecutionId
                || rootLane.AgentProfile != lane.AgentProfile || item.ParentId != rootPlanItem.Id || !added.Required)
                return false;
            var executionMetadata = item.Metadata.GetValueOrDefault(ExecutionKey);
            if (contract.Execution is { } execution && executionMetadata != execution.ToString()
                || contract.Execution is null && executionMetadata is not null) return false;
            var causation = taskFact.Event.Causation as CommandCausation;
            if (causation is null || !Equals(laneFact.Event.Causation, causation)
                || !Equals(delegationFacts[0].Event.Causation, causation) || !Equals(addFact.Event.Causation, causation)
                || !Equals(linkFacts[0].Event.Causation, causation))
                return false;
            var admittedMetadata = added.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var key in item.Metadata.Keys.Where(key => key != ExecutionKey))
                if (!admittedMetadata.TryGetValue(key, out var value) || item.Metadata[key] != value) return false;
            return item.Metadata.Count == admittedMetadata.Count + (item.Metadata.ContainsKey(ExecutionKey) ? 1 : 0);
        }
        catch (Exception failure) when (failure is JsonException or InvalidDataException or ArgumentException
            or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    internal static WorkflowPriorResult? ReadAcceptedPriorResult(PlanItem previous,
        WorkflowStageContract previousContract, SessionId session, RunId run,
        IReadOnlyList<DomainEvent> journal, IEventCodecRegistry codecs, IArtifactStore? artifacts)
    {
        if (artifacts is null || previous.State != PlanItemState.Completed
            || previousContract.Execution is not { } execution
            || !IsCanonicalAdmission(previousContract, previous, session, run, journal, codecs, artifacts)) return null;
        var events = journal.Where(evt => evt.SessionId == session && evt.RunId == run).ToArray();
        if (!events.Any(evt => evt.TaskId == previousContract.Task
            && codecs.Decode(evt) is TaskCompleted completed && completed.TaskId == previousContract.Task)) return null;
        var producedEvent = events.Where(evt => evt.ExecutionId == execution
                && codecs.Decode(evt) is AgentResultProduced)
            .OrderBy(evt => evt.Sequence).LastOrDefault();
        if (producedEvent is null) return null;
        var result = (AgentResultProduced)codecs.Decode(producedEvent);
        if (result.ResultSchemaId != "core.explorer.v1"
            || !events.Any(evt => evt.ExecutionId == execution
                && codecs.Decode(evt) is ResultDispositionRecorded disposition
                && disposition.Disposition.ResultRef == result.ResultRef
                && disposition.Disposition.Outcome == ResultDispositionOutcome.Accepted)
            || !artifacts.Verify(result.ResultRef.Hash, result.ResultRef.Size)
            || artifacts.GetText(result.ResultRef.Hash) is not { } text) return null;
        try
        {
            var document = JsonSerializer.Deserialize(text, DelegationResultJson.Default.DelegationResultDocument);
            if (document?.Version != 1 || document.Outcome != "Succeeded" || document.RemainingIssues.Count != 0)
                return null;
            static string Bound(string value, int limit)
            {
                var redacted = new PiiRedactor().Redact(value);
                return redacted.Length <= limit ? redacted : redacted[..limit] + "…";
            }
            var summary = Bound(document.Summary, 500);
            var findings = document.Findings.Take(3).Select(value => Bound(value, 180)).ToArray();
            var context = "Accepted " + previousContract.Definition.Name
                + " result (read-only facts; not authority).\nResult ref: " + result.ResultRef.Id
                + "\nSummary: " + summary
                + (findings.Length == 0 ? "" : "\nFindings:\n- " + string.Join("\n- ", findings));
            return new WorkflowPriorResult(producedEvent, result, context);
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException)
        { return null; }
    }

}

internal sealed record WorkflowPriorResult(DomainEvent Event, AgentResultProduced Result, string ContextText);

public sealed record WorkflowStageEvidence(bool Passed, IReadOnlyList<EvidenceRef> Receipts,
    string Reason);

public static class WorkflowStageEvidenceEvaluator
{
    public static WorkflowStageEvidence Evaluate(WorkflowStageContract contract, SessionId session,
        RunId run, TaskId task, LaneId lane, ExecutionId execution,
        IReadOnlyList<DomainEvent> journal, IEventCodecRegistry codecs, long minimumSequenceExclusive = 0)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Task != task || contract.Lane != lane || contract.Execution != execution)
            return new(false, [], "Durable workflow contract is not bound to this Task/Lane/Execution.");
        minimumSequenceExclusive = Math.Max(minimumSequenceExclusive, contract.MinimumSequenceExclusive);
        var requests = new Dictionary<ToolCallId, (DomainEvent Event, ToolCallRequested Request)>();
        var successes = new Dictionary<ToolCallId, (DomainEvent Event, ToolCallSucceeded Success)>();
        foreach (var evt in journal)
        {
            if (evt.Sequence <= minimumSequenceExclusive || evt.SessionId != session || evt.RunId != run || evt.TaskId != task
                || evt.LaneId != lane || evt.ExecutionId != execution) continue;
            switch (codecs.Decode(evt))
            {
                case ToolCallRequested request:
                    if (!requests.TryAdd(request.ToolCallId, (evt, request)))
                        throw new InvalidDataException("Workflow stage contains duplicate tool request identities.");
                    break;
                case ToolCallSucceeded success:
                    if (!successes.TryAdd(success.ToolCallId, (evt, success)))
                        throw new InvalidDataException("Workflow stage contains duplicate tool receipt identities.");
                    break;
            }
        }

        var evidence = new List<EvidenceRef>();
        long? verificationReceiptSequence = null;
        ToolCallId? verificationCallId = null;
        foreach (var toolId in contract.Definition.RequiredSuccessfulToolIds)
        {
            var calls = successes.Where(pair => requests.TryGetValue(pair.Key, out var request)
                && string.Equals(request.Request.ToolName, toolId, StringComparison.Ordinal)
                && IsDeclaredCheck(contract, toolId, request.Request.ArgumentsJson))
                .OrderBy(pair => pair.Value.Event.Sequence).ToArray();
            if (calls.Length == 0) continue;
            var call = calls[0];
            var requestEvent = requests[call.Key].Event;
            if (requestEvent.Sequence >= call.Value.Event.Sequence) throw new InvalidDataException("Tool receipt predates its request.");
            evidence.Add(new EvidenceRef(EvidenceKind.ToolBacked,
                contract.Definition.Name + " used " + toolId + (contract.Definition.RequiresEveryTool ? " (All)" : " (Any)"),
                ReceiptRef: new EvidenceReceiptRef(session, call.Value.Event.EventId, call.Key)));
            if (contract.Definition.Stage == WorkflowStage.Verify
                && string.Equals(toolId, "dyn.core.verify_integration", StringComparison.Ordinal))
            {
                verificationReceiptSequence = call.Value.Event.Sequence;
                verificationCallId = call.Key;
            }
        }
        var passed = contract.Definition.RequiresEveryTool
            ? evidence.Count == contract.Definition.RequiredSuccessfulToolIds.Count : evidence.Count > 0;
        if (passed && contract.Definition.Stage == WorkflowStage.Verify
            && (verificationReceiptSequence is null || verificationCallId is null
                || HasWorkspaceMutationSinceVerification(journal, codecs, session, run,
                    verificationReceiptSequence.Value, verificationCallId)))
        {
            passed = false;
            return new(false, evidence,
                "The workspace check is stale: a later or unresolved potentially mutating tool call exists in this Run.");
        }
        return new(passed, evidence,
            passed ? "The declared stage gate has exact successful tool receipt(s)."
                : "Missing successful receipt for required tool(s): "
                    + string.Join(", ", contract.Definition.RequiredSuccessfulToolIds));
    }

    private static bool HasWorkspaceMutationSinceVerification(IReadOnlyList<DomainEvent> journal,
        IEventCodecRegistry codecs, SessionId session, RunId run, long receiptSequence, ToolCallId verifierCall)
    {
        var events = journal.Where(evt => evt.SessionId == session && evt.RunId == run).ToArray();
        var requests = events.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ToolCallRequested)
            .ToDictionary(item => ((ToolCallRequested)item.Payload).ToolCallId,
                item => (item.Event, Request: (ToolCallRequested)item.Payload));
        var starts = events.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ToolCallStarted)
            .GroupBy(item => ((ToolCallStarted)item.Payload).ToolCallId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Event.Sequence).First());
        var terminalSequence = events.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ToolCallSucceeded or ToolCallFailed or ToolCallEffectUnknown
                or ToolCallReconciled or ToolCallCancelled or ToolCallRejected)
            .GroupBy(item => item.Payload switch
            {
                ToolCallSucceeded value => value.ToolCallId,
                ToolCallFailed value => value.ToolCallId,
                ToolCallEffectUnknown value => value.ToolCallId,
                ToolCallReconciled value => value.ToolCallId,
                ToolCallCancelled value => value.ToolCallId,
                ToolCallRejected value => value.ToolCallId,
                _ => throw new InvalidOperationException("Not a terminal tool event."),
            })
            .ToDictionary(group => group.Key, group => group.Min(item => item.Event.Sequence));

        foreach (var pair in requests)
        {
            var callId = pair.Key;
            if (callId == verifierCall) continue;
            var requestSequence = pair.Value.Event.Sequence;
            var potentiallyMutating = IsPotentiallyMutatingTool(pair.Value.Request.ToolName);
            if (requestSequence > receiptSequence && potentiallyMutating) return true;
            if (!starts.TryGetValue(callId, out var started))
            {
                if (potentiallyMutating && requestSequence <= receiptSequence
                    && (!terminalSequence.TryGetValue(callId, out var terminal) || terminal > receiptSequence))
                    return true;
                continue;
            }
            var effect = ((ToolCallStarted)started.Payload).EffectClass;
            if (effect == EffectClass.None) continue;
            if (started.Event.Sequence > receiptSequence
                || !terminalSequence.TryGetValue(callId, out var settled) || settled > receiptSequence)
                return true;
        }
        return false;
    }

    private static bool IsPotentiallyMutatingTool(string toolName) => toolName switch
    {
        "filesystem.read" or "filesystem.search" or "filesystem.list" or "reference.resolve"
            or "core.agents.mailbox.receive" => false,
        _ => true,
    };

    private static bool IsDeclaredCheck(WorkflowStageContract contract, string toolId, string argumentsJson)
    {
        if (contract.Definition.Stage != WorkflowStage.Verify) return true;
        if (!string.Equals(toolId, "dyn.core.verify_integration", StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("executable", out var executable) || executable.GetString() is not { } executableValue
                || !root.TryGetProperty("argv", out var argv) || argv.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("cwd", out var cwd) || cwd.GetString() is not { } cwdValue
                || !root.TryGetProperty("timeoutSeconds", out var timeout) || !timeout.TryGetInt32(out var timeoutSeconds)
                || root.TryGetProperty("networkRequired", out var network)
                    && network.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
                return false;
            if (!string.Equals(executableValue, contract.VerifyExecutable, StringComparison.Ordinal))
                return false;
            using var expectedDocument = JsonDocument.Parse(contract.VerifyArgvJson!);
            var expectedRoot = expectedDocument.RootElement;
            if (expectedRoot.ValueKind != JsonValueKind.Array) return false;
            var expectedArgv = expectedRoot.EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
            return argv.EnumerateArray().Select(value => value.GetString() ?? "")
                .SequenceEqual(expectedArgv, StringComparer.Ordinal)
                && cwdValue == contract.VerifyCwd && timeoutSeconds == 120;
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }
}
