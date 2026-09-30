namespace OmniCore.Execution;

using System.Diagnostics;
using OmniCore.Abstractions;

/// <summary>
/// Implementación de IProcessRuntime sobre System.Diagnostics.Process (ADR-0038 §2, ADR-0011 §4).
/// Wait: drena stdout/stderr CONCURRENTEMENTE (Task.Run) para no bloquear si el hijo produce
/// mucho, espera el timeout real o la cancelación, mata el árbol (Kill(true)) si vence el
/// timeout y DESPUÉS recoge la salida (ya el proceso no existe → ReadToEnd no bloquea).
/// CancelTree mata el árbol completo (Windows process group, ADR-0038 §4).
/// </summary>
public sealed class SystemProcessRuntime : IProcessRuntime, IProcessRuntimeFactory
{
    private readonly Dictionary<int, Process> _alive = new();

    private readonly string[] _additionalEnvironmentAllowlist;

    public SystemProcessRuntime(IEnumerable<string>? additionalEnvironmentAllowlist = null)
    {
        var extras = additionalEnvironmentAllowlist?.ToArray() ?? Array.Empty<string>();
        if (extras.Any(name => !IsValidEnvironmentName(name)))
            throw new ArgumentException("La allowlist de entorno contiene un nombre inválido.",
                nameof(additionalEnvironmentAllowlist));
        _additionalEnvironmentAllowlist = extras.Distinct(EnvironmentComparer).ToArray();
    }

    public static IProcessRuntime Instance() => new SystemProcessRuntime();

    public IProcessRuntime Create() => new SystemProcessRuntime(_additionalEnvironmentAllowlist);

    public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo();
        psi.FileName = launch.Executable;
        // ProcessStartInfo starts as a copy of the parent environment. Clear it before adding
        // the ADR-0037 platform allowlist and the explicit launch delta (which may include a SecretRef).
        psi.UseShellExecute = false;
        psi.Environment.Clear();
        CopyAllowedParentEnvironment(psi.Environment);
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
        }

        psi.CreateNewProcessGroup = true;

        var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Fallo al lanzar: " + launch.Executable);
        }

        _alive[process.Id] = process;
        return new ProcessHandle(process.Id, this);
    }

    private static StringComparer EnvironmentComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsValidEnvironmentName(string name) => !string.IsNullOrWhiteSpace(name)
        && !name.Contains('=') && !name.Any(char.IsControl);

    private void CopyAllowedParentEnvironment(IDictionary<string, string?> target)
    {
        var allowed = new HashSet<string>(EnvironmentComparer)
        {
            "PATH",
        };
        if (OperatingSystem.IsWindows())
        {
            allowed.Add("SystemRoot");
            allowed.Add("WINDIR");
            allowed.Add("TEMP");
            allowed.Add("TMP");
        }
        else
        {
            allowed.Add("HOME");
            allowed.Add("LANG");
            allowed.Add("TMPDIR");
        }
        foreach (var extra in _additionalEnvironmentAllowlist) allowed.Add(extra);

        foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
        {
            if (pair.Key is not string name || pair.Value is not string value || !allowed.Contains(name)) continue;
            target[name] = value;
        }
    }

    public void CancelTree(ProcessHandle handle)
    {
        if (!_alive.TryGetValue(handle.Pid, out var process))
        {
            return;
        }

        KillTree(process);
        try
        {
            process.WaitForExit(30_000);
        }
        catch (Exception)
        {
        }

        _alive.Remove(handle.Pid);
        try
        {
            process.Dispose();
        }
        catch (Exception)
        {
        }
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

        // 1. Drenaje CONCURRENTE (no bloqueante): leer en dos tasks paralelos.
        var stdoutTask = System.Threading.Tasks.Task.Run<string?>(() => SafeRead(process.StandardOutput));
        var stderrTask = System.Threading.Tasks.Task.Run<string?>(() => SafeRead(process.StandardError));

        // 2. Espera del proceso real con timeout y cancelación.
        var completed = WaitForExitOrCancel(process, timeout, cancellationToken);

        // 3. Si venció y sigue vivo, matar el árbol y esperar a que muera.
        if (!completed)
        {
            KillTree(process);
            var deadline = DateTimeOffset.Now.AddSeconds(30);
            while (!process.HasExited && DateTimeOffset.Now < deadline)
            {
                process.WaitForExit(100);
            }
        }

        // 4. Ahora (proceso muerto) recogemos la salida: ReadToEnd finaliza y no bloquea.
        var stdout = WaitOrNull(stdoutTask);
        var stderr = WaitOrNull(stderrTask);
        var exitCode = process.HasExited ? process.ExitCode : -1;

        _alive.Remove(handle.Pid);
        try
        {
            process.Dispose();
        }
        catch (Exception)
        {
        }

        return new ProcessResult(exitCode, stdout, stderr, !completed);
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
                return process.HasExited;
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
        }
    }

    private static string? SafeRead(object reader)
    {
        var sr = reader as System.IO.StreamReader;
        if (sr is null)
        {
            return null;
        }

        try
        {
            var text = sr!.ReadToEnd();
            return text is null ? null : text!.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? WaitOrNull(System.Threading.Tasks.Task<string?> task)
    {
        try
        {
            if (task.IsCompleted)
            {
                return task.Result;
            }

            return task.Wait(5000) ? task.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}