using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class EventStreamIndependentCausationTests
{
    [Fact]
    public void Independent_appends_without_scope_do_not_inherit_previous_event_as_cause()
    {
        var fx = new Fixture();
        fx.Stream.Append(fx.Received(), DurabilityClass.Standard);
        fx.Stream.Append(fx.Received(), DurabilityClass.Standard);

        var steeringEvents = fx.SteeringEvents();
        Assert.Equal(2, steeringEvents.Count);
        Assert.Null(steeringEvents[0].Causation);
        Assert.Null(steeringEvents[1].Causation);
    }

    [Fact]
    public void Independent_batch_items_without_scope_do_not_chain_causation_to_each_other()
    {
        var fx = new Fixture();
        fx.Stream.AppendBatch(new DomainEventPayload[] { fx.Received(), fx.Received() },
            DurabilityClass.Standard);

        var steeringEvents = fx.SteeringEvents();
        Assert.Equal(2, steeringEvents.Count);
        Assert.All(steeringEvents, evt => Assert.Null(evt.Causation));
        Assert.NotEqual(steeringEvents[0].EventId, steeringEvents[1].EventId);
    }

    [Fact]
    public void Reopened_stream_does_not_turn_last_journal_event_into_implicit_cause()
    {
        var fx = new Fixture();
        fx.Stream.Append(fx.Received(), DurabilityClass.Standard);
        var prior = fx.SteeringEvents().Single();

        var reopened = new EventStream(fx.Store, fx.Codecs, fx.Session);
        reopened.Append(fx.Received(), DurabilityClass.Standard);

        var steeringEvents = fx.SteeringEvents();
        Assert.Equal(2, steeringEvents.Count);
        Assert.Null(steeringEvents[1].Causation);
        Assert.NotEqual(prior.EventId, steeringEvents[1].EventId);
    }

    [Fact]
    public void Explicit_command_and_event_causation_are_preserved_exactly()
    {
        var fx = new Fixture();
        var commandId = CommandId.New();
        DomainEvent commandCaused;
        using (CausationScope.Begin(new CommandCausation(commandId)))
        {
            fx.Stream.Append(fx.Received(), DurabilityClass.Standard);
            commandCaused = fx.SteeringEvents().Single();
        }
        Assert.Equal(new CommandCausation(commandId), commandCaused.Causation);

        var source = commandCaused;
        using (CausationScope.Begin(new EventCausation(source.EventId)))
            fx.Stream.Append(fx.Received(), DurabilityClass.Standard);

        var eventCaused = fx.SteeringEvents().Last();
        Assert.Equal(new EventCausation(source.EventId), eventCaused.Causation);
    }

    private sealed class Fixture
    {
        public InMemoryEventStore Store { get; } = new();
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public SessionId Session { get; } = SessionId.New();
        public TestRun.Opened Run { get; }
        public TurnId Turn { get; } = TurnId.New();
        public EventStream Stream { get; }

        public Fixture()
        {
            Stream = new EventStream(Store, Codecs, Session);
            Run = TestRun.Open(Stream, Session, "independent causation test");
            using (ExecutionScope.Begin(new ExecutionScopeState(Run.RunId, Run.RootTask, Run.RootLane, Turn)))
                Stream.Append(new TurnStarted(Turn, Run.RootLane), DurabilityClass.Standard);
        }

        public TurnSteeringReceived Received() => new(SteeringId.New(), Run.RunId, Run.RootLane,
            Turn, "[\"independent input\"]");

        public IReadOnlyList<DomainEvent> SteeringEvents() => Store.ReadFrom(Session, 1)
            .Where(evt => Codecs.Decode(evt) is TurnSteeringReceived).ToArray();
    }
}
