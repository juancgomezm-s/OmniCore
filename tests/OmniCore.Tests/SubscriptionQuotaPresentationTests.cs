using System.Text.Json;
using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Fixture values only: formatting is not an authenticated account query.</summary>
public sealed class SubscriptionQuotaPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Subscription_windows_and_response_rate_limits_are_displayed_separately()
    {
        var snapshot = Snapshot(Quota());
        var line = OmniCore.Cli.CliApp.StatusLineText(snapshot);
        Assert.Contains("tokens 62%", line, StringComparison.Ordinal);
        Assert.Contains("chatgpt 7d remaining 80%", line, StringComparison.Ordinal);
        Assert.Equal(QuotaKind.RateLimitWindow, snapshot.Remaining.Value!.Kind);
        Assert.Equal(10080, snapshot.AccountQuota!.Windows[0].DurationMinutes);
    }

    [Fact]
    public void Only_reported_windows_are_shown_and_unknown_duration_does_not_invent_a_period()
    {
        Assert.Equal("chatgpt codex:primary remaining 80%",
            UsagePresentation.AccountQuota(Quota(duration: null)));
        Assert.Equal("chatgpt 5h remaining 80%", UsagePresentation.AccountQuota(Quota(duration: 300)));
        Assert.DoesNotContain("5h", UsagePresentation.AccountQuota(Quota())!, StringComparison.Ordinal);
        var unknown = Quota() with { Availability = MetricAvailability.Unknown, Windows = [] };
        Assert.Equal("chatgpt quota —", UsagePresentation.AccountQuota(unknown));
        Assert.Null(UsagePresentation.AccountQuota(null));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(101d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_remaining_percentage_is_not_presented_as_measured(double value)
    {
        Assert.Equal("chatgpt 7d remaining —", UsagePresentation.AccountQuota(Quota(remaining: value)));
    }

    [Theory]
    [InlineData(MetricAvailability.Unknown)]
    [InlineData(MetricAvailability.Estimated)]
    [InlineData(MetricAvailability.NotSupported)]
    public void Unmeasured_remaining_percentage_is_unknown_even_if_a_numeric_slot_exists(MetricAvailability availability)
    {
        Assert.Equal("chatgpt 7d remaining —", UsagePresentation.AccountQuota(Quota(metricAvailability: availability)));
    }

    [Fact]
    public void Stale_window_preserves_its_measurement_time_and_marks_the_presentation()
    {
        var quota = Quota() with { Availability = MetricAvailability.Stale, LastQueryAttemptAt = Now.AddMinutes(2) };
        Assert.Equal("chatgpt 7d remaining 80% stale", UsagePresentation.AccountQuota(quota));
        var encoded = JsonSerializer.Serialize(Snapshot(quota), ObservabilityJson.Options);
        var decoded = Assert.IsType<UsageSnapshot>(JsonSerializer.Deserialize<UsageSnapshot>(encoded, ObservabilityJson.Options));
        Assert.Equal(Now, decoded.AccountQuota!.AsOf);
        Assert.Equal(Now.AddMinutes(2), decoded.AccountQuota.LastQueryAttemptAt);
        Assert.Equal("official-fixture-source", decoded.AccountQuota.Source);
        Assert.Equal("account-fixture", decoded.AccountQuota.AccountId);
        Assert.Equal(Now.AddDays(7), decoded.AccountQuota.Windows[0].ResetsAt);
    }

    [Fact]
    public void Account_credit_balance_and_key_limit_have_separate_labels_and_keep_unknown_values()
    {
        var quota = Quota() with
        {
            // Account window availability can be unknown while a credit balance was reported.
            Availability = MetricAvailability.Unknown, Windows = [],
            Credits = [new(CreditScope.AccountBalance, new(MetricAvailability.Reported, 0m, "fixture", Now), "credits", null, null),
                new(CreditScope.KeyLimit, new(MetricAvailability.Unknown, null, "fixture", Now), "credits", null, "fixture-key")],
        };
        Assert.Equal("chatgpt account balance 0 credits · chatgpt key limit — credits", UsagePresentation.AccountQuota(quota));
    }

    [Fact]
    public void Legacy_snapshot_without_account_quota_retains_its_previous_status_line()
    {
        var legacy = Snapshot(null);
        var encoded = JsonSerializer.Serialize(legacy, ObservabilityJson.Options);
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(encoded)!.AsObject();
        Assert.True(parsed.Remove("accountQuota"));
        var decoded = Assert.IsType<UsageSnapshot>(JsonSerializer.Deserialize<UsageSnapshot>(parsed.ToJsonString(), ObservabilityJson.Options));
        Assert.Null(decoded.AccountQuota);
        Assert.Equal("session 3 tok · — · tokens 62%", OmniCore.Cli.CliApp.StatusLineText(decoded));
    }

    private static ProviderQuotaSnapshot Quota(int? duration = 10080, double remaining = 80,
        MetricAvailability metricAvailability = MetricAvailability.Reported) => new("chatgpt", "account-fixture",
        "official-fixture-source", Now, MetricAvailability.Reported,
        [new("codex:primary", "codex", duration, new(MetricAvailability.Reported, 20d, "fixture", Now),
            new(metricAvailability, remaining, "fixture", Now), Now.AddDays(7), null)], [], null);

    private static UsageSnapshot Snapshot(ProviderQuotaSnapshot? quota) => new(new(1, 2, 0, 0),
        new(MetricAvailability.Unknown, null, "fixture"),
        new(MetricAvailability.Reported, new(QuotaKind.RateLimitWindow, 620m, 1000m, "tokens", Now.AddMinutes(1)), "fixture-headers"),
        Now, AccountQuota: quota);
}
