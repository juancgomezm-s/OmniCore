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
public sealed class SystemProcessRuntime : IProcessRuntime, IStreamingProcessRuntime, IProcessRuntimeFactory
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

    public static IProcessRuntime Instance(IEnumerable<string>? additionalEnvironmentAllowlist = null) =>
        new SystemProcessRuntime(additionalEnvironmentAllowlist);

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

        if (launch.CaptureOutput || launch.DiscardOutput)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        if (launch.InitialStandardInput is not null) psi.RedirectStandardInput = true;

        if (OperatingSystem.IsWindows()) psi.CreateNewProcessGroup = true;

        var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Fallo al lanzar: " + launch.Executable);
        }

        if (launch.InitialStandardInput is { } initialInput)
        {
            if (initialInput.Length > 262_144)
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw new ArgumentException("Initial process input exceeds the size limit.", nameof(launch));
            }
            try
            {
                if (initialInput.Length > 0)
                {
                    process.StandardInput.Write(initialInput);
                    process.StandardInput.WriteLine();
                }
                process.StandardInput.Close();
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
                throw;
            }
        }

        if (launch.DiscardOutput)
        {
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        _alive[process.Id] = process;
        return new ProcessHandle(process.Id, this);
    }

    private static StringComparer EnvironmentComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison EnvironmentComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsValidEnvironmentName(string name) => !string.IsNullOrWhiteSpace(name)
        && !name.Contains('=') && !name.Any(char.IsControl);

    private void CopyAllowedParentEnvironment(IDictionary<string, string?> target)
    {
        var allowed = new HashSet<string>(EnvironmentComparer)
        {
            "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "LANG",
        };
        if (OperatingSystem.IsWindows()) allowed.Add("APPDATA");
        foreach (var extra in _additionalEnvironmentAllowlist) allowed.Add(extra);

        foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
        {
            if (pair.Key is not string name || pair.Value is not string value) continue;
            var configured = allowed.Contains(name)
                || name.StartsWith("LC_", EnvironmentComparison)
                || name.StartsWith("DOTNET_", EnvironmentComparison)
                || name.StartsWith("NUGET_", EnvironmentComparison);
            if (!configured) continue;
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
        var stdoutTask = System.Threading.Tasks.Task.Run<string?>(() => SafeRead(process.StandardOutput, 1_048_576));
        var stderrTask = System.Threading.Tasks.Task.Run<string?>(() => SafeRead(process.StandardError, 1_048_576));

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

    public async Task<ProcessResult> WaitStreamingAsync(ProcessHandle handle, TimeSpan timeout,
        Func<Stream, CancellationToken, Task> consumeStandardOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumeStandardOutput);
        if (!_alive.TryGetValue(handle.Pid, out var process))
            return new ProcessResult(-1, null, null, true);
        if (!process.StartInfo.RedirectStandardOutput || !process.StartInfo.RedirectStandardError)
        {
            CancelTree(handle);
            throw new InvalidOperationException("Streaming capture was not enabled for this process.");
        }

        using var timeoutSource = new CancellationTokenSource();
        timeoutSource.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stderrTask = DrainCappedAsync(process.StandardError.BaseStream, 1_048_576, CancellationToken.None);
        var stdoutTask = consumeStandardOutput(process.StandardOutput.BaseStream, linkedSource.Token);
        var waitTask = process.WaitForExitAsync(linkedSource.Token);
        var timedOut = false;
        try
        {
            var first = await Task.WhenAny(waitTask, stdoutTask).ConfigureAwait(false);
            if (first == stdoutTask && stdoutTask.IsFaulted) await stdoutTask.ConfigureAwait(false);
            try { await waitTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                KillTree(process);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            }
            if (!timedOut) await stdoutTask.ConfigureAwait(false);
            else
            {
                linkedSource.Cancel();
                await ObserveAsync(stdoutTask).ConfigureAwait(false);
            }
            var stderr = await stderrTask.ConfigureAwait(false);
            var exitCode = process.HasExited ? process.ExitCode : -1;
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return new ProcessResult(exitCode, null, stderr, timedOut);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            linkedSource.Cancel();
            KillTree(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            await ObserveAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(process.HasExited ? process.ExitCode : -1, null, stderr, true);
        }
        catch
        {
            linkedSource.Cancel();
            KillTree(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            await ObserveAsync(stdoutTask).ConfigureAwait(false);
            await ObserveAsync(stderrTask).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _alive.Remove(handle.Pid);
            try { process.Dispose(); } catch (Exception) { }
        }
    }

    private static async Task<string?> DrainCappedAsync(Stream stream, int cap, CancellationToken cancellationToken)
    {
        using var retained = new MemoryStream(Math.Min(cap, 8192));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var keep = Math.Min(read, cap - (int)retained.Length);
            if (keep > 0) retained.Write(buffer, 0, keep);
        }
        return retained.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(retained.GetBuffer(), 0, (int)retained.Length);
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            if (!process.HasExited) await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch (Exception) { }
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

    private static string? SafeRead(object reader, int cap)
    {
        var sr = reader as System.IO.StreamReader;
        if (sr is null) return null;
        try
        {
            // Keep draining after reaching the cap so the child cannot block on a full pipe,
            // while bounding memory regardless of how much it writes.
            var buffer = new char[8192];
            var output = new System.Text.StringBuilder(Math.Min(cap, 8192));
            while (true)
            {
                var read = sr.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                var keep = Math.Min(read, Math.Max(0, cap - output.Length));
                if (keep > 0) output.Append(buffer, 0, keep);
            }
            return output.ToString();
        }
        catch (Exception) { return null; }
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
