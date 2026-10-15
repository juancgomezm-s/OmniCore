namespace OmniCore.Tests;

using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Domain;

public sealed class ClaudeLaneProcessExecutorTests
{
    [Fact]
    public async System.Threading.Tasks.Task Process_result_is_accepted_only_after_init_barrier_terminal_eof_and_exit_zero()
    {
        var executor = new ClaudeLaneProcessExecutor(new SystemProcessRuntime());
        var durableSession = false;
        var result = await executor.ExecuteAsync(FixtureExe, ["claude-stream"], AppContext.BaseDirectory,
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10), (session, _) =>
            {
                Assert.Equal("fixture-session", session.SessionId);
                durableSession = true;
                return ValueTask.CompletedTask;
            }, TestContext.Current.CancellationToken);

        Assert.True(durableSession);
        Assert.Null(result.Failure);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fixture result", result.Stream?.ResultText);
        Assert.Equal(AgentOutcome.Succeeded, result.AgentResult?.Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Nonzero_exit_and_timeout_never_become_successful_protocol_results()
    {
        var executor = new ClaudeLaneProcessExecutor(new SystemProcessRuntime());
        var failed = await executor.ExecuteAsync(FixtureExe, ["claude-stream-fail"], AppContext.BaseDirectory,
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10), static (_, _) => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken);
        Assert.Equal(ClaudeLaneProcessFailure.NonZeroExit, failed.Failure);
        Assert.Equal(7, failed.ExitCode);
        Assert.Null(failed.Stream);
        Assert.Null(failed.AgentResult);

        var timedOut = await executor.ExecuteAsync(FixtureExe, ["sleep"], AppContext.BaseDirectory,
            new Dictionary<string, string>(), TimeSpan.FromMilliseconds(150), static (_, _) => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken);
        Assert.Equal(ClaudeLaneProcessFailure.TimedOut, timedOut.Failure);
        Assert.Null(timedOut.Stream);
    }

    [Fact]
    public async System.Threading.Tasks.Task Malformed_stream_returns_a_typed_protocol_code_without_stream_content()
    {
        var executor = new ClaudeLaneProcessExecutor(new SystemProcessRuntime());
        var durableSession = false;
        var result = await executor.ExecuteAsync(FixtureExe, ["claude-stream-invalid"], AppContext.BaseDirectory,
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10), (_, _) =>
            {
                durableSession = true;
                return ValueTask.CompletedTask;
            }, TestContext.Current.CancellationToken);

        Assert.False(durableSession);
        Assert.Equal(ClaudeLaneProcessFailure.InvalidProtocol, result.Failure);
        Assert.Equal(ClaudeLaneStreamErrorCode.InvalidJson, result.ProtocolError);
        Assert.Null(result.Stream);
        Assert.Null(result.AgentResult);
    }

    private static string FixtureExe => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
        ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");
}
