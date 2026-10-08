using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;

namespace OmniCore.Host;

/// <summary>A Host invocation hint, not a durable effect or an authority grant.
/// Synthetic callbacks and rejected Turns never create an executor.</summary>
internal static class HostAgentInvocation
{
    private sealed record Invocation(IEventStore Store, SessionId Session, RunId Run);
    private static readonly AsyncLocal<Invocation?> Ambient = new();

    internal static IDisposable Begin(IEventStore store, SessionId session, RunId run)
    {
        var previous = Ambient.Value;
        Ambient.Value = new(store, session, run);
        return new Restore(previous);
    }

    internal static ExecutionId? Resolve(IEventStore store, IEventCodecRegistry codecs,
        SessionId session, RunId run, TaskId? task, LaneId lane, EventStream stream)
    {
        var journal = store.ReadFrom(session, 1);
        var scope = ExecutionScope.Current;
        if (scope?.ExecutionId is { } explicitOwner && scope.RunId == run
            && scope.TaskId == task && scope.LaneId == lane)
        {
            ValidateOwner(explicitOwner);
            return explicitOwner;
        }
        var invocation = Ambient.Value;
        if (invocation is null || !ReferenceEquals(invocation.Store, store)
            || invocation.Session != session || invocation.Run != run) return null;
        var root = journal.Select(codecs.Decode).OfType<RunCreated>().Single(e => e.RunId == run);
        if (task != root.RootTask)
            throw new InvalidDataException("A root Host invocation cannot impersonate a child Lane.");
        if (LaneProjection.Replay(codecs, journal).StateOf(lane) != LaneState.Running
            || TaskGraphProjection.Replay(codecs, journal).StateOf(root.RootTask) != TaskState.Running)
            throw new InvalidDataException("The root Task/Lane is not executing.");
        var candidates = journal.Where(e => e.RunId == run).Select(codecs.Decode)
            .OfType<AgentExecutionStarted>().Where(e => e.LaneId == lane).Distinct().ToArray();
        if (candidates.Length > 1)
            throw new InvalidDataException("Root Lane execution attribution is ambiguous.");
        if (candidates.Length == 1)
        {
            if (candidates[0].ParentExecutionId is not null)
                throw new InvalidDataException("A root executor cannot have a parent.");
            ValidateOwner(candidates[0].ExecutionId);
            return candidates[0].ExecutionId;
        }
        var configuration = journal.Select(codecs.Decode).OfType<LaneCreated>().Single(e => e.LaneId == lane);
        var id = ExecutionId.New();
        using var attribution = ExecutionScope.Begin(new(run, task, lane, ExecutionId: id));
        stream.Append(new AgentExecutionStarted(id, lane, configuration.AgentProfile, null,
            ExecutionRelation.Awaited, ExecutionSupervision.Managed), DurabilityClass.Barrier);
        return id;

        void ValidateOwner(ExecutionId id)
        {
            _ = PreM6RecordProjection.Replay(session, codecs, journal);
            var start = journal.Where(e => e.RunId == run).Select(codecs.Decode)
                .OfType<AgentExecutionStarted>().FirstOrDefault(e => e.ExecutionId == id);
            var configuration = journal.Select(codecs.Decode).OfType<LaneCreated>().Single(e => e.LaneId == lane);
            if (start is null || start.LaneId != lane || start.ProfileId != configuration.AgentProfile
                || configuration.TaskId != task || journal.Select(codecs.Decode).Any(e => e is
                    AgentExecutionCompleted completed && completed.ExecutionId == id
                    || e is AgentExecutionFailed failed && failed.ExecutionId == id))
                throw new InvalidDataException("Turn execution owner is unavailable, terminal or outside its Lane.");
        }
    }

    private sealed class Restore(Invocation? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
