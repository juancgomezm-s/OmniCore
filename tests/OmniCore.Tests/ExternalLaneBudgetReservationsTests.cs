using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class ExternalLaneBudgetReservationsTests
{
    [Fact]
    public void Two_attempts_split_each_budget_without_refunding_uncertain_attempt()
    {
        var execution = ExecutionId.New();
        var lane = LaneId.New();
        var total = new TaskBudget(1m, 40_001, 5, 9);

        var first = ExternalLaneBudgetReservations.Next(total, [], execution, lane, null, resuming: false);

        Assert.Equal(1, first.AttemptNumber);
        Assert.Null(first.SessionId);
        Assert.Equal(new TaskBudget(0.5m, 20_000, 2, 4), first.Budget);
        var firstReservation = ExternalLaneBudgetReservations.ToEvent(execution, lane, first);
        var second = ExternalLaneBudgetReservations.Next(total, [firstReservation], execution, lane,
            "session-bound", resuming: true);

        Assert.Equal(2, second.AttemptNumber);
        Assert.Equal("session-bound", second.SessionId);
        Assert.Equal(new TaskBudget(0.5m, 20_001, 3, 5), second.Budget);
        Assert.Equal(total.MaxCostUsd, first.Budget.MaxCostUsd + second.Budget.MaxCostUsd);
        Assert.Equal(total.MaxTokens, first.Budget.MaxTokens + second.Budget.MaxTokens);
        Assert.Equal(total.MaxTurns, first.Budget.MaxTurns + second.Budget.MaxTurns);
        Assert.Equal(total.MaxToolCalls, first.Budget.MaxToolCalls + second.Budget.MaxToolCalls);

        var secondReservation = ExternalLaneBudgetReservations.ToEvent(execution, lane, second);
        Assert.Throws<InvalidDataException>(() => ExternalLaneBudgetReservations.Next(total,
            [firstReservation, secondReservation], execution, lane, "session-bound", resuming: true));
    }

    [Fact]
    public void Resume_without_reservation_or_matching_session_is_rejected()
    {
        var execution = ExecutionId.New();
        var lane = LaneId.New();
        var total = new TaskBudget(1m, 100, 2, 2);
        var first = ExternalLaneBudgetReservations.ToEvent(execution, lane,
            ExternalLaneBudgetReservations.Next(total, [], execution, lane, null, resuming: false));

        Assert.Throws<InvalidDataException>(() => ExternalLaneBudgetReservations.Next(total,
            [], execution, lane, "legacy-session", resuming: true));
        Assert.Throws<InvalidDataException>(() => ExternalLaneBudgetReservations.Next(total,
            [first], execution, lane, null, resuming: false));
        Assert.Throws<InvalidDataException>(() => ExternalLaneBudgetReservations.Next(total,
            [first], execution, LaneId.New(), "session-bound", resuming: true));
        Assert.Throws<InvalidDataException>(() => ExternalLaneBudgetReservations.Next(
            new TaskBudget(0.01m, 1, 1, 1), [], execution, lane, null, resuming: false));
    }
}
