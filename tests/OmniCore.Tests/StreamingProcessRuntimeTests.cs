namespace OmniCore.Tests;

using System.Text;
using OmniCore.Abstractions;
using OmniCore.Execution;

public sealed class StreamingProcessRuntimeTests
{
    [Fact]
    public async Task Streaming_runtime_drains_capped_stderr_and_delivers_stdout_incrementally()
    {
        var runtime = new SystemProcessRuntime();
        var process = runtime.Launch(FixtureLaunch("stream"), TestContext.Current.CancellationToken);
        var lines = new List<string>();
        var result = await ((IStreamingProcessRuntime)runtime).WaitStreamingAsync(process, TimeSpan.FromSeconds(15),
            async (stdout, cancellationToken) =>
            {
                using var reader = new StreamReader(stdout, Encoding.UTF8);
                while (await reader.ReadLineAsync(cancellationToken) is { } line) lines.Add(line);
            }, TestContext.Current.CancellationToken);

        Assert.Equal(42, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(new[] { "streamed stdout line" }, lines);
        Assert.Equal(1_048_576, result.Stderr!.Length);
    }

    [Fact]
    public async Task Streaming_runtime_cancels_and_reaps_the_process_tree_when_consumer_is_cancelled()
    {
        var runtime = new SystemProcessRuntime();
        var process = runtime.Launch(FixtureLaunch("sleep"), TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ((IStreamingProcessRuntime)runtime).WaitStreamingAsync(process, TimeSpan.FromSeconds(15),
                async (stdout, cancellationToken) => await stdout.CopyToAsync(Stream.Null, cancellationToken),
                cancellation.Token));

        Assert.False(runtime.IsAlive(process));
    }

    private static ProcessLaunch FixtureLaunch(string mode) => new(
        Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe"), [mode],
        AppContext.BaseDirectory, new Dictionary<string, string>(), captureOutput: true);
}
