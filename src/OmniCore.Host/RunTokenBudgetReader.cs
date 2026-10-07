namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Read-only admission state. Missing remaining tokens means unknown, never zero.</summary>
internal sealed record RunTokenBudgetState(long? Limit, long? Remaining, string? Limitation);

/// <summary>
/// Reconstructs input + output consumption for a finite Run token budget. Reasoning and cache
/// details are included in output/input, not added twice. Includes context-service invocations.
/// No reservations or M6 scheduling: the existing authoritative command boundary owns admission.
/// </summary>
internal static class RunTokenBudgetReader
{
    public static RunTokenBudgetState Read(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        RunId runId, long? limit)
    {
        if (limit is null) return new(null, null, null);
        if (limit < 0) return new(limit, null, "invalid token limit");
        var pendingSteps = new HashSet<(TurnId Turn, int Index)>();
        var pendingMeta = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<EventId>();
        long total = 0;
        string? limitation = null;

        foreach (var evt in events.OrderBy(item => item.Sequence))
        {
            if (!seen.Add(evt.EventId)) continue;
            var payload = codecs.Decode(evt);
            var origin = payload switch
            {
                MetaModelInvocationStarted meta => meta.RunId,
                MetaModelInvocationCompleted meta => meta.RunId,
                MetaModelInvocationFailed meta => meta.RunId,
                MetaModelInvocationNotDispatched meta => meta.RunId,
                _ => evt.RunId ?? evt.CorrelationId,
            };
            if (origin != runId) continue;

            switch (payload)
            {
                case ModelStepStarted step:
                    if (!pendingSteps.Add((step.TurnId, step.StepIndex))) limitation = "duplicate model invocation";
                    break;
                case ModelStepCompleted step:
                    if (!pendingSteps.Remove((step.TurnId, step.StepIndex))) limitation = "unmatched model completion";
                    AddUsage(step.Usage, step.ReportedUsageFields ?? TokenUsageFields.All, step.GenerationAttempts);
                    break;
                case ModelStepNotDispatched step:
                    if (!pendingSteps.Remove((step.TurnId, step.StepIndex))) limitation = "unmatched no-dispatch marker";
                    break;
                case MetaModelInvocationStarted meta:
                    if (!pendingMeta.Add(meta.InvocationId)) limitation = "duplicate context-service invocation";
                    break;
                case MetaModelInvocationCompleted meta:
                    if (!pendingMeta.Remove(meta.InvocationId)) limitation = "unmatched context-service completion";
                    AddUsage(meta.Usage, meta.ReportedUsageFields ?? TokenUsageFields.All, meta.GenerationAttempts);
                    break;
                case MetaModelInvocationFailed meta:
                    if (!pendingMeta.Remove(meta.InvocationId)) limitation = "unmatched context-service failure";
                    AddUsage(meta.Usage, meta.ReportedUsageFields ?? TokenUsageFields.All, meta.GenerationAttempts);
                    break;
                case MetaModelInvocationNotDispatched meta:
                    if (!pendingMeta.Remove(meta.InvocationId)) limitation = "unmatched context-service no-dispatch marker";
                    break;
            }
        }

        if (pendingSteps.Count != 0 || pendingMeta.Count != 0) limitation = "unsettled provider invocation";
        return limitation is null
            ? new(limit, limit.Value - total, null)
            : new(limit, null, limitation);

        void AddUsage(TokenUsage? usage, TokenUsageFields fields, GenerationRequestAttemptEvidence? attempts)
        {
            if (attempts is not { HasCompleteUsageCoverage: true })
            {
                limitation = "generation attempt usage coverage unknown";
                return;
            }
            if (usage is null || !fields.HasFlag(TokenUsageFields.Input | TokenUsageFields.Output)
                || TokenUsageValidation.IsInvalid(usage, fields))
            {
                limitation = "unreported or invalid input/output usage";
                return;
            }
            try { total = checked(total + checked(usage.Input + usage.Output)); }
            catch (OverflowException) { limitation = "token usage overflow"; }
        }
    }
}
