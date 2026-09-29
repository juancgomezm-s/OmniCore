namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Execution;

/// <summary>
/// Identidad durable de la raíz del workspace (ADR-0004 §5bis): la ruta FÍSICA de la raíz, con
/// todos los symlinks/junctions resueltos, capturada al establecer la sesión y persistida en
/// <c>WorkspaceRootEstablished.DurableIdentity</c>.
///
/// Por qué: <c>Directory.Exists</c> no basta. Si la ruta de la raíz se sustituye por un enlace a
/// otro árbol, sigue "existiendo" y la recuperación reconciliaría contra el árbol equivocado. Al
/// reabrir se exige que la raíz siga resolviendo a la misma ruta física; si no, la recuperación
/// falla cerrado.
///
/// No escribe ni lee nada dentro del workspace: los datos de runtime viven fuera del repo
/// (ADR-0039 §2) y el <c>.omnicore/</c> de un workspace no confiable no se consulta (INV-029). La
/// versión anterior usaba un marcador en <c>{root}/.omnicore/workspace-id</c>, que un repo podía
/// traer versionado como enlace a un archivo de secretos (su contenido acababa en el journal).
///
/// Limitación documentada: no detecta que un directorio real se sustituya por OTRO directorio real
/// en la misma ruta (borrar y recrear). Para eso haría falta la identidad del sistema de archivos
/// (FileId en Windows, dev/inode en POSIX); queda pendiente.
/// </summary>
public static class WorkspaceRootIdentity
{
    private const string Prefix = "physical:";

    /// <summary>
    /// Establece la identidad de la raíz. Devuelve "" si la ruta no es absoluta, no existe o sus
    /// enlaces no se pueden resolver (ciclo): sin identidad, la sesión no es recuperable
    /// automáticamente y la recuperación falla cerrado.
    /// </summary>
    public static string Establish(string root) => Establish(root, new PathBoundaryValidator());

    internal static string Establish(string root, IPathBoundaryValidator boundary)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            return "";
        }

        var physical = boundary.ResolvePhysical(root);
        return physical is null || physical.Length == 0 ? "" : Prefix + physical;
    }

    /// <summary>
    /// Verifica que la raíz sigue resolviendo a la misma ruta física que cuando se estableció.
    /// False (sin lanzar) si la identidad está vacía o es de un formato anterior, la ruta no es
    /// absoluta, no existe, o resuelve a otro sitio.
    /// </summary>
    public static bool Verify(string root, string expectedIdentity) =>
        Verify(root, expectedIdentity, new PathBoundaryValidator());

    internal static bool Verify(string root, string expectedIdentity, IPathBoundaryValidator boundary)
    {
        if (string.IsNullOrEmpty(expectedIdentity) || !expectedIdentity.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var current = Establish(root, boundary);
        if (current.Length == 0)
        {
            return false;
        }

        // Misma regla de mayúsculas que la frontera de paths: Windows no las distingue.
        return OperatingSystem.IsWindows()
            ? current.Equals(expectedIdentity, StringComparison.OrdinalIgnoreCase)
            : current.Equals(expectedIdentity, StringComparison.Ordinal);
    }
}
