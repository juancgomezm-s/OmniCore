namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Tools;

/// <summary>Host-owned prepare/apply service for the authorized batch integration tool.
/// The proposal and every pre/post image are protected CAS data referenced by ToolCallStarted.</summary>
internal sealed partial class WorktreeIntegrationHostCoordinator : IWorktreeIntegrationCoordinator
{
    private const long MaxBatchBytes = 8L * 1024 * 1024;
    private const int MaxFiles = 64;

    private readonly string _workspaceRoot;
    private readonly string _dataRoot;
    private readonly IArtifactStore _artifacts;
    private readonly GitWorktreeStore _git;
    private readonly IPathBoundaryValidator _paths = new PathBoundaryValidator();
    private readonly RedactionPolicy _redaction = new();
    private readonly ConcurrentDictionary<string, PreparedBatch> _prepared = new(StringComparer.Ordinal);

    internal Action<int, string>? AfterWriteForTests { get; set; }
    internal Action<int, string>? BeforePublishForTests { get; set; }

    public WorktreeIntegrationHostCoordinator(string workspaceRoot, string dataRoot,
        IArtifactStore artifacts, GitWorktreeStore git)
    {
        _workspaceRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    public ReconciliationSpec Prepare(AuthorizedToolIntent authorized, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Artifacts is null || !ReferenceEquals(context.Artifacts, _artifacts))
            throw new InvalidOperationException("Protected integration artifacts are unavailable.");
        var args = ParseArguments(authorized.Intent.NormalizedArgumentsJson);
        var workspace = ProjectIdentity.ResolvePhysicalWorkspaceRoot(context.WorkspaceRoot);
        if (!PathEquals(workspace, _workspaceRoot)) throw new InvalidOperationException("Workspace identity changed.");
        var identity = LoadIdentity(workspace, args.OwnershipId, cancellationToken);
        var capture = Capture(identity, args, cancellationToken);
        if (capture.Files.Count == 0 || capture.Files.Count > MaxFiles)
            throw new InvalidOperationException("Integration proposal has no supported file changes.");

        var images = new List<DurableFileImage>(capture.Files.Count);
        long total = 0;
        foreach (var file in capture.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_redaction.IsSecretPath(file.RelativePath))
                throw new InvalidOperationException("Secret paths cannot enter an integration artifact.");
            var full = ResolveTarget(workspace, file.RelativePath, requireParents: true);
            VerifyTopology(workspace, full);
            var pre = EncodeImage(file.Preimage, file.ExpectedPreSha256);
            var post = EncodeImage(file.Postimage, file.ExpectedPostSha256);
            if (file.ExpectedPreMode is not (null or "100644")
                || file.ExpectedPostMode is not (null or "100644")
                || file.ExpectedPreMode is not null && file.ExpectedPostMode is not null
                    && file.ExpectedPreMode != file.ExpectedPostMode)
                throw new InvalidOperationException("File mode transitions are not supported by this apply boundary.");
            total = checked(total + (file.Preimage?.LongLength ?? 0) + (file.Postimage?.LongLength ?? 0));
            if (total > MaxBatchBytes) throw new InvalidOperationException("Integration batch exceeds the safe image limit.");
                images.Add(new DurableFileImage(file.RelativePath, file.ExpectedPreSha256, file.ExpectedPostSha256,
                file.ExpectedPreMode, file.ExpectedPostMode, pre, post));
        }

        var policyRefusal = ValidatePolicy(images, context.ReadRegistry);
        if (policyRefusal is not null)
            throw new ToolPreflightException(policyRefusal.Code, policyRefusal.Message);

        var payload = new BatchPayload(1, args.OwnershipId, capture.Preview.ProposalId,
            workspace, identity.GitCommonDirectory, capture.Preview.WorkspaceHeadAtCapture,
            capture.Preview.WorkspaceBranchAtCapture, capture.Preview.LaneHeadAtCapture,
            capture.Preview.LaneBranchAtCapture, images.ToArray());
        var payloadJson = JsonSerializer.Serialize(payload, ManifestJson.Default.BatchPayload);
        var purpose = Purpose(workspace, args.OwnershipId, capture.Preview.ProposalId);
        var protectedStore = _artifacts as IProtectedArtifactStore
            ?? throw new InvalidOperationException("Protected integration storage is unavailable.");
        var reference = protectedStore.PutProtectedText(payloadJson, purpose, "application/json", ArtifactKind.Patch);
        if (!_artifacts.Verify(reference.Hash, reference.Size))
            throw new InvalidOperationException("Integration artifact failed verification.");

        var metadata = new BatchMetadata("worktree.integration.batch", 1, authorized.Intent.ToolCallId.ToString(), args.OwnershipId,
            capture.Preview.ProposalId, workspace, identity.GitCommonDirectory,
            capture.Preview.WorkspaceHeadAtCapture, capture.Preview.WorkspaceBranchAtCapture,
            capture.Preview.LaneHeadAtCapture, capture.Preview.LaneBranchAtCapture,
            images.Select(image => new BatchClaim(image.RelativePath, image.ExpectedPreSha256,
                image.ExpectedPostSha256, image.ExpectedPreMode, image.ExpectedPostMode)).ToArray());
        var durableMetadata = JsonSerializer.Serialize(metadata, ManifestJson.Default.BatchMetadata);
        _prepared[authorized.Intent.ToolCallId.ToString()] = new(metadata, reference);
        return new ReconciliationSpec(null, null, null)
        {
            BeforeStateRef = reference,
            DurableMetadataJson = durableMetadata,
            StartedEntityEvent = new WorktreeIntegrationStateRecorded(authorized.Intent.ToolCallId,
                args.OwnershipId, capture.Preview.ProposalId, WorktreeIntegrationState.Started,
                images.Select(image => new WorktreeIntegrationFileStatus(image.RelativePath,
                    ReconciliationOutcome.NotApplied)).ToArray()),
            Reversibility = Reversibility.Unknown,
        };
    }

    public async Task<ToolResult> ApplyAsync(AuthorizedToolIntent authorized, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = ParseArguments(authorized.Intent.NormalizedArgumentsJson);
        var workspace = ProjectIdentity.ResolvePhysicalWorkspaceRoot(context.WorkspaceRoot);
        if (!PathEquals(workspace, _workspaceRoot)) return ToolResult.Error(ToolErrorCode.StaleWrite,
            "Workspace identity changed; no integration files were written.");

        if (!_prepared.TryRemove(authorized.Intent.ToolCallId.ToString(), out var prepared)
            || prepared.Artifact is null || !ReferenceEquals(context.Artifacts, _artifacts))
            return ToolResult.Error(ToolErrorCode.ToolFailure, "Prepared integration state is unavailable.");
        var metadata = prepared.Metadata;

        // DescribeReconciliation has already frozen these bytes in protected CAS and the runtime
        // persisted them in its durable Barrier. Rebuild the same capture to fail on stale input.
        WorktreeIdentity identity;
        WorktreeIntegrationCapture currentCapture;
        try
        {
            identity = LoadIdentity(workspace, args.OwnershipId, cancellationToken);
            currentCapture = Capture(identity, args, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Terminal(context, authorized, metadata, WorktreeIntegrationState.Conflict,
                ReconciliationOutcome.Conflict, "Integration proposal became stale; no files were written.");
        }
        // Keep effect-time exceptions out of the no-write path above. Once ApplyPreparedAsync
        // begins a file operation, failures are conservatively unknown and left to reconciliation.
        return await ApplyPreparedAsync(authorized, context, args, workspace, identity, currentCapture,
            metadata, prepared.Artifact, cancellationToken).ConfigureAwait(false);
    }

    internal FilesystemReconciliation Reconcile(string workspaceRoot, string metadataJson,
        ArtifactRef? beforeStateRef, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var document = JsonDocument.Parse(metadataJson)) EnsureNoDuplicateProperties(document.RootElement);
            var metadata = JsonSerializer.Deserialize(metadataJson, ManifestJson.Default.BatchMetadata);
            if (metadata is null || metadata.Kind != "worktree.integration.batch" || metadata.Version != 1
                || metadata.Files is null || metadata.Files.Length is < 1 or > MaxFiles
                || beforeStateRef is null || !_artifacts.Verify(beforeStateRef.Hash, beforeStateRef.Size))
                return FilesystemReconciliation.Unresolvable("integration metadata or protected manifest unavailable");
            if (!PathEquals(ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot), _workspaceRoot)
                || !PathEquals(metadata.WorkspaceRoot, _workspaceRoot))
                return FilesystemReconciliation.Unresolvable("workspace identity changed");

            var protectedStore = _artifacts as IProtectedArtifactStore;
            if (protectedStore is null) return FilesystemReconciliation.Unresolvable("protected storage unavailable");
            var payloadJson = protectedStore.GetProtectedText(beforeStateRef,
                Purpose(_workspaceRoot, metadata.OwnershipId, metadata.ProposalId));
            var payload = ParsePayload(payloadJson);
            VerifyMetadataMatchesPayload(metadata, payload, beforeStateRef);
            var identity = LoadIdentity(_workspaceRoot, metadata.OwnershipId, cancellationToken);
            if (!PathEquals(identity.GitCommonDirectory, metadata.GitCommonDirectory))
                return FilesystemReconciliation.Unresolvable("Git common directory changed");
            var workspace = _git.InspectAsync(_workspaceRoot, cancellationToken).GetAwaiter().GetResult();
            var lane = _git.InspectAsync(identity.WorktreePath, cancellationToken).GetAwaiter().GetResult();
            if (!workspace.Succeeded || !lane.Succeeded
                || workspace.Identity!.HeadCommit != metadata.WorkspaceHead
                || workspace.Identity.Branch != metadata.WorkspaceBranch
                || lane.Identity!.HeadCommit != metadata.LaneHead
                || lane.Identity.Branch != metadata.LaneBranch
                || !PathEquals(workspace.Identity.GitCommonDirectory, metadata.GitCommonDirectory)
                || !PathEquals(lane.Identity.GitCommonDirectory, metadata.GitCommonDirectory))
                return FilesystemReconciliation.Unresolvable("Git identity changed after integration started");

            var results = new List<WorktreeIntegrationFileStatus>(metadata.Files.Length);
            foreach (var image in payload.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_redaction.IsSecretPath(image.RelativePath))
                    return FilesystemReconciliation.Unresolvable("integration contains a protected path");
                var full = ResolveTarget(_workspaceRoot, image.RelativePath, requireParents: true);
                _ = DecodeImage(image.Preimage, image.ExpectedPreSha256);
                _ = DecodeImage(image.Postimage, image.ExpectedPostSha256);
                ReconciliationOutcome state;
                try
                {
                    VerifyTopology(_workspaceRoot, full);
                    var current = CurrentHash(full);
                    state = current == image.ExpectedPostSha256 ? ReconciliationOutcome.Applied
                        : current == image.ExpectedPreSha256 ? ReconciliationOutcome.NotApplied
                        : ReconciliationOutcome.Conflict;
                }
                catch (Exception)
                {
                    // Directory/reparse/oversized/unreadable targets are neither an absent
                    // file nor a matching image. Recovery must retain them as conflicts.
                    state = ReconciliationOutcome.Conflict;
                }
                results.Add(new WorktreeIntegrationFileStatus(image.RelativePath, state));
            }
            var allApplied = results.All(file => file.Outcome == ReconciliationOutcome.Applied);
            var allPre = results.All(file => file.Outcome == ReconciliationOutcome.NotApplied);
            var entityState = allApplied ? WorktreeIntegrationState.Completed
                : allPre ? WorktreeIntegrationState.NotApplied : WorktreeIntegrationState.Conflict;
            var result = allApplied ? FilesystemReconciliation.Applied("every integration target matches its postimage")
                : allPre ? FilesystemReconciliation.NotApplied("every integration target matches its preimage")
                : FilesystemReconciliation.Conflict("integration targets have mixed or unexpected states");
            return result with { SupplementalEvent = new WorktreeIntegrationStateRecorded(
                ToolCallId.Parse(metadata.ToolCallId), metadata.OwnershipId, metadata.ProposalId,
                entityState, results) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return FilesystemReconciliation.Unresolvable("integration recovery failed closed");
        }
    }

    private async Task<ToolResult> ApplyPreparedAsync(AuthorizedToolIntent authorized,
        ToolExecutionContext context, IntegrationArgs args, string workspace, WorktreeIdentity identity,
        WorktreeIntegrationCapture capture, BatchMetadata metadata, ArtifactRef reference,
        CancellationToken cancellationToken)
    {
        // Reconciliation metadata is not passed to ExecuteAsync by design. Revalidate by rebuilding
        // a strict package from the ownership/proposal and compare it with the prepared image set.
        if (metadata.ToolCallId != authorized.Intent.ToolCallId.ToString()
            || metadata.ProposalId != capture.Preview.ProposalId || metadata.OwnershipId != args.OwnershipId
            || !PathEquals(metadata.WorkspaceRoot, workspace)
            || !PathEquals(metadata.GitCommonDirectory, identity.GitCommonDirectory)
            || metadata.WorkspaceHead != capture.Preview.WorkspaceHeadAtCapture
            || metadata.WorkspaceBranch != capture.Preview.WorkspaceBranchAtCapture
            || metadata.LaneHead != capture.Preview.LaneHeadAtCapture
            || metadata.LaneBranch != capture.Preview.LaneBranchAtCapture)
            return Terminal(context, authorized, metadata, WorktreeIntegrationState.Conflict,
                ReconciliationOutcome.Conflict, "Integration identity changed; no files were written.");

        var protectedStore = _artifacts as IProtectedArtifactStore
            ?? throw new InvalidOperationException("Protected integration storage is unavailable.");
        var purpose = Purpose(workspace, args.OwnershipId, metadata.ProposalId);
        var json = protectedStore.GetProtectedText(reference, purpose);
        var payload = ParsePayload(json);
        VerifyMetadataMatchesPayload(metadata, payload, reference);
        var expectedFiles = payload.Files.ToDictionary(file => file.RelativePath, PathComparer);
        if (!expectedFiles.Keys.ToHashSet(PathComparer).SetEquals(args.Paths)
            || !capture.Files.Select(file => file.RelativePath).ToHashSet(PathComparer).SetEquals(expectedFiles.Keys))
            return Terminal(context, authorized, metadata, WorktreeIntegrationState.Conflict,
                ReconciliationOutcome.Conflict, "Integration proposal changed; no files were written.");

        var captured = capture.Files.ToDictionary(file => file.RelativePath, PathComparer);
        var targets = new List<(DurableFileImage Image, string FullPath, byte[]? Pre, byte[]? Post)>();
        foreach (var image in payload.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = captured[image.RelativePath];
            if (source.ExpectedPreSha256 != image.ExpectedPreSha256
                || source.ExpectedPostSha256 != image.ExpectedPostSha256)
                return Terminal(context, authorized, metadata, WorktreeIntegrationState.Conflict,
                    ReconciliationOutcome.Conflict, "Integration proposal changed; no files were written.");
            var full = ResolveTarget(workspace, image.RelativePath, requireParents: true);
            VerifyTopology(workspace, full);
            var pre = DecodeImage(image.Preimage, image.ExpectedPreSha256);
            var post = DecodeImage(image.Postimage, image.ExpectedPostSha256);
            targets.Add((image, full, pre, post));
        }

        // Check EVERY preimage before the first write. This also makes a proposal stale as a whole.
        var statuses = new List<WorktreeIntegrationFileStatus>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = CurrentHash(target.FullPath);
            var expected = target.Image.ExpectedPreSha256;
            if (state != expected)
                statuses.Add(new(target.Image.RelativePath, state == target.Image.ExpectedPostSha256
                    ? ReconciliationOutcome.Applied : ReconciliationOutcome.Conflict));
            else statuses.Add(new(target.Image.RelativePath, ReconciliationOutcome.NotApplied));
        }
        if (statuses.Any(status => status.Outcome != ReconciliationOutcome.NotApplied))
        {
            context.EmitEvent?.Invoke(new WorktreeIntegrationStateRecorded(authorized.Intent.ToolCallId,
                metadata.OwnershipId, metadata.ProposalId, WorktreeIntegrationState.Conflict, statuses));
            return ToolResult.Error(ToolErrorCode.StaleWrite,
                "One or more integration targets changed; no integration files were written.");
        }

        var ledger = context.ReadRegistry?.Ledger;
        var refusal = ValidatePolicy(payload.Files, context.ReadRegistry);
        if (refusal is not null)
        {
            context.EmitEvent?.Invoke(new WorktreeIntegrationStateRecorded(authorized.Intent.ToolCallId,
                metadata.OwnershipId, metadata.ProposalId, WorktreeIntegrationState.NotApplied, statuses));
            return ToolResult.Error(refusal.Code, refusal.Message);
        }

        var touched = false;
        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                cancellationToken.ThrowIfCancellationRequested();
                VerifyTopology(workspace, target.FullPath);
                if (CurrentHash(target.FullPath) != target.Image.ExpectedPreSha256)
                    throw new StaleIntegrationTargetException();
                touched = true;
                BeforePublishForTests?.Invoke(index, target.Image.RelativePath);
                WriteOne(target.FullPath, target.Post, expectedAbsent: target.Pre is null);
                if (ledger is not null)
                {
                    var before = target.Pre is null ? string.Empty : FileVersion.Decode(target.Pre).Text;
                    var after = target.Post is null ? string.Empty : FileVersion.Decode(target.Post).Text;
                    FileVersion.ChangedLines(before, after, out var deleted, out var inserted);
                    ledger.RecordMutation(target.Image.RelativePath, deleted, inserted,
                        authorized.Intent.ToolCallId, OperationFor(target.Image),
                        FileVersion.CountLines(before));
                }
                AfterWriteForTests?.Invoke(index, target.Image.RelativePath);
            }
            foreach (var target in targets)
            {
                VerifyTopology(workspace, target.FullPath);
                if (CurrentHash(target.FullPath) != target.Image.ExpectedPostSha256)
                    throw new StaleIntegrationTargetException();
            }
        }
        catch (OperationCanceledException) when (!touched) { throw; }
        catch (StaleIntegrationTargetException) when (!touched)
        {
            var classified = ClassifyNow(targets);
            context.EmitEvent?.Invoke(new WorktreeIntegrationStateRecorded(authorized.Intent.ToolCallId,
                metadata.OwnershipId, metadata.ProposalId, WorktreeIntegrationState.Conflict, classified));
            return ToolResult.Error(ToolErrorCode.StaleWrite,
                "An integration target changed before it could be written; no target was overwritten.");
        }
        catch (Exception) when (!touched)
        {
            var classified = ClassifyNow(targets);
            context.EmitEvent?.Invoke(new WorktreeIntegrationStateRecorded(authorized.Intent.ToolCallId,
                metadata.OwnershipId, metadata.ProposalId, WorktreeIntegrationState.Conflict, classified));
            return ToolResult.Error(ToolErrorCode.ToolFailure,
                "Integration could not safely begin; no target was overwritten.");
        }
        catch (Exception) when (touched)
        {
            var classified = ClassifyNow(targets);
            context.EmitEvent?.Invoke(new WorktreeIntegrationStateRecorded(authorized.Intent.ToolCallId,
                metadata.OwnershipId, metadata.ProposalId, WorktreeIntegrationState.Conflict, classified));
            return new ToolResult("Integration was interrupted; recovery will classify every target.", null,
                null, 0, false, EffectOutcome.Unknown, isError: true, ToolErrorCode.UnknownEffect);
        }

        var applied = targets.Select(target => new WorktreeIntegrationFileStatus(target.Image.RelativePath,
            ReconciliationOutcome.Applied)).ToArray();
        context.EmitEvent?.Invoke(new WorktreeIntegrationStateRecorded(authorized.Intent.ToolCallId,
            metadata.OwnershipId, metadata.ProposalId, WorktreeIntegrationState.Completed, applied));
        return new ToolResult("Integration proposal applied to the workspace.", null, null, 0, false,
            EffectOutcome.Applied);
    }

    private ToolResult Terminal(ToolExecutionContext context, AuthorizedToolIntent intent, BatchMetadata metadata,
        WorktreeIntegrationState state, ReconciliationOutcome outcome, string summary)
    {
        context.EmitEvent?.Invoke(new WorktreeIntegrationStateRecorded(intent.Intent.ToolCallId,
            metadata.OwnershipId, metadata.ProposalId, state,
            metadata.Files.Select(file => new WorktreeIntegrationFileStatus(file.RelativePath, outcome)).ToArray()));
        return ToolResult.Error(ToolErrorCode.StaleWrite, summary);
    }

    private List<WorktreeIntegrationFileStatus> ClassifyNow(
        IReadOnlyList<(DurableFileImage Image, string FullPath, byte[]? Pre, byte[]? Post)> targets) =>
        targets.Select(target =>
        {
            ReconciliationOutcome state;
            try
            {
                VerifyTopology(_workspaceRoot, target.FullPath);
                var current = CurrentHash(target.FullPath);
                state = current == target.Image.ExpectedPostSha256 ? ReconciliationOutcome.Applied
                    : current == target.Image.ExpectedPreSha256 ? ReconciliationOutcome.NotApplied
                    : ReconciliationOutcome.Conflict;
            }
            catch (Exception) { state = ReconciliationOutcome.Conflict; }
            return new WorktreeIntegrationFileStatus(target.Image.RelativePath, state);
        }).ToList();

    private WorktreeIdentity LoadIdentity(string root, string ownershipId, CancellationToken cancellationToken)
    {
        var loaded = _git.LoadIdentityAsync(root, _dataRoot, ownershipId, cancellationToken).GetAwaiter().GetResult();
        if (!loaded.Succeeded) throw new InvalidOperationException("Worktree ownership could not be verified.");
        return loaded.Worktree!;
    }

    private WorktreeIntegrationCapture Capture(WorktreeIdentity identity, IntegrationArgs args,
        CancellationToken cancellationToken)
    {
        var result = _git.CaptureIntegrationForApplyAsync(new(identity, args.ProposalId, args.Paths),
            cancellationToken).GetAwaiter().GetResult();
        if (!result.Succeeded || result.Capture is null)
            throw new InvalidOperationException("Integration proposal is stale or unsupported.");
        return result.Capture;
    }

    private static IntegrationArgs ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var ownership = root.GetProperty("ownershipId").GetString() ?? "";
        var proposal = root.GetProperty("proposalId").GetString() ?? "";
        var paths = root.GetProperty("paths").EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
        if (!Guid.TryParseExact(ownership, "N", out _) || proposal.Length != 64
            || proposal.Any(ch => !Uri.IsHexDigit(ch)) || paths.Length is < 1 or > MaxFiles
            || paths.Any(string.IsNullOrWhiteSpace) || paths.Distinct(PathComparer).Count() != paths.Length)
            throw new InvalidOperationException("Integration proposal arguments are invalid.");
        return new(ownership, proposal.ToLowerInvariant(), paths);
    }

    private string ResolveTarget(string root, string relative, bool requireParents)
    {
        if (!_paths.IsSafeRelative(relative) || _redaction.IsSecretPath(relative))
            throw new InvalidOperationException("Integration path is unsupported.");
        var full = Path.GetFullPath(relative, root);
        if (!_paths.IsWithin(full, root)) throw new InvalidOperationException("Integration path escaped the workspace.");
        var parent = Path.GetDirectoryName(full) ?? throw new InvalidOperationException("Integration path has no parent.");
        if (requireParents && !Directory.Exists(parent))
            throw new InvalidOperationException("New directory creation is not supported by this integration.");
        return full;
    }

    private static void VerifyTopology(string root, string full)
    {
        var current = Path.GetDirectoryName(full);
        while (current is not null && IsWithinOrEqual(root, current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { throw new InvalidOperationException("Target parent topology changed."); }
            catch (DirectoryNotFoundException) { throw new InvalidOperationException("Target parent topology changed."); }
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("Target parent topology is unsupported.");
            if (PathEquals(current, root)) break;
            current = Path.GetDirectoryName(current);
        }
        var target = ObserveTarget(full, readBytes: false);
        if (target.Kind is TargetKind.Directory or TargetKind.Unsafe)
            throw new InvalidOperationException("Target file topology or attributes are unsupported.");
    }

    private static MutationRefusal? ValidatePolicy(IReadOnlyList<DurableFileImage> files,
        FileReadRegistry? registry)
    {
        if (registry is null) return null;
        var ledger = registry.Ledger;
        var policy = ledger.MutationPolicy;
        var proposals = new List<MutationProposal>(files.Count);
        foreach (var image in files)
        {
            var pre = DecodeImage(image.Preimage, image.ExpectedPreSha256);
            var post = DecodeImage(image.Postimage, image.ExpectedPostSha256);
            var before = pre is null ? string.Empty : FileVersion.Decode(pre).Text;
            var after = post is null ? string.Empty : FileVersion.Decode(post).Text;
            var exists = pre is not null;
            var operation = OperationFor(image);
            if (exists && (policy?.RequirePriorRead ?? true)
                && !registry.Matches(image.RelativePath, image.ExpectedPreSha256!))
            {
                ledger.RecordTokenViolation(image.RelativePath, operation);
                return new MutationRefusal(ToolErrorCode.PriorReadRequired,
                    "Every existing integration target must have been read at its captured version in this Run.");
            }
            FileVersion.ChangedLines(before, after, out var deleted, out var inserted);
            proposals.Add(new MutationProposal(image.RelativePath, operation, exists,
                deleted, inserted, FileVersion.CountLines(before)));
        }
        return ledger.RefuseMutationBatch(proposals);
    }

    private static ModelToolCapability OperationFor(DurableFileImage image) => image.Postimage is null
        ? ModelToolCapability.DeleteFile
        : image.Preimage is null ? ModelToolCapability.CreateFile : ModelToolCapability.ReplaceFile;

    private static DurableImage? EncodeImage(byte[]? bytes, string? expectedHash)
    {
        if (bytes is null)
        {
            if (expectedHash is not null) throw new InvalidOperationException("Image absence/hash mismatch.");
            return null;
        }
        if (bytes.Length > 2 * 1024 * 1024 || FileVersion.VersionToken(bytes) != expectedHash)
            throw new InvalidOperationException("Image hash or size is unsupported.");
        FileVersion.DecodedFile decoded;
        try { decoded = FileVersion.Decode(bytes); }
        catch (UnsupportedEncodingException) { throw new InvalidOperationException("Binary or unsupported text encoding is not supported."); }
        var reconstructed = FileVersion.Encode(decoded.Text, decoded.Encoding);
        if (!bytes.AsSpan().SequenceEqual(reconstructed))
            throw new InvalidOperationException("Image encoding cannot be preserved exactly.");
        if (new RedactionPolicy().Redact(decoded.Text) != decoded.Text
            || SecretRedactorRegistry.Current is { } secrets && secrets.Redact(decoded.Text) != decoded.Text)
            throw new InvalidOperationException("Content requiring redaction cannot enter an integration artifact.");
        return new DurableImage(decoded.Text, decoded.Encoding.ToString());
    }

    private static byte[]? DecodeImage(DurableImage? image, string? expectedHash)
    {
        if (image is null)
        {
            if (expectedHash is not null) throw new InvalidOperationException("Manifest absence/hash mismatch.");
            return null;
        }
        if (!Enum.TryParse<FileVersion.FileEncoding>(image.Encoding, false, out var encoding))
            throw new InvalidOperationException("Manifest encoding is unsupported.");
        var bytes = FileVersion.Encode(image.Text, encoding);
        if (FileVersion.VersionToken(bytes) != expectedHash) throw new InvalidOperationException("Manifest hash mismatch.");
        return bytes;
    }

    private static void WriteOne(string path, byte[]? bytes, bool expectedAbsent)
    {
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); return; }
        var parent = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(parent, ".omni-integration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       81920, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Create proposals must never replace a file that appeared after the
            // last preimage check. Replacements remain atomic, but the filesystem
            // API does not offer compare-and-swap against an external editor.
            File.Move(temporary, path, overwrite: !expectedAbsent);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private static string? CurrentHash(string path)
    {
        var observed = ObserveTarget(path, readBytes: true);
        return observed.Kind switch
        {
            TargetKind.Missing => null,
            TargetKind.RegularFile => FileVersion.VersionToken(observed.Bytes!),
            _ => throw new InvalidOperationException("Target is not a supported regular file."),
        };
    }

    private static TargetObservation ObserveTarget(string path, bool readBytes)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new(TargetKind.Missing, null); }
        catch (DirectoryNotFoundException) { return new(TargetKind.Missing, null); }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return new(TargetKind.Unsafe, null);
        if ((attributes & FileAttributes.Directory) != 0)
            return new(TargetKind.Directory, null);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            return new(TargetKind.Unsafe, null);
        if (!OperatingSystem.IsWindows())
        {
            var unixMode = File.GetUnixFileMode(path);
            if ((unixMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return new(TargetKind.Unsafe, null);
        }
        if (!readBytes) return new(TargetKind.RegularFile, null);
        var info = new FileInfo(path);
        if (info.Length > 2 * 1024 * 1024)
            throw new InvalidOperationException("Target exceeds the supported recovery size.");
        return new(TargetKind.RegularFile, File.ReadAllBytes(path));
    }

    private static BatchPayload ParsePayload(string json)
    {
        using (var document = JsonDocument.Parse(json)) EnsureNoDuplicateProperties(document.RootElement);
        var payload = JsonSerializer.Deserialize(json, ManifestJson.Default.BatchPayload)
            ?? throw new InvalidOperationException("Protected integration manifest is invalid.");
        if (payload.Version != 1 || payload.Files is null || payload.Files.Length is < 1 or > MaxFiles)
            throw new InvalidOperationException("Protected integration manifest version/size is invalid.");
        return payload;
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate manifest field.");
                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) EnsureNoDuplicateProperties(item);
    }

    private static void VerifyMetadataMatchesPayload(BatchMetadata metadata, BatchPayload payload,
        ArtifactRef reference)
    {
        if (metadata.Kind != "worktree.integration.batch" || metadata.Version != 1
            || payload.Version != 1 || metadata.ToolCallId.Length == 0
            || metadata.OwnershipId != payload.OwnershipId || metadata.ProposalId != payload.ProposalId
            || !PathEquals(metadata.WorkspaceRoot, payload.WorkspaceRoot)
            || !PathEquals(metadata.GitCommonDirectory, payload.GitCommonDirectory)
            || metadata.WorkspaceHead != payload.WorkspaceHead
            || metadata.WorkspaceBranch != payload.WorkspaceBranch
            || metadata.LaneHead != payload.LaneHead || metadata.LaneBranch != payload.LaneBranch
            || metadata.Files.Length != payload.Files.Length)
            throw new InvalidOperationException("Integration manifest identity mismatch.");
        for (var i = 0; i < metadata.Files.Length; i++)
        {
            var claim = metadata.Files[i]; var image = payload.Files[i];
            if (claim.RelativePath != image.RelativePath || claim.ExpectedPreSha256 != image.ExpectedPreSha256
                || claim.ExpectedPostSha256 != image.ExpectedPostSha256 || claim.ExpectedPreMode != image.ExpectedPreMode
                || claim.ExpectedPostMode != image.ExpectedPostMode)
                throw new InvalidOperationException("Integration manifest claims mismatch.");
        }
        if (reference.Size > MaxBatchBytes * 2) throw new InvalidOperationException("Integration artifact exceeds the safe manifest limit.");
    }

    private static string Purpose(string root, string ownershipId, string proposalId) =>
        "m7-worktree-integration:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ProjectIdentity.CanonicalWorkspacePath(root) + "\n" + ownershipId + "\n" + proposalId))).ToLowerInvariant();

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left),
        Path.GetFullPath(right), PathComparer == StringComparer.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsWithinOrEqual(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record IntegrationArgs(string OwnershipId, string ProposalId, string[] Paths);
    private sealed record DurableImage(string Text, string Encoding);
    private sealed record DurableFileImage(string RelativePath, string? ExpectedPreSha256,
        string? ExpectedPostSha256, string? ExpectedPreMode, string? ExpectedPostMode,
        DurableImage? Preimage, DurableImage? Postimage);
    private sealed record BatchClaim(string RelativePath, string? ExpectedPreSha256,
        string? ExpectedPostSha256, string? ExpectedPreMode, string? ExpectedPostMode);
    private sealed record BatchMetadata(string Kind, int Version, string ToolCallId, string OwnershipId, string ProposalId,
        string WorkspaceRoot, string GitCommonDirectory, string WorkspaceHead, string? WorkspaceBranch,
        string LaneHead, string? LaneBranch, BatchClaim[] Files);
    private sealed record BatchPayload(int Version, string OwnershipId, string ProposalId, string WorkspaceRoot,
        string GitCommonDirectory, string WorkspaceHead, string? WorkspaceBranch, string LaneHead,
        string? LaneBranch, DurableFileImage[] Files);
    private sealed record PreparedBatch(BatchMetadata Metadata, ArtifactRef Artifact);
    private sealed class StaleIntegrationTargetException : Exception { }
    private enum TargetKind { Missing, RegularFile, Directory, Unsafe }
    private sealed record TargetObservation(TargetKind Kind, byte[]? Bytes);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(BatchMetadata))]
    [JsonSerializable(typeof(BatchPayload))]
    private sealed partial class ManifestJson : JsonSerializerContext;
}
