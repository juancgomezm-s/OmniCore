namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Read-only admission state. Missing remaining tokens means unknown, never zero.</summary>
internal sealed record RunTokenBudgetState(long? Limit, long? Remaining, string? Limitation,
    long? ObservedSettledTokens = null);

/// <summary>
/// Reconstructs input + output consumption for a finite Run token budget. Reasoning and cache
/// details are included in output/input, not added twice. Includes context-service invocations.
/// An unsettled primary invocation is tolerated only when the live process still owns it and a
/// durable full-ceiling reservation proves its possible consumption. The pool counts that bound
/// separately when admitting another invocation; it is never treated as zero usage.
/// </summary>
internal static class RunTokenBudgetReader
{
    public static RunTokenBudgetState Read(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        RunId runId, long? limit, Func<DomainEvent, ModelStepStarted, bool>? ownedPendingPrimary = null)
    {
        if (limit is null) return new(null, null, null);
        if (limit < 0) return new(limit, null, "invalid token limit");
        var pendingSteps = new Dictionary<(TurnId Turn, int Index), (DomainEvent Event, ModelStepStarted Start)>();
        var pendingMeta = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<EventId>();
        var externalReceipts = events.Where(evt => evt.RunId == runId)
            .Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ExternalLaneUsageRecorded)
            .GroupBy(item => (item.Event.ExecutionId, item.Event.LaneId))
            .ToDictionary(group => group.Key, group => group.ToArray());
        long total = 0;
        string? limitation = null;
        var usageUnknown = false;

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
                    if (!pendingSteps.TryAdd((step.TurnId, step.StepIndex), (evt, step)))
                    { limitation = "duplicate model invocation"; usageUnknown = true; }
                    break;
                case ModelStepCompleted step:
                {
                    var matchedStart = pendingSteps.Remove((step.TurnId, step.StepIndex), out var started);
                    if (!matchedStart)
                    { limitation = "unmatched model completion"; usageUnknown = true; }
                    AddUsage(step.Usage, step.ReportedUsageFields ?? TokenUsageFields.All, step.GenerationAttempts,
                        matchedStart && ExternalReceiptMatches(evt, started.Event, step, externalReceipts));
                    break;
                }
                case ModelStepNotDispatched step:
                    if (!pendingSteps.Remove((step.TurnId, step.StepIndex))) limitation = "unmatched no-dispatch marker";
                    break;
                case MetaModelInvocationStarted meta:
                    if (!pendingMeta.Add(meta.InvocationId)) limitation = "duplicate context-service invocation";
                    break;
                case MetaModelInvocationCompleted meta:
                    if (!pendingMeta.Remove(meta.InvocationId))
                    { limitation = "unmatched context-service completion"; usageUnknown = true; }
                    AddUsage(meta.Usage, meta.ReportedUsageFields ?? TokenUsageFields.All, meta.GenerationAttempts);
                    break;
                case MetaModelInvocationFailed meta:
                    if (!pendingMeta.Remove(meta.InvocationId))
                    { limitation = "unmatched context-service failure"; usageUnknown = true; }
                    AddUsage(meta.Usage, meta.ReportedUsageFields ?? TokenUsageFields.All, meta.GenerationAttempts);
                    break;
                case MetaModelInvocationNotDispatched meta:
                    if (!pendingMeta.Remove(meta.InvocationId)) limitation = "unmatched context-service no-dispatch marker";
                    break;
            }
        }

        if (pendingSteps.Any(item => ownedPendingPrimary?.Invoke(item.Value.Event, item.Value.Start) != true)
            || pendingMeta.Count != 0) limitation ??= "unsettled provider invocation";
        if (externalReceipts.Any(group => !IsConsumedExternalReceipt(group.Key, group.Value, events, codecs, runId)))
        {
            limitation ??= "unmatched external usage receipt";
            usageUnknown = true;
        }
        return limitation is null
            ? new(limit, limit.Value - total, null, total)
            : new(limit, null, limitation, usageUnknown ? null : total);

        void AddUsage(TokenUsage? usage, TokenUsageFields fields, GenerationRequestAttemptEvidence? attempts,
            bool completeExternalReceipt = false)
        {
            if (!completeExternalReceipt && attempts is not { HasCompleteUsageCoverage: true })
            {
                limitation = "generation attempt usage coverage unknown";
                usageUnknown = true;
                return;
            }
            if (usage is null || !fields.HasFlag(TokenUsageFields.Input | TokenUsageFields.Output)
                || TokenUsageValidation.IsInvalid(usage, fields))
            {
                limitation = "unreported or invalid input/output usage";
                usageUnknown = true;
                return;
            }
            try { total = checked(total + checked(usage.Input + usage.Output)); }
            catch (OverflowException) { limitation = "token usage overflow"; usageUnknown = true; }
        }
    }

    private static bool ExternalReceiptMatches(DomainEvent completion, DomainEvent start,
        ModelStepCompleted step,
        IReadOnlyDictionary<(ExecutionId? ExecutionId, LaneId? LaneId), (DomainEvent Event, DomainEventPayload Payload)[]> receipts)
    {
        if (completion.RunId is not { } run || completion.ExecutionId is not { } execution
            || completion.LaneId is not { } lane || start.RunId != run || start.ExecutionId != execution
            || start.LaneId != lane || !receipts.TryGetValue((execution, lane), out var matches)
            || matches.Length != 1 || matches[0].Payload is not ExternalLaneUsageRecorded receipt
            || receipt.ExecutionId != execution || receipt.LaneId != lane
            || !receipt.UsageCoverageComplete
            || receipt.InputTokens is not { } rawInput || receipt.OutputTokens is not { } rawOutput
            || rawInput < 0 || rawOutput < 0) return false;
        try
        {
            var cacheRead = receipt.CacheReadInputTokens ?? 0;
            var cacheCreation = receipt.CacheCreationInputTokens ?? 0;
            return cacheRead >= 0 && cacheCreation >= 0
                && checked(rawInput + cacheRead + cacheCreation) == step.Usage.Input
                && rawOutput == step.Usage.Output
                && (step.ReportedUsageFields ?? TokenUsageFields.All)
                    .HasFlag(TokenUsageFields.Input | TokenUsageFields.Output);
        }
        catch (OverflowException) { return false; }
    }

    private static bool IsConsumedExternalReceipt((ExecutionId? ExecutionId, LaneId? LaneId) key,
        (DomainEvent Event, DomainEventPayload Payload)[] receipts, IReadOnlyList<DomainEvent> events,
        IEventCodecRegistry codecs, RunId run)
    {
        if (receipts.Length != 1 || key.ExecutionId is not { } execution || key.LaneId is not { } lane
            || receipts[0].Payload is not ExternalLaneUsageRecorded receipt
            || receipt.ExecutionId != execution || receipt.LaneId != lane) return false;
        var executionEvents = events.Where(evt => evt.RunId == run && evt.ExecutionId == execution
                && evt.LaneId == lane)
            .Select(evt => (Event: evt, Payload: codecs.Decode(evt))).ToArray();
        var starts = executionEvents.Where(item => item.Payload is ModelStepStarted)
            .Select(item => (item.Event, Start: (ModelStepStarted)item.Payload)).ToArray();
        var completions = executionEvents.Where(item => item.Payload is ModelStepCompleted)
            .Select(item => (item.Event, Step: (ModelStepCompleted)item.Payload)).ToArray();
        return starts.Length == 1 && completions.Length == 1
            && ExternalReceiptMatches(completions[0].Event, starts[0].Event, completions[0].Step,
                new Dictionary<(ExecutionId? ExecutionId, LaneId? LaneId), (DomainEvent Event, DomainEventPayload Payload)[]>
                { [key] = receipts });
    }
}
