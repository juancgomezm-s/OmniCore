namespace OmniCore.Tools;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Multi-file, version-bound text patch. Preview is read-only; apply is one authorized,
/// journaled batch. Every target is checked and budgeted before the first byte is written.
/// Partial publication is reported Unknown and is reconciled as a whole batch, never replayed.
/// </summary>
public sealed class FilesystemApplyPatchTool : ITool, IEffectiveToolAuthorizationIntents,
    ITrustedFilesystemBatchTool
{
    public const string Id = "filesystem.apply_patch";
    public const string MetadataKind = "filesystem.apply_patch.batch";
    public const string ManifestMediaType = "application/vnd.omnicore.filesystem-apply-patch+json";
    private const int MaxFiles = 64;
    private const int MaxHunksPerFile = 32;
    private const int MaxTotalHunks = 128;
    // Keep every published file inside WorkspaceSnapshotStore's independently verified Undo bounds.
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxTotalBytes = 8 * 1024 * 1024;
    private const int MaxPreviewChars = 128 * 1024;

    private readonly IPathBoundaryValidator _boundary;
    private readonly ConcurrentDictionary<string, PreparedBatch> _prepared = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _authorizationFingerprints = new(StringComparer.Ordinal);
    private readonly ToolDescriptor _descriptor;

    /// <summary>Fault seam for deterministic partial-publication/recovery tests.</summary>
    internal Action<int, string>? TestBeforePublish;
    internal Action<int, string>? TestAfterPublish;

    public FilesystemApplyPatchTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(new ToolId(Id),
            "Preview or apply a bounded batch of localized edits to existing files. Each file requires its current SHA-256 version token; apply authorizes every target and reconciles the batch after interruption.",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"preview\",\"apply\"]},\"previewToken\":{\"type\":\"string\",\"minLength\":64,\"maxLength\":64},\"files\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":64,\"items\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"minLength\":1},\"expectedVersion\":{\"type\":\"string\",\"minLength\":64,\"maxLength\":64},\"hunks\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{\"oldText\":{\"type\":\"string\",\"minLength\":1},\"newText\":{\"type\":\"string\",\"minLength\":1}},\"required\":[\"oldText\",\"newText\"],\"additionalProperties\":false}}},\"required\":[\"path\",\"expectedVersion\",\"hunks\"],\"additionalProperties\":false}}},\"required\":[\"mode\",\"files\"],\"additionalProperties\":false}"),
            ["filesystem", "patch", "batch", "write"], false, true, ToolRisk.High,
            ComponentSource.Core(), ToolProtection.Protected, EffectClass.Reconcilable);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!TryParse(call.NormalizedArgumentsJson, out var request))
            return Rejected("La propuesta apply_patch no tiene un formato válido.", ToolErrorCode.InvalidArguments);

        var reads = new List<string>(request.Files.Count);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        try
        {
            foreach (var file in request.Files)
            {
                if (!_boundary.IsSafeRelative(file.Path) || IsGitMetadataPath(file.Path) || !seen.Add(file.Path))
                    return Rejected("La propuesta contiene rutas inseguras o repetidas.", ToolErrorCode.InvalidArguments);
                reads.Add(Path.GetFullPath(file.Path, context.WorkspaceRoot));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { return Rejected("No se pudo resolver una ruta de la propuesta.", ToolErrorCode.InvalidArguments); }

        var claims = new ResourceClaims(reads, [], [], null, []);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            request.Mode == ApplyPatchMode.Apply ? EffectClass.Reconcilable : EffectClass.None,
            claims, ToolRisk.High, null);
        return new Prepared(intent);
    }

    public IReadOnlyList<ToolIntent> ResolveEffectiveAuthorizationIntents(ToolIntent intent,
        ToolPreparationContext preparation, ToolExecutionContext execution, CancellationToken cancellationToken)
    {
        if (!TryParse(intent.NormalizedArgumentsJson, out var request) || request.Mode != ApplyPatchMode.Apply)
            return [];

        var plans = BuildPlans(request, execution, requireVisibleRead: true, cancellationToken);
        EnsurePreviewToken(request, execution.WorkspaceRoot, plans);
        var proposals = plans.Select(plan => plan.Budget).ToArray();
        if (execution.ReadRegistry is not null
            && execution.ReadRegistry.Ledger.RefuseMutationBatch(proposals) is { } refusal)
            throw new ToolPreflightException(refusal.Code, refusal.Message);

        var intents = plans.Select(plan => new ToolIntent(intent.ToolCallId, new ToolId("filesystem.patch"),
            intent.NormalizedArgumentsJson, EffectClass.Reconcilable,
            new ResourceClaims([plan.FullPath], [plan.FullPath], [], null, []), ToolRisk.Medium, null,
            ModelToolCapability.PatchExisting, effectiveMutationHistoryRevision: plan.History?.Revision,
            effectiveMutationProposal: plan.Budget)).ToArray();
        // Denied/Ask calls must not retain megabytes of pre/postimages in a long-lived catalog.
        // Only an exact target/image fingerprint survives permission evaluation.
        if (_authorizationFingerprints.Count >= 512 && !_authorizationFingerprints.ContainsKey(intent.ToolCallId.ToString()))
            throw new ToolPreflightException(ToolErrorCode.LimitExceeded, "El límite de propuestas pendientes se alcanzó; inicia un nuevo turno.");
        _authorizationFingerprints[intent.ToolCallId.ToString()] = AuthorizationFingerprint(execution.WorkspaceRoot, plans);
        return intents;
    }

    public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryParse(intent.Intent.NormalizedArgumentsJson, out var request)
            || request.Mode != ApplyPatchMode.Apply) return null;
        var plans = BuildPlans(request, context, requireVisibleRead: true, cancellationToken);
        EnsurePreviewToken(request, context.WorkspaceRoot, plans);
        if (context.ReadRegistry is not null
            && context.ReadRegistry.Ledger.RefuseMutationBatch(plans.Select(plan => plan.Budget).ToArray()) is { } refusal)
            throw new ToolPreflightException(refusal.Code, refusal.Message);

        if (!_authorizationFingerprints.TryRemove(intent.Intent.ToolCallId.ToString(), out var authorized)
            || authorized != AuthorizationFingerprint(context.WorkspaceRoot, plans))
            throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                "La propuesta cambió después de calcular las autorizaciones; reléela y prepara un nuevo preview.");

        var store = context.MutationHistory ?? throw new ToolPreflightException(ToolErrorCode.ToolFailure,
            "El historial durable de mutaciones no está disponible para el lote.");
        if (context.Artifacts is null || string.IsNullOrWhiteSpace(context.MutationRunId)
            || string.IsNullOrWhiteSpace(context.MutationTurnId))
            throw new ToolPreflightException(ToolErrorCode.ToolFailure,
                "No se puede conservar evidencia durable del lote.");

        var receipts = new List<WorkspaceMutationReceipt>(plans.Count);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prior = store.ReadFile(context.WorkspaceRoot, plan.RelativePath);
            if (prior is { State: not WorkspaceFileHistoryState.Present }
                || prior is { State: WorkspaceFileHistoryState.Present, ExpectedHash: { } known }
                    && known != plan.BeforeHash)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                    "La ruta tiene un historial durable pendiente o distinto; resuélvelo antes de aplicar el lote.");
            var preimage = prior?.ImageRef is { } image && prior.ExpectedHash == plan.BeforeHash
                ? image : FilesystemPreimage.CaptureHistoryImage(context.Artifacts, plan.BeforeBytes);
            var postimage = FilesystemPreimage.CaptureHistoryImage(context.Artifacts, plan.AfterBytes, postimage: true);
            receipts.Add(new WorkspaceMutationReceipt(store.GetWorkspaceId(context.WorkspaceRoot), plan.RelativePath,
                intent.Intent.ToolCallId, context.MutationRunId, context.MutationTurnId,
                ModelToolCapability.PatchExisting.ToString(), plan.BeforeHash, plan.AfterHash, preimage, postimage,
                plan.DeletedLines, plan.InsertedLines, WorkspaceMutationReceiptState.Prepared));
        }
        store.Prepare(receipts, cancellationToken);

        var metadata = ApplyPatchMetadata.Encode(intent.Intent.ToolCallId, Path.GetFullPath(context.WorkspaceRoot),
            receipts.Select(receipt => (receipt.RelativePath, receipt.ExpectedPreHash!, receipt.ExpectedPostHash!,
                receipt.PreimageRef!, receipt.PostimageRef!)).ToArray());
        var artifact = context.Artifacts.PutText(metadata, ManifestMediaType, ArtifactKind.Patch, Sensitivity.Sensitive);
        if (artifact.Redacted || artifact.MediaType != ManifestMediaType || artifact.Kind != ArtifactKind.Patch
            || artifact.Sensitivity != Sensitivity.Sensitive || artifact.Size != Encoding.UTF8.GetByteCount(metadata)
            || !context.Artifacts.Verify(artifact.Hash, artifact.Size)
            || !string.Equals(context.Artifacts.GetText(artifact.Hash), metadata, StringComparison.Ordinal))
            throw new ToolPreflightException(ToolErrorCode.ToolFailure,
                "No se pudo verificar el manifiesto durable del lote.");
        var prepared = new PreparedBatch(request, plans, artifact, metadata);
        _prepared[intent.Intent.ToolCallId.ToString()] = prepared;
        return new ReconciliationSpec(null, null, intent.Intent.ToolCallId.ToString())
        {
            BeforeStateRef = artifact,
            Reversibility = Reversibility.Reversible,
            DurableMetadataJson = metadata,
        };
    }

    public bool ValidateFilesystemBatchEnvelope(ToolIntent intent, IReadOnlyList<ToolIntent> effectiveIntents,
        ToolExecutionContext context, ReconciliationSpec reconciliation)
    {
        try
        {
            if (reconciliation.BeforeStateRef is not { } artifact || context.Artifacts is null
                || artifact.Redacted || artifact.MediaType != ManifestMediaType || artifact.Kind != ArtifactKind.Patch
                || artifact.Sensitivity != Sensitivity.Sensitive || !context.Artifacts.Verify(artifact.Hash, artifact.Size)
                || reconciliation.DurableMetadataJson is not { Length: > 0 } json
                || !string.Equals(context.Artifacts.GetText(artifact.Hash), json, StringComparison.Ordinal)
                || !ApplyPatchMetadata.TryParse(json, out var metadata)
                || metadata.ToolCallId != intent.ToolCallId.ToString()
                || !PathEquals(metadata.WorkspaceRoot, Path.GetFullPath(context.WorkspaceRoot))) return false;
            var authorized = effectiveIntents.SelectMany(effective => effective.Claims.Writes).Select(path =>
                Path.GetRelativePath(context.WorkspaceRoot, Path.GetFullPath(path, context.WorkspaceRoot))
                    .Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(PathComparer);
            var manifest = metadata.Files.Select(file => file.Path).ToHashSet(PathComparer);
            return metadata.Files.Count is > 0 and <= MaxFiles && manifest.Count == metadata.Files.Count
                && manifest.SetEquals(authorized)
                && effectiveIntents.Count == metadata.Files.Count;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException
            or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return false; }
    }

    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
        if (!TryParse(intent.Intent.NormalizedArgumentsJson, out var request))
            return ToolResult.Error(ToolErrorCode.InvalidArguments, "La propuesta apply_patch no tiene un formato válido.");
        if (request.Mode == ApplyPatchMode.Preview)
        {
            var previewPlans = BuildPlans(request, context, requireVisibleRead: false, cancellationToken);
            var preview = BuildPreview(previewPlans);
            if (preview.Length > MaxPreviewChars)
                return ToolResult.Error(ToolErrorCode.LimitExceeded, "El preview supera el límite de salida; divide el lote.");
            var token = CreatePreviewToken(context.WorkspaceRoot, previewPlans);
            using var previewBuffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(previewBuffer))
            {
                writer.WriteStartObject(); writer.WriteString("previewToken", token); writer.WriteString("diff", preview);
                writer.WriteEndObject();
            }
            var previewDocument = Encoding.UTF8.GetString(previewBuffer.ToArray());
            return new ToolResult("Preview listo; no se modificó ningún archivo. Conserva previewToken y envíalo junto con las mismas rutas, versiones y hunks para aplicar.", previewDocument, null,
                previewPlans.Sum(plan => (long)plan.BeforeBytes.Length), false, EffectOutcome.None);
        }

        var key = intent.Intent.ToolCallId.ToString();
        if (!_prepared.TryGetValue(key, out var batch) || batch.Manifest is null || batch.ManifestArtifact is null)
            return ToolResult.Error(ToolErrorCode.ToolFailure,
                "No existe un manifiesto durable validado para esta ejecución apply_patch.");

        IReadOnlyList<FilePlan> current;
        try
        {
            current = BuildPlans(request, context, requireVisibleRead: true, cancellationToken);
            EnsurePreviewToken(request, context.WorkspaceRoot, current);
        }
        catch (ToolPreflightException ex)
        { return ToolResult.Error(ex.ErrorCode, ex.Message); }
        if (!SameTargetsAndImages(batch.Files, current))
            return ToolResult.Error(ToolErrorCode.StaleWrite,
                "Un archivo cambió después del preview/autorización; no se aplicó ningún archivo.");

        var written = 0;
        try
        {
            for (var index = 0; index < batch.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = batch.Files[index];
                TestBeforePublish?.Invoke(index, plan.FullPath);
                PublishOne(plan, context.WorkspaceRoot, cancellationToken);
                written++;
                TestAfterPublish?.Invoke(index, plan.FullPath);
            }

            CompleteHistory(batch, intent.Intent, context, cancellationToken);
            foreach (var plan in batch.Files)
                context.ReadRegistry?.Ledger.RecordMutation(plan.RelativePath, plan.DeletedLines,
                    plan.InsertedLines, intent.Intent.ToolCallId, ModelToolCapability.PatchExisting,
                    FileVersion.CountLines(plan.BeforeText), context.EmitEvent);
            return new ToolResult("Lote apply_patch aplicado: " + batch.Files.Count + " archivos.",
                BuildPreview(batch.Files), batch.ManifestArtifact, batch.Files.Sum(plan => (long)plan.AfterBytes.Length),
                false, EffectOutcome.Applied);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or ToolPreflightException)
        {
            var effect = written == 0 && ex is not PartialPublicationException
                ? EffectOutcome.None : EffectOutcome.Unknown;
            return new ToolResult(effect == EffectOutcome.Unknown
                    ? "El lote se interrumpió después de publicar al menos un archivo; resultado incierto, no lo reintentes. Reconciliación requerida."
                    : "No se pudo publicar el lote; ningún archivo fue modificado.",
                null, batch.ManifestArtifact, written, false, effect, true,
                effect == EffectOutcome.Unknown ? ToolErrorCode.UnknownEffect : ex is ToolPreflightException preflight
                    ? preflight.ErrorCode : ToolErrorCode.ToolFailure);
        }
        finally
        {
            _prepared.TryRemove(key, out _);
        }
    }

    internal static FilesystemReconciliation Reconcile(string workspaceRoot, string json,
        IPathBoundaryValidator boundary, CancellationToken cancellationToken)
    {
        if (!ApplyPatchMetadata.TryParse(json, out var metadata)
            || !PathEquals(metadata.WorkspaceRoot, Path.GetFullPath(workspaceRoot)))
            return FilesystemReconciliation.Unresolvable("apply_patch recovery metadata invalid");
        try
        {
            var applied = 0;
            var unchanged = 0;
            foreach (var file in metadata.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(file.Path, workspaceRoot);
                if (!boundary.IsWithin(full, workspaceRoot) || SecretPathGuard.IsSecretTarget(boundary, full, workspaceRoot))
                    return FilesystemReconciliation.Unresolvable("apply_patch recovery target outside workspace");
                if (!File.Exists(full)) return FilesystemReconciliation.Conflict("apply_patch target disappeared");
                var current = FileVersion.VersionTokenForFile(full);
                if (current == file.AfterHash) applied++;
                else if (current == file.BeforeHash) unchanged++;
                else return FilesystemReconciliation.Conflict("apply_patch target differs from its preimage and postimage");
            }
            if (applied == metadata.Files.Count) return FilesystemReconciliation.Applied("every apply_patch target matches its postimage");
            if (unchanged == metadata.Files.Count) return FilesystemReconciliation.NotApplied("every apply_patch target matches its preimage");
            return FilesystemReconciliation.Conflict("apply_patch batch is partially applied; preserve current files and inspect individually");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or InvalidDataException)
        { return FilesystemReconciliation.Unresolvable("apply_patch recovery failed closed"); }
    }

    private IReadOnlyList<FilePlan> BuildPlans(ApplyPatchRequest request, ToolExecutionContext context,
        bool requireVisibleRead, CancellationToken cancellationToken)
    {
        var plans = new List<FilePlan>(request.Files.Count);
        long totalBytes = 0;
        foreach (var file in request.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_boundary.IsSafeRelative(file.Path) || IsGitMetadataPath(file.Path))
                throw new ToolPreflightException(ToolErrorCode.InvalidArguments, "Ruta relativa no válida: " + file.Path);
            var full = Path.GetFullPath(file.Path, context.WorkspaceRoot);
            if (!_boundary.IsWithin(full, context.WorkspaceRoot)
                || SecretPathGuard.IsSecretTarget(_boundary, full, context.WorkspaceRoot))
                throw new ToolPreflightException(ToolErrorCode.PermissionDenied, "Ruta fuera del workspace o protegida: " + file.Path);
            if (!File.Exists(full))
                throw new ToolPreflightException(ToolErrorCode.StaleWrite, "El archivo debe existir para aplicar un patch: " + file.Path);

            if (requireVisibleRead && context.ReadRegistry is not null
                && (context.ReadRegistry.Ledger.MutationPolicy?.RequirePriorRead ?? true)
                && !context.ReadRegistry.Matches(file.Path, file.ExpectedVersion))
            {
                context.ReadRegistry.Ledger.RecordTokenViolation(file.Path, ModelToolCapability.PatchExisting);
                throw new ToolPreflightException(ToolErrorCode.PriorReadRequired,
                    "PRIOR_READ_REQUIRED: lee " + file.Path + " y usa el token de esa versión.");
            }

            var before = File.ReadAllBytes(full);
            totalBytes = checked(totalBytes + before.LongLength);
            if (before.Length > MaxFileBytes || totalBytes > MaxTotalBytes)
                throw new ToolPreflightException(ToolErrorCode.LimitExceeded, "El lote supera el límite de bytes.");
            var beforeHash = FileVersion.VersionToken(before);
            if (!string.Equals(file.ExpectedVersion, beforeHash, StringComparison.Ordinal))
                throw new ToolPreflightException(ToolErrorCode.StaleWrite, FileVersion.StaleWriteMessage(beforeHash));
            var decoded = FileVersion.Decode(before);
            var ranges = new List<(int Start, int End, string OldText, string NewText)>(file.Hunks.Count);
            foreach (var hunk in file.Hunks)
            {
                var start = decoded.Text.IndexOf(hunk.OldText, StringComparison.Ordinal);
                if (start < 0 || decoded.Text.IndexOf(hunk.OldText, start + hunk.OldText.Length,
                        StringComparison.Ordinal) >= 0)
                    throw new ToolPreflightException(ToolErrorCode.InvalidArguments,
                        "Cada oldText debe aparecer exactamente una vez en " + file.Path + ".");
                if (hunk.OldText == decoded.Text || hunk.NewText.Length == 0 || hunk.OldText == hunk.NewText)
                    throw new ToolPreflightException(ToolErrorCode.InvalidArguments,
                        "Los hunks deben ser localizados y producir un cambio no vacío.");
                var end = checked(start + hunk.OldText.Length);
                if (ranges.Any(existing => start < existing.End && end > existing.Start))
                    throw new ToolPreflightException(ToolErrorCode.InvalidArguments,
                        "Los hunks de " + file.Path + " se solapan.");
                if (requireVisibleRead && context.ReadRegistry is not null
                    && (context.ReadRegistry.Ledger.MutationPolicy?.RequirePriorRead ?? true)
                    && !context.ReadRegistry.HasVisibleCoverage(file.Path, file.ExpectedVersion, start, hunk.OldText.Length))
                {
                    context.ReadRegistry.Ledger.RecordTokenViolation(file.Path, ModelToolCapability.PatchExisting);
                    throw new ToolPreflightException(ToolErrorCode.PriorReadRequired,
                        "PRIOR_READ_REQUIRED: el texto editado no está cubierto por la lectura visible de " + file.Path + ".");
                }
                ranges.Add((start, end, hunk.OldText, hunk.NewText));
            }
            var updated = decoded.Text;
            foreach (var range in ranges.OrderByDescending(item => item.Start))
                updated = updated.Remove(range.Start, range.End - range.Start).Insert(range.Start, range.NewText);
            if (updated == decoded.Text)
                throw new ToolPreflightException(ToolErrorCode.InvalidArguments, "El lote no produce cambios.");
            var after = FileVersion.Encode(updated, decoded.Encoding);
            totalBytes = checked(totalBytes + after.LongLength);
            if (after.Length > MaxFileBytes || totalBytes > MaxTotalBytes)
                throw new ToolPreflightException(ToolErrorCode.LimitExceeded, "El lote supera el límite de bytes.");
            FileVersion.ChangedLines(decoded.Text, updated, out var deleted, out var inserted);
            var relative = context.MutationHistory?.NormalizeRelativePath(context.WorkspaceRoot, file.Path)
                ?? file.Path.Replace('\\', '/');
            var history = context.MutationHistory?.ReadFile(context.WorkspaceRoot, relative);
            if (history is { State: not WorkspaceFileHistoryState.Present }
                || history is { State: WorkspaceFileHistoryState.Present, ExpectedHash: { } known } && known != beforeHash)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                    "La ruta tiene estado durable distinto; vuelve a leer antes de editar " + file.Path + ".");
            var budget = new MutationProposal(relative, ModelToolCapability.PatchExisting, true,
                deleted, inserted, FileVersion.CountLines(decoded.Text));
            plans.Add(new FilePlan(relative, full, before, after, beforeHash, FileVersion.VersionToken(after),
                decoded.Text, updated, deleted, inserted, history, budget));
        }
        return plans;
    }

    private void PublishOne(FilePlan plan, string workspaceRoot, CancellationToken cancellationToken)
    {
        if (!_boundary.IsWithin(plan.FullPath, workspaceRoot)
            || SecretPathGuard.IsSecretTarget(_boundary, plan.FullPath, workspaceRoot))
            throw new ToolPreflightException(ToolErrorCode.PermissionDenied, "La ruta cambió fuera del ámbito autorizado.");
        using (FilePublishLock.Acquire(plan.FullPath, cancellationToken))
        using (var stream = new FileStream(plan.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.WriteThrough))
        {
            if (!_boundary.IsWithin(plan.FullPath, workspaceRoot))
                throw new ToolPreflightException(ToolErrorCode.PermissionDenied, "La ruta cambió al abrir el archivo.");
            using var snapshot = new MemoryStream();
            stream.CopyTo(snapshot);
            var current = snapshot.ToArray();
            if (FileVersion.VersionToken(current) != plan.BeforeHash)
                throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                    FileVersion.StaleWriteMessage(FileVersion.VersionToken(current)));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                stream.Position = 0;
                stream.Write(plan.AfterBytes);
                stream.SetLength(plan.AfterBytes.Length);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                try
                {
                    stream.Position = 0;
                    stream.Write(plan.BeforeBytes);
                    stream.SetLength(plan.BeforeBytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                catch { throw new PartialPublicationException("apply_patch and restoration failed; durable preimage retained."); }
                throw;
            }
        }
        try
        {
            if (FileVersion.VersionTokenForFile(plan.FullPath) != plan.AfterHash)
                throw new PartialPublicationException("apply_patch postimage verification failed.");
        }
        catch (PartialPublicationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new PartialPublicationException("apply_patch postimage could not be verified.", ex); }
    }

    private static void CompleteHistory(PreparedBatch batch, ToolIntent intent,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var store = context.MutationHistory!;
        var receipts = new List<WorkspaceMutationReceipt>(batch.Files.Count);
        var states = new List<WorkspaceFileHistoryEntry>(batch.Files.Count);
        foreach (var file in batch.Files)
        {
            var receipt = store.ReadReceipt(context.WorkspaceRoot, intent.ToolCallId, file.RelativePath)
                ?? throw new InvalidDataException("Prepared apply_patch receipt is missing.");
            var previous = store.ReadFile(context.WorkspaceRoot, file.RelativePath);
            receipts.Add(receipt with { State = WorkspaceMutationReceiptState.Applied });
            states.Add(new WorkspaceFileHistoryEntry(receipt.WorkspaceId, file.RelativePath,
                WorkspaceFileHistoryState.Present, file.AfterHash, receipt.PostimageRef,
                checked((previous?.Revision ?? 0) + 1), intent.ToolCallId));
        }
        store.Complete(receipts, states, cancellationToken);
    }

    private static bool SameTargetsAndImages(IReadOnlyList<FilePlan> first, IReadOnlyList<FilePlan> second) =>
        first.Count == second.Count && first.Zip(second).All(pair =>
            PathEquals(pair.First.RelativePath, pair.Second.RelativePath)
            && pair.First.BeforeHash == pair.Second.BeforeHash && pair.First.AfterHash == pair.Second.AfterHash);

    private static string BuildPreview(IReadOnlyList<FilePlan> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            builder.Append("--- ").Append(file.RelativePath).Append("\n+++ ").Append(file.RelativePath).Append('\n');
            foreach (var hunk in FileVersion.CreateEditHunks(file.BeforeText, file.AfterText))
                builder.Append("@@\n-").Append(hunk.OldText).Append("\n+").Append(hunk.NewText).Append('\n');
            if (builder.Length > MaxPreviewChars) break;
        }
        return builder.ToString();
    }

    private static string CreatePreviewToken(string workspaceRoot, IReadOnlyList<FilePlan> files)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("workspaceRoot", Path.GetFullPath(workspaceRoot));
            writer.WriteStartArray("files");
            foreach (var file in files)
            {
                writer.WriteStartObject(); writer.WriteString("path", file.RelativePath);
                writer.WriteString("beforeSha256", file.BeforeHash); writer.WriteString("afterSha256", file.AfterHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static string AuthorizationFingerprint(string workspaceRoot, IReadOnlyList<FilePlan> files)
    {
        var revisions = string.Join('|', files.Select(file => file.History?.Revision
            .ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CreatePreviewToken(workspaceRoot, files) + ":" + revisions)));
    }

    private static void EnsurePreviewToken(ApplyPatchRequest request, string workspaceRoot,
        IReadOnlyList<FilePlan> plans)
    {
        if (request.Mode == ApplyPatchMode.Apply
            && !string.Equals(request.PreviewToken, CreatePreviewToken(workspaceRoot, plans), StringComparison.Ordinal))
            throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                "El previewToken no coincide con la versión y el resultado de este lote; vuelve a generar el preview.");
    }

    private static bool TryParse(string json, out ApplyPatchRequest request)
    {
        request = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("mode", out var modeElement) || modeElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array
                || filesElement.GetArrayLength() is < 1 or > MaxFiles
                ) return false;
            var mode = modeElement.GetString() switch
            {
                "preview" => ApplyPatchMode.Preview,
                "apply" => ApplyPatchMode.Apply,
                _ => (ApplyPatchMode?)null,
            };
            if (mode is null || root.EnumerateObject().Count() != (mode == ApplyPatchMode.Apply ? 3 : 2)) return false;
            string? previewToken = null;
            if (mode == ApplyPatchMode.Apply)
            {
                if (!root.TryGetProperty("previewToken", out var tokenElement)
                    || tokenElement.ValueKind != JsonValueKind.String || !IsHash(tokenElement.GetString()!)) return false;
                previewToken = tokenElement.GetString()!.ToLowerInvariant();
            }
            var files = new List<FileRequest>();
            var paths = new HashSet<string>(PathComparer);
            var hunkCount = 0;
            long argumentBytes = 0;
            foreach (var item in filesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 3
                    || !item.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("expectedVersion", out var versionElement) || versionElement.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("hunks", out var hunksElement) || hunksElement.ValueKind != JsonValueKind.Array
                    || hunksElement.GetArrayLength() is < 1 or > MaxHunksPerFile) return false;
                var path = pathElement.GetString()!;
                var version = versionElement.GetString()!;
                if (string.IsNullOrWhiteSpace(path) || !paths.Add(path) || !IsHash(version)) return false;
                var hunks = new List<HunkRequest>();
                foreach (var hunk in hunksElement.EnumerateArray())
                {
                    if (hunk.ValueKind != JsonValueKind.Object || hunk.EnumerateObject().Count() != 2
                        || !hunk.TryGetProperty("oldText", out var oldElement) || oldElement.ValueKind != JsonValueKind.String
                        || !hunk.TryGetProperty("newText", out var newElement) || newElement.ValueKind != JsonValueKind.String)
                        return false;
                    var oldText = oldElement.GetString()!;
                    var newText = newElement.GetString()!;
                    if (oldText.Length is 0 or > MaxFileBytes || newText.Length is 0 or > MaxFileBytes) return false;
                    argumentBytes = checked(argumentBytes + Encoding.UTF8.GetByteCount(oldText) + Encoding.UTF8.GetByteCount(newText));
                    if (argumentBytes > MaxTotalBytes) return false;
                    hunks.Add(new HunkRequest(oldText, newText));
                }
                hunkCount += hunks.Count;
                if (hunkCount > MaxTotalHunks) return false;
                files.Add(new FileRequest(path, version, hunks));
            }
            request = new ApplyPatchRequest(mode.Value, previewToken, files);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException or ArgumentException)
        { return false; }
    }

    private static PreparationRejected Rejected(string message, ToolErrorCode code) => new(message, null, code);
    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsGitMetadataPath(string value) => value.Replace('\\', '/').Split('/')[0]
        .Equals(".git", StringComparison.OrdinalIgnoreCase);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool PathEquals(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record ApplyPatchRequest(ApplyPatchMode Mode, string? PreviewToken, IReadOnlyList<FileRequest> Files);
    private sealed record FileRequest(string Path, string ExpectedVersion, IReadOnlyList<HunkRequest> Hunks);
    private sealed record HunkRequest(string OldText, string NewText);
    private enum ApplyPatchMode { Preview, Apply }
    internal sealed record FilePlan(string RelativePath, string FullPath, byte[] BeforeBytes, byte[] AfterBytes,
        string BeforeHash, string AfterHash, string BeforeText, string AfterText, int DeletedLines, int InsertedLines,
        WorkspaceFileHistoryEntry? History, MutationProposal Budget);
    private sealed record PreparedBatch(ApplyPatchRequest Request, IReadOnlyList<FilePlan> Files,
        ArtifactRef? ManifestArtifact = null, string? Manifest = null);

    private sealed class PartialPublicationException(string message, Exception? inner = null)
        : IOException(message, inner);
}

public static class ApplyPatchMetadata
{
    public sealed record FileEntry(string Path, string BeforeHash, string AfterHash,
        ArtifactRef Preimage, ArtifactRef Postimage);
    public sealed record Manifest(string ToolCallId, string WorkspaceRoot, IReadOnlyList<FileEntry> Files);

    internal static string Encode(ToolCallId toolCallId, string workspaceRoot,
        IReadOnlyList<(string Path, string BeforeHash, string AfterHash, ArtifactRef Preimage, ArtifactRef Postimage)> files)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("kind", FilesystemApplyPatchTool.MetadataKind);
            writer.WriteNumber("version", 2); writer.WriteString("toolCallId", toolCallId.ToString());
            writer.WriteString("workspaceRoot", workspaceRoot); writer.WriteStartArray("files");
            foreach (var file in files)
            {
                writer.WriteStartObject(); writer.WriteString("path", file.Path);
                writer.WriteString("beforeSha256", file.BeforeHash); writer.WriteString("afterSha256", file.AfterHash);
                writer.WritePropertyName("preimage"); JsonSerializer.Serialize(writer, file.Preimage,
                    ApplyPatchMetadataJsonContext.Default.ArtifactRef);
                writer.WritePropertyName("postimage"); JsonSerializer.Serialize(writer, file.Postimage,
                    ApplyPatchMetadataJsonContext.Default.ArtifactRef);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static bool TryParse(string json, out Manifest manifest)
    {
        manifest = default!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasUniqueProperties(root) || root.EnumerateObject().Count() != 5
                || root.GetProperty("kind").GetString() != FilesystemApplyPatchTool.MetadataKind
                || root.GetProperty("version").GetInt32() != 2
                || root.GetProperty("toolCallId").ValueKind != JsonValueKind.String
                || root.GetProperty("workspaceRoot").ValueKind != JsonValueKind.String
                || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() is < 1 or > 64) return false;
            var entries = new List<FileEntry>();
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 5
                    || item.GetProperty("path").ValueKind != JsonValueKind.String
                    || item.GetProperty("beforeSha256").ValueKind != JsonValueKind.String
                    || item.GetProperty("afterSha256").ValueKind != JsonValueKind.String) return false;
                var path = item.GetProperty("path").GetString()!;
                var before = item.GetProperty("beforeSha256").GetString()!;
                var after = item.GetProperty("afterSha256").GetString()!;
                var preimage = item.GetProperty("preimage").Deserialize(ApplyPatchMetadataJsonContext.Default.ArtifactRef);
                var postimage = item.GetProperty("postimage").Deserialize(ApplyPatchMetadataJsonContext.Default.ArtifactRef);
                if (!FilesystemReconciliationMetadata.IsSaneRelativePath(path)
                    || path.Replace('\\', '/').Split('/')[0].Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || !seen.Add(path)
                    || !IsHash(before) || !IsHash(after) || before == after
                    || !IsImageRef(preimage, FilesystemPreimage.MediaType)
                    || !IsImageRef(postimage, FilesystemPreimage.PostMediaType)) return false;
                entries.Add(new FileEntry(path, before, after, preimage!, postimage!));
            }
            var id = root.GetProperty("toolCallId").GetString()!;
            if (!Guid.TryParse(id, out _)) return false;
            var workspace = root.GetProperty("workspaceRoot").GetString()!;
            if (string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace)) return false;
            manifest = new Manifest(id, workspace, entries);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        { return false; }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsImageRef(ArtifactRef? reference, string mediaType) => reference is not null
        && reference.Id is not null && reference.Id.Value != Guid.Empty
        && reference.Hash is not null && reference.Hash.Algorithm == "sha256"
        && reference.Hash.Value is { Length: 64 } hash && IsHash(hash)
        && reference.Size is > 0 and <= 2 * 1024 * 1024 && reference.MediaType == mediaType
        && reference.Kind == ArtifactKind.Other && reference.Sensitivity == Sensitivity.Sensitive && !reference.Redacted;
    private static bool HasUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniqueProperties(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (!HasUniqueProperties(item)) return false;
        return true;
    }
}

[JsonSerializable(typeof(ArtifactRef))]
internal partial class ApplyPatchMetadataJsonContext : JsonSerializerContext;