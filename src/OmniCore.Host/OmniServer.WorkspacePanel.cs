using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    private readonly GitWorktreeStore _workspaceGit = new(SystemProcessRuntime.Instance());
    private readonly object _workspacePanelGate = new();
    private (DateTimeOffset At, WorkspaceGitView View)? _workspaceGitCache;
    internal Action? BeforeAlternativeSessionCommit { get; set; }
    internal Action? BeforeAlternativeManifestPublication { get; set; }
    private string PanelData => Path.Combine(OmniHost.WorkspaceDataDirectory(_platformPathsFactory(),
        _workspaceRoot ?? throw new IOException("workspace_not_bound")), "workspace-panel");

    // IOmniClient has synchronous entry points, including calls made on the TUI thread.
    // Start the async Git operation on the pool so none of its nested awaits can capture
    // that blocked UI context. ExecutionContext (including causation) still flows normally.
    private static T CompleteWorkspaceTask<T>(Func<System.Threading.Tasks.Task<T>> operation) =>
        System.Threading.Tasks.Task.Run(operation).GetAwaiter().GetResult();

    private static void CompleteWorkspaceTask(Func<System.Threading.Tasks.Task> operation) =>
        System.Threading.Tasks.Task.Run(operation).GetAwaiter().GetResult();

    internal SessionQueryResult QueryWorkspacePanel(string name, CancellationToken token)
    {
        if (_workspaceRoot is not { } root) throw new IOException("workspace_not_bound");
        if (name.Length > 8192) throw new ArgumentException("panel_request_limit");
        var request = name == "workspace.panel" ? new WorkspacePanelRequest("git")
            : JsonSerializer.Deserialize(Uri.UnescapeDataString(name[16..]), WorkspacePanelJson.Default.WorkspacePanelRequest)
                ?? throw new ArgumentException("panel_request_invalid");
        lock (_workspacePanelGate)
        {
            var session = _lastSessionId;
            var journal = session is null ? [] : _store.ReadFrom(session, 1);
            var origin = journal.Select(_codecs.Decode).OfType<ConversationHistoryImported>().SingleOrDefault();
            WorkspacePanelResult result = request.Kind switch
            {
                "directory" => new(1, session?.ToString(), journal.LastOrDefault()?.Sequence ?? 0, request.Kind,
                    Files: PanelDirectory(root, request.Path, token)),
                "search" => new(1, session?.ToString(), journal.LastOrDefault()?.Sequence ?? 0, request.Kind,
                    Matches: CompleteWorkspaceTask(() => _workspaceGit.SearchWorkspaceAsync(root, request.Search, token))),
                "file" or "diff" => new(1, session?.ToString(), journal.LastOrDefault()?.Sequence ?? 0, request.Kind,
                    File: CompleteWorkspaceTask(() => _workspaceGit.ReadFileAsync(root, request.Path, request.Kind == "diff", token))),
                "checkpoint-diff" => new(1, session?.ToString(), journal.LastOrDefault()?.Sequence ?? 0, request.Kind,
                    File: PanelCheckpointDiff(request, token)),
                "git" => new(1, session?.ToString(), journal.LastOrDefault()?.Sequence ?? 0, request.Kind, Git: PanelGit(root, token)),
                "timeline" => new(1, session?.ToString(), journal.LastOrDefault()?.Sequence ?? 0, request.Kind,
                    Timeline: ReadWorkspaceTimeline(journal), Origin: origin is null ? null : origin with { Messages = [] }),
                "alternative" when Guid.TryParseExact(request.CommandId, "D", out _) => new(1, session?.ToString(),
                    journal.LastOrDefault()?.Sequence ?? 0, request.Kind,
                    Alternative: ReadAlternative(request.CommandId!, session?.ToString())),
                _ => throw new ArgumentException("panel_request_invalid"),
            };
            return new("workspace.panel", JsonSerializer.Serialize(result, WorkspacePanelJson.Default.WorkspacePanelResult));
        }
    }

    private WorkspaceGitView PanelGit(string root, CancellationToken token)
    {
        // Bounded polling clients share a cache; tool events invalidate it, never trigger a scan themselves.
        if (_workspaceGitCache is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(2)) return cached.View;
        var view = CompleteWorkspaceTask(() => _workspaceGit.ReadWorkspaceAsync(root, token));
        _workspaceGitCache = (DateTimeOffset.UtcNow, view); return view;
    }

    private WorkspaceFileView PanelCheckpointDiff(WorkspacePanelRequest request, CancellationToken token)
    {
        if (_lastSessionId is not { } session || request.CheckpointId is not { } id
            || id.Length > 160 || !id.StartsWith(session + "-", StringComparison.Ordinal)
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new IOException("checkpoint_invalid");
        var path = Path.Combine(PanelData, "checkpoints", id + ".json");
        var point = JsonSerializer.Deserialize(File.ReadAllText(path), WorkspacePanelJson.Default.WorkspaceCheckpoint);
        if (point?.Id != id || point.SessionId != session.ToString() || point.Snapshot is null) throw new IOException("checkpoint_not_recoverable");
        var previous = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, session + "-*.json")
            .Select(file => JsonSerializer.Deserialize(File.ReadAllText(file), WorkspacePanelJson.Default.WorkspaceCheckpoint)!)
            .Where(item => item.Sequence < point.Sequence && item.Snapshot is not null).MaxBy(item => item.Sequence);
        return CompleteWorkspaceTask(() => _workspaceGit.ReadCheckpointDiffAsync(_workspaceRoot!, request.Path, point.Snapshot, previous?.Snapshot, token));
    }

    private IReadOnlyList<WorkspaceFile> PanelDirectory(string root, string path, CancellationToken token)
    {
        var status = PanelGit(root, token);
        return CompleteWorkspaceTask(() => _workspaceGit.ReadDirectoryAsync(root, path, token)).Select(file =>
        {
            var changes = status.Files.Where(change => change.Path == file.Path || file.Directory
                && change.Path.StartsWith(file.Path + "/", StringComparison.Ordinal)).ToArray();
            return file with { Status = changes.Length == 0 ? "" : file.Directory ? "M" : changes[0].Status,
                Added = changes.Sum(change => change.Added), Deleted = changes.Sum(change => change.Deleted) };
        }).ToArray();
    }

    internal void RecordWorkspaceCheckpoint(SessionId session, long sequence, string kind)
    {
        if (_workspaceRoot is not { } root || _stateFile is null) return; // simulations have no filesystem authority.
        lock (_workspacePanelGate)
        {
            var id = $"{session}-{sequence}-{kind}";
            var dir = Path.Combine(PanelData, "checkpoints");
            var path = Path.Combine(dir, id + ".json");
            if (File.Exists(path)) return;
            WorkspaceSnapshot? snapshot = null; string? unavailable = null;
            try { snapshot = CompleteWorkspaceTask(() => _workspaceGit.CaptureWorkspaceAsync(root, PanelData, CancellationToken.None)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { unavailable = error is IOException ? error.Message : error.GetType().Name; }
            if (snapshot is not null && _store.ReadFrom(session, 1).Where(evt => evt.Sequence <= sequence).Select(_codecs.Decode)
                .Any(payload => payload is UserInputReceived { Attachments.Count: > 0 } or AssistantImagesRecorded))
                unavailable = "alternative_multimodal_history_not_supported";
            Directory.CreateDirectory(dir);
            var point = new WorkspaceCheckpoint(id, session.ToString(), sequence, kind, DateTimeOffset.UtcNow, snapshot, unavailable);
            AtomicPanelWrite(path, JsonSerializer.Serialize(point, WorkspacePanelJson.Default.WorkspaceCheckpoint));
            _workspaceGitCache = null;
        }
    }

    internal void RecordToolWorkspaceCheckpoint(SessionId session, RunId run, LaneId lane, long sequence, string kind)
    {
        var journal = _store.ReadFrom(session, 1);
        if (!new LaneConversationScope(journal, _codecs, run, lane).AllowsSessionHistory) return;
        RecordWorkspaceCheckpoint(session, sequence, kind);
    }

    private IReadOnlyList<WorkspaceActivity> ReadWorkspaceTimeline(IReadOnlyList<DomainEvent> journal)
    {
        if (_lastSessionId is not { } session) return [];
        var dir = Path.Combine(PanelData, "checkpoints");
        var checkpoints = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, session + "-*.json")
            .Select(path => JsonSerializer.Deserialize(File.ReadAllText(path), WorkspacePanelJson.Default.WorkspaceCheckpoint)!)
            .OrderBy(point => point.Sequence).ToArray() : [];
        var calls = new Dictionary<ToolCallId, ToolCallRequested>();
        var inputSequences = journal.Where(evt => _codecs.Decode(evt) is UserInputReceived).Select(evt => evt.Sequence).ToArray();
        var beforeInput = checkpoints.Where(p => p.Kind == "before-instruction")
            .Select(p => (Point: p, Input: inputSequences.FirstOrDefault(sequence => sequence > p.Sequence)))
            .Where(pair => pair.Input > 0).GroupBy(pair => pair.Input).ToDictionary(group => group.Key, group => group.Last().Point);
        var rows = new List<WorkspaceActivity>();
        var scopes = new Dictionary<RunId, LaneConversationScope>();
        var models = new Dictionary<(RunId, LaneId), string>();
        foreach (var evt in journal)
        {
            var payload = _codecs.Decode(evt);
            if (payload is ModelStepStarted model && evt.RunId is { } modelRun && evt.LaneId is { } modelLane)
                models[(modelRun, modelLane)] = model.ModelId;
            var kind = ""; var text = ""; var activity = ""; IReadOnlyList<string> files = [];
            if (payload is UserInputReceived input) { kind = "instruction"; text = InputText(input.InputPartsJson); }
            else if (payload is ToolCallRequested requested) { calls[requested.ToolCallId] = requested; continue; }
            else if (payload is ToolCallSucceeded success && calls.TryGetValue(success.ToolCallId, out var call))
            {
                kind = "tool"; text = call.ToolName; files = ToolFiles(call.ArgumentsJson);
                activity = call.ToolName is "filesystem.read" ? "read" : call.ToolName is "filesystem.write" or "filesystem.patch" or "worktree.integrate"
                    ? "write" : "tool";
            }
            else if (payload is ToolCallFailed failed && calls.TryGetValue(failed.ToolCallId, out var failedCall))
            { kind = "tool"; text = failedCall.ToolName + ": " + failed.Cause; files = ToolFiles(failedCall.ArgumentsJson); activity = "failed"; }
            else if (payload is TurnCompleted) { kind = "turn"; text = "Turn completed"; }
            if (kind.Length == 0) continue;
            if (files.Count > 0 && evt.RunId is { } scopedRun)
            {
                if (!scopes.TryGetValue(scopedRun, out var scoped)) scopes[scopedRun] = scoped = new LaneConversationScope(journal, _codecs, scopedRun, null);
                if (scoped.OwnerLane(evt) != scoped.TargetLane)
                {
                    files = []; // child worktree paths are not paths in the principal workspace.
                    text += " · agent worktree";
                }
            }
            var checkpoint = kind == "instruction" ? beforeInput.GetValueOrDefault(evt.Sequence)
                : checkpoints.LastOrDefault(p => p.Sequence == evt.Sequence);
            rows.Add(new(evt.Sequence, kind, text.Length > 2000 ? text[..2000] : text, evt.RunId?.ToString(), evt.ExecutionId?.ToString(),
                evt.RunId is { } agentRun && evt.LaneId is { } agentLane && models.TryGetValue((agentRun, agentLane), out var modelId)
                    ? modelId + " · " + agentLane.ToString()[..8] : evt.LaneId?.ToString(), files, activity, checkpoint));
        }
        return rows.TakeLast(300).ToArray();
    }

    private IReadOnlyList<string> ToolFiles(string arguments)
    {
        using var doc = JsonDocument.Parse(arguments);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
        var paths = new List<string>();
        foreach (var key in new[] { "path", "filePath", "relativePath" })
            if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } path)
            {
                try { _workspaceGit.SafeWorkspacePath(_workspaceRoot!, path); paths.Add(path); }
                catch (IOException) { }
            }
        return paths.Distinct(StringComparer.Ordinal).ToArray();
    }

    private WorkspaceAlternative? ReadAlternative(string commandId, string? session)
    {
        var path = Path.Combine(PanelData, "alternatives", commandId + ".json");
        var result = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), WorkspacePanelJson.Default.WorkspaceAlternative) : null;
        return result?.State == "ready" && result.SourceSessionId == session ? result : null;
    }

    private CommandAck CreateWorkspaceAlternative(WireEnvelope command, Dictionary<string, string> fields, bool trusted, CancellationToken token)
    {
        try { return CreateWorkspaceAlternativeCore(command, fields, trusted, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or OperationCanceledException)
        { return new(command.MessageId, "error", "alternative_unavailable:" + error.GetType().Name, RuntimeCommandOutcome.Rejected()); }
    }

    private CommandAck CreateWorkspaceAlternativeCore(WireEnvelope command, Dictionary<string, string> fields, bool trusted, CancellationToken token)
    {
        if (!trusted || _lastSessionId is not { } session || _workspaceRoot is not { } root || _stateFile is null)
            return new(command.MessageId, "error", "alternative_scope_unavailable", RuntimeCommandOutcome.Rejected());
        lock (_modeAuthorityMutationGate)
        lock (_workspacePanelGate)
        {
            var checkpointId = fields.GetValueOrDefault("checkpointId") ?? "";
            if (checkpointId.Length > 160 || !checkpointId.StartsWith(session + "-", StringComparison.Ordinal)
                || checkpointId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                return new(command.MessageId, "error", "checkpoint_invalid", RuntimeCommandOutcome.Rejected());
            var journal = _store.ReadFrom(session, 1);
            if (HasOpenToolCall(journal) || HasOpenModelStep(journal))
                return new(command.MessageId, "busy", "alternative_run_busy", RuntimeCommandOutcome.Deferred("active_turn"));
            var prior = ReadAlternative(command.MessageId, session.ToString());
            if (prior is not null) return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            var checkpointPath = Path.Combine(PanelData, "checkpoints", checkpointId + ".json");
            if (!File.Exists(checkpointPath)) return new(command.MessageId, "error", "checkpoint_not_found", RuntimeCommandOutcome.Rejected());
            var point = JsonSerializer.Deserialize(File.ReadAllText(checkpointPath), WorkspacePanelJson.Default.WorkspaceCheckpoint);
            if (point?.Snapshot is null || point.UnavailableReason is not null || point.Id != checkpointId || point.SessionId != session.ToString() || point.Sequence > _store.CurrentSequence(session))
                return new(command.MessageId, "error", "checkpoint_not_recoverable", RuntimeCommandOutcome.Rejected());
            var instruction = fields.GetValueOrDefault("instruction");
            if (instruction?.Length > 16000) return new(command.MessageId, "error", "instruction_limit", RuntimeCommandOutcome.Rejected());
            var key = Guid.NewGuid().ToString("N"); var branch = "omnicore/alternative-" + key;
            var destination = Path.Combine(_platformPathsFactory().DataDirectory, "alternatives", key);
            var targetData = _platformPathsFactory().WorkspaceDirectory(WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(destination)).ToString());
            // IPlatformPaths may eagerly create an empty data directory. Existing content is
            // foreign; an empty directory for this newly generated workspace is not a collision.
            if (Directory.Exists(destination) || Directory.Exists(targetData) && Directory.EnumerateFileSystemEntries(targetData).Any())
                return new(command.MessageId, "error", "alternative_target_exists", RuntimeCommandOutcome.Rejected());
            var created = false;
            var targetSession = SessionId.New();
            var manifests = Path.Combine(PanelData, "alternatives"); Directory.CreateDirectory(manifests);
            var pendingPath = Path.Combine(manifests, command.MessageId + ".pending.json");
            try
            {
                // Validate/cut history before any new worktree or Session becomes visible.
                var imported = new ConversationHistoryImported(root, session.ToString(), point.Sequence, point.Id,
                    ExportCheckpointConversation(journal, point.Sequence), string.IsNullOrWhiteSpace(instruction) ? null : instruction);
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(imported, WorkspacePanelJson.Default.ConversationHistoryImported)) > 393216)
                    throw new IOException("alternative_history_limit_384k");
                AtomicPanelWrite(pendingPath, JsonSerializer.Serialize(new WorkspaceAlternative(command.MessageId, destination, branch,
                    targetSession.ToString(), root, session.ToString(), point.Sequence, point.Id, "preparing"), WorkspacePanelJson.Default.WorkspaceAlternative));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CompleteWorkspaceTask(() => _workspaceGit.CreateCheckpointWorktreeAsync(root, destination, branch, point.Snapshot, token));
                created = true;
                BeforeAlternativeSessionCommit?.Invoke();
                Directory.CreateDirectory(targetData);
                using (var targetStore = SqliteEventStore.OpenUnpooled(Path.Combine(targetData, "journal.db")))
                {
                    var physical = ProjectIdentity.ResolvePhysicalWorkspaceRoot(destination);
                    var defaults = new SessionSecurityDefaultConfiguration(_platformPathsFactory()).Read();
                    // Fresh policy/defaults, never copied approvals, effects, leases, or pending tool calls.
                    new EventStream(targetStore, _codecs, targetSession).AppendBatch([
                        new SessionCreated(targetSession, WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(destination)).ToString(), destination, ProfileId.New(), DateTimeOffset.UtcNow),
                        SessionSecurityPolicySet.FromPreset(targetSession, 1, defaults.Category, SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow),
                        new SessionRoutingPolicySet(targetSession, _newSessionRoutingPolicy),
                        new WorkspaceRootEstablished(targetSession, physical, DateTimeOffset.UtcNow, WorkspaceRootIdentity.Establish(physical)), imported,
                        new SessionTitleChanged(targetSession, "Alternativa · " + session.ToString()[..8], 5),
                    ], DurabilityClass.Barrier);
                }
                AtomicPanelWrite(Path.Combine(targetData, "lastsession.txt"), targetSession + "\n-");
                BeforeAlternativeManifestPublication?.Invoke();
                var manifest = new WorkspaceAlternative(command.MessageId, destination, branch, targetSession.ToString(), root,
                    session.ToString(), point.Sequence, point.Id, "ready");
                AtomicPanelWrite(Path.Combine(manifests, command.MessageId + ".json"), JsonSerializer.Serialize(manifest, WorkspacePanelJson.Default.WorkspaceAlternative));
                // Publication is committed. A stale intent is harmless and cleaned on restart;
                // a failed intent deletion must never roll back an already published alternative.
                try { File.Delete(pendingPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                _workspaceGitCache = null;
                return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
            {
                try
                {
                    if (created) CompleteWorkspaceTask(() => _workspaceGit.RemoveCheckpointWorktreeAsync(root, destination, branch));
                    // This unique directory did not exist on admission and was created only by this transaction.
                    if (Directory.Exists(targetData)) Directory.Delete(targetData, true);
                    File.Delete(pendingPath);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or InvalidOperationException)
                { return new(command.MessageId, "error", "alternative_cleanup_required", RuntimeCommandOutcome.Rejected()); }
                return new(command.MessageId, "error", "alternative_failed:" + error.GetType().Name, RuntimeCommandOutcome.Rejected());
            }
        }
    }

    internal void RecoverAlternativePreparations()
    {
        if (_stateFile is null || _workspaceRoot is not { } root || _store is not SqliteEventStore sqlite) return;
        var directory = Path.Combine(PanelData, "alternatives");
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.pending.json"))
        {
            SessionId? session = null; var acquired = false;
            try
            {
                var pending = JsonSerializer.Deserialize(File.ReadAllText(file), WorkspacePanelJson.Default.WorkspaceAlternative)
                    ?? throw new IOException("alternative_intent_invalid");
                if (!Guid.TryParseExact(pending.CommandId, "D", out _) || !Guid.TryParseExact(pending.SourceSessionId, "D", out _)
                    || !Guid.TryParseExact(pending.SessionId, "D", out _) || pending.SourceWorkspace != root
                    || pending.Branch.Length != 53 || !pending.Branch.StartsWith("omnicore/alternative-", StringComparison.Ordinal)
                    || !Guid.TryParseExact(pending.Branch[21..], "N", out _)
                    || pending.Workspace != Path.Combine(_platformPathsFactory().DataDirectory, "alternatives", pending.Branch[21..])
                    || Path.GetFileName(file) != pending.CommandId + ".pending.json") throw new IOException("alternative_intent_invalid");
                session = SessionId.Parse(pending.SourceSessionId);
                acquired = sqlite.AcquireSessionWriterLease(session);
                if (ReadAlternative(pending.CommandId, pending.SourceSessionId) is { } ready
                    && ready.Workspace == pending.Workspace && ready.SessionId == pending.SessionId)
                { File.Delete(file); continue; } // manifest publication is the commit point.
                if (Directory.Exists(pending.Workspace))
                {
                    var inspected = CompleteWorkspaceTask(() => _workspaceGit.InspectAsync(pending.Workspace, CancellationToken.None));
                    var pointPath = Path.Combine(PanelData, "checkpoints", pending.CheckpointId + ".json");
                    if (pending.CheckpointId.Length > 160 || pending.CheckpointId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                        throw new IOException("alternative_intent_invalid");
                    var point = JsonSerializer.Deserialize(File.ReadAllText(pointPath), WorkspacePanelJson.Default.WorkspaceCheckpoint);
                    if (!inspected.Succeeded || inspected.Identity!.Branch != pending.Branch || inspected.Identity.HeadCommit != point?.Snapshot?.Commit)
                        throw new IOException("alternative_orphan_changed");
                }
                CompleteWorkspaceTask(() => _workspaceGit.RemoveCheckpointWorktreeAsync(root, pending.Workspace, pending.Branch));
                var targetData = _platformPathsFactory().WorkspaceDirectory(WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(pending.Workspace)).ToString());
                var physical = ProjectIdentity.ResolvePhysicalWorkspaceRoot(targetData);
                if (!string.Equals(ProjectIdentity.CanonicalWorkspacePath(physical), ProjectIdentity.CanonicalWorkspacePath(targetData), StringComparison.Ordinal))
                    throw new IOException("alternative_data_root_changed");
                Directory.Delete(targetData, true); File.Delete(file);
            }
            catch (SessionWriterLeaseUnavailableException) { /* A live owner, not an orphan. */ }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
            { _worktreeMaintenanceProblem = "alternative_cleanup_required"; }
            finally { if (acquired && session is not null) sqlite.ReleaseSessionWriterLease(session); }
        }
    }

    internal IReadOnlyList<ImportedConversationMessage> ExportCheckpointConversation(IReadOnlyList<DomainEvent> journal, long through)
    {
        var messages = journal.Select(_codecs.Decode).OfType<ConversationHistoryImported>().SingleOrDefault()?.Messages.ToList() ?? [];
        var requests = new Dictionary<ToolCallId, ToolCallRequested>();
        var scopes = new Dictionary<RunId, LaneConversationScope>();
        foreach (var evt in journal.Where(evt => evt.Sequence <= through))
        {
            if (evt.RunId is not { } run) continue;
            if (!scopes.TryGetValue(run, out var scope)) scopes[run] = scope = new LaneConversationScope(journal, _codecs, run, null);
            if (!scope.AllowsSessionHistory || scope.OwnerLane(evt) != scope.TargetLane) continue;
            switch (_codecs.Decode(evt))
            {
                case UserInputReceived input when input.Attachments is { Count: > 0 }:
                    throw new IOException("alternative_multimodal_history_not_supported");
                case AssistantImagesRecorded:
                    throw new IOException("alternative_multimodal_history_not_supported");
                case UserInputReceived input when input.Origin != "InteractionResponse(Questionnaire)": messages.Add(new("user", InputText(input.InputPartsJson))); break;
                case AssistantMessageRecorded { ContentRef: { } content }: messages.Add(new("assistant", _artifacts?.GetText(content.Hash) ?? throw new IOException("conversation_artifact_missing"))); break;
                case ToolCallRequested request: requests[request.ToolCallId] = request; break;
                case ToolCallSucceeded success when requests.Remove(success.ToolCallId, out var request):
                    messages.Add(new("tool", success.ResultJson, success.ToolCallId.ToString(), request.ToolName, request.ArgumentsJson)); break;
                case ToolCallFailed failure when requests.Remove(failure.ToolCallId, out var request):
                    messages.Add(new("tool", failure.Cause, failure.ToolCallId.ToString(), request.ToolName, request.ArgumentsJson, true)); break;
            }
        }
        return messages;
    }

    private static void AtomicPanelWrite(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
