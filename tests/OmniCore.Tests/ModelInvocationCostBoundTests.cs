namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Host;

public sealed class ModelInvocationCostBoundTests
{
    [Theory]
    [InlineData(1, "0.401")]
    [InlineData(3, "1.203")]
    public void Quote_covers_declared_input_output_and_every_possible_generation_send(long attempts, string expected)
    {
        var selection = new ModelSelection(new ModelIdValue("fixture"), 100, ToolMode.Direct, null, maxOutputTokens: 1000);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            ModelInvocationCostBound.Quote(selection, new ModelPricing(1m, 1m), 400_000, attempts));
    }

    [Theory]
    [InlineData(null, 1L, 1000L)]
    [InlineData(0L, 1L, 1000L)]
    [InlineData(400000L, null, 1000L)]
    [InlineData(400000L, 0L, 1000L)]
    [InlineData(400000L, -1L, 1000L)]
    [InlineData(400000L, 1L, null)]
    public void Unknown_or_invalid_bounds_are_unavailable_not_zero(long? input, long? attempts, long? output)
    {
        var selection = new ModelSelection(new ModelIdValue("fixture"), 100, ToolMode.Direct, null, maxOutputTokens: output);
        Assert.Null(ModelInvocationCostBound.Quote(selection, new ModelPricing(1m, 1m), input, attempts));
    }

    [Fact]
    public void Unknown_price_negative_price_and_overflow_cannot_authorize_a_free_reservation()
    {
        var selection = new ModelSelection(new ModelIdValue("fixture"), 100, ToolMode.Direct, null, maxOutputTokens: 1000);
        Assert.Null(ModelInvocationCostBound.Quote(selection, null, 400_000, 1));
        Assert.Null(ModelInvocationCostBound.Quote(selection, new ModelPricing(null, 1m), 400_000, 1));
        Assert.Null(ModelInvocationCostBound.Quote(selection, new ModelPricing(-1m, 1m), 400_000, 1));
        Assert.Null(ModelInvocationCostBound.Quote(selection, new ModelPricing(decimal.MaxValue, decimal.MaxValue), long.MaxValue, long.MaxValue));
        Assert.Equal(0m, ModelInvocationCostBound.Quote(selection, new ModelPricing(0m, 0m), 400_000, 1));
    }
}
