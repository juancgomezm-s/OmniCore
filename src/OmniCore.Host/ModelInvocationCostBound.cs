namespace OmniCore.Host;

using OmniCore.Domain;

/// <summary>A conservative admission quote, never measured consumption or a provider invoice.
/// Requires a declared finite model input ceiling, an enforced request output ceiling and
/// a finite physical generation-send bound. A token estimate is not an input ceiling.</summary>
internal static class ModelInvocationCostBound
{
    /// <summary>
    /// Conservative input + output exposure across the declared physical send attempts.
    /// ContextBudget is an estimated materialization policy, not a native input ceiling.
    /// Null means no representable declared bound; it never means zero consumption.
    /// </summary>
    public static long? TokenCeiling(ModelSelection selection, long? modelContextCapacity,
        long? maximumGenerationRequestAttempts)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (modelContextCapacity is not > 0 || selection.MaxOutputTokens is not > 0
            || maximumGenerationRequestAttempts is not > 0)
            return null;
        try
        {
            return checked((modelContextCapacity.Value + selection.MaxOutputTokens.Value)
                * maximumGenerationRequestAttempts.Value);
        }
        catch (OverflowException) { return null; }
    }

    public static decimal? Quote(ModelSelection selection, ModelPricing? pricing,
        long? modelContextCapacity, long? maximumGenerationRequestAttempts)
    {
        if (pricing is not { IsComplete: true } || pricing.InputPricePerMillionUsd < 0m
            || pricing.OutputPricePerMillionUsd < 0m || modelContextCapacity is not > 0
            || selection.MaxOutputTokens is not > 0 || maximumGenerationRequestAttempts is not > 0)
            return null;
        try
        {
            // ContextBudget bounds materialization policy, not necessarily native tokenization.
            // Reserve the whole declared model ceiling rather than pretend an estimate is exact.
            return checked((modelContextCapacity.Value / 1_000_000m * pricing.InputPricePerMillionUsd!.Value
                + selection.MaxOutputTokens.Value / 1_000_000m * pricing.OutputPricePerMillionUsd!.Value)
                * maximumGenerationRequestAttempts.Value);
        }
        catch (OverflowException) { return null; }
    }
}
