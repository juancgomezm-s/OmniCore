namespace OmniCore.Host;

/// <summary>
/// Identidad DURADERA del workspace (ADR-0004 §5, ADR-0039 §2): un token de marcador escrito DENTRO
/// del propio workspace al establecer la sesión ({root}/.omnicore/workspace-id) y perseguido en el
/// evento <c>WorkspaceRootEstablished.DurableIdentity</c>.
///
/// Por qué es necesaria: "<c>VerifiedWorkspaceRoot</c> solo comprueba Directory.Exists" no basta. Si la
/// ruta de la raíz se reemplaza por un junction/symlink que apunta a otro árbol, Directory.Exists sigue
/// devolviendo true y la recuperación simularía reconciliar (clasificar Applied) contra un árbol
/// equivocado. El marcador rompe ese ataque: el árbol sustituto (o una ruta vacía) NO contiene el mismo
/// token, así que la verificación de identidad falla y la recuperación FALLA CERRADO (bloquea, visible y
/// sin clasificar Applied).
///
/// El marcador vive en {root}/.omnicore (git-ignored, ADR-0039 §2). Sólo se lee para comparar identidad;
/// jamás se confía en él para privilegios ni se acepta contenido que no sea el token exacto.
///
/// Limitación documentada: si un actor ya tiene control ESCRITURA sobre el árbol sustituto Y copia
/// también el marcador idéntico dentro de él (toma de control del árbol, no una simple sustitución de
/// enlace), la identidad no lo detecta. Detecta la sustitución de ruta por symlink/junction (el caso de
/// la auditoría), no la falsificación del árbol completo con el marcador replicado. Esto NO se presenta
/// como un cierre completo del crash recovery: es una barrera determinista contra el swap de enlace.
/// </summary>
public static class WorkspaceRootIdentity
{
    private const string MarkerRelativeDirectory = ".omnicore";

    private const string MarkerFileName = "workspace-id";

    /// <summary>
    /// Establece la identidad de la raíz: escribe un token nuevo como marcador dentro del workspace y
    /// lo devuelve. Si no se puede escribir (workspace de solo lectura, marcador pre-existente de otra
    /// identidad, excepción de I/O), devuelve "" — vacío significa que esta raíz NO tiene identidad
    /// durable y la sesión NO será recuperable automáticamente (la recuperación falla cerrado).
    ///
    /// Si ya existe un marcador con OTRO token (la raíz fue establecida previamente por otra sesión),
    /// se conserva el token existente en vez de sobrescribirlo: así todas las sesiones del mismo árbol
    /// comparten UNA identidad y una sustitución de enlace rompe la comparación para todas.
    /// </summary>
    public static string Establish(string root)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
        {
            return "";
        }

        try
        {
            var markerPath = MarkerPath(root);
            if (File.Exists(markerPath!))
            {
                // Identidad ya establecida para este árbol: reutilizarla (nunca sobrescribir).
                var existing = File.ReadAllText(markerPath!).Trim();
                return existing is null || existing.Length == 0 ? "" : existing!;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(markerPath!)!);
            var token = Guid.NewGuid().ToString("N");
            // Escrito exclusivo: si un concurrente creó el marcador entre el check y aquí, se conserva
            // el suyo (FileMode.CreateNew) y devolvemos el existente; nunca se pisa una identidad viva.
            using (var fs = new FileStream(markerPath!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs))
            {
                w.Write(token);
            }

            return token;
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// Verifica que la raíz apunta al MISMO árbol que cuando se estableció: el marcador de identidad
    /// debe existir en {root}/.omnicore/workspace-id y ser exactamente <c>expectedToken</c>. Devuelve
    /// false (sin lanzar) si falta el marcador, no coincide, la ruta no es cualificada o hay error de
    /// I/O. Con false, la recuperación del run NO continúa automáticamente.
    /// </summary>
    public static bool Verify(string root, string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken) || string.IsNullOrEmpty(root)
            || !Path.IsPathFullyQualified(root))
        {
            return false;
        }

        try
        {
            var markerPath = MarkerPath(root);
            if (!File.Exists(markerPath!))
            {
                return false;
            }

            var actual = File.ReadAllText(markerPath!).Trim();
            return !string.IsNullOrEmpty(actual) && actual!.Equals(expectedToken, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Ruta absoluta del marcador dentro de la raíz.</summary>
    internal static string MarkerPath(string root) =>
        Path.Combine(root, MarkerRelativeDirectory, MarkerFileName);
}