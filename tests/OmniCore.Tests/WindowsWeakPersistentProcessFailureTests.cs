namespace OmniCore.Tests;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OmniCore.Sandbox;
using Task = System.Threading.Tasks.Task;

[SupportedOSPlatform("windows")]
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class WindowsWeakPersistentProcessFailureTests
{
    [Fact]
    public async Task Dispose_query_failure_closes_owned_job_before_hpc_on_and_kills_descendant()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var root = NewRoot();
        var marker = Path.Combine(root, "orphan-marker.txt");
        var hooks = new FaultingHooks(WeakHookFailure.Query);
        var control = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await new WindowsWeakPersistentProcessLauncher(hooks)
            .StartAsync(Spec("spawn-delayed-marker", marker, root), TestContext.Current.CancellationToken));
        try
        {
            using var output = new StreamReader(control.StandardOutput, new UTF8Encoding(false, true), leaveOpen: true);
            var descendantId = await ReadDescendantIdAsync(output);
            var error = await Assert.ThrowsAsync<IOException>(async () => await control.DisposeAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal("fixture_query_failure", error.Message);
            Assert.True(hooks.ObservedJob is { IsClosed: true }, "Dispose must close the owned kill-on-close Job after query failure.");
            await WaitForExitAsync(descendantId, TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(marker), "The descendant must be killed even when member enumeration fails.");
        }
        finally
        {
            await control.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Dispose_terminate_failure_closes_job_before_conpty_teardown_without_deadlock()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows ConPTY integration test."); return; }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        { Assert.Skip("ConPTY requires Windows 10 version 1809 or newer."); return; }
        var hooks = new FaultingHooks(WeakHookFailure.Terminate);
        var launch = Spec("terminal-echo", null, Environment.CurrentDirectory) with
        {
            Arguments = ["terminal-echo"]
        };
        launch = launch with { Terminal = true };
        var control = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await new WindowsWeakPersistentProcessLauncher(hooks)
            .StartAsync(launch, TestContext.Current.CancellationToken));
        using var output = new StreamReader(control.StandardOutput, new UTF8Encoding(false), leaveOpen: true);
        var disposeAttempted = false;
        try
        {
            Assert.Contains("READY", await output.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), StringComparison.Ordinal);
            disposeAttempted = true;
            var error = await Assert.ThrowsAsync<IOException>(async () => await control.DisposeAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
            Assert.Equal("fixture_terminate_failure", error.Message);
            Assert.True(hooks.ObservedJob is { IsClosed: true }, "Dispose must close Job Object before closing ConPTY.");
        }
        finally
        {
            if (!disposeAttempted)
            {
                try { await control.DisposeAsync(); }
                catch (IOException) { }
            }
        }
    }

    [Fact]
    public async Task Dispose_wait_failure_still_releases_owned_handles_after_job_termination()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var root = NewRoot();
        var hooks = new FaultingHooks(WeakHookFailure.ProcessWait);
        var control = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await new WindowsWeakPersistentProcessLauncher(hooks)
            .StartAsync(Spec("spawn-delayed-marker", Path.Combine(root, "orphan-marker.txt"), root), TestContext.Current.CancellationToken));
        var disposeAttempted = false;
        try
        {
            using var output = new StreamReader(control.StandardOutput, new UTF8Encoding(false, true), leaveOpen: true);
            _ = await ReadDescendantIdAsync(output);
            disposeAttempted = true;
            var error = await Assert.ThrowsAsync<IOException>(async () => await control.DisposeAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
            Assert.Equal("fixture_wait_failure", error.Message);
            Assert.True(hooks.ObservedJob is { IsClosed: true });
            Assert.True(hooks.ObservedProcess is { IsClosed: true });
            Assert.True(hooks.ObservedInput is { IsClosed: true });
            Assert.True(hooks.ObservedStdout is { IsClosed: true });
        }
        finally
        {
            if (!disposeAttempted)
            {
                try { await control.DisposeAsync(); }
                catch (IOException) { }
            }
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Control_factory_failure_after_resume_returns_unknown_and_closes_owned_tree()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var root = NewRoot();
        var marker = Path.Combine(root, "orphan-marker.txt");
        var hooks = new FaultingHooks(WeakHookFailure.ControlFactory);
        try
        {
            var error = await Assert.ThrowsAsync<ProcessLaunchOutcomeUnknownException>(async () =>
                await new WindowsWeakPersistentProcessLauncher(hooks).StartAsync(
                    Spec("spawn-delayed-marker", marker, root), TestContext.Current.CancellationToken));
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.NotNull(hooks.DescendantProcessId);
            Assert.True(hooks.ObservedJob is { IsClosed: true }, "A control-construction failure must close the resumed process Job.");
            await WaitForExitAsync(hooks.DescendantProcessId!.Value, TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(marker));
        }
        finally { Cleanup(root); }
    }

    private static SandboxLaunchSpec Spec(string command, string? argument, string workingDirectory) =>
        new(SandboxStrength.Weak, Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe"),
            argument is null ? [command] : [command, argument], workingDirectory, new Dictionary<string, string>(), [],
            new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(30)) { Duplex = true };

    private static async Task<int> ReadDescendantIdAsync(StreamReader output)
    {
        var line = await output.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.StartsWith("DESCENDANT:", line, StringComparison.Ordinal);
        return int.Parse(line!["DESCENDANT:".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!HasExited(processId)) await Task.Delay(25, deadline.Token);
    }

    private static bool HasExited(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniWeakDisposeFailure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private enum WeakHookFailure { Query, Terminate, ProcessWait, ControlFactory }

    private sealed class FaultingHooks(WeakHookFailure failure) : IWindowsWeakProcessHooks
    {
        public SafeFileHandle? ObservedJob { get; private set; }
        public SafeProcessHandle? ObservedProcess { get; private set; }
        public SafeFileHandle? ObservedInput { get; private set; }
        public SafeFileHandle? ObservedStdout { get; private set; }
        public int? DescendantProcessId { get; private set; }

        public IReadOnlyList<uint> QueryJobProcessIds(SafeFileHandle job)
        {
            ObservedJob = job;
            if (failure == WeakHookFailure.Query) throw new IOException("fixture_query_failure");
            return AppContainerNative.QueryJobProcessIds(job);
        }

        public bool TerminateJobObject(SafeFileHandle job, uint exitCode)
        {
            ObservedJob = job;
            if (failure == WeakHookFailure.Terminate) throw new IOException("fixture_terminate_failure");
            return AppContainerNative.TerminateJobObject(job, exitCode);
        }

        public uint WaitForProcess(SafeProcessHandle process, uint milliseconds)
        {
            ObservedProcess = process;
            if (failure == WeakHookFailure.ProcessWait) throw new IOException("fixture_wait_failure");
            return AppContainerNative.WaitForSingleObject(process, milliseconds);
        }

        public ISandboxPersistentProcessControl CreateControl(SafeProcessHandle process, SafeFileHandle job,
            SafeFileHandle input, SafeFileHandle stdout, SafeFileHandle? stderr,
            SafePseudoConsoleHandle? pseudoConsole, bool terminal)
        {
            ObservedJob = job;
            ObservedProcess = process;
            ObservedInput = input;
            ObservedStdout = stdout;
            if (failure == WeakHookFailure.ControlFactory)
            {
                using var stream = new FileStream(stdout, FileAccess.Read, 4096, isAsync: false);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                var line = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Assert.StartsWith("DESCENDANT:", line, StringComparison.Ordinal);
                DescendantProcessId = int.Parse(line!["DESCENDANT:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                throw new InvalidOperationException("fixture_control_factory_failure_after_resume");
            }
            return new WindowsWeakPersistentProcessControl(process, job, input, stdout, stderr, pseudoConsole, terminal, this);
        }
    }
}
