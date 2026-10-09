namespace OmniCore.Engine;

using OmniCore.Domain;

public enum FanOutMemberStatus { Queued, Running, Returned, Accepted, Rejected, ReworkRequested, Failed }

public sealed record FanOutMemberEvaluation(DelegationId DelegationId, FanOutMemberStatus Status,
    ArtifactRef? AcceptedResult = null);

public sealed record FanInEvaluation(bool Ready, bool RequiresAggregate,
    IReadOnlyList<ArtifactRef> MemberResults, string WaitingReason);

/// <summary>Pure deterministic FanIn policy. It never executes an agent, reads an artifact, or
/// infers acceptance from completion: every member must have an explicitly accepted result.</summary>
public static class FanInPolicyEvaluator
{
    public static FanInEvaluation Evaluate(FanOutGroup group, IReadOnlyList<FanOutMemberEvaluation> members)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);
        group.Validate();
        if (members.Count != group.MemberDelegationIds.Count
            || members.Where((member, index) => member.DelegationId != group.MemberDelegationIds[index]).Any())
            throw new ArgumentException("Fan-in members must match the group's stable membership order.", nameof(members));
        foreach (var member in members)
        {
            if (!Enum.IsDefined(member.Status)) throw new ArgumentException("Unknown member disposition.", nameof(members));
            if (member.Status == FanOutMemberStatus.Accepted && member.AcceptedResult is null)
                throw new ArgumentException("An accepted member requires its immutable result reference.", nameof(members));
            if (member.Status != FanOutMemberStatus.Accepted && member.AcceptedResult is not null)
                throw new ArgumentException("Only accepted member results can enter fan-in.", nameof(members));
        }

        var ready = members.All(member => member.Status == FanOutMemberStatus.Accepted);
        var reason = ready ? "Ready" : members.Any(member => member.Status is FanOutMemberStatus.ReworkRequested
            or FanOutMemberStatus.Rejected or FanOutMemberStatus.Failed) ? "WaitingForRework" : "WaitingForMembers";
        return new(ready, group.FanInPolicy == FanInPolicy.Aggregate,
            ready ? members.Select(member => member.AcceptedResult!).ToArray() : [], reason);
    }
}
