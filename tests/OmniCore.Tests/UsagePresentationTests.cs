namespace OmniCore.Tests;

using OmniCore.Client;
using OmniCore.Protocol;

/// <summary>Reglas de presentación de ADR-0031 §3: nunca se inventa costo ni cuota.</summary>
public sealed class UsagePresentationTests
{
    private static Metric<Money> Cost(MetricAvailability availability, decimal? amount = null) =>
        new(availability, amount is null ? null : new Money(amount.Value, "USD"), "test");

    private static Metric<QuotaInfo> Quota(MetricAvailability availability, QuotaKind kind = QuotaKind.Credits,
        decimal? remaining = null, decimal? limit = null, string unit = "USD") =>
        new(availability, remaining is null && limit is null ? null : new QuotaInfo(kind, remaining, limit, unit, null), "test");

    [Theory]
    [InlineData(MetricAvailability.Reported, 0.08, "$0.08")]
    [InlineData(MetricAvailability.Reported, 0.0042, "$0.0042")]
    [InlineData(MetricAvailability.Stale, 1.5, "$1.50")]
    [InlineData(MetricAvailability.Estimated, 0.08, "≈$0.08")]
    public void Cost_formats_reported_stale_and_estimated_values(MetricAvailability availability, double amount, string expected) =>
        Assert.Equal(expected, UsagePresentation.Cost(Cost(availability, (decimal)amount)));

    [Theory]
    [InlineData(MetricAvailability.NotSupported)]
    [InlineData(MetricAvailability.Unknown)]
    public void Cost_without_data_is_a_dash(MetricAvailability availability) =>
        Assert.Equal("—", UsagePresentation.Cost(Cost(availability)));

    [Fact]
    public void Cost_not_applicable_is_omitted() =>
        Assert.Null(UsagePresentation.Cost(Cost(MetricAvailability.NotApplicable)));

    [Fact]
    public void Credits_rate_limit_window_and_token_allowance_are_formatted()
    {
        Assert.Equal("credits $17.42", UsagePresentation.Remaining(Quota(MetricAvailability.Reported, QuotaKind.Credits, 17.42m)));
        Assert.Equal("tokens 62%", UsagePresentation.Remaining(Quota(MetricAvailability.Reported, QuotaKind.RateLimitWindow, 62_999m, 100_000m, "tokens")));
        Assert.Equal("1500 tokens", UsagePresentation.Remaining(Quota(MetricAvailability.Stale, QuotaKind.TokenAllowance, 1500m, null, "tokens")));
    }

    [Theory]
    [InlineData(MetricAvailability.Estimated)]
    [InlineData(MetricAvailability.NotSupported)]
    [InlineData(MetricAvailability.Unknown)]
    public void Quota_is_never_estimated_nor_invented(MetricAvailability availability) =>
        Assert.Equal("—", UsagePresentation.Remaining(Quota(availability, QuotaKind.Credits, 10m)));

    [Fact]
    public void Quota_not_applicable_is_omitted_and_a_window_without_limit_is_a_dash()
    {
        Assert.Null(UsagePresentation.Remaining(Quota(MetricAvailability.NotApplicable)));
        Assert.Equal("—", UsagePresentation.Remaining(Quota(MetricAvailability.Reported, QuotaKind.RateLimitWindow, 5m, null, "requests")));
    }

    [Theory]
    [InlineData(400, 599, "session 999 tok")]
    [InlineData(600, 400, "session 1k tok")]
    [InlineData(1000, 500, "session 1.5k tok")]
    [InlineData(100_000, 18_000, "session 118k tok")]
    public void Session_tokens_use_thousands_above_one_thousand(long input, long output, string expected) =>
        Assert.Equal(expected, UsagePresentation.Tokens(new TokenTotals(input, output, 0, 0)));
}
