namespace OmniCore.Execution;

using System.Diagnostics;
using OmniCore.Abstractions;

/// <summary>
/// Implementación de IProcessRuntime sobre System.Diagnostics.Process (ADR-0038 §2, ADR-0011 §4).
/// Comportamiento de M2: lanza con grupo de procesos propio, espera con timeout real y
/// cancela por CancellationToken, y al cancelar/timeout mata el árbol completo con
/// Kill(true) (Windows: process group; el proceso no se descarta hasta que termina de verdad).
/// El drenaje de stdout/stderr ocurre al terminar (los streams ya están cerrados, read no
/// bloquea). El confinamiento de capabilities (Job/AppContainer, bubblewrap) llega en M3.
/// </summary>
public sealed class SystemProcessRuntime : IProcessRuntime, IProcessRuntimeFactory
{
    private readonly Dictionary<int, Process> _alive = new();

    public static IProcessRuntime Instance() => new SystemProcessRuntime();

    public IProcessRuntime Create() => new SystemProcessRuntime();

    public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo();
        psi.FileName = launch.Executable;
        foreach (var arg in launch.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (launch.WorkingDirectory is not null && launch.WorkingDirectory.Length > 0)
        {
            psi.WorkingDirectory = launch.WorkingDirectory;
        }

        foreach (var kv in launch.Environment)
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        if (launch.CaptureOutput)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
        }

        // Grupo de procesos propio: Kill(true) mata todo el árbol en Windows (ADR-0038 §4).
        psi.CreateNewProcessGroup = true;

        var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Fallo al lanzar: " + launch.Executable);
        }

        _alive[process.Id] = process;
        return new ProcessHandle(process.Id, this);
    }

    public void CancelTree(ProcessHandle handle)
    {
        if (!_alive.TryGetValue(handle.Pid, out var process))
        {
            return;
        }

        KillTree(process);
    }

    public bool IsAlive(ProcessHandle handle)
    {
        if (!_alive.TryGetValue(handle.Pid, out var process))
        {
            return false;
        }

        return !process.HasExited;
    }

    public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_alive.TryGetValue(handle.Pid, out var process))
        {
            return new ProcessResult(-1, null, null, true);
        }

        var timedOut = !WaitForExitOrCancel(process, timeout, cancellationToken);
        var stdout = SafeRead(process.StandardOutput);
        var stderr = SafeRead(process.StandardError);
        var exitCode = process.HasExited && !timedOut ? process.ExitCode : -1;

        // El tiempo de timeout se consumió: el árbol muere aunque el wait haya terminado, y
        // NO se devuelve hasta que el proceso salió (evita colgar el journal en el resume).
        if (timedOut)
        {
            KillTree(process);
            try
            {
                process.WaitForExit(60_000);
            }
            catch (Exception)
            {
            }
        }

        _alive.Remove(handle.Pid);
        try
        {
            process.Dispose();
        }
        catch (Exception)
        {
        }

        return new ProcessResult(exitCode, stdout, stderr, timedOut);
    }

    private static bool WaitForExitOrCancel(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadlineMs = Math.Max(1L, (long) timeout.TotalMilliseconds);
        var startMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                return true;
            }

            var elapsed = DateTimeOffset.Now.ToUnixTimeMilliseconds() - startMs;
            if (elapsed >= deadlineMs)
            {
                return false;
            }

            var remainMs = deadlineMs - elapsed;
            try
            {
                var done = process.WaitForExit((int) Math.Min(remainMs, 200L));
                if (done)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                if (process.HasExited)
                {
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception)
        {
            // ya salió o sin permisos; no es fatal
        }
    }

    private static string? SafeRead(StreamReader? reader)
    {
        if (reader is null)
        {
            return null;
        }

        try
        {
            var text = reader!.ReadToEnd();
            return text is null ? null : text!.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}