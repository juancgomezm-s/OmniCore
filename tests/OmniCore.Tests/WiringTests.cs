using OmniCore.Abstractions;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class WiringTests
{
    private sealed class Sink
    {
        public readonly List<OmniCore.Domain.DomainEventPayload> Events = new();

        public VoidBox Emit(OmniCore.Domain.DomainEventPayload payload)
        {
            Events.Add(payload);
            return VoidBox.Instance;
        }

        public bool Any(string eventName)
        {
            foreach (var e in Events)
            {
                if (e.Type().ToString() == eventName)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    public async Task Ask_emits_full_interaction_request()
    {
        // ADR-0034/0036 §5: un Ask abre PermissionRequested + InteractionRequested (con opciones).
        var sink = new Sink();
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", OmniCore.Domain.PermissionDecision.Ask);
        var runtime = ToolRuntime.For(FakeCatalog.Default(), policy, sink.Emit);

        runtime.Run(
            new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "pc-x", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), false, TestContext.Current.CancellationToken);

        Assert.True(sink.Any("toolcall.permission_requested"));
        Assert.True(sink.Any("interaction.requested"));
        Assert.True(sink.Any("toolcall.permission_denied"));
        Assert.False(sink.Any("toolcall.authorized"), "Ask denegado nunca autoriza");
    }

    [Fact]
    public async Task TokenCounter_counts_words_deterministically()
    {
        var counter = new FakeTokenCounter();
        var item = new OmniCore.Domain.ContextItem("id", OmniCore.Domain.ContextItemKind.WorkingState, "a b c d e", 0,
            OmniCore.Domain.ContextPriority.Pinned, OmniCore.Domain.RetentionPolicy.RegenerateEachTurn,
            new OmniCore.Domain.ContextProvenance("test", OmniCore.Domain.ContributionCategory.WorkingState, "test",
                OmniCore.Domain.ScopeLevel.Session, false));

        var count = await counter.CountAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(5, count);
        Assert.Equal(TokenCountAccuracy.Estimated, counter.Accuracy);
        Assert.Equal("fake:words/1", counter.Id.ToString());
    }
}