using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class InternalActCommandTests
{
    [Fact]
    public void Act_loop_suspension_and_resume_use_distinct_internal_causes_on_the_same_turn()
    {
        using var fx = new InternalExplorerAskCommandTests.Fixture(RunMode.Act);
        Assert.Equal(RunMode.Act, RunProjection.Replay(fx.Session, fx.Run, fx.Codecs,
            fx.Store.ReadFrom(fx.Session, 1)).Mode);
        var runtime = OmniCliRuntime.Create(fx.Root);
        var output = new List<string>();
        var initialSequence = fx.Store.CurrentSequence(fx.Session);

        var suspendedExit = runtime.RunActLoop(fx.Turn, output.Add, "act objective", "act instructions",
            fx.Session, fx.Run, fx.Lane, "", null, null, fx.Server,
            new FileArtifactStore(Path.Combine(fx.Root, "blobs")), new InMemoryAuditSink(), null,
            interactive: false, "en", TestContext.Current.CancellationToken);

        Assert.Equal(3, suspendedExit);
        Assert.Equal(1, fx.ProviderCalls);
        var firstTurnEvents = fx.Store.ReadFrom(fx.Session, initialSequence + 1);
        Assert.NotEmpty(firstTurnEvents);
        var firstCommandId = Assert.IsType<CommandCausation>(firstTurnEvents[0].Causation).CommandId;
        Assert.All(firstTurnEvents, evt => Assert.Equal(firstCommandId,
            Assert.IsType<CommandCausation>(evt.Causation).CommandId));
        var originalTurnId = Assert.Single(firstTurnEvents.Select(fx.Codecs.Decode).OfType<TurnStarted>()).TurnId;

        var interaction = Assert.Single(fx.Server.AcquireStore().ReadFrom(fx.Session, 1)
            .Select(fx.Codecs.Decode).OfType<InteractionRequested>());
        var resolved = fx.Questionnaires.Resolve(new EventStream(fx.Store, fx.Codecs, fx.Session),
            interaction.InteractionId, new[] { new QuestionAnswer("choice", new[] { "yes" }, null, null) },
            false, null, new UserInputReceived(fx.Run, "[\"QuestionnaireResponse\"]", null,
                "InteractionResponse(Questionnaire)"));
        Assert.True(resolved.Accepted);
        var beforeResume = fx.Store.CurrentSequence(fx.Session);

        var resumedExit = runtime.RunActLoop(fx.Turn, output.Add, "act objective", "act instructions",
            fx.Session, fx.Run, fx.Lane, "", null, null, fx.Server,
            new FileArtifactStore(Path.Combine(fx.Root, "blobs")), new InMemoryAuditSink(), null,
            interactive: false, "en", TestContext.Current.CancellationToken);

        Assert.Equal(0, resumedExit);
        Assert.Equal(2, fx.ProviderCalls);
        var resumedEvents = fx.Store.ReadFrom(fx.Session, beforeResume + 1);
        Assert.NotEmpty(resumedEvents);
        var resumedTurnEvents = resumedEvents.Where(evt => evt.TurnId == originalTurnId).ToArray();
        Assert.NotEmpty(resumedTurnEvents);
        var secondCommandId = Assert.IsType<CommandCausation>(resumedTurnEvents[0].Causation).CommandId;
        Assert.False(firstCommandId.Value.Equals(secondCommandId.Value));
        Assert.All(resumedTurnEvents, evt => Assert.Equal(secondCommandId,
            Assert.IsType<CommandCausation>(evt.Causation).CommandId));
        var turnIds = fx.Server.AcquireStore().ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode)
            .OfType<TurnStarted>().Select(evt => evt.TurnId).ToArray();
        Assert.Equal(new[] { originalTurnId }, turnIds);
        Assert.Null(CausationScope.Current);
    }
}
