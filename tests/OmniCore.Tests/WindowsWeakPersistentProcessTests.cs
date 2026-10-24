namespace OmniCore.Tests;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Sandbox;
using Task = System.Threading.Tasks.Task;

[SupportedOSPlatform("windows")]
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class WindowsWeakPersistentProcessTests
{
    [Fact]
    public async Task Weak_persistent_pipes_preserve_utf8_and_deliver_eof()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var launcher = new PlatformSandboxProcessLauncher(new SystemProcessRuntime());
        var control = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await launcher.StartAsync(
            Spec("persistent-private-writer", "unused-marker"), TestContext.Current.CancellationToken));
        await using (control)
        {
            using var output = new StreamReader(control.StandardOutput, new UTF8Encoding(false, true), leaveOpen: true);
            Assert.Equal("READY-ñ", await output.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await control.CloseInputAsync();
            Assert.Equal("EOF-FINAL-ñ", await output.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(35, await control.WaitForExitCodeAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Weak_session_manager_sequences_real_process_output_and_routes_stop_to_its_job()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var root = Path.Combine(Path.GetTempPath(), "OmniWeakSession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new PersistentProcessSessionManager(SessionId.New(), new InMemoryEventStore(), EventCodecs.Create(),
            Guid.NewGuid().ToString("N"), root, null, static () => true);
        var run = RunId.New().ToString();
        var lane = LaneId.New().ToString();
        var spec = new SandboxLaunchSpec(SandboxStrength.Weak,
            Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe"), ["persistent-process-control"],
            root, new Dictionary<string, string>(), [], new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []),
            TimeSpan.FromSeconds(30)) { Duplex = true };
        try
        {
            var started = await manager.StartAsync(run, lane, spec, new PlatformSandboxProcessLauncher(new SystemProcessRuntime()),
                null, TestContext.Current.CancellationToken);
            Assert.Equal("running", started.State);
            var handle = Assert.IsType<string>(started.Handle);
            var ready = await manager.ReadAsync(handle, run, lane, 0, 8, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Contains("READY-ñ", string.Concat(ready.Output.Select(chunk => chunk.Text)), StringComparison.Ordinal);
            var readySequence = Assert.Single(ready.Output).Sequence;

            await manager.SendAsync(handle, run, lane, "hello\n", false, null, null, TestContext.Current.CancellationToken);
            var ack = await manager.ReadAsync(handle, run, lane, ready.Last, 8, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Contains("ACK:hello", string.Concat(ack.Output.Select(chunk => chunk.Text)), StringComparison.Ordinal);
            Assert.True(Assert.Single(ack.Output).Sequence > readySequence, "Manager output sequences must advance across real pipe reads.");
            Assert.Equal("stopped", await manager.StopAsync(handle, run, lane, null, TestContext.Current.CancellationToken));
        }
        finally
        {
            await manager.StopAllAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Weak_terminal_uses_real_conpty_for_input_resize_and_output()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows ConPTY integration test."); return; }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        { Assert.Skip("ConPTY requires Windows 10 version 1809 or newer."); return; }
        var root = Path.Combine(Path.GetTempPath(), "OmniWeakConPty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var launch = new SandboxLaunchSpec(SandboxStrength.Weak,
            Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe"), ["terminal-echo"], root,
            new Dictionary<string, string>(), [], new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []),
            TimeSpan.FromSeconds(10)) { Duplex = true, Terminal = true };
        ISandboxPersistentProcessControl? control = null;
        Task<string>? remainingOutput = null;
        try
        {
            control = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await new WindowsWeakPersistentProcessLauncher()
                .StartAsync(launch, TestContext.Current.CancellationToken));
            Assert.True(control.IsTerminal);
            using var reader = new StreamReader(control.StandardOutput, new UTF8Encoding(false), leaveOpen: true);
            var ready = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var readyText = ready is null
                ? null : Regex.Replace(ready, @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", "").Trim();
            Assert.Equal("READY", readyText);
            remainingOutput = reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            control.ResizeTerminal(100, 32);
            await control.StandardInput.WriteAsync(Encoding.UTF8.GetBytes("terminal-test\r\n"), TestContext.Current.CancellationToken);
            await control.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            var exitCode = await control.WaitForExitCodeAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var text = await remainingOutput.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            Assert.Contains("ACK:terminal-test;size=100x32", text, StringComparison.Ordinal);
        }
        finally
        {
            if (control is not null) await control.DisposeAsync();
            if (remainingOutput is not null)
            {
                try { _ = await remainingOutput.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
            }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Weak_child_receives_only_allowlisted_parent_environment_and_explicit_launch_overrides()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows process environment integration test."); return; }
        const string sentinelName = "OMNICORE_M9_WEAK_SENTINEL";
        const string sentinelValue = "fake-secret-environment-sentinel";
        var priorValue = Environment.GetEnvironmentVariable(sentinelName);
        try
        {
            Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
            var launch = new SandboxLaunchSpec(SandboxStrength.Weak,
                Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe"),
                ["environment-probe", "OMNICORE_M9_WEAK_ALLOWED", sentinelName], AppContext.BaseDirectory,
                new Dictionary<string, string> { ["OMNICORE_M9_WEAK_ALLOWED"] = "fixture-allowed-value" }, [],
                new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(10)) { Duplex = true };
            await using var process = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await new WindowsWeakPersistentProcessLauncher()
                .StartAsync(launch, TestContext.Current.CancellationToken));
            using var stdout = new StreamReader(process.StandardOutput, new UTF8Encoding(false), leaveOpen: true);
            using var stderr = new StreamReader(process.StandardError, new UTF8Encoding(false), leaveOpen: true);
            var stdoutTask = stdout.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderrTask = stderr.ReadToEndAsync(TestContext.Current.CancellationToken);
            var exitCode = await process.WaitForExitCodeAsync(TestContext.Current.CancellationToken);
            var text = await stdoutTask;
            _ = await stderrTask;
            Assert.Equal(0, exitCode);
            Assert.Contains("fixture-allowed-value", text, StringComparison.Ordinal);
            Assert.Contains("excluded", text, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinelValue, text, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinelName, text, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(sentinelName, priorValue); }
    }

    [Fact]
    public async Task Weak_job_kills_descendant_and_prevents_its_delayed_marker()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var root = Path.Combine(Path.GetTempPath(), "OmniWeakJob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "orphan-marker.txt");
        var control = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await new WindowsWeakPersistentProcessLauncher()
            .StartAsync(Spec("spawn-delayed-marker", marker, root), TestContext.Current.CancellationToken));
        try
        {
            using var output = new StreamReader(control.StandardOutput, new UTF8Encoding(false, true), leaveOpen: true);
            var line = await output.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.StartsWith("DESCENDANT:", line, StringComparison.Ordinal);
            var descendantId = int.Parse(line!["DESCENDANT:".Length..], System.Globalization.CultureInfo.InvariantCulture);

            await control.TerminateAsync(TestContext.Current.CancellationToken);
            await control.WaitForExitCodeAsync(TestContext.Current.CancellationToken);
            Assert.True(HasExited(descendantId), "The Job Object must own and terminate descendants before stop returns.");
            await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(marker), "A descendant must not survive the root to perform a delayed effect.");
        }
        finally
        {
            await control.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Root_exit_terminates_descendant_before_waiting_for_inherited_pipe_eof()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var root = Path.Combine(Path.GetTempPath(), "OmniWeakRootExit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "orphan-marker.txt");
        var control = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await new WindowsWeakPersistentProcessLauncher()
            .StartAsync(Spec("exit-after-spawn-marker", marker, root), TestContext.Current.CancellationToken));
        try
        {
            using var output = new StreamReader(control.StandardOutput, new UTF8Encoding(false, true), leaveOpen: true);
            var line = await output.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.StartsWith("DESCENDANT:", line, StringComparison.Ordinal);
            var descendantId = int.Parse(line!["DESCENDANT:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0, await control.WaitForExitCodeAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(string.Empty, await output.ReadToEndAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(HasExited(descendantId), "Root exit must fence descendants before pipe EOF is awaited.");
            await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            await control.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Weak_process_cancellation_before_launch_does_not_create_a_child()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new WindowsWeakPersistentProcessLauncher()
            .StartAsync(Spec("persistent-private-writer", "unused-marker"), cancelled.Token));
    }

    [Fact]
    public async Task Weak_native_launch_failure_is_typed_as_prestart_and_never_runs_user_code()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var marker = Path.Combine(Path.GetTempPath(), "OmniWeakNoStart-" + Guid.NewGuid().ToString("N"));
        var launch = new SandboxLaunchSpec(SandboxStrength.Weak, Path.Combine(AppContext.BaseDirectory, "missing-helper.exe"),
            ["write-marker", marker], AppContext.BaseDirectory, new Dictionary<string, string>(), [],
            new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(5)) { Duplex = true };
        await Assert.ThrowsAsync<ProcessLaunchUnavailableBeforeStartException>(async () =>
            await new WindowsWeakPersistentProcessLauncher().StartAsync(launch, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Cancelled_stop_has_no_effect_and_host_disposal_closes_the_owned_job()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Job Object integration test."); return; }
        var root = Path.Combine(Path.GetTempPath(), "OmniWeakDispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "orphan-marker.txt");
        var control = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(await new WindowsWeakPersistentProcessLauncher()
            .StartAsync(Spec("spawn-delayed-marker", marker, root), TestContext.Current.CancellationToken));
        try
        {
            using var output = new StreamReader(control.StandardOutput, new UTF8Encoding(false, true), leaveOpen: true);
            var line = await output.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var descendantId = int.Parse(line!["DESCENDANT:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await control.TerminateAsync(cancelled.Token));
            Assert.False(HasExited(descendantId), "A cancelled stop must not partially signal the process tree.");

            await control.DisposeAsync();
            Assert.True(HasExited(descendantId), "Disposal must close the kill-on-close Job Object and await its tree.");
            await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            await control.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private static SandboxLaunchSpec Spec(string command, string argument, string? workingDirectory = null) =>
        new(SandboxStrength.Weak, Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe"),
            [command, argument], workingDirectory ?? AppContext.BaseDirectory, new Dictionary<string, string>(), [],
            new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(30)) { Duplex = true };
}
