namespace OmniCore.Execution;

using OmniCore.Abstractions;

/// <summary>
/// Resolución multiplataforma para process.exec. El PATH se filtra físicamente para excluir el
/// cwd y todo el workspace; una ruta relativa con separadores dentro del workspace se rechaza.
/// </summary>
public sealed class SystemExecutableResolver : IExecutableResolver
{
    private readonly string[] _pathEntries;
    private readonly string _currentDirectory;
    private readonly bool _allowWorkspaceRelativePaths;

    public SystemExecutableResolver(IEnumerable<string>? pathEntries = null, string? currentDirectory = null,
        bool allowWorkspaceRelativePaths = false)
    {
        _pathEntries = (pathEntries ?? (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        _currentDirectory = Path.GetFullPath(currentDirectory ?? Environment.CurrentDirectory);
        _allowWorkspaceRelativePaths = allowWorkspaceRelativePaths;
    }

    public ExecutableResolution Resolve(string executable, string workspaceRoot)
    {
        try { return ResolveCore(executable, workspaceRoot); }
        catch (ExecutableNotFoundException) { throw; }
        catch (ExecutableRequiresShellException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ExecutableNotFoundException(executable ?? string.Empty); }
    }

    private ExecutableResolution ResolveCore(string executable, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new ExecutableNotFoundException(executable ?? string.Empty);
        var workspace = Path.GetFullPath(workspaceRoot);
        var hasSeparator = executable.IndexOfAny(new[] { '/', '\\' }) >= 0;
        if (Path.IsPathRooted(executable))
        {
            var absolute = Path.GetFullPath(executable);
            if (IsWithin(absolute, workspace) && !_allowWorkspaceRelativePaths)
                throw new ExecutableNotFoundException(executable);
            if (File.Exists(absolute)) return Checked(executable, absolute);
            throw new ExecutableNotFoundException(executable);
        }

        if (hasSeparator)
        {
            if (!_allowWorkspaceRelativePaths)
                throw new ExecutableNotFoundException(executable);
            var relative = Path.GetFullPath(Path.Combine(workspace, executable));
            if (File.Exists(relative)) return Checked(executable, relative);
            throw new ExecutableNotFoundException(executable);
        }

        foreach (var entry in _pathEntries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue; // empty PATH segment means cwd: never search it
            string directory;
            try { directory = Path.GetFullPath(entry); }
            catch (ArgumentException) { continue; }
            if (SamePath(directory, _currentDirectory) || IsWithin(directory, workspace)) continue;
            foreach (var candidate in CandidateNames(executable))
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return Checked(executable, Path.GetFullPath(path));
            }
        }
        throw new ExecutableNotFoundException(executable);
    }

    /// <summary>Un .bat/.cmd en Windows pasaría por cmd.exe, que re-interpreta los argumentos.</summary>
    private static ExecutableResolution Checked(string requested, string resolved)
    {
        var extension = Path.GetExtension(resolved);
        if (OperatingSystem.IsWindows() && (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ExecutableRequiresShellException(requested);
        }

        return new ExecutableResolution(requested, resolved);
    }

    private static IEnumerable<string> CandidateNames(string executable)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(executable))
        {
            yield return executable;
            yield break;
        }
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var extension in extensions)
        {
            if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) continue;
            yield return executable + extension;
        }
        yield return executable;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a),
        Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
