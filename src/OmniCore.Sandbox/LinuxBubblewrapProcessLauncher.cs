using System.Diagnostics;

namespace OmniCore.Sandbox;

/// <summary>Extension transport in private mount, user, PID and network namespaces.
/// Missing kernel/bwrap support is a launch failure, never permission to run outside isolation.</summary>
public sealed class LinuxBubblewrapProcessLauncher : ISandboxProcessLauncher, IStrongSandboxPreflight
{
    public StrongSandboxPreflightResult CheckStrongAvailability(SandboxLaunchSpec launch) =>
        OperatingSystem.IsLinux() && File.Exists("/usr/bin/bwrap") && launch.Duplex
            && !launch.Terminal && launch.Network.Mode == SandboxNetworkMode.Deny
            ? StrongSandboxPreflightResult.Available : StrongSandboxPreflightResult.UnavailableBeforeLaunch;

    public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/bwrap"))
            throw new StrongSandboxUnavailableBeforeLaunchException("extension_linux_bubblewrap_required");
        if (launch.RequestedStrength != SandboxStrength.Strong || !launch.Duplex || launch.Terminal
            || launch.Network.Mode != SandboxNetworkMode.Deny || launch.InitialStandardInput is not null)
            throw new NotSupportedException("extension_sandbox_policy_unsupported");
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
        // Only OS runtime trees, never /home, /root, /etc, host /proc or workspace mounts.
        foreach (var runtime in new[] { "/usr", "/bin", "/sbin", "/lib", "/lib64" })
            if (Directory.Exists(runtime)) Add("--ro-bind", runtime, runtime);
        Add("--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp");
        foreach (var path in launch.AllowedPaths)
        {
            if (path.Access is not (SandboxPathAccess.Read or (SandboxPathAccess.Read | SandboxPathAccess.Write)))
                throw new ArgumentException("extension_sandbox_invalid_path_access");
            Add(path.Access.HasFlag(SandboxPathAccess.Write) ? "--bind" : "--ro-bind", path.Path, path.Path);
        }
        foreach (var variable in launch.Environment) Add("--setenv", variable.Key, variable.Value);
        Add("--chdir", launch.WorkingDirectory ?? "/", "--", launch.Executable);
        foreach (var argument in launch.Arguments) Add(argument);
        var process = Process.Start(start) ?? throw new IOException("extension_sandbox_start_failed");
        return ValueTask.FromResult<ISandboxProcessControl>(new Control(process));
    }

    private sealed class Control(Process process) : ISandboxPersistentProcessControl
    {
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public bool IsTerminal => false;
        public void ResizeTerminal(short columns, short rows) => throw new NotSupportedException("A pseudo-terminal is unavailable on this platform.");
        public ValueTask CloseInputAsync() { process.StandardInput.Close(); return ValueTask.CompletedTask; }
        public async ValueTask<int> WaitForExitCodeAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        public async ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode, []);
        }
        public async ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            process.Dispose();
        }
    }
}

public sealed class LinuxBubblewrapCapabilitiesProbe : ISandboxCapabilitiesProbe
{
    public SandboxCapabilities Probe()
    {
        var available = OperatingSystem.IsLinux() && File.Exists("/usr/bin/bwrap");
        return new SandboxCapabilities(available, true, true,
            available ? null : "Bubblewrap Strong isolation is unavailable on this Linux host.");
    }
}
