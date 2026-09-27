namespace OmniCore.Execution;

using OmniCore.Abstractions;

/// <summary>
/// Validador de frontera de rutas del workspace (ADR-0008 §7, ADR-0037 §1). La implementación
/// vive en Execution (varón el grafo); Security usa solo la interfaz. M2 canonicaliza con
/// Path.GetFullPath (colapsa . y .., resuelve relativos contra el cwd), detecta traversal,
/// resuelve symlinks/junctions en CADA componente intermedio (no solo el objetivo final) para
/// que un enlace que escapa de la raíz no atraviese la frontera, y compara sin importar
/// mayúsculas en Windows (detectado por IsPathFullyQualified("C:\\x")). El sandbox real
/// (AppContainer/Job) llega en M3.
/// </summary>
public sealed class PathBoundaryValidator : IPathBoundaryValidator
{
    private static readonly bool _windows = Path.IsPathFullyQualified("C:\\x");

    private readonly bool _caseInsensitive;

    private readonly bool _resolveLinks;

    public PathBoundaryValidator()
    {
        _caseInsensitive = _windows;
        _resolveLinks = true;
    }

    public PathBoundaryValidator(bool caseInsensitive, bool resolveLinks)
    {
        _caseInsensitive = caseInsensitive;
        _resolveLinks = resolveLinks;
    }

    public static PathBoundaryValidator StrictWindows() => new(true, true);

    public static PathBoundaryValidator Posix() => new(false, true);

    public bool IsWithin(string physicalPath, string root)
    {
        if (physicalPath is null || root is null)
        {
            return false;
        }

        var fullPath = Normalized(physicalPath);
        var fullRoot = Normalized(root);

        if (HasTraversal(fullPath) || HasTraversal(fullRoot))
        {
            return false;
        }

        if (_resolveLinks)
        {
            // Resolver enlaces en CADA componente: un junction intermedio puede apuntar fuera.
            var resolvedRoot = ResolveAllLinks(fullRoot);
            var resolvedPath = ResolveAllLinks(fullPath);
            if (!resolvedRoot.Equals(fullRoot, StringComparison.Ordinal))
            {
                // La raíz misma es un enlace → su objetivo define la frontera.
                return IsWithin(resolvedPath, resolvedRoot);
            }

            fullPath = resolvedPath;
            fullRoot = resolvedRoot;
        }

        return _caseInsensitive
            ? fullPath.ToLowerInvariant().Equals(fullRoot.ToLowerInvariant(), StringComparison.Ordinal)
                || fullPath.ToLowerInvariant().StartsWith(
                    (fullRoot.ToLowerInvariant() + "/"), StringComparison.Ordinal)
            : fullPath.Equals(fullRoot, StringComparison.Ordinal)
                || fullPath.StartsWith(fullRoot + "/", StringComparison.Ordinal);
    }

    public bool IsSafeRelative(string relativePath)
    {
        if (relativePath is null || relativePath.Length == 0)
        {
            return false;
        }

        if (Path.IsPathFullyQualified(relativePath) || relativePath.StartsWith("/") || relativePath.StartsWith("\\"))
        {
            return false;
        }

        var norm = relativePath.Replace('\\', '/');
        if (norm.Contains("..") || norm.Contains(":"))
        {
            return false;
        }

        foreach (var seg in norm.Split('/'))
        {
            if (seg.Length == 0 || seg == "." || seg.StartsWith("~"))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Normaliza: GetFullPath + separadores a '/'.</summary>
    private static string Normalized(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Replace('\\', '/').TrimEnd('/');
    }

    private static bool HasTraversal(string norm)
    {
        foreach (var seg in norm.Split('/'))
        {
            if (seg == "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resuelve todos los componentes que sean symlinks/junctions reescribiendo la ruta con
    /// los objetivos reales. Los directorios intermedios pueden ser enlaces que escapan de la
    /// frontera; resolverlos previa comparación evita el bypass (P1-12).
    /// </summary>
    private static string ResolveAllLinks(string fullPath)
    {
        // Reconstruye la ruta nativa (separadores de plataforma) y resuelve el enlace del
        // primer componente que sea symlink/junction; reescribe el prefijo con su objetivo.
        var native = fullPath.Replace('/', _windows ? '\\' : '/');
        var separator = _windows ? '\\' : '/';
        var segments = native.Contains(separator) ? native.Split(separator) : new string[] { native };
        var cumulative = "";
        var resolved = native;
        foreach (var seg in segments)
        {
            if (seg.Length == 0)
            {
                continue;
            }

            cumulative = cumulative.Length == 0 ? seg : cumulative + separator + seg;
            var linkTarget = ResolveFinalLink(cumulative);
            if (linkTarget is not null && !linkTarget.Equals(cumulative, StringComparison.Ordinal))
            {
                resolved = linkTarget + native.Substring(cumulative.Length);
                break;
            }
        }

        return Normalized(resolved);
    }

    private static string? ResolveFinalLink(string native)
    {
        if (!File.Exists(native) && !Directory.Exists(native))
        {
            return null;
        }

        try
        {
            var fi = new FileInfo(native);
            var target = fi.ResolveLinkTarget(false);
            if (target is not null)
            {
                var targetPath = target!.FullName;
                if (targetPath is not null && targetPath!.Length > 0)
                {
                    return Path.GetFullPath(targetPath!);
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }
}