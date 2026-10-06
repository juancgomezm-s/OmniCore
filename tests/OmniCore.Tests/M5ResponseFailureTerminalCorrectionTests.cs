// Deterministic terminal-event controls for qualification.
// A ResponseFailed event carries no usage here and is terminal: the runner must not consume a
// following completion to manufacture a Passed result or attach that later usage to the failure.
using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class M5ResponseFailureTerminalCorrectionTests
{
    [Fact]
    public async Task ResponseFailed_is_terminal_and_does_not_consume_a_later_completion()
    {
        var probe = QuickProbeSuite.Probes().Single(candidate => candidate.Id.ToString() == "reading-basic");
        var provider = new FailureThenCompletionProvider(probe.Expected);
        var runner = new ProbeRunner(provider);
        var request = new ProbeRequest(probe, new ModelSelection(
            new ModelIdValue("m5-terminal-response-model"), 8192, ToolMode.Direct, null));

        var result = await runner.RunProbeAsync(request, CancellationToken.None);

        Assert.Equal(ProbeStatus.Error, result.Status);
        Assert.Equal(0.0, result.Score);
        Assert.Null(result.Usage);
        Assert.Equal(TokenUsageFields.None, result.ReportedUsageFields);
        Assert.Null(result.CostUsd);
        Assert.False(provider.CompletionWasConsumed);
    }

    [Fact]
    public async Task Max_output_tokens_with_an_exact_structured_answer_remains_scored_by_the_exact_oracle()
    {
        // Select the stable probe ID, not ProbeKind: quick deliberately has three structured probes.
        var probe = QuickProbeSuite.Probes().Single(candidate => candidate.Id.ToString() == "structured-output");
        var runner = new ProbeRunner(new ExactMaxOutputProvider(probe.Expected));
        var request = new ProbeRequest(probe, new ModelSelection(
            new ModelIdValue("m5-terminal-response-model"), 8192, ToolMode.Direct, null));

        var result = await runner.RunProbeAsync(request, CancellationToken.None);

        Assert.Equal(ProbeStatus.Passed, result.Status);
        Assert.Equal(1.0, result.Score);
    }

    private sealed class FailureThenCompletionProvider(string expected) : IModelProvider
    {
        public bool CompletionWasConsumed { get; private set; }
        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ResponseFailed("scripted-failure", "terminal failure event");
            yield return new ResponseCompleted(new ModelResponse([new TextBlock(expected)], StopReason.EndTurn,
                new TokenUsage(10, 5, 0, 0, 0), null, new ProviderMetadata("later", "fixture", null),
                TokenUsageFields.Input | TokenUsageFields.Output));
            CompletionWasConsumed = true;
        }
    }

    private sealed class ExactMaxOutputProvider(string expected) : IModelProvider
    {
        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse([new TextBlock(expected)],
                StopReason.MaxOutputTokens, new TokenUsage(10, 5, 0, 0, 0), null,
                new ProviderMetadata("max-tokens", "fixture", null)));
        }
    }
}
