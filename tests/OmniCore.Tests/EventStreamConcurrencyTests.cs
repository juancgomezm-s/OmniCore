namespace OmniCore.Tests;

using System.Collections.Concurrent;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

public sealed class EventStreamConcurrencyTests
{
    [Fact]
    public void Independent_streams_serialize_validation_and_append_for_one_session()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var codecs = EventCodecs.Create();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, codecs, session);
        var task = TaskId.New();
        var lane = LaneId.New();
        stream.AppendBatch(new DomainEventPayload[] {
            new TaskCreated(task, run.RunId, "concurrent", [], new TaskBudget(null, null, null, null), run.RootTask),
            new TaskReady(task),
            new LaneCreated(lane, task, ProfileId.New()),
            new LaneStarted(lane),
        }, DurabilityClass.Barrier);

        var accepted = new ConcurrentBag<bool>();
        Parallel.For(0, 32, _ =>
        {
            try
            {
                new EventStream(store, codecs, session).Append(new TaskStarted(task, lane));
                accepted.Add(true);
            }
            catch (InvalidStateTransitionException)
            {
                accepted.Add(false);
            }
        });

        Assert.Equal(1, accepted.Count(value => value));
        Assert.Equal(31, accepted.Count(value => !value));
        var journal = store.ReadFrom(session, 1);
        Assert.Single(journal, evt => codecs.Decode(evt) is TaskStarted started && started.TaskId == task);
        _ = CanonicalStateTracker.Replay(codecs, journal);
    }
}
