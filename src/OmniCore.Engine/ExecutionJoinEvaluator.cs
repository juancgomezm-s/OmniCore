using OmniCore.Domain;

namespace OmniCore.Engine;

/// <summary>Deterministic readiness over explicitly accepted results. Completion alone is not acceptance.
/// A missing/failed member waits; the default supervision policy never silently weakens a join.</summary>
public static class ExecutionJoinEvaluator
{
    public static IReadOnlyList<ExecutionId> SatisfyingMembers(ExecutionJoin join,
        IReadOnlySet<ExecutionId> accepted)
    {
        join.Policy.Validate(join.MemberExecutionIds);
        var available = join.MemberExecutionIds.Where(accepted.Contains).ToArray();
        return join.Policy.Kind switch
        {
            JoinKind.All => available.Length == join.MemberExecutionIds.Count ? available : [],
            JoinKind.Any => available.Take(1).ToArray(),
            JoinKind.Quorum => available.Length >= join.Policy.RequiredCount!.Value
                ? available.Take(join.Policy.RequiredCount.Value).ToArray() : [],
            JoinKind.Explicit => join.Policy.RequiredExecutionIds!.All(accepted.Contains)
                ? join.MemberExecutionIds.Where(id => join.Policy.RequiredExecutionIds!.Contains(id)).ToArray() : [],
            _ => throw new ArgumentException("Unknown join policy."),
        };
    }
}
