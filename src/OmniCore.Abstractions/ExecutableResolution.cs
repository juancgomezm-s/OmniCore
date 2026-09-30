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

/// <summary>
/// Typed failure: on Windows a <c>.bat</c>/<c>.cmd</c> always runs through <c>cmd.exe</c>, which re-parses
/// argv as shell text ("BatBadBut"). <c>process.exec</c> never does that (ADR-0015); use <c>shell.exec</c>.
/// </summary>
public sealed class ExecutableRequiresShellException : Exception
{
    public string Executable { get; }
    public ExecutableRequiresShellException(string executable)
        : base("Los scripts .bat/.cmd se interpretan con cmd.exe: usa shell.exec.") => Executable = executable;
}

/// <summary>Resolves names without searching the cwd/workspace (ADR-0037 §6).</summary>
public interface IExecutableResolver
{
    ExecutableResolution Resolve(string executable, string workspaceRoot);
}
