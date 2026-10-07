namespace OmniCore.Domain;

using System.Text.Json.Serialization;

/// <summary>
/// Declared route facts (ADR-0046 §3), not a heuristic, request, or permission.
/// Null means unreported; neither an empty list nor a legacy model name implies support.
/// Effort labels are opaque provider values, not a universal ranking or UltraCode selection.
/// </summary>
public sealed class ReasoningCapability
{
    public bool? Supported { get; }
    public IReadOnlyList<string>? EffortLevels { get; }
    public ReasoningReplayPolicy? ReplayPolicy { get; }
    /// <summary>Explicit User-configured fallback for UltraCode; null means no budget may be inferred.</summary>
    public int? UltraCodeBudgetTokens { get; }
    /// <summary>Explicit visible-output reserve paired with <see cref="UltraCodeBudgetTokens"/>.</summary>
    public int? UltraCodeOutputReserveTokens { get; }

    public static ReasoningCapability Unknown { get; } = new(null, null, null);

    /// <summary>Rejects only a contradiction with declared route facts, before admission
    /// or dispatch. Unknown is not permission, but neither is it an invented negative.
    /// Provider labels are compared exactly; this does not define their wire dialect.</summary>
    public void ValidateRequest(ReasoningRequest? request)
    {
        if (request is null) return;
        if (Supported == false || (EffortLevels is not null
            && !EffortLevels.Contains(request.Kind, StringComparer.Ordinal)))
            throw new InvalidOperationException("Selected reasoning is incompatible with declared route capability.");
    }

    [JsonConstructor]
    public ReasoningCapability(bool? supported, IReadOnlyList<string>? effortLevels = null,
        ReasoningReplayPolicy? replayPolicy = null, int? ultraCodeBudgetTokens = null,
        int? ultraCodeOutputReserveTokens = null)
    {
        if (replayPolicy is { } policy && !Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(replayPolicy));
        if (effortLevels is not null)
        {
            if (supported != true)
                throw new ArgumentException("Effort levels require declared reasoning support.", nameof(effortLevels));
            if (effortLevels.Any(string.IsNullOrWhiteSpace)
                || effortLevels.Distinct(StringComparer.Ordinal).Count() != effortLevels.Count)
                throw new ArgumentException("Effort levels must be non-empty, unique provider labels.", nameof(effortLevels));
        }
        if (ultraCodeBudgetTokens is not null || ultraCodeOutputReserveTokens is not null)
        {
            if (supported != true || effortLevels?.Contains("budget", StringComparer.Ordinal) != true
                || ultraCodeBudgetTokens is null or < 1024 || ultraCodeOutputReserveTokens is null or <= 0)
                throw new ArgumentException("UltraCode manual reasoning requires a declared budget capability, explicit budget, and positive output reserve.");
        }
        Supported = supported;
        EffortLevels = effortLevels is null ? null : Array.AsReadOnly(effortLevels.ToArray());
        ReplayPolicy = replayPolicy;
        UltraCodeBudgetTokens = ultraCodeBudgetTokens;
        UltraCodeOutputReserveTokens = ultraCodeOutputReserveTokens;
    }
}
