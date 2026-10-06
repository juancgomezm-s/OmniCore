using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class TokenUsageValidationTests
{
    [Fact]
    public void Every_reporting_mask_requires_both_bits_to_compare_each_subset()
    {
        var cases = new[]
        {
            (Usage: new TokenUsage(10, 5, 11, 0, 0), Bits: TokenUsageFields.Input | TokenUsageFields.CacheRead),
            (Usage: new TokenUsage(10, 5, 0, 11, 0), Bits: TokenUsageFields.Input | TokenUsageFields.CacheWrite),
            (Usage: new TokenUsage(10, 5, 0, 0, 6), Bits: TokenUsageFields.Output | TokenUsageFields.Reasoning),
        };
        foreach (var (usage, bits) in cases)
            for (var mask = 0; mask <= 31; mask++)
                Assert.Equal(((TokenUsageFields)mask & bits) == bits,
                    TokenUsageValidation.IsInvalid(usage, (TokenUsageFields)mask));
    }

    [Fact]
    public void Cache_details_are_not_assumed_disjoint_and_equal_boundaries_are_valid()
    {
        var usage = new TokenUsage(10, 5, 10, 10, 5);
        Assert.False(TokenUsageValidation.IsInvalid(usage, TokenUsageFields.All));
        Assert.Equal(.000015m, new ModelPricing(1m, 1m).CostUsd(usage, TokenUsageFields.All));
    }

    [Fact]
    public void Known_contradiction_cannot_be_quoted_even_with_explicit_zero_prices()
    {
        var usage = new TokenUsage(10, 5, 11, 0, 0);
        Assert.Null(new ModelPricing(0m, 0m).CostUsd(usage, TokenUsageFields.All));
        Assert.Equal(.000015m, new ModelPricing(1m, 1m).CostUsd(usage,
            TokenUsageFields.Input | TokenUsageFields.Output));
        Assert.Null(new ModelPricing(1m, 1m).CostUsd(usage, TokenUsageFields.CacheRead));
    }

    [Fact]
    public void Negative_placeholders_keep_the_existing_invalid_raw_data_contract()
    {
        Assert.True(TokenUsageValidation.IsInvalid(new TokenUsage(10, 5, -1, 0, 0), TokenUsageFields.None));
        Assert.True(TokenUsageValidation.IsInvalid(new TokenUsage(10, 5, 0, -1, 0), TokenUsageFields.None));
        Assert.True(TokenUsageValidation.IsInvalid(new TokenUsage(10, 5, 0, 0, -1), TokenUsageFields.None));
    }
}
