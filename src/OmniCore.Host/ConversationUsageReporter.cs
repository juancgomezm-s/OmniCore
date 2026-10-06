using System.Globalization;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;

namespace OmniCore.Host;

public static partial class SessionUsageReporter
{
    /// <summary>Unique invocations, not repeated UsageUpdated samples or final-turn summaries.</summary>
    public static ConversationUsageMeasurement ReadConversation(IEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts, SessionId session)
    {
        var events = store.ReadFrom(session, 1).ToArray();
        var steps = new Dictionary<string, (TokenUsage? Usage, decimal? Cost, TokenUsageFields Fields)>();
        var stepTurns = new HashSet<TurnId>();
        var legacy = new Dictionary<TurnId, ModelCompleted>();
        var conflict = false;
        void Start(string key) { steps.TryAdd(key, (null, null, TokenUsageFields.None)); }
        void Complete(string key, TokenUsage? usage, decimal? cost, TokenUsageFields fields = TokenUsageFields.Input | TokenUsageFields.Output)
        {
            if (steps.TryGetValue(key, out var prior) && prior.Usage is not null && prior != (usage, cost, fields)) conflict = true;
            else steps[key] = (usage, cost, fields);
        }
        foreach (var e in events)
        {
            switch (codecs.Decode(e))
            {
                case ModelStepStarted s:
                    stepTurns.Add(s.TurnId); Start("step:" + s.TurnId + ":" + s.StepIndex); break;
                case ModelStepCompleted s:
                    stepTurns.Add(s.TurnId); Complete("step:" + s.TurnId + ":" + s.StepIndex, s.Usage, s.CostUsd,
                        s.ReportedUsageFields ?? (TokenUsageFields.Input | TokenUsageFields.Output)); break;
                case MetaModelInvocationStarted s: Start("meta:" + s.InvocationId); break;
                case MetaModelInvocationCompleted s: Complete("meta:" + s.InvocationId, s.Usage, s.CostUsd, s.ReportedUsageFields ?? (TokenUsageFields.Input | TokenUsageFields.Output)); break;
                case MetaModelInvocationFailed s: Complete("meta:" + s.InvocationId, s.Usage, s.CostUsd, s.ReportedUsageFields ?? (TokenUsageFields.Input | TokenUsageFields.Output)); break;
                case ModelCompleted s: legacy.TryAdd(s.TurnId, s); break;
            }
        }
        foreach (var (turn, completed) in legacy)
        {
            if (stepTurns.Contains(turn)) continue; // a final summary is the same spend, never an additional call.
            TokenUsage? usage = null;
            decimal? cost = null;
            if (completed.ResponseArtifact is { } artifact && artifacts.GetText(artifact.Hash) is { } json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var names = new[] { "input", "output", "cacheRead", "cacheWrite", "reasoning" };
                    var values = new long[5];
                    if (root.TryGetProperty("omnicoreUsage", out _) && names.Select((name, index) =>
                        root.TryGetProperty(name, out var value) && value.TryGetInt64(out values[index]) && values[index] >= 0).All(v => v))
                        usage = new(values[0], values[1], values[2], values[3], values[4]);
                    if (root.TryGetProperty("costUsd", out var c) && c.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(c.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) cost = parsed;
                }
                catch (JsonException) { }
            }
            Complete("legacy:" + turn, usage, cost);
        }
        var measured = steps.Values.Where(s => s.Usage is not null
            && !TokenUsageValidation.IsInvalid(s.Usage, s.Fields)).ToArray();
        var tokens = new TokenTotals(measured.Sum(s => s.Usage!.Input), measured.Sum(s => s.Usage!.Output),
            measured.Sum(s => s.Usage!.CacheRead), measured.Sum(s => s.Usage!.CacheWrite));
        var incomplete = steps.Count - measured.Count(s => s.Fields.HasFlag(TokenUsageFields.Input | TokenUsageFields.Output));
        var allTokens = incomplete == 0 && !conflict;
        var allCost = allTokens && measured.All(s => s.Cost is >= 0);
        var date = events.LastOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow;
        Metric<long?> Counter(TokenUsageFields field, long sum) => new(!conflict && incomplete == 0 && measured.All(s => s.Fields.HasFlag(field))
            ? MetricAvailability.Reported : MetricAvailability.Unknown,
            !conflict && incomplete == 0 && measured.All(s => s.Fields.HasFlag(field)) ? sum : null, "journal:provider-reported-fields", date);
        return new(session.ToString(), new(allTokens ? MetricAvailability.Reported : MetricAvailability.Unknown,
                allTokens ? tokens : null, "journal:model_step+meta_model;legacy-fallback", date),
            new(allTokens ? MetricAvailability.Reported : MetricAvailability.Unknown,
                allTokens ? tokens.Input + tokens.Output : null, "journal:unique-invocations", date),
            new(allCost ? MetricAvailability.Estimated : MetricAvailability.Unknown,
                allCost ? new Money(measured.Sum(s => s.Cost!.Value), "USD") : null, "declared-price-per-invocation", date),
            events.LastOrDefault()?.Sequence ?? 0, steps.Count, incomplete, date,
            new(Counter(TokenUsageFields.Input, tokens.Input), Counter(TokenUsageFields.Output, tokens.Output),
                Counter(TokenUsageFields.CacheRead, tokens.CacheRead), Counter(TokenUsageFields.CacheWrite, tokens.CacheWrite)));
    }
}
