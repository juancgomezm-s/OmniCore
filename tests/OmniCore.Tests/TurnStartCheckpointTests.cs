using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Offline provider fixtures: verifies durable ordering, not authenticated usage.</summary>
public sealed class TurnStartCheckpointTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("cancel")]
    public void New_turn_notifies_after_commit_before_provider_even_when_provider_does_not_complete(string outcome)
    {
        using var fx = new InternalExplorerAskCommandTests.Fixture(RunMode.Act);
        var catalog = new FakeCatalog();
        var artifacts = new FileArtifactStore(Path.Combine(fx.Root, "checkpoint-cas"));
        var boostId = Guid.NewGuid();
        var request = new ReasoningRequest("high", null);
        var resolution = new ReasoningResolution(request, request, ReasoningSelectionSource.TurnBoost,
            turnBoostId: boostId);
        var notifications = new List<TurnStarted>();
        var providerCalls = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var turn = new ExplorerTurn((_, token) =>
        {
            providerCalls++;
            var observed = Assert.Single(notifications);
            Assert.Equal(boostId, observed.ReasoningResolution!.TurnBoostId);
            Assert.Contains(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<TurnStarted>(),
                persisted => persisted.TurnId == observed.TurnId);
            if (outcome == "fail") throw new InvalidOperationException("offline provider failure");
            if (outcome == "cancel")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("offline-checkpoint", "fixture", null));
        }, ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy([]), fx.Root), catalog,
            new ContextMaterializer(new FakeTokenCounter(), []),
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, request,
                reasoningResolution: resolution), fx.Store, fx.Codecs, artifacts,
            new InMemoryAuditSink(), new RedactionPolicy());

        var execution = fx.Server.ExecuteExplorerTurn(fx.Session, fx.Run,
            token => turn.Ask("fixture", "system", fx.Session, fx.Run, fx.Lane, "", token,
                turnStarted: started =>
                {
                    Assert.Equal(0, providerCalls);
                    Assert.Contains(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<TurnStarted>(),
                        persisted => persisted.TurnId == started.TurnId);
                    notifications.Add(started);
                }), cancellation.Token);

        Assert.Equal(1, providerCalls);
        var notification = Assert.Single(notifications);
        var durable = Assert.Single(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<TurnStarted>());
        Assert.Equal(notification.TurnId, durable.TurnId);
        Assert.True(resolution.IsEquivalentTo(durable.ReasoningResolution));
        if (outcome == "complete") Assert.Equal(StopReason.EndTurn, execution.Result!.StopReason);
        else Assert.False(execution.Result?.StopReason == StopReason.EndTurn);
    }
}
