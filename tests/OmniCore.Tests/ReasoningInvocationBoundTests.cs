using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

/// <summary>Declared configuration bounds, not measured usage or real provider billing.</summary>
public sealed class ReasoningInvocationBoundTests
{
    [Theory]
    [InlineData(null, 2048L, 1L)]
    [InlineData(0L, 2048L, 1L)]
    [InlineData(-1L, 2048L, 1L)]
    [InlineData(8192L, null, 1L)]
    [InlineData(8192L, 2048L, null)]
    [InlineData(8192L, 2048L, 0L)]
    [InlineData(8192L, 2048L, -1L)]
    public void Missing_or_nonpositive_capacity_output_or_attempts_is_unknown_not_zero(
        long? capacity, long? output, long? attempts)
    {
        var selection = new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null,
            maxOutputTokens: output);
        Assert.Null(ModelInvocationCostBound.TokenCeiling(selection, capacity, attempts));
    }

    [Theory]
    [InlineData(long.MaxValue, 1L, 1L)]
    [InlineData(8192L, 2048L, long.MaxValue)]
    public void Overflow_is_unknown_instead_of_wrapped_or_clamped(long capacity, long output, long attempts)
    {
        var selection = new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null,
            maxOutputTokens: output);
        Assert.Null(ModelInvocationCostBound.TokenCeiling(selection, capacity, attempts));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(8192L)]
    [InlineData(100000L)]
    public void Estimate_never_replaces_declared_native_capacity_and_all_send_attempts_are_counted(long contextBudget)
    {
        var request = new ReasoningRequest("budget", 1024);
        var selection = new ModelSelection(new ModelIdValue("fixture"), contextBudget, ToolMode.Direct, request,
            maxOutputTokens: 2048);
        Assert.Equal(30720L, ModelInvocationCostBound.TokenCeiling(selection, 8192, 3));
        Assert.Equal(request, selection.Reasoning);
        Assert.Equal(contextBudget, selection.ContextBudget);
    }
}
