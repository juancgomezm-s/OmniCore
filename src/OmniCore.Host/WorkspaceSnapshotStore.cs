namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;
using OmniCore.Tools;

/// <summary>A verified, read-only view of physical effects recorded by OmniCore. Git changes,
/// process effects, legacy records without images, and incomplete effects are never candidates.</summary>
public sealed partial class WorkspaceSnapshotStore
{
    private const int MaxFiles = 64;
    private const long MaxImageBytes = 2L * 1024 * 1024;
    private const long MaxBatchBytes = 8L * 1024 * 1024;
    private const long MaxSerializedArtifactBytes = 16L * 1024 * 1024;
    private readonly RedactionPolicy _redaction = new();
    private readonly string _workspaceRoot;
    private readonly string _rootIdentity;
    private readonly bool _verifyPhysicalRoot;
    private readonly IEventStore _events;
    private readonly IEventCodecRegistry _codecs;
    private readonly IArtifactStore _artifacts;

    public WorkspaceSnapshotStore(string workspaceRoot, IEventStore events,
        IEventCodecRegistry codecs, IArtifactStore artifacts, string rootIdentity)
        : this(workspaceRoot, events, codecs, artifacts, rootIdentity, verifyPhysicalRoot: true) { }

    internal WorkspaceSnapshotStore(string workspaceRoot, IEventStore events,
        IEventCodecRegistry codecs, IArtifactStore artifacts, string rootIdentity, bool verifyPhysicalRoot)
    {
        _workspaceRoot = verifyPhysicalRoot ? ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot)
            : Path.GetFullPath(workspaceRoot);
        _rootIdentity = rootIdentity ?? throw new ArgumentNullException(nameof(rootIdentity));
        _verifyPhysicalRoot = verifyPhysicalRoot;
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    public UndoPlan BuildPlan(SessionId sessionId, ToolCallId? requestedEffect = null, bool includeCompensated = false)
    {
        var all = _events.ReadFrom(sessionId, 1).Where(item => item.SessionId == sessionId).ToArray();
        var decoded = all.Select(_codecs.Decode).ToArray();
        var session = decoded.OfType<SessionCreated>().SingleOrDefault(item => item.SessionId == sessionId);
        var established = decoded.OfType<WorkspaceRootEstablished>().SingleOrDefault(item => item.SessionId == sessionId);
        if (!IsVerifiedSession(session, established))
            return requestedEffect is { } effect
                ? new UndoPlan(sessionId, [new UndoPlanEntry(new ToolCallId(effect.Value), "", "Unsupported", null, null,
                    null, 0, null, null, "", null, null, "The source session does not belong to this verified workspace.")])
                : new UndoPlan(sessionId, []);
        var started = new Dictionary<ToolCallId, (ToolCallStarted Event, DomainEvent Envelope)>();
        var success = new Dictionary<ToolCallId, (ToolCallSucceeded Event, DomainEvent Envelope)>();
        var reconciled = new Dictionary<ToolCallId, DomainEvent>();
        var integration = new Dictionary<ToolCallId, (WorktreeIntegrationStateRecorded Event, DomainEvent Envelope)>();
        var envelopesById = all.ToDictionary(item => item.EventId);
        var undoStartPairs = all.Select(item => (Envelope: item, Payload: _codecs.Decode(item)))
            .Where(item => item.Payload is UndoStarted)
            .ToDictionary(item => ((UndoStarted)item.Payload).UndoId,
                item => (Payload: (UndoStarted)item.Payload, item.Envelope), StringComparer.Ordinal);
        var undoEnds = all.Select(item => (Envelope: item, Payload: _codecs.Decode(item)))
            .Where(item => item.Payload is UndoCompleted).ToArray();
        var undoEventsValid = undoStartPairs.All(pair =>
            pair.Value.Payload.WorkspaceIdentity == _rootIdentity
            && pair.Value.Payload.Files is { Count: > 0 and <= MaxFiles }
            && pair.Value.Payload.Files.All(file => FilesystemReconciliationMetadata.IsSaneRelativePath(file.RelativePath)
                && (file.QuarantinePath is null || FilesystemReconciliationMetadata.IsSaneRelativePath(file.QuarantinePath)))
            && pair.Value.Payload.Attribution is { } attribution
            && envelopesById.TryGetValue(attribution.SourceEventId, out var sourceEnvelope)
            && _codecs.Decode(sourceEnvelope) is ToolCallStarted sourceEffect
            && sourceEffect.ToolCallId == pair.Value.Payload.OriginalEffect
            && sourceEnvelope.RunId == attribution.SourceRunId && sourceEnvelope.TaskId == attribution.SourceTaskId
            && sourceEnvelope.LaneId == attribution.SourceLaneId && sourceEnvelope.TurnId == attribution.SourceTurnId
            && sourceEnvelope.ExecutionId == attribution.SourceExecutionId
            && pair.Value.Envelope.Causation is CommandCausation startCause && startCause.CommandId == pair.Value.Payload.CommandId
            && pair.Value.Envelope.RunId == attribution.SourceRunId && pair.Value.Envelope.TaskId == attribution.SourceTaskId
            && pair.Value.Envelope.LaneId == attribution.SourceLaneId && pair.Value.Envelope.TurnId == attribution.SourceTurnId
            && pair.Value.Envelope.ExecutionId == attribution.UndoExecutionId)
            && undoEnds.All(item => undoStartPairs.TryGetValue(((UndoCompleted)item.Payload).UndoId, out var start)
                && start.Payload.CommandId == ((UndoCompleted)item.Payload).CommandId
                && start.Payload.OriginalEffect == ((UndoCompleted)item.Payload).OriginalEffect
                && start.Payload.Attribution == ((UndoCompleted)item.Payload).Attribution
                && start.Payload.WorkspaceIdentity == ((UndoCompleted)item.Payload).WorkspaceIdentity
                && start.Payload.Files.Select(file => file.RelativePath).SequenceEqual(
                    ((UndoCompleted)item.Payload).Files.Select(file => file.RelativePath), StringComparer.Ordinal)
                && item.Envelope.Causation is CommandCausation cause && cause.CommandId == start.Payload.CommandId
                && item.Envelope.RunId == start.Envelope.RunId && item.Envelope.TaskId == start.Envelope.TaskId
                && item.Envelope.LaneId == start.Envelope.LaneId && item.Envelope.TurnId == start.Envelope.TurnId
                && item.Envelope.ExecutionId == start.Payload.Attribution.UndoExecutionId);
        if (!undoEventsValid) return new UndoPlan(sessionId, []);
        var alreadyUndone = undoEnds.Select(item => (UndoCompleted)item.Payload)
            .Where(item => !includeCompensated && item.Outcome is UndoOutcome.Applied or UndoOutcome.NoOp)
            .Select(item => item.Attribution.SourceEventId).ToHashSet();
        var rootTasks = all.Where(item => item.RunId is not null).Select(item => (Envelope: item, Payload: _codecs.Decode(item)))
            .Where(item => item.Payload is RunCreated)
            .Select(item => ((RunCreated)item.Payload, item.Envelope.RunId!.Value))
            .ToDictionary(item => item.Item2, item => item.Item1.RootTask);
        var laneCandidates = all.Where(item => item.RunId is not null).Select(item => (Envelope: item, Payload: _codecs.Decode(item)))
            .Where(item => item.Payload is LaneCreated created && rootTasks.TryGetValue(item.Envelope.RunId!.Value, out var task)
                && created.TaskId == task)
            .GroupBy(item => item.Envelope.RunId!.Value)
            .Where(group => group.Count() == 1)
            .SelectMany(group => group.Select(item => (Run: group.Key, Lane: ((LaneCreated)item.Payload).LaneId)))
            .ToHashSet();
        foreach (var envelope in all)
        {
            var payload = _codecs.Decode(envelope);
            if (payload is ToolCallStarted start) started[start.ToolCallId] = (start, envelope);
            else if (payload is ToolCallSucceeded done) success[done.ToolCallId] = (done, envelope);
            else if (payload is ToolCallReconciled { Outcome: ReconciliationOutcome.Applied })
            {
                var id = ((ToolCallReconciled)payload).ToolCallId;
                if (started.TryGetValue(id, out var begun) && SameScope(begun.Envelope, envelope)) reconciled[id] = envelope;
            }
            else if (payload is WorktreeIntegrationStateRecorded state) integration[state.ToolCallId] = (state, envelope);
        }

        var rows = new List<UndoPlanEntry>();
        foreach (var pair in started.OrderByDescending(item => item.Value.Envelope.Sequence))
        {
            if (requestedEffect is not null && requestedEffect != pair.Key) continue;
            var (start, envelope) = pair.Value;
            if (alreadyUndone.Contains(envelope.EventId)) continue;
            if (!HasAttributionScope(envelope)) continue;
            var attribution = new UndoAttribution(envelope.EventId, envelope.RunId, envelope.TaskId,
                envelope.LaneId, envelope.TurnId, envelope.ExecutionId, ExecutionId.New());
            if (integration.TryGetValue(pair.Key, out var batchState))
            {
                var hasSuccess = success.TryGetValue(pair.Key, out var batchSuccess)
                    && SameScope(envelope, batchSuccess.Envelope);
                var hasRecoveredCompletion = reconciled.TryGetValue(pair.Key, out var recovered)
                    && SameScope(envelope, recovered);
                if (!SameScope(envelope, batchState.Envelope) || batchState.Event.State != WorktreeIntegrationState.Completed
                    || batchState.Event.Files is null || batchState.Event.Files.Count is < 1 or > MaxFiles
                    || (!hasSuccess && !hasRecoveredCompletion)
                    || hasSuccess && batchSuccess!.Event.AfterStateRef is not null) continue;
                AddBatchRows(rows, sessionId, pair.Key, envelope, start, batchState.Event, attribution);
                continue;
            }
            if (!success.TryGetValue(pair.Key, out var succeeded)
                || !SameScope(envelope, succeeded.Envelope)) continue;
            if (start.Reversibility is not (Reversibility.Reversible or Reversibility.Compensatable)) continue;
            // Lane worktrees need the exact durable LaneWorktree binding. Until one can be
            // resolved here, only the Run's root lane is mapped to this verified workspace.
            if (envelope.RunId is not { } run || envelope.LaneId is not { } lane
                || !laneCandidates.Contains((run.Value, lane)) || !rootTasks.TryGetValue(run.Value, out var rootTask)
                || envelope.TaskId != rootTask) continue;
            AddSingleRow(rows, sessionId, pair.Key, envelope, start, succeeded.Event, attribution);
        }
        if (requestedEffect is not null && rows.Count == 0)
            return new UndoPlan(sessionId, [new UndoPlanEntry(new ToolCallId(requestedEffect.Value), "", "Unsupported", null, null,
                null, 0, null, null, "", null, null,
                "No fully attributed reversible effect with verified pre/postimages was found.")]);
        return new UndoPlan(sessionId, rows);
    }

    public bool VerifySession(SessionId sessionId)
    {
        var payloads = _events.ReadFrom(sessionId, 1).Where(item => item.SessionId == sessionId)
            .Select(_codecs.Decode).ToArray();
        return IsVerifiedSession(payloads.OfType<SessionCreated>().SingleOrDefault(),
            payloads.OfType<WorkspaceRootEstablished>().SingleOrDefault());
    }

    private bool IsVerifiedSession(SessionCreated? session, WorkspaceRootEstablished? established)
    {
        var expectedWorkspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(_workspaceRoot)).ToString();
        return session is not null && session.WorkspaceId == expectedWorkspace && established is not null
            && PathEquals(established.CanonicalRoot, ProjectIdentity.CanonicalWorkspacePath(_workspaceRoot))
            && (!_verifyPhysicalRoot || WorkspaceRootIdentity.Verify(_workspaceRoot, established.DurableIdentity))
            && string.Equals(established.DurableIdentity, _rootIdentity, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private void AddSingleRow(List<UndoPlanEntry> rows, SessionId session, ToolCallId id,
        DomainEvent envelope, ToolCallStarted start, ToolCallSucceeded success, UndoAttribution attribution)
    {
        if (start.TargetRef is null || !FilesystemReconciliationMetadata.IsSaneRelativePath(start.TargetRef)
            || start.BeforeStateRef is null || success.AfterStateRef is null
            || start.BeforeStateRef.Size > MaxSerializedArtifactBytes || success.AfterStateRef.Size > MaxSerializedArtifactBytes
            || string.IsNullOrWhiteSpace(start.ReconciliationJson)
            || start.BeforeStateRef.MediaType != FilesystemPreimage.MediaType
            || success.AfterStateRef.MediaType != FilesystemPreimage.PostMediaType) return;
        try
        {
            var before = FilesystemPreimage.Read(_artifacts, start.BeforeStateRef);
            var after = FilesystemPreimage.Read(_artifacts, success.AfterStateRef);
            if (Size(before) > MaxImageBytes || Size(after) > MaxImageBytes) return;
            using (var document = JsonDocument.Parse(start.ReconciliationJson)) EnsureNoDuplicates(document.RootElement);
            var metadata = FilesystemReconciliationMetadata.Parse(start.ReconciliationJson);
            if (metadata is null || !SamePath(metadata.Path, start.TargetRef)
                || metadata.ExpectedPreHash != Hash(before) || metadata.ExpectedPostHash != Hash(after)) return;
            _ = ResolvePath(start.TargetRef);
            rows.Add(new UndoPlanEntry(id, start.TargetRef, "Ready", Hash(after), before,
                Hash(before), envelope.Sequence, start.BeforeStateRef, success.AfterStateRef, ScopeId(envelope), after,
                attribution, null));
        }
        catch (Exception ex) when (IsDataFailure(ex)) { }
    }

    private void AddBatchRows(List<UndoPlanEntry> rows, SessionId session, ToolCallId id,
        DomainEvent envelope, ToolCallStarted start, WorktreeIntegrationStateRecorded state, UndoAttribution attribution)
    {
        var evidence = ReadBatchEvidence(id, start, state);
        if (evidence is null) return;
        rows.AddRange(evidence.Select(file => new UndoPlanEntry(id, file.RelativePath, "Ready", Hash(file.PostimageBytes),
            file.PreimageBytes, Hash(file.PreimageBytes), envelope.Sequence, start.BeforeStateRef, null,
            ScopeId(envelope), file.PostimageBytes, attribution, null)));
    }

    /// <summary>
    /// Validates an integration's durable journal/CAS evidence without consulting the current
    /// filesystem or deciding whether the effect remains eligible for Undo. Historical diff
    /// readers use this after the workspace has changed or the effect has been compensated.
    /// </summary>
    internal IReadOnlyList<BatchEvidenceFile>? ReadBatchEvidence(ToolCallId id, ToolCallStarted start,
        WorktreeIntegrationStateRecorded state)
    {
        try
        {
            if (start.BeforeStateRef is null || _artifacts is not IProtectedArtifactStore protectedStore
                || start.BeforeStateRef.Size > MaxBatchBytes * 2
                || start.BeforeStateRef.MediaType != "application/json"
                || string.IsNullOrWhiteSpace(start.ReconciliationJson)
                || Encoding.UTF8.GetByteCount(start.ReconciliationJson) > MaxBatchBytes * 2) return null;
            using (var metadataDocument = JsonDocument.Parse(start.ReconciliationJson)) EnsureNoDuplicates(metadataDocument.RootElement);
            var metadata = JsonSerializer.Deserialize(start.ReconciliationJson, BatchJson.Default.BatchMetadata);
            if (metadata is null || metadata.Kind != "worktree.integration.batch" || metadata.Version is not (1 or 2)
                || metadata.ToolCallId != id.ToString() || metadata.Files is null || metadata.Files.Length is < 1 or > MaxFiles
                || !PathEquals(metadata.WorkspaceRoot, _workspaceRoot) || !Guid.TryParseExact(metadata.OwnershipId, "N", out _)
                || !IsSha256(metadata.ProposalId) || !IsGitOid(metadata.WorkspaceHead) || !IsGitOid(metadata.LaneHead)
                || metadata.GitCommonDirectory is null || !Path.IsPathFullyQualified(metadata.GitCommonDirectory)
                || state.State != WorktreeIntegrationState.Completed || state.ToolCallId != id
                || state.OwnershipId != metadata.OwnershipId || state.ProposalId != metadata.ProposalId
                || metadata.Files.Any(claim => claim is null) || state.Files is null
                || state.Files.Any(status => status is null) || metadata.Files.Length != state.Files.Count) return null;
            var purpose = "m7-worktree-integration:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                ProjectIdentity.CanonicalWorkspacePath(_workspaceRoot) + "\n" + metadata.OwnershipId + "\n" + metadata.ProposalId))).ToLowerInvariant();
            var json = protectedStore.GetProtectedText(start.BeforeStateRef, purpose);
            if (Encoding.UTF8.GetByteCount(json) > MaxBatchBytes * 2) return null;
            using var document = JsonDocument.Parse(json);
            EnsureNoDuplicates(document.RootElement);
            var payload = JsonSerializer.Deserialize(json, BatchJson.Default.BatchPayload);
            if (payload is null || payload.Version != metadata.Version || payload.OwnershipId != metadata.OwnershipId
                || payload.ProposalId != metadata.ProposalId || !PathEquals(payload.WorkspaceRoot, _workspaceRoot)
                || payload.Files is null || payload.Files.Any(file => file is null)
                || payload.Files.Length != metadata.Files.Length || payload.Files.Length > MaxFiles) return null;
            if (metadata.GitCommonDirectory != payload.GitCommonDirectory
                || metadata.WorkspaceHead != payload.WorkspaceHead || metadata.WorkspaceBranch != payload.WorkspaceBranch
                || metadata.LaneHead != payload.LaneHead || metadata.LaneBranch != payload.LaneBranch) return null;
            if (metadata.Version == 2 && (!IsGitOid(metadata.LaneTreeAtCapture)
                || metadata.LaneTreeAtCapture != payload.LaneTreeAtCapture
                || metadata.CompleteLaneResult != payload.CompleteLaneResult
                || state.LaneTreeAtCapture != metadata.LaneTreeAtCapture
                || state.LaneHeadAtCapture != metadata.LaneHead
                || state.CompleteLaneResult != metadata.CompleteLaneResult)) return null;
            long batchBytes = 0;
            var paths = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var verifiedRows = new List<BatchEvidenceFile>(payload.Files.Length);
            for (var index = 0; index < payload.Files.Length; index++)
            {
                var file = payload.Files[index]; var claim = metadata.Files[index];
                var status = state.Files[index];
                if (status.Outcome != ReconciliationOutcome.Applied || file.RelativePath != claim.RelativePath
                    || status.RelativePath != file.RelativePath || !FilesystemReconciliationMetadata.IsSaneRelativePath(file.RelativePath)
                    || !paths.Add(file.RelativePath)
                    || _redaction.IsSecretPath(file.RelativePath)
                    || claim.ExpectedPreSha256 != file.ExpectedPreSha256 || claim.ExpectedPostSha256 != file.ExpectedPostSha256
                    || claim.ExpectedPreMode != file.ExpectedPreMode || claim.ExpectedPostMode != file.ExpectedPostMode
                    || claim.ExpectedPreMode is not (null or "100644") || claim.ExpectedPostMode is not (null or "100644")
                    || claim.ExpectedPreMode is not null && claim.ExpectedPostMode is not null && claim.ExpectedPreMode != claim.ExpectedPostMode)
                    return null;
                var pre = Decode(file.Preimage, file.ExpectedPreSha256);
                var post = Decode(file.Postimage, file.ExpectedPostSha256);
                batchBytes = checked(batchBytes + Size(pre) + Size(post));
                if (Size(pre) > MaxImageBytes || Size(post) > MaxImageBytes || batchBytes > MaxBatchBytes) return null;
                verifiedRows.Add(new BatchEvidenceFile(file.RelativePath, pre, post));
            }
            return verifiedRows;
        }
        catch (Exception ex) when (IsDataFailure(ex) || ex is CryptographicException) { return null; }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsGitOid(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private byte[]? Decode(DurableImage? image, string? expectedHash)
    {
        if (image is null) { if (expectedHash is not null) throw new InvalidOperationException(); return null; }
        if (image.Text is null || image.Encoding is null || !Enum.TryParse<FileVersion.FileEncoding>(image.Encoding, out var encoding))
            throw new InvalidOperationException();
        var bytes = FileVersion.Encode(image.Text, encoding);
        if (Hash(bytes) != expectedHash) throw new InvalidOperationException();
        return bytes;
    }

    internal string ResolvePath(string relative)
    {
        if (!FilesystemReconciliationMetadata.IsSaneRelativePath(relative)) throw new InvalidOperationException();
        var full = Path.GetFullPath(relative, _workspaceRoot);
        var rel = Path.GetRelativePath(_workspaceRoot, full);
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException();
        return full;
    }

    private static string? Hash(byte[]? bytes) => bytes is null ? FilesystemReconciliationMetadata.AbsentPreHash : FileVersion.VersionToken(bytes);
    private static long Size(byte[]? bytes) => bytes?.LongLength ?? 0;
    private static bool SamePath(string left, string right) => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool SameScope(DomainEvent left, DomainEvent right) => left.RunId == right.RunId && left.TaskId == right.TaskId
        && left.LaneId == right.LaneId && left.TurnId == right.TurnId && left.ExecutionId == right.ExecutionId;
    private static bool HasAttributionScope(DomainEvent envelope) => envelope.RunId is not null && envelope.TaskId is not null
        && envelope.LaneId is not null && envelope.TurnId is not null && envelope.ExecutionId is not null;
    private static string ScopeId(DomainEvent envelope) => string.Join("/", envelope.RunId, envelope.TaskId, envelope.LaneId, envelope.TurnId, envelope.ExecutionId);
    private static bool IsDataFailure(Exception exception) => exception is InvalidOperationException or JsonException or IOException
        or UnauthorizedAccessException or ArgumentException or DecoderFallbackException or OverflowException;
    private static void EnsureNoDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidOperationException(); EnsureNoDuplicates(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) EnsureNoDuplicates(child);
    }

    private sealed record BatchMetadata(string Kind, int Version, string ToolCallId, string OwnershipId, string ProposalId,
        string WorkspaceRoot, string GitCommonDirectory, string WorkspaceHead, string? WorkspaceBranch,
        string LaneHead, string? LaneBranch, BatchClaim[] Files, string? LaneTreeAtCapture = null,
        bool CompleteLaneResult = false);
    private sealed record BatchClaim(string RelativePath, string? ExpectedPreSha256, string? ExpectedPostSha256,
        string? ExpectedPreMode, string? ExpectedPostMode);
    private sealed record BatchPayload(int Version, string OwnershipId, string ProposalId, string WorkspaceRoot,
        string GitCommonDirectory, string WorkspaceHead, string? WorkspaceBranch, string LaneHead,
        string? LaneBranch, DurableFileImage[] Files, string? LaneTreeAtCapture = null,
        bool CompleteLaneResult = false);
    private sealed record DurableFileImage(string RelativePath, string? ExpectedPreSha256, string? ExpectedPostSha256,
        string? ExpectedPreMode, string? ExpectedPostMode, DurableImage? Preimage, DurableImage? Postimage);
    private sealed record DurableImage(string Text, string Encoding);
    internal sealed record BatchEvidenceFile(string RelativePath, byte[]? PreimageBytes, byte[]? PostimageBytes);
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(BatchMetadata))][JsonSerializable(typeof(BatchPayload))]
    private sealed partial class BatchJson : JsonSerializerContext;
}

public sealed record UndoPlan(SessionId SessionId, IReadOnlyList<UndoPlanEntry> Entries);

public sealed record UndoPlanEntry(ToolCallId OriginalEffect, string RelativePath, string State,
    string? ExpectedCurrentHash, byte[]? RestoreBytes, string? ResultingHash, long Sequence,
    ArtifactRef? PreimageRef, ArtifactRef? PostimageRef, string Scope,
    byte[]? PostimageBytes = null,
    UndoAttribution? Attribution = null,
    string? Reason = null);
