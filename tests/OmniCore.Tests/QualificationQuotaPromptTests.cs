using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class QualificationQuotaPromptTests
{
    [Theory]
    [InlineData("es", "desactualizada")]
    [InlineData("en", "stale")]
    public void Stale_measurement_is_explicit_even_when_window_value_was_reported(string locale, string status)
    {
        var snapshot = Fixture() with { Availability = MetricAvailability.Stale };
        using var output = new StringWriter();
        Assert.False(OmniCore.Cli.ModelPolicyCommands.ConfirmQualificationQuota(snapshot,
            new Localization(locale), new StringReader("n"), output, true, CancellationToken.None));
        Assert.Contains(status, output.ToString());
        Assert.Contains(snapshot.AsOf.ToString("O"), output.ToString());
        Assert.Contains("5%", output.ToString());
    }

    [Theory]
    [InlineData("es", "sí", true)]
    [InlineData("en", "yes", true)]
    [InlineData("es", "n", false)]
    [InlineData("en", "", false)]
    public void Prompt_presents_origin_measurement_window_and_reset_before_specific_consent(
        string locale, string answer, bool expected)
    {
        var snapshot = Fixture();
        using var output = new StringWriter();
        Assert.Equal(expected, OmniCore.Cli.ModelPolicyCommands.ConfirmQualificationQuota(snapshot,
            new Localization(locale), new StringReader(answer), output, true, CancellationToken.None));
        var text = output.ToString();
        Assert.Contains("fixture-provider", text);
        Assert.Contains("fixture-account", text);
        Assert.Contains("offline-fixture", text);
        Assert.Contains(snapshot.AsOf.ToString("O"), text);
        Assert.Contains("5%", text);
        Assert.Contains("window-5h", text);
        Assert.Contains(snapshot.Windows[0].ResetsAt!.Value.ToString("O"), text);
        Assert.Contains("probe", text);
        Assert.DoesNotContain("{", text);
    }

    [Fact]
    public void Noninteractive_display_does_not_read_or_authorize_even_with_yes_as_input()
    {
        using var output = new StringWriter();
        Assert.False(OmniCore.Cli.ModelPolicyCommands.ConfirmQualificationQuota(Fixture(),
            Localization.Spanish(), new ForbiddenReader(), output, false, CancellationToken.None));
        Assert.Contains("--yes no", output.ToString());
        Assert.Contains("5%", output.ToString());
    }

    [Fact]
    public void Cancellation_neither_prompts_nor_authorizes()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        Assert.Throws<OperationCanceledException>(() =>
            OmniCore.Cli.ModelPolicyCommands.ConfirmQualificationQuota(Fixture(), Localization.Spanish(),
                new ForbiddenReader(), output, true, cancellation.Token));
        Assert.Equal("", output.ToString());
    }

    private static ProviderQuotaSnapshot Fixture()
    {
        var date = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        return new("fixture-provider", "fixture-account", "offline-fixture", date,
            MetricAvailability.Reported, [new("window-5h", "fixture", 300,
                new(MetricAvailability.Reported, 95d, "offline-fixture", date),
                new(MetricAvailability.Reported, 5d, "offline-fixture", date), date.AddHours(1), null)], [], null);
    }

    private sealed class ForbiddenReader : TextReader
    {
        public override string? ReadLine() => throw new InvalidOperationException("Must not read input.");
    }
}
