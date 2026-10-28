using OmniCore.Cli;
using OmniCore.Protocol;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class ReleaseUpdateCliTests
{
    [Theory]
    [InlineData("check --channel live")]
    [InlineData("check --channel stable --force")]
    [InlineData("apply --channel live")]
    [InlineData("rollback --bogus")]
    [InlineData("status extra")]
    [InlineData("unknown")]
    public async Task Malformed_update_commands_stop_before_any_checker_or_model_route(string tail)
    {
        var previousOut = Console.Out; using var output = new StringWriter(); Console.SetOut(output);
        var calls = 0;
        CliApp.ReleaseUpdateCheckOverride = (_, _) => { calls++; throw new InvalidOperationException("must not check"); };
        try
        {
            var args = new[] { "update" }.Concat(tail.Split(' ')).ToArray();
            var exit = await CliApp.RunAsync(args);
            Assert.Equal(2, exit); Assert.Equal(0, calls);
            Assert.Contains("omni update check|status", output.ToString());
        }
        finally { CliApp.ReleaseUpdateCheckOverride = null; Console.SetOut(previousOut); }
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("rollback")]
    public async Task Destructive_commands_require_confirmation_before_checking_or_inspecting(string command)
    {
        var previousOut = Console.Out; using var output = new StringWriter(); Console.SetOut(output);
        var calls = 0;
        CliApp.ReleaseUpdateCheckOverride = (_, _) => { calls++; throw new InvalidOperationException("must not check"); };
        try
        {
            var exit = await CliApp.RunAsync(["update", command]);
            Assert.Equal(1, exit); Assert.Equal(0, calls);
            Assert.Contains("update_confirmation_required", output.ToString());
        }
        finally { CliApp.ReleaseUpdateCheckOverride = null; Console.SetOut(previousOut); }
    }

    [Fact]
    public async Task Apply_with_confirmation_reports_a_release_without_a_verifiable_asset()
    {
        var previousOut = Console.Out; using var output = new StringWriter(); Console.SetOut(output);
        var calls = 0;
        CliApp.ReleaseUpdateCheckOverride = (request, _) =>
        {
            calls++;
            return Task.FromResult(new ReleaseUpdateCheckResult(request.Channel, ReleaseUpdateStatus.UpdateAvailable,
                ReleaseCompatibility.Compatible, request.InstalledVersion, "0.2.0", "v0.2.0"));
        };
        try
        {
            var exit = await CliApp.RunAsync(["update", "apply", "--yes"]);
            Assert.Equal(1, exit); Assert.Equal(1, calls);
            Assert.Contains("update_asset_unavailable", output.ToString());
        }
        finally { CliApp.ReleaseUpdateCheckOverride = null; Console.SetOut(previousOut); }
    }

    [Fact]
    public async Task Valid_preview_check_uses_only_the_injected_checker_and_sanitizes_terminal_content()
    {
        var previousOut = Console.Out; using var output = new StringWriter(); Console.SetOut(output);
        ReleaseUpdateCheckRequest? received = null;
        CliApp.ReleaseUpdateCheckOverride = (request, _) =>
        {
            received = request;
            return Task.FromResult(new ReleaseUpdateCheckResult(request.Channel, ReleaseUpdateStatus.CompatibilityUnknown,
                ReleaseCompatibility.Unknown, request.InstalledVersion, "2.0.0-rc.1", "v2.0.0-rc.1",
                "1111111111111111111111111111111111111111",
                "https://github.com/juancgomezm-s/OmniCore/releases/tag/v2.0.0-rc.1", "note\u001b[31mtext"));
        };
        try
        {
            var exit = await CliApp.RunAsync(["--locale", "en", "update", "check", "--channel", "preview"]);
            Assert.Equal(0, exit); Assert.Equal(ReleaseUpdateChannel.Preview, received!.Channel);
            Assert.Contains("Compatibility unknown", output.ToString());
            Assert.Contains("Metadata only", output.ToString());
            Assert.DoesNotContain('\u001b', output.ToString());
        }
        finally { CliApp.ReleaseUpdateCheckOverride = null; Console.SetOut(previousOut); }
    }
}
