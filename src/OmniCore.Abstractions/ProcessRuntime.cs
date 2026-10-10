namespace OmniCore.Abstractions;

/// <summary>
/// IProcessRuntime mínimo (ADR-0038 §2, ADR-0011 §4): se requiere en M2 para controlar el
/// árbol de procesos del servidor local (managed). El confinamiento (filesystem/red) llega en M3.
/// </summary>
public interface IProcessRuntime
{
    /// <summary>Lanza un proceso con el árbol bajo control del runtime.</summary>
    ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken);

    /// <summary>Cancela el árbol de procesos (graceful → kill tree).</summary>
    void CancelTree(ProcessHandle handle);

    /// <summary>Espera la salida y devuelve el resultado.</summary>
    ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>True mientras el proceso siga vivo (supervisión sin consumir el handle).</summary>
    bool IsAlive(ProcessHandle handle);
}

/// <summary>Información de lanzamiento de un proceso.</summary>
public sealed class ProcessLaunch
{
    public string Executable { get; }

    public IReadOnlyList<string> Args { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public bool CaptureOutput { get; }

    /// <summary>
    /// Proceso de larga vida que nadie espera (un servidor local): su salida se drena y descarta
    /// mientras corre. Sin drenarla, el buffer de la tubería se llena y el proceso se bloquea al escribir.
    /// </summary>
    public bool DiscardOutput { get; init; }

    public ProcessLaunch(string executable, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, bool captureOutput)
    {
        Executable = executable;
        Args = args;
        WorkingDirectory = workingDirectory;
        Environment = environment;
        CaptureOutput = captureOutput;
    }
}

/// <summary>Handle opaco de un proceso en ejecución.</summary>
public sealed class ProcessHandle
{
    public int Pid { get; }

    public IProcessRuntime Runtime { get; }

    public ProcessHandle(int pid, IProcessRuntime runtime)
    {
        Pid = pid;
        Runtime = runtime;
    }
}

/// <summary>Resultado de un proceso terminado.</summary>
public sealed class ProcessResult
{
    public int ExitCode { get; }

    public string? Stdout { get; }

    public string? Stderr { get; }

    public bool TimedOut { get; }

    public ProcessResult(int exitCode, string? stdout, string? stderr, bool timedOut)
    {
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        TimedOut = timedOut;
    }
}

/// <summary>Fábrica del runtime de procesos para el Host.</summary>
public interface IProcessRuntimeFactory
{
    IProcessRuntime Create();
}