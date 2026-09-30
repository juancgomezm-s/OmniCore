namespace OmniCore.Tools;

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;

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
            "Reads the contents of a file within the workspace (read-only).",
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
            "Lists entries in a directory within the workspace (read-only).",
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

    private static string EscapeJsonString(string s)
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
/// Tool Core `search.text` (M2 EPIC-018): búsqueda de texto read-only dentro del workspace.
/// Args: pattern (requerido), regex (bool, default false; compila con RegexOptions.NonBacktracking
/// y un patrón inválido produce un ToolResult fallido, nunca una excepción), path (default ".",
/// raíz de una búsqueda RECURSIVA estilo ripgrep), glob (opcional, contra el nombre de archivo),
/// maxResults (default 100, tope 1000) y caseSensitive (default false).
///
/// Reglas de frontera (misma política que filesystem.list / ADR-0024):
///  - salta `.git/` y nunca recorre enlaces de directorio (symlink o junction), evita salir de
///    la frontera física y los ciclos;
///  - un enlace de archivo solo se busca si su destino físico queda dentro del workspace, y la
///    coincidencia se reporta en la ruta relativa del enlace;
///  - salta archivos de secretos (ADR-0018): su contenido nunca se devuelve;
///  - salta archivos binarios (byte NUL en los primeros 8 KB) y encodings no soportados;
///  - el texto de cada coincidencia se trunca a 300 caracteres y se redacta (ADR-0018 §4).
///
/// Output JSON serializado con Utf8JsonWriter (nunca concatenación de strings):
/// {"matches":[{"path":...,"line":...,"text":...}],"truncated":bool}, con path relativo al
/// workspace (barras '/'), line 1-based y truncated=true si se cortó la búsqueda por el tope.
/// </summary>
public sealed class SearchTextTool : ITool
{
    private const int DefaultMaxResults = 100;
    private const int HardCapMaxResults = 1000;
    private const int MaxMatchTextChars = 300;
    private const int BinaryCheckBytes = 8 * 1024;

    private readonly ToolDescriptor _descriptor;
    private readonly IPathBoundaryValidator _boundary;

    public SearchTextTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("search.text"),
            "Searches workspace files recursively for a text pattern (read-only) and returns matching lines.",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\"},\"regex\":{\"type\":\"boolean\"},\"path\":{\"type\":\"string\"},\"glob\":{\"type\":\"string\"},\"maxResults\":{\"type\":\"integer\"},\"caseSensitive\":{\"type\":\"boolean\"}},\"required\":[\"pattern\"],\"additionalProperties\":false}"),
            new string[] { "read" },
            true,
            false,
            ToolRisk.Low,
            ComponentSource.Core(),
            ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        // Prepare es puro (ADR-0014 §3): solo declara la claim de lectura de la ruta pedida.
        // La búsqueda ocurre en Execute. ADR-0018 §4: una ruta de secretos (.env, claves,
        // credenciales) se rechaza aquí, antes de ejecutar nada (journal → ToolCallRejected).
        var path = ParseArguments(call.NormalizedArgumentsJson).Path ?? ".";

        if (path.Length > 0 && new RedactionPolicy().IsSecretPath(path))
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

    public System.Threading.Tasks.Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var args = ParseArguments(intent.Intent.NormalizedArgumentsJson);
        var pattern = args.Pattern;
        if (pattern is null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Falta 'pattern' en los argumentos"));
        }

        if (pattern.Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("'pattern' no puede estar vacío"));
        }

        var maxResults = args.MaxResults ?? DefaultMaxResults;
        if (maxResults > HardCapMaxResults)
        {
            maxResults = HardCapMaxResults;
        }

        // El patrón regex se compila UNA sola vez, antes de tocar el filesystem: un patrón
        // inválido es un resultado fallido de la tool (spec §71), nunca una excepción ni un
        // falso "0 coincidencias".
        Regex? regex = null;
        if (args.Regex)
        {
            var options = RegexOptions.NonBacktracking
                | (args.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            try
            {
                regex = new Regex(pattern, options);
            }
            catch (ArgumentException ex)
            {
                return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Patrón regex inválido: " + ex.Message));
            }
            catch (NotSupportedException ex)
            {
                // NonBacktracking no soporta algunas construcciones (p. ej. backreferences):
                // mismo tratamiento que un patrón inválido.
                return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Patrón regex no soportado: " + ex.Message));
            }
        }

        var path = args.Path ?? ".";
        var full = JoinPath(context.WorkspaceRoot, path);
        if (!_boundary.IsWithin(full, context.WorkspaceRoot))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Ruta fuera del workspace"));
        }

        // ADR-0018 §3: la ruta pedida puede ser un enlace hacia un archivo de secretos;
        // IsSecretTarget resuelve el destino físico antes de decidir.
        if (SecretPathGuard.IsSecretTarget(_boundary, full, context.WorkspaceRoot))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "Acceso denegado: la ruta apunta a un archivo de secretos y está protegida (ADR-0018)"));
        }

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Ruta no encontrada: " + path));
        }

        var matches = new List<SearchMatch>();
        var truncated = false;

        try
        {
            if (File.Exists(full))
            {
                // Archivo concreto: se busca respetando el glob como filtro adicional.
                var target = ResolveFileTarget(full, context.WorkspaceRoot);
                if (target is not null && MatchesGlob(Path.GetFileName(full), args.Glob))
                {
                    SearchFile(target, RelativePath(context.WorkspaceRoot, full), pattern, regex,
                        args.CaseSensitive, maxResults, matches, ref truncated);
                }
            }
            else
            {
                // Directorio: búsqueda recursiva. Un directorio que es un enlace (symlink o
                // junction) no se recorre NUNCA, aunque su destino quede dentro (misma regla
                // que filesystem.list: evita salir de la frontera y los ciclos).
                if (!IsLink(full))
                {
                    SearchDirectory(full, NormalizeRelativeBase(path), pattern, regex, args.CaseSensitive,
                        args.Glob, maxResults, matches, context.WorkspaceRoot, ref truncated);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Acceso denegado durante la búsqueda: " + path));
        }
        catch (IOException)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Error de E/S durante la búsqueda: " + path));
        }

        // Orden determinista: por ruta y, dentro de cada archivo, por línea.
        matches.Sort(static (a, b) =>
        {
            var byPath = string.Compare(a.Path, b.Path, StringComparison.Ordinal);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });

        var json = SerializeMatches(matches, truncated);
        return System.Threading.Tasks.Task.FromResult(new ToolResult(
            truncated ? "truncated (" + matches.Count + " matches)" : "ok (" + matches.Count + " matches)",
            json,
            null,
            json.Length,
            truncated,
            EffectOutcome.None));
    }

    // ---------------------------------------------------------------- búsqueda

    private void SearchDirectory(string fullDir, string relativeDir, string pattern, Regex? regex,
        bool caseSensitive, string? glob, int maxResults, List<SearchMatch> matches, string workspaceRoot,
        ref bool truncated)
    {
        if (truncated)
        {
            return;
        }

        FileSystemInfo[] entries;
        try
        {
            // Orden alfabético ordinal por directorio: recorrido determinista.
            entries = new DirectoryInfo(fullDir)
                .EnumerateFileSystemInfos()
                .OrderBy(static e => e.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return; // subdirectorio ilegible: se omite y se sigue con el resto
        }
        catch (IOException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (truncated || matches.Count >= maxResults)
            {
                // Quedan entradas por buscar: el resultado se marca truncado.
                truncated = true;
                return;
            }

            if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullEntryPath = entry.FullName;

            // ADR-0018 §3: entries que apuntan (directa o vía enlace) a secretos no se buscan.
            if (SecretPathGuard.IsSecretTarget(_boundary, fullEntryPath, workspaceRoot))
            {
                continue;
            }

            // Frontera: el entry (sin resolver) debe quedar dentro del workspace.
            if (!_boundary.IsWithin(fullEntryPath, workspaceRoot))
            {
                continue;
            }

            var relPath = relativeDir == "." ? entry.Name : relativeDir.TrimEnd('/') + "/" + entry.Name;

            var isLink = entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isLink)
            {
                // Regla de filesystem.list: un enlace solo se sigue si su destino físico queda
                // dentro del workspace; los enlaces de DIRECTORIO no se recorren nunca.
                if (entry is not FileInfo)
                {
                    continue;
                }

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
                {
                    continue;
                }

                if (Directory.Exists(target))
                {
                    continue;
                }

                if (!MatchesGlob(entry.Name, glob))
                {
                    continue;
                }

                SearchFile(target, relPath, pattern, regex, caseSensitive, maxResults, matches,
                    ref truncated);
                continue;
            }

            if (entry is DirectoryInfo sub)
            {
                SearchDirectory(sub.FullName, relPath, pattern, regex, caseSensitive, glob, maxResults, matches,
                    workspaceRoot, ref truncated);
                continue;
            }

            if (!MatchesGlob(entry.Name, glob))
            {
                continue;
            }

            SearchFile(fullEntryPath, relPath, pattern, regex, caseSensitive, maxResults, matches,
                ref truncated);
        }
    }

    private static void SearchFile(string filePath, string reportPath, string pattern, Regex? regex,
        bool caseSensitive, int maxResults, List<SearchMatch> matches, ref bool truncated)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (IOException)
        {
            return; // el archivo desapareció entre enumeración y lectura: se omite
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // Binario: un byte NUL en los primeros 8 KB descarta el archivo.
        var checkLength = Math.Min(bytes.Length, BinaryCheckBytes);
        for (var i = 0; i < checkLength; i++)
        {
            if (bytes[i] == 0)
            {
                return;
            }
        }

        string content;
        try
        {
            content = FileVersion.Decode(bytes).Text;
        }
        catch (UnsupportedEncodingException)
        {
            return; // encoding no soportado o bytes corruptos: se omite
        }

        var lines = SplitLines(content);
        for (var i = 0; i < lines.Length; i++)
        {
            if (matches.Count >= maxResults)
            {
                // Techo alcanzado y quedan líneas: el resultado se marca truncado.
                truncated = true;
                return;
            }

            var line = lines[i];
            var hit = regex is not null
                ? regex.IsMatch(line)
                : caseSensitive
                    ? line.Contains(pattern, StringComparison.Ordinal)
                    : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (!hit)
            {
                continue;
            }

            var text = line.Length > MaxMatchTextChars ? line.Substring(0, MaxMatchTextChars) : line;

            // ADR-0018 §4: el texto expuesto al modelo se redacta igual que en filesystem.read.
            text = new RedactionPolicy().Redact(text);
            matches.Add(new SearchMatch(reportPath, i + 1, text));
        }
    }

    private static string[] SplitLines(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        // Un salto de línea final no crea una línea fantasma vacía al final.
        if (lines.Length > 0 && lines[lines.Length - 1].Length == 0
            && (content.EndsWith('\n') || content.EndsWith('\r')))
        {
            Array.Resize(ref lines, lines.Length - 1);
        }

        return lines;
    }

    // ---------------------------------------------------------------- helpers

    private static string JoinPath(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var trimmedRoot = root.TrimEnd('/').TrimEnd('\\');
        if (normalized.Length > 0 && normalized[0] == '/')
        {
            return trimmedRoot + normalized;
        }

        return trimmedRoot + "/" + normalized;
    }

    private static string RelativePath(string workspaceRoot, string fullPath)
        => Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');

    private static string NormalizeRelativeBase(string path)
    {
        var norm = path.Replace('\\', '/').TrimEnd('/');
        if (norm.StartsWith("./", StringComparison.Ordinal))
        {
            norm = norm.Substring(2);
        }

        return norm.Length == 0 ? "." : norm;
    }

    private static bool IsLink(string dirPath)
    {
        var info = new DirectoryInfo(dirPath);
        return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>Resuelve el destino de un posible enlace de archivo; null si está roto o fuera del workspace.</summary>
    private string? ResolveFileTarget(string filePath, string workspaceRoot)
    {
        var info = new FileInfo(filePath);
        var isLink = info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        if (!isLink)
        {
            return filePath;
        }

        string? target;
        try
        {
            target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            target = null;
        }

        if (target is null || !_boundary.IsWithin(target, workspaceRoot))
        {
            return null;
        }

        return target;
    }

    /// <summary>Comprueba el nombre de archivo contra un glob simple (* y ?); null o vacío acepta todo.</summary>
    private static bool MatchesGlob(string fileName, string? glob)
    {
        if (glob is null || glob.Length == 0)
        {
            return true;
        }

        var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, pattern, RegexOptions.CultureInvariant);
    }

    private static string SerializeMatches(List<SearchMatch> matches, bool truncated)
    {
        // Serializador real (Utf8JsonWriter): escapado correcto, sin concatenación de strings.
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("matches");
            foreach (var match in matches)
            {
                writer.WriteStartObject();
                writer.WriteString("path", match.Path);
                writer.WriteNumber("line", match.Line);
                writer.WriteString("text", match.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("truncated", truncated);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    // ---------------------------------------------------------------- argumentos

    private readonly struct SearchArguments
    {
        public string? Pattern { get; init; }
        public bool Regex { get; init; }
        public bool CaseSensitive { get; init; }
        public string? Path { get; init; }
        public string? Glob { get; init; }
        public int? MaxResults { get; init; }
    }

    /// <summary>
    /// Parse con System.Text.Json (no ArgsJson): los patrones regex suelen contener barras
    /// invertidas y comillas, y el mini parser no decodifica escapes JSON.
    /// </summary>
    private static SearchArguments ParseArguments(string json)
    {
        if (json is null || json.Length == 0)
        {
            return default;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var root = doc.RootElement;
            return new SearchArguments
            {
                Pattern = GetString(root, "pattern"),
                Regex = GetBool(root, "regex", fallback: false),
                CaseSensitive = GetBool(root, "caseSensitive", fallback: false),
                Path = GetString(root, "path"),
                Glob = GetString(root, "glob"),
                MaxResults = GetInt(root, "maxResults"),
            };
        }
        catch (JsonException)
        {
            return default; // sin pattern → error tipado de la tool
        }
    }

    private static string? GetString(JsonElement root, string key)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement root, string key, bool fallback)
        => root.TryGetProperty(key, out var value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;

    private static int? GetInt(JsonElement root, string key)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    private readonly struct SearchMatch
    {
        public string Path { get; }
        public int Line { get; }
        public string Text { get; }

        public SearchMatch(string path, int line, string text)
        {
            Path = path;
            Line = line;
            Text = text;
        }
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
