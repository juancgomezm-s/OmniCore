namespace OmniCore.Abstractions;

/// <summary>Resolution result for a process executable (ADR-0015/ADR-0037 §6).</summary>
public sealed class ExecutableResolution
{
    public string Requested { get; }
    public string ResolvedPath { get; }

    public ExecutableResolution(string requested, string resolvedPath)
    {
        Requested = requested;
        ResolvedPath = resolvedPath;
    }
}

/// <summary>Typed failure: no executable was found in the permitted search locations.</summary>
public sealed class ExecutableNotFoundException : Exception
{
    public string Executable { get; }
    public ExecutableNotFoundException(string executable)
        : base("No se encontró un ejecutable en las ubicaciones permitidas.") => Executable = executable;
}

/// <summary>Resolves names without searching the cwd/workspace (ADR-0037 §6).</summary>
public interface IExecutableResolver
{
    ExecutableResolution Resolve(string executable, string workspaceRoot);
}
