namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// filesystem.write (M3 EPIC-021, ADR-0044 §5): escritura COMPLETA de un archivo dentro del
/// workspace.
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
///  - modo de mutación: el reemplazo exige <c>FileMutationMode.Full</c>; la creación exige al
///    menos <c>PatchAndCreate</c> (la frontera de capacidad expone filesystem.write create-only
///    en ese modo; esta verificación en la tool es defensa en profundidad porque aquí ya se
///    sabe si el destino existe) → MUTATION_REFUSED;
///  - límites por Turn: MaxFilesPerTurn y MaxChangedLinesPerTurn (líneas borradas +
///    insertadas acumuladas) → LIMIT_EXCEEDED;
///  - MaxRewriteRatio: fracción de las líneas ORIGINALES que esta operación reescribe;
///  - lectura previa efectiva de la ruta/versión en el mismo Run para el REEMPLAZO
///    (PRIOR_READ_REQUIRED) cuando la política la exige (<c>RequirePriorRead</c>);
///  - registra la validación post-edición pendiente si la política la exige
///    (<c>RequirePostEditValidation</c>).
///
/// Escrito atómico (bloqueante 1 de auditoría, igual que filesystem.patch): temporal hermano
/// con apertura EXCLUSIVA + FileOptions.WriteThrough y publicación vía reemplazo atómico. Los
/// metadatos de reconciliación viajan en el <c>ToolCallStarted</c> (Barrier, ADR-0002 §2):
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
            "Writes a complete file within the workspace: creates it if it does not exist, or replaces existing content after verifying expectedVersion. Use filesystem.patch for localized changes.",
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
            return Rejected("No se pudo interpretar el JSON de argumentos del write");
        }

        if (path.Length == 0)
        {
            return Rejected("Falta 'path' en los argumentos");
        }

        // ADR-0018 §4: rutas de secretos se REJECT en Prepare (nunca se llega a Execute).
        if (new RedactionPolicy().IsSecretPath(path))
        {
            return Rejected("Acceso denegado: la ruta contiene secretos (.env, claves, credenciales) y está protegida (ADR-0018)");
        }

        // El contenido debe estar presente como string (puede estar vacío: la decisión de si un
        // vacío es válido —creación sí, reemplazo no— es de Execute, que sabe si el archivo existe).
        if (content is null)
        {
            return Rejected("Falta 'content' en los argumentos");
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
    public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context)
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
                return new ReconciliationSpec(expectedVersion,
                    FilesystemPatchTool.VersionToken(postBytes), null);
            }

            if (Directory.Exists(full))
            {
                return null;
            }

            // Creación: sin estado previo observable. El post-hash usa el encoding de creación
            // (UTF-8 sin BOM); el reconciliador ve el centinela y clasifica la ausencia del
            // archivo como NotApplied (reintentable sin duplicar).
            var createBytes = FileVersion.Encode(content, FileVersion.FileEncoding.Utf8NoBom);
            return new ReconciliationSpec(FilesystemReconciliationMetadata.AbsentPreHash,
                FilesystemPatchTool.VersionToken(createBytes), null);
        }
        catch (System.Exception)
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
#pragma warning restore CS0649

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var (path, expectedVersion, content) = ParseArguments(intent.Intent.NormalizedArgumentsJson);
        if (path is null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("No se pudo interpretar el JSON de argumentos del write"));
        }

        if (path.Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Falta 'path' en los argumentos"));
        }

        if (content is null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Falta 'content' en los argumentos"));
        }

        var full = JoinPath(context.WorkspaceRoot, path);
        if (!_boundary.IsWithin(full, context.WorkspaceRoot))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Ruta fuera del workspace"));
        }

        // ADR-0018 §3: la ruta pedida puede ser un enlace hacia un archivo de secretos.
        if (SecretPathGuard.IsSecretTarget(_boundary, full, context.WorkspaceRoot))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "Acceso denegado: la ruta apunta a un archivo de secretos y está protegida (ADR-0018)"));
        }

        // Un write opera sobre un ARCHIVO: una ruta que nombra un directorio (existente o con
        // grafía de directorio) nunca es un destino válido.
        if (Directory.Exists(full) || Path.GetFileName(full).Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("La ruta es un directorio, no un archivo: " + path));
        }

        var ledger = context.ReadRegistry?.Ledger;

        if (File.Exists(full))
        {
            return ReplaceFile(path, full, expectedVersion, content!, ledger, intent, context);
        }

        return CreateFile(path, full, content!, ledger, intent, context);
    }

    /// <summary>REEMPLAZO de un archivo existente: token obligatorio + STALE_WRITE + política de mutación.</summary>
    private Task<ToolResult> ReplaceFile(string path, string full, string? expectedVersion, string content,
        MutationLedger? ledger, AuthorizedToolIntent intent, ToolExecutionContext context)
    {
        // El reemplazo completo exige SIEMPRE el token de versión (ADR-0044 §5): es la única
        // defensa contra pisar un estado que el modelo no vio.
        if (expectedVersion is null || expectedVersion.Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "Falta 'expectedVersion': el token de versión es obligatorio para reemplazar un archivo existente (ADR-0044 §5). "
                + "Lee el archivo y usa el token [version:…], o usa filesystem.patch para un cambio localizado."));
        }

        // ADR-0044 §5 (RequirePriorRead): lectura previa EFECTIVA del MISMO path/version en este
        // Run. Por defecto se exige; la política del modelo puede desactivarlo (RequirePriorRead
        // = false), nunca al revés. El registro ausente (pipeline sin frontera) no restringe aquí.
        if (context.ReadRegistry is not null && (ledger?.MutationPolicy?.RequirePriorRead ?? true)
            && !context.ReadRegistry.Matches(path, expectedVersion))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "PRIOR_READ_REQUIRED: no se puede reemplazar " + path
                + " sin una lectura previa efectiva de esa ruta/versión en este Run (ADR-0044 §5)."
                + " Lee el archivo y usa el token [version:…] que la lectura devuelva."));
        }

        // Se opera sobre los BYTES REALES: token SHA-256 del contenido vigente, encoding
        // detectado y conservado al reescribir (ADR-0044 §5).
        var bytes = File.ReadAllBytes(full);
        var actualVersion = FileVersion.VersionToken(bytes);
        if (expectedVersion != actualVersion)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(FileVersion.StaleWriteMessage(actualVersion)));
        }

        FileVersion.DecodedFile decoded;
        try
        {
            decoded = FileVersion.Decode(bytes);
        }
        catch (UnsupportedEncodingException ex)
        {
            // Encoding no soportado (p. ej. UTF-32): se rechaza sin modificar el archivo.
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(ex.Message));
        }

        if (decoded.Text == content)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "El contenido es idéntico al actual: no hay cambio que aplicar (un reemplazo sin cambio no es un efecto válido)"));
        }

        if (content.Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "No se permite vaciar un archivo existente con filesystem.write: conserva contenido o usa filesystem.patch"));
        }

        // Presupuesto de la política (ADR-0044 §5) ANTES de escribir nada: modo (el reemplazo
        // completo exige Full), archivos/líneas por Turn y ratio de reescritura.
        FileVersion.ChangedLines(decoded.Text, content, out var deleted, out var inserted);
        var originalLines = FileVersion.CountLines(decoded.Text);
        var refusal = ledger?.RefuseMutation(ModelToolCapability.ReplaceFile, path, targetExists: true,
            deleted, inserted, originalLines);
        if (refusal is not null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(refusal));
        }

        var newBytes = FileVersion.Encode(content, decoded.Encoding);
        return Publish(path, full, newBytes, ledger, intent, context.WorkspaceRoot, created: false,
            deletedLines: deleted, insertedLines: inserted,
            summaryPrefix: "Archivo reemplazado: " + path + " (" + originalLines + "→"
                + FileVersion.CountLines(content) + " líneas)");
    }

    /// <summary>CREACIÓN de un archivo nuevo (directorios padre incluidos), UTF-8 sin BOM.</summary>
    private Task<ToolResult> CreateFile(string path, string full, string content,
        MutationLedger? ledger, AuthorizedToolIntent intent, ToolExecutionContext context)
    {
        // Presupuesto de la política (ADR-0044 §5) ANTES de escribir nada: modo (la creación
        // exige al menos PatchAndCreate) y archivos/líneas por Turn. Una creación no reescribe
        // contenido previo: no hay ratio que verificar.
        var lines = FileVersion.CountLines(content);
        var refusal = ledger?.RefuseMutation(ModelToolCapability.CreateFile, path, targetExists: false,
            deletedLines: 0, insertedLines: lines, originalLines: 0);
        if (refusal is not null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(refusal));
        }

        var newBytes = FileVersion.Encode(content, FileVersion.FileEncoding.Utf8NoBom);
        return Publish(path, full, newBytes, ledger, intent, context.WorkspaceRoot, created: true,
            deletedLines: 0, insertedLines: lines, summaryPrefix: "Archivo creado: " + path + " (" + lines + " líneas)");
    }

    /// <summary>
    /// Escritura atómica compartida por creación y reemplazo (bloqueante 1 de auditoría): bytes
    /// al temporal hermano con apertura EXCLUSIVA (CreateNew + FileShare.None, WriteThrough) y
    /// publicación vía reemplazo atómico. En la CREACIÓN se re-verifica que el destino siga
    /// ausente justo antes de publicar (TOCTOU): si apareció, se niega sin pisarlo. La
    /// contabilidad del ledger SOLO se actualiza tras publicar con éxito.
    /// </summary>
    private Task<ToolResult> Publish(string path, string full, byte[] newBytes, MutationLedger? ledger,
        AuthorizedToolIntent intent, string workspaceRoot, bool created, int deletedLines, int insertedLines,
        string summaryPrefix)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(full)!,
            "." + Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        // Revalidar la frontera del TEMPORAL antes de escribirlo (el padre puede ser un
        // symlink/junction hacia fuera del workspace).
        if (!_boundary.IsWithin(tempPath, workspaceRoot))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Temporal fuera del workspace"));
        }

        var createdTemp = false;
        try
        {
            // Directorios padre: la creación puede anidar bajo un árbol aún inexistente.
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                createdTemp = true;
                stream.Write(newBytes);
                stream.Flush(flushToDisk: true);
            }

            // Revalidar la frontera de la ruta antes de publicar (criterio de aceptación 2).
            if (!_boundary.IsWithin(full, workspaceRoot))
            {
                return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Ruta fuera del workspace (revalidada antes de publicar)"));
            }

            if (created && File.Exists(full))
            {
                // TOCTOU de creación: el archivo apareció entre la verificación y la publicación.
                // No se pisa: el modelo debe releer y decidir (reemplazo o patch).
                return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                    "El archivo ya existe (apareció tras verificar su ausencia): no se escribió nada. Reléelo y usa expectedVersion para reemplazarlo."));
            }

            // Costura de test: fallo determinista DESPUÉS de escribir el temporal y antes de
            // publicar, para forzar el camino de fallo (original intacto + temporal limpio).
            TestFailureHook?.Invoke(tempPath, full);

            File.Move(tempPath, full, overwrite: !created);
        }
        catch (System.IO.IOException ex)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Error de I/O al publicar el write: " + ex.Message));
        }
        catch (System.Exception ex)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Error al publicar el write: " + ex.Message));
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
        ledger?.RecordMutation(path, deletedLines, insertedLines, intent.Intent.ToolCallId);

        var newVersion = FileVersion.VersionToken(newBytes);
        var summary = summaryPrefix + " [version:" + newVersion + "]";
        return System.Threading.Tasks.Task.FromResult(new ToolResult(summary, null, null, newBytes.Length, false, EffectOutcome.Applied));
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

    private static string JoinPath(string root, string relative)
    {
        if (relative is null) return root;
        var norm = relative.Replace('\\', '/');
        return root.TrimEnd('/') + "/" + norm;
    }
}
