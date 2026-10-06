namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Engine;

public sealed class SpendGuardAccountingValidityTests
{
    [Fact]
    public void Token_overflow_does_not_wrap_or_mutate_the_last_valid_counters()
    {
        var guard = new SpendGuard(new TaskBudget(null, long.MaxValue, null, null));
        guard.AdvanceTurn(long.MaxValue);
        Assert.Throws<OverflowException>(() => guard.AdvanceTurn(1));
        Assert.Equal(long.MaxValue, guard.Tokens());
        Assert.Equal(1, guard.Turns());
    }

    [Fact]
    public void Negative_cost_is_rejected_instead_of_becoming_a_zero_cost_step()
    {
        var guard = new SpendGuard(new TaskBudget(1m, null, null, null));
        guard.AddCostUsd(.5m);
        Assert.Throws<ArgumentOutOfRangeException>(() => guard.AddCostUsd(-1m));
        Assert.Equal(.5m, guard.CostUsd());
        guard.AddCostUsd(0m);
        Assert.Equal(.5m, guard.CostUsd());
    }

    [Fact]
    public void Monetary_overflow_preserves_the_last_valid_total_instead_of_saturating()
    {
        var guard = new SpendGuard(new TaskBudget(null, null, null, null));
        guard.AddCostUsd(decimal.MaxValue - 1m);
        Assert.Throws<OverflowException>(() => guard.AddCostUsd(2m));
        Assert.Equal(decimal.MaxValue - 1m, guard.CostUsd());
    }
}
