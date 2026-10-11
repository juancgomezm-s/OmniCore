namespace OmniCore.Host;

using OmniCore.Domain;

internal sealed record ExternalLaneInvocationQuota(int AttemptNumber, string? SessionId, TaskBudget Budget);

/// <summary>Splits an external lane's durable budget into at most two non-refundable attempts.</summary>
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
        if (total.MaxCostUsd is not > 0m || total.MaxTokens is not > 1
            || total.MaxTurns is not > 1 || total.MaxToolCalls is not > 1)
            throw new InvalidDataException("ExternalLaneBudgetCannotBeReservedForTwoAttempts");

        var first = new TaskBudget(total.MaxCostUsd.Value / 2m, total.MaxTokens.Value / 2,
            total.MaxTurns.Value / 2, total.MaxToolCalls.Value / 2);
        var second = new TaskBudget(total.MaxCostUsd.Value - first.MaxCostUsd!.Value,
            total.MaxTokens.Value - first.MaxTokens!.Value,
            total.MaxTurns.Value - first.MaxTurns!.Value,
            total.MaxToolCalls.Value - first.MaxToolCalls!.Value);
        if (first.MaxCostUsd is not > 0m || first.MaxTokens is not > 0
            || first.MaxTurns is not > 0 || first.MaxToolCalls is not > 0
            || second.MaxCostUsd is not > 0m || second.MaxTokens is not > 0
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
            quota.Budget.MaxCostUsd!.Value, quota.Budget.MaxTokens!.Value,
            quota.Budget.MaxTurns!.Value, quota.Budget.MaxToolCalls!.Value);

    private static bool Matches(ExternalLaneInvocationReserved reservation, int attempt, string? session,
        TaskBudget budget) => reservation.AttemptNumber == attempt && reservation.SessionId == session
        && reservation.ReservedCostUsd == budget.MaxCostUsd
        && reservation.ReservedTokens == budget.MaxTokens
        && reservation.ReservedTurns == budget.MaxTurns
        && reservation.ReservedToolCalls == budget.MaxToolCalls;
}
