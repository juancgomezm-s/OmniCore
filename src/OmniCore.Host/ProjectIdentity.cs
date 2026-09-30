namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Execution;

/// <summary>Deriva la identidad de un repositorio sin ejecutar procesos Git (ADR-0022 §3, INV-008).</summary>
public static class ProjectIdentity
{
    /// <summary>Devuelve el ProjectId del repositorio en <paramref name="workspaceRoot"/>.</summary>
    public static ProjectId FromWorkspaceRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var boundary = new PathBoundaryValidator();
        var root = boundary.ResolvePhysical(Path.GetFullPath(workspaceRoot))
            ?? throw new ArgumentException("No se pudo resolver físicamente la raíz del proyecto.", nameof(workspaceRoot));

        var gitEntry = Path.Combine(root, ".git");
        if (!Directory.Exists(gitEntry) && !File.Exists(gitEntry))
        {
            return ProjectId.Derive(CanonicalPath(root));
        }

        var gitDirectory = ResolveGitDirectory(root, gitEntry);
        var commonDirectory = ResolveCommonDirectory(gitDirectory);
        var physicalCommonDirectory = boundary.ResolvePhysical(commonDirectory)
            ?? throw new ArgumentException("No se pudo resolver físicamente el git-common-dir.", nameof(workspaceRoot));
        var origin = ReadOrigin(Path.Combine(physicalCommonDirectory, "config"));
        return ProjectId.Derive(origin is null ? CanonicalPath(physicalCommonDirectory) : NormalizeOrigin(origin));
    }

    /// <summary>Resuelve la ruta física que se usa para la identidad y almacenamiento del workspace.</summary>
    public static string ResolvePhysicalWorkspaceRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return new PathBoundaryValidator().ResolvePhysical(Path.GetFullPath(workspaceRoot))
            ?? throw new ArgumentException("No se pudo resolver físicamente la raíz del workspace.", nameof(workspaceRoot));
    }

    private static string ResolveGitDirectory(string root, string gitEntry)
    {
        if (Directory.Exists(gitEntry))
        {
            return Path.GetFullPath(gitEntry);
        }

        var line = File.ReadLines(gitEntry).FirstOrDefault(static value =>
            value.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            throw new ArgumentException("El archivo .git no contiene una referencia gitdir válida.", nameof(root));
        }

        var target = line["gitdir:".Length..].Trim();
        if (target.Length == 0)
        {
            throw new ArgumentException("El archivo .git contiene una referencia gitdir vacía.", nameof(root));
        }

        return Path.GetFullPath(Path.IsPathFullyQualified(target) ? target : Path.Combine(root, target));
    }

    private static string ResolveCommonDirectory(string gitDirectory)
    {
        var commonFile = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(commonFile))
        {
            return gitDirectory;
        }

        var target = File.ReadAllText(commonFile).Trim();
        if (target.Length == 0)
        {
            throw new ArgumentException("El archivo commondir está vacío.", nameof(gitDirectory));
        }

        return Path.GetFullPath(Path.IsPathFullyQualified(target) ? target : Path.Combine(gitDirectory, target));
    }

    private static string? ReadOrigin(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        var inOriginSection = false;
        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inOriginSection = line.StartsWith("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOriginSection || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0 || !line[..separator].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>Normaliza transporte, host, credenciales y sufijo .git a una identidad host/ruta.</summary>
    public static string NormalizeOrigin(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        var value = origin.Trim();
        string host;
        string path;

        // Forma SCP de Git: git@host:org/repo. Solo se trata como SCP si no parece una URL.
        var scpSeparator = value.IndexOf(':');
        var slash = value.IndexOf('/');
        if (!value.Contains("://", StringComparison.Ordinal) && scpSeparator > 0
            && (slash < 0 || scpSeparator < slash))
        {
            var at = value.LastIndexOf('@', scpSeparator);
            host = value[(at + 1)..scpSeparator];
            path = value[(scpSeparator + 1)..];
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return "file/" + CanonicalPath(uri.LocalPath);
            }

            host = uri.IdnHost;
            path = uri.AbsolutePath;
        }
        else
        {
            throw new ArgumentException("El origin Git no es una URL reconocida.", nameof(origin));
        }

        host = host.Trim().ToLowerInvariant();
        path = Uri.UnescapeDataString(path).Replace('\\', '/').Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        if (host.Length == 0 || path.Length == 0)
        {
            throw new ArgumentException("El origin Git debe incluir host y ruta de repositorio.", nameof(origin));
        }

        return host + "/" + path;
    }

    private static string CanonicalPath(string path)
    {
        var canonical = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        return OperatingSystem.IsWindows() ? canonical.ToLowerInvariant() : canonical;
    }
}
