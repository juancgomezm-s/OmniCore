using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmniCore.Sandbox;

/// <summary>Processes in private mount, user, PID and optionally network namespaces.
/// Missing kernel/bwrap support is a launch failure, never permission to run outside isolation.</summary>
public sealed class LinuxBubblewrapProcessLauncher : ISandboxProcessLauncher, IStrongSandboxPreflight
{
    public StrongSandboxPreflightResult CheckStrongAvailability(SandboxLaunchSpec launch) =>
        LinuxSandboxKernel.Available
            && !launch.Terminal && launch.Network.Mode is SandboxNetworkMode.Deny or SandboxNetworkMode.AllowAll
            && (!launch.Duplex || launch.InitialStandardInput is null)
            ? StrongSandboxPreflightResult.Available : StrongSandboxPreflightResult.UnavailableBeforeLaunch;

    public async ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/bwrap"))
            throw new StrongSandboxUnavailableBeforeLaunchException("extension_linux_bubblewrap_required");
        if (!LinuxSandboxKernel.Available)
            throw new StrongSandboxUnavailableBeforeLaunchException("linux_landlock_or_sandbox_helper_unavailable");
        if (launch.RequestedStrength != SandboxStrength.Strong || launch.Terminal
            || launch.Network.Mode is not (SandboxNetworkMode.Deny or SandboxNetworkMode.AllowAll)
            || launch.Duplex && launch.InitialStandardInput is not null)
            throw new NotSupportedException("linux_sandbox_policy_unsupported");
        if (launch.InitialStandardInput?.Length > 262_144 || launch.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid input size or process timeout.", nameof(launch));
        var executable = ResolveExecutable(launch.Executable, launch.WorkingDirectory);
        var start = new ProcessStartInfo("/usr/bin/bwrap")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = "/"
        };
        start.Environment.Clear();
        void Add(params string[] args) { foreach (var argument in args) start.ArgumentList.Add(argument); }
        Add("--unshare-all", "--unshare-user", "--disable-userns", "--new-session", "--die-with-parent",
            "--cap-drop", "ALL", "--clearenv");
        if (launch.Network.Mode == SandboxNetworkMode.AllowAll) Add("--share-net");
        // Only runtime trees and selected public identity files, never user homes,
        // credential directories, private /etc configuration or the host /proc.
        foreach (var runtime in new[] { "/usr", "/bin", "/sbin", "/lib", "/lib64" })
            if (Directory.Exists(runtime)) Add("--ro-bind", runtime, runtime);
        Add("--proc", "/proc", "--dev", "/dev", "--tmpfs", "/dev/shm", "--tmpfs", "/tmp");
        Add("--ro-bind", LinuxSandboxKernel.HelperPath, "/omnicore-linux-sandbox");
        foreach (var path in new[] { "/etc/os-release", "/etc/passwd", "/etc/group", "/etc/nsswitch.conf" })
            if (File.Exists(path)) Add("--ro-bind", path, path);
        Add("--dir", "/tmp/omnicore-home");
        // A user SDK needs its runtime directories, not the adjacent certificate store,
        // global tools or arbitrary private files. Other tools get only their executable;
        // callers must declare additional package/library paths explicitly.
        var executableDirectory = Path.GetDirectoryName(executable)!;
        if (!new[] { "/usr", "/bin", "/sbin", "/lib", "/lib64" }.Any(root => Within(executableDirectory, root))
            && !launch.AllowedPaths.Any(path => path.Access.HasFlag(SandboxPathAccess.Read)
                && Directory.Exists(path.Path) && Within(executableDirectory, Path.GetFullPath(path.Path))))
        {
            Add("--ro-bind", executable, executable);
            if (Path.GetFileName(executable) == "dotnet")
                foreach (var name in new[] { "host", "shared", "sdk", "packs", "sdk-manifests", "templates", "metadata" })
                {
                    var directory = Path.Combine(executableDirectory, name);
                    if (Directory.Exists(directory)) Add("--ro-bind", directory, directory);
                }
        }
        if (launch.Network.Mode == SandboxNetworkMode.AllowAll)
            foreach (var path in new[] { "/etc/resolv.conf", "/etc/hosts", "/etc/ssl/certs" })
                if (File.Exists(path) || Directory.Exists(path)) Add("--ro-bind", path, path);
        foreach (var path in launch.AllowedPaths)
        {
            if (path.Access is not (SandboxPathAccess.Read or (SandboxPathAccess.Read | SandboxPathAccess.Write)))
                throw new ArgumentException("extension_sandbox_invalid_path_access");
            Add(path.Access.HasFlag(SandboxPathAccess.Write) ? "--bind" : "--ro-bind", path.Path, path.Path);
        }
        Add("--setenv", "PATH", executableDirectory + ":/usr/local/bin:/usr/bin:/bin",
            "--setenv", "HOME", "/tmp/omnicore-home",
            "--setenv", "DOTNET_CLI_HOME", "/tmp/omnicore-home",
            "--setenv", "DOTNET_CLI_TELEMETRY_OPTOUT", "1",
            "--setenv", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "0",
            "--setenv", "DOTNET_GENERATE_ASPNET_CERTIFICATE", "false",
            "--setenv", "DOTNET_NOLOGO", "1");
        if (!launch.Duplex)
            Add("--setenv", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", "true",
                "--setenv", "DOTNET_CLI_USE_MSBUILD_SERVER", "0",
                "--setenv", "MSBUILDDISABLENODEREUSE", "1",
                "--setenv", "UseSharedCompilation", "false");
        foreach (var variable in launch.Environment) Add("--setenv", variable.Key, variable.Value);
        Add("--chdir", launch.WorkingDirectory ?? "/", "--", "/omnicore-linux-sandbox");
        foreach (var path in launch.AllowedPaths.Where(path => path.Access.HasFlag(SandboxPathAccess.Write)))
            Add("--write", path.Path);
        Add("--", executable);
        foreach (var argument in launch.Arguments) Add(argument);
        // PDEATHSIG observes the creating Linux thread, not the whole managed process.
        // A retiring ThreadPool thread must not kill a healthy sandbox after ~20 seconds.
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = Process.Start(start) ?? throw new IOException("linux_sandbox_start_failed");
                started.TrySetResult(child);
                child.WaitForExit();
            }
            catch (OperationCanceledException) { started.TrySetCanceled(cancellationToken); }
            catch (Exception error) { started.TrySetException(error); }
        }) { IsBackground = true, Name = "OmniCore Linux process owner" };
        owner.Start();
        var process = await started.Task.ConfigureAwait(false);
        return new Control(process, launch);
    }

    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    private static string ResolveExecutable(string executable, string? workingDirectory)
    {
        var candidate = Path.IsPathRooted(executable) ? executable
            : executable.Contains(Path.DirectorySeparatorChar)
                ? Path.GetFullPath(executable, workingDirectory ?? Environment.CurrentDirectory)
            : (Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin")
                .Split(Path.PathSeparator).Where(Path.IsPathRooted)
                .Select(directory => Path.Combine(directory, executable)).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("The executable is unavailable.");
        var file = new FileInfo(Path.GetFullPath(candidate));
        if (!file.Exists) throw new FileNotFoundException("The executable is unavailable.");
        return file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
    }

    private sealed class Control : ISandboxPersistentProcessControl
    {
        private readonly Process process;
        private readonly TimeSpan? _timeout;
        private readonly Task<string>? _stdout;
        private readonly Task<string>? _stderr;
        private readonly Task _input;
        private readonly Task<int> _exit;
        private readonly object _lifecycle = new();
        private bool _disposed;

        public Control(Process process, SandboxLaunchSpec launch)
        {
            this.process = process;
            _timeout = launch.Timeout;
            _exit = ObserveExitAsync(process);
            if (!launch.Duplex)
            {
                _stdout = process.StandardOutput.ReadToEndAsync();
                _stderr = process.StandardError.ReadToEndAsync();
                _input = FeedInputAsync(launch.InitialStandardInput);
            }
            else _input = Task.CompletedTask;
        }

        private static async Task<int> ObserveExitAsync(Process process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }

        private async Task FeedInputAsync(string? input)
        {
            try
            {
                if (input is not null) await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException) { } // The child may exit without consuming its input.
        }

        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public bool IsTerminal => false;
        public void ResizeTerminal(short columns, short rows) => throw new NotSupportedException("A pseudo-terminal is unavailable on this platform.");
        public ValueTask CloseInputAsync() { process.StandardInput.Close(); return ValueTask.CompletedTask; }
        public async ValueTask<int> WaitForExitCodeAsync(CancellationToken cancellationToken)
        {
            return await _exit.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_timeout is { } timeout) deadline.CancelAfter(timeout);
            var timedOut = false;
            try { await _exit.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                timedOut = true;
            }
            await _input.ConfigureAwait(false);
            var chunks = new List<SandboxOutputChunk>();
            if (_stdout is not null && await _stdout.ConfigureAwait(false) is { Length: > 0 } stdout)
                chunks.Add(new(SandboxOutputStream.StandardOutput, stdout));
            if (_stderr is not null && await _stderr.ConfigureAwait(false) is { Length: > 0 } stderr)
                chunks.Add(new(SandboxOutputStream.StandardError, stderr));
            return new(await _exit.ConfigureAwait(false), chunks, timedOut);
        }
        public async ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            // The session monitor may dispose the control as soon as it observes exit,
            // while a native stop is still unwinding. Retain one observed exit result
            // and serialize Process access with disposal; never query a disposed handle.
            lock (_lifecycle)
            {
                if (!_disposed)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
            }
            await _exit.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                await _input.ConfigureAwait(false);
                if (_stdout is not null) await _stdout.ConfigureAwait(false);
                if (_stderr is not null) await _stderr.ConfigureAwait(false);
            }
            finally
            {
                lock (_lifecycle)
                {
                    if (!_disposed) { process.Dispose(); _disposed = true; }
                }
            }
        }
    }
}

public sealed class LinuxBubblewrapCapabilitiesProbe : ISandboxCapabilitiesProbe
{
    public SandboxCapabilities Probe()
    {
        var available = LinuxSandboxKernel.Available;
        return new SandboxCapabilities(available, true, true,
            available ? null : "Strong isolation requires bubblewrap, Landlock and the installed OmniCore sandbox helper.");
    }
}

internal static partial class LinuxSandboxKernel
{
    internal static string HelperPath => Path.Combine(AppContext.BaseDirectory, "omnicore-linux-sandbox");
    internal static bool Available => OperatingSystem.IsLinux() && File.Exists("/usr/bin/bwrap")
        && File.Exists(HelperPath) && LandlockVersion(444, 0, 0, 1) >= 1;

    [LibraryImport("libc", EntryPoint = "syscall")]
    private static partial nint LandlockVersion(nint syscallNumber, nint attributes, nuint size, uint flags);
}
