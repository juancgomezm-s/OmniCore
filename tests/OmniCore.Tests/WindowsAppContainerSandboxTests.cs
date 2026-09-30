namespace OmniCore.Tests;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OmniCore.Abstractions;
using OmniCore.Execution;
using OmniCore.Sandbox;

public sealed class WindowsAppContainerSandboxTests
{
    [Fact]
    public async Task Strong_process_reads_allowed_workspace_but_not_outside_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }

        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxTest-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxOutside-" + Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(workspace);
        const string insideSecret = "inside-readable";
        const string outsideSecret = "outside-denied";
        var inside = Path.Combine(workspace, "inside.txt");
        await File.WriteAllTextAsync(inside, insideSecret, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(outside, outsideSecret, TestContext.Current.CancellationToken);
        try
        {
            var helper = HelperExecutable();
            var launcher = new WindowsAppContainerProcessLauncher();
            var insideResult = await RunReadAsync(launcher, helper, workspace, inside);
            Assert.Equal(0, insideResult.ExitCode);
            Assert.Contains(insideSecret, OutputText(insideResult));

            var outsideResult = await RunReadAsync(launcher, helper, workspace, outside);
            Assert.Equal(23, outsideResult.ExitCode);
            Assert.Contains("DENIED", OutputText(outsideResult));
        }
        finally
        {
            TryDelete(workspace);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task Network_is_denied_when_the_launch_spec_has_no_network_capability()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }

        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxNetworkTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, HelperExecutable(),
                new[] { "connect", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                workspace, new Dictionary<string, string>(), new[]
                {
                    new SandboxAllowedPath(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)
                }, new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()), TimeSpan.FromSeconds(5));
            await using var process = await new WindowsAppContainerProcessLauncher()
                .StartAsync(launch, TestContext.Current.CancellationToken);
            var result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(24, result.ExitCode);
            Assert.Contains("DENIED", OutputText(result));
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public async Task Timeout_terminates_the_entire_AppContainer_job_tree()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }

        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxJobTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var pidFile = Path.Combine(workspace, "child.pid");
        try
        {
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, HelperExecutable(), new[] { "spawn", pidFile },
                workspace, new Dictionary<string, string>(), new[]
                {
                    new SandboxAllowedPath(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)
                }, new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()), TimeSpan.FromSeconds(2));
            await using var process = await new WindowsAppContainerProcessLauncher()
                .StartAsync(launch, TestContext.Current.CancellationToken);
            var result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(result.TimedOut, "The configured timeout should have killed the process.");
            Assert.True(File.Exists(pidFile), "The parent should have written its child process id before timeout.");
            var childPid = int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken));
            Assert.True(WaitUntilExited(childPid), "The Job Object must terminate descendants, not only the parent.");
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void AppContainer_profile_name_is_stable_per_workspace_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer profile naming is Windows-specific.");
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreProfileName");
        Assert.Equal(WindowsAppContainerProcessLauncher.GetProfileName(workspace),
            WindowsAppContainerProcessLauncher.GetProfileName(Path.Combine(workspace, ".")));
        Assert.NotEqual(WindowsAppContainerProcessLauncher.GetProfileName(workspace),
            WindowsAppContainerProcessLauncher.GetProfileName(workspace + "-other"));
    }

    [Fact]
    public async Task Strong_request_selects_the_available_launcher_without_starting_the_weak_runtime()
    {
        var runtime = new FakeProcessRuntime();
        var strong = new FakeSandboxLauncher();
        var selector = new PlatformSandboxProcessLauncher(runtime,
            new FixedCapabilitiesProbe(new SandboxCapabilities(true, true, true, null)), strong);
        var result = await selector.StartAsync(Spec(SandboxStrength.Strong), CancellationToken.None);
        Assert.Equal(1, strong.Starts);
        Assert.Equal(0, runtime.Starts);
        await result.DisposeAsync();
    }

    [Fact]
    public async Task Strong_request_is_not_silently_downgraded_when_unavailable()
    {
        var runtime = new FakeProcessRuntime();
        var selector = new PlatformSandboxProcessLauncher(runtime,
            new FixedCapabilitiesProbe(new SandboxCapabilities(false, true, true, "unavailable")),
            new FakeSandboxLauncher());
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await selector.StartAsync(Spec(SandboxStrength.Strong), CancellationToken.None));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task Weak_request_uses_the_runtime_launcher()
    {
        var runtime = new FakeProcessRuntime();
        var selector = new PlatformSandboxProcessLauncher(runtime,
            new FixedCapabilitiesProbe(new SandboxCapabilities(false, true, true, "unavailable")),
            new FakeSandboxLauncher());
        var process = await selector.StartAsync(Spec(SandboxStrength.Weak), CancellationToken.None);
        Assert.Equal(1, runtime.Starts);
        await process.DisposeAsync();
    }

    [Fact]
    public void Probe_reports_unavailability_with_a_reason_or_availability()
    {
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(capabilities.StrongAvailable);
            Assert.False(string.IsNullOrWhiteSpace(capabilities.StrongUnavailableReason));
            return;
        }

        if (!capabilities.StrongAvailable)
            Assert.False(string.IsNullOrWhiteSpace(capabilities.StrongUnavailableReason));
        else
            Assert.Null(capabilities.StrongUnavailableReason);
    }

    private static SandboxLaunchSpec Spec(SandboxStrength strength) => new(strength, "test", Array.Empty<string>(),
        Environment.CurrentDirectory, new Dictionary<string, string>(), Array.Empty<SandboxAllowedPath>(),
        new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()));

    private static async Task<SandboxProcessOutput> RunReadAsync(ISandboxProcessLauncher launcher,
        string helper, string workspace, string path)
    {
        var launch = new SandboxLaunchSpec(SandboxStrength.Strong, helper, new[] { "read", path }, workspace,
            new Dictionary<string, string>(), new[]
            {
                new SandboxAllowedPath(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)
            }, new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()), TimeSpan.FromSeconds(10));
        await using var process = await launcher.StartAsync(launch, TestContext.Current.CancellationToken);
        return await process.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    private static string HelperExecutable() => Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe");

    private static string OutputText(SandboxProcessOutput output) => string.Concat(output.Chunks.Select(chunk => chunk.Text));

    private static bool WaitUntilExited(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            Thread.Sleep(50);
        }

        return false;
    }

    private sealed class FixedCapabilitiesProbe(SandboxCapabilities value) : ISandboxCapabilitiesProbe
    {
        public SandboxCapabilities Probe() => value;
    }

    private sealed class FakeSandboxLauncher : ISandboxProcessLauncher
    {
        public int Starts { get; private set; }
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
        {
            Starts++;
            return ValueTask.FromResult<ISandboxProcessControl>(new FakeSandboxProcess());
        }
    }

    private sealed class FakeSandboxProcess : ISandboxProcessControl
    {
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SandboxProcessOutput(0, Array.Empty<SandboxOutputChunk>()));
        public ValueTask TerminateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProcessRuntime : IProcessRuntime
    {
        public int Starts { get; private set; }
        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            Starts++;
            return new ProcessHandle(1, this);
        }
        public void CancelTree(ProcessHandle handle) { }
        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(0, null, null, false);
        public bool IsAlive(ProcessHandle handle) => true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
