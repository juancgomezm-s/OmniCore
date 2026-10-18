namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Execution;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class SystemProcessRuntimeEnvironmentTests
{
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
