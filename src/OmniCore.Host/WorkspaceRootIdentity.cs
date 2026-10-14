namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Execution;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Identidad durable de la raíz del workspace (ADR-0004 §5bis): SHA-256 de la ruta FÍSICA de la
/// raíz, con todos los symlinks/junctions resueltos, capturada al establecer la sesión y persistida
/// en <c>WorkspaceRootEstablished.DurableIdentity</c>. No serializa la ruta dentro del identity
/// token, para que la redacción de secretos del journal no rompa la comprobación al reabrir.
///
/// Por qué: <c>Directory.Exists</c> no basta. Si la ruta de la raíz se sustituye por un enlace a
/// otro árbol, sigue "existiendo" y la recuperación reconciliaría contra el árbol equivocado. Al
/// reabrir se exige que la raíz siga resolviendo a la misma ruta física; si no, la recuperación
/// falla cerrado.
///
/// No escribe ni lee nada dentro del workspace: los datos de runtime viven fuera del repo
/// (ADR-0039 §2) y el <c>.omnicore/</c> de un workspace no confiable no se consulta (INV-029).
///
/// Limitación documentada: no detecta que un directorio real se sustituya por OTRO directorio real
/// en la misma ruta (borrar y recrear). Para eso haría falta la identidad del sistema de archivos
/// (FileId en Windows, dev/inode en POSIX); queda pendiente.
/// </summary>
public static class WorkspaceRootIdentity
{
    private const string Prefix = "physical-sha256:";
    private const string LegacyPrefix = "physical:";

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
        return physical is null || physical.Length == 0 ? "" : CurrentIdentity(physical);
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
        if (string.IsNullOrEmpty(expectedIdentity))
        {
            return false;
        }

        var physical = EstablishPhysical(root, boundary);
        if (physical is null)
        {
            return false;
        }

        if (expectedIdentity.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var currentIdentity = CurrentIdentity(physical);
            return currentIdentity.Equals(expectedIdentity, StringComparison.Ordinal);
        }

        // Compatibility with journals written before the identity token was hashed. These
        // legacy values are accepted only when their persisted full path has not been redacted.
        if (!expectedIdentity.StartsWith(LegacyPrefix, StringComparison.Ordinal)) return false;
        var current = LegacyPrefix + physical;

        // Misma regla de mayúsculas que la frontera de paths: Windows no las distingue.
        return OperatingSystem.IsWindows()
            ? current.Equals(expectedIdentity, StringComparison.OrdinalIgnoreCase)
            : current.Equals(expectedIdentity, StringComparison.Ordinal);
    }

    private static string? EstablishPhysical(string root, IPathBoundaryValidator boundary)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return null;
        var physical = boundary.ResolvePhysical(root);
        return physical is null || physical.Length == 0 ? null : physical;
    }

    private static string CurrentIdentity(string physicalRoot)
    {
        var canonical = ProjectIdentity.CanonicalWorkspacePath(physicalRoot);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Prefix + Convert.ToHexStringLower(hash);
    }
}
