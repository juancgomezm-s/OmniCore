namespace OmniCore.Host;

using OmniCore.Domain;

internal sealed record ExternalLaneInvocationQuota(int AttemptNumber, string? SessionId, TaskBudget Budget);

/// <summary>Splits declared external-lane quotas into at most two non-refundable attempts.</summary>
internal static class ExternalLaneBudgetReservations
{
    internal static ExternalLaneInvocationQuota Next(TaskBudget total,
        IReadOnlyList<ExternalLaneInvocationReserved> reservations, ExecutionId executionId,
        LaneId laneId, string? boundSessionId, bool resuming)
    {
        ArgumentNullException.ThrowIfNull(total);
        ArgumentNullException.ThrowIfNull(reservations);
        if (reservations.Any(item => item.ExecutionId != executionId || item.LaneId != laneId))
            throw new InvalidDataException("ExternalLaneBudgetReservationOwnershipMismatch");
        if (total.MaxCostUsd is <= 0m or > 100m || total.MaxTokens is <= 1
            || total.MaxTurns is not > 1 || total.MaxToolCalls is not > 1)
            throw new InvalidDataException("ExternalLaneBudgetCannotBeReservedForTwoAttempts");

        decimal? firstCost = total.MaxCostUsd is { } totalCost ? totalCost / 2m : null;
        long? firstTokens = total.MaxTokens is { } totalTokens ? totalTokens / 2 : null;
        var first = new TaskBudget(firstCost, firstTokens,
            total.MaxTurns.Value / 2, total.MaxToolCalls.Value / 2);
        var second = new TaskBudget(total.MaxCostUsd is { } cost ? cost - first.MaxCostUsd!.Value : null,
            total.MaxTokens is { } tokens ? tokens - first.MaxTokens!.Value : null,
            total.MaxTurns.Value - first.MaxTurns!.Value,
            total.MaxToolCalls.Value - first.MaxToolCalls!.Value);
        if (first.MaxCostUsd is <= 0m || first.MaxTokens is <= 0
            || first.MaxTurns is not > 0 || first.MaxToolCalls is not > 0
            || second.MaxCostUsd is <= 0m || second.MaxTokens is <= 0
            || second.MaxTurns is not > 0 || second.MaxToolCalls is not > 0)
            throw new InvalidDataException("ExternalLaneBudgetCannotBeReservedForTwoAttempts");

        if (reservations.Count == 0)
        {
            if (resuming || boundSessionId is not null)
                throw new InvalidDataException("ExternalLaneHistoricalAttemptHasNoBudgetReservation");
            return new ExternalLaneInvocationQuota(1, null, first);
        }
        if (reservations.Count != 1 || !Matches(reservations[0], 1, null, first))
            throw new InvalidDataException("ExternalLaneBudgetReservationInvalid");
        if (!resuming || string.IsNullOrWhiteSpace(boundSessionId))
            throw new InvalidDataException("ExternalLaneRecoveryRequiresManualResolution");
        return new ExternalLaneInvocationQuota(2, boundSessionId, second);
    }

    internal static ExternalLaneInvocationReserved ToEvent(ExecutionId execution, LaneId lane,
        ExternalLaneInvocationQuota quota) => new(execution, lane, quota.AttemptNumber, quota.SessionId,
            quota.Budget.MaxCostUsd, quota.Budget.MaxTokens,
            quota.Budget.MaxTurns!.Value, quota.Budget.MaxToolCalls!.Value);

    private static bool Matches(ExternalLaneInvocationReserved reservation, int attempt, string? session,
        TaskBudget budget) => reservation.AttemptNumber == attempt && reservation.SessionId == session
        && reservation.ReservedCostUsd == budget.MaxCostUsd
        && reservation.ReservedTokens == budget.MaxTokens
        && reservation.ReservedTurns == budget.MaxTurns
        && reservation.ReservedToolCalls == budget.MaxToolCalls;
}
