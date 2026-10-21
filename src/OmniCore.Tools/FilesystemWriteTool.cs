namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// filesystem.write accepts complete desired content and normalizes existing destinations to
/// guarded edits (ADR-0044 §5.1). New destinations use the ordinary authorized create path.
///
///  - CREACIÓN: si el archivo no existe, lo crea (directorios padre incluidos) en UTF-8 sin
///    BOM. No exige token: no hay estado previo que verificar.
///  - REEMPLAZO: si el archivo existe, exige <c>expectedVersion</c> (token SHA-256 del
///    contenido vigente) y lo verifica ANTES de mutar; un token obsoleto → STALE_WRITE con el
///    mismo formato que filesystem.patch y el archivo queda intacto. Conserva el encoding/BOM
///    original. Rechaza el no-op (contenido idéntico) y vaciar el archivo existente.
///
/// Cuando el pipeline activa la política del modelo (registry por-Run cableado), aplica
/// ADR-0044 §5 ANTES de escribir nada:
///
///  - effective capability: localized existing-file edits use PatchExisting, creation uses
///    CreateFile, and a Full replacement still requires its effective ceiling. The complete
///    diff is validated and charged once, independently of the payload's shape;
///  - límites por Turn: MaxFilesPerTurn y MaxChangedLinesPerTurn (líneas borradas +
///    insertadas acumuladas) → LIMIT_EXCEEDED;
///  - MaxRewriteRatio: fracción de las líneas ORIGINALES que esta operación reescribe;
///  - lectura previa efectiva de la ruta/versión en el mismo Run para el REEMPLAZO
///    (PRIOR_READ_REQUIRED) cuando la política la exige (<c>RequirePriorRead</c>);
///  - registra la validación post-edición pendiente si la política la exige
///    (<c>RequirePostEditValidation</c>).
///
/// Existing files use the shared patch editor and preserve their identity. A durable pre-image
/// supports rollback/reconciliation; a process interruption can leave a Conflict, not an atomic
/// rename. New files publish through a sibling temporary and an exclusive move. The
/// reconciliation metadata travels in <c>ToolCallStarted</c> (Barrier, ADR-0002 §2):
/// para una creación el pre-hash es el centinela <c>absent</c> (no hay estado previo), así un
/// crash entre el Barrier y la publicación reconcilia Applied/NotApplied sin duplicar
/// (ADR-0004 §4). La frontera de paths y los destinos de secretos se revalidan en Execute
/// contra escapes y symlinks/junctions, además de en Prepare.
/// </summary>
public sealed class FilesystemWriteTool : ITool, IReconcilableTool
{
    private readonly ToolDescriptor _descriptor;

    private readonly IPathBoundaryValidator _boundary;

    public FilesystemWriteTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("filesystem.write"),
            "Creates a new workspace file or normalizes complete desired content into edits of an existing file. Existing files require expectedVersion and visible read coverage when prior reads are required; the complete diff obeys permissions and mutation limits.",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"},\"expectedVersion\":{\"type\":[\"string\",\"null\"]}},\"required\":[\"path\",\"content\"]}"),
            new[] { "write" },
            readOnly: false,
            destructive: false,
            risk: ToolRisk.Medium,
            source: ComponentSource.Core(),
            protection: ToolProtection.None,
            effectClass: EffectClass.NonIdempotent);
    }

    public ToolDescriptor Descriptor => _descriptor;

    /// <summary>Runtime-only metadata classification, called after pure Prepare and before any
    /// permission decision. This never reads file contents and never mutates the filesystem.</summary>
    internal ToolIntent ClassifyTargetForAuthorization(ToolIntent intent, string workspaceRoot,
        IWorkspaceMutationHistoryStore? historyStore = null)
    {
        var (path, _, _) = ParseArguments(intent.NormalizedArgumentsJson);
        if (string.IsNullOrWhiteSpace(path)) return intent;
        var full = JoinPath(workspaceRoot, path);
        if (!_boundary.IsWithin(full, workspaceRoot)) return intent;
        var exists = File.Exists(full);
        var history = historyStore?.ReadFile(workspaceRoot, path);
        var tombstone = !exists && history?.State == WorkspaceFileHistoryState.Tombstone;
        var operation = tombstone ? ModelToolCapability.ReplaceFile
            : exists && history is { State: WorkspaceFileHistoryState.ExternalConflict or WorkspaceFileHistoryState.Unknown }
                ? ModelToolCapability.ReplaceFile
            : exists ? ModelToolCapability.PatchExisting
            : history is { State: WorkspaceFileHistoryState.Present or WorkspaceFileHistoryState.ExternalConflict or WorkspaceFileHistoryState.Unknown }
                ? ModelToolCapability.ReplaceFile : ModelToolCapability.CreateFile;
        return new ToolIntent(intent.ToolCallId, intent.ToolId, intent.NormalizedArgumentsJson,
            intent.Effect, intent.Claims, intent.Risk, intent.Reconciliation, operation,
            requiresPhysicalCreateCapability: tombstone,
            effectiveMutationHistoryRevision: history?.Revision);
    }

    /// <summary>Permission identity for the effective patch delegated by an existing-file write.</summary>
    internal static ToolIntent? EffectivePatchAuthorizationIntent(ToolIntent intent) =>
        intent.ToolId.ToString() == "filesystem.write"
        && (intent.EffectiveMutationOperation == ModelToolCapability.PatchExisting
            || intent.RequiresPhysicalCreateCapability)
            ? new ToolIntent(intent.ToolCallId, new ToolId("filesystem.patch"), intent.NormalizedArgumentsJson,
                intent.Effect, intent.Claims, intent.Risk, null,
                intent.RequiresPhysicalCreateCapability ? ModelToolCapability.ReplaceFile : ModelToolCapability.PatchExisting,
                intent.RequiresPhysicalCreateCapability,
                intent.EffectiveMutationHistoryRevision)
            : null;

    /// <summary>After the original permission has been resolved, inspect the existing file and
    /// derive the capability required by the actual diff. This is read-only and occurs before
    /// ToolCallStarted; stale targets stop before the effect barrier.</summary>
    internal bool TryDetermineAuthorizedOperation(ToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken, out ModelToolCapability operation, out ToolErrorCode? error,
        out string? reason)
    {
        operation = intent.EffectiveMutationOperation ?? ModelToolCapability.CreateFile;
        error = null;
        reason = null;
        var (path, expectedVersion, desired) = ParseArguments(intent.NormalizedArgumentsJson);
        if (string.IsNullOrWhiteSpace(path) || desired is null)
        {
            error = ToolErrorCode.InvalidArguments;
            reason = "Write arguments changed before normalization.";
            return false;
        }
        var full = JoinPath(context.WorkspaceRoot, path);
        if (!_boundary.IsWithin(full, context.WorkspaceRoot)
            || SecretPathGuard.IsSecretTarget(_boundary, full, context.WorkspaceRoot))
        {
            error = ToolErrorCode.PermissionDenied;
            reason = "Write target is outside the authorized workspace or is protected.";
            return false;
        }
        if (operation == ModelToolCapability.CreateFile)
        {
            if (File.Exists(full))
            {
                error = ToolErrorCode.StaleWrite;
                reason = "The destination appeared after create authorization; reread it before editing.";
                return false;
            }
            return true;
        }
        if (!File.Exists(full) && intent.RequiresPhysicalCreateCapability)
        {
            if (!TryReadTombstone(path, context, out var tombstoneBytes, out var tombstoneEntry)
                || expectedVersion is null || tombstoneEntry.ExpectedHash != expectedVersion)
            {
                error = ToolErrorCode.StaleWrite;
                reason = "The deleted-file preimage is unavailable or its version changed.";
                return false;
            }
            if (context.ReadRegistry is not null
                && (context.ReadRegistry.Ledger.MutationPolicy?.RequirePriorRead ?? true)
                && !context.ReadRegistry.HasCompleteVisibleRead(path, expectedVersion))
            {
                error = ToolErrorCode.PriorReadRequired;
                reason = "PRIOR_READ_REQUIRED: read the complete protected preimage before recreating this file.";
                return false;
            }
            try
            {
                var decodedTombstone = FileVersion.Decode(tombstoneBytes);
                _ = FileVersion.CreateEditHunks(decodedTombstone.Text, desired);
                operation = ModelToolCapability.ReplaceFile;
                return true;
            }
            catch (UnsupportedEncodingException ex)
            {
                error = ToolErrorCode.ToolFailure; reason = ex.Message; return false;
            }
        }
        if (!File.Exists(full))
        {
            error = ToolErrorCode.StaleWrite;
            reason = "The destination disappeared after edit authorization; it was not recreated.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            error = ToolErrorCode.InvalidArguments;
            reason = "expectedVersion is required for an existing file.";
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes;
        FileVersion.DecodedFile decoded;
        try
        {
            bytes = File.ReadAllBytes(full);
            if (FileVersion.VersionToken(bytes) != expectedVersion)
            {
                error = ToolErrorCode.StaleWrite;
                reason = FileVersion.StaleWriteMessage(FileVersion.VersionToken(bytes));
                return false;
            }
            decoded = FileVersion.Decode(bytes);
        }
        catch (UnsupportedEncodingException ex)
        {
            error = ToolErrorCode.ToolFailure;
            reason = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ToolErrorCode.ToolFailure;
            reason = "Could not inspect the authorized write target: " + ex.GetType().Name;
            return false;
        }

        var hunks = FileVersion.CreateEditHunks(decoded.Text, desired);
        var durableState = context.MutationHistory?.ReadFile(context.WorkspaceRoot, path);
        if (durableState is { State: WorkspaceFileHistoryState.ExternalConflict or WorkspaceFileHistoryState.Unknown })
        {
            if (!string.Equals(durableState.ObservedHash, expectedVersion, StringComparison.Ordinal))
            {
                error = ToolErrorCode.StaleWrite;
                reason = "The externally changed target must be reread before ReplaceFile authorization.";
                return false;
            }
            operation = ModelToolCapability.ReplaceFile;
        }
        else operation = DetermineOperation(decoded.Text, hunks, context.ReadRegistry?.Ledger.MutationPolicy);
        return true;
    }

    /// <summary>Recheck target identity before the durable start barrier. An appearance or
    /// disappearance since authorization must not switch create/edit capabilities.</summary>
    internal bool TargetMatchesAuthorizedClassification(ToolIntent intent, string workspaceRoot,
        IWorkspaceMutationHistoryStore? historyStore = null)
    {
        if (intent.EffectiveMutationOperation is not (ModelToolCapability.CreateFile
            or ModelToolCapability.PatchExisting or ModelToolCapability.ReplaceFile)) return false;
        var (path, _, _) = ParseArguments(intent.NormalizedArgumentsJson);
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = JoinPath(workspaceRoot, path);
        if (!_boundary.IsWithin(full, workspaceRoot)) return false;
        var exists = File.Exists(full);
        if (intent.EffectiveMutationHistoryRevision is { } expectedRevision
            && historyStore?.ReadFile(workspaceRoot, path)?.Revision != expectedRevision) return false;
        if (intent.EffectiveMutationOperation == ModelToolCapability.CreateFile) return !exists;
        if (intent.RequiresPhysicalCreateCapability)
        {
            var history = historyStore?.ReadFile(workspaceRoot, path);
            var (_, expectedVersion, _) = ParseArguments(intent.NormalizedArgumentsJson);
            return !exists && history is { State: WorkspaceFileHistoryState.Tombstone, ExpectedHash: { } hash }
                && string.Equals(hash, expectedVersion, StringComparison.Ordinal);
        }
        if (intent.EffectiveMutationOperation == ModelToolCapability.ReplaceFile)
        {
            var history = historyStore?.ReadFile(workspaceRoot, path);
            var (_, expectedVersion, _) = ParseArguments(intent.NormalizedArgumentsJson);
            if (history is { State: WorkspaceFileHistoryState.ExternalConflict or WorkspaceFileHistoryState.Unknown })
                return exists && history.ObservedHash is { } observed
                    && string.Equals(observed, expectedVersion, StringComparison.Ordinal);
        }
        return exists;
    }

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        var (path, _, content) = ParseArguments(call.NormalizedArgumentsJson);
        if (path is null)
        {
            return Rejected("No se pudo interpretar el JSON de argumentos del write",
                ToolErrorCode.InvalidArguments);
        }

        if (path.Length == 0)
        {
            return Rejected("Falta 'path' en los argumentos", ToolErrorCode.InvalidArguments);
        }

        // ADR-0018 §4: rutas de secretos se REJECT en Prepare (nunca se llega a Execute).
        if (new RedactionPolicy().IsSecretPath(path))
        {
            return Rejected("Acceso denegado: la ruta contiene secretos (.env, claves, credenciales) y está protegida (ADR-0018)",
                ToolErrorCode.PermissionDenied);
        }

        // El contenido debe estar presente como string (puede estar vacío: la decisión de si un
        // vacío es válido —creación sí, reemplazo no— es de Execute, que sabe si el archivo existe).
        if (content is null)
        {
            return Rejected("Falta 'content' en los argumentos", ToolErrorCode.InvalidArguments);
        }

        // Se declara el claim de lectura+escritura sobre la ruta concreta para que Security
        // evalúe la resource path real (no vacío) — ADR-0014 §3, ADR-0044 §2.
        var claims = new ResourceClaims(new[] { path }, new[] { path }, new NetworkGrant[0], null, new string[0]);

        // Prepare es PURO (INV-013, ADR-0014 §3): no toca el filesystem. Si el archivo existe y
        // el token falta/está obsoleto se rechaza en ExecuteAsync (y DescribeReconciliation
        // devuelve null): aquí no hay I/O para saberlo.
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.NonIdempotent, claims, ToolRisk.Medium, null);
        return new Prepared(intent);
    }

    /// <summary>
    /// Metadatos de reconciliación (ADR-0004 §4): dry-run SIN MUTAR que devuelve el hash PRE y
    /// el hash POST de los bytes que la operación escribiría. El ToolRuntime los serializa en el
    /// ToolCallStarted (Barrier) antes del efecto; tras un crash, el reconciliador clasifica
    /// Applied/NotApplied/Conflict sin re-ejecutar.
    ///
    ///  - CREACIÓN: pre = centinela <c>absent</c> (no hay estado previo observable); post = hash
    ///    de los bytes UTF-8 sin BOM del contenido.
    ///  - REEMPLAZO: pre = token de versión vigente (solo si coincide con el contenido real);
    ///    post = hash de los nuevos bytes conservando el encoding detectado.
    ///
    /// Es oportunista: si el dry-run no es fiable (ruta fuera de la frontera o hacia un secreto,
    /// reemplazo con token obsoleto o encoding inválido) devuelve null y ExecuteAsync decide.
    /// </summary>
    public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var (path, expectedVersion, content) = ParseArguments(intent.Intent.NormalizedArgumentsJson);
        if (path is null || path.Length == 0 || content is null)
        {
            return null;
        }

        if (intent.Intent.Claims.Writes.Count != 1)
        {
            return null;
        }

        var full = JoinPath(context.WorkspaceRoot, path);
        if (!_boundary.IsWithin(full, context.WorkspaceRoot)
            || SecretPathGuard.IsSecretTarget(_boundary, full, context.WorkspaceRoot))
        {
            return null;
        }

        try
        {
            if (File.Exists(full))
            {
                // Reemplazo: metadatos fiables solo si el token declara el estado REAL vigente.
                var bytes = File.ReadAllBytes(full);
                if (expectedVersion is null || FilesystemPatchTool.VersionToken(bytes) != expectedVersion)
                {
                    return null;
                }

                var postBytes = FileVersion.Encode(content, FileVersion.Decode(bytes).Encoding);
                var decoded = FileVersion.Decode(bytes);
                var hunks = FileVersion.CreateEditHunks(decoded.Text, content);
                var reconciliation = FilesystemPreimage.Capture(new ReconciliationSpec(expectedVersion,
                    FilesystemPatchTool.VersionToken(postBytes), null),
                    FilesystemPreimage.IsLeafOnly(full, context.WorkspaceRoot) ? context.Artifacts : null, bytes);
                var postImage = context.MutationHistory is null ? null
                    : FilesystemPreimage.CaptureAfter(context.Artifacts, postBytes);
                PrepareHistoryReceipt(intent, context, path, reconciliation,
                    FileVersion.VersionToken(bytes), FilesystemPatchTool.VersionToken(postBytes),
                    DetermineOperation(decoded.Text, hunks, context.ReadRegistry?.Ledger.MutationPolicy),
                    hunks.Sum(hunk => hunk.DeletedLines), hunks.Sum(hunk => hunk.InsertedLines),
                    FileVersion.CountLines(decoded.Text), priorImage: null, postImage: postImage,
                    cancellationToken: cancellationToken);
                return reconciliation;
            }

            if (Directory.Exists(full))
            {
                return null;
            }

            // A deleted path may only be recreated from its durable, explicitly read tombstone.
            if (TryReadTombstone(path, context, out var tombstoneBytes, out var tombstone))
            {
                if (expectedVersion is null || expectedVersion != tombstone.ExpectedHash) return null;
                var decoded = FileVersion.Decode(tombstoneBytes);
                var hunks = FileVersion.CreateEditHunks(decoded.Text, content);
                var postBytes = FileVersion.Encode(content, decoded.Encoding);
                var rec = FilesystemPreimage.Capture(new ReconciliationSpec(
                    FilesystemReconciliationMetadata.AbsentPreHash, FilesystemPatchTool.VersionToken(postBytes), null),
                    FilesystemPreimage.IsLeafOnly(full, context.WorkspaceRoot) ? context.Artifacts : null, null);
                var postImage = context.MutationHistory is null ? null
                    : FilesystemPreimage.CaptureAfter(context.Artifacts, postBytes);
                PrepareHistoryReceipt(intent, context, path, rec, null,
                    FilesystemPatchTool.VersionToken(postBytes), ModelToolCapability.ReplaceFile,
                    hunks.Sum(hunk => hunk.DeletedLines), hunks.Sum(hunk => hunk.InsertedLines),
                    FileVersion.CountLines(decoded.Text), tombstone.ImageRef, postImage, cancellationToken);
                return rec;
            }

            // Creación: sin estado previo observable. El post-hash usa el encoding de creación
            // (UTF-8 sin BOM); el reconciliador ve el centinela y clasifica la ausencia del
            // archivo como NotApplied (reintentable sin duplicar).
            var createBytes = FileVersion.Encode(content, FileVersion.FileEncoding.Utf8NoBom);
            var createReconciliation = FilesystemPreimage.Capture(new ReconciliationSpec(FilesystemReconciliationMetadata.AbsentPreHash,
                FilesystemPatchTool.VersionToken(createBytes), null),
                FilesystemPreimage.IsLeafOnly(full, context.WorkspaceRoot) ? context.Artifacts : null, null);
            var createPostImage = context.MutationHistory is null ? null
                : FilesystemPreimage.CaptureAfter(context.Artifacts, createBytes);
            PrepareHistoryReceipt(intent, context, path, createReconciliation, null,
                FilesystemPatchTool.VersionToken(createBytes), ModelToolCapability.CreateFile,
                0, FileVersion.CountLines(content), 0, priorImage: null, postImage: createPostImage,
                cancellationToken: cancellationToken);
            return createReconciliation;
        }
        catch (System.Exception ex) when (ex is not FilesystemPreimageException)
        {
            return null;
        }
    }

    /// <summary>
    /// Costura interna de test (bloqueante 2 de auditoría): permite inyectar un fallo
    /// determinista DESPUÉS de escribir el temporal y antes de publicar, para probar el
    /// catch de publicación (original intacto + temporal limpio) sin estado global.
    /// </summary>
#pragma warning disable CS0649
    internal Action<string, string>? TestFailureHook;

    internal Action<string, string>? TestAfterPublishHook;
#pragma warning restore CS0649

    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (path, expectedVersion, content) = ParseArguments(intent.Intent.NormalizedArgumentsJson);
        if (path is null)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "No se pudo interpretar el JSON de argumentos del write");
        }

        if (path.Length == 0)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Falta 'path' en los argumentos");
        }

        if (content is null)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Falta 'content' en los argumentos");
        }

        var full = JoinPath(context.WorkspaceRoot, path);
        if (!_boundary.IsWithin(full, context.WorkspaceRoot))
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Ruta fuera del workspace");
        }

        // ADR-0018 §3: la ruta pedida puede ser un enlace hacia un archivo de secretos.
        if (SecretPathGuard.IsSecretTarget(_boundary, full, context.WorkspaceRoot))
        {
            return ToolResult.Error(ToolErrorCode.PermissionDenied,
                "Acceso denegado: la ruta apunta a un archivo de secretos y está protegida (ADR-0018)");
        }

        if (context.MutationHistory is not null
            && context.MutationHistory.ReadReceipt(context.WorkspaceRoot, intent.Intent.ToolCallId, path) is null)
            return ToolResult.Error(ToolErrorCode.ToolFailure,
                "Durable mutation preimage/receipt was not prepared; no file was changed.");

        // Un write opera sobre un ARCHIVO: una ruta que nombra un directorio (existente o con
        // grafía de directorio) nunca es un destino válido.
        if (Directory.Exists(full) || Path.GetFileName(full).Length == 0)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "La ruta es un directorio, no un archivo: " + path);
        }

        var ledger = context.ReadRegistry?.Ledger;

        ToolResult result;
        if (File.Exists(full))
        {
            if (intent.Intent.EffectiveMutationOperation == ModelToolCapability.CreateFile)
                return ToolResult.Error(ToolErrorCode.StaleWrite,
                    "El destino apareció después de autorizar una creación; relee el archivo antes de editarlo.");
            if (intent.Intent.EffectiveMutationOperation == ModelToolCapability.PatchExisting
                && !File.Exists(full))
                return ToolResult.Error(ToolErrorCode.StaleWrite,
                    "El destino desapareció después de autorizar la edición; no se creó un archivo nuevo.");
            result = await ReplaceFile(path, full, expectedVersion, content!, ledger, intent, context,
                cancellationToken).ConfigureAwait(false);
        }
        else if (intent.Intent.RequiresPhysicalCreateCapability
            && intent.Intent.EffectiveMutationOperation is ModelToolCapability.PatchExisting or ModelToolCapability.ReplaceFile)
        {
            result = await RecreateTombstone(path, full, expectedVersion, content!, ledger, intent, context,
                cancellationToken).ConfigureAwait(false);
        }
        else if (intent.Intent.EffectiveMutationOperation is ModelToolCapability.PatchExisting or ModelToolCapability.ReplaceFile)
        {
            return ToolResult.Error(ToolErrorCode.StaleWrite,
                "El destino desapareció después de autorizar la edición; no se creó un archivo nuevo.");
        }
        else
        {
            result = await CreateFile(path, full, content!, ledger, intent, context, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!result.IsError && context.MutationHistory is not null)
            CompleteHistoryReceipt(intent, context, path, result.AfterStateBytes, cancellationToken);
        return result;
    }

    /// <summary>REEMPLAZO de un archivo existente: token obligatorio + STALE_WRITE + política de mutación.</summary>
    private async Task<ToolResult> ReplaceFile(string path, string full, string? expectedVersion, string content,
        MutationLedger? ledger, AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        // El reemplazo completo exige SIEMPRE el token de versión (ADR-0044 §5): es la única
        // defensa contra pisar un estado que el modelo no vio.
        if (expectedVersion is null || expectedVersion.Length == 0)
        {
            ledger?.RecordTokenViolation(path, ModelToolCapability.ReplaceFile);
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Falta 'expectedVersion': el token de versión es obligatorio para reemplazar un archivo existente (ADR-0044 §5). "
                + "Lee el archivo y usa el token [version:…], o usa filesystem.patch para un cambio localizado.");
        }

        // ADR-0044 §5 (RequirePriorRead): lectura previa EFECTIVA del MISMO path/version en este
        // Run. Por defecto se exige; la política del modelo puede desactivarlo (RequirePriorRead
        // = false), nunca al revés. El registro ausente (pipeline sin frontera) no restringe aquí.
        if (context.ReadRegistry is not null && (ledger?.MutationPolicy?.RequirePriorRead ?? true)
            && !context.ReadRegistry.Matches(path, expectedVersion))
        {
            ledger?.RecordTokenViolation(path, ModelToolCapability.ReplaceFile);
            return ToolResult.Error(ToolErrorCode.PriorReadRequired,
                "PRIOR_READ_REQUIRED: no se puede reemplazar " + path
                + " sin una lectura previa efectiva de esa ruta/versión en este Run (ADR-0044 §5)."
                + " Lee el archivo y usa el token [version:…] que la lectura devuelva.");
        }

        // Se opera sobre los BYTES REALES: token SHA-256 del contenido vigente, encoding
        // detectado y conservado al reescribir (ADR-0044 §5).
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        var actualVersion = FileVersion.VersionToken(bytes);
        if (expectedVersion != actualVersion)
        {
            ledger?.RecordTokenViolation(path, ModelToolCapability.ReplaceFile);
            return ToolResult.Error(ToolErrorCode.StaleWrite,
                FileVersion.StaleWriteMessage(actualVersion));
        }

        FileVersion.DecodedFile decoded;
        try
        {
            decoded = FileVersion.Decode(bytes);
        }
        catch (UnsupportedEncodingException ex)
        {
            // Encoding no soportado (p. ej. UTF-32): se rechaza sin modificar el archivo.
            return ToolResult.Error(ToolErrorCode.ToolFailure, ex.Message);
        }

        if (content.Length == 0)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "No se permite vaciar un archivo existente con filesystem.write: conserva contenido o usa filesystem.patch");
        }

        var hunks = FileVersion.CreateEditHunks(decoded.Text, content);
        if (hunks.Count == 0)
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "El contenido es idéntico al actual: no hay cambio que aplicar.");
        var operation = DetermineOperation(decoded.Text, hunks, ledger?.MutationPolicy);
        var patch = new FilesystemPatchTool(_boundary);
        var patchResult = await patch.ExecuteNormalizedWriteAsync(intent, context, path, expectedVersion, hunks,
            operation, cancellationToken, TestFailureHook, TestAfterPublishHook).ConfigureAwait(false);
        if (patchResult.IsError) return patchResult;
        var afterState = patchResult.AfterStateBytes;
        if (afterState is null) return patchResult;
        var deletedLines = hunks.Sum(hunk => hunk.DeletedLines);
        var insertedLines = hunks.Sum(hunk => hunk.InsertedLines);
        var summary = "Archivo reemplazado: " + path + " (" + deletedLines + " líneas eliminadas, "
            + insertedLines + " insertadas) [version:" + FileVersion.VersionToken(afterState) + "]";
        return new ToolResult(summary, patchResult.Preview, patchResult.Artifact, patchResult.OriginalSize,
            patchResult.WasExternalized, patchResult.EffectOutcome)
        { AfterStateBytes = afterState };
    }

    private async Task<ToolResult> RecreateTombstone(string path, string full, string? expectedVersion, string content,
        MutationLedger? ledger, AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (expectedVersion is null || !TryReadTombstone(path, context, out var bytes, out var tombstone)
            || tombstone.ExpectedHash != expectedVersion)
            return ToolResult.Error(ToolErrorCode.StaleWrite,
                "The protected deleted-file preimage changed; no file was recreated.");
        if (context.ReadRegistry is not null && (ledger?.MutationPolicy?.RequirePriorRead ?? true)
            && !context.ReadRegistry.HasCompleteVisibleRead(path, expectedVersion))
            return ToolResult.Error(ToolErrorCode.PriorReadRequired,
                "PRIOR_READ_REQUIRED: read the complete protected preimage in this Run before recreating it.");
        FileVersion.DecodedFile decoded;
        try { decoded = FileVersion.Decode(bytes); }
        catch (UnsupportedEncodingException ex) { return ToolResult.Error(ToolErrorCode.ToolFailure, ex.Message); }
        if (content.Length == 0)
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "No se permite vaciar un archivo existente con filesystem.write: conserva contenido o usa filesystem.patch");
        var hunks = FileVersion.CreateEditHunks(decoded.Text, content);
        var deleted = hunks.Sum(hunk => hunk.DeletedLines);
        var inserted = hunks.Sum(hunk => hunk.InsertedLines);
        var originalLines = FileVersion.CountLines(decoded.Text);
        var operation = ModelToolCapability.ReplaceFile;
        var refusal = ledger?.RefuseMutation(operation, path, targetExists: true, deleted, inserted, originalLines);
        if (refusal is not null) return ToolResult.Error(refusal.Code, refusal.Message);
        var newBytes = FileVersion.Encode(content, decoded.Encoding);
        return await Publish(path, full, newBytes, ledger, intent, context, created: true,
            expectedVersion: null, deletedLines: deleted, insertedLines: inserted,
            summaryPrefix: "Archivo recreado desde su preimage: " + path, cancellationToken,
            originalLines: originalLines, logicalExisting: true).ConfigureAwait(false);
    }

    private static ModelToolCapability DetermineOperation(string original,
        IReadOnlyList<TextEditHunk> hunks, FileMutationPolicy? policy)
    {
        var originalLines = FileVersion.CountLines(original);
        var deleted = hunks.Sum(hunk => hunk.DeletedLines);
        var fullSpan = original.Length > 0 && hunks.Count == 1 && hunks[0].Offset == 0
            && hunks[0].OldText.Length == original.Length;
        var ratioRequiresReplace = policy is { Mode: FileMutationMode.Full, MaxRewriteRatio: < 1.0 }
            && originalLines > 0 && deleted / (double)originalLines > policy.MaxRewriteRatio;
        // In PatchAndCreate a whole-file-shaped payload can still be a diff whose effective
        // capability is PatchExisting. The ledger applies the configured rewrite ratio to
        // every hunk; only Full (or the legacy unbounded path) may classify a full-span edit as
        // ReplaceFile.
        var fullReplacementAuthorized = fullSpan && (policy is null || policy.Mode == FileMutationMode.Full);
        return fullReplacementAuthorized || ratioRequiresReplace
            ? ModelToolCapability.ReplaceFile : ModelToolCapability.PatchExisting;
    }

    private static bool TryReadTombstone(string path, ToolExecutionContext context, out byte[] bytes,
        out WorkspaceFileHistoryEntry entry)
    {
        bytes = [];
        entry = null!;
        var history = context.MutationHistory?.ReadFile(context.WorkspaceRoot, path);
        if (history is not { State: WorkspaceFileHistoryState.Tombstone, ImageRef: { } image,
                ExpectedHash: { } expected } || context.Artifacts is null) return false;
        try
        {
            var restored = FilesystemPreimage.Read(context.Artifacts, image);
            if (restored is null || FileVersion.VersionToken(restored) != expected) return false;
            bytes = restored; entry = history; return true;
        }
        catch (FilesystemPreimageException) { return false; }
    }

    private static void PrepareHistoryReceipt(AuthorizedToolIntent intent, ToolExecutionContext context,
        string path, ReconciliationSpec reconciliation, string? expectedPreHash, string expectedPostHash,
        ModelToolCapability operation, int deletedLines, int insertedLines, int originalLines,
        ArtifactRef? priorImage, ArtifactRef? postImage, CancellationToken cancellationToken)
    {
        if (context.MutationHistory is null) return;
        if (string.IsNullOrWhiteSpace(context.MutationRunId) || string.IsNullOrWhiteSpace(context.MutationTurnId)
            || operation != ModelToolCapability.CreateFile && priorImage is null && reconciliation.BeforeStateRef is null)
            throw new ToolPreflightException(ToolErrorCode.ToolFailure,
                "Durable mutation history could not be prepared for this execution scope.");
        var image = priorImage ?? (operation == ModelToolCapability.CreateFile ? null : reconciliation.BeforeStateRef);
        var receipt = new WorkspaceMutationReceipt(context.MutationHistory.GetWorkspaceId(context.WorkspaceRoot),
            context.MutationHistory.NormalizeRelativePath(context.WorkspaceRoot, path), intent.Intent.ToolCallId,
            context.MutationRunId, context.MutationTurnId,
            operation.ToString(), expectedPreHash, expectedPostHash, image, postImage, deletedLines, insertedLines,
            WorkspaceMutationReceiptState.Prepared);
        context.MutationHistory.Prepare([receipt], cancellationToken);
    }

    private static void CompleteHistoryReceipt(AuthorizedToolIntent intent, ToolExecutionContext context,
        string path, byte[]? afterBytes, CancellationToken cancellationToken)
    {
        var store = context.MutationHistory!;
        if (afterBytes is null) throw new InvalidOperationException("A successful file mutation has no verified postimage bytes.");
        var prepared = store.ReadReceipt(context.WorkspaceRoot, intent.Intent.ToolCallId, path)
            ?? throw new InvalidDataException("Durable mutation receipt is missing after a successful filesystem effect.");
        // Postimages are optional when the safe artifact store rejects/redacts their content;
        // the committed hash and receipt remain sufficient for a present-file baseline.
        // Deletion/recreation preimages, by contrast, must always have a faithful CAS image.
        var postImage = prepared.PostimageRef;
        var previous = store.ReadFile(context.WorkspaceRoot, path);
        var state = new WorkspaceFileHistoryEntry(prepared.WorkspaceId, path.Replace('\\', '/'),
            WorkspaceFileHistoryState.Present, FileVersion.VersionToken(afterBytes), postImage,
            checked((previous?.Revision ?? 0) + 1), intent.Intent.ToolCallId);
        store.Complete([prepared with { State = WorkspaceMutationReceiptState.Applied }], [state], cancellationToken);
    }

    /// <summary>CREACIÓN de un archivo nuevo (directorios padre incluidos), UTF-8 sin BOM.</summary>
    private async Task<ToolResult> CreateFile(string path, string full, string content,
        MutationLedger? ledger, AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Presupuesto de la política (ADR-0044 §5) ANTES de escribir nada: modo (la creación
        // exige al menos PatchAndCreate) y archivos/líneas por Turn. Una creación no reescribe
        // contenido previo: no hay ratio que verificar.
        var lines = FileVersion.CountLines(content);
        var refusal = ledger?.RefuseMutation(ModelToolCapability.CreateFile, path, targetExists: false,
            deletedLines: 0, insertedLines: lines, originalLines: 0);
        if (refusal is not null)
        {
            return ToolResult.Error(refusal.Code, refusal.Message);
        }

        var newBytes = FileVersion.Encode(content, FileVersion.FileEncoding.Utf8NoBom);
        return await Publish(path, full, newBytes, ledger, intent, context, created: true,
            expectedVersion: null, deletedLines: 0, insertedLines: lines,
            summaryPrefix: "Archivo creado: " + path + " (" + lines + " líneas)", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Escritura atómica compartida por creación y reemplazo (bloqueante 1 de auditoría): bytes
    /// al temporal hermano con apertura EXCLUSIVA (CreateNew + FileShare.None, WriteThrough) y
    /// publicación vía reemplazo atómico. En la CREACIÓN se re-verifica que el destino siga
    /// ausente justo antes de publicar (TOCTOU): si apareció, se niega sin pisarlo. La
    /// contabilidad del ledger SOLO se actualiza tras publicar con éxito.
    /// </summary>
    private async Task<ToolResult> Publish(string path, string full, byte[] newBytes, MutationLedger? ledger,
        AuthorizedToolIntent intent, ToolExecutionContext context, bool created, string? expectedVersion,
        int deletedLines, int insertedLines, string summaryPrefix, CancellationToken cancellationToken,
        int originalLines = 0, bool logicalExisting = false)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(full)!,
            "." + Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        // Revalidar la frontera del TEMPORAL antes de escribirlo (el padre puede ser un
        // symlink/junction hacia fuera del workspace).
        if (!_boundary.IsWithin(tempPath, context.WorkspaceRoot))
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Temporal fuera del workspace");
        }

        var createdTemp = false;
        try
        {
            // Directorios padre: la creación puede anidar bajo un árbol aún inexistente.
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                createdTemp = true;
                await stream.WriteAsync(newBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Revalidar la frontera de la ruta antes de publicar (criterio de aceptación 2).
            if (!_boundary.IsWithin(full, context.WorkspaceRoot))
            {
                return ToolResult.Error(ToolErrorCode.InvalidArguments,
                    "Ruta fuera del workspace (revalidada antes de publicar)");
            }

            if (created && File.Exists(full))
            {
                // TOCTOU de creación: el archivo apareció entre la verificación y la publicación.
                // No se pisa: el modelo debe releer y decidir (reemplazo o patch).
                return ToolResult.Error(ToolErrorCode.StaleWrite,
                    "El archivo ya existe (apareció tras verificar su ausencia): no se escribió nada. Reléelo y usa expectedVersion para reemplazarlo.");
            }

            // Costura de test: fallo determinista DESPUÉS de escribir el temporal y antes de
            // publicar, para forzar el camino de fallo (original intacto + temporal limpio).
            TestFailureHook?.Invoke(tempPath, full);

            // El commit barrier está en el lock por ruta: el re-chequeo final y el rename quedan
            // serializados entre writers cooperantes. No hay API portable de rename condicional
            // por hash; un writer no cooperante aún puede cambiar el destino entre el hash y move.
            using (FilePublishLock.Acquire(full, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (created)
                {
                    if (File.Exists(full))
                    {
                        return ToolResult.Error(ToolErrorCode.StaleWrite,
                            "El archivo ya existe (apareció tras verificar su ausencia): no se escribió nada. Reléelo y usa expectedVersion para reemplazarlo.");
                    }
                }
                else
                {
                    if (!File.Exists(full))
                    {
                        return ToolResult.Error(ToolErrorCode.StaleWrite, FileVersion.StaleWriteMessage("absent"));
                    }

                    byte[] currentBytes;
                    try
                    {
                        currentBytes = File.ReadAllBytes(full);
                    }
                    catch (FileNotFoundException)
                    {
                        return ToolResult.Error(ToolErrorCode.StaleWrite, FileVersion.StaleWriteMessage("absent"));
                    }
                    catch (DirectoryNotFoundException)
                    {
                        return ToolResult.Error(ToolErrorCode.StaleWrite, FileVersion.StaleWriteMessage("absent"));
                    }

                    var currentVersion = FileVersion.VersionToken(currentBytes);
                    if (expectedVersion != currentVersion)
                    {
                        return ToolResult.Error(ToolErrorCode.StaleWrite, FileVersion.StaleWriteMessage(currentVersion));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (created)
                {
                    File.Move(tempPath, full, overwrite: false);
                }
                else if (OperatingSystem.IsWindows())
                {
                    // File.Replace publishes by an atomic filesystem replacement on Windows.
                    // It cannot condition the replacement on the hash just checked above.
                    File.Replace(tempPath, full, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, full, overwrite: true);
                }

                TestAfterPublishHook?.Invoke(tempPath, full);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.IO.IOException ex)
        {
            return ToolResult.Error(ToolErrorCode.ToolFailure,
                "Error de I/O al publicar el write: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            return ToolResult.Error(ToolErrorCode.ToolFailure,
                "Error al publicar el write: " + ex.Message);
        }
        finally
        {
            // Solo se limpia el temporal si este proceso lo creó: nunca se toca un nombre que no
            // creamos nosotros, aunque coincida con el que se esperaba.
            if (createdTemp && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // El temporal ya debería estar reemplazado/renombrado en el caso de éxito.
                    // Un fallo de limpieza aquí no invalida el resultado del write.
                }
            }
        }

        // Contabiliza SOLO tras el efecto durable (y registra la validación post-edición si la
        // política la exige, ADR-0044 §5).
        ledger?.RecordMutation(path, deletedLines, insertedLines, intent.Intent.ToolCallId,
            created && !logicalExisting ? ModelToolCapability.CreateFile : ModelToolCapability.ReplaceFile, originalLines,
            context.EmitEvent);

        var newVersion = FileVersion.VersionToken(newBytes);
        var summary = summaryPrefix + " [version:" + newVersion + "]";
        return new ToolResult(summary, null, null, newBytes.Length, false, EffectOutcome.Applied)
        { AfterStateBytes = newBytes };
    }

    /// <summary>
    /// Parsea los argumentos JSON con un parser real (System.Text.Json): content puede contener
    /// saltos de línea, comillas y comas que un parser plano no soporta.
    /// </summary>
    private static (string? path, string? expectedVersion, string? content) ParseArguments(string argsJson)
    {
        if (argsJson is null || argsJson.Length == 0)
        {
            return (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            return (GetString(root, "path"), GetString(root, "expectedVersion"), GetString(root, "content"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static string? GetString(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static ToolPreparation Rejected(string reason) => new PreparationRejected(reason, null);

    /// <summary>Rechazo con código tipado (spec §71): el runtime lo persiste en el evento.</summary>
    private static ToolPreparation Rejected(string reason, ToolErrorCode errorCode) =>
        new PreparationRejected(reason, null, errorCode);

    private static string JoinPath(string root, string relative)
    {
        if (relative is null) return root;
        var norm = relative.Replace('\\', '/');
        return root.TrimEnd('/') + "/" + norm;
    }
}
