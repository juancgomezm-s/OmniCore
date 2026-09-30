namespace OmniCore.Tools;

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Valida argumentos de tool contra su InputSchema declarado (ADR-0014 §1, INV-001).
/// Sin reflexión (AOT): usa System.Text.Json para parsear y validar tipos básicos.
/// </summary>
internal static class ToolSchemaValidator
{
    /// <summary>
    /// Valida los argumentos contra el schema. Devuelve null si es válido, o un mensaje de error.
    /// </summary>
    public static string? Validate(string argumentsJson, InputSchema schema)
    {
        if (argumentsJson is null || argumentsJson.Length == 0)
        {
            argumentsJson = "{}";
        }

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "Los argumentos deben ser un objeto JSON";
        }

        // Parsear el schema (JSON string) para extraer properties, required, additionalProperties
        var schemaJson = schema.ToString();
        using var schemaDoc = JsonDocument.Parse(schemaJson);
        var schemaRoot = schemaDoc.RootElement;

        // Obtener required fields
        var required = new HashSet<string>();
        if (schemaRoot.TryGetProperty("required", out var reqElem) && reqElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reqElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    required.Add(item.GetString()!);
                }
            }
        }

        // Obtener properties
        var properties = new Dictionary<string, JsonElement>();
        if (schemaRoot.TryGetProperty("properties", out var propsElem) && propsElem.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsElem.EnumerateObject())
            {
                properties[prop.Name] = prop.Value.Clone();
            }
        }

        // Verificar additionalProperties (por defecto false para seguridad)
        var allowAdditional = true;
        if (schemaRoot.TryGetProperty("additionalProperties", out var addProps))
        {
            allowAdditional = addProps.ValueKind == JsonValueKind.True;
        }
        else
        {
            // Por seguridad, si no se declara, no permitir campos extra
            allowAdditional = false;
        }

        // Validar campos requeridos
        foreach (var req in required)
        {
            if (!root.TryGetProperty(req, out _))
            {
                return $"Campo requerido faltante: '{req}'";
            }
        }

        // Validar cada propiedad presente
        foreach (var prop in root.EnumerateObject())
        {
            var key = prop.Name;
            var value = prop.Value;

            if (!properties.TryGetValue(key, out var propSchema))
            {
                if (!allowAdditional)
                {
                    return $"Campo desconocido: '{key}'";
                }
                continue;
            }

            // Validar tipo
            var typeError = ValidateType(key, value, propSchema);
            if (typeError is not null)
            {
                return typeError;
            }
        }

        return null;
    }

    private static string? ValidateType(string key, JsonElement value, JsonElement propSchema)
    {
        if (!propSchema.TryGetProperty("type", out var typeElem))
        {
            return null; // sin tipo declarado, no validar
        }

        var expectedType = typeElem.GetString();
        if (expectedType is null) return null;

        return expectedType switch
        {
            "string" when value.ValueKind != JsonValueKind.String => $"Campo '{key}': se esperaba string",
            "integer" when value.ValueKind != JsonValueKind.Number => $"Campo '{key}': se esperaba integer",
            "number" when value.ValueKind != JsonValueKind.Number => $"Campo '{key}': se esperaba number",
            "boolean" when value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False => $"Campo '{key}': se esperaba boolean",
            "array" when value.ValueKind != JsonValueKind.Array => $"Campo '{key}': se esperaba array",
            "object" when value.ValueKind != JsonValueKind.Object => $"Campo '{key}': se esperaba object",
            _ => null
        };
    }
}

/// <summary>
/// Tools de lectura reales de M2 (ADR-0038 §2: sin APIs de plataforma fuera de las
/// abstracciones; aquí el acceso a filesystem se hace a través de la raíz del workspace
/// validada, sin salidas por path traversal). Frontera de paths congeb aplicada por Security.
/// </summary>
public sealed class ReadFileTool : ITool
{
    private readonly ToolDescriptor _descriptor;

    private readonly IPathBoundaryValidator _boundary;

    public ReadFileTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("filesystem.read"),
            "Lee el contenido de un archivo dentro del workspace (read-only).",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}"),
            new string[] { "read" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        // Prepare es puro: declara las claims de lectura de la ruta concreta pedida (ADR-0014 §3),
        // para que Security evalúe la resource path real (no vacío). La lectura ocurre en Execute.
        var path = ExtractPath(call.NormalizedArgumentsJson);

        // ADR-0018 §4: rutas de secretos (.env, PEM/SSH, credenciales) se REJECT en Prepare:
        // nunca se llega a ejecutar la tool (el journal termina en ToolCallRejected, sin
        // toolcall.succeeded). P0-2.
        if (path is not null && path!.Length > 0 && new OmniCore.Domain.RedactionPolicy().IsSecretPath(path!))
        {
            return new PreparationRejected(
                "Acceso denegado: la ruta contiene secretos (.env, claves, credenciales) y está protegida (ADR-0018)",
                null);
        }

        var claims = path is null || path!.Length == 0
            ? ResourceClaims.Empty()
            : new ResourceClaims(new string[] { path! }, new string[0], new NetworkGrant[0], null, new string[0]);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.None,
            claims, ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var path = ExtractPath(intent.Intent.NormalizedArgumentsJson);
        if (path is null)
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
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Archivo no encontrado: " + path));
        }

        // Token de versión: SHA-256 de los BYTES REALES (no del string decodificado), calculado
        // ANTES de redactar/truncar, para que coincida con la verificación de filesystem.patch
        // (ADR-0044 §5) aunque el contenido expuesto al modelo se recorte o se redacta.
        var bytes = File.ReadAllBytes(full);
        var version = FileVersion.VersionToken(bytes);

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
        // Redacción obligatoria del contenido antes de exponerlo al modelo (ADR-0018 §4).
        content = new OmniCore.Domain.RedactionPolicy().Redact(content);
        var truncated = content.Length > 8000 ? content.Substring(0, 8000) : content;
        var externalized = content.Length > 8000;

        // El token se añade al final, DESPUÉS de truncar/redactar, para que siempre llegue al
        // modelo utilizable aunque el contenido no sea el completo.
        var preview = truncated + "\n\n" + VersionTokenMarker(version);

        // ADR-0044 §5: registrar la lectura EFECTIVA (éxito + token real visible en el marcador
        // [version:…] del resultado) en el registro por-Run, para que filesystem.patch exija esta
        // lectura previa del MISMO path/version antes de mutar. Se registra SOLO aquí, en el
        // camino exitoso, justo antes de devolver el ToolResult exitoso. El marker [version:…] se
        // añade al final de `preview` DESPUÉS del truncado/redacción (sobre `content`), así que en
        // el momento del registro el modelo SÍ va a recibir ese token en el resultado: la identidad
        // path/version registrada coincide siempre con lo que el modelo observa. Un read que
        // devuelve antes de esta línea (archivo inexistente, fuera del workspace, encoding
        // inválido tras Decode, error, denegación, cancelación o lectura de secretos rechazada en
        // Prepare) NO registra y por tanto no habilita un patch posterior. Si el pipeline no
        // cablea registro (uso directo de la tool, sin política de modelo activa), no hay nada que
        // registrar y el comportamiento de M2 se conserva.
        if (context.ReadRegistry is not null)
        {
            context.ReadRegistry!.RecordRead(path, version);
        }

        return System.Threading.Tasks.Task.FromResult(new ToolResult(
            externalized ? "Contenido truncado (externalizado)" : "ok (" + path + ")", preview, null,
            content.Length, externalized, EffectOutcome.None));
    }

    private static string? ExtractPath(string argsJson)
    {
        var map = ArgsJson.Parse(argsJson);
        return map.TryGetValue("path", out var p) ? p : null;
    }

    private static string JoinPath(string root, string relative)
    {
        if (relative is null) return root;
        var norm = relative.Replace('\\', '/');
        return root.TrimEnd('/') + "/" + norm;
    }

    /// <summary>
    /// Marcador del token de versión en el resultado de una lectura. Se usa "version" (no
    /// "token") para no chocar con el patrón de redacción de secretos (ADR-0018 §4) que captura
    /// "token: &lt;valor&gt;"; el hex del token no contiene ':' ni espacios, así que el marcador
    /// completo sobrevive intacto a la redacción obligatoria del tool result.
    /// </summary>
    internal static string VersionTokenMarker(string version) => "[version:" + version + "]";

    /// <summary>Extrae el token de versión de un marcador [version:hex] en el resultado.</summary>
    internal static string? ExtractVersionToken(string text)
    {
        if (text is null) return null;
        const string start = "[version:";
        var idx = text.LastIndexOf(start, StringComparison.Ordinal);
        if (idx < 0) return null;
        var end = text.IndexOf(']', idx + start.Length);
        if (end < 0) return null;
        return text.Substring(idx + start.Length, end - idx - start.Length);
    }
}

/// <summary>
/// Tool para listar directorios dentro del workspace (read-only).
/// Args: path (relative, default "."), recursive (bool, default false), maxEntries (int, default 200, hard cap 2000).
/// Output: entries sorted by path, each with relative path (forward slashes), kind (file/dir) and size for files;
/// truncated: true when the cap was hit. Skips .git/; secret paths hidden via SecretPathGuard.
/// </summary>
public sealed class ListDirectoryTool : ITool
{
    private readonly ToolDescriptor _descriptor;
    private readonly IPathBoundaryValidator _boundary;

    public ListDirectoryTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("filesystem.list"),
            "Lista entradas de un directorio dentro del workspace (read-only).",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"recursive\":{\"type\":\"boolean\"},\"maxEntries\":{\"type\":\"integer\"}},\"additionalProperties\":false}"),
            new string[] { "read" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        // Prepare es puro: declara las claims de lectura de la ruta concreta pedida (ADR-0014 §3),
        // para que Security evalúe la resource path real (no vacío). La lectura ocurre en Execute.
        var path = ExtractPath(call.NormalizedArgumentsJson) ?? ".";

        // ADR-0018 §4: rutas de secretos (.env, PEM/SSH, credenciales) se REJECT en Prepare:
        // nunca se llega a ejecutar la tool (el journal termina en ToolCallRejected, sin
        // toolcall.succeeded). P0-2.
        if (path.Length > 0 && new OmniCore.Domain.RedactionPolicy().IsSecretPath(path))
        {
            return new PreparationRejected(
                "Acceso denegado: la ruta contiene secretos (.env, claves, credenciales) y está protegida (ADR-0018)",
                null);
        }

        var claims = new ResourceClaims(new string[] { path }, new string[0], new NetworkGrant[0], null, new string[0]);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.None,
            claims, ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var path = ExtractPath(intent.Intent.NormalizedArgumentsJson) ?? ".";
        var recursive = ExtractBool(intent.Intent.NormalizedArgumentsJson, "recursive", false);
        var maxEntries = ExtractInt(intent.Intent.NormalizedArgumentsJson, "maxEntries", 200);
        const int HardCap = 2000;
        if (maxEntries > HardCap) maxEntries = HardCap;

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

        if (!Directory.Exists(full))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Directorio no encontrado: " + path));
        }

        var entries = new List<DirectoryEntry>();
        var truncated = false;
        try
        {
            CollectEntries(full, context.WorkspaceRoot, path, recursive, entries, maxEntries, ref truncated);
        }
        catch (UnauthorizedAccessException)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Acceso denegado al directorio: " + path));
        }

        // Ordenar por ruta relativa
        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

        var json = SerializeEntries(entries, truncated);
        return System.Threading.Tasks.Task.FromResult(new ToolResult(
            truncated ? "truncated (" + entries.Count + " entries)" : "ok (" + entries.Count + " entries)",
            json, null, json.Length, truncated, EffectOutcome.None));
    }

    private void CollectEntries(string fullDir, string workspaceRoot, string relativeDir,
        bool recursive, List<DirectoryEntry> entries, int maxEntries, ref bool truncated)
    {
        if (truncated || entries.Count >= maxEntries) return;

        var dirInfo = new DirectoryInfo(fullDir);
        foreach (var entry in dirInfo.EnumerateFileSystemInfos())
        {
            if (truncated || entries.Count >= maxEntries) break;

            // Saltar .git/
            if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                continue;

            var relPath = relativeDir == "." ? entry.Name : relativeDir.TrimEnd('/') + "/" + entry.Name;
            var fullEntryPath = entry.FullName;

            // ADR-0018 §3: comprobar si el entry apunta a un secreto
            if (SecretPathGuard.IsSecretTarget(_boundary, fullEntryPath, workspaceRoot))
                continue;

            // Un enlace (symlink o junction) solo se lista si su destino físico sigue dentro del
            // workspace, y nunca se recorre: evita salir de la frontera y los ciclos.
            var isLink = entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isLink)
            {
                string? target;
                try
                {
                    target = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                }
                catch (IOException)
                {
                    target = null;
                }

                if (target is null || !_boundary.IsWithin(target, workspaceRoot))
                    continue;
            }

            if (entry is FileInfo fileInfo)
            {
                entries.Add(new DirectoryEntry(relPath.Replace("\\", "/"), "file", fileInfo.Length));
            }
            else if (entry is DirectoryInfo dirInfoEntry)
            {
                entries.Add(new DirectoryEntry(relPath.Replace("\\", "/"), "dir", null));
                if (recursive && !isLink)
                {
                    CollectEntries(fullEntryPath, workspaceRoot, relPath, recursive, entries, maxEntries, ref truncated);
                }
            }
        }

        if (entries.Count >= maxEntries)
        {
            truncated = true;
            if (entries.Count > maxEntries)
            {
                entries.RemoveRange(maxEntries, entries.Count - maxEntries);
            }
        }
    }

    private static string? ExtractPath(string argsJson)
    {
        var map = ArgsJson.Parse(argsJson);
        return map.TryGetValue("path", out var p) ? p : null;
    }

    private static bool ExtractBool(string argsJson, string key, bool defaultValue)
    {
        var map = ArgsJson.Parse(argsJson);
        if (map.TryGetValue(key, out var v) && bool.TryParse(v, out var parsed))
            return parsed;
        return defaultValue;
    }

    private static int ExtractInt(string argsJson, string key, int defaultValue)
    {
        var map = ArgsJson.Parse(argsJson);
        if (map.TryGetValue(key, out var v) && int.TryParse(v, out var parsed))
            return parsed;
        return defaultValue;
    }

    private static string JoinPath(string root, string relative)
    {
        if (relative is null) return root;
        var norm = relative.Replace("\\", "/");
        return root.TrimEnd('/') + "/" + norm;
    }

    private static string SerializeEntries(List<DirectoryEntry> entries, bool truncated)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append("\"entries\":[");
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var e = entries[i];
            sb.Append("{\"path\":\"");
            sb.Append(EscapeJsonString(e.RelativePath));
            sb.Append("\",\"kind\":\"");
            sb.Append(e.Kind);
            sb.Append('"');
            if (e.Size.HasValue)
            {
                sb.Append(",\"size\":");
                sb.Append(e.Size.Value);
            }
            sb.Append('}');
        }
        sb.Append(']');
        if (truncated)
        {
            sb.Append(",\"truncated\":true");
        }
        sb.Append('}');
        return sb.ToString();
    }

    internal static string EscapeJsonString(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.AppendFormat("\\u{0:X4}", (int)c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private sealed class DirectoryEntry
    {
        public string RelativePath { get; }
        public string Kind { get; }
        public long? Size { get; }

        public DirectoryEntry(string relativePath, string kind, long? size)
        {
            RelativePath = relativePath;
            Kind = kind;
            Size = size;
        }
    }
}

/// <summary>
/// Tool para buscar texto dentro de archivos del workspace (read-only).
/// Args: pattern (required string), regex (bool, default false — if true use `RegexOptions.NonBacktracking`,
/// and reject patterns that fail to compile with an error result), path (default "."), glob (optional, e.g. `*.cs`,
/// matched against the file name), maxResults (default 100, hard cap 1000), caseSensitive (default false).
/// Output JSON: matches = [{ path (relative, forward slashes), line (1-based), text (trimmed to 300 chars) }],
/// truncated. Skip .git/, skip binary files (a NUL byte in the first 8 KB), skip secret files (never return
/// their content), read-only effect class.
/// </summary>
public sealed class SearchTextTool : ITool
{
    private readonly ToolDescriptor _descriptor;
    private readonly IPathBoundaryValidator _boundary;

    public SearchTextTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("search.text"),
            "Busca coincidencias de un patrón de texto dentro de archivos del workspace (read-only).",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\"},\"regex\":{\"type\":\"boolean\"},\"path\":{\"type\":\"string\"},\"glob\":{\"type\":\"string\"},\"maxResults\":{\"type\":\"integer\"},\"caseSensitive\":{\"type\":\"boolean\"}},\"required\":[\"pattern\"],\"additionalProperties\":false}"),
            new string[] { "read" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        // Prepare es puro: declara las claims de lectura de la ruta concreta pedida (ADR-0014 §3),
        // para que Security evalúe la resource path real (no vacío). La lectura ocurre en Execute.
        var path = ExtractPath(call.NormalizedArgumentsJson) ?? ".";

        // ADR-0018 §4: rutas de secretos (.env, PEM/SSH, credenciales) se REJECT en Prepare:
        // nunca se llega a ejecutar la tool (el journal termina en ToolCallRejected, sin
        // toolcall.succeeded). P0-2.
        if (path.Length > 0 && new OmniCore.Domain.RedactionPolicy().IsSecretPath(path))
        {
            return new PreparationRejected(
                "Acceso denegado: la ruta contiene secretos (.env, claves, credenciales) y está protegida (ADR-0018)",
                null);
        }

        var claims = new ResourceClaims(new string[] { path }, new string[0], new NetworkGrant[0], null, new string[0]);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.None,
            claims, ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var pattern = ExtractPattern(intent.Intent.NormalizedArgumentsJson);
        if (pattern is null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Falta 'pattern' en los argumentos"));
        }

        var regex = ExtractBool(intent.Intent.NormalizedArgumentsJson, "regex", false);
        var path = ExtractPath(intent.Intent.NormalizedArgumentsJson) ?? ".";
        var glob = ExtractGlob(intent.Intent.NormalizedArgumentsJson);
        var maxResults = ExtractInt(intent.Intent.NormalizedArgumentsJson, "maxResults", 100);
        const int HardCap = 1000;
        if (maxResults > HardCap) maxResults = HardCap;
        var caseSensitive = ExtractBool(intent.Intent.NormalizedArgumentsJson, "caseSensitive", false);

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

        // Check if the path exists
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Ruta no encontrada: " + path));
        }

        var matches = new List<Match>();
        var truncated = false;

        if (File.Exists(full))
        {
            // Single file case
            ProcessFile(full, context.WorkspaceRoot, pattern, regex, glob, maxResults, caseSensitive, matches, ref truncated);
        }
        else if (Directory.Exists(full))
        {
            // Directory case: non-recursive search
            ProcessDirectory(full, context.WorkspaceRoot, pattern, regex, glob, maxResults, caseSensitive, matches, ref truncated);
        }

        // Sort matches by path, then line
        matches.Sort((a, b) =>
        {
            int pathCompare = string.Compare(a.Path, b.Path, StringComparison.Ordinal);
            if (pathCompare != 0) return pathCompare;
            return a.Line.CompareTo(b.Line);
        });

        // Truncate if we exceeded maxResults (though we should have stopped earlier)
        if (matches.Count > maxResults)
        {
            matches.RemoveRange(maxResults, matches.Count - maxResults);
            truncated = true;
        }

        var json = SerializeMatches(matches, truncated);
        return System.Threading.Tasks.Task.FromResult(new ToolResult(
            truncated ? $"truncated ({matches.Count} matches)" : $"ok ({matches.Count} matches)",
            json, null, json.Length, truncated, EffectOutcome.None));
    }

    private void ProcessFile(string filePath, string workspaceRoot, string pattern, bool regex, string? glob, int maxResults, bool caseSensitive, List<Match> matches, ref bool truncated)
    {
        // Check if the file is a symlink and resolve if necessary, while checking boundary and secrets
        string? fileToRead = null;
        var fileInfo = new FileInfo(filePath);

        // Check boundary for the file path itself (symlink or regular)
        if (!_boundary.IsWithin(filePath, workspaceRoot))
            return;

        // Check secret for the file path (resolving links)
        if (SecretPathGuard.IsSecretTarget(_boundary, filePath, workspaceRoot))
            return;

        // Handle symlinks/junctions
        var isLink = fileInfo.LinkTarget is not null || fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
        if (isLink)
        {
            string? target;
            try
            {
                target = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch (IOException)
            {
                target = null;
            }

            if (target is null || !_boundary.IsWithin(target, workspaceRoot))
                return;

            // Check if target is a directory (skip if so)
            var targetAttrs = File.GetAttributes(target);
            if ((targetAttrs & FileAttributes.Directory) == FileAttributes.Directory)
                return;

            fileToRead = target;
        }
        else
        {
            // Regular file
            // Check if it's a directory (shouldn't happen because we checked File.Exists, but just in case)
            if ((fileInfo.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                return;

            fileToRead = filePath;
        }

        // Check glob against file name only
        if (!string.IsNullOrEmpty(glob))
        {
            var fileName = Path.GetFileName(fileToRead);
            if (!MatchesGlob(fileName, glob))
                return;
        }

        // Search the file
        SearchFile(fileToRead, workspaceRoot, pattern, regex, maxResults, caseSensitive, matches, ref truncated);
    }

    private void ProcessDirectory(string dirPath, string workspaceRoot, string pattern, bool regex, string? glob, int maxResults, bool caseSensitive, List<Match> matches, ref bool truncated)
    {
        var dirInfo = new DirectoryInfo(dirPath);
        foreach (var entry in dirInfo.EnumerateFileSystemInfos())
        {
            // Skip .git/
            if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                continue;

            var fullEntryPath = entry.FullName;

            // Check boundary for the entry (symlink or regular file/dir)
            if (!_boundary.IsWithin(fullEntryPath, workspaceRoot))
                continue;

            // Check secret for the entry (resolving links)
            if (SecretPathGuard.IsSecretTarget(_boundary, fullEntryPath, workspaceRoot))
                continue;

            // Handle symlinks/junctions for directories: we skip symlinks to directories (non-recursive)
            var isLink = entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isLink)
            {
                string? target;
                try
                {
                    target = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                }
                catch (IOException)
                {
                    target = null;
                }

                if (target is null || !_boundary.IsWithin(target, workspaceRoot))
                    continue;

                // If target is a directory, skip (non-recursive)
                var targetAttrs = File.GetAttributes(target);
                if ((targetAttrs & FileAttributes.Directory) == FileAttributes.Directory)
                    continue;

                // If target is a file, we will process it below
                // Note: we fall through to the file processing below
            }
            else
            {
                // Not a link
                var attrs = entry.Attributes;
                if ((attrs & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    // Skip directories (non-recursive)
                    continue;
                }
                // Otherwise, it's a file
            }

            // At this point, we have a file (or a symlink to a file) that is not a directory
            // Resolve the symlink to get the actual file to read (if needed)
            string? fileToRead = null;
            if (isLink)
            {
                // We already resolved the target above and checked it's a file and within workspace
                // We need to get the target again (or we could have saved it)
                try
                {
                    var info = new FileInfo(fullEntryPath);
                    fileToRead = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                }
                catch (IOException)
                {
                    continue;
                }

                if (fileToRead is null)
                    continue;
            }
            else
            {
                fileToRead = fullEntryPath;
            }

            // Check glob against file name only
            if (!string.IsNullOrEmpty(glob))
            {
                var fileName = Path.GetFileName(fileToRead);
                if (!MatchesGlob(fileName, glob))
                    continue;
            }

            // Search the file
            SearchFile(fileToRead, workspaceRoot, pattern, regex, maxResults, caseSensitive, matches, ref truncated);

            // If we've reached maxResults, break out of the directory loop
            if (matches.Count >= maxResults)
            {
                truncated = true;
                break;
            }
        }
    }

    private void SearchFile(string filePath, string workspaceRoot, string pattern, bool regex, int maxResults, bool caseSensitive, List<Match> matches, ref bool truncated)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            // Check for binary: if there's a null byte in the first 8KB, skip
            int checkLength = Math.Min(bytes.Length, 8000);
            for (int i = 0; i < checkLength; i++)
            {
                if (bytes[i] == 0)
                {
                    // Binary file, skip
                    return;
                }
            }

            // Decode the file content
            FileVersion.DecodedFile decoded;
            try
            {
                decoded = FileVersion.Decode(bytes);
            }
            catch (UnsupportedEncodingException)
            {
                // Unsupported encoding, skip
                return;
            }

            var content = decoded.Text;
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.None);

            Regex? regexPattern = null;
            if (regex)
            {
                try
                {
                    regexPattern = new Regex(pattern, RegexOptions.NonBacktracking);
                }
                catch (ArgumentException)
                {
                    // Invalid regex, skip this file
                    return;
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                bool matchesPattern = false;

                if (regex)
                {
                    matchesPattern = regexPattern!.IsMatch(line);
                }
                else
                {
                    if (caseSensitive)
                    {
                        matchesPattern = line.Contains(pattern);
                    }
                    else
                    {
                        matchesPattern = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }

                if (matchesPattern)
                {
                    // Trim line to 300 characters (take first 300 chars)
                    var text = line.Length > 300 ? line.Substring(0, 300) : line;
                    var relativePath = MakeRelativePath(filePath, workspaceRoot);
                    matches.Add(new Match
                    {
                        Path = relativePath,
                        Line = i + 1, // 1-based line number
                        Text = text
                    });

                    if (matches.Count >= maxResults)
                    {
                        truncated = true;
                        return;
                    }
                }
            }
        }
        catch (IOException)
        {
            // Ignore file access errors and skip the file
        }
    }

    private static string? ExtractPattern(string argsJson)
    {
        var map = ArgsJson.Parse(argsJson);
        return map.TryGetValue("pattern", out var p) ? p : null;
    }

    private static string? ExtractGlob(string argsJson)
    {
        var map = ArgsJson.Parse(argsJson);
        return map.TryGetValue("glob", out var g) ? g : null;
    }

    private static bool ExtractBool(string argsJson, string key, bool defaultValue)
    {
        var map = ArgsJson.Parse(argsJson);
        if (map.TryGetValue(key, out var v) && bool.TryParse(v, out var parsed))
            return parsed;
        return defaultValue;
    }

    private static int ExtractInt(string argsJson, string key, int defaultValue)
    {
        var map = ArgsJson.Parse(argsJson);
        if (map.TryGetValue(key, out var v) && int.TryParse(v, out var parsed))
            return parsed;
        return defaultValue;
    }

    private static string JoinPath(string root, string relative)
    {
        if (relative is null) return root;
        var norm = relative.Replace("\\", "/");
        return root.TrimEnd('/') + "/" + norm;
    }

    private static string MakeRelativePath(string fullPath, string workspaceRoot)
    {
        // Ensure workspaceRoot ends with a separator for correct subtraction
        if (!workspaceRoot.EndsWith("/") && !workspaceRoot.EndsWith("\\"))
        {
            workspaceRoot = workspaceRoot + "/";
        }

        var relative = fullPath.Substring(workspaceRoot.Length);
        return relative.Replace('\\', '/');
    }

    private static bool MatchesGlob(string input, string pattern)
    {
        // Convert simple glob to regex: .* -> .*, ? -> .
        // We'll escape the pattern except for * and ?
        var escaped = Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return Regex.IsMatch(input, $"^{escaped}$", RegexOptions.None);
    }

    private static string SerializeMatches(List<Match> matches, bool truncated)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append("\"matches\":[");
        for (var i = 0; i < matches.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var m = matches[i];
            sb.Append("{\"path\":\"");
            sb.Append(ListDirectoryTool.EscapeJsonString(m.Path));
            sb.Append("\",\"line\":");
            sb.Append(m.Line);
            sb.Append(",\"text\":\"");
            sb.Append(ListDirectoryTool.EscapeJsonString(m.Text));
            sb.Append('}');
        }
        sb.Append(']');
        if (truncated)
        {
            sb.Append(",\"truncated\":true");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string? ExtractPath(string argsJson)
    {
        var map = ArgsJson.Parse(argsJson);
        return map.TryGetValue("path", out var p) ? p : null;
    }

    private sealed class Match
    {
        public string Path { get; set; } = "";
        public int Line { get; set; }
        public string Text { get; set; } = "";
    }
}

/// <summary>Mini parser de argumentos JSON para herramientas (objeto plano de strings).</summary>
internal sealed class ArgsJson
{
    public static Dictionary<string, string> Parse(string json)
    {
        var map = new Dictionary<string, string>();
        if (json is null || json.Length == 0) return map;
        var body = json.Trim();
        if (body.Length >= 2 && body[0] == '{') body = body.Substring(1, body.Length - 2);
        var i = 0;
        while (i < body.Length)
        {
            var colon = body.IndexOf(':', i);
            if (colon < 0) break;
            var key = body.Substring(i, colon - i).Trim().Trim('"');
            var after = colon + 1;
            while (after < body.Length && body[after] == ' ') after += 1;
            if (after >= body.Length) break;
            if (body[after] == '"')
            {
                var end = body.IndexOf('"', after + 1);
                if (end < 0) break;
                map[key] = body.Substring(after + 1, end - after - 1);
                i = body.IndexOf(',', end) < 0 ? body.Length : body.IndexOf(',', end) + 1;
            }
            else
            {
                var comma = body.IndexOf(',', after);
                map[key] = comma < 0 ? body.Substring(after).Trim() : body.Substring(after, comma - after).Trim();
                i = comma < 0 ? body.Length : comma + 1;
            }
        }

        return map;
    }
}