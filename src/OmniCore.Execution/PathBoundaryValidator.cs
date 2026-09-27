namespace OmniCore.Execution;

using OmniCore.Abstractions;

/// <summary>
/// Validador de frontera de rutas del workspace (ADR-0008 §7, ADR-0037 §1). La implementación
/// vive en Execution (varón el grafo); Security usa solo la interfaz. M2 canonicaliza con
/// Path.GetFullPath (colapsa . y .., resuelve relativos contra el cwd), unifica separadores a
/// '/' para comparar, detecta traversal y, con resolución activa, comprueba que el objetivo
/// final no escape de la raíz vía symlink/junction (FileInfo.ResolveLinkTarget). En Windows
/// compara sin importar mayúsculas. El sandbox real (AppContainer/Job) llega en M3.
/// </summary>
public sealed class PathBoundaryValidator : IPathBoundaryValidator
{
    private readonly bool _caseInsensitive;

    private readonly bool _resolveLinks;

    public PathBoundaryValidator()
    {
        _caseInsensitive = Path.IsPathFullyQualified("C:");
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

        // 1. Traversal: cualquier segmento '..' (tras GetFullPath ya colapsado) es sospechoso.
        if (HasTraversal(fullPath) || HasTraversal(fullRoot))
        {
            return false;
        }

        // 2. Enlaces: resolver symlinks/junctions del objetivo final; si la raíz es un enlace,
        //    la frontera real es su objetivo.
        if (_resolveLinks)
        {
            var resolvedRoot = ResolveFinalLink(fullRoot);
            if (!resolvedRoot.Equals(fullRoot, StringComparison.Ordinal))
            {
                return IsWithin(resolvedRoot, fullRoot);
            }

            fullPath = ResolveFinalLink(fullPath);
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

        // Absolutas (C:\…, /…, //host/…) nunca son "relative".
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

    /// <summary>Normaliza: GetFullPath (absoluto, colapsa . y ..) + separadores a '/'.</summary>
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

    /// <summary>Devuelve el objetivo real si la ruta final es un symlink/junction.</summary>
    private static string ResolveFinalLink(string fullPath)
    {
        var native = fullPath.Replace('/', '\\');
        if (!File.Exists(native) && !Directory.Exists(native))
        {
            return fullPath;
        }

        try
        {
            var fi = new FileInfo(native);
            var target = fi.ResolveLinkTarget(true);
            if (target is not null)
            {
                var targetPath = target!.FullName;
                if (targetPath is not null && targetPath!.Length > 0
                    && !targetPath!.Equals(fullPath, StringComparison.Ordinal)
                    && !targetPath!.Replace('\\', '/').Equals(fullPath, StringComparison.Ordinal))
                {
                    return Normalized(targetPath!);
                }
            }
        }
        catch (Exception)
        {
            // no-enlace o sin permisos; tratar como ruta ordinaria
        }

        return fullPath;
    }
}