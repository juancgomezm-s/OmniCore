namespace OmniCore.Tools;

using System.Security.Cryptography;
using System.Text;
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
            new InputSchema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"expectedVersion\":{\"type\":\"string\"},\"oldText\":{\"type\":\"string\"},\"newText\":{\"type\":\"string\"}}}"),
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

    public Task<ToolResult> ExecuteAsync(IAuthorizedToolIntent intent, ToolExecutionContext context,
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

        if (!File.Exists(full))
        {
            // No se crea: un patch solo aplica sobre un archivo existente (ADR-0044 §3).
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Archivo no encontrado (un patch no crea archivos): " + path));
        }

        var content = File.ReadAllText(full);

        // Token de versión: se calcula sobre el contenido vigente y se compara con el esperado.
        // Un token obsoleto se rechaza ANTES de cualquier mutación (STALE_WRITE, ADR-0044 §5).
        var actualVersion = VersionToken(content);
        if (expectedVersion is null || expectedVersion != actualVersion)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("expectedVersion obsoleto (STALE_WRITE): el contenido del archivo "
                + "cambió desde la lectura. Token actual=" + actualVersion + ". Reléelo e intenta de nuevo."));
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

        File.WriteAllText(full, updated);

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

    /// <summary>Token de versión de un contenido: SHA-256 hex. Determinista y comparable.</summary>
    public static string VersionToken(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private static string JoinPath(string root, string relative)
    {
        if (relative is null) return root;
        var norm = relative.Replace('\\', '/');
        return root.TrimEnd('/') + "/" + norm;
    }
}
