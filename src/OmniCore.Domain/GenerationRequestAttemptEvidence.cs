namespace OmniCore.Domain;

using System.Text.Json.Serialization;

/// <summary>
/// Durable observation of generation-request sends for one provider invocation. This is not a
/// token-usage or billing receipt: a zero observed count is not evidence that the invocation was free.
/// </summary>
public sealed record GenerationRequestAttemptEvidence(
    long ObservedGenerationSends,
    long? MaximumGenerationRequestAttempts)
{
    /// <summary>Whether the observation is structurally consistent with its declared finite bound.</summary>
    [JsonIgnore]
    public bool IsValid => ObservedGenerationSends >= 0
        && (MaximumGenerationRequestAttempts is null
            || MaximumGenerationRequestAttempts > 0
                && ObservedGenerationSends <= MaximumGenerationRequestAttempts.Value);

    /// <summary>
    /// Whether a terminal response's usage can cover the whole invocation: either exactly one send
    /// was observed under a positive bound, or the bound of one limits an unobserved counter to a
    /// single response. The latter is a bounded inference, not a measured send count.
    /// </summary>
    [JsonIgnore]
    public bool HasCompleteUsageCoverage => IsValid
        && MaximumGenerationRequestAttempts is > 0
        && (ObservedGenerationSends == 1
            || ObservedGenerationSends == 0 && MaximumGenerationRequestAttempts == 1);
}
