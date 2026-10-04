namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Run-scoped durable debt; a new boundary cannot erase it. Unknown/partial effects
/// remain pending; only explicit no-effect outcomes or covered IDs release debt.</summary>
public static class PostEditValidationProjection
{
    public static IReadOnlyList<ToolCallId> Pending(RunId runId, IEventCodecRegistry codecs,
        IReadOnlyList<DomainEvent> events)
    {
        var pending = new HashSet<ToolCallId>();
        foreach (var evt in events)
        {
            if (evt.RunId != runId) continue;
            switch (codecs.Decode(evt))
            {
                case PostEditValidationPending edit when edit.RunId == runId:
                    pending.Add(edit.ToolCallId);
                    break;
                case PostEditValidationConsumed validation when validation.RunId == runId
                    && validation.Gate is "build" or "test":
                    pending.ExceptWith(validation.EditIds);
                    break;
                case ToolCallFailed failed when failed.EffectOutcome == EffectOutcome.None:
                    pending.Remove(failed.ToolCallId);
                    break;
                case ToolCallReconciled reconciled when reconciled.Outcome == ReconciliationOutcome.NotApplied:
                    pending.Remove(reconciled.ToolCallId);
                    break;
            }
        }
        return pending.ToArray();
    }
}
