using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Real ExplorerTurn/tool/context wiring with a scripted provider, not authenticated usage.</summary>
public sealed class ExplorerTurnSteeringTests
{
    [Fact]
    public void Steering_waits_for_next_step_preserves_tools_and_continuation_and_replays_once()
    {
        using var fixture = new Fixture();
        var state = new ProviderState("fixture", "{\"continuation\":\"opaque\"}");
        var call = new ToolCallBlock(ToolCallId.New(), "list-fixture", "filesystem.list", "{\"path\":\".\"}");
        SteeringId first = SteeringId.New(), second = SteeringId.New();
        var requests = new List<ModelRequest>();
        ModelResponse Complete(ModelRequest request, CancellationToken token)
        {
            requests.Add(request);
            if (requests.Count == 1)
            {
                fixture.Receive(first, "first direction");
                fixture.Receive(second, "second direction");
                Assert.DoesNotContain(request.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
                    text => text.Text.Contains("direction", StringComparison.Ordinal));
                return new ModelResponse(new ContentBlock[] { call }, StopReason.ToolUse,
                    new TokenUsage(2, 1, 0, 0, 0), state, new ProviderMetadata("fixture", "test", null));
            }
            Assert.Equal(state, request.Continuation);
            return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "test", null));
        }
        var explorer = fixture.Explorer(Complete);
        var result = fixture.Ask(explorer);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(requests[0].Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
            text => text.Text.Contains("direction", StringComparison.Ordinal));
        var blocks = requests[1].Messages.SelectMany(message => message.Content).ToArray();
        var toolResult = Assert.Single(blocks.OfType<ToolResultBlock>(), item => item.Id == call.Id);
        Assert.False(toolResult.IsError);
        var firstText = Assert.Single(blocks.OfType<TextBlock>(), text => text.Text == "first direction");
        var secondText = Assert.Single(blocks.OfType<TextBlock>(), text => text.Text == "second direction");
        Assert.True(Array.IndexOf(blocks, toolResult) < Array.IndexOf(blocks, firstText));
        Assert.True(Array.IndexOf(blocks, firstText) < Array.IndexOf(blocks, secondText));
        var events = fixture.Store.ReadFrom(fixture.Session, 1);
        var applied = events.Select(fixture.Codecs.Decode).OfType<TurnSteeringApplied>().ToArray();
        Assert.Equal(new[] { first, second }, applied.Select(item => item.SteeringId));
        Assert.All(applied, item => Assert.Equal(1, item.StepIndex));
        var appliedEnvelopes = events.Where(evt => fixture.Codecs.Decode(evt) is TurnSteeringApplied).ToArray();
        var nextStart = Assert.Single(events, evt => fixture.Codecs.Decode(evt) is ModelStepStarted start && start.StepIndex == 1);
        Assert.Equal(appliedEnvelopes.Last().Sequence + 1, nextStart.Sequence);
        Assert.Single(events.Select(fixture.Codecs.Decode).OfType<TurnStarted>());
        Assert.Single(events.Select(fixture.Codecs.Decode).OfType<UserInputReceived>());
        Assert.DoesNotContain(events, evt => evt.Type.ToString().StartsWith("followup.", StringComparison.Ordinal));
        var replay = explorer.LoadConversation(new EventStream(fixture.Store, fixture.Codecs, fixture.Session), fixture.Run.RunId);
        var replayText = replay.SelectMany(message => message.Content).OfType<TextBlock>().ToArray();
        Assert.Single(replayText, text => text.Text == "first direction");
        Assert.Single(replayText, text => text.Text == "second direction");
        var turn = Assert.Single(applied.Select(item => item.TurnId).Distinct());
        Assert.Empty(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, turn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_next_step_drops_pending_input_on_completion_or_failure(bool fail)
    {
        using var fixture = new Fixture();
        var id = SteeringId.New();
        var explorer = fixture.Explorer((request, token) =>
        {
            fixture.Receive(id, "unused direction");
            if (fail) throw new InvalidOperationException("fixture failure");
            return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "test", null));
        });
        var result = fixture.Ask(explorer);
        Assert.Equal(fail ? StopReason.Error : StopReason.EndTurn, result.StopReason);
        var payloads = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
        var dropped = Assert.Single(payloads.OfType<TurnSteeringDropped>());
        Assert.Equal(id, dropped.SteeringId);
        Assert.NotEmpty(dropped.Reason);
        Assert.Empty(payloads.OfType<TurnSteeringApplied>());
        var events = fixture.Store.ReadFrom(fixture.Session, 1);
        var droppedEnvelope = Assert.Single(events, evt => fixture.Codecs.Decode(evt) is TurnSteeringDropped);
        var terminalEnvelope = Assert.Single(events, evt => fixture.Codecs.Decode(evt) is TurnCompleted or TurnAbandoned);
        Assert.Equal(droppedEnvelope.Sequence + 1, terminalEnvelope.Sequence);
        Assert.Empty(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, dropped.TurnId));
    }

    private sealed class Fixture : IDisposable
    {
        public readonly InMemoryEventStore Store = new();
        public readonly EventCodecs Codecs = EventCodecs.Create();
        public readonly SessionId Session = SessionId.New();
        public readonly TestRun.Opened Run;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-steering-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Run = TestRun.Open(Store, Session);
        }
        public void Receive(SteeringId id, string text)
        {
            var turn = Store.ReadFrom(Session, 1).Select(Codecs.Decode).OfType<TurnStarted>().Last().TurnId;
            using var cause = CausationScope.Begin(new CommandCausation(CommandId.New()));
            Assert.True(SteeringQueue.TryReceive(Store, Codecs, Session, id, Run.RunId, Run.RootLane, turn, text, "fixture"));
        }
        public ExplorerTurn Explorer(Func<ModelRequest, CancellationToken, ModelResponse> complete)
        {
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                    { ["filesystem.list"] = PermissionDecision.Allow }), _root);
            return new ExplorerTurn(complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                Store, Codecs, new FileArtifactStore(Path.Combine(_root, "blobs")), new InMemoryAuditSink(),
                new RedactionPolicy());
        }
        public ExplorerTurn.TurnResult Ask(ExplorerTurn explorer) => explorer.Ask("original intention", "system", Session,
            Run.RunId, Run.RootLane, "", TestContext.Current.CancellationToken);
        public void Dispose() => Directory.Delete(_root, true);
    }
}
