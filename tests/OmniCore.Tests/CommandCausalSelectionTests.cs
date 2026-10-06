using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

/// <summary>Envelope fixtures test causal selection, not authenticated calls or journal admission.</summary>
public sealed class CommandCausalSelectionTests
{
    [Fact]
    public void Selects_only_new_roots_and_transitive_descendants_in_the_requested_session()
    {
        var session = SessionId.New();
        var foreignSession = SessionId.New();
        var command = CommandId.New();
        var foreignCommand = CommandId.New();
        var previous = Envelope(session, 1, new CommandCausation(command));
        var uncaused = Envelope(session, 2, null);
        var root = Envelope(session, 3, new CommandCausation(command));
        var unrelated = Envelope(session, 4, new CommandCausation(foreignCommand));
        var child = Envelope(session, 5, new EventCausation(root.EventId));
        var oldChild = Envelope(session, 6, new EventCausation(previous.EventId));
        var grandchild = Envelope(session, 7, new EventCausation(child.EventId));
        var foreign = Envelope(foreignSession, 8, new CommandCausation(command));
        var foreignChild = Envelope(session, 9, new EventCausation(foreign.EventId));
        var unrelatedChild = Envelope(session, 10, new EventCausation(unrelated.EventId));
        var afterLast = Envelope(session, 11, null);
        var candidates = new[] { previous, uncaused, root, unrelated, child, oldChild,
            grandchild, foreign, foreignChild, unrelatedChild, afterLast };

        Assert.Equal(new[] { root, child, grandchild },
            OmniServer.SelectCommandResultEvents(candidates.Reverse().ToArray(), session, 1, command));
        Assert.Empty(OmniServer.SelectCommandResultEvents(candidates, session, 11, command));
        Assert.Empty(OmniServer.SelectCommandResultEvents(candidates, SessionId.New(), 0, command));
    }

    private static DomainEvent Envelope(SessionId session, long sequence, CausationId? cause) =>
        DomainEvent.Stored(EventId.New(), session, sequence, EventType.Of("fixture.command-causal-selection"),
            1, DateTimeOffset.UtcNow, cause, null, null, null, null, null, null, null,
            Array.Empty<ArtifactRef>(), "{}");
}
