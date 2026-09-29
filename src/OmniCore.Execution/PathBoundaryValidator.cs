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
            // Resolver enlaces en CADA componente y de forma iterativa: un enlace puede apuntar
            // a otro enlace (cadena) o a un directorio que contiene enlaces que escapan.
            var resolvedRoot = ResolveAllLinks(fullRoot);
            var resolvedPath = ResolveAllLinks(fullPath);
            if (resolvedRoot is null || resolvedPath is null)
            {
                // Ciclo o profundidad excesiva: no se puede demostrar que quede dentro.
                return false;
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

    public string? ResolvePhysical(string path)
    {
        if (path is null || path.Length == 0)
        {
            return null;
        }

        var normalized = Normalized(path);
        return _resolveLinks ? ResolveAllLinks(normalized) : normalized;
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

    /// <summary>Máximo de enlaces a seguir antes de declarar la ruta irresoluble (evita ciclos).</summary>
    private const int MaxLinkHops = 40;

    /// <summary>
    /// Resuelve todos los componentes que sean symlinks/junctions reescribiendo la ruta con
    /// los objetivos reales, y repite hasta que ningún componente sea un enlace: un enlace
    /// puede apuntar a otro enlace, o a un directorio que contiene enlaces que escapan de la
    /// frontera. Devuelve null ante un ciclo o una cadena demasiado larga.
    /// </summary>
    private static string? ResolveAllLinks(string fullPath)
    {
        var current = Normalized(fullPath);
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            var next = ResolveFirstLink(current);
            if (next is null)
            {
                return current;
            }

            current = next;
        }

        return null;
    }

    /// <summary>
    /// Reescribe la ruta sustituyendo el PRIMER componente que sea un enlace por su objetivo, o
    /// devuelve null si ningún componente es un enlace.
    /// </summary>
    private static string? ResolveFirstLink(string normalizedPath)
    {
        var native = Path.GetFullPath(normalizedPath);
        var root = Path.GetPathRoot(native) ?? "";
        var parts = native.Substring(root.Length).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            var linkTarget = ResolveFinalLink(current);
            if (linkTarget is not null && !Normalized(linkTarget).Equals(Normalized(current), StringComparison.Ordinal))
            {
                var rest = string.Join(Path.DirectorySeparatorChar, parts, i + 1, parts.Length - i - 1);
                return Normalized(rest.Length == 0 ? linkTarget : Path.Combine(linkTarget, rest));
            }
        }

        return null;
    }

    /// <summary>
    /// Objetivo inmediato del enlace en <paramref name="native"/> (symlink o junction), resuelto a
    /// ruta absoluta, o null si no es un enlace. Funciona también con enlaces rotos: un enlace
    /// cuyo destino todavía no existe sigue apuntando fuera y debe tratarse como tal.
    /// </summary>
    private static string? ResolveFinalLink(string native)
    {
        try
        {
            var info = new FileInfo(native);
            var target = info.LinkTarget;
            if (target is null || target.Length == 0)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(native) ?? native;
            return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(parent, target));
        }
        catch (Exception)
        {
            return null;
        }
    }
}