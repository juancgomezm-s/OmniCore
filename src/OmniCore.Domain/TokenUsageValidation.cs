namespace OmniCore.Domain;

/// <summary>Validates normalized usage without turning unreported placeholders into measurements.</summary>
public static class TokenUsageValidation
{
    public static bool IsInvalid(TokenUsage usage, TokenUsageFields reportedFields)
    {
        // Preserve existing rejection of negative raw slots, even if a provider omitted their bit.
        if (usage.Input < 0 || usage.Output < 0 || usage.CacheRead < 0
            || usage.CacheWrite < 0 || usage.Reasoning < 0) return true;
        // Input includes each cache detail and Output includes reasoning. Compare only when
        // both quantities were reported; cache details are not specified as disjoint.
        return reportedFields.HasFlag(TokenUsageFields.Input | TokenUsageFields.CacheRead)
                && usage.CacheRead > usage.Input
            || reportedFields.HasFlag(TokenUsageFields.Input | TokenUsageFields.CacheWrite)
                && usage.CacheWrite > usage.Input
            || reportedFields.HasFlag(TokenUsageFields.Output | TokenUsageFields.Reasoning)
                && usage.Reasoning > usage.Output;
    }
}
