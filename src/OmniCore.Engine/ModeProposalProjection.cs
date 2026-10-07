namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Session-scoped advisory history, independent of effective mode/authority.</summary>
public static class ModeProposalProjection
{
    public static IReadOnlyList<RunModeProposed> Replay(SessionId sessionId, RunId runId,
        IEventCodecRegistry codecs, IEnumerable<DomainEvent> events)
    {
        var journal = events.ToArray();
        if (journal.Any(evt => evt.SessionId != sessionId))
            throw Invalid("foreign session");
        CanonicalStateTracker.Replay(codecs, journal);
        var result = new List<RunModeProposed>();
        var preceding = new List<DomainEvent>();
        var seen = new HashSet<ToolCallId>();
        foreach (var evt in journal)
        {
            if (codecs.Decode(evt) is RunModeProposed proposed)
            {
                if (evt.RunId != proposed.RunId || evt.CorrelationId != proposed.RunId
                    || evt.TurnId != proposed.TurnId || evt.ToolCallId != proposed.ToolCallId)
                    throw Invalid("proposal envelope mismatch");
                Validate(proposed);
                var authority = RunProjection.Replay(sessionId, proposed.RunId, codecs, preceding).ModeAuthority;
                if (authority is null || proposed.From != authority.Mode
                    || proposed.AuthorityRevision != authority.Revision
                    || proposed.ObjectiveRevision != authority.ObjectiveRevision
                    || proposed.ObjectiveDigest != authority.ObjectiveDigest
                    || proposed.PolicyRevision != authority.PolicyRevision)
                    throw Invalid("stale or foreign authority snapshot");
                var requests = preceding.Where(item => item.RunId == proposed.RunId
                    && item.TurnId == proposed.TurnId && codecs.Decode(item) is ToolCallRequested requested
                    && requested.ToolCallId == proposed.ToolCallId && requested.ToolName == "mode.propose").ToArray();
                var successes = preceding.Where(item => item.RunId == proposed.RunId
                    && item.TurnId == proposed.TurnId && codecs.Decode(item) is ToolCallSucceeded succeeded
                    && succeeded.ToolCallId == proposed.ToolCallId).ToArray();
                if (requests.Length != 1 || successes.Length != 1
                    || !SameScope(evt, requests[0]) || !SameScope(evt, successes[0])
                    || !ModeProposeTool.TryParse(((ToolCallRequested)codecs.Decode(requests[0])).ArgumentsJson,
                        out var target, out var reason) || target != proposed.To || reason != proposed.Reason
                    || !seen.Add(proposed.ToolCallId))
                    throw Invalid("proposal lacks a unique matching model tool receipt");
                if (proposed.RunId == runId) result.Add(proposed);
            }
            preceding.Add(evt);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static bool SameScope(DomainEvent proposed, DomainEvent receipt) =>
        proposed.TaskId is not null && proposed.LaneId is not null
        && proposed.SessionId == receipt.SessionId && proposed.RunId == receipt.RunId
        && proposed.CorrelationId == receipt.CorrelationId && proposed.TaskId == receipt.TaskId
        && proposed.LaneId == receipt.LaneId && proposed.TurnId == receipt.TurnId
        && proposed.ToolCallId == receipt.ToolCallId && proposed.ExecutionId == receipt.ExecutionId;

    internal static void Validate(RunModeProposed proposed)
    {
        if (proposed.RunId.Value == Guid.Empty || proposed.TurnId.Value == Guid.Empty
            || proposed.ToolCallId.Value == Guid.Empty || !Enum.IsDefined(proposed.From)
            || !Enum.IsDefined(proposed.To) || string.IsNullOrWhiteSpace(proposed.Reason)
            || proposed.Reason.Length > 512 || proposed.AuthorityRevision < 0
            || proposed.ObjectiveRevision <= 0 || proposed.PolicyRevision <= 0
            || string.IsNullOrWhiteSpace(proposed.ObjectiveDigest)) throw Invalid("invalid proposal metadata");
    }

    private static InvalidStateTransitionException Invalid(string reason) =>
        new("run mode proposal", reason, "run.mode_proposed");
}
