using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class HostProviderRequestTimeoutTests
{
    [Fact]
    public void Default_transport_deadline_stays_five_minutes()
    {
        using var client = OmniHost.CreateChatCompletionsClient("https://example.com/v1", null);
        Assert.Equal(TimeSpan.FromMinutes(5), client.Timeout);
    }

    [Fact]
    public void Local_acceptance_can_allow_35_minute_prefill_without_changing_the_default()
    {
        using var first = OmniHost.CreateChatCompletionsClient("https://example.com/v1", null, TimeSpan.FromMinutes(35));
        using var second = OmniHost.CreateChatCompletionsClient("https://example.com/v1", null);
        Assert.Equal(TimeSpan.FromMinutes(35), first.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(5), second.Timeout);
        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void Invalid_or_unbounded_deadlines_are_rejected(long milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OmniHost.CreateChatCompletionsClient("https://example.com/v1", null, TimeSpan.FromMilliseconds(milliseconds)));
}
