namespace OmniCore.Tools;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Comprueba rutas de secretos sobre el DESTINO FÍSICO real (ADR-0018 §3). Prepare solo puede
/// mirar la ruta que escribió el modelo (es puro, sin I/O); en ejecución, un enlace como
/// <c>notes.txt → .env</c> se resuelve y se evalúa el archivo al que apunta. Falla cerrado: si
/// el destino no puede resolverse (ciclo de enlaces), se trata como secreto.
/// </summary>
internal static class SecretPathGuard
{
    public static bool IsSecretTarget(IPathBoundaryValidator boundary, string fullPath, string workspaceRoot)
    {
        var physical = boundary.ResolvePhysical(fullPath);
        var physicalRoot = boundary.ResolvePhysical(workspaceRoot);
        if (physical is null || physicalRoot is null)
        {
            return true;
        }

        var policy = new RedactionPolicy();
        var relative = Path.GetRelativePath(physicalRoot, physical);
        return policy.IsSecretPath(relative) || policy.IsSecretPath(Path.GetFileName(physical));
    }
}
