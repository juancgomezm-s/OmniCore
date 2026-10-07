using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class M5QualificationTerminalResponseTests
{
    [Theory]
    [InlineData(StopReason.Error)]
    [InlineData(StopReason.Cancelled)]
    [InlineData(StopReason.ContentFilter)]
    [InlineData(StopReason.Refusal)]
    [InlineData(StopReason.ContextOverflow)]
    [InlineData(StopReason.ToolUse)]
    [InlineData(StopReason.InputRequired)]
    public async Task Failed_blocked_or_unfinished_probe_is_not_passed_by_matching_text(StopReason reason)
    {
        var result = await Run(reason);
        Assert.Equal(ProbeStatus.Error, result.Status);
        Assert.Equal(0, result.Score);
        Assert.Null(result.Output);
        Assert.NotNull(result.Error);
        Assert.Equal(new TokenUsage(10, 5, 2, 1, 1), result.Usage);
        Assert.Equal(TokenUsageFields.All, result.ReportedUsageFields);
        Assert.Equal(0.000020m, result.CostUsd); // reported valid usage can still have incurred cost
    }

    [Theory]
    [InlineData(StopReason.EndTurn)]
    [InlineData(StopReason.StopSequence)]
    [InlineData(StopReason.MaxOutputTokens)]
    public async Task Completed_text_is_scored_by_exact_oracle_not_an_end_turn_only_rule(StopReason reason)
    {
        var result = await Run(reason);
        Assert.Equal(ProbeStatus.Passed, result.Status);
        Assert.Equal(1, result.Score);
        Assert.Equal("fox", result.Output);
        Assert.Null(result.Error);
    }

    private static Task<ProbeResult> Run(StopReason reason)
    {
        var probe = QuickProbeSuite.Probes().Single(p => p.Id.ToString() == "reading-basic");
        var runner = new ProbeRunner(new Provider(reason), ProbeRunner.DefaultPerProbeTimeout,
            usage => (usage.Input + usage.Output * 2m) / 1_000_000m);
        return runner.RunProbeAsync(new ProbeRequest(probe,
            new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null)), CancellationToken.None);
    }

    [Theory]
    [InlineData(ProbeKind.Reading, "fox", "fox", " extra", ProbeStatus.Failed)]
    [InlineData(ProbeKind.Reasoning, "42", "42", " tokens", ProbeStatus.Failed)]
    [InlineData(ProbeKind.StructuredOutput, "{\"count\":1}", "{\"count\":1}", " trailing", ProbeStatus.Failed)]
    [InlineData(ProbeKind.StructuredOutput, "{\"count\":1}", "{\"count\":", "1}", ProbeStatus.Passed)]
    [InlineData(ProbeKind.Reading, "fox", "f", "ox", ProbeStatus.Passed)]
    public async Task Exact_oracle_scores_all_visible_text_not_only_the_first_block(
        ProbeKind kind, string expected, string first, string second, ProbeStatus status)
    {
        var probe = new Probe(ProbeId.WellKnown("visible-blocks"), kind, "fixture", expected, 0m);
        var runner = new ProbeRunner(new Provider(StopReason.EndTurn,
            [new TextBlock(first), new TextBlock(second)]));
        var result = await runner.RunProbeAsync(new ProbeRequest(probe,
            new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null)),
            TestContext.Current.CancellationToken);
        Assert.Equal(first + second, result.Output);
        Assert.Equal(status, result.Status);
        Assert.Equal(status == ProbeStatus.Passed ? 1d : 0d, result.Score);
    }

    [Fact]
    public void Reasoning_and_nested_tool_results_are_not_visible_answer_text()
    {
        var reasoning = new ReasoningBlock("not the answer", ReasoningVisibility.Full, null);
        var tool = new ToolResultBlock(ToolCallId.New(), [new TextBlock("not the answer")], false);
        static ModelResponse Response(params ContentBlock[] content) => new(content, StopReason.EndTurn,
            new TokenUsage(10, 5, 0, 0, 0), null, new ProviderMetadata("fixture", "scripted", null));
        Assert.Null(ProbeScorer.ExtractText(Response(reasoning, tool)));
        Assert.Equal("fox", ProbeScorer.ExtractText(Response(reasoning, new TextBlock("fox"), tool)));
        Assert.Equal(string.Empty, ProbeScorer.ExtractText(Response(new TextBlock(string.Empty))));
    }

    private sealed class Provider(StopReason reason, IReadOnlyList<ContentBlock>? content = null) : IModelProvider
    {
        public ProviderCapabilities Capabilities => new(true, false, false);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(content ?? [new TextBlock("fox")], reason,
                new TokenUsage(10, 5, 2, 1, 1), null, new ProviderMetadata("fixture", "scripted", null),
                TokenUsageFields.All));
        }
    }
}
