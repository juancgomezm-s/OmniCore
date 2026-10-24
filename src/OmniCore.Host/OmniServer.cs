namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;
using OmniCore.Extensions;
using OmniCore.Memory;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Text.Json;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// OmniServer: sesión de protocolo in-process que expone el runtime detrás de IOmniClient
/// (ADR-0019 §1). Traduce los comandos wire a llamadas del Engine y acumula los eventos wire.
/// En M1 atiende <c>sim</c> y la query <c>state</c>.
/// </summary>
public sealed partial class OmniServer : IOmniClient, ITrustedUserActionClient
{
    private static readonly ConditionalWeakTable<IEventStore, object> ModeAuthorityGates = new();

    private readonly IEventStore _store;

    private readonly object _modeAuthorityMutationGate;

    private readonly IEventCodecRegistry _codecs;

    private readonly IAuditSink _audit;

    private readonly SimulationEngine _engine;

    private readonly List<WireEnvelope> _events = new();

    private SessionId? _lastSessionId;

    private RunId? _lastRunId;

    private ContextSnapshot? _lastSnapshot;

    private string _lastWorkingStateText = "";

    private PromptExpanded? _lastPromptExpanded;

    private WorkflowRequested? _lastWorkflowRequested;

    private WorkflowInvocation? _lastWorkflowInvocation;

    private string? _pendingPromptOrigin;

    private string? _lastForcedCommandTool;

    private readonly CommandService _commandService = new();

    private FakeCatalog? _diagnosticCatalog;

    private ModelCapabilityBoundary? _diagnosticBoundary;

    private RunMode _diagnosticMode = RunMode.Plan;

    /// <summary>
    /// Motivo de un bloqueo de la recuperación del Run real al arrancar (null = recuperación ok o
    /// no aplicable). Visible vía <c>LastRecoveryProblem()</c> y <c>Query("state")</c>. NUNCA se
    /// traga un fallo de recuperación: si la raíz falta/corrompe o el store/reconciliador falla, el
    /// Run queda sin reconciliar y NO continuable automáticamente (ADR-0004 §5).
    /// </summary>
    private string? _recoveryProblem;

    private string? _worktreeMaintenanceProblem;

    private bool _worktreeStartupRecoveryAttempted;
    private bool _writerOwnershipUnavailable;
    private bool _sessionSecurityRecoveryBlocked;
    private bool _writerLeaseRecoveryBlocked;

    private readonly string? _stateFile;

    private readonly IArtifactStore? _artifacts;
    private readonly IWorkspaceMutationHistoryStore? _mutationHistory;
    private readonly ConcurrentDictionary<SessionId, PersistentProcessSessionManager> _processSessions = new();
    private readonly ConcurrentDictionary<(SessionId Session, RunId Run, LaneId Lane, ExecutionId Execution), PersistentProcessSessionManager> _childProcessSessions = new();
    private readonly ConcurrentDictionary<SessionId, PersistentProcessSessionQuota> _processSessionQuotas = new();
    private readonly ConcurrentDictionary<(SessionId Session, RunId Run, LaneId Lane), ExplorerTurn> _activeExplorerTurns = new();
    private readonly ConcurrentDictionary<(SessionId Session, RunId Run),
        Func<(bool? Completed, CommandAck Ack, Exception? Failure)>> _deferredProcessRunCompletions = new();
    private readonly string _hostLeaseGeneration = Guid.NewGuid().ToString("N");

    internal IDisposable RegisterActiveChildExplorerTurn(SessionId session, RunId run, LaneId lane,
        ExplorerTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var key = (session, run, lane);
        if (!_activeExplorerTurns.TryAdd(key, turn))
            throw new InvalidOperationException("active_child_turn_registration_conflict");
        return new ActiveChildExplorerTurnRegistration(this, key, turn);
    }

    private sealed class ActiveChildExplorerTurnRegistration(OmniServer server,
        (SessionId Session, RunId Run, LaneId Lane) key, ExplorerTurn turn) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (server._activeExplorerTurns.TryGetValue(key, out var current) && ReferenceEquals(current, turn))
                server._activeExplorerTurns.TryRemove(key, out _);
        }
    }

    internal PersistentProcessSessionManager ProcessSessions(SessionId session, string? workspaceRoot = null)
    {
        if (_store is SqliteEventStore sqlite && !sqlite.OwnsSessionWriterLease(session))
            throw new SessionWriterLeaseUnavailableException(session);
        return _processSessions.GetOrAdd(session, id =>
        {
            var effectiveWorkspaceRoot = workspaceRoot ?? VerifiedWorkspaceRoot(session) ?? _workspaceRoot ?? string.Empty;
            var manager = new PersistentProcessSessionManager(id, _store, _codecs, _hostLeaseGeneration,
                string.IsNullOrWhiteSpace(effectiveWorkspaceRoot) ? string.Empty : Path.GetFullPath(effectiveWorkspaceRoot), _artifacts,
                () => _store is not SqliteEventStore ownedStore || ownedStore.OwnsSessionWriterLease(id),
                _processSessionQuotas.GetOrAdd(id, _ => new PersistentProcessSessionQuota()),
                () => TrimProcessSessions(id));
            var latest = new Dictionary<string, ProcessSessionStateRecorded>(StringComparer.Ordinal);
            var journal = _store.ReadFrom(id, 1);
            var childExecutions = journal.Select(_codecs.Decode).OfType<AgentExecutionStarted>()
                .Where(item => item.ParentExecutionId is not null).Select(item => item.ExecutionId).ToHashSet();
            foreach (var envelope in journal.Where(evt => evt.ExecutionId is null
                || !childExecutions.Contains(new ExecutionId(evt.ExecutionId.Value))))
                if (_codecs.Decode(envelope) is ProcessSessionStateRecorded evidence)
                    latest[evidence.HandleId] = evidence;
            manager.RestoreUnknown(latest);
            return manager;
        });
    }

    internal PersistentProcessSessionManager ProcessSessionsForChild(SessionId session, RunId run,
        TaskId task, LaneId lane, ExecutionId execution, string physicalWorkspaceRoot,
        TaskPacket packet, WorktreeIdentity? identity)
    {
        if (_store is SqliteEventStore sqlite && !sqlite.OwnsSessionWriterLease(session))
            throw new SessionWriterLeaseUnavailableException(session);
        var root = Path.GetFullPath(physicalWorkspaceRoot);
        var journal = _store.ReadFrom(session, 1);
        var runEvents = journal.Where(evt => evt.RunId == run).ToArray();
        var packetScopeMatches = packet.SchemaVersion == 1 && packet.SessionId == session && packet.RunId == run
            && packet.TaskId == task && packet.LaneId == lane && packet.ExecutionId == execution;
        var packetRootMatches = ProcessPathEquals(packet.WorktreePath, root);
        var workflowBinding = packet.WorkflowImplementation;
        var identityMatches = identity is null || ProcessPathEquals(identity.WorktreePath, root)
            && ProcessPathEquals(identity.RepositoryRoot, packet.RepositoryRoot)
            && ProcessPathEquals(identity.GitCommonDirectory, packet.GitCommonDirectory)
            && identity.OwnershipId == packet.OwnershipId
            && (workflowBinding is null ? identity.LaneId == lane : identity.LaneId == workflowBinding.LaneId
                && identity.OwnershipId == workflowBinding.OwnershipId);
        var worktreePacketMatches = false;
        var ownerBindingMatches = workflowBinding is null;
        foreach (var payload in runEvents.Select(_codecs.Decode))
        {
            if (payload is LaneWorktreeCreated created && created.TaskId == task && created.LaneId == lane
                && created.ExecutionId == execution && created.OwnershipId == packet.OwnershipId
                && ProcessPathEquals(created.WorktreePath, root) && ReadTaskPacket(created.TaskPacketRef) == packet)
            {
                worktreePacketMatches = true;
                break;
            }
            if (payload is LaneWorktreeBound bound && bound.TaskId == task && bound.LaneId == lane
                && bound.ExecutionId == execution && bound.OwnershipId == packet.OwnershipId
                && ProcessPathEquals(bound.WorktreePath, root) && ReadTaskPacket(bound.TaskPacketRef) == packet)
            {
                worktreePacketMatches = true;
                ownerBindingMatches = workflowBinding is { } binding && identity is { } ownerIdentity
                    && bound.OwnerTaskId == binding.TaskId && bound.OwnerLaneId == binding.LaneId
                    && bound.OwnerExecutionId == binding.ExecutionId && bound.OwnershipId == binding.OwnershipId
                    && ownerIdentity.LaneId == binding.LaneId && ownerIdentity.OwnershipId == binding.OwnershipId
                    && bound.WorkflowId == binding.WorkflowId && bound.WorkflowVersion == binding.WorkflowVersion
                    && bound.WorkflowInstance == binding.Instance && bound.SourceTree == binding.Tree
                    && bound.SourceHead == binding.Head && bound.SourceBranch == binding.Branch;
                break;
            }
        }
        var workflowBindingAccepted = workflowBinding is null;
        if (workflowBinding is not null && identity is not null
            && ReadWorkflowStageForTask(session, run, task) is { } verifyContract
            && verifyContract.Definition.Stage == WorkflowStage.Verify && verifyContract.Task == task
            && verifyContract.Lane == lane && verifyContract.Execution == execution)
            workflowBindingAccepted = VerifyAcceptedImplementationBinding(verifyContract, session, run,
                journal, CancellationToken.None);
        var runActive = _lastSessionId == session && _lastRunId == run
            && new RunControlService(_store, _codecs).ActiveRun(session) == run;
        var runState = RunProjection.Replay(session, run, _codecs, runEvents).State;
        var runAvailable = runState is RunState.Running or RunState.AwaitingInput;
        var lanes = LaneProjection.Replay(_codecs, runEvents);
        var laneRunning = lanes.StateOf(lane) == LaneState.Running
            && lanes.ForTask(task).SingleOrDefault(item => item.Id == lane) is { AgentProfile: { } };
        var profileId = lanes.ForTask(task).SingleOrDefault(item => item.Id == lane)?.AgentProfile;
        var executionActive = profileId is { } currentProfile && runEvents.Select(_codecs.Decode)
            .OfType<AgentExecutionStarted>().Count(item => item.ExecutionId == execution
                && item.LaneId == lane && item.ProfileId == currentProfile) == 1
            && !runEvents.Any(evt => evt.ExecutionId == execution
                && _codecs.Decode(evt) is AgentExecutionCompleted or AgentExecutionFailed);
        if (!packetScopeMatches || !packetRootMatches || !identityMatches || !worktreePacketMatches
            || !ownerBindingMatches || !workflowBindingAccepted || !runActive || !runAvailable
            || !laneRunning || !executionActive)
            throw new InvalidOperationException("process.child_scope_unavailable: packetScope=" + packetScopeMatches
                + "; packetRoot=" + packetRootMatches + "; identity=" + identityMatches
                + "; worktreePacket=" + worktreePacketMatches + "; ownerBinding=" + ownerBindingMatches
                + "; workflowBinding=" + workflowBindingAccepted + "; runActive=" + runActive
                + "; runState=" + runState + "; laneRunning=" + laneRunning + "; executionActive=" + executionActive);
        var records = PreM6RecordProjection.Replay(session, _codecs, journal);
        var delegations = records.Records.Values.Where(history => history.Phase == PreM6RecordPhase.Accepted)
            .SelectMany(history => history.Facts.OfType<DelegationCreated>())
            .Select(item => item.Delegation).Where(item => item.ChildTaskId == task && item.ChildLaneId == lane
                && item.ProfileId == profileId).ToArray();
        if (delegations.Length != 1
            || !records.Records.TryGetValue("delegation:" + delegations[0].DelegationId, out var acceptedHistory)
            || acceptedHistory.Facts.OfType<DelegationAccepted>().SingleOrDefault()?.ChildExecutionId != execution)
            throw new InvalidOperationException("process.child_delegation_not_accepted");
        var key = (session, run, lane, execution);
        var manager = _childProcessSessions.GetOrAdd(key, _ =>
        {
            var childManager = new PersistentProcessSessionManager(session, _store, _codecs, _hostLeaseGeneration,
                root, _artifacts, () => _store is not SqliteEventStore ownedStore || ownedStore.OwnsSessionWriterLease(session),
                _processSessionQuotas.GetOrAdd(session, _ => new PersistentProcessSessionQuota()),
                () => TrimProcessSessions(session), enforceChildControlProcessScope: true);
            var latest = new Dictionary<string, ProcessSessionStateRecorded>(StringComparer.Ordinal);
            foreach (var envelope in journal.Where(evt => evt.RunId == run && evt.TaskId == task
                && evt.LaneId == lane && evt.ExecutionId == execution))
                if (_codecs.Decode(envelope) is ProcessSessionStateRecorded state) latest[state.HandleId] = state;
            using (ExecutionScope.Begin(new ExecutionScopeState(run, task, lane, ExecutionId: execution)))
                childManager.RestoreUnknown(latest);
            return childManager;
        });
        if (!manager.UsesWorkspaceRoot(root))
            throw new InvalidOperationException("process.child_workspace_changed");
        return manager;
    }

    internal void StopProcessesForChild(SessionId session, RunId run, LaneId lane, ExecutionId execution)
    {
        if (_childProcessSessions.TryGetValue((session, run, lane, execution), out var manager))
        {
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run,
                TaskForLane(session, run, lane), lane, ExecutionId: execution));
            manager.StopRunAsync(run.ToString(), null).GetAwaiter().GetResult();
        }
    }

    private TaskId? TaskForLane(SessionId session, RunId run, LaneId lane) => _store.ReadFrom(session, 1)
        .Where(evt => evt.RunId == run).Select(_codecs.Decode).OfType<LaneCreated>()
        .SingleOrDefault(item => item.LaneId == lane)?.TaskId;

    private static bool ProcessPathEquals(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void TrimProcessSessions(SessionId session)
    {
        if (_processSessions.TryGetValue(session, out var root)) root.RecycleTerminalProcesses(force: true);
        foreach (var pair in _childProcessSessions.Where(item => item.Key.Session == session))
            pair.Value.RecycleTerminalProcesses(force: true);
    }

    internal bool CanExecuteNativeProcessControlDuringTurn(SessionId session, RunId run,
        string toolName, string argumentsJson) =>
        CanExecuteNativeProcessControl(session, run, toolName, argumentsJson);

    internal bool IsChildProcessHandle(SessionId session, RunId run, string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("handle", out var handleNode)
                || handleNode.ValueKind != JsonValueKind.String || handleNode.GetString() is not { } handle) return false;
            var journal = _store.ReadFrom(session, 1);
            var state = journal.Where(evt => evt.RunId == run)
                .Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
                .LastOrDefault(item => item.Payload is ProcessSessionStateRecorded process
                    && process.HandleId == handle && process.RunId == run.ToString());
            if (state.Payload is not ProcessSessionStateRecorded processState
                || state.Event.ExecutionId is not { } execution || state.Event.LaneId is not { } lane
                || processState.LaneId != lane.ToString()) return false;
            return journal.Where(evt => evt.RunId == run).Select(_codecs.Decode).OfType<AgentExecutionStarted>()
                .Any(started => started.ExecutionId == execution && started.LaneId == lane
                    && started.ParentExecutionId is not null);
        }
        catch (JsonException) { return false; }
    }

    internal bool CanExecuteNativeProcessControl(SessionId session, RunId run, string toolName, string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("handle", out var handleNode)
                || handleNode.ValueKind != JsonValueKind.String || handleNode.GetString() is not { Length: > 0 } handle)
                return false;
            if (TryGetNativeChildProcessOwner(session, run, handle, toolName, out var childLane, out _))
                return _activeExplorerTurns.ContainsKey((session, run, childLane));
            if (!TryGetNativeProcessRootLane(session, run, out var lane)
                || !_processSessions.TryGetValue(session, out var manager)
                || !manager.CanControlOwnedProcess(handle, run.ToString(), lane.ToString(), toolName)) return false;
            if (_activeExplorerTurns.ContainsKey((session, run, lane))) return true;
            return NativeProcessControlRunIsQuiescent(session, run, lane, manager)
                && new RunControlService(_store, _codecs).UnreconciledEffects(session).Count == 0;
        }
        catch (JsonException) { return false; }
    }

    private bool TryGetNativeChildProcessOwner(SessionId session, RunId run, string handle, string toolName,
        out LaneId lane, out ExecutionId execution)
    {
        lane = default!; execution = default!;
        if (_lastSessionId != session || _lastRunId != run
            || new RunControlService(_store, _codecs).ActiveRun(session) != run) return false;
        var matches = _childProcessSessions.Where(pair => pair.Key.Session == session && pair.Key.Run == run
            && pair.Value.CanControlOwnedProcess(handle, run.ToString(), pair.Key.Lane.ToString(), toolName)).ToArray();
        if (matches.Length != 1) return false;
        var key = matches[0].Key;
        var journal = _store.ReadFrom(session, 1);
        if (AcceptedDelegationExecutionForChild(session, run, TaskForLane(session, run, key.Lane), key.Lane) != key.Execution
            || journal.Any(evt => evt.RunId == run && evt.ExecutionId == key.Execution
                && _codecs.Decode(evt) is AgentExecutionCompleted or AgentExecutionFailed)) return false;
        lane = key.Lane; execution = key.Execution;
        return true;
    }

    private ExecutionId? AcceptedDelegationExecutionForChild(SessionId session, RunId run, TaskId? task, LaneId lane)
    {
        if (task is not { } childTask || AcceptedDelegationForChild(session, run, childTask, lane) is not { } delegationId) return null;
        var history = PreM6RecordProjection.Replay(session, _codecs, _store.ReadFrom(session, 1));
        return history.Records.TryGetValue("delegation:" + delegationId, out var record)
            && record.Phase == PreM6RecordPhase.Accepted
            ? record.Facts.OfType<DelegationAccepted>().SingleOrDefault()?.ChildExecutionId : null;
    }

    private bool TryGetNativeProcessRootLane(SessionId session, RunId run, out LaneId lane)
    {
        lane = default!;
        if (_lastSessionId != session || _lastRunId != run
            || new RunControlService(_store, _codecs).ActiveRun(session) != run) return false;
        var own = EventsForRun(_store.ReadFrom(session, 1), run);
        var projection = RunProjection.Replay(session, run, _codecs, own);
        if (projection.State != RunState.Running || projection.RootTask is not { } rootTask || HasPendingInteraction(own))
            return false;
        var rootLanes = LaneProjection.Replay(_codecs, own).ForTask(rootTask)
            .Where(item => item.State == LaneState.Running).ToArray();
        if (rootLanes.Length != 1) return false;
        lane = rootLanes[0].Id;
        return true;
    }

    internal int ExecuteNativeProcessControlDuringTurn(SessionId session, RunId run, string toolName,
        string argumentsJson, string invocationId, CancellationToken cancellationToken,
        bool nativeChoiceCapability)
    {
        if (_lastSessionId != session || _lastRunId != run
            || new RunControlService(_store, _codecs).ActiveRun(session) != run)
            return 6;
        var own = EventsForRun(_store.ReadFrom(session, 1), run);
        var projection = RunProjection.Replay(session, run, _codecs, own);
        if (projection.State != RunState.Running || projection.RootTask is not { } rootTask) return 6;
        using var document = JsonDocument.Parse(argumentsJson);
        var handle = document.RootElement.GetProperty("handle").GetString()!;
        if (TryGetNativeChildProcessOwner(session, run, handle, toolName, out var childLane, out var childExecution))
        {
            if (!_activeExplorerTurns.TryGetValue((session, run, childLane), out var childTurn)) return 6;
            var childResult = childTurn.ExecuteNativeToolAction(toolName, argumentsJson, invocationId, session, run,
                childLane, cancellationToken, allowOpenTurn: true, requireExistingTurn: true,
                allowDeferredInteraction: nativeChoiceCapability, expectedExecutionId: childExecution);
            if (childResult.FailureDiagnostic == "native_process_turn_closed_before_effect") return 6;
            if (childResult.PendingInteractionId is not null) return 3;
            return childResult.StopReason == StopReason.EndTurn && childResult.ToolCalls.All(item => item.Succeeded) ? 0 : 1;
        }
        var lanes = LaneProjection.Replay(_codecs, own).ForTask(rootTask)
            .Where(lane => lane.State == LaneState.Running).ToArray();
        if (lanes.Length != 1 || !_activeExplorerTurns.TryGetValue((session, run, lanes[0].Id), out var turn)) return 6;
        var result = turn.ExecuteNativeToolAction(toolName, argumentsJson, invocationId, session, run,
            lanes[0].Id, cancellationToken, allowOpenTurn: true);
        if (result.FailureDiagnostic == "native_process_turn_closed_before_effect") return 6;
        if (result.PendingInteractionId is not null) return 3;
        return result.StopReason == StopReason.EndTurn && result.ToolCalls.All(item => item.Succeeded) ? 0 : 1;
    }

    internal DelegationId? AcceptedDelegationForChild(SessionId session, RunId run, TaskId task, LaneId lane)
    {
        if (_lastSessionId != session || _lastRunId != run
            || new RunControlService(_store, _codecs).ActiveRun(session) != run) return null;
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        if (RunProjection.Replay(session, run, _codecs, own).State is not (RunState.Running or RunState.AwaitingInput)
            || LaneProjection.Replay(_codecs, own).StateOf(lane) != LaneState.Running) return null;
        var records = PreM6RecordProjection.Replay(session, _codecs, journal);
        var matches = records.Records.Values
            .Where(history => history.Phase == PreM6RecordPhase.Accepted)
            .SelectMany(history => history.Facts.OfType<DelegationCreated>())
            .Select(evt => evt.Delegation)
            .Where(delegation => delegation.ChildTaskId == task && delegation.ChildLaneId == lane)
            .ToArray();
        return matches.Length == 1 ? matches[0].DelegationId : null;
    }

    internal async Task StopProcessesForRunAsync(SessionId session, RunId run)
    {
        if (_processSessions.TryGetValue(session, out var manager))
            await manager.StopRunAsync(run.ToString(), null).ConfigureAwait(false);
        foreach (var pair in _childProcessSessions.Where(item => item.Key.Session == session
            && item.Key.Run == run).ToArray())
        {
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run,
                TaskForLane(session, run, pair.Key.Lane), pair.Key.Lane, ExecutionId: pair.Key.Execution));
            await pair.Value.StopRunAsync(run.ToString(), null).ConfigureAwait(false);
        }
    }

    internal void ResumeDeferredProcessRunCompletion(SessionId session, RunId run)
    {
        if (_lastSessionId != session || _lastRunId != run
            || new RunControlService(_store, _codecs).ActiveRun(session) != run
            || _processSessions.TryGetValue(session, out var manager)
                && manager.HasUnresolvedForRun(run.ToString())
            || _childProcessSessions.Any(pair => pair.Key.Session == session && pair.Key.Run == run
                && pair.Value.HasUnresolvedForRun(run.ToString()))) return;
        if (_deferredProcessRunCompletions.TryRemove((session, run), out var resume))
            _ = resume();
    }

    internal bool IsProcessRunCompletionDeferred(SessionId session, RunId run) =>
        _deferredProcessRunCompletions.ContainsKey((session, run))
        && _processSessions.TryGetValue(session, out var manager)
        && manager.HasUnresolvedForRun(run.ToString());

    internal ArtifactRef ImportUserAttachment(byte[] bytes, string fileName)
    {
        if (_artifacts is not IBinaryArtifactStore binary)
            throw new InvalidOperationException("Binary attachment storage is unavailable.");
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 260)
            throw new InvalidDataException("Attachment filename is invalid.");
        var safeName = Path.GetFileName(fileName.Replace('\\', '/'));
        if (safeName.Length is < 1 or > 180 || safeName.Any(char.IsControl))
            throw new InvalidDataException("Attachment filename is invalid.");
        var mediaType = AttachmentContentMaterializer.DetectMediaType(bytes);
        AttachmentContentMaterializer.ValidateBeforePublication(bytes, safeName);
        var original = binary.PutBytes(bytes, mediaType, ArtifactKind.Other, Sensitivity.Sensitive) with { FileName = safeName };
        if (_artifacts is null) return original;
        var extracted = AttachmentContentMaterializer.CreateExtractedTextSnapshot(original, bytes, _artifacts);
        return extracted is null ? original : original with
        {
            ExtractedTextSnapshot = extracted.Value.Snapshot,
            ExtractorId = extracted.Value.ExtractorId,
        };
    }
    private IPlatformPaths? _sidebarPaths;
    public void ConfigureSidebarPaths(IPlatformPaths paths) => _sidebarPaths = paths;
    private SidebarConfiguration SidebarSettings() => new(_sidebarPaths ?? OmniHost.CreatePlatformPaths(), _workspaceRoot ?? Environment.CurrentDirectory);
    private readonly UserWorkspaceSpendReader? _userSpendReader;
    private readonly string? _userDatabasePath;
    public SessionObservationHub Observability { get; }

    private string? _workspaceRoot;
    private Func<IPlatformPaths> _platformPathsFactory = () => OmniHost.CreatePlatformPaths();
    private Func<string> _externalAgentActiveDirectory = () => Directory.GetCurrentDirectory();
    private Func<ExternalAgentDiscoveryCatalog> _externalAgentCatalogFactory = static () => new();
    private Func<IExtensionRpcInvoker> _lifecycleRpcInvokerFactory = static () => new ExtensionRpcProcessInvoker();
    private readonly Func<IPlatformPaths, OpenRouterConnectionService>? _openRouterServiceFactory;
    private SessionRoutingPolicy _newSessionRoutingPolicy = SessionRoutingPolicy.Empty();
    private AgentProfileConfiguration.Loaded _agentProfiles = new(new AgentProfileRegistry([]), null);

    private bool SourceModelAllowsOrchestration(SessionId session, IReadOnlyList<DomainEvent> journal,
        ModelStepStarted step)
    {
        if (ReadSessionSecurityPolicy(session) is not { AllowOrchestration: true }) return false;
        if (step.RouteId is null) return false;
        var routing = journal.Where(evt => evt.SessionId == session)
            .OrderBy(evt => evt.Sequence).Select(_codecs.Decode)
            .Select(payload => payload switch
            {
                SessionRoutingPolicySet set => set.Policy,
                SessionRoutingPolicyRevised revised => revised.Policy,
                _ => null,
            }).LastOrDefault(policy => policy is not null);
        var route = routing?.AllowedRoutes.SingleOrDefault(candidate => candidate.RouteId.Equals(step.RouteId));
        if (route is null) return false;
        try
        {
            using var policies = new SqliteModelPolicyStore(_platformPathsFactory().UserDatabasePath);
            return policies.Get(ModelPolicyKey.For(route.ProviderId, step.ModelId), CancellationToken.None)
                ?.Policy.AllowOrchestration == true;
        }
        catch (Exception) { return false; }
    }

    private ModelStepStarted? DelegationSourceModelStep(IReadOnlyList<DomainEvent> journal, Delegation delegation)
    {
        if (_artifacts is null || !Enum.IsDefined(delegation.PacketRef.Kind)
            || !_artifacts.Verify(delegation.PacketRef.Hash, delegation.PacketRef.Size)) return null;
        try
        {
            var text = _artifacts.GetText(delegation.PacketRef.Hash);
            if (text is null) return null;
            var packet = System.Text.Json.JsonSerializer.Deserialize(text,
                DelegationContextJson.Default.DelegationContextPacket);
            if (packet is null || packet.SessionId != _lastSessionId || packet.RunId != _lastRunId
                || packet.Target.DelegationId != delegation.DelegationId
                || packet.Target.ParentExecutionId != delegation.ParentExecutionId
                || packet.SourceEvent is null) return null;
            var source = journal.SingleOrDefault(evt => evt.SessionId == packet.SourceEvent.SessionId
                && evt.EventId == packet.SourceEvent.EventId && evt.Sequence == packet.SourceSequence);
            return source is not null && _codecs.Decode(source) is ModelStepStarted step
                && source.ExecutionId == delegation.ParentExecutionId ? step : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException
            or InvalidOperationException or ArgumentException)
        { return null; }
    }

    internal void ConfigureAgentProfiles(AgentProfileConfiguration.Loaded profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _agentProfiles = profiles;
    }

    internal AgentProfile? ResolveLaneAgentProfile(SessionId sessionId, RunId runId, LaneId laneId)
    {
        var lane = new EventStream(_store, _codecs, sessionId).EventsSince(1)
            .Where(evt => evt.RunId == runId).Select(_codecs.Decode)
            .OfType<LaneCreated>().SingleOrDefault(created => created.LaneId == laneId)
            ?? throw new InvalidOperationException("Cannot resolve the requested Run/Lane configuration.");
        if (lane.AgentProfileRevision is null && lane.AgentProfileHash is null) return null;
        if (lane.AgentProfileRevision is null || lane.AgentProfileHash is null)
            throw new InvalidOperationException("Incomplete durable AgentProfile binding.");
        var profile = _agentProfiles.Registry.Find(lane.AgentProfile)
            ?? throw new InvalidOperationException("The Lane's configured AgentProfile is unavailable.");
        if (profile.Revision != lane.AgentProfileRevision || AgentProfileFingerprint.Hash(profile) != lane.AgentProfileHash)
            throw new InvalidOperationException("The Lane's configured AgentProfile has changed; execution is blocked.");
        return profile;
    }

    internal void ConfigureNewSessionRoutingPolicy(SessionRoutingPolicy policy)
    {
        var frozen = policy.Freeze();
        if (frozen.Revision != 1 || frozen.BillingPolicy.Any(mode => mode is BillingMode.Unknown or BillingMode.MeteredCurrency))
            throw new ArgumentException("Initial routing policy cannot grant unconsented metered or unknown routes");
        _newSessionRoutingPolicy = frozen;
    }

    public void ConfigureWorkspaceRoot(string workspaceRoot)
    {
        if (_workspaceRoot != Path.GetFullPath(workspaceRoot)) InvalidateExtensionContextScope();
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        // A host can start without a last-session pointer (first launch, or a removed state
        // file). Once the caller supplies its physically established workspace, recovery may
        // still find old provisioning failures in that workspace's durable journal history.
        // The sweeper independently verifies the root binding and ownership before any cleanup.
        if (!_writerOwnershipUnavailable) RecoverExpiredWorktreesAtStartup();
        if (!_writerOwnershipUnavailable) RecoverAlternativePreparations();
    }

    internal void ConfigurePlatformPaths(Func<IPlatformPaths> factory) =>
        _platformPathsFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    internal void ConfigureExternalAgentDiscovery(Func<string> activeDirectory,
        Func<ExternalAgentDiscoveryCatalog>? catalogFactory = null)
    {
        _externalAgentActiveDirectory = activeDirectory ?? throw new ArgumentNullException(nameof(activeDirectory));
        _externalAgentCatalogFactory = catalogFactory ?? (static () => new ExternalAgentDiscoveryCatalog());
    }

    internal void ConfigureLifecycleRpcInvoker(Func<IExtensionRpcInvoker> factory) =>
        _lifecycleRpcInvokerFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit,
        IArtifactStore? artifacts = null, UserWorkspaceSpendReader? userSpendReader = null,
        Func<IPlatformPaths>? platformPathsFactory = null,
        Func<IPlatformPaths, OpenRouterConnectionService>? openRouterServiceFactory = null,
        IWorkspaceMutationHistoryStore? mutationHistory = null)
    {
        _ = SecretRedactor.Shared;
        _store = store;
        _modeAuthorityMutationGate = ModeAuthorityGates.GetValue(store, static _ => new object());
        _codecs = codecs;
        _audit = audit;
        _artifacts = artifacts;
        _mutationHistory = mutationHistory;
        _platformPathsFactory = platformPathsFactory ?? (() => OmniHost.CreatePlatformPaths());
        _lifecycleRpcInvokerFactory = CreateExtensionInvoker;
        _openRouterServiceFactory = openRouterServiceFactory;
        _userSpendReader = userSpendReader;
        _userDatabasePath = userSpendReader is null ? null : Path.Combine(userSpendReader.UserDataDirectory, "user.db");
        Observability = new SessionObservationHub(store, codecs, artifacts);
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = null;
        LoadLastSession();
        RehydrateUnderWriterOwnership();
        // Recuperación del Run real (ADR-0004 §5): idempotente, no-op sin run persistido.
        if (!_writerOwnershipUnavailable)
        {
            RecoverPendingEffects();
            RecoverPendingAgentOrchestration();
            RecoverExpiredWorktreesAtStartup();
        }
    }

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit, string stateFile,
        IArtifactStore? artifacts = null, UserWorkspaceSpendReader? userSpendReader = null,
        Func<IPlatformPaths>? platformPathsFactory = null,
        Func<IPlatformPaths, OpenRouterConnectionService>? openRouterServiceFactory = null,
        IWorkspaceMutationHistoryStore? mutationHistory = null)
    {
        _ = SecretRedactor.Shared;
        _store = store;
        _modeAuthorityMutationGate = ModeAuthorityGates.GetValue(store, static _ => new object());
        _codecs = codecs;
        _audit = audit;
        _artifacts = artifacts;
        _mutationHistory = mutationHistory;
        _platformPathsFactory = platformPathsFactory ?? (() => OmniHost.CreatePlatformPaths());
        _lifecycleRpcInvokerFactory = CreateExtensionInvoker;
        _openRouterServiceFactory = openRouterServiceFactory;
        _userSpendReader = userSpendReader;
        _userDatabasePath = userSpendReader is null ? null : Path.Combine(userSpendReader.UserDataDirectory, "user.db");
        Observability = new SessionObservationHub(store, codecs, artifacts);
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = stateFile;
        LoadLastSession();
        RehydrateUnderWriterOwnership();
        // Recuperación del Run real (ADR-0004 §5): idempotente, no-op sin run persistido.
        if (!_writerOwnershipUnavailable)
        {
            RecoverPendingEffects();
            RecoverPendingAgentOrchestration();
            RecoverExpiredWorktreesAtStartup();
        }
    }

    internal IWorkspaceMutationHistoryStore? MutationHistoryStore => _mutationHistory;

    /// <summary>Composición común: tools Core + defaults por modo (modo Act del sim).</summary>
    internal static SimulationEngine BuildEngine(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit) =>
        new SimulationEngine(store, codecs, audit, ScriptedToolExecutor.WithCoreTools(
            HostTools.Default().Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act)));

    private void LoadLastSession()
    {
        if (_stateFile is null || !File.Exists(_stateFile!))
        {
            return;
        }

        var text = File.ReadAllText(_stateFile!);
        var sep = text.IndexOf('\n');
        if (sep <= 0)
        {
            return;
        }

        _lastSessionId = SessionId.Parse(text.Substring(0, sep));
        _lastRunId = text[(sep + 1)..].Trim() == "-" ? null : RunId.Parse(text.Substring(sep + 1));

        // Reconstruir el snapshot desde el journal (P0-3): una nueva invocación recupera el
        // contexto que el run de otra invocación materializó, sin re-ejecutar el sim.
        if (_store is not SqliteEventStore && _lastRunId is not null) _lastSnapshot = MaterializeFromJournal(_lastSessionId!, _lastRunId);
    }

    private void RehydrateUnderWriterOwnership()
    {
        if (_store is not SqliteEventStore) return;
        if (!EnsureWriterOwnershipForCommand())
        {
            // Query-only hosts may observe the existing journal, but never run recovery or
            // mutate a projection while another process owns this Session.
            _writerOwnershipUnavailable = true;
            _lastSnapshot = null;
        }
    }

    private void ReconcileObsoleteMonetaryBudget()
    {
        if (_lastSessionId is { } session)
            ObsoleteMonetaryBudgetRecovery.Reconcile(_store, _codecs, session);
    }

    internal SqliteEventStore? ReceiptStore => _store as SqliteEventStore;
    internal bool SessionSecurityRecoveryBlocked => _sessionSecurityRecoveryBlocked;

    internal IDisposable AcquireSessionSecurityAdmission()
    {
        Monitor.Enter(_modeAuthorityMutationGate);
        return new MonitorLock(_modeAuthorityMutationGate);
    }

    private sealed class MonitorLock(object gate) : IDisposable
    {
        private object? _gate = gate;
        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _gate, null);
            if (held is not null) Monitor.Exit(held);
        }
    }

    internal void ClosePersistentStore()
    {
        foreach (var sessions in _processSessions.Values)
            CompleteWorkspaceTask(async () => await sessions.StopAllAsync().ConfigureAwait(false));
        foreach (var pair in _childProcessSessions)
        {
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(pair.Key.Run,
                TaskForLane(pair.Key.Session, pair.Key.Run, pair.Key.Lane), pair.Key.Lane,
                ExecutionId: pair.Key.Execution));
            CompleteWorkspaceTask(async () => await pair.Value.StopAllAsync().ConfigureAwait(false));
        }
        _processSessions.Clear();
        _childProcessSessions.Clear();
        foreach (var process in _extensionProcesses.Values)
            CompleteWorkspaceTask(async () => await process.DisposeAsync().ConfigureAwait(false));
        _extensionProcesses.Clear();
        (_store as SqliteEventStore)?.Close();
        if (_mutationHistory is IDisposable disposable) disposable.Dispose();
    }

    internal bool EnsureWriterOwnershipForCommand()
    {
        if (_store is not SqliteEventStore store) return true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = ReadSessionPointer();
            if (before is { } pointer && pointer.Session is { } target)
            {
                var acquiredTargetLease = false;
                var targetPublished = false;
                try
                {
                    acquiredTargetLease = store.AcquireSessionWriterLease(target);
                    if (ReadSessionPointer() != before) continue;
                    lock (_modeAuthorityMutationGate)
                    {
                        // A recovered Session becomes writable only after its durable ceiling is
                        // present. Keep this migration on lease recovery, never on a read query.
                        if (ReadSessionPointer() != before) continue;
                        _ = ProcessSessions(target); // Resolve interrupted process effects only after this host owns the Session lease.
                        EnsureSessionSecurityPolicy(target);
                        _lastSessionId = target;
                        _lastRunId = pointer.Run;
                        _lastSnapshot = pointer.Run is { } run ? MaterializeFromJournal(target, run) : null;
                        targetPublished = true;
                    }
                }
                catch (SessionWriterLeaseUnavailableException)
                {
                    _writerOwnershipUnavailable = true;
                    _sessionSecurityRecoveryBlocked = false;
                    _writerLeaseRecoveryBlocked = true;
                    _recoveryProblem = "session writer ownership is unavailable";
                    return false;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException or InvalidDataException)
                {
                    _writerOwnershipUnavailable = true;
                    _writerLeaseRecoveryBlocked = false;
                    _sessionSecurityRecoveryBlocked = true;
                    _recoveryProblem = "session security initialization is blocked: " + exception.GetType().Name;
                    return false;
                }
                finally
                {
                    if (acquiredTargetLease && !targetPublished) store.ReleaseSessionWriterLease(target);
                }
            }
            else if (_stateFile is not null)
            {
                if (ReadSessionPointer() is not null) continue;
                _lastSessionId = null;
                _lastRunId = null;
                _lastSnapshot = null;
            }
            else if (_lastSessionId is { } existing)
            {
                var acquiredExistingLease = false;
                var existingPublished = false;
                try
                {
                    acquiredExistingLease = store.AcquireSessionWriterLease(existing);
                    lock (_modeAuthorityMutationGate)
                    {
                        _ = ProcessSessions(existing);
                        EnsureSessionSecurityPolicy(existing);
                        _lastSnapshot = _lastRunId is { } run ? MaterializeFromJournal(existing, run) : null;
                        existingPublished = true;
                    }
                }
                catch (SessionWriterLeaseUnavailableException)
                {
                    _writerOwnershipUnavailable = true;
                    _sessionSecurityRecoveryBlocked = false;
                    _writerLeaseRecoveryBlocked = true;
                    _recoveryProblem = "session writer ownership is unavailable";
                    return false;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException or InvalidDataException)
                {
                    _writerOwnershipUnavailable = true;
                    _writerLeaseRecoveryBlocked = false;
                    _sessionSecurityRecoveryBlocked = true;
                    _recoveryProblem = "session security initialization is blocked: " + exception.GetType().Name;
                    return false;
                }
                finally
                {
                    if (acquiredExistingLease && !existingPublished) store.ReleaseSessionWriterLease(existing);
                }
            }
            _writerOwnershipUnavailable = false;
            if (_sessionSecurityRecoveryBlocked || _writerLeaseRecoveryBlocked)
            {
                _recoveryProblem = null;
                _sessionSecurityRecoveryBlocked = false;
                _writerLeaseRecoveryBlocked = false;
            }
            return true;
        }
        return false;
    }

    internal CommandAck WriterOwnershipFailureAck(string messageId) => _sessionSecurityRecoveryBlocked
        ? new CommandAck(messageId, "error", "session_security.recovery_blocked", RuntimeCommandOutcome.Rejected())
        : new CommandAck(messageId, "busy", "session_writer_owned", RuntimeCommandOutcome.Deferred("session_writer_owned"));

    private (SessionId? Session, RunId? Run)? ReadSessionPointer()
    {
        if (_stateFile is null || !File.Exists(_stateFile)) return null;
        var text = File.ReadAllText(_stateFile);
        var separator = text.IndexOf('\n');
        if (separator <= 0 || separator == text.Length - 1)
            throw new InvalidDataException("The durable session pointer is malformed.");
        try { return (SessionId.Parse(text[..separator]), text[(separator + 1)..].Trim() == "-" ? null : RunId.Parse(text[(separator + 1)..].TrimEnd('\r', '\n'))); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The durable session pointer is malformed.", exception);
        }
    }

    /// <summary>
    /// Reconstruye el ContextSnapshot del último run desde el store persistente (replay del
    /// journal vía las proyecciones, igual que MaterializeSnapshot pero sin RunResult vivo).
    /// Devuelve null si el journal no tiene eventos del run.
    /// </summary>
    private ContextSnapshot? MaterializeFromJournal(SessionId sessionId, RunId runId)
    {
        try
        {
            var tail = EventsForRun(_store.ReadFrom(sessionId, 1), runId);
            if (tail.Count == 0)
            {
                return null;
            }

            var runProj = OmniCore.Engine.RunProjection.Replay(sessionId, runId, _codecs, tail);
            var planProj = OmniCore.Engine.PlanProjection.Replay(_codecs, tail);
            var workingState = OmniCore.Engine.WorkingStateProjector.Project(runProj, planProj);
            if (workingState is null)
            {
                return null;
            }

            _lastWorkingStateText = OmniCore.Engine.WorkingStateProjector.Render(workingState!);
            return MaterializeSnapshot(
                new SimulationEngine.RunResult(sessionId, runId, 0, new string[0], runProj, planProj,
                    OmniCore.Engine.TaskGraphProjection.Replay(_codecs, tail),
                    OmniCore.Engine.LaneProjection.Replay(_codecs, tail), workingState));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveLastSession()
    {
        if (_stateFile is null || _lastSessionId is null)
        {
            return;
        }

        var temporary = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, _lastSessionId.ToString() + "\n" + (_lastRunId?.ToString() ?? "-")); File.Move(temporary, _stateFile, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// Recuperación del Run real al arrancar el Host (ADR-0004 §5, ADR-0041 §2): si hay un run
    /// persistido reanudable, detecta sus ToolCalls <c>Started</c>-sin-outcome (crash) y las
    /// reconcilia con el <c>FilesystemReconciler</c> REAL contra la RAÍZ DURADERA Y VERIFICADA del
    /// workspace del run (el evento <c>WorkspaceRootEstablished</c>, único origen aceptado). NO usa
    /// el executor del sim: un <c>RunResumeService</c> dedicado, idempotente, que rechaza Runs
    /// terminales y nunca re-ejecuta la tool ni re-infere la respuesta.
    ///
    /// Contrato de seguridad (corrección del bloqueo previo a cwd + catch): NUNCA se sustituye una
    /// raíz faltante/corrompida por el cwd del proceso ni se acepta una ruta de display como
    /// autoridad; sin raíz durable verificada o si el store/reconciliador falla, la recuperación
    /// queda BLOQUEADA — visible (<c>_recoveryProblem</c>) y sin reconciliar ni clasificar Applied—
    /// y el Run NO se continúa automáticamente. Los fallos se exponen, no se tragan.
    /// </summary>
    private HostRecoveryResult RecoverPendingEffects()
    {
        try
        {
            // Budget migration is recovery too: a read/decode/Barrier failure must remain
            // visible and block continuation, not escape construction or be cleared below.
            ReconcileObsoleteMonetaryBudget();
        }
        catch (Exception failure)
        {
            return BlockWith("no se pudo recuperar el presupuesto de la sesión (" + failure.Message
                + "): recuperación bloqueada, sin continuar automáticamente");
        }
        if (_lastSessionId is null || _lastRunId is null)
        {
            // Sin run persistido no hay nada que recuperar; estado limpio.
            _recoveryProblem = null;
            return HostRecoveryResult.Ok(0);
        }

        try
        {
            // Sin efectos laterales pendientes no hay nada que reconciliar: no se exige raíz
            // (p. ej. tras un sim con tools falsas que nunca estableció workspace).
            var withoutReconciler = new RunResumeService(_store, _codecs, null, "");
            if (!withoutReconciler.HasPendingSideEffects(_lastSessionId!))
            {
                // Aun así se cierran las lecturas interrumpidas (EffectClass.None): no necesitan
                // reconciliador ni raíz, y si no quedarían en Started para siempre.
                withoutReconciler.Resume(_lastSessionId!, _lastRunId!);
                _recoveryProblem = null;
                return HostRecoveryResult.Ok(0);
            }
        }
        catch (Exception ex)
        {
            return BlockWith("no se pudo inspeccionar el journal del run (" + (ex.Message ?? "?") +
                "): recuperación bloqueada, sin re-ejecutar ni clasificar Applied");
        }

        string? root;
        try
        {
            root = VerifiedWorkspaceRoot(_lastSessionId!);
        }
        catch (Exception ex)
        {
            // Error del journal/resolución al leer la raíz: se expone y se bloquea, sin reconciliar.
            return BlockWith("no se pudo verificar la raíz durable del run (" + (ex.Message ?? "?") +
                "): recuperación bloqueada, sin re-ejecutar ni clasificar Applied");
        }

        if (root is null)
        {
            // Sin autoridad de paths no se inspecciona el workspace. Se cierra conservadoramente
            // como Unresolvable y se publica la InteractionRequest humana; nunca se clasifica
            // Applied ni se re-ejecuta, conforme a ADR-0004 §5bis.
            try
            {
                new RunResumeService(_store, _codecs, null, "").Resume(_lastSessionId!, _lastRunId!);
            }
            catch (Exception)
            {
                // El diagnóstico de bloqueo de raíz sigue siendo válido; el journal tampoco se
                // puede avanzar con seguridad si la publicación/consolidación falla.
            }
            return BlockWith("sin raíz de workspace durable y verificada: recuperación bloqueada," +
                " el run NO se continúa automáticamente; el efecto no verificable queda para resolución humana");
        }

        var service = new RunResumeService(_store, _codecs,
            new WorktreeIntegrationAwareReconciler(root!, _artifacts,
                new FilesystemReconciler(new PathBoundaryValidator())), root!,
            ResolveLaneWorktreeRootForRecovery, ResolveLaneWorktreeBoundRootForRecovery);
        try
        {
            var n = service.Resume(_lastSessionId!, _lastRunId!);
            _recoveryProblem = null;
            return HostRecoveryResult.Ok(n);
        }
        catch (Exception ex)
        {
            // Error del journal/reconciliador DURANTE la recuperación: no se oculta. El run queda
            // sin continuar automáticamente y sin clasificar Applied para el trabajo no resuelto.
            return BlockWith("fallo de recuperación del run (" + (ex.Message ?? "?") +
                "): el Run queda sin continuar automáticamente, sin re-ejecutar ni clasificar Applied");
        }
    }

    /// <summary>Registra un bloqueo visible y lo devuelve como resultado de recuperación.</summary>
    private HostRecoveryResult BlockWith(string reason)
    {
        _recoveryProblem = reason;
        return HostRecoveryResult.Blocked(reason);
    }

    /// <summary>Runs bounded seven-day cleanup only after normal Run/lane recovery is clear and
    /// the workspace root is revalidated from the journal. Maintenance failure is observable but
    /// never weakens or blocks ordinary tool authorization.</summary>
    private void RecoverExpiredWorktreesAtStartup()
    {
        if (_worktreeStartupRecoveryAttempted || _recoveryProblem is not null
            || _store is not IWorkspaceJournalReader) return;
        string? root;
        if (_lastSessionId is { } session)
        {
            // Once a session exists, only its durable root binding is authoritative. Never
            // substitute a later configured cwd if that binding is missing or invalid.
            root = VerifiedWorkspaceRoot(session);
            if (root is null) { _worktreeStartupRecoveryAttempted = true; return; }
        }
        else
        {
            // ConfigureWorkspaceRoot is called only after the CLI has resolved the physical
            // workspace. SweepAsync still requires matching durable WorkspaceRootEstablished
            // records and exact repository identity; a caller-provided path alone cannot
            // authorize cleanup.
            root = _workspaceRoot;
            if (root is null) return;
            if (!HasHistoricalWorkspaceSession(root))
            {
                _worktreeStartupRecoveryAttempted = true;
                return;
            }
        }
        _worktreeStartupRecoveryAttempted = true;
        try
        {
            var outcome = WorktreeStartupRecoveryHost.SweepAsync(_store, _codecs, root,
                _platformPathsFactory(), DateTimeOffset.UtcNow, CancellationToken.None)
                .GetAwaiter().GetResult();
            _worktreeMaintenanceProblem = outcome.Succeeded ? null
                : outcome.ErrorCode?.ToString() ?? "MaintenanceFailed";
        }
        catch (Exception)
        {
            _worktreeMaintenanceProblem = "MaintenanceFailed";
        }
    }

    private bool HasHistoricalWorkspaceSession(string workspaceRoot)
    {
        if (_store is not IWorkspaceJournalReader reader) return false;
        try
        {
            var canonicalRoot = ProjectIdentity.CanonicalWorkspacePath(workspaceRoot);
            var workspaceId = WorkspaceId.Of(canonicalRoot).ToString();
            return reader.ReadEvents(EventType.Of("session.created")).Any(envelope =>
                _codecs.Decode(envelope) is SessionCreated created
                && string.Equals(created.WorkspaceId, workspaceId, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            // A journal read failure cannot authorize cleanup. The sweep remains skipped.
            return false;
        }
    }

    /// <summary>
    /// Raíz del workspace del run reanudable, reconstruida del evento durable y VERIFICADA (ADR-0004 §5).
    /// Devuelve null (recuperación bloqueada) si: no hay evento <c>WorkspaceRootEstablished</c>, la ruta
    /// no es absoluta o no existe como directorio (workspace movido/borrado). NUNCA devuelve el cwd del
    /// proceso ni una ruta de display. Las excepciones del journal (lectura/códec) se propagan al
    /// llamante, que las expone como bloqueo visible.
    /// </summary>
    private string? VerifiedWorkspaceRoot(SessionId sessionId)
    {
        var (canonical, identity) = RecordedWorkspaceRoot(sessionId);
        var events = _store.ReadFrom(sessionId, 1);
        if (!string.IsNullOrWhiteSpace(canonical)
            && TryVerifyRecordedWorkspaceRoot(canonical!, identity ?? "") is { } recorded)
            return recorded;

        if (!string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            var bound = ResolveConfiguredWorkspaceRoot(events, _workspaceRoot!, identity ?? "");
            if (bound is not null) return bound;
        }

        // A configured path is only a recovery candidate when both its durable physical
        // identity and the session's canonical WorkspaceId match. No cwd/display-path fallback.
        return null;
    }

    /// <summary>
    /// Raíz canónica persistida por la sesión (solo el evento <c>WorkspaceRootEstablished</c>),
    /// junto con su identidad durable (el token del marcador dentro del workspace; ADR-0004 §5).
    /// La recuperación usa el token al reabrir para verificar que la ruta no fue sustituida por
    /// symlink/junction. Token vacío = identidad no establecida: la sesión no es recuperable
    /// automáticamente y la recuperación falla cerrado.
    /// </summary>
    private (string? Root, string? Identity) RecordedWorkspaceRoot(SessionId sessionId)
    {
        foreach (var evt in _store.ReadFrom(sessionId, 1))
        {
            if (!evt.Type.ToString().Equals("workspace.root_established", StringComparison.Ordinal))
            {
                continue;
            }

            var established = _codecs.Decode(evt)
                as WorkspaceRootEstablished;
            if (established is not null && established!.CanonicalRoot is not null
                && established!.CanonicalRoot.Length > 0)
            {
                return (established!.CanonicalRoot, established!.DurableIdentity ?? "");
            }

            return (null, null);
        }

        return (null, null);
    }

    private IReadOnlyList<DomainEvent> EventsForRun(IReadOnlyList<DomainEvent> all, RunId runId)
    {
        var events = new List<DomainEvent>();
        var inRun = false;
        foreach (var evt in all)
        {
            if (evt.Type.ToString() == "run.created")
            {
                var created = _codecs.Decode(evt) as RunCreated;
                inRun = created?.RunId == runId;
            }

            if (inRun) events.Add(evt);
        }

        return events;
    }

    public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken) =>
        SafeCommandAck(SendCore(command, cancellationToken, trustedUserAction: false));

    public CommandAck SendUserAction(WireEnvelope command, CancellationToken cancellationToken) =>
        SafeCommandAck(SendCore(command, cancellationToken, trustedUserAction: true));

    private static string SafeCommandError(string error)
    {
        try { return new PiiRedactor().Redact(error); }
        catch (Exception) { return "No se pudo completar el command."; }
    }

    private static CommandAck SafeCommandAck(CommandAck ack) => ack.Error is null ? ack
        : new CommandAck(ack.CommandId, ack.Status, SafeCommandError(ack.Error), ack.Outcome, ack.FirstSeq, ack.LastSeq);

    private CommandAck SendCore(WireEnvelope command, CancellationToken cancellationToken, bool trustedUserAction,
        WorkflowRuntimeAuthorization? workflowAuthorization = null)
    {
        if (command.MessageType != MessageTypes.Command)
        {
            return new CommandAck(command.MessageId, "error",
                "esperaba un command, recibí " + command.MessageType, RuntimeCommandOutcome.Rejected());
        }

        var fields = JsonObj.Parse(command.PayloadJson);
        var queryName = fields.TryGetValue("query", out var q) ? q : null;
        if (queryName is not null)
        {
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp());
        }

        // ADR-0013 §3: todo evento escrito mientras se atiende el comando lleva su CommandId como
        // causa. Un messageId que no es un UUID no puede ser CommandId: el comando se rechaza.
        if (!Guid.TryParse(command.MessageId, out var commandGuid))
        {
            return new CommandAck(command.MessageId, "error", "messageId no es un identificador válido",
                RuntimeCommandOutcome.Rejected());
        }

        var requestedConversationCommand = fields.TryGetValue("cmd", out var requestedName)
            && requestedName is "conversation.new" or "conversation.resume";
        // Reject a plainly stale native input before lease recovery can perform the separate,
        // conservative legacy-policy migration for the active Session.
        if (!requestedConversationCommand && fields.GetValueOrDefault("cmd") == "session.input"
            && fields.TryGetValue("expectedPolicyRevision", out var expectedPolicyRaw)
            && long.TryParse(expectedPolicyRaw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedPolicyRevision)
            && expectedPolicyRevision > 0
            && (ReadSessionPointer()?.Session ?? _lastSessionId) is { } scopedSession
            && ReadSessionSecurityPolicy(scopedSession) is { } currentPolicy
            && currentPolicy.PolicyRevision != expectedPolicyRevision)
            return new CommandAck(command.MessageId, "error", "session_security.stale_revision",
                RuntimeCommandOutcome.Rejected());
        if (!requestedConversationCommand && !EnsureWriterOwnershipForCommand())
            return WriterOwnershipFailureAck(command.MessageId);

        using var causation = CausationScope.Begin(new CommandCausation(new CommandId(commandGuid)));
        var commandName = fields.TryGetValue("cmd", out var c) ? c : null;
        if (commandName == CompanionPetProtocol.Command) return ChangeCompanion(command);
        if (commandName == "extensions.package")
            return ChangeExtensionPackage(command, fields, trustedUserAction, cancellationToken);
        if (commandName == "conversation.alternative")
            return CreateWorkspaceAlternative(command, fields, trustedUserAction, cancellationToken);
        if (commandName is "command.invoke.user" or "command.continue" or "workflow.continue" or "process.control")
            return trustedUserAction ? HandleNativeCommandAction(command, commandName, cancellationToken)
                : new CommandAck(command.MessageId, "error", "trusted_user_action_required", RuntimeCommandOutcome.Rejected());
        if (commandName is "conversation.new" or "conversation.resume" or "conversation.rename" or "conversation.compact")
            return HandleConversationCommand(command, fields, commandName, trustedUserAction, cancellationToken);
        if (commandName == "session.security.set")
            return trustedUserAction
                ? SetSessionSecurityPolicy(command, fields)
                : new CommandAck(command.MessageId, "error", "trusted_user_action_required",
                    RuntimeCommandOutcome.Rejected());
        if (commandName == "session.security.default.set")
            return trustedUserAction
                ? SetSessionSecurityDefault(command, fields, cancellationToken)
                : new CommandAck(command.MessageId, "error", "trusted_user_action_required",
                    RuntimeCommandOutcome.Rejected());
        if (commandName is "session.model.select" or "session.model.route.respond")
            return trustedUserAction
                ? HandleSessionModelCommand(command, fields, commandName, cancellationToken)
                : new CommandAck(command.MessageId, "error", "trusted_user_action_required",
                    RuntimeCommandOutcome.Rejected());
        if (commandName == UiLocalePreferencesProtocol.ConfigureCommand)
        {
            if (!trustedUserAction)
                return new(command.MessageId, "error", "ui.locale.error.trusted_action_required",
                    RuntimeCommandOutcome.Rejected());
            try
            {
                var request = UiLocalePreferencesProtocol.DecodeUpdate(command.PayloadJson)
                    ?? throw new ArgumentException("UI locale request is malformed.");
                new UserLocaleConfiguration(_platformPathsFactory()).Set(request, cancellationToken);
                return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException
                or System.Text.Json.JsonException or ConfigValidationException)
            {
                return new(command.MessageId, "error", UiLocaleErrorKey(exception), RuntimeCommandOutcome.Rejected());
            }
        }
        if (commandName == "sidebar.configure")
        {
            if (!trustedUserAction) return new(command.MessageId, "error", "Trusted user action required", RuntimeCommandOutcome.Rejected());
            try
            {
                SidebarSettings().Set(fields.GetValueOrDefault("scope") ?? "", fields.GetValueOrDefault("key") ?? "",
                    fields.GetValueOrDefault("value") ?? "", fields.GetValueOrDefault("revision") ?? "");
                return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException
                or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
            { return new(command.MessageId, "error", exception is ConfigValidationException ? "Invalid sidebar settings" : exception.Message, RuntimeCommandOutcome.Rejected()); }
        }
        if (commandName == "delegation.create") return CreateQueuedDelegation(command, trustedUserAction,
            cancellationToken, workflowAuthorization);
        if (commandName == "fanout.create") return CreateFanOutGroup(command, trustedUserAction, cancellationToken);
        if (commandName == "fanout.replace_member") return ReplaceFanOutMember(command, trustedUserAction, cancellationToken);
        if (commandName is "delegation.cancel" or "delegation.disposition" or "delegation.mailbox.send"
            or "execution.join" or "execution.join.cancel")
            return ControlDelegation(command, commandName, trustedUserAction, cancellationToken);
        if (commandName == "command.invoke")
        {
            try
            {
                RefreshProjectExtensionCommands();
                _lastWorkflowInvocation = null;
                _lastWorkflowRequested = null;
                _lastPromptExpanded = null;
                _lastForcedCommandTool = null;
                using var document = System.Text.Json.JsonDocument.Parse(command.PayloadJson);
                var root = document.RootElement;
                var name = root.GetProperty("name").GetString() ?? "";
                var args = root.TryGetProperty("arguments", out var argumentArray)
                    ? argumentArray.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                    : Array.Empty<string>();
                var origin = root.TryGetProperty("origin", out var originValue)
                    ? originValue.GetString() ?? "Typed" : "Typed";
                var commandContext = CommandContextForCurrentRun();
                var invoked = _commandService.InvokeAsync(new CommandInvocation(name, args, origin),
                    commandContext,
                    cancellationToken).AsTask().GetAwaiter().GetResult();
                if (invoked is PromptCommandRequested prompt)
                {
                    _lastPromptExpanded = new PromptExpanded(prompt.Text, prompt.Origin);
                    _lastForcedCommandTool = prompt.ForcedToolName;
                    _lastWorkflowRequested = null;
                    _pendingPromptOrigin = prompt.Origin;
                }
                else if (invoked is WorkflowRequested workflow)
                {
                    _lastPromptExpanded = null;
                    _lastWorkflowRequested = workflow;
                    _lastWorkflowInvocation = new WorkflowInvocation(command.MessageId, workflow,
                        commandContext.Session, commandContext.Run);
                    _pendingPromptOrigin = null;
                }
                else throw new InvalidOperationException("Command handler returned an unsupported outcome.");
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or KeyNotFoundException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return new CommandAck(command.MessageId, "error", ex.Message, RuntimeCommandOutcome.Rejected());
            }
        }

        if (commandName is "skills.activate" or "skills.release")
        {
            lock (_modeAuthorityMutationGate)
                return ChangeSessionSkillActivation(command, commandName, fields, trustedUserAction, cancellationToken);
        }
        if (commandName is "memory.session.add" or "memory.session.forget" or "memory.session.propose"
            or "memory.session.promote" or "memory.session.reject")
        {
            lock (_modeAuthorityMutationGate)
                return ChangeSessionMemory(command, commandName, fields, trustedUserAction, cancellationToken);
        }
        if (commandName == "external.memory.consent")
        {
            if (!trustedUserAction)
                return new CommandAck(command.MessageId, "error", "External memory requires an explicit trusted user action.",
                    RuntimeCommandOutcome.Rejected());
            var decision = fields.GetValueOrDefault("decision");
            if (decision is not ("allow" or "revoke"))
                return new CommandAck(command.MessageId, "error", "External memory decision is unsupported.",
                    RuntimeCommandOutcome.Rejected());
            var scope = fields.GetValueOrDefault("scope");
            if (scope is not ("global" or "project"))
                return new CommandAck(command.MessageId, "error", "External memory scope is unsupported.",
                    RuntimeCommandOutcome.Rejected());
            var paths = _platformPathsFactory();
            var consent = new ExternalAgentMemoryConsentStore(paths.DataDirectory);
            if (scope == "global") consent.SetGlobal(decision == "allow");
            else
            {
                var workspaceTrusted = _workspaceRoot is not null && new WorkspaceTrustStore(paths).IsTrusted(_workspaceRoot);
                var project = ResolveExternalAgentProjectScope(paths, workspaceTrusted);
                if (!project.Trusted || project.Identity is null)
                    return new CommandAck(command.MessageId, "error", "Project memory requires a trusted project root.",
                        RuntimeCommandOutcome.Rejected());
                consent.SetProject(decision == "allow", project.Identity);
            }
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        if (commandName == "extensions.consent")
            return ChangeProjectExtensionConsent(command, fields, trustedUserAction);

        if (commandName == "sim")
        {
            return RunSim(command, fields);
        }

        if (commandName == "sim.resume")
        {
            return ResumeSim(command);
        }

        // Conversación ↔ Run, interrupción, cancelación e interacciones (ADR-0035, ADR-0034).
        if (commandName is "session.input" or "run.interrupt" or "run.cancel" or "interaction.respond"
            or "interaction.continue")
        {
            return RunControl(command, commandName, fields, trustedUserAction);
        }

        if (commandName == "explore.start")
        {
            return StartExplorerRun(command, fields);
        }

        if (commandName is "mode.default.set" or "run.mode.select" or "run.mode.revoke"
            or "reasoning.default.set" or "reasoning.default.revoke"
            or "run.reasoning.select" or "run.reasoning.revoke")
        {
            if (!trustedUserAction)
                return new CommandAck(command.MessageId, "error", "mode and reasoning authority require a trusted user action",
                    RuntimeCommandOutcome.Rejected());
            return commandName switch
            {
                "mode.default.set" => SetDefaultRunMode(command, fields),
                "run.mode.select" => SelectRunMode(command, fields),
                "run.mode.revoke" => RevokeRunModeAuthority(command, fields),
                "reasoning.default.set" => SetDefaultReasoning(command, fields),
                "reasoning.default.revoke" => RevokeDefaultReasoning(command, fields),
                "run.reasoning.select" => SelectRunReasoning(command, fields),
                _ => RevokeRunReasoning(command, fields),
            };
        }

        if (commandName == "act")
        {
            return StartActRun(command, fields);
        }

        return new CommandAck(command.MessageId, "error", "comando desconocido o payload inválido",
            RuntimeCommandOutcome.Rejected());
    }

    /// <summary>
    /// Eventos del protocolo de la sesión en curso desde la secuencia dada (ADR-0013 §1): los eventos
    /// de dominio del journal traducidos por <see cref="ProtocolMapper"/>, seguidos de las
    /// notificaciones del servidor (resumen de una simulación o de un resume).
    /// </summary>
    public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence)
    {
        var result = new List<WireEnvelope>();
        if (_lastSessionId is not null && _store.CurrentSequence(_lastSessionId) >= Math.Max(1, fromSequence))
        {
            var journal = _store.ReadFrom(_lastSessionId, 1);
            var events = journal.Where(evt => evt.Sequence >= Math.Max(1, fromSequence)).ToArray();
            result.AddRange(new ProtocolMapper(_codecs, _artifacts).Map(events, journal));
        }

        result.AddRange(_events);
        return result;
    }

    internal IReadOnlyList<WireEnvelope> MapReplayPage(IReadOnlyList<DomainEvent> events) =>
        new ProtocolMapper(_codecs, _artifacts).Map(events);

    internal (IReadOnlyList<WireEnvelope> Events, long NextSequence, long HighWater, bool HasGap) ReadStreamPage(
        SessionId session, long fromSequence, int pageSize)
    {
        if (_store is SqliteEventStore sqlite)
        {
            var page = sqlite.ReadPage(session, fromSequence, Math.Clamp(pageSize, 1, 64));
            return (MapReplayPage(page.Events), page.NextSequence, page.HighWater, page.HasGap);
        }
        var all = _store.ReadFrom(session, fromSequence);
        var selected = all.Take(Math.Clamp(pageSize, 1, 64)).ToArray();
        var mapped = MapReplayPage(selected);
        var next = selected.Length == 0 ? _store.CurrentSequence(session) + 1 : selected[^1].Sequence + 1;
        return (mapped, next, _store.CurrentSequence(session), selected.Length > 0 && selected[0].Sequence != fromSequence);
    }

    /// <summary>Publica PlanApproval tras una propuesta aceptada de plan.propose; no contesta por el usuario.</summary>
    public InteractionId? RequestPlanApprovalIfNeeded() => RequirePlanApprovalInteraction(RequestPlanApprovalCommand());

    internal static InteractionId? RequirePlanApprovalInteraction(
        (InteractionId? InteractionId, CommandAck Ack, Exception? Failure) publication)
    {
        if (publication.Failure is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        if (publication.Ack.Status != "ok"
            || publication.Ack.Outcome?.Kind is not (RuntimeCommandOutcomeKind.Accepted or RuntimeCommandOutcomeKind.NoOp))
            throw new InvalidOperationException(publication.Ack.Error ?? "Plan approval publication was not confirmed");
        return publication.InteractionId;
    }

    /// <summary>Internal command boundary for the conditional PlanApproval publication.</summary>
    internal (InteractionId? InteractionId, CommandAck Ack, Exception? Failure) RequestPlanApprovalCommand()
    {
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var commandMessageId = commandId.Value.ToString();
        var sessionId = _lastSessionId;
        long? sequenceBefore = null;
        try
        {
            sequenceBefore = sessionId is null ? 0 : _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;

            CommandAck NoOpAck() => new(commandMessageId, "ok", null, RuntimeCommandOutcome.NoOp());

            if (sessionId is null || _lastRunId is null) return (null, NoOpAck(), null);
            var runId = _lastRunId;
            var all = _store.ReadFrom(sessionId, 1);
            var own = EventsForRun(all, runId);
            var projection = RunProjection.Replay(sessionId, runId, _codecs, own);
            if (projection.Mode != RunMode.Plan || projection.State is not (RunState.Running or RunState.AwaitingInput)
                || projection.RootTask is null) return (null, NoOpAck(), null);

            var requested = new HashSet<ToolCallId>();
            long acceptedProposalSequence = 0;
            long lastApprovalResolutionSequence = 0;
            var pendingApproval = new Dictionary<InteractionId, InteractionRequested>();
            foreach (var evt in own)
            {
                switch (_codecs.Decode(evt))
                {
                    case ToolCallRequested call when call.ToolName == "plan.propose": requested.Add(call.ToolCallId); break;
                    case ToolCallSucceeded succeeded when requested.Contains(succeeded.ToolCallId):
                        acceptedProposalSequence = evt.Sequence;
                        break;
                    case InteractionRequested approvalRequest when approvalRequest.Kind == InteractionKind.PlanApproval:
                        pendingApproval[approvalRequest.InteractionId] = approvalRequest;
                        break;
                    case InteractionResolved resolved when pendingApproval.ContainsKey(resolved.InteractionId):
                        pendingApproval.Remove(resolved.InteractionId);
                        lastApprovalResolutionSequence = evt.Sequence;
                        break;
                }
            }
            if (pendingApproval.Count > 0) return (pendingApproval.Keys.Last(), NoOpAck(), null);
            if (projection.State != RunState.Running || acceptedProposalSequence <= lastApprovalResolutionSequence)
                return (null, NoOpAck(), null);
            var rootLane = LaneProjection.Replay(_codecs, own).ForTask(projection.RootTask!)
                .FirstOrDefault(lane => lane.State == LaneState.Running)?.Id;
            if (rootLane is null) return (null, NoOpAck(), null);

            var interaction = InteractionId.New();
            var request = new InteractionRequested(interaction, InteractionKind.PlanApproval,
                "{\"operation\":\"plan.approval\",\"reason\":\"El plan fue propuesto por el modelo y requiere aprobación\"}",
                "[{\"id\":\"approve_execute\",\"intent\":\"allow\"},"
                    + "{\"id\":\"approve_only\",\"intent\":\"allow\"},"
                    + "{\"id\":\"continue_planning\",\"intent\":\"allow\"},"
                    + "{\"id\":\"reject\",\"intent\":\"deny\"}]",
                "reject", null, rootLane, projection.RootTask, null, 0, 1);
            // PlanApproval es una espera humana durable: publica la interacción y la transición
            // canónica del Run a AwaitingInput en el mismo commit (ADR-0034/0035/0036).
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(runId, projection.RootTask, rootLane));
            new EventStream(_store, _codecs, sessionId).AppendBatch(
                new DomainEventPayload[] { request, new RunAwaitingInput(runId, rootLane) },
                DurabilityClass.Standard);
            var publicationAck = CommandOutcomeAck(commandMessageId, "ok", null,
                RuntimeCommandOutcome.Accepted(), sessionId, sequenceBefore.Value, commandId);
            return (publicationAck.Status == "ok" ? interaction : null, publicationAck, null);
        }
        catch (Exception failure)
        {
            if (sessionId is null || sequenceBefore is null)
                return (null, UnavailableCommandOutcome(commandMessageId), failure);
            try
            {
                // Confirm once: neither a locally generated ID nor an uncertain append is durable proof.
                var persisted = CommandResultEvents(sessionId, sequenceBefore.Value, commandId);
                var interaction = persisted.Select(_codecs.Decode).OfType<InteractionRequested>()
                    .LastOrDefault(request => request.Kind == InteractionKind.PlanApproval)?.InteractionId;
                var ack = persisted.Length == 0
                    ? new CommandAck(commandMessageId, "error", failure.Message, RuntimeCommandOutcome.Rejected())
                    : new CommandAck(commandMessageId, "error", failure.Message, RuntimeCommandOutcome.Accepted(),
                        persisted.Min(evt => evt.Sequence), persisted.Max(evt => evt.Sequence));
                return (interaction, ack, failure);
            }
            catch (Exception)
            {
                return (null, UnavailableCommandOutcome(commandMessageId), failure);
            }
        }
    }

    /// <summary>Resuelve una interacción usando el protocolo tipado del servidor.</summary>
    public CommandAck RespondToInteraction(InteractionId interaction, string optionId) => Send(
        WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "interaction.respond") + ","
            + JsonObj.Field("interactionId", interaction.ToString()) + ","
            + JsonObj.Field("optionId", optionId) + "}"), CancellationToken.None);

    /// <summary>Envía respuesta tipada del cuestionario por IOmniClient (ADR-0034/0045).</summary>
    public CommandAck RespondToQuestionnaire(InteractionId interaction,
        IReadOnlyList<QuestionAnswer> answers, bool cancelled) => Send(
        WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "interaction.respond") + ","
            + JsonObj.Field("responseType", "questionnaire") + ","
            + JsonObj.Field("interactionId", interaction.ToString()) + ","
            + JsonObj.FieldRaw("answers", QuestionnaireCodec.EncodeAnswers(answers)) + ","
            + JsonObj.FieldRaw("cancelled", cancelled ? "true" : "false") + "}"), CancellationToken.None);

    /// <summary>Proyección diagnóstica de la misma catalog/boundary que recibió ExplorerTurn.</summary>
    public void ConfigureToolDiagnostics(FakeCatalog catalog, ModelCapabilityBoundary boundary, RunMode mode)
    {
        _diagnosticCatalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _diagnosticBoundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _diagnosticMode = mode;
    }

    public RunMode CurrentRunMode()
    {
        if (_lastSessionId is null || _lastRunId is null) return RunMode.Act;
        var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
        return RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, events).Mode ?? ReadModePreference().Mode;
    }

    public RunModeAuthority? CurrentModeAuthority()
    {
        if (_lastSessionId is null || _lastRunId is null) return null;
        var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
        return RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, events).ModeAuthority;
    }

    private CommandContext CommandContextForCurrentRun()
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            return new CommandContext(null, null, null, _workspaceRoot, false,
                "Start a Run and authorize Orchestrate mode before requesting a workflow.");
        var journal = _store.ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, _codecs, EventsForRun(journal, run));
        var authority = projection.ModeAuthority;
        var authorized = projection.State == RunState.Running && authority?.Mode == RunMode.Orchestrate
            && authority.Authorization is not null
            && authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow);
        var reason = authorized ? null : "An active Orchestrate Run with current authorization is required.";
        return new CommandContext(session, run, projection.Mode ?? CurrentRunMode(), _workspaceRoot, authorized, reason);
    }

    private void RefreshProjectExtensionCommands()
    {
        _commandService.Registry.RemoveSource(SourceKind.Extension);
        var paths = _platformPathsFactory();
        var trusted = _workspaceRoot is not null && new WorkspaceTrustStore(paths).IsTrusted(_workspaceRoot);
        foreach (var package in ProjectExtensionPackageCatalog.Ready(
            _workspaceRoot ?? Directory.GetCurrentDirectory(), paths, trusted))
        {
            var extension = package.Extension;
            var capabilities = package.Capabilities;
            if (capabilities?.Contains("command.register") != true || capabilities.Contains("tool.register") != true) continue;
            foreach (var command in extension.Manifest!.Provides?.Commands ?? [])
            {
                if (command.Kind != "Extension" || command.Tool is null) continue;
                try { _commandService.Registry.Register(new ProjectExtensionCommandHandler(extension, command)); }
                catch (InvalidOperationException) { /* Conflicts stay unavailable; no source silently wins. */ }
            }
        }
        SessionSkillWorkflowCommands.Refresh(_commandService.Registry, _lastSessionId, _workspaceRoot,
            paths.DataDirectory, _workspaceRoot is null ? null : OmniHost.WorkspaceDataDirectory(paths, _workspaceRoot),
            trusted, ProjectExtensionPackageCatalog.Ready(_workspaceRoot ?? Directory.GetCurrentDirectory(),
                paths, trusted));
    }

    public RunReasoningSelectionState? CurrentRunReasoningSelection()
    {
        if (_lastSessionId is null || _lastRunId is null) return null;
        var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
        return RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, events).ReasoningSelection;
    }

    private RunModePreference ReadModePreference()
    {
        if (_userDatabasePath is null) return new RunModePreference(RunMode.Act, 0);
        using var store = new RunModePreferenceStore(_userDatabasePath);
        return store.Read();
    }

    private UserReasoningPreference ReadReasoningPreference()
    {
        if (_userDatabasePath is null) return new UserReasoningPreference(false, null, 0);
        using var store = new ReasoningPreferenceStore(_userDatabasePath);
        return store.Read();
    }

    private static bool TryParseRunMode(string? value, out RunMode mode)
    {
        mode = value?.ToLowerInvariant() switch
        {
            "plan" => RunMode.Plan,
            "act" => RunMode.Act,
            "orq" or "orchestrate" => RunMode.Orchestrate,
            _ => (RunMode)(-1),
        };
        return Enum.IsDefined(mode);
    }

    private static string RunModeWireName(RunMode mode) => mode == RunMode.Orchestrate
        ? "orq" : mode.ToString().ToLowerInvariant();

    internal static string ModeAuthorityJson(RunModeAuthority authority, DateTimeOffset utcNow)
    {
        var auth = authority.Authorization;
        var allowed = auth?.AllowedModes ?? Array.Empty<RunMode>();
        var limits = auth?.Limits;
        var authorization = auth is null ? "null" : "{"
            + JsonObj.Field("authorizationId", auth.AuthorizationId.ToString("D"))
            + ",\"allowedModes\":[" + string.Join(",", allowed.Select(mode =>
                "\"" + RunModeWireName(mode) + "\"")) + "],\"limits\":{"
            + "\"maxAgents\":" + limits!.MaxAgents.ToString(CultureInfo.InvariantCulture)
            + ",\"maxDepth\":" + limits.MaxDepth.ToString(CultureInfo.InvariantCulture)
            + ",\"maxTurns\":" + limits.MaxTurns.ToString(CultureInfo.InvariantCulture)
            + ",\"maxToolCalls\":" + limits.MaxToolCalls.ToString(CultureInfo.InvariantCulture)
            + ",\"maxElapsedSeconds\":" + limits.MaxElapsedSeconds.ToString(CultureInfo.InvariantCulture)
            + ",\"maxSpendUsd\":" + (limits.MaxSpendUsd?.ToString(CultureInfo.InvariantCulture) ?? "null")
            + "}," + JsonObj.Field("grantedAtUtc", auth.GrantedAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture))
            + ",\"planCoverage\":" + (auth.PlanCoverage is not { } coverage ? "null" : "{"
                + JsonObj.Field("runId", coverage.RunId.ToString()) + ","
                + JsonObj.Field("planId", coverage.PlanId.ToString())
                + ",\"planRevision\":" + coverage.PlanRevision.ToString(CultureInfo.InvariantCulture) + ","
                + JsonObj.Field("rootTaskId", coverage.RootTaskId.ToString()) + "}")
            + ",\"expired\":" + (auth.IsExpiredAt(utcNow) ? "true" : "false") + "}";
        return "{" + JsonObj.Field("runId", authority.RunId.ToString())
            + ",\"revision\":" + authority.Revision.ToString(CultureInfo.InvariantCulture)
            + "," + JsonObj.Field("mode", RunModeWireName(authority.Mode))
            + "," + JsonObj.Field("strategy", authority.Strategy.ToString().ToLowerInvariant())
            + "," + JsonObj.Field("effort", authority.ProductEffort.ToString().ToLowerInvariant())
            + ",\"modePinned\":" + (authority.ModePinned ? "true" : "false")
            + ",\"autoModeSwitch\":" + (authority.AutoModeSwitch ? "true" : "false")
            + ",\"effectiveAutoModeSwitch\":" + (authority.IsAutoModeSwitchEffectiveAt(utcNow) ? "true" : "false")
            + "," + JsonObj.Field("objectiveDigest", authority.ObjectiveDigest)
            + ",\"authorization\":" + authorization + "}";
    }

    private CommandAck SetDefaultRunMode(WireEnvelope command, Dictionary<string, string> fields)
    {
        try
        {
            if (_userDatabasePath is null || !fields.TryGetValue("mode", out var rawMode)
                || !TryParseRunMode(rawMode, out var mode)
                || !fields.TryGetValue("expectedRevision", out var rawRevision)
                || !long.TryParse(rawRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedRevision))
                return new CommandAck(command.MessageId, "error",
                    "mode.default.set requires a valid mode, User database, and expectedRevision",
                    RuntimeCommandOutcome.Rejected());

            using var store = new RunModePreferenceStore(_userDatabasePath);
            store.Set(mode, expectedRevision);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (RunModePreferenceConflictException)
        {
            return new CommandAck(command.MessageId, "error", "Run mode preference revision conflict",
                RuntimeCommandOutcome.Rejected());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or SqliteException or OverflowException)
        {
            return new CommandAck(command.MessageId, "error", "Could not persist the User Run mode preference",
                RuntimeCommandOutcome.Rejected());
        }
    }

    /// <summary>
    /// Applies one deterministic UltraCode policy decision. This is an internal Host boundary,
    /// not a wire command: the caller cannot choose an origin, grant, objective, limits, or scope
    /// from model/tool output. The Run revision is reloaded while holding the shared store gate.
    /// </summary>
    internal InternalCommandResult ApplyUltraCodePolicyTransition(SessionId sessionId, RunId runId,
        RunMode targetMode, string reason, long expectedAuthorityRevision,
        CancellationToken cancellationToken = default, EventId? proposalEventId = null)
    {
        var commandId = CommandId.New();
        var messageId = commandId.ToString();
        long? sequenceBefore = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_modeAuthorityMutationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sequenceBefore = _store.CurrentSequence(sessionId);
                var all = _store.ReadFrom(sessionId, 1);
                var created = all.Where(evt => evt.SessionId == sessionId && evt.RunId == runId
                        && _codecs.Decode(evt) is RunCreated runCreated && runCreated.RunId == runId)
                    .ToArray();
                if (created.Length != 1)
                    return PolicyTransitionResult(messageId, "The target Run is absent or ambiguous in this Session",
                        RuntimeCommandOutcome.Rejected());

                var own = EventsForRun(all, runId);
                if (own.Count == 0 || own.Any(evt => IsRunModeEvent(_codecs.Decode(evt))
                    && (evt.SessionId != sessionId || evt.RunId != runId || evt.CorrelationId != runId)))
                    return PolicyTransitionResult(messageId, "The target Run has inconsistent journal scope",
                        RuntimeCommandOutcome.Deferred("RunScopeUnavailable"));
                if (SessionRoutingAuthorization.Read(all, _codecs, sessionId) is null)
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("RoutingPolicyUnavailable"));

                var projection = RunProjection.Replay(sessionId, runId, _codecs, own);
                if (projection.IsTerminal())
                    return PolicyTransitionResult(messageId, "The target Run is terminal",
                        RuntimeCommandOutcome.Rejected());
                if (HasOpenModelStep(own))
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
                if (HasOpenToolCall(own))
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("ToolCallActive"));
                if (HasPendingModelRouteConsent(own))
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("ModelRouteConsent"));
                if (!IsBoundedPolicyReason(reason))
                    return PolicyTransitionResult(messageId, "Policy transition reason is invalid",
                        RuntimeCommandOutcome.Rejected());
                var durableReason = SecretRedactor.Shared.Redact(reason);

                var current = projection.ModeAuthority;
                if (current is null || current.Revision != expectedAuthorityRevision
                    || current.ProductEffort != ProductEffort.UltraCode || current.ModePinned
                    || !current.AutoModeSwitch || current.Authorization is not { } authorization
                    || !authorization.AllowedModes.Contains(targetMode)
                    || !current.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
                    return PolicyTransitionResult(messageId, "A current UltraCode authorization is required",
                        RuntimeCommandOutcome.Rejected());
                if (!Enum.IsDefined(targetMode))
                    return PolicyTransitionResult(messageId, "The target RunMode is unknown",
                        RuntimeCommandOutcome.Rejected());
                if (targetMode == current.Mode)
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.NoOp());

                if (proposalEventId is not null)
                {
                    var source = all.SingleOrDefault(evt => evt.EventId == proposalEventId);
                    if (source is null || source.RunId != runId
                        || _codecs.Decode(source) is not RunModeProposed proposal
                        || proposal.AuthorityRevision != current.Revision || proposal.From != current.Mode
                        || proposal.To != targetMode
                        || !own.Any(evt => _codecs.Decode(evt) is TurnCompleted completed
                            && completed.TurnId == proposal.TurnId))
                        return PolicyTransitionResult(messageId, "Proposal is not a completed current-authority recommendation",
                            RuntimeCommandOutcome.Rejected());
                    _ = ModeProposalProjection.Replay(sessionId, runId, _codecs, all);
                }

                if (current.Mode == RunMode.Plan && targetMode == RunMode.Act)
                {
                    if (HasPendingInteraction(own))
                        return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("PendingInteraction"));
                    if (authorization.PlanCoverage is not { } coverage)
                        return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("PlanCoverageUnavailable"));
                    var plan = PlanProjection.Replay(_codecs, own).Latest();
                    if (plan is null || coverage.RunId != runId || coverage.PlanId != plan.Id
                        || coverage.PlanRevision != plan.Revision || coverage.RootTaskId != projection.RootTask)
                        return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("PlanCoverageStale"));
                }

                // A mode decision cannot grant a route or consume a pending consent. Any following
                // provider selection still passes the ordinary SessionRoutingAuthorization gate.
                var nextRevision = checked(current.Revision + 1);
                var nextAuthorization = authorization with { AuthorityRevision = nextRevision };
                var next = current with
                {
                    Revision = nextRevision,
                    Mode = targetMode,
                    Authorization = nextAuthorization,
                };
                next.Validate();

                cancellationToken.ThrowIfCancellationRequested();
                using var command = CausationScope.Begin(new CommandCausation(commandId));
                using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: runId));
                var batch = new DomainEventPayload[]
                {
                    new RunModeChanged(runId, current.Mode, targetMode, durableReason),
                    new RunModeTransitionAuthorized(runId, current.Mode, targetMode, durableReason,
                        "UltraCodePolicy", messageId, nextRevision, current.ObjectiveRevision,
                        current.ObjectiveDigest, current.PolicyRevision, authorization.AuthorizationId,
                        proposalEventId, current.Mode == RunMode.Plan && targetMode == RunMode.Act
                            ? authorization.PlanCoverage : null),
                    new RunModeAuthoritySelected(next, messageId, "UltraCodePolicy"),
                };
                new EventStream(_store, _codecs, sessionId).AppendBatch(batch, DurabilityClass.Barrier);
                var ack = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    sessionId, sequenceBefore.Value, commandId);
                return new InternalCommandResult(ack);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            var ack = sequenceBefore is null
                ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, sessionId, sequenceBefore.Value,
                    "Could not persist the UltraCode policy transition", restoreRunIdentity: false);
            return new InternalCommandResult(ack, failure);
        }
    }

    private static InternalCommandResult PolicyTransitionResult(string commandMessageId, string? error,
        RuntimeCommandOutcome outcome) => new(new CommandAck(commandMessageId,
            outcome.Kind == RuntimeCommandOutcomeKind.Rejected ? "error" : "ok", error, outcome));

    private static bool IsBoundedPolicyReason(string reason)
    {
        // Reasons are observable diagnostic text, not an enum or classifier vocabulary.
        // Bound the serialized value without rejecting otherwise meaningful Unicode text.
        return !string.IsNullOrWhiteSpace(reason) && reason.Length <= 512;
    }

    private static bool IsRunModeEvent(DomainEventPayload payload) => payload is
        RunModeChanged or RunModeTransitionAuthorized or RunModeAuthoritySelected or RunModeAuthorityRevoked;

    private bool HasPendingModelRouteConsent(IReadOnlyList<DomainEvent> events)
    {
        var pending = new Dictionary<InteractionId, InteractionKind>();
        foreach (var evt in events)
        {
            var payload = _codecs.Decode(evt);
            switch (payload)
            {
                case InteractionRequested requested when evt.RunId is not null
                    && requested.Kind == InteractionKind.ModelRouteConsent:
                    pending[requested.InteractionId] = requested.Kind;
                    break;
                case InteractionResolved resolved:
                    pending.Remove(resolved.InteractionId);
                    break;
                case InteractionExpired expired:
                    pending.Remove(expired.InteractionId);
                    break;
            }
        }
        return pending.Values.Contains(InteractionKind.ModelRouteConsent);
    }

    private bool HasPendingInteraction(IReadOnlyList<DomainEvent> events)
    {
        var pending = new HashSet<InteractionId>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case InteractionRequested requested: pending.Add(requested.InteractionId); break;
                case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
            }
        }
        return pending.Count != 0;
    }

    private CommandAck SelectRunMode(WireEnvelope command, Dictionary<string, string> fields)
    {
        lock (_modeAuthorityMutationGate) return SelectRunModeCore(command, fields);
    }

    private CommandAck SelectRunModeCore(WireEnvelope command, Dictionary<string, string> fields)
    {
        var messageId = command.MessageId;
        if (_lastSessionId is not { } session || _lastRunId is not { } run
            || !fields.TryGetValue("mode", out var rawMode) || !TryParseRunMode(rawMode, out var mode))
            return new CommandAck(messageId, "error", "run.mode.select requires an active Run and a valid mode",
                RuntimeCommandOutcome.Rejected());

        var commandId = new CommandId(Guid.Parse(messageId));
        long? before = null;
        try
        {
            var all = _store.ReadFrom(session, 1);
            var own = EventsForRun(all, run);
            var projection = RunProjection.Replay(session, run, _codecs, own);
            if (projection.IsTerminal())
                return new CommandAck(messageId, "error", "the active Run is terminal", RuntimeCommandOutcome.Rejected());
            if (HasOpenModelStep(own))
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
            if (HasOpenToolCall(own))
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ToolCallActive"));

            var current = projection.ModeAuthority ?? RunModeAuthority.Legacy(
                own.Select(_codecs.Decode).OfType<RunCreated>().Single(created => created.RunId == run));
            if (fields.TryGetValue("expectedRevision", out var expectedRevisionText)
                && (!long.TryParse(expectedRevisionText, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var expectedRevision) || expectedRevision != current.Revision))
                return new CommandAck(messageId, "error", "run_mode_revision_changed",
                    RuntimeCommandOutcome.Rejected());
            var effort = fields.TryGetValue("effort", out var rawEffort) ? rawEffort.ToLowerInvariant() : "standard";
            if (effort is not ("standard" or "ultracode"))
                return new CommandAck(messageId, "error", "effort must be standard or ultracode",
                    RuntimeCommandOutcome.Rejected());

            var adaptive = false;
            if (fields.TryGetValue("adaptive", out var rawAdaptive))
            {
                if (rawAdaptive is not ("true" or "false"))
                    return new CommandAck(messageId, "error", "adaptive must be true or false",
                        RuntimeCommandOutcome.Rejected());
                adaptive = rawAdaptive == "true";
            }
            ModeSwitchAuthorization? authorization = null;
            var coverCurrentPlan = false;
            if (fields.TryGetValue("coverCurrentPlan", out var rawPlanCoverage))
            {
                if (rawPlanCoverage is not ("true" or "false") || !adaptive)
                    return new CommandAck(messageId, "error",
                        "coverCurrentPlan requires explicit adaptive UltraCode and a boolean value",
                        RuntimeCommandOutcome.Rejected());
                coverCurrentPlan = rawPlanCoverage == "true";
            }
            var productEffort = effort == "ultracode" ? ProductEffort.UltraCode : ProductEffort.Standard;
            if (adaptive)
            {
                if (productEffort != ProductEffort.UltraCode || !TryReadUltraCodeLimits(fields, out var limits, out var allowedModes))
                    return new CommandAck(messageId, "error",
                        "adaptive mode switching requires explicit UltraCode limits and allowedModes",
                        RuntimeCommandOutcome.Rejected());
                if (!allowedModes.Contains(mode))
                    return new CommandAck(messageId, "error", "allowedModes must include the selected mode",
                        RuntimeCommandOutcome.Rejected());
                var nextRevision = checked(current.Revision + (current.Authorization is null ? 1 : 2));
                ModeSwitchPlanCoverage? planCoverage = null;
                if (coverCurrentPlan)
                {
                    var plan = PlanProjection.Replay(_codecs, own).Latest();
                    if (plan is null || plan.RunId != run || projection.RootTask is not { } rootTask)
                        return new CommandAck(messageId, "ok", null,
                            RuntimeCommandOutcome.Deferred("PlanCoverageUnavailable"));
                    planCoverage = new ModeSwitchPlanCoverage(run, plan.Id, plan.Revision, rootTask);
                }
                authorization = new ModeSwitchAuthorization(Guid.NewGuid(), nextRevision,
                    current.ObjectiveRevision, current.ObjectiveDigest, current.PolicyRevision, allowedModes,
                    limits, DateTimeOffset.UtcNow, planCoverage);
                authorization.Validate();
            }
            else if (fields.ContainsKey("maxAgents") || fields.ContainsKey("maxDepth")
                || fields.ContainsKey("maxTurns") || fields.ContainsKey("maxToolCalls")
                || fields.ContainsKey("maxElapsedSeconds") || fields.ContainsKey("maxSpendUsd")
                || fields.ContainsKey("allowedModes"))
            {
                return new CommandAck(messageId, "error",
                    "mode-switch limits are accepted only with explicit adaptive UltraCode",
                    RuntimeCommandOutcome.Rejected());
            }

            var sequenceBefore = _store.CurrentSequence(session);
            before = sequenceBefore;
            var next = new RunModeAuthority(run,
                checked(current.Revision + (current.Authorization is null ? 1 : 2)), mode,
                current.Strategy, productEffort, !adaptive, adaptive, current.ObjectiveRevision,
                current.ObjectiveDigest, current.PolicyRevision, authorization);
            next.Validate();

            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, projection.RootTask,
                LastLaneId()));
            var payloads = new List<DomainEventPayload>();
            if (current.Authorization is { } oldAuthorization)
                payloads.Add(new RunModeAuthorityRevoked(run, current.Revision + 1,
                    oldAuthorization.AuthorizationId, "User selected a new mode authority", messageId, "User"));
            if (current.Mode != mode)
                payloads.Add(new RunModeChanged(run, current.Mode, mode, "ExplicitUserSelection"));
            payloads.Add(new RunModeTransitionAuthorized(run, current.Mode, mode,
                "ExplicitUserSelection", "User", messageId, next.Revision, next.ObjectiveRevision,
                next.ObjectiveDigest, next.PolicyRevision, authorization?.AuthorizationId));
            payloads.Add(new RunModeAuthoritySelected(next, messageId, "User"));
            new EventStream(_store, _codecs, session).AppendBatch(payloads, DurabilityClass.Barrier);
            return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, commandId);
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException
            or OverflowException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return before is null ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, session, before.Value, "Could not persist Run mode authority");
        }
    }

    private CommandAck RevokeRunModeAuthority(WireEnvelope command, Dictionary<string, string> fields)
    {
        lock (_modeAuthorityMutationGate) return RevokeRunModeAuthorityCore(command, fields);
    }

    private CommandAck RevokeRunModeAuthorityCore(WireEnvelope command, Dictionary<string, string> fields)
    {
        var messageId = command.MessageId;
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            return new CommandAck(messageId, "error", "run.mode.revoke requires an active Run",
                RuntimeCommandOutcome.Rejected());
        long? before = null;
        try
        {
            var own = EventsForRun(_store.ReadFrom(session, 1), run);
            var projection = RunProjection.Replay(session, run, _codecs, own);
            var current = projection.ModeAuthority;
            if (projection.IsTerminal())
                return new CommandAck(messageId, "error", "the active Run is terminal", RuntimeCommandOutcome.Rejected());
            if (current?.Authorization is not { } authorization || !current.AutoModeSwitch)
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.NoOp());
            if (HasOpenModelStep(own))
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
            if (HasOpenToolCall(own))
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ToolCallActive"));
            var commandId = new CommandId(Guid.Parse(messageId));
            var sequenceBefore = _store.CurrentSequence(session);
            before = sequenceBefore;
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, projection.RootTask, LastLaneId()));
            new EventStream(_store, _codecs, session).Append(new RunModeAuthorityRevoked(run,
                checked(current.Revision + 1), authorization.AuthorizationId, "User revoked UltraCode",
                messageId, "User"), DurabilityClass.Barrier);
            return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, commandId);
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException
            or OverflowException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return before is null ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, session, before.Value, "Could not revoke mode authority");
        }
    }

    private CommandAck SetDefaultReasoning(WireEnvelope command, Dictionary<string, string> fields)
    {
        if (_userDatabasePath is null || !TryReadExpectedRevision(fields, out var expectedRevision)
            || !TryReadReasoningRequest(fields, out var request))
            return new CommandAck(command.MessageId, "error",
                "reasoning.default.set requires a User database, explicit kind (off is allowed), and expectedRevision",
                RuntimeCommandOutcome.Rejected());
        try
        {
            using var store = new ReasoningPreferenceStore(_userDatabasePath);
            store.Set(request, expectedRevision);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (ReasoningPreferenceConflictException)
        {
            return new CommandAck(command.MessageId, "error", "User reasoning preference revision conflict",
                RuntimeCommandOutcome.Rejected());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or UnauthorizedAccessException or SqliteException or OverflowException)
        {
            return new CommandAck(command.MessageId, "error", "Could not persist the User reasoning preference",
                RuntimeCommandOutcome.Rejected());
        }
    }

    private CommandAck RevokeDefaultReasoning(WireEnvelope command, Dictionary<string, string> fields)
    {
        if (_userDatabasePath is null || !TryReadExpectedRevision(fields, out var expectedRevision))
            return new CommandAck(command.MessageId, "error",
                "reasoning.default.revoke requires a User database and expectedRevision",
                RuntimeCommandOutcome.Rejected());
        try
        {
            using var store = new ReasoningPreferenceStore(_userDatabasePath);
            store.Reset(expectedRevision);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (ReasoningPreferenceConflictException)
        {
            return new CommandAck(command.MessageId, "error", "User reasoning preference revision conflict",
                RuntimeCommandOutcome.Rejected());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or UnauthorizedAccessException or SqliteException or OverflowException)
        {
            return new CommandAck(command.MessageId, "error", "Could not revoke the User reasoning preference",
                RuntimeCommandOutcome.Rejected());
        }
    }

    private CommandAck SelectRunReasoning(WireEnvelope command, Dictionary<string, string> fields)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run
            || !TryReadExpectedRevision(fields, out var expectedRevision)
            || !TryReadReasoningRequest(fields, out var request))
            return new CommandAck(command.MessageId, "error",
                "run.reasoning.select requires an active Run, explicit kind, and expectedRevision",
                RuntimeCommandOutcome.Rejected());

        long? before = null;
        try
        {
            var own = EventsForRun(_store.ReadFrom(session, 1), run);
            var projection = RunProjection.Replay(session, run, _codecs, own);
            if (projection.IsTerminal())
                return new CommandAck(command.MessageId, "error", "the active Run is terminal",
                    RuntimeCommandOutcome.Rejected());
            if (HasOpenModelStep(own))
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
            if (HasOpenToolCall(own))
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Deferred("ToolCallActive"));
            var currentRevision = projection.ReasoningSelection?.Revision ?? 0;
            if (expectedRevision != currentRevision)
                return new CommandAck(command.MessageId, "error", "Run reasoning preference revision conflict",
                    RuntimeCommandOutcome.Rejected());

            var sequenceBefore = _store.CurrentSequence(session);
            before = sequenceBefore;
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, projection.RootTask, LastLaneId()));
            new EventStream(_store, _codecs, session).Append(new RunReasoningPreferenceSelected(run,
                checked(currentRevision + 1), request, "User", command.MessageId), DurabilityClass.Barrier);
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException
            or OverflowException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value,
                    "Could not persist Run reasoning preference");
        }
    }

    private CommandAck RevokeRunReasoning(WireEnvelope command, Dictionary<string, string> fields)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run
            || !TryReadExpectedRevision(fields, out var expectedRevision))
            return new CommandAck(command.MessageId, "error",
                "run.reasoning.revoke requires an active Run and expectedRevision",
                RuntimeCommandOutcome.Rejected());

        long? before = null;
        try
        {
            var own = EventsForRun(_store.ReadFrom(session, 1), run);
            var projection = RunProjection.Replay(session, run, _codecs, own);
            if (projection.IsTerminal())
                return new CommandAck(command.MessageId, "error", "the active Run is terminal",
                    RuntimeCommandOutcome.Rejected());
            var current = projection.ReasoningSelection;
            var revision = current?.Revision ?? 0;
            if (expectedRevision != revision)
                return new CommandAck(command.MessageId, "error", "Run reasoning preference revision conflict",
                    RuntimeCommandOutcome.Rejected());
            if (current is null || !current.HasSelection || current.Source != "User")
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp());
            if (HasOpenModelStep(own))
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
            if (HasOpenToolCall(own))
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Deferred("ToolCallActive"));

            var sequenceBefore = _store.CurrentSequence(session);
            before = sequenceBefore;
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, projection.RootTask, LastLaneId()));
            new EventStream(_store, _codecs, session).Append(new RunReasoningPreferenceRevoked(run,
                checked(revision + 1), command.MessageId), DurabilityClass.Barrier);
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException
            or OverflowException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value,
                    "Could not revoke Run reasoning preference");
        }
    }

    private static bool TryReadExpectedRevision(Dictionary<string, string> fields, out long revision)
    {
        revision = 0;
        return fields.TryGetValue("expectedRevision", out var raw)
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out revision)
            && revision >= 0;
    }

    private static bool TryReadReasoningRequest(Dictionary<string, string> fields, out ReasoningRequest? request)
    {
        request = null;
        if (!fields.TryGetValue("kind", out var kind) || string.IsNullOrWhiteSpace(kind)) return false;
        if (kind == "off") return !fields.ContainsKey("budgetTokens");
        if (fields.TryGetValue("budgetTokens", out var rawBudget))
        {
            if (!int.TryParse(rawBudget, NumberStyles.None, CultureInfo.InvariantCulture, out var budget)
                || budget < 1024 || kind != "budget")
                return false;
            request = new ReasoningRequest(kind, budget);
        }
        else
        {
            if (kind == "budget") return false;
            request = new ReasoningRequest(kind, null);
        }
        return request.Kind == kind;
    }

    private static string ReasoningRequestJson(ReasoningRequest? request) => request is null
        ? "null"
        : "{" + JsonObj.Field("kind", request.Kind) + ",\"budgetTokens\":"
            + (request.BudgetTokens?.ToString(CultureInfo.InvariantCulture) ?? "null") + "}";

    private static bool TryReadUltraCodeLimits(Dictionary<string, string> fields,
        out ModeSwitchLimits limits, out IReadOnlyList<RunMode> allowedModes)
    {
        limits = null!;
        allowedModes = Array.Empty<RunMode>();
        bool Int(string key, out int value)
        {
            value = 0;
            return fields.TryGetValue(key, out var raw)
                && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        bool Long(string key, out long value)
        {
            value = 0;
            return fields.TryGetValue(key, out var raw)
                && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        if (!Int("maxAgents", out var agents) || !Int("maxDepth", out var depth)
            || !Int("maxTurns", out var turns) || !Int("maxToolCalls", out var tools)
            || !Long("maxElapsedSeconds", out var seconds)
            || !fields.TryGetValue("allowedModes", out var modeText)) return false;
        decimal? spend = null;
        if (fields.TryGetValue("maxSpendUsd", out var spendText) && spendText != "null")
        {
            if (!decimal.TryParse(spendText, NumberStyles.Number, CultureInfo.InvariantCulture, out var declaredSpend))
                return false;
            spend = declaredSpend;
        }
        var modes = new List<RunMode>();
        var modeParts = modeText.Split(',');
        if (modeParts.Length == 0 || modeParts.Any(string.IsNullOrWhiteSpace)) return false;
        foreach (var part in modeParts.Select(value => value.Trim()))
        {
            if (!TryParseRunMode(part, out var parsed) || modes.Contains(parsed)) return false;
            modes.Add(parsed);
        }
        if (modes.Count == 0) return false;
        limits = new ModeSwitchLimits(agents, depth, turns, tools, seconds, spend);
        try { limits.Validate(); }
        catch (ArgumentOutOfRangeException) { return false; }
        allowedModes = modes.AsReadOnly();
        return true;
    }

    private bool HasOpenModelStep(IReadOnlyList<DomainEvent> events)
    {
        var starts = new HashSet<(TurnId Turn, int Step)>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case ModelStepStarted started:
                    // Duplicate starts are malformed journal state; treating one as open fails closed.
                    if (!starts.Add((started.TurnId, started.StepIndex))) return true;
                    break;
                case ModelStepCompleted completed: starts.Remove((completed.TurnId, completed.StepIndex)); break;
                case ModelStepNotDispatched notDispatched:
                    starts.Remove((notDispatched.TurnId, notDispatched.StepIndex));
                    break;
            }
        }
        return starts.Count > 0;
    }

    private bool HasOpenModelStep(IReadOnlyList<DomainEvent> events, LaneId lane) =>
        HasOpenModelStep(events.Where(evt => evt.LaneId == lane).ToArray());

    /// <summary>
    /// A completed model step does not mean its tool loop is finished: ToolCallStarted is
    /// deliberately journaled after ModelStepCompleted. Do not apply a mode downgrade/revocation
    /// while any tool lifecycle is still nonterminal, including an uncertain effect awaiting
    /// reconciliation.
    /// </summary>
    private bool HasOpenToolCall(IReadOnlyList<DomainEvent> events)
    {
        var calls = new Dictionary<ToolCallId, ToolCallState>();
        foreach (var evt in events)
        {
            var payload = _codecs.Decode(evt);
            if (payload is ToolCallRequested requested)
            {
                if (!calls.TryAdd(requested.ToolCallId, ToolCallState.Requested)) return true;
                continue;
            }

            var id = payload switch
            {
                ToolCallPrepared value => value.ToolCallId,
                ToolCallRejected value => value.ToolCallId,
                PermissionEvaluated value => value.ToolCallId,
                PermissionRequested value => value.ToolCallId,
                PermissionGranted value => value.ToolCallId,
                PermissionDenied value => value.ToolCallId,
                ToolCallAuthorized value => value.ToolCallId,
                ToolCallStarted value => value.ToolCallId,
                ToolCallSucceeded value => value.ToolCallId,
                ToolCallFailed value => value.ToolCallId,
                ToolCallEffectUnknown value => value.ToolCallId,
                ToolCallReconciled value => value.ToolCallId,
                ToolCallCancelled value => value.ToolCallId,
                _ => (ToolCallId?)null,
            };
            if (id is not { } callId) continue;
            if (!calls.TryGetValue(callId, out var state)) return true;
            try { calls[callId] = StateMachines.ApplyToolCall(state, payload); }
            catch (InvalidStateTransitionException) { return true; }
        }

        return calls.Values.Any(state => state is not (ToolCallState.Succeeded or ToolCallState.Failed
            or ToolCallState.Rejected or ToolCallState.Cancelled or ToolCallState.Reconciled));
    }

    private bool HasOpenToolCall(IReadOnlyList<DomainEvent> events, LaneId lane) =>
        HasOpenToolCall(events.Where(evt => evt.LaneId == lane).ToArray());

    public SessionId? LastSessionId() => _lastSessionId;

    /// <summary>Revalidates the Session and active Run while writer ownership is held.</summary>
    internal bool MatchesTrustedActionScope(string? expectedSessionId, string? expectedRunId,
        long? expectedRevision, bool checkRevision, bool requirePlanRun)
    {
        var sessionId = _lastSessionId?.ToString();
        if (!string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal)) return false;
        var session = _lastSessionId;
        var activeRun = session is { } current
            ? new OmniCore.Engine.RunControlService(_store, _codecs).ActiveRun(current)
            : null;
        if (!string.Equals(activeRun?.ToString(), expectedRunId, StringComparison.Ordinal)) return false;
        if (activeRun is null) return !checkRevision || expectedRevision is null;
        var runEvents = EventsForRun(_store.ReadFrom(session!, 1), activeRun);
        var projection = OmniCore.Engine.RunProjection.Replay(session!, activeRun, _codecs, runEvents);
        var created = runEvents.Select(_codecs.Decode).OfType<RunCreated>().SingleOrDefault(item => item.RunId == activeRun);
        var authority = projection.ModeAuthority ?? (created is null ? null : RunModeAuthority.Legacy(created));
        if (authority is null) return false;
        if (checkRevision && authority.Revision != expectedRevision) return false;
        return !requirePlanRun || authority.Mode == RunMode.Plan;
    }

    internal string TrustedActionScopeJson()
    {
        var session = _lastSessionId;
        var activeRun = session is { } current
            ? new OmniCore.Engine.RunControlService(_store, _codecs).ActiveRun(current)
            : null;
        if (session is null || activeRun is null)
            return "{\"sessionId\":" + (session is null ? "null" : QuoteScope(session.ToString()))
                + ",\"runId\":null,\"mode\":null,\"revision\":null}";
        var runEvents = EventsForRun(_store.ReadFrom(session, 1), activeRun);
        var projection = OmniCore.Engine.RunProjection.Replay(session, activeRun, _codecs, runEvents);
        var created = runEvents.Select(_codecs.Decode).OfType<RunCreated>().SingleOrDefault(item => item.RunId == activeRun);
        var authority = projection.ModeAuthority ?? (created is null ? null : RunModeAuthority.Legacy(created));
        return "{\"sessionId\":" + QuoteScope(session.Value.ToString())
            + ",\"runId\":" + QuoteScope(activeRun.Value.ToString())
            + ",\"mode\":" + (authority is null ? "null" : QuoteScope(RunModeWireName(authority.Mode)))
            + ",\"revision\":" + (authority?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + "}";
    }

    private static string QuoteScope(string value) => "\"" + JsonObj.Escape(value) + "\"";

    /// <summary>Consume el origen de un PromptCommand para persistirlo en el próximo UserInputReceived.</summary>
    public string? ConsumePromptOrigin()
    {
        var origin = _pendingPromptOrigin;
        _pendingPromptOrigin = null;
        return origin;
    }

    public RunId? LastRunId() => _lastRunId;

    /// <summary>
    /// Motivo (o null) de un bloqueo de la recuperación del Run real al arrancar. Visible para que
    /// el cliente/CLI pueda reflejar que el run NO es continua automáticamente y pida intervención.
    /// </summary>
    public string? LastRecoveryProblem() => _recoveryProblem;

    /// <summary>Closed maintenance diagnostic for startup worktree cleanup; null means that the
    /// sweep completed or was not applicable. It contains no Git/process output.</summary>
    public string? LastWorktreeMaintenanceProblem() => _worktreeMaintenanceProblem;

    private CommandAck StartExplorerRun(WireEnvelope command, Dictionary<string, string> fields)
    {
        var objective = fields.TryGetValue("objective", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(objective))
        {
            return new CommandAck(command.MessageId, "error", "falta el objetivo del Explorer",
                RuntimeCommandOutcome.Rejected());
        }

        // `explore.start` is the explicit PLAN/exploration flow; the product default applies to
        // ordinary `session.input`, not to this command's declared semantics.
        return StartRunAct(command, objective!, Path.GetFullPath("."), RunMode.Plan);
    }

    /// <summary>
    /// <c>act</c>: crea un Run Act REAL reutilizando el Explorer (vertical M3, ADR-0035 §3, ADR-0044 §5).
    /// Acepta <c>objective</c> (la instrucción) y opcionalmente <c>workspace</c> (raíz; por defecto el cwd).
    /// Emite <c>SessionCreated</c> + <c>WorkspaceRootEstablished</c> con identidad durable + el run Act
    /// completo (RunCreated/RunStarted/Task/Lane/Plan). El plan llama al Turn de Explorer con las tools
    /// filesystem.read/filesystem.patch bajo la política efectiva; NO es un sustituto del sim y no
    /// autoaprueba Ask (sin cliente interactivo, Ask → Deny, ADR-0003).
    /// </summary>
    private CommandAck StartActRun(WireEnvelope command, Dictionary<string, string> fields)
    {
        var objective = fields.TryGetValue("objective", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(objective))
        {
            return new CommandAck(command.MessageId, "error", "falta el objetivo del act",
                RuntimeCommandOutcome.Rejected());
        }

        var workspace = fields.TryGetValue("workspace", out var w) ? w : null;
        string workspacePath;
        try
        {
            workspacePath = workspace is null || workspace!.Length == 0
                ? Path.GetFullPath(".")
                : Path.GetFullPath(workspace!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            // Path admission precedes all journal writes. Do not leak the supplied path
            // or mistake an invalid request for an indeterminate committed command.
            return new CommandAck(command.MessageId, "error", "Workspace inválido para act.",
                RuntimeCommandOutcome.Rejected());
        }
        return StartRunAct(command, objective!, workspacePath);
    }

    /// <summary>Creación compartida de un Run Act real con raíz durable y verificable.</summary>
    private CommandAck StartRunAct(WireEnvelope command, string objective, string workspacePath,
        RunMode mode = RunMode.Act)
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var taskId = TaskId.New();
        var laneId = LaneId.New();
        var planId = PlanId.New();
        var stream = new EventStream(_store, _codecs, sessionId);
        var now = DateTimeOffset.UtcNow;
        var budget = new TaskBudget(null, null, null, null);
        try
        {
            var reasoningPreference = ReadReasoningPreference();
            var physicalWorkspaceRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspacePath);
            // Establish the existing workspace marker before committing its reference. The
            // journal initialization is all-or-nothing; no incomplete Run becomes selectable.
            var durableIdentity = WorkspaceRootIdentity.Establish(workspacePath);
            var hookPaths = _platformPathsFactory();
            var lifecycleHooks = ProjectExtensionHooks.Load(physicalWorkspaceRoot, hookPaths,
                new WorkspaceTrustStore(hookPaths).IsTrusted(physicalWorkspaceRoot),
                _lifecycleRpcInvokerFactory());
            var sessionStartingApplications = DispatchCreationHooks(lifecycleHooks,
                LifecyclePoint.SessionStarting, "session.created", runId, taskId, laneId);
            var securityDefaults = new SessionSecurityDefaultConfiguration(hookPaths);
            using (securityDefaults.AcquireAdmissionLease())
            {
            var securityDefault = securityDefaults.Read();
            var creationEvents = new List<DomainEventPayload>
            {
                new SessionCreated(sessionId,
                    WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physicalWorkspaceRoot)).ToString(),
                    workspacePath, ProfileId.New(), now),
                SessionSecurityPolicySet.FromPreset(sessionId, 1, securityDefault.Category,
                    SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault, now),
                new SessionRoutingPolicySet(sessionId, _newSessionRoutingPolicy),
                new WorkspaceRootEstablished(sessionId, workspacePath, now, durableIdentity),
                new RunCreated(runId, sessionId, objective, mode,
                    ExecutionStrategy.Direct, FailurePolicy.BlockDependents, budget, taskId, now),
                new RunModeAuthoritySelected(new RunModeAuthority(runId, 1, mode,
                    ExecutionStrategy.Direct, ProductEffort.Standard, false, false, 1,
                    RunModeAuthority.ObjectiveDigestFor(objective), 1, null), command.MessageId, "RunCreated"),
            };
            // Capture only an explicit User choice. Absence remains absence, so a new Run
            // does not manufacture a reasoning selection or alter legacy event counts.
            if (reasoningPreference.HasSelection)
                creationEvents.Add(new RunReasoningPreferenceSelected(runId, 1, reasoningPreference.Request,
                    "UserDefault", "RunCreated", reasoningPreference.Revision));
            creationEvents.AddRange(new DomainEventPayload[]
            {
                new RunStarted(runId),
                new TaskCreated(taskId, runId, objective, Array.Empty<TaskDependency>(), budget),
                new TaskReady(taskId),
                new LaneCreated(laneId, taskId, _agentProfiles.DefaultProfile?.Id ?? ProfileId.New(),
                    _agentProfiles.DefaultProfile?.Revision, _agentProfiles.DefaultProfile is { } rootProfile
                        ? AgentProfileFingerprint.Hash(rootProfile) : null),
                new LaneStarted(laneId),
                new TaskStarted(taskId, laneId),
                new PlanCreated(planId, runId, PlanItemId.New(), objective),
            });
            stream.AppendBatch(creationEvents, DurabilityClass.Barrier);
            }
            AppendLifecycleApplications(stream, sessionStartingApplications,
                new ExecutionScopeState(runId, taskId, laneId));
            foreach (var (point, eventType, pointTask, pointLane) in new[]
            {
                (LifecyclePoint.SessionStarted, "session.started", (TaskId?)taskId, (LaneId?)laneId),
                (LifecyclePoint.RunStarting, "run.started", (TaskId?)taskId, (LaneId?)laneId),
                (LifecyclePoint.TaskCreated, "task.created", (TaskId?)taskId, (LaneId?)laneId),
                (LifecyclePoint.LaneStarting, "lane.started", (TaskId?)taskId, (LaneId?)laneId),
            })
                AppendLifecycleApplications(stream,
                    DispatchCreationHooks(lifecycleHooks, point, eventType, runId, pointTask, pointLane),
                    new ExecutionScopeState(runId, pointTask, pointLane));

            _lastSessionId = sessionId;
            _lastRunId = runId;
            _lastSnapshot = null;
            _lastWorkingStateText = "";
            _lastSnapshot = MaterializeFromJournal(sessionId, runId);
            SaveLastSession();
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), sessionId, 0,
                new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (Exception)
        {
            return FailedDurableCommandAck(command, sessionId, 0,
                "No se pudo completar la creación del Run.", newSession: true);
        }
    }

    private static IReadOnlyList<HookApplied> DispatchCreationHooks(IReadOnlyList<IHook> hooks,
        LifecyclePoint point, string eventType, RunId? runId, TaskId? taskId = null, LaneId? laneId = null)
    {
        if (hooks.Count == 0) return [];
        var result = new LifecycleHookDispatcher().DispatchAsync(hooks,
            new HookContext(point, EventType: eventType, TaskId: taskId?.ToString(), LaneId: laneId?.ToString()),
            CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (result.Denied || result.Validations.Count > 0 || result.DeniedToolIds.Count > 0)
            throw new InvalidOperationException("Lifecycle hook requested an unsupported restriction at " + point + ".");
        return result.Applications.Select(application => new HookApplied(runId, null,
            application.HookId, point.ToString(), application.Result, application.DetailIds)).ToArray();
    }

    private static void AppendLifecycleApplications(EventStream stream, IReadOnlyList<HookApplied> applications,
        ExecutionScopeState? scope = null)
    {
        if (applications.Count > 0)
        {
            using var execution = scope is { } executionScope ? ExecutionScope.Begin(executionScope) : null;
            stream.AppendBatch(applications, DurabilityClass.Barrier);
        }
    }

    private void DispatchRunStartLifecycle(SessionId session, RunId runId)
    {
        var events = _store.ReadFrom(session, 1);
        var createdTask = events.Select(_codecs.Decode).OfType<TaskCreated>().LastOrDefault(task => task.RunId == runId);
        var createdLane = createdTask is null ? null : events.Select(_codecs.Decode).OfType<LaneCreated>()
            .LastOrDefault(lane => lane.TaskId == createdTask.TaskId);
        var hooks = LoadLifecycleHooks(session, events);
        var stream = new EventStream(_store, _codecs, session);
        foreach (var (point, eventType) in new[]
        {
            (LifecyclePoint.RunStarting, "run.started"),
            (LifecyclePoint.TaskCreated, "task.created"),
            (LifecyclePoint.LaneStarting, "lane.started"),
        })
            AppendLifecycleApplications(stream, DispatchCreationHooks(hooks, point, eventType, runId,
                createdTask?.TaskId, createdLane?.LaneId), createdTask is null ? null
                    : new ExecutionScopeState(runId, createdTask.TaskId, createdLane?.LaneId));
    }

    internal void DispatchCreatedTaskLifecycle(SessionId session, RunId run, TaskId task, LaneId lane)
    {
        var events = _store.ReadFrom(session, 1);
        var hooks = LoadLifecycleHooks(session, events);
        AppendLifecycleApplications(new EventStream(_store, _codecs, session), DispatchCreationHooks(hooks,
            LifecyclePoint.TaskCreated, "task.created", run, task, lane), new ExecutionScopeState(run, task, lane));
    }

    internal void DispatchStartedLaneLifecycle(SessionId session, RunId run, TaskId task, LaneId lane)
    {
        var events = _store.ReadFrom(session, 1);
        var hooks = LoadLifecycleHooks(session, events);
        AppendLifecycleApplications(new EventStream(_store, _codecs, session), DispatchCreationHooks(hooks,
            LifecyclePoint.LaneStarting, "lane.started", run, task, lane), new ExecutionScopeState(run, task, lane));
    }

    private IReadOnlyList<IHook> LoadLifecycleHooks(SessionId session, IReadOnlyList<DomainEvent>? events = null)
    {
        events ??= _store.ReadFrom(session, 1);
        var established = events.Select(_codecs.Decode).OfType<WorkspaceRootEstablished>().LastOrDefault();
        if (established is null)
        {
            // Legacy sessions can still load explicitly consented User/Trusted hooks. Without a
            // durable root identity they never gain Project scope, even when a root was configured.
            var userScopeRoot = !string.IsNullOrWhiteSpace(_workspaceRoot)
                && Path.IsPathFullyQualified(_workspaceRoot)
                ? _workspaceRoot : Path.GetFullPath(".");
            var pathsWithoutProjectAuthority = _platformPathsFactory();
            return ProjectExtensionHooks.Load(userScopeRoot, pathsWithoutProjectAuthority,
                workspaceTrusted: false, _lifecycleRpcInvokerFactory());
        }
        var physicalRoot = TryVerifyRecordedWorkspaceRoot(established.CanonicalRoot,
            established.DurableIdentity);
        if (physicalRoot is null && !string.IsNullOrWhiteSpace(_workspaceRoot))
            physicalRoot = ResolveConfiguredWorkspaceRoot(events, _workspaceRoot!, established.DurableIdentity);

        if (physicalRoot is null) return [];
        var paths = _platformPathsFactory();
        var securityDefault = new SessionSecurityDefaultConfiguration(paths).Read();
        return ProjectExtensionHooks.Load(physicalRoot, paths,
            new WorkspaceTrustStore(paths).IsTrusted(physicalRoot), _lifecycleRpcInvokerFactory());
    }

    /// <summary>
    /// A journal path can be redacted because it is display/path data. Reopening may therefore use
    /// a configured path only after proving both the physical-root identity and the canonical
    /// WorkspaceId recorded for this session. This never accepts cwd or a merely matching project.
    /// </summary>
    private string? TryVerifyRecordedWorkspaceRoot(string candidate,
        string durableIdentity)
    {
        if (!Path.IsPathFullyQualified(candidate) || !Directory.Exists(candidate)
            || string.IsNullOrWhiteSpace(durableIdentity)) return null;

        try
        {
            return WorkspaceRootIdentity.Verify(candidate, durableIdentity) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private string? ResolveConfiguredWorkspaceRoot(IReadOnlyList<DomainEvent> events, string candidate,
        string durableIdentity)
    {
        if (!Path.IsPathFullyQualified(candidate) || !Directory.Exists(candidate)
            || string.IsNullOrWhiteSpace(durableIdentity)) return null;

        try
        {
            var physical = ProjectIdentity.ResolvePhysicalWorkspaceRoot(candidate);
            if (!WorkspaceRootIdentity.Verify(physical, durableIdentity)) return null;
            var sessionWorkspaceId = events.Select(_codecs.Decode).OfType<SessionCreated>()
                .LastOrDefault()?.WorkspaceId;
            if (string.IsNullOrWhiteSpace(sessionWorkspaceId)) return null;
            // SessionCreated may have captured the configured lexical path (legacy StartSession)
            // or the physical path (StartRunAct); accept either only alongside the physical hash.
            var configuredId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(candidate)).ToString();
            var physicalId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physical)).ToString();
            return string.Equals(sessionWorkspaceId, configuredId, StringComparison.Ordinal)
                || string.Equals(sessionWorkspaceId, physicalId, StringComparison.Ordinal)
                ? physical : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private void DispatchRunTerminalLifecycle(SessionId session, RunId runId, LifecyclePoint point)
    {
        var events = _store.ReadFrom(session, 1);
        AppendLifecycleApplications(new EventStream(_store, _codecs, session),
            DispatchCreationHooks(LoadLifecycleHooks(session, events), point, "run.failed", runId),
            new ExecutionScopeState(runId));
    }

    /// <summary>Store del servidor (para el Turn de Explorer, que persiste en el mismo journal).</summary>
    internal IEventStore AcquireStore() => _store;

    /// <summary>Registro de codecs del servidor.</summary>
    internal IEventCodecRegistry AcquireCodecs() => _codecs;

    /// <summary>El LaneId del último run (del journal) o null si no hay run persistido.</summary>
    public LaneId? LastLaneId()
    {
        if (_lastSessionId is null || _lastRunId is null)
        {
            return null;
        }

        try
        {
            var tail = _store.ReadFrom(_lastSessionId!, 1);
            // P0-4: el lane del Run actual (no el primero de la sesión): captura desde el
            // RunCreated que coincide con _lastRunId.
            var inOwnRun = false;
            foreach (var evt in tail)
            {
                if (evt.Type.ToString().Equals("run.created", StringComparison.Ordinal))
                {
                    var payload = _codecs.Decode(evt);
                    var runCreated = payload as RunCreated;
                    inOwnRun = runCreated is not null
                        && runCreated!.RunId.ToString().Equals(_lastRunId!.ToString(), StringComparison.Ordinal);
                    continue;
                }

                if (!inOwnRun || !evt.Type.ToString().Equals("lane.created", StringComparison.Ordinal))
                {
                    continue;
                }

                var lanePayload = _codecs.Decode(evt);
                if (lanePayload is LaneCreated lane)
                {
                    return lane.LaneId;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Fingerprint de ejecución del último Turn del Run activo (ADR-0017): qué configuración efectiva recibió.
    /// Solo identidades y hashes, nunca el contenido de los componentes. <c>{"fingerprint":null}</c> si ningún Turn
    /// lo registró.
    /// </summary>
    private string ReadLastTurnFingerprint()
    {
        if (_lastSessionId is null || _lastRunId is null) return "{\"fingerprint\":null}";
        TurnStarted? latest = null;
        foreach (var evt in EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId))
            if (_codecs.Decode(evt) is TurnStarted { Fingerprint: not null } started) latest = started;
        if (latest?.Fingerprint is not { } fingerprint) return "{\"fingerprint\":null}";
        var components = fingerprint.Components.Select(component => "{" + JsonObj.Field("name", component.Name)
            + "," + JsonObj.Field("version", component.Version) + "," + JsonObj.Field("hash", component.Hash.ToString()) + "}");
        return "{\"fingerprint\":{" + JsonObj.Field("turnId", latest.TurnId.ToString()) + ","
            + JsonObj.Field("modelKey", fingerprint.ModelKey) + "," + JsonObj.Field("build", fingerprint.Build) + ","
            + JsonObj.Field("harnessPolicy", fingerprint.HarnessPolicyHash) + ","
            + JsonObj.Field("toolkit", fingerprint.ToolkitHash) + ","
            + JsonObj.Field("tokenizer", fingerprint.TokenizerHash) + ","
            + JsonObj.Field("contextPolicy", fingerprint.ContextPolicyHash) + ","
            + JsonObj.Field("overrides", fingerprint.OverridesHash) + ","
            + JsonObj.Field("modelPolicy", fingerprint.ModelPolicyHash) + ",\"components\":["
            + string.Join(",", components) + "]}}";
    }

    private string? ReadPersistedContextSnapshot()
    {
        if (_artifacts is null || _lastSessionId is null || _lastRunId is null) return null;
        try
        {
            var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
            ArtifactRef? snapshotRef = null;
            foreach (var evt in events)
            {
                if (_codecs.Decode(evt) is TurnStarted started && started.ContextSnapshotRef is not null)
                    snapshotRef = started.ContextSnapshotRef;
            }
            if (snapshotRef is null) return null;

            var snapshotJson = _artifacts.GetText(snapshotRef.Hash);
            if (snapshotJson is null) return null;
            using var source = System.Text.Json.JsonDocument.Parse(snapshotJson);
            using var output = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(output))
            {
                var root = source.RootElement;
                writer.WriteStartObject();
                writer.WriteString("snapshotId", root.GetProperty("snapshotId").GetString());
                writer.WriteString("fingerprint", root.GetProperty("fingerprint").GetString());
                if (root.TryGetProperty("snapshotFingerprint", out var snapshotFingerprint))
                    writer.WriteString("snapshotFingerprint", snapshotFingerprint.GetString());
                writer.WriteNumber("tokenCount", root.GetProperty("tokenCount").GetInt32());
                if (root.TryGetProperty("tokenAccuracy", out var accuracy))
                {
                    writer.WritePropertyName("tokenAccuracy");
                    accuracy.WriteTo(writer);
                }
                if (root.TryGetProperty("tokenBudget", out var budget))
                {
                    writer.WritePropertyName("tokenBudget");
                    budget.WriteTo(writer);
                }
                if (root.TryGetProperty("overflowed", out var overflowed))
                {
                    writer.WritePropertyName("overflowed");
                    overflowed.WriteTo(writer);
                }
                writer.WriteStartArray("items");
                foreach (var item in root.GetProperty("items").EnumerateArray())
                {
                    writer.WriteStartObject();
                    Copy(item, writer, "id", "kind", "tokens", "tokenAccuracy", "priority", "contributor",
                        "category", "source", "scope", "sensitive", "refs");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("diagnostics");
                if (root.TryGetProperty("diagnostics", out var diagnostics))
                {
                    foreach (var diagnostic in diagnostics.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        Copy(diagnostic, writer, "itemId", "decision", "tokens", "reason", "contributor",
                            "category", "source", "scope", "sensitive");
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or KeyNotFoundException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Copy(System.Text.Json.JsonElement source, System.Text.Json.Utf8JsonWriter writer,
        params string[] properties)
    {
        foreach (var name in properties)
            if (source.TryGetProperty(name, out var value))
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
    }

    private InteractionRequested? FindPendingMemoryPromotion(SessionId session, InteractionId interactionId)
    {
        var pending = PendingMemoryPromotionRequests(session);
        return pending.SingleOrDefault(request => request.InteractionId == interactionId);
    }

    private InteractionRequested? FindPendingMemoryPromotion(SessionId session, string candidateId)
    {
        var matches = PendingMemoryPromotionRequests(session)
            .Where(request => ReadMemoryPromotionIdentity(request.SubjectJson).CandidateId == candidateId).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("sessionPreferences.error.memory_approval_ambiguous");
        return matches.SingleOrDefault();
    }

    private InteractionRequested[] PendingMemoryPromotionRequests(SessionId session)
    {
        var pending = new Dictionary<InteractionId, InteractionRequested>();
        foreach (var evt in _store.ReadFrom(session, 1))
        {
            switch (_codecs.Decode(evt))
            {
                case InteractionRequested request when request.Kind == InteractionKind.MemoryPromotion:
                    pending[request.InteractionId] = request;
                    break;
                case InteractionResolved resolved:
                    pending.Remove(resolved.InteractionId);
                    break;
                case InteractionExpired expired:
                    pending.Remove(expired.InteractionId);
                    break;
            }
        }
        return pending.Values.ToArray();
    }

    private static (string CandidateId, string ConflictMemoryId) ReadMemoryPromotionIdentity(string subjectJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(subjectJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("operation", out var operation) || operation.GetString() != "memory.promotion"
                || !root.TryGetProperty("candidateId", out var candidate) || candidate.ValueKind != System.Text.Json.JsonValueKind.String
                || !root.TryGetProperty("conflictMemoryId", out var conflict) || conflict.ValueKind != System.Text.Json.JsonValueKind.String)
                throw new InvalidDataException("sessionPreferences.error.memory_approval_invalid");
            var candidateId = candidate.GetString(); var conflictId = conflict.GetString();
            if (string.IsNullOrWhiteSpace(candidateId) || string.IsNullOrWhiteSpace(conflictId))
                throw new InvalidDataException("sessionPreferences.error.memory_approval_invalid");
            return (candidateId, conflictId);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException("sessionPreferences.error.memory_approval_invalid", exception);
        }
    }

    private void ApplyMemoryPromotionDecision(SessionId session, InteractionRequested request, string optionId,
        string commandId, RunControlService control, bool resolveInteraction = true)
    {
        if (request.Kind != InteractionKind.MemoryPromotion)
            throw new InvalidDataException("sessionPreferences.error.memory_approval_invalid");
        var identity = ReadMemoryPromotionIdentity(request.SubjectJson);
        var optionIds = new HashSet<string>(StringComparer.Ordinal);
        using (var options = System.Text.Json.JsonDocument.Parse(request.OptionsJson))
        {
            foreach (var option in options.RootElement.EnumerateArray())
                if (option.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
                    optionIds.Add(id.GetString()!);
        }
        if (request.DefaultOptionId != "reject" || !optionIds.SetEquals(["promote", "reject"])
            || optionId is not ("promote" or "reject"))
            throw new InvalidInteractionOptionException(request.InteractionId, optionId);

        if (_workspaceRoot is null) throw new InvalidOperationException("sessionPreferences.error.session_unavailable");
        var store = new SessionMemoryStore(OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), _workspaceRoot));
        IReadOnlyList<DomainEventPayload> ReadJournal() => _store.ReadFrom(session, 1).Select(_codecs.Decode).ToArray();
        void Emit(DomainEventPayload payload) => new EventStream(_store, _codecs, session)
            .Append(payload, DurabilityClass.Barrier);
        var policy = new SessionMemoryPolicy(session, store, ReadJournal, Emit);
        var proposed = ReadJournal().OfType<MemoryCandidateProposed>()
            .LastOrDefault(item => item.CandidateId == identity.CandidateId)
            ?? throw new InvalidDataException("sessionPreferences.error.memory_approval_invalid");
        if (proposed.Decision != "ask"
            || !string.Equals(proposed.ConflictMemoryId ?? "", identity.ConflictMemoryId, StringComparison.Ordinal))
            throw new InvalidDataException("sessionPreferences.error.memory_approval_invalid");
        if (optionId == "promote") policy.Promote(identity.CandidateId, identity.ConflictMemoryId);
        else policy.Reject(identity.CandidateId);
        if (resolveInteraction) control.Respond(session, request.InteractionId, optionId, commandId);
    }

    private CommandAck ChangeSessionMemory(WireEnvelope command, string commandName,
        IReadOnlyDictionary<string, string> fields, bool trustedUserAction, CancellationToken cancellationToken)
    {
        if (!trustedUserAction)
            return new CommandAck(command.MessageId, "error", "Session memory changes require a trusted user action.",
                RuntimeCommandOutcome.Rejected());
        if (_lastSessionId is not { } session || _workspaceRoot is null)
            return new CommandAck(command.MessageId, "error", "An existing Session and Workspace are required.",
                RuntimeCommandOutcome.Rejected());
        var hasExpectedSession = fields.ContainsKey("expectedSessionId");
        var hasExpectedRevision = fields.ContainsKey("expectedRevision");
        if (hasExpectedRevision && !hasExpectedSession)
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.trusted_action_required",
                RuntimeCommandOutcome.Rejected());
        if (fields.TryGetValue("expectedSessionId", out var expectedSessionId)
            && !string.Equals(expectedSessionId, session.ToString(), StringComparison.Ordinal))
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.session_changed",
                RuntimeCommandOutcome.Rejected());
        try
        {
            var store = new SessionMemoryStore(OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), _workspaceRoot));
            IReadOnlyList<DomainEventPayload> ReadJournal() => _store.ReadFrom(session, 1).Select(_codecs.Decode).ToArray();
            if (hasExpectedRevision)
            {
                var expectedRevision = fields.GetValueOrDefault("expectedRevision");
                var currentJournal = ReadJournal();
                var currentRecords = store.Read(MemoryScope.ForSession(session), cancellationToken);
                var currentCandidates = SessionMemoryPolicy.ProjectForPresentation(currentJournal);
                var currentRevision = ComputeSessionMemoryRevision(session, currentRecords, currentCandidates);
                if (!IsSha256(expectedRevision) || !FixedHashEquals(expectedRevision!, currentRevision))
                    return new CommandAck(command.MessageId, "error", "sessionPreferences.error.memory_changed",
                        RuntimeCommandOutcome.Rejected());
            }
            void Emit(DomainEventPayload payload) => new EventStream(_store, _codecs, session)
                .Append(payload, DurabilityClass.Barrier);
            var memoryPolicy = new SessionMemoryPolicy(session, store, ReadJournal, Emit);
            if (commandName == "memory.session.forget")
            {
                if (!CanMutateSessionPreference(session))
                    return new CommandAck(command.MessageId, "busy", "sessionPreferences.error.execution_busy",
                        RuntimeCommandOutcome.Deferred("session_preferences_busy"));
                var id = fields.GetValueOrDefault("memoryId") ?? "";
                var removed = memoryPolicy.Forget(id, cancellationToken);
                return removed
                    ? new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted())
                    : new CommandAck(command.MessageId, "error", "No Session memory item has that id.", RuntimeCommandOutcome.Rejected());
            }

            var candidateId = fields.GetValueOrDefault("candidateId") ?? "";
            if (commandName is "memory.session.reject" or "memory.session.promote")
            {
                if (fields.ContainsKey("supersedesId"))
                    throw new InvalidOperationException("sessionPreferences.error.memory_supersedes_host_only");
                var pending = FindPendingMemoryPromotion(session, candidateId)
                    ?? throw new InvalidOperationException("sessionPreferences.error.memory_approval_not_pending");
                if (!CanMutateSessionPreference(session))
                    return new CommandAck(command.MessageId, "busy", "sessionPreferences.error.execution_busy",
                        RuntimeCommandOutcome.Deferred("session_preferences_busy"));
                var control = new RunControlService(_store, _codecs);
                ApplyMemoryPromotionDecision(session, pending,
                    commandName == "memory.session.promote" ? "promote" : "reject", command.MessageId, control);
            }
            else
            {
                if (!CanMutateSessionPreference(session))
                    return new CommandAck(command.MessageId, "busy", "sessionPreferences.error.execution_busy",
                        RuntimeCommandOutcome.Deferred("session_preferences_busy"));
                var content = fields.GetValueOrDefault("content") ?? "";
                if (content.Length > 2048 || string.IsNullOrWhiteSpace(content))
                    throw new ArgumentException("sessionPreferences.error.memory_invalid");
                _ = memoryPolicy.Propose(content, MemoryKind.Fact, MemoryCandidateOrigin.User);
            }
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new CommandAck(command.MessageId, "error", exception.Message, RuntimeCommandOutcome.Rejected());
        }
    }

    private static string ComputeSessionMemoryRevision(SessionId session,
        IReadOnlyList<MemoryRecord> records, IReadOnlyList<MemoryCandidateView> candidates)
    {
        var canonical = new System.Text.StringBuilder("session-memory-state-v1\n");
        static void Part(System.Text.StringBuilder target, string? value)
        {
            value ??= "";
            target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
        }
        Part(canonical, session.ToString());
        foreach (var record in records.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Part(canonical, "record"); Part(canonical, record.Id); Part(canonical, record.Scope.Level.ToString());
            Part(canonical, record.Scope.Id); Part(canonical, record.Kind.ToString()); Part(canonical, record.Content);
            Part(canonical, record.Importance.ToString("R", CultureInfo.InvariantCulture));
            Part(canonical, record.Confidence.ToString("R", CultureInfo.InvariantCulture));
            Part(canonical, record.Source.ToString()); Part(canonical, record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            Part(canonical, record.LastUsedAt?.ToString("O", CultureInfo.InvariantCulture));
            Part(canonical, record.Expiration.ToString()); Part(canonical, record.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture));
            Part(canonical, record.ExpireAfterUnusedDays?.ToString(CultureInfo.InvariantCulture)); Part(canonical, record.Supersedes);
            foreach (var tag in record.Tags) Part(canonical, tag);
            Part(canonical, "tags-end");
            foreach (var evidence in record.EvidenceRefs) Part(canonical, evidence);
            Part(canonical, "evidence-end");
        }
        foreach (var candidate in candidates.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Part(canonical, "candidate"); Part(canonical, candidate.Id); Part(canonical, candidate.Kind.ToString());
            Part(canonical, candidate.Content); Part(canonical, candidate.Origin); Part(canonical, candidate.Decision);
            Part(canonical, candidate.Reason); Part(canonical, candidate.ConflictMemoryId);
            Part(canonical, candidate.Occurrences.ToString(CultureInfo.InvariantCulture));
            Part(canonical, candidate.ProposedAt.ToString("O", CultureInfo.InvariantCulture));
        }
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private bool CanMutateSessionPreference(SessionId session)
    {
        if (HasSessionSecurityInFlightAuthority(session)) return false;
        var history = _store.ReadFrom(session, 1);
        var activeRun = new RunControlService(_store, _codecs).ActiveRun(session);
        return activeRun is null || !IsSessionTurnInFlight(history, activeRun);
    }

    private CommandAck ChangeSessionSkillActivation(WireEnvelope command, string commandName,
        IReadOnlyDictionary<string, string> fields, bool trustedUserAction, CancellationToken cancellationToken)
    {
        if (_lastSessionId is not { } session)
            return new CommandAck(command.MessageId, "error", "An existing Session is required to activate a skill.",
                RuntimeCommandOutcome.Rejected());
        var hasExpectedSession = fields.ContainsKey("expectedSessionId");
        var hasExpectedRevision = fields.ContainsKey("expectedRevision");
        if (hasExpectedRevision && !hasExpectedSession)
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.trusted_action_required",
                RuntimeCommandOutcome.Rejected());
        var skillId = fields.GetValueOrDefault("skillId") ?? "";
        var scopeText = fields.GetValueOrDefault("scope") ?? ScopeLevel.Project.ToString();
        if (!Enum.TryParse<ScopeLevel>(scopeText, ignoreCase: false, out var skillScope)
            || skillScope is not (ScopeLevel.BuiltIn or ScopeLevel.User or ScopeLevel.Project
                or ScopeLevel.Workspace or ScopeLevel.Session))
            return new CommandAck(command.MessageId, "error", "Skill scope is unsupported.",
                RuntimeCommandOutcome.Rejected());
        if (fields.ContainsKey("expectedSessionId") && !trustedUserAction)
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.trusted_action_required",
                RuntimeCommandOutcome.Rejected());
        if (fields.TryGetValue("expectedSessionId", out var expectedSessionId)
            && !string.Equals(expectedSessionId, session.ToString(), StringComparison.Ordinal))
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.session_changed",
                RuntimeCommandOutcome.Rejected());
        try
        {
            var paths = _platformPathsFactory();
            var workspaceRoot = _workspaceRoot;
            var trusted = workspaceRoot is not null && new WorkspaceTrustStore(paths).IsTrusted(workspaceRoot);
            var workspaceData = workspaceRoot is null ? null : OmniHost.WorkspaceDataDirectory(paths, workspaceRoot);
            var store = new SessionSkillActivationStore(workspaceData ?? paths.DataDirectory);
            var expectedRevision = fields.GetValueOrDefault("expectedRevision");
            if (hasExpectedRevision
                && (!IsSha256(expectedRevision) || !FixedHashEquals(expectedRevision!, store.Revision(session))))
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.selection_changed",
                    RuntimeCommandOutcome.Rejected());
            if (commandName == "skills.release")
            {
                if (!CanMutateSessionPreference(session))
                    return new CommandAck(command.MessageId, "busy", "sessionPreferences.error.execution_busy",
                        RuntimeCommandOutcome.Deferred("session_preferences_busy"));
                var releaseIdentity = new SkillActivationIdentity(skillScope, skillId);
                var prior = store.ReadDetailed(session).GetValueOrDefault(releaseIdentity);
                if ((hasExpectedRevision && (!fields.ContainsKey("expectedActiveManifestSha256")
                        || !fields.ContainsKey("expectedActivePackageContentSha256")))
                    || (fields.TryGetValue("expectedActiveManifestSha256", out var expectedActiveManifest)
                        && (prior is null || !FixedHashEquals(expectedActiveManifest, prior.ManifestSha256)
                            || !fields.TryGetValue("expectedActivePackageContentSha256", out var expectedActivePackage)
                            || !FixedHashEquals(expectedActivePackage, prior.PackageContentSha256))))
                    return new CommandAck(command.MessageId, "error", "sessionPreferences.error.selection_changed",
                        RuntimeCommandOutcome.Rejected());
                var removed = store.Release(session, skillScope, skillId);
                if (removed && prior is not null)
                {
                    var automatic = prior.Origin == "Automatic";
                    var packageHash = prior.PackageContentSha256;
                    if (automatic) store.SuppressAutomatic(session, skillScope, skillId,
                        prior.ManifestSha256, packageHash);
                    try
                    {
                        new EventStream(_store, _codecs, session).Append(new SkillActivationChanged(skillId,
                            skillScope, prior.ManifestSha256, Activated: false,
                            Origin: automatic ? "Automatic" : "User"), DurabilityClass.Barrier);
                    }
                    catch
                    {
                        if (automatic) store.ClearAutomaticSuppression(session, skillScope, skillId);
                        store.Activate(session, skillScope, skillId, prior.ManifestSha256,
                            packageHash, prior.Origin);
                        throw;
                    }
                }
                return new CommandAck(command.MessageId, "ok", removed ? null : "Skill was not active.",
                    RuntimeCommandOutcome.Accepted());
            }
            if (skillScope == ScopeLevel.Project && !trusted)
                throw new InvalidOperationException("Trust the workspace before activating Project skills.");
            if (!CanMutateSessionPreference(session))
                return new CommandAck(command.MessageId, "busy", "sessionPreferences.error.execution_busy",
                    RuntimeCommandOutcome.Deferred("session_preferences_busy"));
            var discovered = DiscoverAllSkillSources(paths, workspaceData, session, trusted, cancellationToken);
            var loader = new SkillManifestLoader();
            var candidates = discovered.Where(skill => skill.Scope == skillScope
                && string.Equals(skill.Manifest.Id, skillId, StringComparison.Ordinal)).ToArray();
            var selected = candidates.SingleOrDefault(skill => skill.State != SkillLifecycleState.Conflict
                && skill.State != SkillLifecycleState.Released)
                ?? throw new InvalidOperationException(candidates.Length == 0
                    ? "No eligible skill has that id in the selected scope."
                    : "Skill activation is ambiguous or shadowed by a sealed or more trusted source.");
            if ((fields.ContainsKey("expectedRevision") || fields.ContainsKey("expectedManifestSha256"))
                && (!fields.TryGetValue("expectedManifestSha256", out var expectedHash)
                    || !string.Equals(expectedHash, selected.ManifestSha256, StringComparison.Ordinal)))
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.selection_changed",
                    RuntimeCommandOutcome.Rejected());
            var loadedSkill = loader.Activate(selected); // Explicit activation validates the bounded package.
            if ((fields.ContainsKey("expectedRevision") || fields.ContainsKey("expectedPackageContentSha256"))
                && (!fields.TryGetValue("expectedPackageContentSha256", out var expectedPackageHash)
                    || !FixedHashEquals(expectedPackageHash,
                        loadedSkill.PackageContentSha256 ?? loadedSkill.ContentSha256)))
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.selection_changed",
                    RuntimeCommandOutcome.Rejected());
            var activationIdentity = new SkillActivationIdentity(selected.Scope, selected.Manifest.Id!);
            var previous = store.ReadDetailed(session).GetValueOrDefault(activationIdentity);
            store.Activate(session, selected.Scope, selected.Manifest.Id!, selected.ManifestSha256,
                loadedSkill.PackageContentSha256 ?? loadedSkill.ContentSha256, "User");
            try
            {
                new EventStream(_store, _codecs, session).Append(new SkillActivationChanged(skillId,
                    selected.Scope, selected.ManifestSha256, Activated: true, Origin: "User"), DurabilityClass.Barrier);
            }
            catch
            {
                if (previous is null) store.Release(session, selected.Scope, selected.Manifest.Id!);
                else store.Activate(session, selected.Scope, selected.Manifest.Id!, previous.ManifestSha256,
                    previous.PackageContentSha256, previous.Origin);
                throw;
            }
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new CommandAck(command.MessageId, "error", exception.Message, RuntimeCommandOutcome.Rejected());
        }
    }

    private CommandAck ChangeProjectExtensionConsent(WireEnvelope command,
        IReadOnlyDictionary<string, string> fields, bool trustedUserAction)
    {
        if (!trustedUserAction)
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.trusted_action_required",
                RuntimeCommandOutcome.Rejected());
        if (_lastSessionId is not { } session)
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.session_unavailable",
                RuntimeCommandOutcome.Rejected());
        if (!fields.TryGetValue("expectedSessionId", out var expectedSessionId)
            || !string.Equals(expectedSessionId, session.ToString(), StringComparison.Ordinal))
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.session_changed",
                RuntimeCommandOutcome.Rejected());
        try
        {
            using var payloadDocument = System.Text.Json.JsonDocument.Parse(command.PayloadJson);
            var payload = payloadDocument.RootElement;
            if (payload.ValueKind != System.Text.Json.JsonValueKind.Object
                || !WireString(payload, "extensionId", out var extensionId) || string.IsNullOrWhiteSpace(extensionId)
                || !WireString(payload, "version", out var version) || string.IsNullOrWhiteSpace(version)
                || !WireString(payload, "manifestSha256", out var expectedManifestHash)
                || !WireString(payload, "packageSha256", out var expectedPackageHash)
                || !WireString(payload, "consentHash", out var expectedConsentHash)
                || !payload.TryGetProperty("grant", out var grantElement)
                || grantElement.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                || !payload.TryGetProperty("capabilities", out var capabilitiesElement)
                || capabilitiesElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.invalid_request",
                    RuntimeCommandOutcome.Rejected());
            var grant = grantElement.GetBoolean();
            var scopeText = payload.TryGetProperty("scope", out var scopeNode)
                && scopeNode.ValueKind == System.Text.Json.JsonValueKind.String
                ? scopeNode.GetString() : ScopeLevel.Project.ToString();
            if (!Enum.TryParse<ScopeLevel>(scopeText, ignoreCase: false, out var extensionScope)
                || extensionScope is not (ScopeLevel.Project or ScopeLevel.User))
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.invalid_request",
                    RuntimeCommandOutcome.Rejected());
            if (!WireString(payload, "expectedSessionId", out var rawExpectedSession)
                || !string.Equals(rawExpectedSession, session.ToString(), StringComparison.Ordinal))
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.session_changed",
                    RuntimeCommandOutcome.Rejected());

            var paths = _platformPathsFactory();
            if (extensionScope == ScopeLevel.Project && (_workspaceRoot is null
                || !new WorkspaceTrustStore(paths).IsTrusted(_workspaceRoot)))
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.workspace_untrusted",
                    RuntimeCommandOutcome.Rejected());
            var capabilities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in capabilitiesElement.EnumerateArray())
            {
                if (element.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(element.GetString())
                    || !capabilities.Add(element.GetString()!))
                    throw new System.Text.Json.JsonException("Invalid capability array.");
            }

            var root = extensionScope == ScopeLevel.User
                ? Path.Combine(paths.DataDirectory, "extensions")
                : Path.Combine(_workspaceRoot!, ".omnicore", "extensions");
            var sourceTrust = extensionScope == ScopeLevel.User ? TrustLevel.Trusted : TrustLevel.Project;
            var discovered = new ExtensionManifestLoader().Discover(root, sourceTrust, workspaceTrusted: true)
                .Where(extension => string.Equals(extension.Manifest?.Id, extensionId, StringComparison.Ordinal))
                .ToArray();
            if (discovered.Length != 1)
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.selection_changed",
                    RuntimeCommandOutcome.Rejected());
            var extension = discovered[0];
            if (!string.Equals(extension.Manifest?.Version, version, StringComparison.Ordinal)
                || !FixedHashEquals(expectedManifestHash, extension.ManifestSha256)
                || !FixedHashEquals(expectedPackageHash, extension.PackageSha256)
                || !FixedHashEquals(expectedConsentHash, extension.ConsentHash))
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.selection_changed",
                    RuntimeCommandOutcome.Rejected());
            var requested = new HashSet<string>(extension.Manifest?.Requests?.Capabilities ?? [], StringComparer.Ordinal);
            if (!capabilities.IsSubsetOf(requested) || (grant && capabilities.Count == 0)
                || (!grant && capabilities.Count != 0))
                return new CommandAck(command.MessageId, "error", "sessionPreferences.error.invalid_request",
                    RuntimeCommandOutcome.Rejected());

            var store = new ExtensionConsentStore(paths, extensionScope, _workspaceRoot);
            InvalidateExtensionContext(extensionId);
            if (grant) store.Grant(extension, capabilities);
            else store.Revoke(extensionId);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException
            or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new CommandAck(command.MessageId, "error", "sessionPreferences.error.operation_failed",
                RuntimeCommandOutcome.Rejected());
        }
    }

    private static bool WireString(System.Text.Json.JsonElement payload, string name, out string value)
    {
        if (payload.TryGetProperty(name, out var field) && field.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            value = field.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private static bool FixedHashEquals(string supplied, string actual) =>
        actual.Length is 64 or 128 && supplied.Length == actual.Length && supplied.All(Uri.IsHexDigit)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(supplied.ToLowerInvariant()),
            System.Text.Encoding.ASCII.GetBytes(actual.ToLowerInvariant()));

    public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name == CompanionPetProtocol.Query || name.StartsWith("companion.pet:", StringComparison.Ordinal)
            || name.StartsWith("companion.pet.events:", StringComparison.Ordinal)) return QueryCompanion(name);
        if (name is "extensions.packages" or "extensions.views" or "extensions.context" or "extensions.context.catalog" || name.StartsWith("extensions.view:", StringComparison.Ordinal)
            || name.StartsWith("extensions.configuration:", StringComparison.Ordinal) || name.StartsWith("extensions.notifications:", StringComparison.Ordinal))
            return QueryExtensionPlatform(name, cancellationToken);
        if (name == "workspace.panel" || name.StartsWith("workspace.panel:", StringComparison.Ordinal))
            return QueryWorkspacePanel(name, cancellationToken);
        if (name == "models.catalog")
            return new SessionQueryResult(name, EncodeSessionModelCatalog(cancellationToken));
        if (name == "session.model.selection")
            return new SessionQueryResult(name, EncodeCurrentSessionModelSelection());
        if (name == "command.catalog")
            return new SessionQueryResult(name, CommandCatalogJson.Encode(CurrentNativeCommandCatalog()));
        if (name == "command.recovery")
            return new SessionQueryResult(name, EncodeNativeCommandRecovery());
        if (name == "workflow.recovery")
            return new SessionQueryResult(name, EncodeNativeWorkflowRecovery());
        if (name == "process.sessions")
            return new SessionQueryResult(name, _lastSessionId is { } processSession && _lastRunId is { } processRun
                ? EncodeProcessSessions(processSession, processRun) : "null");
        if (name.StartsWith("worktree.preview:", StringComparison.Ordinal))
        {
            var ownershipId = name["worktree.preview:".Length..];
            return new SessionQueryResult("worktree.preview", EncodeWorktreePreview(ownershipId, cancellationToken));
        }
        if (name.StartsWith("process.sessions:", StringComparison.Ordinal))
        {
            var requested = name["process.sessions:".Length..];
            if (Guid.TryParseExact(requested, "D", out var runGuid) && requested == runGuid.ToString("D")
                && _lastSessionId is { } processSession && _lastRunId == new RunId(runGuid))
                return new SessionQueryResult("process.sessions", EncodeProcessSessions(processSession, new RunId(runGuid)));
            return new SessionQueryResult("process.sessions", "null");
        }
        if (name.StartsWith("process.output:", StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            if (parts.Length == 4 && Guid.TryParseExact(parts[1], "D", out var runGuid)
                && parts[1] == runGuid.ToString("D") && parts[2].Length == 32
                && parts[2].All(Uri.IsHexDigit)
                && long.TryParse(parts[3], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var after) && after >= 0
                && _lastSessionId is { } processSession && _lastRunId == new RunId(runGuid))
                return new SessionQueryResult("process.output", EncodeProcessOutput(processSession,
                    new RunId(runGuid), parts[2], after));
            return new SessionQueryResult("process.output", "null");
        }
        if (name == "conversations") return new(name, ConversationJson.Encode(Conversations()));
        if (name == OpenRouterProtocol.StatusQuery)
        {
            var paths = _platformPathsFactory();
            var service = _openRouterServiceFactory?.Invoke(paths) ?? new OpenRouterConnectionService(paths: paths);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var rows = service.List(cancellationToken).Select(row => new OpenRouterProtocol.ProviderState(
                row.ProviderId, row.State.ToString(), row.CanConnect, row.CanTest, row.CanDiscoverModels,
                row.CanDisconnect,
                ProviderConnectionMetadataStore.Read(paths.DataDirectory, row.ProviderId, cancellationToken).ValidatedAt,
                ProviderConnectionMetadataStore.Read(paths.DataDirectory, row.ProviderId, cancellationToken).DiscoveredAt,
                loaded.Registry.Models().Count(model => model.ProviderId == row.ProviderId))).ToArray();
            return new SessionQueryResult(name, OpenRouterProtocol.EncodeStatus(rows));
        }
        if (name == OpenRouterProtocol.CreditsQuery
            || name.StartsWith(OpenRouterProtocol.CreditsQuery + ":", StringComparison.Ordinal))
        {
            var paths = _platformPathsFactory();
            var service = _openRouterServiceFactory?.Invoke(paths) ?? new OpenRouterConnectionService(paths: paths);
            var available = service.List(cancellationToken).Where(row => row.CanTest).Select(row => row.ProviderId).ToArray();
            string? requested = name == OpenRouterProtocol.CreditsQuery ? null : name[(OpenRouterProtocol.CreditsQuery.Length + 1)..];
            var providerId = requested ?? (available.Length == 1 ? available[0] : null);
            if (providerId is null && available.Length > 1)
                return new SessionQueryResult(name, "{\"selectionRequired\":true,\"providerIds\":["
                    + string.Join(",", available.Select(id => "\"" + JsonObj.Escape(id) + "\"")) + "]}");
            if (providerId is null || !available.Contains(providerId, StringComparer.Ordinal))
                return new SessionQueryResult(name, "null");
            var snapshot = service.ReadCreditsAsync(providerId, cancellationToken).GetAwaiter().GetResult();
            // The explicit credits action also refreshes the session-scoped status-line cache.
            // It remains keyed by provider, so a reading for OpenRouter cannot replace another
            // provider's quota or authorize routing/spend.
            if (_lastSessionId is { } session)
                Observability.SetQuota(session, OpenRouterQuota(providerId, snapshot));
            decimal? Value(CreditScope scope) => snapshot.Measurements.FirstOrDefault(item => item.Scope == scope)?.Amount.Value;
            string Source(CreditScope scope, string fallback) => snapshot.Measurements.FirstOrDefault(item => item.Scope == scope)?.Amount.Source ?? fallback;
            var key = snapshot.Key;
            var accountBalance = snapshot.Measurements.FirstOrDefault(item => item.Scope == CreditScope.AccountBalance);
            var state = new OpenRouterProtocol.CreditState(providerId, snapshot.KeyAvailability.ToString(), key.AsOf,
                snapshot.AccountBalanceAvailability.ToString(), accountBalance?.Amount.AsOf,
                snapshot.Limitation, key.Limit, key.LimitRemaining, key.Usage,
                Value(CreditScope.AccountBalance), snapshot.AsOf, snapshot.LastQueryAttemptAt,
                key.IsManagementKey, key.LimitResetInterval, key.LimitResetAt, "USD",
                Source(CreditScope.KeyLimit, "openrouter.key"),
                snapshot.Measurements.FirstOrDefault(item => item.Scope == CreditScope.AccountBalance)?.Amount.Source);
            return new SessionQueryResult(name, OpenRouterProtocol.EncodeCredits(state));
        }
        if (name == UiLocalePreferencesProtocol.QueryName)
            return new(name, UiLocalePreferencesProtocol.Encode(new UserLocaleConfiguration(_platformPathsFactory()).Read()));
        if (name == "sidebarSettings") return new(name, SidebarPreferencesJson.Encode(SidebarSettings().Read()));
        if (name == "sessionSidebar")
            return new SessionQueryResult(name, _lastSessionId is null ? "null"
                : SidebarJson.Encode(SessionSidebarReader.Read(_store, _codecs, _lastSessionId, _lastRunId, _recoveryProblem is not null)));
        if (name == "changedFiles")
            return new SessionQueryResult(name, _lastSessionId is null ? "null"
                : FilesJson.Encode(ChangedFilesReader.Read(_store, _codecs, _lastSessionId, _artifacts)));
        if (name.StartsWith("diff.open:", StringComparison.Ordinal))
            return new SessionQueryResult("diff.open", _lastSessionId is null ? "null"
                : FilesJson.Encode(ChangedFilesReader.ReadDiff(_store, _codecs, _lastSessionId, name[10..], _artifacts)));
        if (name == "agents")
            return new SessionQueryResult(name, _lastSessionId is null ? "null"
                : AgentsJson.Encode(AgentLaneReader.Read(_store, _codecs, _lastSessionId, _lastRunId,
                    _artifacts, Observability.Activities(_lastSessionId))));
        if (name == "sessionObservability" || name.StartsWith("sessionObservability:", StringComparison.Ordinal))
        {
            if (_lastSessionId is null) return new SessionQueryResult("sessionObservability", "null");
            var after = name.Contains(':') && long.TryParse(name[(name.IndexOf(':') + 1)..], out var cursor) ? cursor : 0;
            var snapshot = Observability.Snapshot(_lastSessionId, after);
            snapshot = snapshot with { Quotas = snapshot.Quotas.Select(quota => CurrentCredentialQuota(_lastSessionId, quota)).ToArray() };
            return new SessionQueryResult("sessionObservability", ObservabilityJson.Encode(snapshot));
        }
        if (name == "commands")
        {
            RefreshProjectExtensionCommands();
            var catalog = _commandService.Catalog(CommandContextForCurrentRun());
            var names = new[] { "mode", "ultracode", "reasoning" }.Concat(catalog.Commands.Select(command => command.Name));
            var json = "{\"commands\":[" + string.Join(",", names.Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "],"
                + "\"catalog\":" + CommandCatalogJson.Encode(catalog) + "}";
            return new SessionQueryResult("commands", json);
        }
        if (name == "extensions")
        {
            var paths = _platformPathsFactory();
            var trusted = _workspaceRoot is not null && new WorkspaceTrustStore(paths).IsTrusted(_workspaceRoot);
            var loader = new ExtensionManifestLoader();
            var discovered = new List<(DiscoveredExtension Extension, IReadOnlySet<string> Capabilities, ScopeLevel Scope)>();
            Add(Path.Combine(Path.GetFullPath(paths.DataDirectory), "extensions"), TrustLevel.Trusted,
                ScopeLevel.User, new ExtensionConsentStore(paths, ScopeLevel.User), true);
            if (_workspaceRoot is not null)
                Add(Path.Combine(_workspaceRoot, ".omnicore", "extensions"), TrustLevel.Project,
                    ScopeLevel.Project, new ExtensionConsentStore(paths, _workspaceRoot), trusted);
            var ambiguousIds = discovered.Where(item => item.Extension.Manifest?.Id is not null)
                .GroupBy(item => item.Extension.Manifest!.Id!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            var entries = discovered.Select(item =>
            {
                var extension = item.Extension;
                var id = extension.Manifest?.Id;
                var requested = extension.Manifest?.Requests?.Capabilities ?? [];
                var granted = item.Capabilities;
                var effectiveStatus = id is not null && ambiguousIds.Contains(id)
                    ? ExtensionDiscoveryStatus.Untrusted : extension.Status;
                return "{" + string.Join(",", new[]
                {
                    id is not null ? JsonObj.Field("id", id) : "\"id\":null",
                    JsonObj.Field("scope", item.Scope.ToString()),
                    extension.Manifest?.Version is { } version ? JsonObj.Field("version", version) : "\"version\":null",
                    extension.Manifest?.DisplayName is { } displayName ? JsonObj.Field("displayName", displayName) : "\"displayName\":null",
                    "\"uiResources\":" + ExtensionUiResourcesJson.Encode(extension.Manifest?.UiResources),
                    JsonObj.Field("trust", extension.Trust.ToString()),
                    JsonObj.Field("status", effectiveStatus.ToString()),
                    JsonObj.Field("manifestSha256", extension.ManifestSha256),
                    JsonObj.Field("packageSha256", extension.PackageSha256),
                    JsonObj.Field("consentHash", extension.ConsentHash),
                    JsonObj.FieldBool("ambiguousId", id is not null && ambiguousIds.Contains(id)),
                    "\"requestedCapabilities\":[" + string.Join(",", requested.Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]",
                    "\"consentedCapabilities\":[" + string.Join(",", granted.Order(StringComparer.Ordinal)
                        .Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]",
                    extension.Diagnostic is { } diagnostic ? JsonObj.Field("diagnostic", diagnostic) : "\"diagnostic\":null",
                }) + "}";
            });
            return new SessionQueryResult(name, "{\"workspaceTrusted\":" + (trusted ? "true" : "false")
                + ",\"session\":" + ( _lastSessionId is { } currentSession ? "\"" + JsonObj.Escape(currentSession.ToString()) + "\"" : "null")
                + ",\"extensions\":[" + string.Join(",", entries) + "]}");

            void Add(string root, TrustLevel trustLevel, ScopeLevel scope, ExtensionConsentStore consentStore, bool sourceTrusted)
            {
                var candidates = loader.Discover(root, trustLevel, sourceTrusted);
                var grants = candidates.Where(extension => extension.Manifest?.Id is not null)
                    .GroupBy(extension => extension.Manifest!.Id!, StringComparer.Ordinal)
                    .Where(group => group.Count() == 1)
                    .ToDictionary(group => group.Key, group => consentStore.ConsentedCapabilities(group.Single()), StringComparer.Ordinal);
                var reviewed = loader.DiscoverWithConsents(root, trustLevel, sourceTrusted, grants);
                foreach (var extension in reviewed)
                    discovered.Add((extension, extension.Manifest?.Id is { } id && grants.TryGetValue(id, out var values)
                        ? values : new HashSet<string>(StringComparer.Ordinal), scope));
            }
        }
        if (name == "mcp")
        {
            var paths = _platformPathsFactory();
            var workspace = _workspaceRoot ?? Environment.CurrentDirectory;
            var trusted = _workspaceRoot is not null && new WorkspaceTrustStore(paths).IsTrusted(_workspaceRoot);
            // Inspection is deliberately configuration-only: it never probes a process or endpoint.
            return new SessionQueryResult(name, new McpServerStore(paths, workspace, trusted).InspectionJson());
        }
        if (name == "hooks")
        {
            if (_workspaceRoot is null) return new SessionQueryResult(name,
                "{\"workspaceTrusted\":false,\"hooks\":[]}");
            var trusted = new WorkspaceTrustStore(_platformPathsFactory()).IsTrusted(_workspaceRoot);
            var root = Path.Combine(_workspaceRoot, ".omnicore", "extensions");
            var loader = new ExtensionManifestLoader();
            var discovered = loader.Discover(root, TrustLevel.Project, trusted);
            var consents = new ExtensionConsentStore(_platformPathsFactory(), _workspaceRoot);
            var granted = discovered.Where(extension => extension.Manifest?.Id is not null)
                .GroupBy(extension => extension.Manifest!.Id!, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => consents.ConsentedCapabilities(group.Single()), StringComparer.Ordinal);
            if (trusted) discovered = loader.DiscoverWithConsents(root, TrustLevel.Project, trusted, granted);
            var entries = new List<string>();
            foreach (var extension in discovered)
            {
                var id = extension.Manifest?.Id;
                var capabilities = id is not null && granted.TryGetValue(id, out var existing)
                    ? existing : new HashSet<string>(StringComparer.Ordinal);
                var declarations = extension.Manifest?.Provides?.Hooks ?? [];
                for (var index = 0; index < declarations.Count; index++)
                {
                    var declaration = declarations[index];
                    var requested = declaration.Capabilities ?? [];
                    var missing = requested.Where(capability => !capabilities.Contains(
                        "hook." + capability.ToLowerInvariant())).ToArray();
                    var enabled = trusted && extension.Status == ExtensionDiscoveryStatus.Ready
                        && capabilities.Contains("hook.register") && missing.Length == 0
                        && Enum.TryParse<LifecyclePoint>(declaration.Point, ignoreCase: false, out var point)
                        && LifecycleHookSupport.IsSupported(point);
                    var state = !trusted ? "WorkspaceTrustRequired"
                        : extension.Status != ExtensionDiscoveryStatus.Ready ? extension.Status.ToString()
                        : !Enum.TryParse<LifecyclePoint>(declaration.Point, ignoreCase: false, out var declaredPoint)
                            || !LifecycleHookSupport.IsSupported(declaredPoint) ? "UnsupportedPoint"
                        : enabled ? "Enabled" : "ConsentRequired";
                    entries.Add("{" + string.Join(",", new[]
                    {
                        id is null ? "\"id\":null" : JsonObj.Field("id", id + ".hook." + index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        id is null ? "\"extensionId\":null" : JsonObj.Field("extensionId", id),
                        JsonObj.Field("point", declaration.Point ?? "unknown"),
                        JsonObj.Field("state", state),
                        JsonObj.FieldBool("enabled", enabled),
                        "\"requestedCapabilities\":[" + string.Join(",", requested.Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]",
                        "\"consentedCapabilities\":[" + string.Join(",", requested
                            .Where(value => !missing.Contains(value, StringComparer.Ordinal))
                            .Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]",
                        extension.Diagnostic is { } diagnostic ? JsonObj.Field("diagnostic", diagnostic) : "\"diagnostic\":null",
                    }) + "}");
                }
            }
            return new SessionQueryResult(name, "{\"workspaceTrusted\":" + (trusted ? "true" : "false")
                + ",\"hooks\":[" + string.Join(",", entries) + "]}");
        }
        if (name == "skills")
        {
            var paths = _platformPathsFactory();
            var trusted = _workspaceRoot is not null && new WorkspaceTrustStore(paths).IsTrusted(_workspaceRoot);
            var workspaceData = _workspaceRoot is null ? null : OmniHost.WorkspaceDataDirectory(paths, _workspaceRoot);
            var store = new SessionSkillActivationStore(workspaceData ?? paths.DataDirectory);
            var discovered = DiscoverAllSkillSources(paths, workspaceData, _lastSessionId, trusted, cancellationToken);
            var active = _lastSessionId is { } session ? store.ReadDetailed(session)
                : new Dictionary<SkillActivationIdentity, SkillActivationRecord>();
            var loader = new SkillManifestLoader();
            var packageHashes = new Dictionary<SkillActivationIdentity, string?>();
            foreach (var skill in discovered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = new SkillActivationIdentity(skill.Scope, skill.Manifest.Id!);
                if (skill.State is SkillLifecycleState.Conflict or SkillLifecycleState.Released
                    || skill.Trust is TrustLevel.Untrusted or TrustLevel.ThirdParty)
                {
                    packageHashes[identity] = null;
                    continue;
                }
                try
                {
                    var loaded = loader.Activate(skill);
                    packageHashes[identity] = loaded.PackageContentSha256 ?? loaded.ContentSha256;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                    or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    packageHashes[identity] = null;
                }
            }
            var entries = discovered.Select(skill =>
            {
                var state = skill.State;
                var diagnostic = skill.Diagnostic;
                var identity = new SkillActivationIdentity(skill.Scope, skill.Manifest.Id!);
                var packageHash = packageHashes.GetValueOrDefault(identity);
                if (state != SkillLifecycleState.Conflict && active.TryGetValue(identity, out var hash))
                {
                    if (string.Equals(hash.ManifestSha256, skill.ManifestSha256, StringComparison.Ordinal)
                        && packageHash is not null && FixedHashEquals(hash.PackageContentSha256, packageHash))
                        state = SkillLifecycleState.Activated;
                    else
                    {
                        state = SkillLifecycleState.Released;
                        diagnostic = "The skill package changed after activation; activate it again.";
                    }
                }
                else if (_lastSessionId is { } inspectionSession
                    && store.HasAutomaticSuppression(inspectionSession, skill.Scope, skill.Manifest.Id!, skill.ManifestSha256))
                {
                    state = SkillLifecycleState.Released;
                    diagnostic = "Automatic selection was released for this package in the current Session.";
                }
                return "{" + string.Join(",", new[]
            {
                JsonObj.Field("id", skill.Manifest.Id ?? ""),
                JsonObj.Field("version", skill.Manifest.Version ?? ""),
                JsonObj.Field("displayName", skill.Manifest.DisplayName ?? skill.Manifest.Id ?? ""),
                JsonObj.Field("scope", skill.Scope.ToString()),
                JsonObj.Field("trust", skill.Trust.ToString()),
                JsonObj.Field("state", state.ToString()),
                JsonObj.Field("manifestSha256", skill.ManifestSha256),
                packageHash is null ? "\"packageContentSha256\":null" : JsonObj.Field("packageContentSha256", packageHash),
                skill.Manifest.Sealed ? "\"sealed\":true" : "\"sealed\":false",
                "\"budgetTokens\":" + skill.Manifest.BudgetTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "\"tools\":[" + string.Join(",", (skill.Manifest.Tools ?? []).Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]",
                JsonObj.FieldBool("modelInvocable", skill.InvocationRestrictions?.AllowModelInvocation
                    ?? skill.Manifest.ModelInvocable),
                JsonObj.Field("activationOrigin", active.GetValueOrDefault(
                    new SkillActivationIdentity(skill.Scope, skill.Manifest.Id!))?.Origin
                    ?? (_lastSessionId is { } originSession && store.HasAutomaticSuppression(originSession,
                        skill.Scope, skill.Manifest.Id!, skill.ManifestSha256) ? "Automatic" : "None")),
                skill.Description is { } description ? JsonObj.Field("description", description) : "\"description\":null",
                skill.InvocationRestrictions is { } restrictions ? JsonObj.Field("sourceFormat", restrictions.SourceFormat) : "\"sourceFormat\":null",
                "\"allowedToolsGrantPermissions\":false",
                diagnostic is { } message ? JsonObj.Field("diagnostic", message) : "\"diagnostic\":null",
            }) + "}";
            });
            var activations = active.OrderBy(pair => pair.Key).Select(pair =>
            {
                var matchingSources = discovered.Where(skill => skill.Scope == pair.Key.Scope
                    && string.Equals(skill.Manifest.Id, pair.Key.Id, StringComparison.Ordinal)).Take(2).ToArray();
                var current = matchingSources.Length == 1 ? matchingSources[0] : null;
                var currentPackageHash = current is null ? null : packageHashes.GetValueOrDefault(pair.Key);
                var sourceState = matchingSources.Length > 1 ? "unavailable"
                    : current is null ? "missing"
                    : currentPackageHash is null ? "unavailable"
                    : string.Equals(current.ManifestSha256, pair.Value.ManifestSha256, StringComparison.Ordinal)
                        && FixedHashEquals(currentPackageHash, pair.Value.PackageContentSha256) ? "current" : "changed";
                return "{" + string.Join(",", new[]
                {
                    JsonObj.Field("id", pair.Key.Id), JsonObj.Field("scope", pair.Key.Scope.ToString()),
                    JsonObj.Field("manifestSha256", pair.Value.ManifestSha256),
                    JsonObj.Field("packageContentSha256", pair.Value.PackageContentSha256),
                    JsonObj.Field("origin", pair.Value.Origin), JsonObj.Field("sourceState", sourceState),
                }) + "}";
            });
            var suppressions = _lastSessionId is { } suppressionSession
                ? store.ReadAutomaticSuppressions(suppressionSession) : [];
            return new SessionQueryResult(name, "{\"workspaceTrusted\":" + (trusted ? "true" : "false")
                + ",\"session\":" + (_lastSessionId is { } currentSession ? "\"" + JsonObj.Escape(currentSession.ToString()) + "\"" : "null")
                + ",\"sessionId\":" + (_lastSessionId is { } nativeSession ? "\"" + JsonObj.Escape(nativeSession.ToString()) + "\"" : "null")
                + ",\"activationRevision\":" + (_lastSessionId is { } revisionSession
                    ? "\"" + store.Revision(revisionSession) + "\"" : "null")
                + ",\"skills\":[" + string.Join(",", entries) + "]"
                + ",\"activations\":[" + string.Join(",", activations) + "]"
                + ",\"suppressions\":[" + string.Join(",", suppressions.Select(item => "{" + string.Join(",", new[]
                {
                    JsonObj.Field("id", item.SkillId), JsonObj.Field("scope", item.Scope.ToString()),
                    JsonObj.Field("manifestSha256", item.ManifestSha256), JsonObj.Field("packageContentSha256", item.PackageContentSha256),
                }) + "}")) + "]}");
        }
        if (name == "agent.sources")
        {
            var paths = _platformPathsFactory();
            var trusted = _workspaceRoot is not null && new WorkspaceTrustStore(paths).IsTrusted(_workspaceRoot);
            var memoryConsent = new ExternalAgentMemoryConsentStore(paths.DataDirectory);
            var globalMemoryAllowed = memoryConsent.IsAllowed();
            var project = ResolveExternalAgentProjectScope(paths, trusted);
            var projectMemoryAllowed = project.Trusted && project.Identity is not null
                && memoryConsent.IsProjectAllowed(project.Identity);
            var sources = _externalAgentCatalogFactory().Discover(project.Root, _externalAgentActiveDirectory(),
                project.Trusted, globalMemoryAllowed, cancellationToken,
                projectMemoryAllowed ? project.Identity : null,
                path => new WorkspaceTrustStore(paths).IsTrusted(path));
            var entries = sources.Select(source => "{" + string.Join(",", new[]
            {
                JsonObj.Field("kind", source.Kind.ToString()),
                JsonObj.Field("adapter", source.Adapter),
                JsonObj.Field("scope", source.Scope),
                JsonObj.Field("path", source.DisplayPath),
                JsonObj.Field("sha256", source.Sha256),
                source.ActivationSha256 is null ? "\"activationSha256\":null" : JsonObj.Field("activationSha256", source.ActivationSha256),
                JsonObj.Field("state", source.State),
                source.Diagnostic is null ? "\"diagnostic\":null" : JsonObj.Field("diagnostic", source.Diagnostic),
                source.RequiresUserConsent ? "\"requiresConsent\":true" : "\"requiresConsent\":false",
            }) + "}");
            return new SessionQueryResult(name, "{\"workspaceTrusted\":" + (trusted ? "true" : "false")
                + ",\"projectRootTrusted\":" + (project.Trusted ? "true" : "false")
                + (project.Root is null ? ",\"projectRoot\":null" : "," + JsonObj.Field("projectRoot", project.Root))
                + ",\"externalMemoryAllowed\":" + (globalMemoryAllowed ? "true" : "false")
                + ",\"externalProjectMemoryAllowed\":" + (projectMemoryAllowed ? "true" : "false")
                + ",\"sources\":[" + string.Join(",", entries) + "]}");
        }
        if (name is "memory" or "memory.session")
        {
            if (_workspaceRoot is null || _lastSessionId is not { } session)
            return new SessionQueryResult("memory.session", "{\"scope\":\"Session\",\"session\":null,\"sessionId\":null,\"revision\":null,\"records\":[],\"candidates\":[]}");
            var scope = MemoryScope.ForSession(session);
            var store = new SessionMemoryStore(OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), _workspaceRoot));
            var records = store.Read(scope, cancellationToken);
            var journal = _store.ReadFrom(session, 1).Select(_codecs.Decode).ToArray();
            var candidates = SessionMemoryPolicy.ProjectForPresentation(journal);
            var revision = ComputeSessionMemoryRevision(session, records, candidates);
            var entries = records.Select(record => "{" + string.Join(",", new[]
            {
                JsonObj.Field("id", record.Id),
                JsonObj.Field("kind", record.Kind.ToString()),
                JsonObj.Field("content", new PiiRedactor().Redact(record.Content)),
                JsonObj.Field("source", record.Source.ToString()),
                "\"importance\":" + record.Importance.ToString("R", CultureInfo.InvariantCulture),
                "\"confidence\":" + record.Confidence.ToString("R", CultureInfo.InvariantCulture),
                JsonObj.Field("createdAt", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
                "\"tags\":[" + string.Join(",", record.Tags.Select(tag => "\"" + JsonObj.Escape(tag) + "\"")) + "]",
            }) + "}");
            var candidateJson = candidates.Select(candidate => "{" + string.Join(",", new[]
            {
                JsonObj.Field("id", candidate.Id), JsonObj.Field("kind", candidate.Kind.ToString()),
                JsonObj.Field("content", new PiiRedactor().Redact(candidate.Content)),
                JsonObj.Field("origin", candidate.Origin), JsonObj.Field("decision", candidate.Decision),
                candidate.Reason is null ? "\"reason\":null" : JsonObj.Field("reason", candidate.Reason),
                candidate.ConflictMemoryId is null ? "\"conflictMemoryId\":null" : JsonObj.Field("conflictMemoryId", candidate.ConflictMemoryId),
                "\"occurrences\":" + candidate.Occurrences.ToString(CultureInfo.InvariantCulture),
                JsonObj.Field("proposedAt", candidate.ProposedAt.ToString("O", CultureInfo.InvariantCulture)),
            }) + "}");
            return new SessionQueryResult("memory.session", "{\"scope\":\"Session\","
                + JsonObj.Field("session", session.ToString()) + "," + JsonObj.Field("sessionId", session.ToString())
                + "," + JsonObj.Field("revision", revision)
                + ",\"records\":[" + string.Join(",", entries)
                + "],\"candidates\":[" + string.Join(",", candidateJson) + "]}");
        }
        if (name == "modePreference")
        {
            var preference = ReadModePreference();
            return new SessionQueryResult(name, "{" + JsonObj.Field("mode", preference.Mode.ToString().ToLowerInvariant())
                + ",\"revision\":" + preference.Revision.ToString(CultureInfo.InvariantCulture) + "}");
        }
        if (name == "modeAuthority")
        {
            var authority = CurrentModeAuthority();
            return new SessionQueryResult(name, authority is null ? "null" : ModeAuthorityJson(authority, DateTimeOffset.UtcNow));
        }
        if (name == "reasoningPreference")
        {
            var preference = ReadReasoningPreference();
            return new SessionQueryResult(name, "{\"hasSelection\":" + (preference.HasSelection ? "true" : "false")
                + ",\"request\":" + ReasoningRequestJson(preference.Request)
                + ",\"revision\":" + preference.Revision.ToString(CultureInfo.InvariantCulture) + "}");
        }
        if (name == "runReasoningPreference")
        {
            var selection = CurrentRunReasoningSelection();
            return new SessionQueryResult(name, selection is null ? "null"
                : "{\"revision\":" + selection.Revision.ToString(CultureInfo.InvariantCulture)
                    + ",\"hasSelection\":" + (selection.HasSelection ? "true" : "false")
                    + ",\"request\":" + ReasoningRequestJson(selection.Request)
                    + "," + JsonObj.Field("source", selection.Source)
                    + ",\"userPreferenceRevision\":" + (selection.UserPreferenceRevision?.ToString(CultureInfo.InvariantCulture) ?? "null")
                    + ",\"hasCapturedUserDefault\":" + (selection.HasCapturedUserDefault ? "true" : "false")
                    + ",\"capturedUserDefault\":" + ReasoningRequestJson(selection.CapturedUserDefault)
                    + ",\"capturedUserPreferenceRevision\":" + (selection.CapturedUserPreferenceRevision?.ToString(CultureInfo.InvariantCulture) ?? "null")
                    + "}");
        }
        if (name == "workspaceStatus")
            return new SessionQueryResult("workspaceStatus", "{" + JsonObj.Field("workingDirectory", _workspaceRoot ?? "") + "}");
        if (name.StartsWith("complete:", StringComparison.Ordinal))
        {
            var prefix = name.Substring("complete:".Length).Replace('\\', '/');
            var root = _workspaceRoot;
            if (root is null || !Directory.Exists(root) || prefix.Split('/').Any(part => part == ".."))
                return new SessionQueryResult("complete", "{\"paths\":[]}");
            var separator = prefix.LastIndexOf('/');
            var directoryPart = separator < 0 ? "" : prefix.Substring(0, separator);
            var filePart = separator < 0 ? prefix : prefix.Substring(separator + 1);
            var directory = Path.GetFullPath(Path.Combine(root, directoryPart.Replace('/', Path.DirectorySeparatorChar)));
            var rootWithSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!directory.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                && !directory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return new SessionQueryResult("complete", "{\"paths\":[]}");
            var current = Path.GetFullPath(root);
            foreach (var segment in directoryPart.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return new SessionQueryResult("complete", "{\"paths\":[]}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return new SessionQueryResult("complete", "{\"paths\":[]}");
                }
            }
            var paths = Array.Empty<string>();
            if (Directory.Exists(directory))
            {
                try
                {
                    paths = Directory.EnumerateFiles(directory, filePart + "*", SearchOption.TopDirectoryOnly)
                        .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                        .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                        .Where(path => !path.Split('/').Any(part => part is ".git" or ".omnicore" or "bin" or "obj" or "node_modules"))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    paths = Array.Empty<string>();
                }
            }
            return new SessionQueryResult("complete", "{\"paths\":[" + string.Join(",", paths.Select(path => "\"" + JsonObj.Escape(path) + "\"")) + "]}");
        }
        if (name == "state")
        {
            var run = _lastSnapshot is null ? "none" : "M2:" + _lastSnapshot!.TokenCount.ToString();
            return new SessionQueryResult("state", "{\"runtime\":\"omnicore\",\"milestone\":\"M2\","
                + JsonObj.Field("runState", run)
                + "," + JsonObj.Field("recovery", _recoveryProblem is null ? "ok" : "blocked")
                + "," + JsonObj.Field("worktreeMaintenance", _worktreeMaintenanceProblem ?? "ok") + "}");
        }

        if (name == "sessionIdentity")
            return new SessionQueryResult("sessionIdentity", "{" + JsonObj.Field("sessionId", _lastSessionId?.ToString() ?? "") + "}");

        if (name == "sessionSecurity")
            return new SessionQueryResult(name, CurrentSessionSecurityJson());
        if (name == "sessionSecurityDefault")
            return new SessionQueryResult(name, CurrentSessionSecurityDefaultJson());

        if (name == "commandOutcome")
        {
            if (_lastPromptExpanded is { } prompt) return new SessionQueryResult("commandOutcome", "{\"outcome\":{"
                + JsonObj.Field("kind", "promptExpanded") + "," + JsonObj.Field("text", prompt.Text) + ","
                + JsonObj.Field("origin", prompt.Origin) + (_lastForcedCommandTool is { } tool
                    ? "," + JsonObj.Field("toolName", tool) : "") + "}}");
            if (_lastWorkflowRequested is { } workflow) return new SessionQueryResult("commandOutcome", "{\"outcome\":{"
                + JsonObj.Field("kind", "workflowRequested") + "," + JsonObj.Field("workflowId", workflow.Workflow.Id) + ","
                + JsonObj.Field("version", workflow.Workflow.Version) + ","
                + JsonObj.Field("commandId", _lastWorkflowInvocation?.CommandId ?? "") + ","
                + JsonObj.FieldRaw("arguments", "[" + string.Join(",", workflow.Arguments.Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]") + "}}");
            return new SessionQueryResult("commandOutcome", "{\"outcome\":null}");
        }

        if (name == "tools")
        {
            var catalog = _diagnosticCatalog ?? OmniHost.CreateExplorerTools().Catalog();
            var boundary = _diagnosticBoundary;
            var entries = new List<string>();
            // The inspector lists every built-in tool, not only the active Run's catalog: tools
            // outside it (e.g. writes before an ACT Run) are shown as unavailable, never as allowed.
            var actCatalog = OmniHost.CreateActTools().Catalog();
            var rows = catalog.Definitions().Select(definition => (Definition: definition, Catalog: catalog, Active: true))
                .Concat(actCatalog.Definitions()
                    .Where(definition => catalog.Find(new ToolId(definition.Name)) is null)
                    .Select(definition => (Definition: definition, Catalog: actCatalog, Active: false)));
            foreach (var (definition, owner, active) in rows)
            {
                if (definition.Name == "companion.pet" && !CompanionService.Status().Discovered) continue;
                var tool = owner.Find(new ToolId(definition.Name));
                if (tool is null) continue;
                var descriptor = tool.Descriptor;
                var effect = descriptor.EffectClass;
                var claims = descriptor.ReadOnly ? ResourceClaims.Empty()
                    : new ResourceClaims(Array.Empty<string>(), new[] { "diagnostic-target" },
                        Array.Empty<NetworkGrant>(), null, Array.Empty<string>());
                var intent = new ToolIntent(ToolCallId.New(), descriptor.Id, "{}", effect, claims,
                    descriptor.Risk, null);
                var boundaryVisible = boundary is null || boundary.IsToolVisible(definition.Name);
                var boundaryDecision = boundary?.Evaluate(intent);
                var modeRejected = _diagnosticMode == RunMode.Plan && !descriptor.ReadOnly;
                var decision = modeRejected ? "reject:PLAN mode"
                    : !active ? "unavailable:act catalog"
                    : boundaryDecision is { Allowed: false } ? "reject:ModelCapabilityBoundary"
                    : boundaryDecision is { RequiresAsk: true } ? "ask:ModelCapabilityBoundary"
                    : descriptor.Destructive ? "ask:permission policy"
                    : "permission policy";
                entries.Add("{" + JsonObj.Field("visibleName", definition.Name) + ","
                    + JsonObj.Field("toolId", descriptor.Id.ToString()) + ","
                    + JsonObj.Field("description", definition.Description) + ","
                    + "\"uiDescriptions\":" + ExtensionUiResourcesJson.EncodeDescriptions((tool as ExtensionTool)?.UiDescriptions) + ","
                    + JsonObj.Field("source", descriptor.Source.Kind + ":" + descriptor.Source.Scope
                        + ":" + descriptor.Source.Trust + ":" + descriptor.Source.Owner
                        + "@" + descriptor.Source.Version) + ","
                    + JsonObj.Field("effectClass", effect.ToString()) + ","
                    + JsonObj.Field("risk", descriptor.Risk.ToString()) + ","
                    + JsonObj.Field("protection", descriptor.Protection.ToString()) + ","
                    + JsonObj.FieldRaw("tags", "[" + string.Join(",", descriptor.Tags.Select(tag => "\"" + JsonObj.Escape(tag) + "\"")) + "]") + ","
                    + JsonObj.Field("decision", decision) + ","
                    + JsonObj.FieldBool("visible", active && boundaryVisible && !modeRejected) + "}");
            }
            return new SessionQueryResult("tools", "{" + JsonObj.Field("mode", _diagnosticMode.ToString())
                + ",\"tools\":[" + string.Join(",", entries) + "]}");
        }

        if (name == "workingState")
        {
            if (_lastSessionId is not null && _lastRunId is not null)
            {
                try
                {
                    var own = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
                    var run = RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, own);
                    var plan = PlanProjection.Replay(_codecs, own);
                    var projected = WorkingStateProjector.Project(run, plan);
                    if (projected is not null) _lastWorkingStateText = WorkingStateProjector.Render(projected);
                }
                catch (Exception) { }
            }
            var safe = _lastWorkingStateText is null || _lastWorkingStateText.Length == 0 ? "{}"
                : "{" + JsonObj.Field("workingState", RedactPii(_lastWorkingStateText)) + "}";
            return new SessionQueryResult("workingState", safe);
        }

        if (name == "turnFingerprint")
            return new SessionQueryResult("turnFingerprint", ReadLastTurnFingerprint());

        if (name == "context")
        {
            var persisted = ReadPersistedContextSnapshot();
            return new SessionQueryResult("context", persisted is null
                ? "{\"snapshot\":null}" : "{\"snapshot\":" + persisted + "}");
        }

        return null;
    }

    internal (byte[] Bytes, long Size)? ReadAuthorizedBinaryArtifactChunk(string sessionId, string hash,
        long offset, int length)
    {
        var activeSession = _lastSessionId;
        if (activeSession?.ToString() != sessionId || _artifacts is not IBinaryArtifactStore binary
            || length is < 1 or > 512 * 1024 || offset < 0 || !IsCanonicalSha256(hash)) return null;
        var authorization = new SessionArtifactReadAuthorization(_store, () => _lastSessionId, _artifacts);
        if (!authorization.IsReferenced(hash)) return null;
        // FileArtifactStore deliberately requires an expected raw byte size for binary reads.
        // Obtain it from the canonical session event reference; never trust a size supplied by
        // the wire caller or reinterpret image bytes as UTF-8.
        var expectedSize = _store.ReadFrom(activeSession!, 1)
            .SelectMany(evt => evt.ArtifactRefs)
            .FirstOrDefault(reference => reference.Hash.Algorithm == "sha256"
                && string.Equals(reference.Hash.Value, hash, StringComparison.Ordinal))?.Size;
        if (expectedSize is not { } size || size is < 1 or > 32 * 1024 * 1024) return null;
        var bytes = binary.GetBytes(ContentHash.Sha256(hash), size);
        if (bytes is null || offset > bytes.LongLength || offset + length > bytes.LongLength) return null;
        return (bytes.AsSpan(checked((int)offset), length).ToArray(), bytes.LongLength);
    }

    internal IReadOnlyList<AssistantImageOutput> CurrentGeneratedImages()
    {
        if (_lastSessionId is not { } session) return [];
        return _store.ReadFrom(session, 1).Select(_codecs.Decode).OfType<AssistantImagesRecorded>()
            .SelectMany(record => record.Images).ToArray();
    }

    private static bool IsCanonicalSha256(string hash) => hash.Length == 64
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static ProviderQuotaSnapshot OpenRouterQuota(string providerId, OpenRouterCreditSnapshot snapshot)
    {
        var availability = snapshot.Measurements.Any(item => item.Amount.Availability == MetricAvailability.Stale)
            || snapshot.AccountBalanceAvailability == MetricAvailability.Stale
            ? MetricAvailability.Stale
            : snapshot.Measurements.Any(item => item.Amount.Availability == MetricAvailability.Reported)
                ? MetricAvailability.Reported : snapshot.AccountBalanceAvailability;
        return new ProviderQuotaSnapshot(providerId, null, "openrouter.credits", snapshot.AsOf,
            availability, [], snapshot.Measurements, snapshot.Limitation, snapshot.LastQueryAttemptAt,
            snapshot.CredentialGeneration);
    }

    internal ProviderQuotaSnapshot? CurrentCredentialQuota(SessionId session, string providerId)
    {
        var quota = Observability.Quota(session, providerId);
        return quota is null ? null : CurrentCredentialQuota(session, quota);
    }

    private ProviderQuotaSnapshot CurrentCredentialQuota(SessionId session, ProviderQuotaSnapshot quota)
    {
        if (!string.Equals(quota.Source, "openrouter.credits", StringComparison.Ordinal)) return quota;
        var current = ProviderConnectionMetadataStore.Read(_platformPathsFactory().DataDirectory,
            quota.ProviderId, CancellationToken.None).CredentialGeneration;
        if (!string.IsNullOrWhiteSpace(current)
            && string.Equals(current, quota.CredentialGeneration, StringComparison.Ordinal)) return quota;
        var stale = quota with
        {
            Availability = MetricAvailability.Stale,
            Credits = quota.Credits.Select(credit => credit with
            {
                Amount = credit.Amount with { Availability = MetricAvailability.Stale },
            }).ToArray(),
        };
        Observability.SetQuota(session, stale);
        return stale;
    }

    private static string UiLocaleErrorKey(Exception exception)
    {
        if (exception.Data["UiLocaleErrorKey"] is string errorKey)
            return errorKey;
        var key = exception switch
        {
            ConfigValidationException => "ui.locale.error.invalid_settings",
            System.Text.Json.JsonException => "ui.locale.error.invalid_request",
            InvalidOperationException => "ui.locale.error.invalid_settings",
            ArgumentException => "ui.locale.error.invalid_request",
            YamlDotNet.Core.YamlException => "ui.locale.error.invalid_settings",
            _ => "ui.locale.error.save_failed",
        };
        return key;
    }

    /// <summary>Redacta PII del WorkingState antes de exponerlo en queries.</summary>
    private static string RedactPii(string content) =>
        new OmniCore.Domain.PiiRedactor().Redact(content);

    private CommandAck RunSim(WireEnvelope command, Dictionary<string, string> fields)
    {
        // Keep the attempted session identity even when Execute throws before returning.
        // Never attribute a partial new simulation to the previously selected session.
        var attemptSession = SessionId.New();
        try
        {
            // El escenario viaja completo en el comando (ADR-0041 §1); sin él, uno incluido por nombre.
            var scenarioName = fields.TryGetValue("scenario", out var s) ? s : null;
            var scenario = fields.TryGetValue("scenarioYaml", out var yaml) && yaml.Length > 0
                ? ScenarioLoader.Parse(yaml)
                : "with-tool-crash" == scenarioName
                    ? Scenarios.WithToolCrash()
                    : Scenarios.MultiItemPlan();

            // Permisos del escenario + capa del modo del Run (ADR-0037 §4): el executor de esta simulación.
            _engine.SetToolExecutor(ScriptedToolExecutor.WithCoreTools(HostTools.Default().Catalog(),
                new ScriptedPermissionPolicy(scenario.Permissions).WithModeDefaults(scenario.Mode)));
            var result = _engine.Execute(scenario, CancellationToken.None, attemptSession);
            _lastSessionId = result.SessionId;
            _lastRunId = result.RunId;
            _lastSnapshot = null;
            _lastWorkingStateText = "";
            SaveLastSession();
            AuditRun(result);
            var snapshot = MaterializeSnapshot(result);
            _lastSnapshot = snapshot;
            _lastWorkingStateText = result.WorkingState is null
                ? ""
                : OmniCore.Engine.WorkingStateProjector.Render(result.WorkingState!);
            _events.Add(WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.FieldRaw("type", "\"sim.events\"")
                + "," + JsonObj.Field("exitCode", result.ExitCode == 0 ? "0" : "1")
                + "," + JsonObj.Field("run", result.Run.State.ToString())
                + "," + JsonObj.Field("ctxTokens", snapshot is null ? "0" : snapshot.TokenCount.ToString()) + "}"));
            return result.ExitCode == 0
                ? CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    result.SessionId, 0, new CommandId(Guid.Parse(command.MessageId)))
                : CommandOutcomeAck(command.MessageId, "error", "sim falló: " + string.Join("; ", result.Diagnostics),
                    RuntimeCommandOutcome.Accepted(), result.SessionId, 0,
                    new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (ScenarioFormatException sfe)
        {
            return new CommandAck(command.MessageId, "error", sfe.UserMessage.Render(),
                RuntimeCommandOutcome.Rejected());
        }
        catch (Exception ex)
        {
            var detail = ex is OmniCore.Infrastructure.EventParseException parse
                ? "Evento " + parse.EventType + ": " + (parse.Detail ?? "?")
                : (ex.Message ?? "exception");
            var stack = ex.StackTrace is null ? "" : string.Join("; ", ex.StackTrace);
            return FailedDurableCommandAck(command, attemptSession, 0, detail + " :: " + stack, newSession: true);
        }
    }

    /// <summary>
    /// Materializa un ContextSnapshot del run (ADR-0042 §2): WorkingState + conversación, con
    /// conteo de tokens. Es el post-proceso que el Context Engine hace en el Host (el Engine
    /// permanece desacoplado).
    /// </summary>
    private ContextSnapshot? MaterializeSnapshot(SimulationEngine.RunResult result)
    {
        if (result.WorkingState is null)
        {
            return null;
        }

        var counter = new FakeTokenCounter();
        var contributors = new IContextContributor[] {
            new WorkingStateContributor(OmniCore.Engine.WorkingStateProjector.Render(result.WorkingState)),
        };
        var materializer = new ContextMaterializer(counter, contributors);
        var policy = ContextManagementPolicy.Default;
        var usableContext = 8192L;
        var contextPolicyHash = ComputeContextPolicyHash(policy, usableContext);
        var harnessHash = "harness-v1"; // deterministic placeholder for simulation harness
        var fingerprint = new ExecutionFingerprint(
            "qwen38-27b-local", harnessHash, "core-tools", contextPolicyHash, "none", "M2",
            "", counter.Id.Value);
        var request = new MaterializeRequest(result.SessionId, result.RunId, null, null, null,
            _store.CurrentSequence(result.SessionId), fingerprint);
        return materializer.Materialize(request, CancellationToken.None);
    }

    /// <summary>
    /// Alimenta el audit sink con los eventos de permisos del run (ADR-0043 §1, INV-012):
    /// el Engine no escribe auditoría directamente; un consumidor lee los eventos canónicos
    /// y registra los PermissionGranted/Denied como metadata redactada.
    /// </summary>
    private void AuditRun(SimulationEngine.RunResult result)
    {
        // Consumidor de eventos para auditoría (INV-012, ADR-0043 §1): el Engine no escribe
        // auditoría; aquí se leen los eventos canónicos de permisos del run y cada decisión
        // (Allow/Ask/Deny, incluida la resolución de cada Ask) se registra como metadata con
        // WorkspaceId, SessionId, RunId, timestamp y referencia al evento original (sesión + seq).
        // El sink redacta secretos (ADR-0018 §4): aquí no entra contenido ni credenciales.
        WorkspaceId? workspace = null;
        var toolNames = new Dictionary<ToolCallId, string>();
        foreach (var evt in _store.ReadFrom(result.SessionId, 1))
        {
            var payload = _codecs.Decode(evt);
            switch (payload)
            {
                case SessionCreated created:
                    workspace = WorkspaceId.Parse(created.WorkspaceId);
                    continue;
                case ToolCallRequested requested:
                    toolNames[requested.ToolCallId] = requested.ToolName;
                    continue;
            }

            var details = PermissionAuditDetails(payload, toolNames);
            if (details is null)
            {
                continue;
            }

            _audit.Record(new AuditRecord(
                evt.Type.ToString(),
                workspace,
                result.SessionId,
                result.RunId,
                evt.Timestamp,
                "session:" + result.SessionId + ":" + evt.Sequence,
                details), CancellationToken.None);
        }
    }

    /// <summary>
    /// Metadata de decisión por evento de permiso (null = no auditable): tool, decisión y
    /// resolución de cada Ask —granted/denied con su causa—, capas evaluadas y grant aplicado.
    /// </summary>
    private static Dictionary<string, string>? PermissionAuditDetails(DomainEventPayload payload,
        Dictionary<ToolCallId, string> toolNames)
    {
        switch (payload)
        {
            case PermissionEvaluated evaluated:
            {
                var details = ToolDetails(toolNames, evaluated.ToolCallId);
                details["decision"] = evaluated.Decision.ToString();
                details["layers"] = evaluated.LayersJson;
                if (evaluated.AppliedGrant is not null)
                {
                    details["grant"] = evaluated.AppliedGrant.ToString();
                }

                return details;
            }

            case PermissionRequested ask:
            {
                var details = ToolDetails(toolNames, ask.ToolCallId);
                details["decision"] = "Ask";
                details["interaction"] = ask.InteractionId.ToString();
                return details;
            }

            case PermissionGranted granted:
            {
                var details = ToolDetails(toolNames, granted.ToolCallId);
                details["decision"] = "Allow";
                if (granted.GrantId is not null)
                {
                    details["grant"] = granted.GrantId.ToString();
                }

                if (granted.Lifetime.HasValue)
                {
                    details["lifetime"] = granted.Lifetime.Value.ToString();
                }

                return details;
            }

            case PermissionDenied denied:
            {
                var details = ToolDetails(toolNames, denied.ToolCallId);
                details["decision"] = "Deny";
                details["cause"] = denied.Cause;
                return details;
            }

            default:
                return null;
        }
    }

    private static Dictionary<string, string> ToolDetails(Dictionary<ToolCallId, string> toolNames,
        ToolCallId callId)
    {
        var details = new Dictionary<string, string>();
        if (toolNames.TryGetValue(callId, out var tool))
        {
            details["tool"] = tool;
        }

        return details;
    }

    /// <summary>
    /// Comandos de control del Run sobre la sesión en curso del servidor. Los errores de dominio
    /// vuelven como <c>CommandAck</c> fallido con su motivo (nunca se inventa un estado).
    /// </summary>
    private CommandAck RunControl(WireEnvelope command, string commandName, Dictionary<string, string> fields,
        bool trustedUserAction)
    {
        var control = new RunControlService(_store, _codecs, (day, baseline) =>
            UserDailyBudgetContinuation.Limit(_userSpendReader, _codecs, day, baseline),
            rootProfile: _agentProfiles.DefaultProfile?.Id,
            rootProfileRevision: _agentProfiles.DefaultProfile?.Revision,
            rootProfileHash: _agentProfiles.DefaultProfile is { } rootProfile ? AgentProfileFingerprint.Hash(rootProfile) : null,
            initialReasoningSelection: runId =>
            {
                var preference = ReadReasoningPreference();
                return preference.HasSelection
                    ? new RunReasoningPreferenceSelected(runId, 1, preference.Request,
                        "UserDefault", "RunCreated", preference.Revision)
                    : null;
            });
        SessionId? outcomeSession = null;
        long outcomeSequenceBefore = 0;
        var explicitOutcome = commandName is "session.input" or "run.interrupt" or "run.cancel"
            or "interaction.respond" or "interaction.continue";
        var commandId = new CommandId(Guid.Parse(command.MessageId));
        try
        {
            switch (commandName)
            {
                case "session.input":
                {
                    // Explicit intent only: invalid kinds never become a FollowUp or a new Run.
                    if (fields.TryGetValue("kind", out var kind) && kind != "followup")
                    {
                        if (kind != "steering") throw new FormatException("kind de session.input inválido");
                        return ReceiveSteeringCommand(command, fields, commandId);
                    }
                    var text = fields.TryGetValue("text", out var t) ? t : "";
                    ArtifactRef[] attachments = [];
                    if (fields.TryGetValue("attachments", out var attachmentsJson))
                    {
                        if (!trustedUserAction || _artifacts is not IBinaryArtifactStore binaryStore)
                            return CommandOutcomeAck(command.MessageId, "error", "attachments_require_trusted_binary_store",
                                RuntimeCommandOutcome.Rejected(), null, 0, commandId);
                        attachments = AttachmentReferences.Decode(attachmentsJson);
                        foreach (var reference in attachments)
                        {
                            var bytes = binaryStore.GetBytes(reference.Hash, reference.Size);
                            if (bytes is null || AttachmentContentMaterializer.DetectMediaType(bytes) != reference.MediaType)
                                return CommandOutcomeAck(command.MessageId, "error", "attachment_missing_or_corrupt",
                                    RuntimeCommandOutcome.Rejected(), null, 0, commandId);
                            AttachmentContentMaterializer.ValidateAttachmentBytes(reference.MediaType, bytes);
                        }
                    }
                    if (text.Length == 0 && attachments.Length == 0)
                    {
                        if (explicitOutcome)
                        {
                            return CommandOutcomeAck(command.MessageId, "error", "falta 'text'",
                                RuntimeCommandOutcome.Rejected(), null, 0, commandId);
                        }

                        return CommandAck.Fail(command.MessageId, "falta 'text'");
                    }

                    outcomeSession = _lastSessionId;
                    outcomeSequenceBefore = outcomeSession is null ? 0 : _store.CurrentSequence(outcomeSession);
                    var session = outcomeSession ?? SessionId.New();
                    bool startsRun;
                    lock (_modeAuthorityMutationGate)
                    {
                        var hasNativeSecurityRevisions = fields.ContainsKey("expectedPolicyRevision")
                            || fields.ContainsKey("expectedDefaultRevision");
                        if (hasNativeSecurityRevisions
                            && (!trustedUserAction || !fields.ContainsKey("expectedPolicyRevision")
                                || !fields.ContainsKey("expectedDefaultRevision")))
                            throw new FormatException("session_security.expected_revision_required");
                        var expectedPolicyRaw = fields.GetValueOrDefault("expectedPolicyRevision");
                        var expectedDefaultRaw = fields.GetValueOrDefault("expectedDefaultRevision");
                        var expectedPolicyRevision = expectedPolicyRaw is not (null or "null")
                            && long.TryParse(expectedPolicyRaw, System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out var parsedPolicyRevision)
                                ? parsedPolicyRevision : (long?)null;
                        var expectedDefaultRevision = expectedDefaultRaw is not (null or "null")
                            ? expectedDefaultRaw : null;
                        if (hasNativeSecurityRevisions && (expectedPolicyRaw is not (null or "null")
                                && expectedPolicyRevision is null
                            || expectedDefaultRevision is not null && (expectedDefaultRevision.Length != 64
                                || !expectedDefaultRevision.All(Uri.IsHexDigit))))
                            throw new FormatException("session_security.invalid_expected_revision");
                        if (outcomeSession is null)
                        {
                            if (expectedPolicyRevision is not null)
                                throw new FormatException("session_security.session_scope_changed");
                            outcomeSession = session; // keep the attempted identity even if initialization fails
                            StartSession(session, expectedDefaultRevision: expectedDefaultRevision);
                        }
                        else if (expectedDefaultRevision is not null)
                            throw new FormatException("session_security.session_scope_changed");
                        EnsureSessionSecurityPolicy(session);
                        if (expectedPolicyRevision is { } expected
                            && ReadSessionSecurityPolicy(session)?.PolicyRevision != expected)
                            throw new SessionSecurityRevisionConflictException();
                        var mode = ReadModePreference().Mode;
                        if (trustedUserAction && fields.TryGetValue("mode", out var requestedMode))
                        {
                            if (!TryParseRunMode(requestedMode, out mode))
                                throw new FormatException("mode de session.input inválido");
                        }
                        startsRun = control.ActiveRun(session) is null;
                        RecordWorkspaceCheckpoint(session, _store.CurrentSequence(session), "before-instruction");
                        _lastRunId = control.SendInput(session, text, mode, ConsumePromptOrigin(), attachments);
                    }
                    if (startsRun && _lastRunId is { } startedRun)
                        DispatchRunStartLifecycle(session, startedRun);
                    _lastSessionId = session;
                    break;
                }

                case "run.interrupt":
                    outcomeSession = RequireSession();
                    outcomeSequenceBefore = _store.CurrentSequence(outcomeSession);
                    control.Interrupt(outcomeSession, RunFrom(fields));
                    break;
                case "run.cancel":
                    outcomeSession = RequireSession();
                    outcomeSequenceBefore = _store.CurrentSequence(outcomeSession);
                    control.CancelRun(outcomeSession, RunFrom(fields));
                    break;
                case "interaction.continue":
                {
                    if (!trustedUserAction) throw new FormatException("trusted interaction recovery required");
                    var session = RequireSession();
                    var expectedRun = fields.GetValueOrDefault("expectedRunId");
                    var expectedPolicyRevision = fields.GetValueOrDefault("expectedPolicyRevision");
                    outcomeSession = session;
                    outcomeSequenceBefore = _store.CurrentSequence(session);
                    lock (_modeAuthorityMutationGate)
                    {
                        if (expectedRun is null || !Guid.TryParseExact(expectedRun, "D", out var expectedRunGuid)
                            || expectedRun != new RunId(expectedRunGuid).ToString()
                            || control.ActiveRun(session)?.ToString() != expectedRun
                            || !long.TryParse(expectedPolicyRevision,
                                System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out var policyRevision)
                            || policyRevision < 1
                            || ReadSessionSecurityPolicy(session)?.PolicyRevision != policyRevision)
                            throw new SessionSecurityRevisionConflictException();
                    }
                    break;
                }
                case "interaction.respond":
                {
                    var interaction = fields.TryGetValue("interactionId", out var i) && Guid.TryParse(i, out var g)
                        ? new InteractionId(g)
                        : throw new FormatException("interactionId inválido");
                    if (fields.TryGetValue("responseType", out var responseType) && responseType == "questionnaire")
                    {
                        if (_artifacts is null) throw new FormatException("artifact store no disponible");
                        var session = RequireSession();
                        outcomeSession = session;
                        outcomeSequenceBefore = _store.CurrentSequence(session);
                        var questionnaire = new QuestionnaireInteractionService(_store, _codecs, _artifacts);
                        var answers = QuestionnaireCodec.DecodeAnswers(fields.TryGetValue("answers", out var a) ? a : "[]");
                        var cancelled = fields.TryGetValue("cancelled", out var c) && c == "true";
                        var stream = new EventStream(_store, _codecs, session);
                        DomainEventPayload? transition = null;
                        var requestedScope = questionnaire.SourceScope(stream, interaction);
                        var expectedRun = fields.GetValueOrDefault("expectedRunId");
                        var expectedPolicyRevision = fields.GetValueOrDefault("expectedPolicyRevision");
                        lock (_modeAuthorityMutationGate)
                        {
                            if (trustedUserAction)
                            {
                                if (expectedRun is null || !Guid.TryParseExact(expectedRun, "D", out var expectedRunGuid)
                                    || expectedRun != new RunId(expectedRunGuid).ToString()
                                    || requestedScope?.RunId?.ToString() != expectedRun
                                    || control.ActiveRun(session)?.ToString() != expectedRun
                                    || !long.TryParse(expectedPolicyRevision,
                                        System.Globalization.NumberStyles.None,
                                        System.Globalization.CultureInfo.InvariantCulture, out var policyRevision)
                                    || policyRevision < 1
                                    || ReadSessionSecurityPolicy(session)?.PolicyRevision != policyRevision)
                                    throw new SessionSecurityRevisionConflictException();
                            }
                            if (requestedScope?.RunId is { } activeRun
                                && RunProjection.Replay(session, activeRun, _codecs, _store.ReadFrom(session, 1)).State
                                    == RunState.AwaitingInput)
                                transition = new UserInputReceived(activeRun, "[\"QuestionnaireResponse\"]", null,
                                    "InteractionResponse(Questionnaire)");
                            var result = questionnaire.Resolve(stream, interaction, answers, cancelled, null, transition);
                            if (!result.Accepted)
                                throw new FormatException(result.AlreadyResolved ? "interacción ya resuelta"
                                    : result.UnknownInteraction ? "interacción desconocida"
                                    : "respuesta inválida: " + string.Join(",", (result.Errors ?? Array.Empty<QuestionnaireError>())
                                        .Select(e => e.Code.ToString())));
                        }
                    }
                    else if (fields.TryGetValue("responseType", out responseType)
                        && responseType == "weakSandboxConsent")
                    {
                        if (!trustedUserAction) throw new FormatException("trusted Weak sandbox response required");
                        var session = RequireSession();
                        var expectedRun = fields.GetValueOrDefault("expectedRunId");
                        var expectedPolicyRevision = fields.GetValueOrDefault("expectedPolicyRevision");
                        if (!Guid.TryParseExact(expectedRun, "D", out var runGuid)
                            || expectedRun != new RunId(runGuid).ToString()
                            || !Guid.TryParseExact(fields.GetValueOrDefault("taskId"), "D", out var taskGuid)
                            || !Guid.TryParseExact(fields.GetValueOrDefault("laneId"), "D", out var laneGuid)
                            || !Guid.TryParseExact(fields.GetValueOrDefault("turnId"), "D", out var turnGuid)
                            || !Guid.TryParseExact(fields.GetValueOrDefault("toolCallId"), "D", out var toolCallGuid)
                            || !Guid.TryParseExact(fields.GetValueOrDefault("invocationId"), "D", out var invocationGuid)
                            || !Guid.TryParseExact(fields.GetValueOrDefault("interactionId"), "D", out var interactionGuid)
                            || !long.TryParse(expectedPolicyRevision, System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out var policyRevision)
                            || policyRevision < 1)
                            throw new FormatException("weak_sandbox.invalid_scope");
                        outcomeSession = session;
                        outcomeSequenceBefore = _store.CurrentSequence(session);
                        RespondNativeWeakSandboxConsent(session, new RunId(runGuid),
                            new TaskId(taskGuid), new LaneId(laneGuid), new TurnId(turnGuid),
                            new ToolCallId(toolCallGuid), new InteractionId(interactionGuid),
                            new CommandId(invocationGuid), fields.GetValueOrDefault("optionId") ?? "",
                            policyRevision, command.MessageId);
                    }
                    else if (fields.TryGetValue("responseType", out responseType) && responseType == "choice")
                    {
                        if (!trustedUserAction) throw new FormatException("trusted choice response required");
                        var session = RequireSession();
                        var optionId = fields.GetValueOrDefault("optionId") ?? "";
                        var expectedRunId = fields.GetValueOrDefault("expectedRunId");
                        var expectedPolicyRevision = fields.GetValueOrDefault("expectedPolicyRevision");
                        outcomeSession = session;
                        outcomeSequenceBefore = _store.CurrentSequence(session);
                        lock (_modeAuthorityMutationGate)
                        {
                            if (expectedRunId is null || !Guid.TryParseExact(expectedRunId, "D", out var expectedRunGuid)
                                || expectedRunId != new RunId(expectedRunGuid).ToString()
                                || control.ActiveRun(session)?.ToString() != expectedRunId
                                || !long.TryParse(expectedPolicyRevision,
                                    System.Globalization.NumberStyles.None,
                                    System.Globalization.CultureInfo.InvariantCulture, out var policyRevision)
                                || policyRevision < 1
                                || ReadSessionSecurityPolicy(session)?.PolicyRevision != policyRevision)
                                throw new SessionSecurityRevisionConflictException();
                            var memoryPromotion = FindPendingMemoryPromotion(session, interaction);
                            if (memoryPromotion is not null)
                            {
                                ApplyMemoryPromotionDecision(session, memoryPromotion, optionId,
                                    command.MessageId, control, resolveInteraction: false);
                                control.RespondNativeChoice(session, interaction, new RunId(expectedRunGuid),
                                    optionId, command.MessageId);
                            }
                            else
                            {
                                var expectedRun = new RunId(expectedRunGuid);
                                var childProcessBinding = NativeChildProcessControlChoiceBinding(session,
                                    expectedRun, interaction);
                                if (childProcessBinding is not null
                                    && Guid.TryParseExact(childProcessBinding.ContinuationTaskId, "D", out var childTaskGuid)
                                    && Guid.TryParseExact(childProcessBinding.ContinuationLaneId, "D", out var childLaneGuid)
                                    && Guid.TryParseExact(childProcessBinding.ContinuationTurnId, "D", out var childTurnGuid)
                                    && Guid.TryParseExact(childProcessBinding.ContinuationToolCallId, "D", out var childCallGuid)
                                    && childProcessBinding.NativeInvocationId is { } childInvocation
                                    && AcceptedDelegationExecutionForChild(session, expectedRun,
                                        new TaskId(childTaskGuid), new LaneId(childLaneGuid)) is { } childExecution)
                                {
                                    var childScope = new ExecutionScopeState(expectedRun,
                                        new TaskId(childTaskGuid), new LaneId(childLaneGuid), new TurnId(childTurnGuid),
                                        new ToolCallId(childCallGuid), childExecution);
                                    control.RespondNativeChoice(session, interaction, expectedRun, optionId,
                                        command.MessageId, nativeProcessControlScope: childScope,
                                        nativeProcessControlInvocationId: childInvocation);
                                }
                                else
                                {
                                    var nativeWorktreeToolOnly = IsNativeWorktreeToolPermission(session,
                                        expectedRun, interaction);
                                    control.RespondNativeChoice(session, interaction, expectedRun,
                                        optionId, command.MessageId, nativeWorktreeToolOnly);
                                }
                            }
                        }
                    }
                    else
                    {
                        var session = RequireSession();
                        var optionId = fields.TryGetValue("optionId", out var option) ? option : "";
                        var sequenceBeforeResponse = _store.CurrentSequence(session);
                        outcomeSession = session;
                        outcomeSequenceBefore = sequenceBeforeResponse;
                        if (optionId == "accept") EnsureLaneAcceptanceFresh(session, interaction);
                        lock (_modeAuthorityMutationGate)
                        {
                            var pendingMemoryPromotion = FindPendingMemoryPromotion(session, interaction);
                            if (pendingMemoryPromotion is not null)
                                ApplyMemoryPromotionDecision(session, pendingMemoryPromotion, optionId,
                                    command.MessageId, control);
                            else
                                control.Respond(session, interaction, optionId, command.MessageId);
                        }
                        var failedTransition = _store.ReadFrom(session, 1)
                            .Where(evt => evt.Sequence > sequenceBeforeResponse && evt.RunId is not null)
                            .Select(evt => _codecs.Decode(evt)).OfType<RunFailed>().LastOrDefault();
                        if (failedTransition is not null)
                            DispatchRunTerminalLifecycle(session, failedTransition.RunId, LifecyclePoint.RunFailed);
                        AuditEffectResolutions(session, sequenceBeforeResponse, interaction);
                        if (control.UnreconciledEffects(session).Count == 0) _recoveryProblem = null;
                    }
                    break;
                }
            }

            SaveLastSession();
            if (explicitOutcome)
            {
                if (_artifacts is not null && outcomeSession is { } summarySession)
                    new RunSummaryService(_store, _codecs, _artifacts, static text => text).EnsureRecorded(summarySession);
                return CommandOutcomeAck(command.MessageId, "ok", null,
                    RuntimeCommandOutcome.Accepted(), outcomeSession, outcomeSequenceBefore, commandId);
            }

            return CommandAck.Ok(command.MessageId);
        }
        catch (UnreconciledEffectException ex)
        {
            var interactionId = ex.Interactions.FirstOrDefault()?.ToString() ?? "none";
            var error = "effects.unresolved(interactionId=" + interactionId + ")";
            if (explicitOutcome)
            {
                return CommandOutcomeAck(command.MessageId, "error", error,
                    RuntimeCommandOutcome.Rejected(), outcomeSession, outcomeSequenceBefore, commandId);
            }

            return CommandAck.Fail(command.MessageId, error);
        }
        catch (Exception ex) when (ex is RunAlreadyActiveException or RunNotActiveException
            or InteractionNotPendingException or InvalidInteractionOptionException or InvalidStateTransitionException
            or FormatException or ArgumentException or SessionSecurityRevisionConflictException)
        {
            if (explicitOutcome)
            {
                return CommandOutcomeAck(command.MessageId, "error", ex.Message,
                    RuntimeCommandOutcome.Rejected(), outcomeSession, outcomeSequenceBefore, commandId);
            }

            return CommandAck.Fail(command.MessageId, ex.Message);
        }
        catch (Exception)
        {
            return outcomeSession is null
                ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command, outcomeSession, outcomeSequenceBefore,
                    "No se pudo completar el command de control.", newSession: _lastSessionId != outcomeSession);
        }
    }

    private CommandAck CommandOutcomeAck(string commandMessageId, string status, string? error,
        RuntimeCommandOutcome outcome, SessionId? session, long sequenceBefore, CommandId commandId)
    {
        if (error is not null) error = SafeCommandError(error);
        if (session is null)
        {
            return new CommandAck(commandMessageId, status, error, outcome);
        }

        long[] causedSequences;
        try
        {
            causedSequences = CommandResultEvents(session, sequenceBefore, commandId)
                .Select(evt => evt.Sequence)
                .ToArray();
        }
        catch (Exception)
        {
            return UnavailableCommandOutcome(commandMessageId);
        }
        return causedSequences.Length == 0
            ? new CommandAck(commandMessageId, status, error, outcome)
            : new CommandAck(commandMessageId, status, error, outcome,
                causedSequences.Min(), causedSequences.Max());
    }

    // Deferred confirmation is not proof of rejection or zero effects. Never expose the
    // journal exception, invent a range, or blindly retry a possibly persisted command.
    private static CommandAck UnavailableCommandOutcome(string commandMessageId) =>
        new(commandMessageId, "error", "No se pudo confirmar el resultado durable del command.",
            RuntimeCommandOutcome.Deferred("JournalOutcomeUnavailable"));

    private DomainEvent[] CommandResultEvents(SessionId session, long sequenceBefore, CommandId commandId) =>
        SelectCommandResultEvents(_store.ReadFrom(session, sequenceBefore + 1), session, sequenceBefore, commandId);

    internal static DomainEvent[] SelectCommandResultEvents(IReadOnlyList<DomainEvent> events,
        SessionId session, long sequenceBefore, CommandId commandId)
    {
        var causedIds = new HashSet<EventId>();
        var result = new List<DomainEvent>();
        // Causal parents precede their children in the session journal. Only roots appended
        // after this invocation and their descendants qualify; chronological adjacency,
        // Run identity, and causes from an earlier invocation are not sufficient.
        foreach (var evt in events.OrderBy(evt => evt.Sequence))
        {
            if (evt.SessionId != session || evt.Sequence <= sequenceBefore) continue;
            if (evt.Causation is CommandCausation command && command.CommandId == commandId
                || evt.Causation is EventCausation parent && causedIds.Contains(parent.EventId))
            {
                causedIds.Add(evt.EventId);
                result.Add(evt);
            }
        }
        return result.ToArray();
    }

    /// <summary>Authoritative admission only; consumption belongs to a ModelStep boundary.</summary>
    private CommandAck ReceiveSteeringCommand(WireEnvelope command, Dictionary<string, string> fields,
        CommandId commandId)
    {
        Guid RequiredId(string name) => fields.TryGetValue(name, out var value) && Guid.TryParse(value, out var id)
            ? id : throw new FormatException(name + " inválido o ausente");
        var session = new SessionId(RequiredId("sessionId"));
        var run = new RunId(RequiredId("runId"));
        var lane = new LaneId(RequiredId("laneId"));
        var turn = new TurnId(RequiredId("turnId"));
        if (_lastSessionId != session) throw new FormatException("steering pertenece a otra sesión");
        var text = fields.TryGetValue("text", out var input) ? input : "";
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("falta 'text'");
        var steeringId = fields.ContainsKey("steeringId")
            ? new SteeringId(RequiredId("steeringId")) : new SteeringId(commandId.Value);
        var events = _store.ReadFrom(session, 1);
        var created = events.Select(_codecs.Decode).OfType<RunCreated>()
            .FirstOrDefault(item => item.RunId == run && item.SessionId == session);
        var requestedLane = LaneProjection.Replay(_codecs, events).Get(lane);
        if (created is null || requestedLane is null
            || !events.Select(_codecs.Decode).OfType<TaskCreated>()
                .Any(task => task.TaskId == requestedLane.TaskId && task.RunId == run))
            throw new FormatException("steering Run/Lane fuera de scope");
        var before = _store.CurrentSequence(session);
        using var execution = ExecutionScope.Begin(new ExecutionScopeState(run, requestedLane.TaskId, lane, turn));
        var accepted = SteeringQueue.TryReceive(_store, _codecs, session, steeringId, run, lane, turn,
            text, fields.TryGetValue("origin", out var origin) ? origin : null);
        var appended = accepted && _store.ReadFrom(session, before + 1).Any(evt =>
            evt.Causation is CommandCausation cause && cause.CommandId == commandId
            && _codecs.Decode(evt) is TurnSteeringReceived received && received.SteeringId == steeringId);
        return CommandOutcomeAck(command.MessageId, accepted ? "ok" : "error",
            accepted ? null : "steering rechazado: identidad en conflicto o Turn no abierto",
            !accepted ? RuntimeCommandOutcome.Rejected()
                : appended ? RuntimeCommandOutcome.Accepted() : RuntimeCommandOutcome.NoOp(),
            session, before, commandId);
    }

    /// <summary>Queues one CLI FollowUp intent as an internal Host command.</summary>
    internal (bool Queued, CommandAck Ack, Exception? Failure) QueueFollowUpPromptCommand(SessionId sessionId, RunId runId,
        LaneId laneId, string prompt, string? origin)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var commandMessageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        try
        {
            var events = _store.ReadFrom(sessionId, 1);
            var decoded = events.Select(evt => (Event: evt, Payload: _codecs.Decode(evt))).ToArray();
            var created = decoded.Select(pair => pair.Payload).OfType<RunCreated>()
                .FirstOrDefault(item => item.RunId == runId && item.SessionId == sessionId);
            if (created is null)
            {
                return (false, new CommandAck(commandMessageId, "error",
                    "the requested Run does not belong to the requested Session",
                    RuntimeCommandOutcome.Rejected()), null);
            }

            // Filter by envelope Run correlation rather than the current-run interval: one Session
            // may contain lifecycle events for an earlier Run after another Run was created.
            var own = events.Where(evt => evt.CorrelationId == runId || evt.RunId == runId).ToArray();
            var runProjection = RunProjection.Replay(sessionId, runId, _codecs, own);
            if (runProjection.IsTerminal())
            {
                return (false, new CommandAck(commandMessageId, "error", "the requested Run is terminal",
                    RuntimeCommandOutcome.Rejected()), null);
            }

            var lane = decoded.FirstOrDefault(pair => pair.Event.RunId == runId
                && pair.Payload is LaneCreated laneCreated && laneCreated.LaneId == laneId);
            if (lane.Payload is not LaneCreated requestedLane
                || !decoded.Any(pair => pair.Event.RunId == runId
                    && pair.Payload is TaskCreated taskCreated && taskCreated.TaskId == requestedLane.TaskId
                    && taskCreated.RunId == runId))
            {
                return (false, new CommandAck(commandMessageId, "error",
                    "the requested Lane does not belong to the requested Run",
                    RuntimeCommandOutcome.Rejected()), null);
            }

            sequenceBefore = _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
            // Queue writes belong to the explicit Run/Lane only; never inherit a caller's unrelated
            // Task/Lane/Turn ambient attribution.
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(runId, requestedLane.TaskId, laneId));
            var queued = FollowUpQueue.TryQueue(_store, _codecs, sessionId, runId, laneId, prompt, origin);
            var outcome = queued ? RuntimeCommandOutcome.Accepted() : RuntimeCommandOutcome.NoOp();
            return (queued, CommandOutcomeAck(commandMessageId, "ok", null, outcome,
                sessionId, sequenceBefore.Value, commandId), null);
        }
        catch (Exception failure)
        {
            var ack = sequenceBefore is null
                ? UnavailableCommandOutcome(commandMessageId)
                : FailedDurableCommandAck(commandMessageId, sessionId, sequenceBefore.Value,
                    failure.Message, restoreRunIdentity: false);
            return (false, ack, failure);
        }
    }

    /// <summary>
    /// Runs one CLI Explorer Ask invocation as an internal Host command, including each Act-loop
    /// iteration. A normal callback return means the invocation was accepted, not that the Run completed.
    /// Failure preserves the original exception (including cancellation) alongside the durable outcome.
    /// Accepted with error describes partial effects, never successful completion or safe blind retry.
    /// </summary>
    internal (ExplorerTurn.TurnResult? Result, CommandAck Ack, Exception? Failure,
        InternalCommandResult? PolicyTransition) ExecuteExplorerTurn(SessionId sessionId,
        RunId runId, Func<CancellationToken, ExplorerTurn.TurnResult> execute,
        CancellationToken cancellationToken, bool readOnlyLane = false, DelegationId? delegationId = null,
        bool waitForCapacity = false, ExplorerTurn? activeTurn = null, LaneId? activeLane = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var messageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        AgentCapacity.Lease? capacity = null;
        var callbackInvoked = false;
        var ownsDelegationLease = delegationId is { } owned
            && AgentCapacity.For(_store).IsActive(sessionId, runId, owned);
        try
        {
            sequenceBefore = _store.CurrentSequence(sessionId);
            if (!ownsDelegationLease)
            {
                var scheduler = AgentCapacity.For(_store);
                capacity = waitForCapacity
                    ? scheduler.Acquire(sessionId, runId, null, MaxAgentsFor(sessionId, runId), 0,
                        readOnlyLane, cancellationToken, destinationRoot: _workspaceRoot)
                    : scheduler.TryAcquire(sessionId, runId, null, MaxAgentsFor(sessionId, runId), 0,
                        readOnlyLane, cancellationToken, destinationRoot: _workspaceRoot);
            }
            if (!ownsDelegationLease && capacity is null)
                return (null, CommandOutcomeAck(messageId, "ok", null,
                    RuntimeCommandOutcome.Deferred("WaitingForCapacity"), sessionId,
                    sequenceBefore.Value, commandId), null, null);
            if (_lastSessionId != sessionId || _lastRunId != runId
                || new RunControlService(_store, _codecs).ActiveRun(sessionId) != runId)
            {
                return (null, new CommandAck(messageId, "error",
                    "the requested session/run is not the current active Run",
                    RuntimeCommandOutcome.Rejected()), null, null);
            }

            sequenceBefore = _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
            using var agentInvocation = HostAgentInvocation.Begin(_store, sessionId, runId);
            callbackInvoked = true;
            var registered = activeTurn is not null && activeLane is { } lane
                && _activeExplorerTurns.TryAdd((sessionId, runId, lane), activeTurn);
            if (activeTurn is not null && !registered)
                return (null, new CommandAck(messageId, "busy", "active_turn_registration_conflict",
                    RuntimeCommandOutcome.Deferred("active_turn_registration_conflict")), null, null);
            ExplorerTurn.TurnResult result;
            try { result = execute(cancellationToken); }
            finally
            {
                if (registered && activeLane is { } registeredLane)
                    _activeExplorerTurns.TryRemove((sessionId, runId, registeredLane), out _);
            }
            ReconcileTerminatedSupervisors(sessionId, runId);
            var policyTransition = result.StopReason == StopReason.EndTurn
                ? EvaluateCompletedTurnModePolicy(sessionId, runId, result.TurnId, sequenceBefore.Value, commandId, cancellationToken) : null;
            var ack = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                sessionId, sequenceBefore.Value, commandId);
            return (result, ack, null, policyTransition);
        }
        catch (Exception failure)
        {
            var ack = sequenceBefore is null
                ? UnavailableCommandOutcome(messageId)
                : !callbackInvoked && failure is IOException
                    ? new CommandAck(messageId, "error", SafeCommandError(failure.Message),
                        RuntimeCommandOutcome.Deferred("AdmissionUnavailable"))
                    : FailedDurableCommandAck(messageId, sessionId, sequenceBefore.Value,
                        failure.Message, restoreRunIdentity: false);
            return (null, ack, failure, null);
        }
        finally { capacity?.Dispose(); }
    }

    private int MaxAgentsFor(SessionId session, RunId run)
    {
        var journal = _store.ReadFrom(session, 1).Where(evt => evt.RunId == run).ToArray();
        if (journal.Length == 0) return 1;
        var authority = RunProjection.Replay(session, run, _codecs, journal).ModeAuthority;
        return authority?.Mode == RunMode.Orchestrate
            && authority.Authorization is { } authorization
            && authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow)
            ? Math.Max(1, authorization.Limits.MaxAgents) : 1;
    }

    private InternalCommandResult? EvaluateCompletedTurnModePolicy(SessionId session, RunId run,
        TurnId? exactTurn, long before, CommandId commandId, CancellationToken cancellationToken)
    {
        if (exactTurn is null) return null;
        lock (_modeAuthorityMutationGate)
        {
            var all = _store.ReadFrom(session, 1);
            var own = EventsForRun(all, run);
            var authority = RunProjection.Replay(session, run, _codecs, own).ModeAuthority;
            if (authority is null || !authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow)) return null;
            var causal = SelectCommandResultEvents(all, session, before, commandId)
                .Where(evt => evt.RunId == run).ToArray();
            var completed = causal.Select(_codecs.Decode).OfType<TurnCompleted>()
                .SingleOrDefault(completed => completed.TurnId == exactTurn);
            if (completed is null) return null;
            var source = causal.LastOrDefault(evt => evt.Sequence > before
                && _codecs.Decode(evt) is RunModeProposed proposal && proposal.TurnId == completed.TurnId);
            if (source is null || _codecs.Decode(source) is not RunModeProposed proposed) return null;
            // Only the explicit covered-plan predicate is implemented here. A model's reason
            // alone is not an observable product-policy justification for other transitions.
            if (authority.Mode != RunMode.Plan || proposed.To != RunMode.Act)
                return PolicyTransitionResult(CommandId.New().ToString(), null,
                    RuntimeCommandOutcome.Deferred("ModePolicyPredicateUnavailable"));
            return ApplyUltraCodePolicyTransition(session, run, RunMode.Act,
                "Explicitly covered current plan revision; completed turn recommends execution",
                proposed.AuthorityRevision, cancellationToken, source.EventId);
        }
    }

    internal InternalCommandResult EnsureSessionRoutingPolicy(SessionId session)
    {
        var commandId = CommandId.New();
        var messageId = commandId.ToString();
        long? before = null;
        try
        {
            if (_lastSessionId != session)
                return new InternalCommandResult(new CommandAck(messageId, "error", "Session is not selected",
                    RuntimeCommandOutcome.Rejected()));
            before = _store.CurrentSequence(session);
            if (SessionRoutingAuthorization.Read(_store.ReadFrom(session, 1), _codecs, session) is not null)
                return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.NoOp(), session, before.Value, commandId);
            using var command = CausationScope.Begin(new CommandCausation(commandId));
            new EventStream(_store, _codecs, session).Append(new SessionRoutingPolicySet(session, _newSessionRoutingPolicy));
            return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before.Value, commandId);
        }
        catch (Exception failure)
        {
            var ack = before is null
                ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, session, before.Value, failure.Message, restoreRunIdentity: false);
            return new InternalCommandResult(ack, failure);
        }
    }

    internal (bool Authorized, InteractionId? Interaction, CommandAck Ack, Exception? Failure) AuthorizeModelRoute(
        SessionId session, RunId run, ModelRoute route, BillingMode mode, bool requireConsent = false,
        RouteConsentTurn? continuation = null)
    {
        var commandId = CommandId.New();
        var messageId = commandId.ToString();
        long? before = null;
        try
        {
            var control = new RunControlService(_store, _codecs);
            if (_lastSessionId != session || _lastRunId != run || control.ActiveRun(session) != run)
                return (false, null, new CommandAck(messageId, "error", "Routing authorization requires the selected active Run",
                    RuntimeCommandOutcome.Rejected()), null);
            before = _store.CurrentSequence(session);
            var events = _store.ReadFrom(session, 1);
            var policy = SessionRoutingAuthorization.Read(events, _codecs, session);
            if (policy is null)
                return (false, null, new CommandAck(messageId, "error", "Session routing policy is absent", RuntimeCommandOutcome.Rejected()), null);
            if (!requireConsent && policy.Allows(route, mode))
            {
                var ack = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.NoOp(), session, before.Value, commandId);
                return (ack.Status == "ok" && ack.Outcome?.Kind == RuntimeCommandOutcomeKind.NoOp, null, ack, null);
            }
            var offer = new SessionRoutingAuthorization.Offer(policy.Revision, AuthorizedModelRoute.From(route, mode));
            var pending = new Dictionary<InteractionId, InteractionRequested>();
            foreach (var evt in events)
            {
                switch (_codecs.Decode(evt))
                {
                    case InteractionRequested request when evt.RunId == run:
                        pending[request.InteractionId] = request; break;
                    case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                    case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
                }
            }
            var existing = pending.Values.FirstOrDefault(request => SessionRoutingAuthorization.Parse(request) == offer);
            if (existing is not null)
            {
                var ack = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelRouteConsent"),
                    session, before.Value, commandId);
                return (false, ack.Status == "ok" ? existing.InteractionId : null, ack, null);
            }
            var interaction = InteractionId.New();
            using var command = CausationScope.Begin(new CommandCausation(commandId));
            var subject = SessionRoutingAuthorization.Context(offer);
            if (continuation is not null)
                subject = subject[..^1] + ",\"continuation\":"
                    + System.Text.Json.JsonSerializer.Serialize(continuation, RouteConsentJsonContext.Default.RouteConsentTurn) + "}";
            var requestEvent = new InteractionRequested(interaction, InteractionKind.ModelRouteConsent,
                subject,
                "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_route\",\"intent\":\"allow\"}]",
                "deny", null, null, null, null, 0, 1);
            var runProjection = RunProjection.Replay(session, run, _codecs, events);
            var runLane = runProjection.RootTask is { } rootTask
                ? LaneProjection.Replay(_codecs, events).ForTask(rootTask)
                    .FirstOrDefault(lane => lane.State == LaneState.Running)
                : null;
            var requestScope = new ExecutionScopeState(run, runProjection.RootTask, runLane?.Id);
            var batch = new List<DomainEventPayload> { requestEvent };
            var scopes = new List<ExecutionScopeState?> { requestScope };
            if (runProjection.State == RunState.Running && pending.Count == 0)
            {
                if (runLane is null)
                    throw new InvalidStateTransitionException("run", "running without a root Lane for route consent",
                        requestEvent.Type().ToString());
                batch.Add(new RunAwaitingInput(run, runLane.Id));
                scopes.Add(requestScope);
            }
            new EventStream(_store, _codecs, session).AppendBatch(batch, DurabilityClass.Barrier, scopes);
            var consentAck = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelRouteConsent"),
                session, before.Value, commandId);
            return (false, consentAck.Status == "ok" ? interaction : null, consentAck, null);
        }
        catch (Exception failure)
        {
            if (before is null) return (false, null, UnavailableCommandOutcome(messageId), failure);
            try
            {
                var persisted = CommandResultEvents(session, before.Value, commandId);
                var observed = persisted.Select(_codecs.Decode).OfType<InteractionRequested>()
                    .LastOrDefault(request => request.Kind == InteractionKind.ModelRouteConsent);
                var ack = persisted.Length == 0
                    ? new CommandAck(messageId, "error", failure.Message, RuntimeCommandOutcome.Rejected())
                    : new CommandAck(messageId, "error", failure.Message, RuntimeCommandOutcome.Accepted(),
                        persisted.Min(evt => evt.Sequence), persisted.Max(evt => evt.Sequence));
                return (false, observed?.InteractionId, ack, failure);
            }
            catch (Exception)
            {
                return (false, null, UnavailableCommandOutcome(messageId), failure);
            }
        }
    }

    internal InternalCommandResult ResolveModelRouteWithoutClient(SessionId session, RunId run, InteractionId interaction)
    {
        var commandId = CommandId.New();
        long? before = null;
        try
        {
            var control = new RunControlService(_store, _codecs);
            var events = _store.ReadFrom(session, 1);
            if (_lastSessionId != session || _lastRunId != run || control.ActiveRun(session) != run
                || events.LastOrDefault(evt => _codecs.Decode(evt) is InteractionRequested request
                    && request.InteractionId == interaction)?.RunId != run)
                return new CommandAck(commandId.ToString(), "error", "Routing interaction does not belong to the selected active Run",
                    RuntimeCommandOutcome.Rejected());
            before = _store.CurrentSequence(session);
            using var command = CausationScope.Begin(new CommandCausation(commandId));
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: run));
            try
            {
                control.ResolveModelRouteWithoutClient(session, interaction);
                return CommandOutcomeAck(commandId.ToString(), "ok", null, RuntimeCommandOutcome.Accepted(), session, before.Value, commandId);
            }
            catch (Exception ex) when (ex is InvalidInteractionOptionException or InteractionNotPendingException)
            {
                return CommandOutcomeAck(commandId.ToString(), "error", ex.Message, RuntimeCommandOutcome.Rejected(), session, before.Value, commandId);
            }
        }
        catch (Exception failure)
        {
            return new InternalCommandResult(before is null
                ? UnavailableCommandOutcome(commandId.ToString())
                : FailedDurableCommandAck(commandId.ToString(), session, before.Value,
                    failure.Message, restoreRunIdentity: false), failure);
        }
    }

    internal InternalCommandResult ResolveBudgetWithoutClient(SessionId sessionId, RunId runId, InteractionId interactionId)
    {
        var commandId = CommandId.New();
        var messageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        try
        {
            var control = new RunControlService(_store, _codecs);
            if (_lastSessionId != sessionId || _lastRunId != runId || control.ActiveRun(sessionId) != runId)
                return new CommandAck(messageId, "error", "Budget interaction does not belong to the current active Run",
                    RuntimeCommandOutcome.Rejected());
            var requestEvent = _store.ReadFrom(sessionId, 1).LastOrDefault(evt =>
                _codecs.Decode(evt) is InteractionRequested request && request.InteractionId == interactionId);
            if (requestEvent?.RunId != runId)
                return new CommandAck(messageId, "error", "Budget interaction has no matching Run attribution",
                    RuntimeCommandOutcome.Rejected());
            sequenceBefore = _store.CurrentSequence(sessionId);
            using var command = CausationScope.Begin(new CommandCausation(commandId));
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: runId));
            try
            {
                control.ResolveBudgetWithoutClient(sessionId, interactionId);
                return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    sessionId, sequenceBefore.Value, commandId);
            }
            catch (Exception ex) when (ex is InteractionNotPendingException or InvalidInteractionOptionException or RunNotActiveException)
            {
                return CommandOutcomeAck(messageId, "error", ex.Message, RuntimeCommandOutcome.Rejected(),
                    sessionId, sequenceBefore.Value, commandId);
            }
        }
        catch (Exception failure)
        {
            return new InternalCommandResult(sequenceBefore is null
                ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, sessionId, sequenceBefore.Value,
                    failure.Message, restoreRunIdentity: false), failure);
        }
    }

    internal InternalCommandResult RecordModelEscalationRequested(SessionId sessionId, ModelEscalationRequested payload) =>
        RecordModelEscalation(sessionId, payload.RunId, payload);

    internal InternalCommandResult RecordModelEscalationApproved(SessionId sessionId, ModelEscalationApproved payload) =>
        RecordModelEscalation(sessionId, payload.RunId, payload);

    internal InternalCommandResult RecordModelEscalationCompleted(SessionId sessionId, ModelEscalationCompleted payload) =>
        RecordModelEscalation(sessionId, payload.RunId, payload);

    private InternalCommandResult RecordModelEscalation(SessionId sessionId, RunId runId, DomainEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var commandMessageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        try
        {
            var belongsToSession = _store.ReadFrom(sessionId, 1)
                .Select(_codecs.Decode)
                .OfType<RunCreated>()
                .Any(created => created.RunId.Equals(runId));
            if (!belongsToSession)
            {
                return new InternalCommandResult(new CommandAck(commandMessageId, "error",
                    "the Run does not belong to the requested Session", RuntimeCommandOutcome.Rejected()));
            }

            sequenceBefore = _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
            // Do not inherit unrelated ambient task/lane/turn attribution: this record names only its
            // persisted Run. Payload v2 TurnId/LaneId remain null unless the caller explicitly supplied them.
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: runId));
            new EventStream(_store, _codecs, sessionId).Append(payload, DurabilityClass.Standard);
            return CommandOutcomeAck(commandMessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                sessionId, sequenceBefore.Value, commandId);
        }
        catch (Exception failure)
        {
            var ack = sequenceBefore is null
                ? UnavailableCommandOutcome(commandMessageId)
                : FailedDurableCommandAck(commandMessageId, sessionId, sequenceBefore.Value,
                    failure.Message, restoreRunIdentity: false);
            return new InternalCommandResult(ack, failure);
        }
    }

    internal (bool? Completed, CommandAck Ack, Exception? Failure) CheckRunCompletionAndGate(SessionId sessionId, RunId runId,
        Func<EventStream, IReadOnlyList<ExternalCompletionGateResult>>? runExternalGates,
        MutationLedger? mutationLedger,
        Func<LifecyclePoint, HookDispatchResult>? lifecycleDispatch = null)
    {
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var commandMessageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        try
        {
            var all = _store.ReadFrom(sessionId, 1);
            if (!all.Select(_codecs.Decode).OfType<RunCreated>().Any(created => created.RunId.Equals(runId)))
            {
                return (null, new CommandAck(commandMessageId, "error", "the Run does not belong to the requested Session",
                    RuntimeCommandOutcome.Rejected()), null);
            }

            var own = EventsForRun(all, runId);
            var run = RunProjection.Replay(sessionId, runId, _codecs, own);
            var tasks = TaskGraphProjection.Replay(_codecs, own);
            var plan = PlanProjection.Replay(_codecs, own);
            var laneProjection = LaneProjection.Replay(_codecs, own);
            LaneId? rootLane = null;
            if (run.RootTask is { } rootTask)
            {
                var runningRootLanes = laneProjection.ForTask(rootTask)
                    .Where(lane => lane.State == LaneState.Running).ToArray();
                if (runningRootLanes.Length == 1) rootLane = runningRootLanes[0].Id;
            }

            sequenceBefore = _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
            // Gates validate the latest completed intent on the root Lane, not a synthetic Turn
            // or an unrelated ambient/other-Lane attribution. An open latest Turn cannot borrow
            // attribution from an older completed one.
            var sourceTurnEvent = rootLane is null ? null : own.LastOrDefault(evt =>
                _codecs.Decode(evt) is TurnStarted started && started.LaneId == rootLane);
            var sourceTurn = sourceTurnEvent is null ? null : ((TurnStarted)_codecs.Decode(sourceTurnEvent)).TurnId;
            if (sourceTurn is not null && !own.Any(evt =>
                    _codecs.Decode(evt) is TurnCompleted completed && completed.TurnId == sourceTurn))
                sourceTurn = null;
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(runId, run.RootTask, rootLane,
                sourceTurn, ExecutionId: sourceTurn is null ? null : sourceTurnEvent?.ExecutionId));
            var stream = new EventStream(_store, _codecs, sessionId);
            if (_processSessions.TryGetValue(sessionId, out var processManager)
                && processManager.HasUnresolvedForRun(runId.ToString()))
            {
                _deferredProcessRunCompletions[(sessionId, runId)] = () =>
                    CheckRunCompletionAndGate(sessionId, runId, runExternalGates, mutationLedger, lifecycleDispatch);
                var deferredAck = CommandOutcomeAck(commandMessageId, "ok", null,
                    RuntimeCommandOutcome.Accepted(), sessionId, sequenceBefore.Value, commandId);
                return (false, deferredAck, null);
            }
            _deferredProcessRunCompletions.TryRemove((sessionId, runId), out _);
            var completed = new RunCoupon(run, tasks, plan).CheckCompletionAndGate(new PlanService(),
                new ProgressReconciler(), _store, _codecs, sessionId, stream,
                runExternalGates is null ? null : () => runExternalGates(stream), mutationLedger,
                lifecycleDispatch);
            if (_artifacts is not null)
                new RunSummaryService(_store, _codecs, _artifacts, static text => text).EnsureRecorded(sessionId);
            var ack = CommandOutcomeAck(commandMessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                sessionId, sequenceBefore.Value, commandId);
            return (completed, ack, null);
        }
        catch (Exception failure)
        {
            return (null, sequenceBefore is null
                ? UnavailableCommandOutcome(commandMessageId)
                : FailedDurableCommandAck(commandMessageId, sessionId, sequenceBefore.Value,
                    failure.Message, restoreRunIdentity: false), failure);
        }
    }

    private void AuditEffectResolutions(SessionId session, long sequenceBeforeResponse, InteractionId interaction)
    {
        WorkspaceId? workspace = null;
        foreach (var evt in _store.ReadFrom(session, 1))
        {
            if (_codecs.Decode(evt) is SessionCreated created)
            {
                try { workspace = WorkspaceId.Parse(created.WorkspaceId); }
                catch (FormatException) { workspace = null; }
                break;
            }
        }

        foreach (var evt in _store.ReadFrom(session, sequenceBeforeResponse + 1))
        {
            if (_codecs.Decode(evt) is not ToolCallReconciled { Cause: InteractionCause.User } resolved)
                continue;
            _audit.Record(new AuditRecord("effect.human_resolution", workspace, session, evt.CorrelationId,
                evt.Timestamp, "session:" + session + ":" + evt.Sequence,
                new Dictionary<string, string>
                {
                    ["interactionId"] = interaction.ToString(),
                    ["toolCallId"] = resolved.ToolCallId.ToString(),
                    ["outcome"] = resolved.Outcome.ToString(),
                }), CancellationToken.None);
        }
    }

    private SessionId RequireSession() =>
        _lastSessionId ?? throw new FormatException("runtime.session.required");

    private RunId RunFrom(Dictionary<string, string> fields) =>
        fields.TryGetValue("runId", out var r) && Guid.TryParse(r, out var g)
            ? new RunId(g)
            : _lastRunId ?? throw new FormatException("falta runId y no hay un Run en curso");

    /// <summary>Crea una sesión nueva en el journal (primer input sin sesión previa).</summary>
    private void StartSession(SessionId session, bool deferRouting = false, string? expectedDefaultRevision = null)
    {
        // Hosts that explicitly configure a workspace root establish it durably with the
        // session. This gives delegated work and crash recovery the same verified authority
        // used by `act`, without trusting a later in-memory ConfigureWorkspaceRoot call.
        // Legacy/in-memory callers that do not configure a root retain their event shape.
        var workspace = _workspaceRoot is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(".");
        var paths = _platformPathsFactory();
        var hooks = ProjectExtensionHooks.Load(workspace, paths,
            new WorkspaceTrustStore(paths).IsTrusted(workspace), _lifecycleRpcInvokerFactory());
        var startingApplications = DispatchCreationHooks(hooks, LifecyclePoint.SessionStarting,
            "session.created", null);
        var defaults = new SessionSecurityDefaultConfiguration(paths);
        var stream = new EventStream(_store, _codecs, session);
        using (defaults.AcquireAdmissionLease())
        {
            // The snapshot used in the journal is exactly the one compared with the caller.
            // The config lease prevents another Host's default.set from racing this commit.
            var securityDefault = defaults.Read();
            if (expectedDefaultRevision is not null
                && (securityDefault.Revision == "unavailable"
                    || !string.Equals(securityDefault.Revision, expectedDefaultRevision, StringComparison.Ordinal)))
                throw new SessionSecurityRevisionConflictException();
            var creation = new List<DomainEventPayload>
            {
                new SessionCreated(session, WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(workspace)).ToString(),
                    workspace, ProfileId.New(), DateTimeOffset.UtcNow),
                SessionSecurityPolicySet.FromPreset(session, 1, securityDefault.Category,
                    SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow),
            };
            if (!deferRouting) creation.Add(new SessionRoutingPolicySet(session, _newSessionRoutingPolicy));
            if (_workspaceRoot is { Length: > 0 })
            {
                var physicalRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspace);
                creation.Add(new WorkspaceRootEstablished(session, physicalRoot, DateTimeOffset.UtcNow,
                    WorkspaceRootIdentity.Establish(physicalRoot)));
            }
            stream.AppendBatch(creation, DurabilityClass.Barrier);
        }
        AppendLifecycleApplications(stream, startingApplications);
        AppendLifecycleApplications(stream, DispatchCreationHooks(hooks, LifecyclePoint.SessionStarted,
            "session.started", null));
    }

    private void EnsureLaneAcceptanceFresh(SessionId session, InteractionId interaction)
    {
        var events = _store.ReadFrom(session, 1);
        var scoped = events.Select(envelope => (Envelope: envelope, Payload: _codecs.Decode(envelope)))
            .Where(item => item.Payload is LaneAcceptancePending pending && pending.InteractionId == interaction)
            .ToArray();
        if (scoped.Length == 0) return;
        if (scoped.Length != 1 || scoped[0].Payload is not LaneAcceptancePending acceptance
            || scoped[0].Envelope.TaskId != acceptance.TaskId
            || scoped[0].Envelope.LaneId != acceptance.LaneId
            || scoped[0].Envelope.ExecutionId != acceptance.ExecutionId)
            throw new FormatException("lane acceptance ownership is ambiguous");
        var pendingEvent = scoped[0].Envelope;
        var requestEvents = events.Select(envelope => (Envelope: envelope, Payload: _codecs.Decode(envelope)))
            .Where(item => item.Payload is InteractionRequested request
                && request.InteractionId == interaction && request.Kind == InteractionKind.AcceptanceConfirmation)
            .ToArray();
        if (requestEvents.Length != 1 || requestEvents[0].Envelope.TaskId != pendingEvent.TaskId
            || requestEvents[0].Envelope.LaneId != pendingEvent.LaneId
            || requestEvents[0].Envelope.ExecutionId != pendingEvent.ExecutionId
            || requestEvents[0].Envelope.RunId != pendingEvent.RunId)
            throw new FormatException("lane acceptance interaction scope mismatch");
        var bindings = events.Select(envelope => (Envelope: envelope, Payload: _codecs.Decode(envelope)))
            .Where(item => item.Payload is LaneWorktreeCreated created
                && created.TaskId == acceptance.TaskId && created.LaneId == acceptance.LaneId
                && created.ExecutionId == acceptance.ExecutionId
                && item.Envelope.TaskId == acceptance.TaskId && item.Envelope.LaneId == acceptance.LaneId
                && item.Envelope.ExecutionId == acceptance.ExecutionId
                && item.Envelope.RunId == pendingEvent.RunId)
            .Select(item => (LaneWorktreeCreated)item.Payload!).ToArray();
        if (bindings.Length != 1 || _workspaceRoot is null)
            throw new FormatException("lane acceptance worktree binding unavailable");
        string root;
        try { root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(_workspaceRoot); }
        catch (Exception) { throw new FormatException("lane acceptance workspace unavailable"); }
        var dataRoot = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), root);
        var binding = bindings[0];
        var git = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var loaded = git.LoadIdentityAsync(root, dataRoot, binding.OwnershipId, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!loaded.Succeeded || loaded.Worktree is not { } identity
            || !string.Equals(identity.WorktreePath, binding.WorktreePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !string.Equals(identity.GitCommonDirectory, binding.GitCommonDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new FormatException("lane acceptance worktree ownership mismatch");
        var preview = git.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(identity), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!preview.Succeeded || preview.Preview is not { } current
            || !string.Equals(current.TheirsTree, acceptance.LaneTreeAtAcceptance, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.LaneHeadAtCapture, acceptance.LaneHeadAtAcceptance, StringComparison.OrdinalIgnoreCase)
            || current.LaneBranchAtCapture != acceptance.LaneBranchAtAcceptance)
            throw new FormatException("lane acceptance source changed; review the lane again");
    }

    private CommandAck ResumeSim(WireEnvelope command)
    {
        if (_lastSessionId is null || _lastRunId is null)
        {
            return new CommandAck(command.MessageId, "error", "no hay un run previo para reanudar",
                RuntimeCommandOutcome.Rejected());
        }

        var session = _lastSessionId;
        var sequenceBefore = _store.CurrentSequence(session);
        try
        {
            var stream = new EventStream(_store, _codecs, session);
            var reconciled = _engine.Resume(session, _lastRunId!, stream);
            _events.Add(WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.FieldRaw("type", "\"sim.resumed\"")
                + "," + JsonObj.Field("reconciled", reconciled == 0 ? "0" : "1") + "}"));
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (Exception ex)
        {
            var stack = ex.StackTrace is null ? "" : string.Join("; ", ex.StackTrace);
            return FailedDurableCommandAck(command, session, sequenceBefore,
                (ex.Message ?? "exception") + " :: " + stack);
        }
    }

    private CommandAck FailedDurableCommandAck(WireEnvelope command, SessionId session,
        long sequenceBefore, string error, bool newSession = false)
        => FailedDurableCommandAck(command.MessageId, session, sequenceBefore, error, newSession);

    private CommandAck FailedDurableCommandAck(string commandMessageId, SessionId session,
        long sequenceBefore, string error, bool newSession = false, bool restoreRunIdentity = true)
    {
        error = SafeCommandError(error);
        var commandId = new CommandId(Guid.Parse(commandMessageId));
        DomainEvent[] persisted;
        try
        {
            persisted = CommandResultEvents(session, sequenceBefore, commandId);
        }
        catch (Exception)
        {
            return UnavailableCommandOutcome(commandMessageId);
        }
        if (persisted.Length == 0)
            return new CommandAck(commandMessageId, "error", error, RuntimeCommandOutcome.Rejected());

        // Internal evaluations can target another persisted Run/Session. Confirm their effects
        // without changing which Run the Host has selected or re-decoding unrelated lifecycle data.
        if (!restoreRunIdentity)
            return new CommandAck(commandMessageId, "error", error, RuntimeCommandOutcome.Accepted(),
                persisted.Min(evt => evt.Sequence), persisted.Max(evt => evt.Sequence));

        if (newSession)
        {
            _lastRunId = null;
            _lastSnapshot = null;
            _lastWorkingStateText = "";
        }
        _lastSessionId = session;
        var created = persisted.Select(_codecs.Decode).OfType<RunCreated>().LastOrDefault();
        if (created is not null)
        {
            if (!Equals(_lastRunId, created.RunId))
            {
                _lastSnapshot = null;
                _lastWorkingStateText = "";
            }
            _lastRunId = created.RunId;
        }
        return new CommandAck(commandMessageId, "error", error, RuntimeCommandOutcome.Accepted(),
            persisted.Min(evt => evt.Sequence), persisted.Max(evt => evt.Sequence));
    }

    /// <summary>
    /// Computa un hash determinista de la política de contexto efectiva (ContextManagementPolicy + budget).
    /// Versión 1: campos estables de ContextManagementPolicy + budget de tokens utilizables.
    /// </summary>
    private static string ComputeContextPolicyHash(ContextManagementPolicy policy, long usableContext)
    {
        var canonical = string.Join("|",
            "ctx-policy-v3-verbatim",
            policy.ExternalizeAboveCharacters.ToString(CultureInfo.InvariantCulture),
            policy.CompressBodyCharacters.ToString(CultureInfo.InvariantCulture),
            policy.RecentTailItems.ToString(CultureInfo.InvariantCulture),
            policy.CompactAfterItems.ToString(CultureInfo.InvariantCulture),
            policy.MaxCheckpointCharacters.ToString(CultureInfo.InvariantCulture),
            policy.AutomaticCompaction ? "auto" : "explicit",
            usableContext.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
/// Resultado de la recuperación del Run real al arrancar el Host (ADR-0004 §5).
/// <c>Ok</c>: la verificación de raíz pasó y (si había toolcalls huérfanas) se reconciliaron;
/// <c>Reconciled</c> cuenta la reconciliación. <c>Blocked</c>: no se pudo verificar la raíz durable o
/// falló el store/reconciliador; el Run NO se continúa automáticamente y no se reconcilió nada
/// (sin re-ejecutar ni clasificar Applied). El motivo se expone en <c>Reason</c>.
/// </summary>
public sealed record HostRecoveryResult(int Reconciled, string? BlockedReason)
{
    public bool IsBlocked() => BlockedReason is not null;

    public static HostRecoveryResult Ok(int reconciled) => new(reconciled, null);

    public static HostRecoveryResult Blocked(string reason) => new(0, reason);
}

/// <summary>Escenarios de simulación incluidos para los tests deterministas y <c>omni sim</c>.</summary>
public sealed class Scenarios
{
    /// <summary>Plan de varios items con TaskGraph N:M, el caso central del criterio de M1 (ADR-0041).</summary>
    public static SimulationScenario MultiItemPlan()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.read", "read"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "multi-item-plan",
            RunMode.Act,
            "Corregir el test de autenticación",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Inspeccionar auth", new string[0]),
                new SimulatedPlanMutation("P2", "Aplicar fix", new string[] { "P1" }),
                new SimulatedPlanMutation("P3", "Validar", new string[] { "P2" }),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Explorar flujo de auth"),
                new SimulatedTask("T2",
                    new SimulatedLink[] { new SimulatedLink("P2", "implements") },
                    new string[] { "T1" }, "Aplicar el fix"),
                new SimulatedTask("T3",
                    new SimulatedLink[] { new SimulatedLink("P3", "verifies") },
                    new string[] { "T2" }, "Validar el fix"),
            },
            turns,
            AuthPermissions(),
            "Completed",
            new Dictionary<string, string>());
    }

    /// <summary>Permisos del escenario por defecto: escritura Allow, resto Allow (perfil autónomo simulado).</summary>
    public static Dictionary<string, PermissionDecision> AuthPermissions()
    {
        var p = new Dictionary<string, PermissionDecision>();
        p["fake.read"] = PermissionDecision.Allow;
        p["fake.test"] = PermissionDecision.Allow;
        p["fake.write"] = PermissionDecision.Allow;
        return p;
    }

    /// <summary>Escenario con una tool call real en los turns (ejercita ToolRuntime completo).</summary>
    public static SimulationScenario WithTools()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.write", "applied"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "with-tools",
            RunMode.Act,
            "Escribir un archivo",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Escribir", new string[0]),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Escribir objetivo"),
            },
            turns,
            AuthPermissions(),
            "Completed",
            new Dictionary<string, string>());
    }

    /// <summary>Escenario que ejerce plan.propose de verdad (P0-6): el modelo inicia el item.</summary>
    public static SimulationScenario WithPlanPropose()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("plan.propose", "applied"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "with-plan-propose",
            RunMode.Act,
            "Iniciar el plan",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Inspeccionar", new string[0]),
                new SimulatedPlanMutation("P2", "Implementar", new string[] { "P1" }),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Inspeccionar"),
                // Un item sin Tasks vinculadas no se completa solo (ADR-0016 §5): P2 tiene su trabajo.
                new SimulatedTask("T2",
                    new SimulatedLink[] { new SimulatedLink("P2", "implements") },
                    new string[] { "T1" }, "Implementar"),
            },
            turns,
            AuthPermissions(),
            "Completed",
            new Dictionary<string, string>());
    }

    /// <summary>Escenario con crash inyectado tras el Started de fake.write (ADR-0041 §2).</summary>
    public static SimulationScenario WithToolCrash()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.write", "applied"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "with-tool-crash",
            RunMode.Act,
            "Escribir un archivo",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Escribir", new string[0]),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Escribir objetivo"),
            },
            turns,
            AuthPermissions(),
            "Running",
            new Dictionary<string, string>(),
            "fake.write");
    }

    /// <summary>WorkingState renderizado de ejemplo para `omni explain` sin LLM (M2).</summary>
    public static string SampleWorkingState()
    {
        var rootItem = OmniCore.Domain.PlanItemId.New();
        var items = new OmniCore.Domain.PlanItem[] {
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "Explorar el repositorio",
                OmniCore.Domain.PlanItemState.InProgress, 1, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "Resumir estructura",
                OmniCore.Domain.PlanItemState.Ready, 2, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
        };
        _ = rootItem;
        var plan = OmniCore.Engine.PlanProjection.FromItems(OmniCore.Domain.PlanId.New(),
            OmniCore.Domain.RunId.New(), items);
        var run = OmniCore.Engine.RunProjection.ForSample(
            OmniCore.Domain.SessionId.New(), OmniCore.Domain.RunId.New(), "Explícame este repositorio");
        return OmniCore.Engine.WorkingStateProjector.Render(
            OmniCore.Engine.WorkingStateProjector.Project(run, plan));
    }

}
