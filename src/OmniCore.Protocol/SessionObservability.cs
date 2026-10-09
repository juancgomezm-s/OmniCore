using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

public enum ChatActivityPhase
{
    WaitingForResponse, Reasoning, AnswerText, Tools, WaitingForApproval,
    Completed, Cancelled, Failed,
}

/// <summary>Content-free, non-canonical observation. Sequence is scoped to SessionId, not a journal sequence.</summary>
public sealed record ChatActivityEvent(string SessionId, string TurnId, string? RunId, string? LaneId,
    long Sequence, ChatActivityPhase Phase, bool FirstAnswerTextReceived, DateTimeOffset AsOf, string Source,
    bool ObservedInProcess = false);

public sealed record ContextComponent(string Kind, Metric<long?> Tokens);

/// <summary>The most recent submitted model request, not lifetime spending or a promise about hidden provider state.</summary>
public sealed record SessionContextMeasurement(string SessionId, string TurnId, int StepIndex, string ModelId,
    Metric<long?> Tokens, Metric<long?> Capacity, Metric<long?> UsableBudget, Metric<double?> UsedPercent,
    IReadOnlyList<ContextComponent> Components, Metric<long?> ReportedReasoningTokens, DateTimeOffset AsOf);
public sealed record LaneContextSnapshot(string TurnId, string? RunId, string? LaneId,
    SessionContextMeasurement? Context, SessionContextMeasurement? PendingContext);

/// <summary>Cache counters are breakdowns of Input, reasoning is a breakdown of Output; Total never adds them twice.</summary>
public sealed record ConversationUsageMeasurement(string SessionId, Metric<TokenTotals> Tokens, Metric<long?> Total,
    Metric<Money> Cost, long BasedOnJournalSequence, int ModelInvocations, int IncompleteInvocations,
    DateTimeOffset AsOf, TokenCounterMeasurements? Breakdown = null);
public sealed record TokenCounterMeasurements(Metric<long?> Input, Metric<long?> Output, Metric<long?> CacheRead,
    Metric<long?> CacheWrite);

public enum CreditScope { AccountBalance, KeyLimit }
public sealed record CreditMeasurement(CreditScope Scope, Metric<decimal?> Amount, string Unit, string? Currency, string? KeyId);
public sealed record ProviderUsageWindow(string Id, string? LimitId, int? DurationMinutes,
    Metric<double?> UsedPercent, Metric<double?> RemainingPercent, DateTimeOffset? ResetsAt, string? ResetDescription);
public sealed record ProviderQuotaSnapshot(string ProviderId, string? AccountId, string Source, DateTimeOffset AsOf,
    MetricAvailability Availability, IReadOnlyList<ProviderUsageWindow> Windows,
    IReadOnlyList<CreditMeasurement> Credits, string? Limitation, DateTimeOffset? LastQueryAttemptAt = null);

/// <summary>Replayable readings plus transient activity. No credentials, prompts, filenames, or response text.</summary>
public sealed record SessionObservabilitySnapshot(string SessionId, long BasedOnJournalSequence, DateTimeOffset AsOf,
    bool Updating, SessionContextMeasurement? Context, SessionContextMeasurement? PendingContext,
    ConversationUsageMeasurement Consumption, IReadOnlyList<ProviderQuotaSnapshot> Quotas,
    IReadOnlyList<ChatActivityEvent> Activities, long ActivitySequence,
    IReadOnlyList<LaneContextSnapshot>? LaneContexts = null);

public static class ObservabilityJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter<MetricAvailability>());
        options.Converters.Add(new JsonStringEnumConverter<ChatActivityPhase>());
        options.Converters.Add(new JsonStringEnumConverter<CreditScope>());
        return options;
    }
    public static string Encode(SessionObservabilitySnapshot snapshot) => JsonSerializer.Serialize(snapshot, ObservabilityJsonContext.Default.SessionObservabilitySnapshot);
    public static SessionObservabilitySnapshot? Decode(string json) => JsonSerializer.Deserialize(json, ObservabilityJsonContext.Default.SessionObservabilitySnapshot);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SessionObservabilitySnapshot))]
[JsonSerializable(typeof(LaneContextSnapshot))]
internal partial class ObservabilityJsonContext : JsonSerializerContext;
