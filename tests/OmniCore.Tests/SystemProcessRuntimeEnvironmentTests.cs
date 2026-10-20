namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Execution;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class SystemProcessRuntimeEnvironmentTests
{
    [Fact]
    public void Empty_initial_stdin_closes_at_eof_and_is_safe_for_fast_exits()
    {
        var runtime = new SystemProcessRuntime();
        var helper = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
            ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");
        var token = TestContext.Current.CancellationToken;

        var eofHandle = runtime.Launch(new ProcessLaunch(helper, ["stdin-eof"], AppContext.BaseDirectory,
            new Dictionary<string, string>(), captureOutput: true) { InitialStandardInput = string.Empty }, token);
        var eofResult = runtime.Wait(eofHandle, TimeSpan.FromSeconds(10), token);
        Assert.False(eofResult.TimedOut);
        Assert.Equal(0, eofResult.ExitCode);
        Assert.Equal("EOF", eofResult.Stdout);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var fastExit = OperatingSystem.IsWindows()
                ? new ProcessLaunch("cmd.exe", ["/c", "exit 0"], "", new Dictionary<string, string>(), captureOutput: true)
                    { InitialStandardInput = string.Empty }
                : new ProcessLaunch("/bin/true", [], "", new Dictionary<string, string>(), captureOutput: true)
                    { InitialStandardInput = string.Empty };
            var handle = runtime.Launch(fastExit, token);
            var result = runtime.Wait(handle, TimeSpan.FromSeconds(10), token);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
        }
    }

    [Fact]
    public void Windows_runtime_passes_APPDATA_but_excludes_unrelated_environment_values()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("APPDATA is a Windows process environment contract.");
            return;
        }

        const string sentinelName = "OMNICORE_TEST_SECRET_SENTINEL";
        var sentinelValue = "sentinel-" + Guid.NewGuid().ToString("N");
        var appDataValue = Path.Combine(Path.GetTempPath(), "omnicore-appdata-" + Guid.NewGuid().ToString("N"));
        var previousAppData = Environment.GetEnvironmentVariable("APPDATA");
        var previousSentinel = Environment.GetEnvironmentVariable(sentinelName);
        try
        {
            Environment.SetEnvironmentVariable("APPDATA", appDataValue);
            Environment.SetEnvironmentVariable(sentinelName, sentinelValue);

            var runtime = new SystemProcessRuntime();
            var executable = Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe");
            var process = runtime.Launch(new ProcessLaunch(executable,
                ["environment-probe", "APPDATA", sentinelName], AppContext.BaseDirectory,
                new Dictionary<string, string>(), captureOutput: true), TestContext.Current.CancellationToken);
            var result = runtime.Wait(process, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            var lines = (result.Stdout ?? "").Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(new[] { appDataValue, "excluded" }, lines);
            Assert.DoesNotContain(sentinelValue, result.Stdout ?? "", StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", previousAppData);
            Environment.SetEnvironmentVariable(sentinelName, previousSentinel);
        }
    }
}
