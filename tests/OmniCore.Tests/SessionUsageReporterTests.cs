namespace OmniCore.Tests;

using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;
using OmniCore.Protocol;

/// <summary>Costo y cuota reales en la status line (M5, ADR-0031 §3): nunca inventados.</summary>
public sealed class SessionUsageReporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TokenTotals Tokens = new(100_000, 18_000, 0, 0);

    [Fact]
    public void Declared_price_gives_an_estimated_cost()
    {
        var snapshot = SessionUsageReporter.Build(Tokens, 0.0842m, true, priceDeclared: true, localModel: false, [], Now);
        Assert.Equal(MetricAvailability.Estimated, snapshot.SessionCost.Availability);
        Assert.Equal("≈$0.08", UsagePresentation.Cost(snapshot.SessionCost));
    }

    [Fact]
    public void Missing_price_is_not_applicable_locally_and_unknown_remotely()
    {
        var local = SessionUsageReporter.Build(Tokens, 0m, false, priceDeclared: false, localModel: true, [], Now);
        var remote = SessionUsageReporter.Build(Tokens, 0m, false, priceDeclared: false, localModel: false, [], Now);
        Assert.Null(UsagePresentation.Cost(local.SessionCost));
        Assert.Equal("—", UsagePresentation.Cost(remote.SessionCost));
    }

    [Fact]
    public void Declared_price_with_a_turn_without_cost_is_not_presented_as_a_total()
    {
        var snapshot = SessionUsageReporter.Build(Tokens, 0.01m, costComplete: false, priceDeclared: true, localModel: false, [], Now);
        Assert.Equal("—", UsagePresentation.Cost(snapshot.SessionCost));
    }

    [Fact]
    public void Quota_comes_only_from_reported_rate_limit_windows_preferring_tokens()
    {
        var windows = new[]
        {
            new RateLimitWindow(RateLimitWindowKind.Requests, 100, 90, Now.AddMinutes(1)),
            new RateLimitWindow(RateLimitWindowKind.Tokens, 400_000, 248_000, Now.AddMinutes(1)),
        };
        var reported = SessionUsageReporter.Build(Tokens, 0m, true, true, false, windows, Now);
        var none = SessionUsageReporter.Build(Tokens, 0m, true, true, false, [], Now);

        Assert.Equal("tokens 62%", UsagePresentation.Remaining(reported.Remaining));
        Assert.Equal(MetricAvailability.Reported, reported.Remaining.Availability);
        Assert.Equal("—", UsagePresentation.Remaining(none.Remaining));
    }

    [Fact]
    public void Status_line_joins_tokens_cost_and_quota_and_omits_what_does_not_apply()
    {
        var local = SessionUsageReporter.Build(new TokenTotals(900, 50, 0, 0), 0m, false, false, true, [], Now);
        Assert.Equal("session 950 tok · —", OmniCore.Cli.CliApp.StatusLineText(local));
    }

    [Fact]
    public async System.Threading.Tasks.Task Cloud_providers_expose_the_rate_limit_windows_of_their_last_response()
    {
        var handler = new HeaderHandler();
        var provider = new AnthropicMessagesProvider(new ProviderDescriptor("anthropic", ProviderFamily.AnthropicMessages,
            "https://api.example.test", AuthConfig.None(), false, false, true), new NoSecrets(), () => new HttpClient(handler, false));
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("m"), 4096, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.User, [new TextBlock("hola")])], null, [], ToolChoice.Auto(), null, null, null, null);

        await foreach (var _ in provider.StreamAsync(request, TestContext.Current.CancellationToken)) { }

        var window = Assert.Single(provider.LastRateLimits, w => w.Kind == RateLimitWindowKind.Tokens);
        Assert.Equal(1000, window.Limit);
        Assert.Equal(620, window.Remaining);
        Assert.True(provider.Capabilities.ReportsQuota);
    }

    private sealed class NoSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => Secret.Of("unused-secret");
    }

    private sealed class HeaderHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
event: message_start
data: {"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

event: message_stop
data: {"type":"message_stop"}

""", Encoding.UTF8, "text/event-stream"),
            };
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-tokens-limit", "1000");
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-tokens-remaining", "620");
            return System.Threading.Tasks.Task.FromResult(response);
        }
    }
}
