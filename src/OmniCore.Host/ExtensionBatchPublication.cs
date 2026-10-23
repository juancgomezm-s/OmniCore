namespace OmniCore.Host;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Infrastructure;
using OmniCore.Execution;
using OmniCore.Tools;

/// <summary>Core action that applies one complete protected Strong-v1 extension proposal.</summary>
internal sealed class ExtensionBatchApplyTool : ITool, ITrustedBatchPublicationTool,
    IEffectiveToolAuthorizationIntents
{
    private readonly ToolDescriptor _descriptor = new(new ToolId("extension.batch.apply"),
        "Applies one complete protected extension write proposal after authorizing every affected file.",
        new InputSchema("{\"type\":\"object\",\"properties\":{\"proposalId\":{\"type\":\"string\"},\"artifactHash\":{\"type\":\"string\"},\"artifactSize\":{\"type\":\"integer\"}},\"required\":[\"proposalId\",\"artifactHash\",\"artifactSize\"],\"additionalProperties\":false}"),
        ["extension", "batch", "write"], false, true, ToolRisk.High, ComponentSource.Core(),
        ToolProtection.Protected, EffectClass.Reconcilable);
    private readonly IExtensionBatchPublicationCoordinator _coordinator;

    public ExtensionBatchApplyTool(IExtensionBatchPublicationCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.NormalizedArgumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || root.GetProperty("proposalId").ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(root.GetProperty("proposalId").GetString(), "N", out _)
                || root.GetProperty("artifactHash").ValueKind != JsonValueKind.String
                || !IsHash(root.GetProperty("artifactHash").GetString()!)
                || root.GetProperty("artifactSize").ValueKind != JsonValueKind.Number
                || !root.GetProperty("artifactSize").TryGetInt64(out var size) || size is <= 0 or > 16 * 1024 * 1024)
                return Rejected();
            var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.Reconcilable, new ResourceClaims([], [], [], null, []), ToolRisk.High, null);
            return new Prepared(intent);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { return Rejected(); }
    }

    public IReadOnlyList<ToolIntent> ResolveEffectiveAuthorizationIntents(ToolIntent intent,
        ToolPreparationContext preparation, ToolExecutionContext execution, CancellationToken cancellationToken) =>
        _coordinator.ResolveAuthorizedIntents(intent, execution, cancellationToken);

    public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent,
        ToolExecutionContext context, CancellationToken cancellationToken) =>
        _coordinator.PrepareExtensionBatch(intent, context, cancellationToken);

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken) => _coordinator.ApplyExtensionBatchAsync(intent, context, cancellationToken);

    public bool ValidatePublicationEnvelope(ToolIntent intent, IReadOnlyList<ToolIntent> effectiveIntents, ToolExecutionContext context,
        ReconciliationSpec reconciliation)
    {
        try
        {
            if (context.Artifacts is null || reconciliation.BeforeStateRef is not { } artifact
                || !context.Artifacts.Verify(artifact.Hash, artifact.Size)
                || reconciliation.StartedEntityEvent is not null
                || reconciliation.DurableMetadataJson is not { Length: > 0 } json) return false;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 8
                || root.GetProperty("kind").GetString() != "extension.write.batch"
                || root.GetProperty("version").GetInt32() != 1
                || root.GetProperty("toolCallId").GetString() != intent.ToolCallId.ToString()
                || root.GetProperty("proposalId").ValueKind != JsonValueKind.String
                || root.GetProperty("workspaceRoot").GetString() != Path.GetFullPath(context.WorkspaceRoot)
                || root.GetProperty("artifactHash").GetString() != artifact.Hash.Value
                || root.GetProperty("artifactSize").GetInt64() != artifact.Size
                || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() is < 1 or > 64) return false;
            var authorizedPaths = effectiveIntents.SelectMany(effective => effective.Claims.Writes).Select(path =>
                Path.GetRelativePath(context.WorkspaceRoot, Path.GetFullPath(path, context.WorkspaceRoot))
                    .Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(PathComparer);
            var metadataPaths = new HashSet<string>(PathComparer);
            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 3
                    || item.GetProperty("path").ValueKind != JsonValueKind.String
                    || item.GetProperty("beforeSha256").ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                    || item.GetProperty("afterSha256").ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
                var path = item.GetProperty("path").GetString()!;
                if (!metadataPaths.Add(path) || !authorizedPaths.Contains(path)) return false;
            }
            return metadataPaths.SetEquals(authorizedPaths);
        }
        catch (Exception) { return false; }
    }

    private static PreparationRejected Rejected() => new("Invalid extension batch proposal reference.", null,
        ToolErrorCode.InvalidArguments);
    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed partial class WorktreeIntegrationHostCoordinator : IExtensionBatchPublicationCoordinator
{
    private readonly ConcurrentDictionary<string, ExtensionPreparedBatch> _preparedExtensions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<ExtensionWriteBatchProposal, string, bool>> _extensionValidators = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<ExtensionWriteBatchProposal, string, bool>> _processProposalApplied = new(StringComparer.Ordinal);

    public void RegisterExtensionProposalValidator(string toolId,
        Func<ExtensionWriteBatchProposal, string, bool> validator)
    {
        if (string.IsNullOrWhiteSpace(toolId) || !_extensionValidators.TryAdd(toolId,
                validator ?? throw new ArgumentNullException(nameof(validator))))
            throw new InvalidOperationException("Extension proposal validator registration is invalid or duplicated.");
    }

    public void RegisterProcessProposalApplied(string toolId,
        Func<ExtensionWriteBatchProposal, string, bool> complete)
    {
        if (!PersistentProcessSessionManager.IsProcessWriterTool(toolId)
            || !_processProposalApplied.TryAdd(toolId, complete ?? throw new ArgumentNullException(nameof(complete))))
            throw new InvalidOperationException("Process proposal completion registration is invalid or duplicated.");
    }

    public IReadOnlyList<ToolIntent> ResolveAuthorizedIntents(ToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var proposal = ReadExtensionProposal(intent, context, cancellationToken, out _);
        var workspace = Path.GetFullPath(context.WorkspaceRoot);
        var result = new List<ToolIntent>(proposal.Changes.Count);
        var policyImages = new List<DurableFileImage>(proposal.Changes.Count);
        foreach (var change in proposal.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = ResolveExtensionTarget(workspace, change.Path);
            var before = File.Exists(full) ? File.ReadAllBytes(full) : null;
            if (Hash(before) != change.BeforeSha256)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                    "Extension proposal no longer matches the captured workspace preimage.");
            var history = context.MutationHistory?.ReadFile(workspace, change.Path);
            var operation = change.AfterBytes is null ? ModelToolCapability.DeleteFile
                : change.BeforeSha256 is null && history?.State != WorkspaceFileHistoryState.Tombstone
                    ? ModelToolCapability.CreateFile : ModelToolCapability.ReplaceFile;
            var toolId = operation switch
            {
                ModelToolCapability.CreateFile => new ToolId("filesystem.write"),
                ModelToolCapability.DeleteFile => new ToolId("filesystem.delete"),
                _ => new ToolId("filesystem.write"),
            };
            if (history is { State: WorkspaceFileHistoryState.Present, ExpectedHash: { } expected }
                && expected != change.BeforeSha256)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension target differs from durable workspace history.");
            if (history?.State is WorkspaceFileHistoryState.ExternalConflict or WorkspaceFileHistoryState.Unknown)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension target has unresolved durable history.");
            var logicalBefore = before;
            if (logicalBefore is null && operation == ModelToolCapability.ReplaceFile
                && history is { State: WorkspaceFileHistoryState.Tombstone, ExpectedHash: { } tombstoneHash,
                    ImageRef: { } tombstoneImage })
                logicalBefore = ReadHistoryImage(context.Artifacts, tombstoneImage, tombstoneHash);
            if (operation == ModelToolCapability.ReplaceFile && logicalBefore is null)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                    "The previous version required to authorize replacement is unavailable.");
            var beforeText = logicalBefore is null ? string.Empty : FileVersion.Decode(logicalBefore).Text;
            var afterText = change.AfterBytes is null ? string.Empty : FileVersion.Decode(change.AfterBytes).Text;
            FileVersion.ChangedLines(beforeText, afterText, out var deleted, out var inserted);
            var proposalBudget = new MutationProposal(change.Path, operation, logicalBefore is not null,
                deleted, inserted, FileVersion.CountLines(beforeText));
            policyImages.Add(new DurableFileImage(change.Path, change.BeforeSha256, change.AfterSha256,
                null, null, EncodeImage(before, change.BeforeSha256), EncodeImage(change.AfterBytes, change.AfterSha256)));
            result.Add(new ToolIntent(intent.ToolCallId, toolId, intent.NormalizedArgumentsJson,
                EffectClass.Reconcilable, new ResourceClaims([], [full], [], null, []), ToolRisk.High, null,
                operation, requiresPhysicalCreateCapability: operation == ModelToolCapability.CreateFile
                    || operation == ModelToolCapability.ReplaceFile && before is null,
                effectiveMutationHistoryRevision: history?.Revision,
                effectiveMutationProposal: proposalBudget));
        }
        if (ValidatePolicy(policyImages, context) is { } refusal)
            throw new ToolPreflightException(refusal.Code, refusal.Message);
        return result;
    }

    public ReconciliationSpec PrepareExtensionBatch(AuthorizedToolIntent authorized, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var proposal = ReadExtensionProposal(authorized.Intent, context, cancellationToken, out var reference);
        var workspace = Path.GetFullPath(context.WorkspaceRoot);
        if (!PathEquals(workspace, _workspaceRoot))
            throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Workspace identity changed.");
        if (proposal.Changes.Count is < 1 or > 64 || proposal.Changes.Select(x => x.Path).Distinct(PathComparer).Count() != proposal.Changes.Count)
            throw new ToolPreflightException(ToolErrorCode.InvalidArguments, "Extension batch is invalid.");

        var files = new List<ExtensionPreparedFile>(proposal.Changes.Count);
        var policyImages = new List<DurableFileImage>(proposal.Changes.Count);
        long total = 0;
        foreach (var change in proposal.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = ResolveExtensionTarget(workspace, change.Path);
            var before = File.Exists(full) ? File.ReadAllBytes(full) : null;
            if (Hash(before) != change.BeforeSha256 || Hash(change.AfterBytes) != change.AfterSha256)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension proposal preimage changed; reread and rerun the extension.");
            if (before is not null && change.AfterBytes is null && !File.Exists(full))
                throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension delete target changed.");
            total = checked(total + (before?.LongLength ?? 0) + (change.AfterBytes?.LongLength ?? 0));
            if (total > 16L * 1024 * 1024) throw new ToolPreflightException(ToolErrorCode.LimitExceeded, "Extension batch exceeds the safe image limit.");
            var history = context.MutationHistory?.ReadFile(workspace, change.Path);
            if (history is { State: WorkspaceFileHistoryState.Present, ExpectedHash: { } known }
                && known != change.BeforeSha256)
            {
                context.MutationHistory!.MarkConflict(workspace, change.Path, change.BeforeSha256, cancellationToken);
                throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension target differs from durable workspace history.");
            }
            var logicalBefore = before;
            if (logicalBefore is null && change.AfterBytes is not null
                && history is { State: WorkspaceFileHistoryState.Tombstone, ExpectedHash: { } tombstoneHash,
                    ImageRef: { } tombstoneImage })
                logicalBefore = ReadHistoryImage(context.Artifacts, tombstoneImage, tombstoneHash);
            var logicalBeforeHash = logicalBefore is null ? null : FileVersion.VersionToken(logicalBefore);
            if (history is { State: WorkspaceFileHistoryState.Tombstone, ExpectedHash: { } expectedTombstone }
                && change.AfterBytes is not null && logicalBeforeHash != expectedTombstone)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                    "Deleted target history image is unavailable or stale.");
            var logicalOperation = change.AfterBytes is null ? ModelToolCapability.DeleteFile
                : logicalBefore is null ? ModelToolCapability.CreateFile : ModelToolCapability.ReplaceFile;
            files.Add(new ExtensionPreparedFile(change.Path, full, before, change.AfterBytes,
                change.BeforeSha256, change.AfterSha256, logicalBefore, logicalBeforeHash, logicalOperation));
            policyImages.Add(new DurableFileImage(change.Path, change.BeforeSha256, change.AfterSha256,
                null, null, EncodeImage(before, change.BeforeSha256), EncodeImage(change.AfterBytes, change.AfterSha256)));
        }
        if (ValidatePolicy(policyImages, context) is { } policyRefusal)
            throw new ToolPreflightException(policyRefusal.Code, policyRefusal.Message);
        var store = context.MutationHistory ?? throw new ToolPreflightException(ToolErrorCode.ToolFailure,
            "Durable workspace mutation history is unavailable.");
        if (string.IsNullOrWhiteSpace(context.MutationRunId) || string.IsNullOrWhiteSpace(context.MutationTurnId))
            throw new ToolPreflightException(ToolErrorCode.ToolFailure, "Durable mutation attribution is unavailable.");
        var receipts = files.Select(file =>
        {
            var previous = store.ReadFile(workspace, file.RelativePath);
            ArtifactRef? preRef = file.LogicalBefore is null ? previous?.ImageRef
                : FilesystemPreimage.CaptureHistoryImage(context.Artifacts!, file.LogicalBefore);
            ArtifactRef? postRef = file.After is null ? null : FilesystemPreimage.CaptureHistoryImage(context.Artifacts!, file.After, postimage: true);
            var beforeText = file.LogicalBefore is null ? string.Empty : FileVersion.Decode(file.LogicalBefore).Text;
            var afterText = file.After is null ? string.Empty : FileVersion.Decode(file.After).Text;
            FileVersion.ChangedLines(beforeText, afterText, out var deleted, out var inserted);
            var operation = file.LogicalOperation;
            return new WorkspaceMutationReceipt(store.GetWorkspaceId(workspace), file.RelativePath,
                authorized.Intent.ToolCallId, context.MutationRunId, context.MutationTurnId,
                operation.ToString(), file.BeforeHash, file.AfterHash, preRef, postRef, deleted, inserted,
                WorkspaceMutationReceiptState.Prepared);
        }).ToArray();
        store.Prepare(receipts, cancellationToken);
        var metadata = WriteExtensionMetadata(authorized.Intent.ToolCallId, proposal.ProposalId, workspace, reference, files);
        _preparedExtensions[authorized.Intent.ToolCallId.ToString()] = new(proposal, reference, files);
        return new ReconciliationSpec(null, null, null)
        {
            BeforeStateRef = reference,
            DurableMetadataJson = metadata,
            Reversibility = Reversibility.Unknown,
        };
    }

    public async Task<ToolResult> ApplyExtensionBatchAsync(AuthorizedToolIntent authorized, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!_preparedExtensions.TryRemove(authorized.Intent.ToolCallId.ToString(), out var batch))
            return ToolResult.Error(ToolErrorCode.ToolFailure, "Prepared extension batch is unavailable.");
        var workspace = Path.GetFullPath(context.WorkspaceRoot);
        var processOrigin = batch.Proposal.Origin == "process";
        if (processOrigin && !_processProposalApplied.ContainsKey(batch.Proposal.ToolId))
            return ToolResult.Error(ToolErrorCode.PermissionDenied,
                "The process proposal owner is unavailable; no files were published.");
        foreach (var file in batch.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Hash(File.Exists(file.FullPath) ? File.ReadAllBytes(file.FullPath) : null) != file.BeforeHash)
                return ToolResult.Error(ToolErrorCode.StaleWrite, "Extension batch became stale before publication; no files were written.");
            VerifyExtensionTopology(workspace, file.FullPath);
        }
        if (!_extensionValidators.TryGetValue(batch.Proposal.ToolId, out var currentValidator)
            || !currentValidator(batch.Proposal, workspace))
            return ToolResult.Error(ToolErrorCode.PermissionDenied,
                "The originating extension package or consent is no longer current.");
        var written = 0;
        try
        {
            foreach (var file in batch.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifyExtensionTopology(workspace, file.FullPath);
                if (Hash(File.Exists(file.FullPath) ? File.ReadAllBytes(file.FullPath) : null) != file.BeforeHash)
                    throw new InvalidDataException("Extension target changed immediately before publication.");
                if (file.After is null) File.Delete(file.FullPath);
                else WriteExtensionAtomically(file.FullPath, file.After, file.Before is null);
                written++;
                VerifyExtensionTopology(workspace, file.FullPath);
                if (Hash(File.Exists(file.FullPath) ? File.ReadAllBytes(file.FullPath) : null) != file.AfterHash)
                    throw new InvalidDataException("Extension target postimage could not be verified.");
                var ledger = context.ReadRegistry?.Ledger;
                if (ledger is not null)
                {
                    var beforeText = file.LogicalBefore is null ? string.Empty : FileVersion.Decode(file.LogicalBefore).Text;
                    var afterText = file.After is null ? string.Empty : FileVersion.Decode(file.After).Text;
                    FileVersion.ChangedLines(beforeText, afterText, out var deleted, out var inserted);
                    var op = file.LogicalOperation;
                    ledger.RecordMutation(file.RelativePath, deleted, inserted, authorized.Intent.ToolCallId,
                        op, FileVersion.CountLines(beforeText), context.EmitEvent);
                }
            }
            CompleteExtensionHistory(context, authorized.Intent, batch.Files, cancellationToken);
            if (processOrigin && (!_processProposalApplied.TryGetValue(batch.Proposal.ToolId, out var complete)
                || !complete(batch.Proposal, workspace)))
                return new ToolResult("Process batch applied, but its lifecycle receipt requires reconciliation.",
                    null, null, batch.Files.Count, false, EffectOutcome.Unknown, true, ToolErrorCode.UnknownEffect);
            return new ToolResult("Extension batch applied.", null, null, batch.Files.Count, false, EffectOutcome.Applied);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new ToolResult("Extension batch publication requires reconciliation (" + ex.GetType().Name + ").",
                null, null, written, false, EffectOutcome.Unknown, true, ToolErrorCode.UnknownEffect);
        }
    }

    private ExtensionWriteBatchProposal ReadExtensionProposal(ToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken, out ArtifactRef reference)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(intent.NormalizedArgumentsJson);
        var args = doc.RootElement;
        var proposalId = args.GetProperty("proposalId").GetString()!;
        var hash = args.GetProperty("artifactHash").GetString()!;
        var size = args.GetProperty("artifactSize").GetInt64();
        reference = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(hash.ToLowerInvariant()), size,
            "application/json", ArtifactKind.Patch, Sensitivity.Sensitive);
        var protectedStore = context.Artifacts as IProtectedArtifactStore
            ?? throw new ToolPreflightException(ToolErrorCode.ToolFailure, "Protected extension artifacts are unavailable.");
        var json = protectedStore.GetProtectedText(reference, ExtensionTool.ProposalPurpose(context.WorkspaceRoot, proposalId));
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new ToolPreflightException(ToolErrorCode.LimitExceeded, "Extension proposal exceeds the safe size limit.");
        var proposal = JsonSerializer.Deserialize(json, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal)
            ?? throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension proposal is invalid.");
        var extensionOrigin = proposal.Origin == "extension" && proposal.Isolation == ProcessIsolation.PrivateExtensionWorkspace.ToString();
        var processOrigin = proposal.Origin == "process" && proposal.Isolation == ProcessIsolation.PrivateProcessWorkspace.ToString()
            && proposal.ProcessHandleId is { Length: 32 } processHandle && Guid.TryParseExact(processHandle, "N", out _);
        if (proposal.Version != 1 || proposal.ProposalId != proposalId || proposal.ToolId.Length == 0
            || !extensionOrigin && !processOrigin
            || !PathEquals(proposal.WorkspaceRoot, context.WorkspaceRoot)
            || string.IsNullOrWhiteSpace(context.MutationRunId) || string.IsNullOrWhiteSpace(context.MutationTurnId)
            || string.IsNullOrWhiteSpace(context.MutationLaneId)
            || proposal.MutationRunId != context.MutationRunId
            || proposal.MutationLaneId != context.MutationLaneId
            || processOrigin && (!PersistentProcessSessionManager.IsProcessWriterTool(proposal.ToolId)
                || proposal.ProcessHandleId is null)
            || !Guid.TryParse(proposal.ToolCallId, out _)
            || proposal.Changes is null || proposal.Changes.Count is < 1 or > 64)
            throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension proposal identity is invalid.");
        var seen = new HashSet<string>(PathComparer);
        foreach (var change in proposal.Changes)
        {
            if (!seen.Add(change.Path) || _redaction.IsSecretPath(change.Path)
                || Hash(change.AfterBytes) != change.AfterSha256)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension proposal paths or hashes are invalid.");
        }
        if (!_extensionValidators.TryGetValue(proposal.ToolId, out var validator)
            || !validator(proposal, context.WorkspaceRoot))
            throw new ToolPreflightException(ToolErrorCode.PermissionDenied,
                "The originating extension package or consent is no longer current.");
        return proposal;
    }

    private static string ResolveExtensionTarget(string workspace, string relative)
    {
        var paths = new PathBoundaryValidator();
        if (!paths.IsSafeRelative(relative) || new RedactionPolicy().IsSecretPath(relative))
            throw new ToolPreflightException(ToolErrorCode.CapabilityRefused, "Extension target path is unsafe.");
        var full = Path.GetFullPath(relative, workspace);
        if (!paths.IsWithin(full, workspace)) throw new ToolPreflightException(ToolErrorCode.CapabilityRefused, "Extension target escapes workspace.");
        VerifyExtensionTopology(workspace, full);
        var parent = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(parent)) throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Extension batch cannot create directories.");
        return full;
    }

    private static void VerifyExtensionTopology(string workspace, string full)
    {
        var target = Path.GetFullPath(full);
        var root = Path.GetFullPath(workspace);
        for (var current = Path.GetDirectoryName(target); current is not null && IsWithinOrEqual(root, current);
            current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("Extension target topology is unsafe.");
            if (PathEquals(current, root)) break;
        }
        if (File.Exists(target) || Directory.Exists(target))
        {
            var attributes = File.GetAttributes(target);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Extension target topology is unsafe.");
        }
    }

    private static string WriteExtensionMetadata(ToolCallId toolCallId, string proposalId, string workspace,
        ArtifactRef reference, IReadOnlyList<ExtensionPreparedFile> files)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("kind", "extension.write.batch"); writer.WriteNumber("version", 1);
            writer.WriteString("toolCallId", toolCallId.ToString()); writer.WriteString("proposalId", proposalId);
            writer.WriteString("workspaceRoot", workspace); writer.WriteString("artifactHash", reference.Hash.Value);
            writer.WriteNumber("artifactSize", reference.Size); writer.WriteStartArray("files");
            foreach (var file in files)
            {
                writer.WriteStartObject(); writer.WriteString("path", file.RelativePath);
                if (file.BeforeHash is null) writer.WriteNull("beforeSha256"); else writer.WriteString("beforeSha256", file.BeforeHash);
                if (file.AfterHash is null) writer.WriteNull("afterSha256"); else writer.WriteString("afterSha256", file.AfterHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static void WriteExtensionAtomically(string path, byte[] bytes, bool expectedAbsent)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".omnicore-ext-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite: !expectedAbsent);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private static void CompleteExtensionHistory(ToolExecutionContext context, ToolIntent intent,
        IReadOnlyList<ExtensionPreparedFile> files, CancellationToken token)
    {
        var store = context.MutationHistory!;
        var receipts = new List<WorkspaceMutationReceipt>();
        var states = new List<WorkspaceFileHistoryEntry>();
        foreach (var file in files)
        {
            var prepared = store.ReadReceipt(context.WorkspaceRoot, intent.ToolCallId, file.RelativePath)
                ?? throw new InvalidDataException("Prepared extension receipt is missing.");
            var old = store.ReadFile(context.WorkspaceRoot, file.RelativePath);
            receipts.Add(prepared with { State = WorkspaceMutationReceiptState.Applied });
            states.Add(file.After is null
                ? new WorkspaceFileHistoryEntry(prepared.WorkspaceId, file.RelativePath, WorkspaceFileHistoryState.Tombstone,
                    file.BeforeHash, prepared.PreimageRef ?? old?.ImageRef, checked((old?.Revision ?? 0) + 1), intent.ToolCallId)
                : new WorkspaceFileHistoryEntry(prepared.WorkspaceId, file.RelativePath, WorkspaceFileHistoryState.Present,
                    file.AfterHash, prepared.PostimageRef, checked((old?.Revision ?? 0) + 1), intent.ToolCallId));
        }
        store.Complete(receipts, states, token);
    }


    internal FilesystemReconciliation ReconcileExtensionBatch(string workspaceRoot, string json,
        ArtifactRef? beforeStateRef, CancellationToken cancellationToken)
    {
        try
        {
            if (beforeStateRef is null || !_artifacts.Verify(beforeStateRef.Hash, beforeStateRef.Size))
                return FilesystemReconciliation.Unresolvable("protected extension proposal unavailable");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() != "extension.write.batch"
                || root.GetProperty("version").GetInt32() != 1
                || root.GetProperty("artifactHash").GetString() != beforeStateRef.Hash.Value
                || root.GetProperty("artifactSize").GetInt64() != beforeStateRef.Size
                || !PathEquals(Path.GetFullPath(workspaceRoot), root.GetProperty("workspaceRoot").GetString()!))
                return FilesystemReconciliation.Unresolvable("extension recovery metadata identity mismatch");
            var proposalId = root.GetProperty("proposalId").GetString()!;
            var store = _artifacts as IProtectedArtifactStore;
            if (store is null) return FilesystemReconciliation.Unresolvable("protected extension storage unavailable");
            var proposalJson = store.GetProtectedText(beforeStateRef,
                ExtensionTool.ProposalPurpose(workspaceRoot, proposalId));
            var proposal = JsonSerializer.Deserialize(proposalJson,
                ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal);
            if (proposal is null || proposal.ProposalId != proposalId || proposal.Changes.Count is < 1 or > 64
                || proposal.ToolCallId != root.GetProperty("toolCallId").GetString()
                || !PathEquals(proposal.WorkspaceRoot, workspaceRoot)
                || proposal.MutationRunId.Length == 0 || proposal.MutationTurnId.Length == 0)
                return FilesystemReconciliation.Unresolvable("extension proposal invalid");
            var metadataFiles = root.GetProperty("files").EnumerateArray().Select(item =>
                (Path: item.GetProperty("path").GetString()!,
                    Before: item.GetProperty("beforeSha256").ValueKind == JsonValueKind.Null ? null : item.GetProperty("beforeSha256").GetString(),
                    After: item.GetProperty("afterSha256").ValueKind == JsonValueKind.Null ? null : item.GetProperty("afterSha256").GetString()))
                .ToArray();
            if (!metadataFiles.SequenceEqual(proposal.Changes.Select(change =>
                    (change.Path, change.BeforeSha256, change.AfterSha256))))
                return FilesystemReconciliation.Unresolvable("extension recovery file list mismatch");
            var applied = 0; var unchanged = 0;
            foreach (var change in proposal.Changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveExtensionTarget(Path.GetFullPath(workspaceRoot), change.Path);
                var current = Hash(File.Exists(path) ? File.ReadAllBytes(path) : null);
                if (current == change.AfterSha256) applied++;
                else if (current == change.BeforeSha256) unchanged++;
                else return FilesystemReconciliation.Conflict("extension batch has a mixed or unexpected file state");
            }
            if (applied == proposal.Changes.Count) return FilesystemReconciliation.Applied("every extension batch target matches its postimage");
            if (unchanged == proposal.Changes.Count) return FilesystemReconciliation.NotApplied("every extension batch target matches its preimage");
            return FilesystemReconciliation.Conflict("extension batch has a mixed file state");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { return FilesystemReconciliation.Unresolvable("extension batch recovery failed closed"); }
    }    private static string? Hash(byte[]? bytes) => bytes is null ? null
        : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ExtensionPreparedBatch(ExtensionWriteBatchProposal Proposal, ArtifactRef Artifact,
        IReadOnlyList<ExtensionPreparedFile> Files);
    private sealed record ExtensionPreparedFile(string RelativePath, string FullPath, byte[]? Before, byte[]? After,
        string? BeforeHash, string? AfterHash, byte[]? LogicalBefore, string? LogicalBeforeHash,
        ModelToolCapability LogicalOperation);

}
