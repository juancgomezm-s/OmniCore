using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class HostTests
{
    [Fact]
    public async Task RunAsync_delegates_args_and_exit_code_to_client()
    {
        string[]? received = null;

        var exitCode = await OmniHost.Create(["plan", "x"]).RunAsync((args, _) =>
        {
            received = args;
            return Task.FromResult(7);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(7, exitCode);
        Assert.NotNull(received);
        Assert.Equal(["plan", "x"], received);
    }
}
