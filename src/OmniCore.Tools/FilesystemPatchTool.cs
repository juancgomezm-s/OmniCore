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
/// mutar (STALE_WRITE). La frontera de paths se revalida en Execute contra escapes y
/// symlinks/junctions, además de en Prepare.
/// </summary>
public sealed class FilesystemPatchTool : ITool
{
    private readonly ToolDescriptor _descriptor;

    private readonly IPathBoundaryValidator _boundary;

    public FilesystemPatchTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("filesystem.patch"),
            "Aplica un parche localizado (oldText->newText) sobre un archivo existente dentro del workspace, verificando expectedVersion.",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"expectedVersion\":{\"type\":\"string\"},\"oldText\":{\"type\":\"string\"},\"newText\":{\"type\":\"string\"}},\"required\":[\"path\",\"expectedVersion\",\"oldText\",\"newText\"]}"),
            new[] { "write" },
            readOnly: false,
            destructive: false,
            risk: ToolRisk.Medium,
            source: ComponentSource.Core(),
            protection: ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        var (path, expectedVersion, oldText, newText) = ParseArguments(call.NormalizedArgumentsJson);
        if (path is null)
        {
            return Rejected("No se pudo interpretar el JSON de argumentos del patch");
        }

        if (path is null || path!.Length == 0)
        {
            return Rejected("Falta 'path' en los argumentos");
        }

        // ADR-0018 §4: rutas de secretos se REJECT en Prepare (nunca se llega a Execute).
        if (new RedactionPolicy().IsSecretPath(path!))
        {
            return Rejected("Acceso denegado: la ruta contiene secretos (.env, claves, credenciales) y está protegida (ADR-0018)");
        }

        if (expectedVersion is null || expectedVersion!.Length == 0)
        {
            return Rejected("Falta 'expectedVersion': el token de versión es obligatorio para un patch (ADR-0044 §5)");
        }

        if (oldText is null || oldText!.Length == 0)
        {
            return Rejected("Falta 'oldText': el texto a reemplazar no puede estar vacío");
        }

        if (newText is null)
        {
            return Rejected("Falta 'newText' en los argumentos");
        }

        if (oldText == newText)
        {
            return Rejected("'oldText' y 'newText' son idénticos: no hay cambio que aplicar");
        }

        // Se declara el claim de escritura sobre la ruta concreta para que Security evalúe la
        // resource path real (no vacío) — ADR-0014 §3, ADR-0044 §2 (RequireExpectedVersionToken).
        var claims = new ResourceClaims(new[] { path! }, new[] { path! }, new NetworkGrant[0], null, new string[0]);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.NonIdempotent, claims, ToolRisk.Medium, null);
        return new Prepared(intent);
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
        var (path, expectedVersion, oldText, newText) = ParseArguments(intent.Intent.NormalizedArgumentsJson);
        if (path is null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("No se pudo interpretar el JSON de argumentos del patch"));
        }

        if (path is null || path!.Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Falta 'path' en los argumentos"));
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

        if (!File.Exists(full))
        {
            // No se crea: un patch solo aplica sobre un archivo existente (ADR-0044 §3).
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Archivo no encontrado (un patch no crea archivos): " + path));
        }

        // El patch opera sobre los BYTES REALES: se lee el contenido crudo, se calcula el token
        // de versión SHA-256 sobre esos bytes (no sobre el string decodificado) y se conserva el
        // encoding/BOM al reescribir (ADR-0044 §5).
        var bytes = File.ReadAllBytes(full);
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
        var content = decoded.Text;

        // Token de versión: se calcula sobre los bytes vigentes y se compara con el esperado.
        // Un token obsoleto se rechaza ANTES de cualquier mutación (STALE_WRITE, ADR-0044 §5).
        var actualVersion = FileVersion.VersionToken(bytes);
        if (expectedVersion is null || expectedVersion != actualVersion)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("expectedVersion obsoleto (STALE_WRITE): el contenido del archivo "
                + "cambió desde la lectura. Token actual=" + actualVersion + ". Reléelo e intenta de nuevo."));
        }

        // Bloqueante 2: no se permite un oldText que sea el contenido completo del archivo, ni un
        // newText que vacíe el archivo (un parche localizado nunca sustituye el archivo completo).
        if (oldText! == content)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("oldText es el contenido completo del archivo: no se permite sustituir el archivo entero con un patch localizado"));
        }

        if (newText!.Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("newText vacío: no se permite vaciar el archivo con un patch"));
        }

        // Localización: oldText debe aparecer exactamente una vez. Cero ocurrencias → no se
        // aplica; varias → ambiguo, se rechaza para no pisar contenido no relacionado.
        var first = content.IndexOf(oldText!, StringComparison.Ordinal);
        if (first < 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("oldText no encontrado en el archivo: el patch no se aplicó"));
        }

        if (content.IndexOf(oldText!, first + oldText!.Length, StringComparison.Ordinal) >= 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("oldText es ambiguo: aparece más de una vez en el archivo"));
        }

        var updated = content.Substring(0, first) + newText! + content.Substring(first + oldText!.Length);

        // Sin reescritura completa: el resultado difiere solo en el rango del parche y conserva
        // el contenido no relacionado. No se aplica si el resultado coincide con el contenido
        // original (cero cambios), para no generar un efecto sin razón.
        if (updated == content)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("El patch no produce ningún cambio sobre el contenido actual"));
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
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Temporal fuera del workspace"));
        }
        var createdTemp = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                createdTemp = true;
                stream.Write(newBytes);
                stream.Flush(flushToDisk: true);
            }

            // Revalidar la frontera de la ruta antes de publicar (criterio de aceptación 2).
            if (!_boundary.IsWithin(full, context.WorkspaceRoot))
            {
                return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Ruta fuera del workspace (revalidada antes de publicar)"));
            }

            // Costura de test: si está configurada, lanza una excepción aquí — DESPUÉS de
            // escribir el temporal y antes de publicar — para forzar el camino de fallo.
            TestFailureHook?.Invoke(tempPath, full);

            PublishAtomic(tempPath, full);
        }
        catch (System.IO.IOException ex)
        {
            return System.Threading.Tasks.Task.FromResult(
                ToolResult.Error("Error de I/O al publicar el patch: " + ex.Message));
        }
        catch (System.Exception ex)
        {
            return System.Threading.Tasks.Task.FromResult(
                ToolResult.Error("Error al publicar el patch: " + ex.Message));
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
        return System.Threading.Tasks.Task.FromResult(new ToolResult(summary, null, null, updated.Length, false, EffectOutcome.Applied));
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
    /// Publica el archivo temporal sobre el destino mediante System.IO.File.Move con
    /// overwrite=true. En Windows, cuando ambos están en la misma unidad, .NET lo implementa
    /// con MoveFileEx + REPLACE_EXISTING, que es una operación atómica a nivel de sistema de
    /// archivos (el rename reemplaza el destino en un solo paso). Esto NO es una garantía
    /// absoluta de atomicidad documentada por Microsoft para todos los sistemas operativos:
    /// en plataformas o configuraciones donde el reemplazo no pueda ser atómico (unidades
    /// cruzadas, redes), el resultado depende de la implementación de la plataforma y no
    /// debe asumirse como atómico. Si la operación no es atómica, es la responsabilidad del
    /// caller (ExecuteAsync) de limpiar el temporal si el destino no quedó correctamente.
    /// </summary>
    private static void PublishAtomic(string tempPath, string destPath)
    {
        System.IO.File.Move(tempPath, destPath, overwrite: true);
    }
}
