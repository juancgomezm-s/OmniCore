namespace OmniCore.Tools;

using System.Security.Cryptography;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// filesystem.patch (M3, ADR-0044 §5): parche localizado sobre un archivo existente dentro del
/// workspace. No reescritura completa, no creación ni borrado: reemplaza exactamente UNA
/// ocurrencia de oldText por newText, conservando el contenido no relacionado. Requiere
/// expectedVersion (token SHA-256 del contenido vigente) y rechaza un token obsoleto ANTES de
/// mutar (STALE_WRITE). Cuando el pipeline activa la política del modelo (registry por-Run
/// cableado), exige además una LECTURA PREVIA efectiva de esa ruta/versión en el mismo Run
/// (PRIOR_READ_REQUIRED, ADR-0044 §5): un read fallido, de otra ruta, un token fabricado o de
/// un Run anterior no habilitan el patch. La frontera de paths se revalida en Execute contra
/// escapes y symlinks/junctions, además de en Prepare.
/// </summary>
public sealed class FilesystemPatchTool : ITool, IReconcilableTool
{
    private readonly ToolDescriptor _descriptor;

    private readonly IPathBoundaryValidator _boundary;

    public FilesystemPatchTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("filesystem.patch"),
            "Applies a localized patch (oldText->newText) to an existing file within the workspace, verifying expectedVersion.",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"expectedVersion\":{\"type\":\"string\"},\"oldText\":{\"type\":\"string\"},\"newText\":{\"type\":\"string\"}},\"required\":[\"path\",\"expectedVersion\",\"oldText\",\"newText\"]}"),
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
        var (path, expectedVersion, oldText, newText) = ParseArguments(call.NormalizedArgumentsJson);
        if (path is null)
        {
            return Rejected("No se pudo interpretar el JSON de argumentos del patch",
                ToolErrorCode.InvalidArguments);
        }

        if (path is null || path!.Length == 0)
        {
            return Rejected("Falta 'path' en los argumentos", ToolErrorCode.InvalidArguments);
        }

        // ADR-0018 §4: rutas de secretos se REJECT en Prepare (nunca se llega a Execute).
        if (new RedactionPolicy().IsSecretPath(path!))
        {
            return Rejected("Acceso denegado: la ruta contiene secretos (.env, claves, credenciales) y está protegida (ADR-0018)",
                ToolErrorCode.PermissionDenied);
        }

        if (expectedVersion is null || expectedVersion!.Length == 0)
        {
            return Rejected("Falta 'expectedVersion': el token de versión es obligatorio para un patch (ADR-0044 §5)",
                ToolErrorCode.InvalidArguments);
        }

        if (oldText is null || oldText!.Length == 0)
        {
            return Rejected("Falta 'oldText': el texto a reemplazar no puede estar vacío",
                ToolErrorCode.InvalidArguments);
        }

        if (newText is null)
        {
            return Rejected("Falta 'newText' en los argumentos", ToolErrorCode.InvalidArguments);
        }

        if (oldText == newText)
        {
            return Rejected("'oldText' y 'newText' son idénticos: no hay cambio que aplicar",
                ToolErrorCode.InvalidArguments);
        }

        // Se declara el claim de escritura sobre la ruta concreta para que Security evalúe la
        // resource path real (no vacío) — ADR-0014 §3, ADR-0044 §2 (RequireExpectedVersionToken).
        var claims = new ResourceClaims(new[] { path! }, new[] { path! }, new NetworkGrant[0], null, new string[0]);

        // Prepare es PURO (INV-013, ADR-0014 §3): no lee el archivo. Los metadatos de reconciliación
        // se calculan en DescribeReconciliation, ya autorizados y antes del efecto.
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.NonIdempotent, claims, ToolRisk.Medium, null);
        return new Prepared(intent);
    }

    /// <summary>
    /// Metadatos de reconciliación (ADR-0004 §4): dry-run del parche, sin mutar nada, que da el hash
    /// PRE (token de versión esperado) y el hash POST (SHA-256 de los bytes que el parche
    /// escribiría). El ToolRuntime los serializa en el ToolCallStarted (Barrier) antes del efecto;
    /// tras un crash, el reconciliador clasifica Applied/NotApplied/Conflict sin re-ejecutar. Es
    /// oportunista: si el dry-run no es posible (archivo ausente, encoding inválido, STALE_WRITE,
    /// ruta fuera de la frontera o hacia un secreto) devuelve null y ExecuteAsync decide.
    /// </summary>
    public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var (path, expectedVersion, oldText, newText) = ParseArguments(intent.Intent.NormalizedArgumentsJson);
        if (path is null || path.Length == 0 || expectedVersion is null || oldText is null || newText is null)
        {
            return null;
        }

        var full = JoinPath(context.WorkspaceRoot, path);
        if (SecretPathGuard.IsSecretTarget(_boundary, full, context.WorkspaceRoot))
        {
            return null;
        }

        return DryRunReconciliation(context.WorkspaceRoot, intent.Intent.Claims, path, expectedVersion,
            oldText, newText, context.Artifacts);
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
        var (path, expectedVersion, oldText, newText) = ParseArguments(intent.Intent.NormalizedArgumentsJson);
        if (path is null)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "No se pudo interpretar el JSON de argumentos del patch");
        }

        if (path is null || path!.Length == 0)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Falta 'path' en los argumentos");
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

        if (!File.Exists(full))
        {
            // No se crea: un patch solo aplica sobre un archivo existente (ADR-0044 §3).
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Archivo no encontrado (un patch no crea archivos): " + path);
        }

        // ADR-0044 §5: exigir lectura previa EFECTIVA del MISMO path/version en este Run antes
        // de mutar. El modelo debe haber leído este archivo con éxito y usar EL token que esa
        // lectura expuso; un read fallido, un read de otra ruta, un token fabricado o un token
        // de un Run anterior no habilitan el patch. Se exige POR DEFECTO cuando el pipeline
        // activa la política del modelo (ReadRegistry != null); la política puede desactivarlo
        // explícitamente (RequirePriorRead = false), nunca relajar otra defensa. Esto corta
        // ANTES de leer los bytes y de mutar.
        if (context.ReadRegistry is not null
            && (context.ReadRegistry!.Ledger.MutationPolicy?.RequirePriorRead ?? true)
            && (expectedVersion is null || !context.ReadRegistry!.Matches(path!, expectedVersion!)))
        {
            context.ReadRegistry!.Ledger.RecordTokenViolation(path!, ModelToolCapability.PatchExisting);
            return ToolResult.Error(ToolErrorCode.PriorReadRequired,
                "PRIOR_READ_REQUIRED: no se puede parchear " + path
                + " sin una lectura previa efectiva de esa ruta/versión en este Run (ADR-0044 §5)."
                + " Lee el archivo y usa el token [version:…] que la lectura devuelva.");
        }

        // El patch opera sobre los BYTES REALES: se lee el contenido crudo, se calcula el token
        // de versión SHA-256 sobre esos bytes (no sobre el string decodificado) y se conserva el
        // encoding/BOM al reescribir (ADR-0044 §5).
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
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
        var content = decoded.Text;

        // Token de versión: se calcula sobre los bytes vigentes y se compara con el esperado.
        // Un token obsoleto se rechaza ANTES de cualquier mutación (STALE_WRITE, ADR-0044 §5).
        var actualVersion = FileVersion.VersionToken(bytes);
        if (expectedVersion is null || expectedVersion != actualVersion)
        {
            context.ReadRegistry?.Ledger.RecordTokenViolation(path!, ModelToolCapability.PatchExisting);
            return ToolResult.Error(ToolErrorCode.StaleWrite,
                FileVersion.StaleWriteMessage(actualVersion));
        }

        // Bloqueante 2: no se permite un oldText que sea el contenido completo del archivo, ni un
        // newText que vacíe el archivo (un parche localizado nunca sustituye el archivo completo).
        if (oldText! == content)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "oldText es el contenido completo del archivo: no se permite sustituir el archivo entero con un patch localizado");
        }

        if (newText!.Length == 0)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "newText vacío: no se permite vaciar el archivo con un patch");
        }

        // Localización: oldText debe aparecer exactamente una vez. Cero ocurrencias → no se
        // aplica; varias → ambiguo, se rechaza para no pisar contenido no relacionado.
        var first = content.IndexOf(oldText!, StringComparison.Ordinal);
        if (first < 0)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "oldText no encontrado en el archivo: el patch no se aplicó");
        }

        if (content.IndexOf(oldText!, first + oldText!.Length, StringComparison.Ordinal) >= 0)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "oldText es ambiguo: aparece más de una vez en el archivo");
        }

        var updated = content.Substring(0, first) + newText! + content.Substring(first + oldText!.Length);

        // Sin reescritura completa: el resultado difiere solo en el rango del parche y conserva
        // el contenido no relacionado. No se aplica si el resultado coincide con el contenido
        // original (cero cambios), para no generar un efecto sin razón.
        if (updated == content)
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "El patch no produce ningún cambio sobre el contenido actual");
        }

        // Presupuesto de la política del modelo (ADR-0044 §5, EPIC-021) ANTES de escribir nada:
        // modo de mutación, MaxFilesPerTurn y MaxChangedLinesPerTurn (por Turn) y
        // MaxRewriteRatio (por operación, sobre las líneas originales que no sobreviven). Sin
        // política activa (frontera sin cablear) esta capa no restringe: manda el techo.
        FileVersion.ChangedLines(content, updated, out var deletedLines, out var insertedLines);
        var refusal = context.ReadRegistry?.Ledger.RefuseMutation(
            ModelToolCapability.PatchExisting, path!, targetExists: true,
            deletedLines, insertedLines, FileVersion.CountLines(content));
        if (refusal is not null)
        {
            return ToolResult.Error(refusal.Code, refusal.Message);
        }

        // Escrito atómico (M3, bloqueante 1 de auditoría): se construyen los bytes actualizados,
        // se escriben en un archivo temporal hermano creado con apertura EXCLUSIVA
        // (FileMode.CreateNew + FileShare.None, FileOptions.WriteThrough) y recién entonces se
        // publica sobre el original vía operación de reemplazo/rename atómica donde la plataforma
        // lo permita. La apertura exclusiva evita el pre-creado de un symlink/junction por un
        // actor local que reutilice el nombre, aunque esto NO elimina todas las carreras de
        // symlinks (TOCTOU): se documenta como limitación conocida. Si el reemplazo falla, se
        // limpia el temporal de esta operación (sin tocar el original).
        var newBytes = FileVersion.Encode(updated, decoded.Encoding);
        var tempPath = Path.Combine(
            Path.GetDirectoryName(full)!,
            "." + Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        // Revalidar la frontera del TEMPORAL antes de escribirlo: el nombre se construye con
        // GetFileName/GetDirectoryName pero si el padre de 'full' es un symlink/junction que
        // apunta fuera, el temporal podría crearse fuera del workspace.
        if (!_boundary.IsWithin(tempPath, context.WorkspaceRoot))
        {
            return ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Temporal fuera del workspace");
        }
        var createdTemp = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
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

            // Costura de test: permite mutar el destino después de la comprobación inicial y
            // antes de la comparación final protegida (o cancelar antes del commit).
            TestFailureHook?.Invoke(tempPath, full);

            using (FilePublishLock.Acquire(full, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(full))
                {
                    return ToolResult.Error(ToolErrorCode.StaleWrite, FileVersion.StaleWriteMessage("absent"));
                }

                // Recomparar los bytes inmediatamente antes de publicar bajo el lock por ruta.
                // Un writer no cooperante aún tiene una ventana entre esta lectura y el rename.
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

                cancellationToken.ThrowIfCancellationRequested();
                PublishAtomic(tempPath, full);
                TestAfterPublishHook?.Invoke(tempPath, full);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.IO.IOException ex)
        {
            return 
                ToolResult.Error(ToolErrorCode.ToolFailure, "Error de I/O al publicar el patch: " + ex.Message);
        }
        catch (System.Exception ex)
        {
            return 
                ToolResult.Error(ToolErrorCode.ToolFailure, "Error al publicar el patch: " + ex.Message);
        }
        finally
        {
            // Solo se limpia el temporal si este proceso lo creó (createdTemp): nunca se toca
            // un nombre que no creamos nosotros, aunque coincida con el que se esperaba.
            if (createdTemp && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // El temporal ya debería estar reemplazado/renombrado en el caso de éxito.
                    // Un fallo de limpieza aquí no invalida el resultado del patch.
                }
            }
        }

        var summary = "Patch aplicado: " + path + " (" + oldText!.Length + "→" + newText!.Length + " caracteres)";

        // Contabiliza SOLO tras el efecto durable (y registra la validación post-edición si la
        // política la exige, ADR-0044 §5).
        context.ReadRegistry?.Ledger.RecordMutation(path!, deletedLines, insertedLines,
            intent.Intent.ToolCallId, ModelToolCapability.PatchExisting, FileVersion.CountLines(content));

        return new ToolResult(summary, null, null, updated.Length, false, EffectOutcome.Applied)
        { AfterStateBytes = newBytes };
    }

    /// <summary>
    /// Dry-run SIN MUTACIÓN del plan de reconciliación del parche (ADR-0004 §4): si el archivo está
    /// dentro de la frontera, existe, tiene el token de versión esperado y el reemplazo es no ambiguo
    /// (UNA sola ocurrencia), calcula el hash POST (SHA-256 de los bytes que el parche escribiría) y
    /// devuelve un <c>ReconciliationSpec</c> con pre=expectedVersion / post=postHash. En cualquier otro
    /// caso devuelve null (reconciliación conservadora): esto NO cambia la decisión de Prepare de
    /// aceptar/rechazar, solo enriquece el intent con metadatos cuando son fiables.
    /// </summary>
    private ReconciliationSpec? DryRunReconciliation(string workspaceRoot, ResourceClaims claims,
        string path, string expectedVersion, string oldText, string newText, IArtifactStore? artifacts)
    {
        if (claims.Writes.Count != 1)
        {
            return null;
        }

        try
        {
            var full = JoinPath(workspaceRoot, path);
            if (!_boundary.IsWithin(full, workspaceRoot) || !File.Exists(full))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(full);
            if (FilesystemPatchTool.VersionToken(bytes) != expectedVersion)
            {
                return null; // STALE_WRITE ya en Prepare: no hay metadatos fiables de reconciliación
            }

            var decoded = FileVersion.Decode(bytes);
            var content = decoded.Text;
            var first = content.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0 || content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
            {
                return null; // reemplazo ausente o ambiguo: no se puede predecir el post-hash
            }

            var updated = content.Substring(0, first) + newText + content.Substring(first + oldText.Length);
            var postBytes = FileVersion.Encode(updated, decoded.Encoding);
            return FilesystemPreimage.Capture(new ReconciliationSpec(expectedVersion,
                FilesystemPatchTool.VersionToken(postBytes), null),
                FilesystemPreimage.IsLeafOnly(full, workspaceRoot) ? artifacts : null, bytes);
        }
        catch (System.Exception ex) when (ex is not FilesystemPreimageException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parsea los argumentos JSON con un parser real (System.Text.Json): oldText/newText pueden
    /// contener saltos de línea, comillas y comas que un parser plano no soporta.
    /// </summary>
    private static (string? path, string? expectedVersion, string? oldText, string? newText)
        ParseArguments(string argsJson)
    {
        if (argsJson is null || argsJson.Length == 0)
        {
            return (null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            return (
                GetString(root, "path"),
                GetString(root, "expectedVersion"),
                GetString(root, "oldText"),
                GetString(root, "newText"));
        }
        catch (JsonException)
        {
            return (null, null, null, null);
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

    /// <summary>Token de versión = SHA-256 hex de los BYTES REALES del contenido (no del string decodificado),
    /// para que sea estable ante BOM/encoding. Determinista y comparable (ADR-0044 §5).</summary>
    public static string VersionToken(byte[] contentBytes)
    {
        var hash = SHA256.HashData(contentBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string JoinPath(string root, string relative)
    {
        if (relative is null) return root;
        var norm = relative.Replace('\\', '/');
        return root.TrimEnd('/') + "/" + norm;
    }

    /// <summary>
    /// Publica el temporal con reemplazo atómico cuando la plataforma lo ofrece. En Windows se
    /// prefiere File.Replace; en otras plataformas se usa rename-overwrite. Ninguna API
    /// disponible ofrece compare-and-swap condicionado por el hash, de modo que un writer
    /// no cooperante aún puede cambiar el destino después de la última lectura/hash y antes del
    /// rename. Writers OmniCore cooperantes se serializan mediante FilePublishLock.
    /// </summary>
    private static void PublishAtomic(string tempPath, string destPath)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.File.Replace(tempPath, destPath, destinationBackupFileName: null);
        }
        else
        {
            System.IO.File.Move(tempPath, destPath, overwrite: true);
        }
    }
}
