using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;

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

    [Fact]
    public void Optional_cost_and_token_quotas_stay_absent_across_nonrefundable_attempts()
    {
        var execution = ExecutionId.New();
        var lane = LaneId.New();
        var total = new TaskBudget(null, null, 5, 9);

        var first = ExternalLaneBudgetReservations.Next(total, [], execution, lane, null, resuming: false);
        Assert.Equal(new TaskBudget(null, null, 2, 4), first.Budget);
        var firstReservation = ExternalLaneBudgetReservations.ToEvent(execution, lane, first);
        Assert.Null(firstReservation.ReservedCostUsd);
        Assert.Null(firstReservation.ReservedTokens);

        var second = ExternalLaneBudgetReservations.Next(total, [firstReservation], execution, lane,
            "session-bound", resuming: true);
        Assert.Equal(new TaskBudget(null, null, 3, 5), second.Budget);
        Assert.Null(ExternalLaneBudgetReservations.ToEvent(execution, lane, second).ReservedCostUsd);
        Assert.Null(ExternalLaneBudgetReservations.ToEvent(execution, lane, second).ReservedTokens);
    }

    [Fact]
    public void A_single_declared_token_or_cost_ceiling_is_split_without_inventing_the_other()
    {
        var execution = ExecutionId.New();
        var lane = LaneId.New();

        var tokenOnly = ExternalLaneBudgetReservations.Next(new TaskBudget(null, 8000, 4, 12),
            [], execution, lane, null, resuming: false);
        Assert.Equal(4000, tokenOnly.Budget.MaxTokens);
        Assert.Null(tokenOnly.Budget.MaxCostUsd);

        var costOnly = ExternalLaneBudgetReservations.Next(new TaskBudget(0.25m, null, 4, 12),
            [], execution, lane, null, resuming: false);
        Assert.Equal(0.125m, costOnly.Budget.MaxCostUsd);
        Assert.Null(costOnly.Budget.MaxTokens);
    }

    [Fact]
    public void Historical_explicit_reservation_values_still_match_the_split_plan()
    {
        var execution = ExecutionId.New();
        var lane = LaneId.New();
        var total = new TaskBudget(1m, 40_001, 5, 9);
        var first = ExternalLaneBudgetReservations.Next(total, [], execution, lane, null, resuming: false);
        var encoded = ExternalLaneBudgetReservations.ToEvent(execution, lane, first);

        var recovered = ExternalLaneBudgetReservations.Next(total, [encoded], execution, lane,
            "session-bound", resuming: true);
        Assert.Equal(new TaskBudget(0.5m, 20_001, 3, 5), recovered.Budget);
    }

    [Fact]
    public void Reservation_journal_keeps_v1_explicit_values_and_round_trips_absent_quotas()
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var execution = ExecutionId.New();
        var lane = LaneId.New();
        var codec = codecs.CodecFor(new ExternalLaneInvocationReserved(execution, lane, 1,
            null, 0.5m, 20_000, 2, 4).Type());
        var historical = new ExternalLaneInvocationReserved(execution, lane, 1,
            null, 0.5m, 20_000, 2, 4);
        var historicalEvent = DomainEvent.Create(session, historical.Type(), 1, null, run, run,
            null, lane, null, null, null, Array.Empty<ArtifactRef>(), codec.Encode(historical), executionId: execution);
        var historicalRead = Assert.IsType<ExternalLaneInvocationReserved>(codecs.Decode(historicalEvent));
        Assert.Equal(0.5m, historicalRead.ReservedCostUsd);
        Assert.Equal(20_000, historicalRead.ReservedTokens);

        var optional = new ExternalLaneInvocationReserved(execution, lane, 1,
            null, null, null, 2, 4);
        var optionalRead = Assert.IsType<ExternalLaneInvocationReserved>(
            codec.Decode(optional.Type(), codec.Encode(optional)));
        Assert.Null(optionalRead.ReservedCostUsd);
        Assert.Null(optionalRead.ReservedTokens);
        Assert.Equal(1, codecs.CurrentVersion(optional.Type()));
    }
}
