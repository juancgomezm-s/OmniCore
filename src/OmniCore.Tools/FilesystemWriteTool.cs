namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Creates new workspace files in UTF-8 without BOM. Existing destinations are
/// refused in every policy, including FullAgent; localized edits use filesystem.patch.
/// Publication is no-overwrite, with durable reconciliation metadata and mutation accounting.
/// The expectedVersion argument remains readable for compatibility with historical journals.
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
            "Creates a NEW file within the workspace. Existing files are never overwritten: read them and use filesystem.patch for localized edits. Never delete/recreate existing files or use shell/process commands to bypass this rule; preserve file identity and unrelated content.",
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

        // New files may be empty. Existing destinations are always refused in Execute.
        if (content is null)
        {
            return Rejected("Falta 'content' en los argumentos", ToolErrorCode.InvalidArguments);
        }

        // Se declara el claim de lectura+escritura sobre la ruta concreta para que Security
        // evalúe la resource path real (no vacío) — ADR-0014 §3, ADR-0044 §2.
        var claims = new ResourceClaims(new[] { path }, new[] { path }, new NetworkGrant[0], null, new string[0]);

        // Prepare is pure: existence is checked in Execute, never by overwriting a destination.
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
    /// Existing destinations return no reconciliation spec because this tool cannot edit them.
    ///
    /// Es oportunista: si el dry-run no es fiable (ruta fuera de la frontera o hacia un secreto,
    /// o destino existente) devuelve null y ExecuteAsync decide.
    /// </summary>
    public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var (path, _, content) = ParseArguments(intent.Intent.NormalizedArgumentsJson);
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
            if (File.Exists(full)) return null; // Existing files must use filesystem.patch.

            if (Directory.Exists(full))
            {
                return null;
            }

            // Creación: sin estado previo observable. El post-hash usa el encoding de creación
            // (UTF-8 sin BOM); el reconciliador ve el centinela y clasifica la ausencia del
            // archivo como NotApplied (reintentable sin duplicar).
            var createBytes = FileVersion.Encode(content, FileVersion.FileEncoding.Utf8NoBom);
            return FilesystemPreimage.Capture(new ReconciliationSpec(FilesystemReconciliationMetadata.AbsentPreHash,
                FilesystemPatchTool.VersionToken(createBytes), null),
                FilesystemPreimage.IsLeafOnly(full, context.WorkspaceRoot) ? context.Artifacts : null, null);
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
        var (path, _, content) = ParseArguments(intent.Intent.NormalizedArgumentsJson);
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

        // Un write opera sobre un ARCHIVO: una ruta que nombra un directorio (existente o con
        // grafía de directorio) nunca es un destino válido.
        if (Directory.Exists(full) || Path.GetFileName(full).Length == 0)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "La ruta es un directorio, no un archivo: " + path);
        }

        var ledger = context.ReadRegistry?.Ledger;

        if (File.Exists(full))
        {
            return ToolResult.Error(ToolErrorCode.MutationRefused,
                "MUTATION_REFUSED: filesystem.write solo crea archivos nuevos. El archivo ya existe: léelo y usa filesystem.patch "
                + "con expectedVersion para editarlo sin sustituirlo. No lo borres ni lo recrees mediante otras herramientas.");
        }

        return await CreateFile(path, full, content!, ledger, intent, context, cancellationToken)
            .ConfigureAwait(false);
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
        return await Publish(path, full, newBytes, ledger, intent, context.WorkspaceRoot,
            deletedLines: 0, insertedLines: lines,
            summaryPrefix: "Archivo creado: " + path + " (" + lines + " líneas)", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creación atómica sin sobrescritura (bloqueante 1 de auditoría): bytes
    /// al temporal hermano con apertura EXCLUSIVA (CreateNew + FileShare.None, WriteThrough) y
    /// publicación vía rename sin sobrescritura. Se re-verifica que el destino siga
    /// ausente justo antes de publicar (TOCTOU): si apareció, se niega sin pisarlo. La
    /// contabilidad del ledger SOLO se actualiza tras publicar con éxito.
    /// </summary>
    private async Task<ToolResult> Publish(string path, string full, byte[] newBytes, MutationLedger? ledger,
        AuthorizedToolIntent intent, string workspaceRoot,
        int deletedLines, int insertedLines, string summaryPrefix, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(full)!,
            "." + Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        // Revalidar la frontera del TEMPORAL antes de escribirlo (el padre puede ser un
        // symlink/junction hacia fuera del workspace).
        if (!_boundary.IsWithin(tempPath, workspaceRoot))
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
            if (!_boundary.IsWithin(full, workspaceRoot))
            {
                return ToolResult.Error(ToolErrorCode.InvalidArguments,
                    "Ruta fuera del workspace (revalidada antes de publicar)");
            }

            if (File.Exists(full))
            {
                // TOCTOU de creación: el archivo apareció entre la verificación y la publicación.
                // No se pisa: el modelo debe releer y usar filesystem.patch.
                return ToolResult.Error(ToolErrorCode.StaleWrite,
                    "El archivo ya existe (apareció tras verificar su ausencia): no se escribió nada. Reléelo y usa filesystem.patch.");
            }

            // Costura de test: fallo determinista DESPUÉS de escribir el temporal y antes de
            // publicar, para forzar el camino de fallo (original intacto + temporal limpio).
            TestFailureHook?.Invoke(tempPath, full);

            // El commit barrier está en el lock por ruta: el re-chequeo final y el rename quedan
            // serializados entre writers cooperantes. Move(overwrite:false) también rechaza
            // un destino creado por otro proceso entre la comprobación y la publicación.
            using (FilePublishLock.Acquire(full, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(full))
                    return ToolResult.Error(ToolErrorCode.StaleWrite,
                        "El archivo apareció antes de publicar: no se escribió nada; usa filesystem.patch.");
                File.Move(tempPath, full, overwrite: false);

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
            ModelToolCapability.CreateFile, 0);

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
