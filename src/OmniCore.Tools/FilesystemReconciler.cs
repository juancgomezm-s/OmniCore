namespace OmniCore.Tools;

using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Serialización canónica y TIPADA de los metadatos de reconciliación de un efecto de filesystem
/// (ADR-0004 §4, ADR-0013 §1). Estos metadatos viajan en el evento durable <c>ToolCallStarted</c>
/// con commit Barrier (ADR-0002 §2) ANTES de que la tool ejecute: así sobreviven a un crash y son
/// la única fuente fiel del estado esperado (path, hash previo, hash posterior) al reconciliar.
///
/// Forma canónica (JSON plano, campos siempre emitidos en este orden, hash en minúsculas):
///   {"kind":"filesystem.patch","path":"doc.txt","expectedPreHash":"SHA256-hex-64","expectedPostHash":"SHA256-hex-64"}
///
/// El <c>kind</c> es <c>filesystem.patch</c> también para <c>filesystem.write</c> (EPIC-021): es
/// la forma durable de todo efecto de contenido de archivo, y mantener UN solo kind evita romper
/// journals previos (la semántica distingue por <see cref="AbsentPreHash"/>).
///
/// CREACIÓN de archivos (EPIC-021): un <c>filesystem.write</c> sobre un archivo inexistente no
/// tiene pre-hash real; el campo <c>expectedPreHash</c> lleva el centinela <see cref="AbsentPreHash"/>
/// ("absent"). En reconciliación: archivo ausente → NotApplied (el create no llegó a aplicarse y
/// puede reintentarse sin duplicar); presente → se clasifica solo por post-hash.
///
/// Contrato de seguridad de Parse: devuelve null (nunca un objeto semi-inicializado) si el JSON no
/// es la forma canónica exacta o si los campos obligatorios están ausentes o sospechosos; el
/// reconciliador entonces FALLA CERRADO (Unresolvable) en vez de clasificar con datos inventados.
/// </summary>
public sealed class FilesystemReconciliationMetadata
{
    public const string Kind = "filesystem.patch";

    /// <summary>
    /// Centinela de expectedPreHash para la CREACIÓN de un archivo (filesystem.write sobre un
    /// destino inexistente): no hay estado previo observable. Único valor no-hex aceptado por
    /// <see cref="Parse"/> en ese campo; cualquier otra grafía falla cerrado.
    /// </summary>
    public const string AbsentPreHash = "absent";

    public string Path { get; }

    public string ExpectedPreHash { get; }

    public string? ExpectedPostHash { get; }

    private FilesystemReconciliationMetadata(string path, string expectedPreHash, string? expectedPostHash)
    {
        Path = path;
        ExpectedPreHash = expectedPreHash;
        ExpectedPostHash = expectedPostHash;
    }

    /// <summary>Serie la forma canónica tipada. Hash pre obligatorio; post puede faltar (solo NotApplied/Conflict).</summary>
    public static string Encode(string path, string? expectedPreHash, string? expectedPostHash)
    {
        return "{\"kind\":\"" + Kind + "\",\"path\":" + JsonString(path)
            + ",\"expectedPreHash\":" + JsonString(expectedPreHash)
            + ",\"expectedPostHash\":" + JsonString(expectedPostHash) + "}";
    }

    /// <summary>
    /// Parsea la forma canónica de forma estricta. Devuelve null si el JSON no es la forma canónica
    /// exacta o si Path/ExpectedPreHash no son strings no vacíos (falla cerrado).
    /// </summary>
    public static FilesystemReconciliationMetadata? Parse(string? json)
    {
        if (json is null || json.Length == 0)
        {
            return null;
        }

        string? kind;
        string? path;
        string? pre;
        string? post;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            kind = GetString(root, "kind");
            path = GetString(root, "path");
            pre = GetString(root, "expectedPreHash");
            post = GetString(root, "expectedPostHash");
        }
        catch (JsonException)
        {
            return null;
        }

        if (kind is null || kind != Kind)
        {
            return null;
        }

        if (path is null || path!.Length == 0 || !IsSaneRelativePath(path!))
        {
            return null;
        }

        if (pre is null || pre!.Length == 0 || (!IsSha256Hex(pre!) && pre != AbsentPreHash))
        {
            return null;
        }

        if (post is not null && post!.Length > 0 && !IsSha256Hex(post!))
        {
            return null;
        }

        return new FilesystemReconciliationMetadata(path!, pre!, post is null || post!.Length == 0 ? null : post);
    }

    /// <summary>
    /// Verifica que un path persistido es una RUTA RELATIVA plausible dentro del workspace, no un
    /// intento de escape (ADR-0039 §2, "falla cerrado"): rechaza vacíos, absolutos (Unix/Windows),
    /// retrocesos ("..") y marcadores de unidad. El reconciliador luego confirma la frontera real con
    /// IPathBoundaryValidator que además resuelve symlinks/junctions.
    /// </summary>
    public static bool IsSaneRelativePath(string path)
    {
        if (path is null || path.Length == 0)
        {
            return false;
        }

        var norm = path.Replace('\\', '/');
        if (norm.StartsWith('/'))
        {
            return false; // absoluta Unix
        }

        if (norm.Length >= 2 && ((norm[0] >= 'a' && norm[0] <= 'z') || (norm[0] >= 'A' && norm[0] <= 'Z'))
            && norm[1] == ':')
        {
            return false; // unidad Windows "C:/..."
        }

        if (norm == ".." || norm.StartsWith("../") || norm.Contains("/../"))
        {
            return false;
        }

        return true;
    }

    private static bool IsSha256Hex(string s)
    {
        // SHA-256 hex = exactamente 64 caracteres hex en minúsculas/uppercase.
        if (s.Length != 64)
        {
            return false;
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }

    private static string JsonString(string? s)
    {
        if (s is null)
        {
            return "null";
        }

        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string? GetString(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}

/// <summary>
/// Reconciliador seguro de efectos de filesystem (ADR-0004 §4). Ante una ToolCall Started-sin-outcome,
/// relee el hash actual del archivo y lo compara con los hashes pre/post esperados persistidos por el
/// reconciliador en <c>ToolCallStarted.ReconciliationJson</c>. SOLO OBSERVA (lee bytes); nunca muta ni
/// re-ejecuta la tool. Clasifica:
///   - actual == post-hash  → Applied        (el efecto quedó escrito)
///   - actual == pre-hash   → NotApplied     (el efecto no llegó a aplicarse)
///   - otro valor / ausente → Conflict       (estado inesperado: el agente relee y decide)
///   - metadatos ausentes o ruta sospechosa  → Unresolvable (falla cerrado: nunca Applied)
///
/// CREACIÓN de archivos (EPIC-021, ADR-0044): cuando expectedPreHash es el centinela
/// <c>absent</c> (filesystem.write sobre un destino inexistente), un archivo AUSENTE ya no es
/// Conflict sino NotApplied: el create no llegó a aplicarse y puede reintentarse sin duplicar.
/// Un archivo PRESENTE se clasifica solo contra el post-hash (Applied si coincide; Conflict si
/// otra cosa lo escribió concurrentemente).
/// </summary>
public sealed class FilesystemReconciler : IFilesystemReconciler
{
    private readonly IPathBoundaryValidator _boundary;

    public FilesystemReconciler(IPathBoundaryValidator boundary)
        => _boundary = boundary;

    public FilesystemReconciliation Reconcile(string workspaceRoot, string reconciliationJson,
        CancellationToken cancellationToken)
    {
        // 1. Fallo cerrado: sin metadatos canónicos no hay forma segura de concluir nada.
        var meta = FilesystemReconciliationMetadata.Parse(reconciliationJson);
        if (meta is null)
        {
            return FilesystemReconciliation.Unresolvable(
                "metadatos de reconciliación ausentes, malformados o con ruta sospechosa: falla cerrado, sin re-ejecutar");
        }

        if (workspaceRoot is null || workspaceRoot.Length == 0)
        {
            return FilesystemReconciliation.Unresolvable("raíz del workspace ausente: falla cerrado");
        }

        // 2. Confirmar la frontera REAL (resuelve symlinks/junctions/..) antes de tocar nada.
        var full = JoinPath(workspaceRoot, meta.Path);
        if (!_boundary.IsWithin(full, workspaceRoot))
        {
            return FilesystemReconciliation.Unresolvable(
                "la ruta persistida queda fuera de la frontera del workspace: falla cerrado, sin re-ejecutar");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Observar el estado actual (solo lectura). CREACIÓN (pre == absent): el archivo
        //    ausente es NotApplied, no Conflict: el create no llegó a aplicarse y puede
        //    reintentarse sin duplicar (EPIC-021).
        var isCreation = meta.ExpectedPreHash == FilesystemReconciliationMetadata.AbsentPreHash;
        if (!File.Exists(full))
        {
            if (isCreation)
            {
                return FilesystemReconciliation.NotApplied(
                    "el archivo no existe y la mutación era una creación: el efecto no llegó a aplicarse; puede reintentarse");
            }

            return FilesystemReconciliation.Conflict(
                "el archivo esperado no existe: estado inesperado desde pre/post conocidos");
        }

        string current;
        try
        {
            current = FileVersion.VersionTokenForFile(full);
        }
        catch (System.Exception ex)
        {
            return FilesystemReconciliation.Unresolvable("no se pudo leer el hash actual: " + ex.Message);
        }

        // 4. Clasificar. Applied SOLO si el estado real coincide con el post-escrito previsto.
        if (meta.ExpectedPostHash is not null && current == meta.ExpectedPostHash)
        {
            return FilesystemReconciliation.Applied("el hash actual coincide con el post-hash esperado: el efecto quedó aplicado");
        }

        // En una creación no hay pre-hash observable con el que comparar: todo lo que no es el
        // post-hash esperado es Conflict (otro proceso escribió el destino), nunca NotApplied.
        if (!isCreation && current == meta.ExpectedPreHash)
        {
            return FilesystemReconciliation.NotApplied("el archivo sigue en su estado previo (pre-hash): el efecto no se aplicó");
        }

        return FilesystemReconciliation.Conflict(
            "el hash actual no coincide con el pre-hash ni con el post-hash: estado ambiguo, el agente debe releer");
    }

    private static string JoinPath(string root, string relative)
    {
        var norm = relative.Replace('\\', '/');
        return root.TrimEnd('/') + "/" + norm;
    }
}