using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Abre un Run válido en un journal de test: RunCreated → RunStarted → Task raíz creada, lista y
/// en ejecución con su Lane raíz (ADR-0035 §2, ADR-0036). El journal valida cada transición, así
/// que un test que escribe eventos de un Run necesita que ese Run exista de verdad.
/// </summary>
internal static class TestRun
{
    public sealed record Opened(SessionId SessionId, RunId RunId, TaskId RootTask, LaneId RootLane);

    public static Opened Open(IEventStore store, SessionId sessionId, string objective = "objetivo de test",
        RunMode mode = RunMode.Act) =>
        Open(new EventStream(store, EventCodecs.Create(), sessionId), sessionId, objective, mode);

    /// <summary>Igual, escribiendo con un stream dado (p. ej. para que sea el único escritor).</summary>
    public static Opened Open(EventStream stream, SessionId sessionId, string objective = "objetivo de test",
        RunMode mode = RunMode.Act, ProfileId? agentProfile = null)
    {
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var budget = new TaskBudget(null, null, null, null);
        stream.AppendBatch(new DomainEventPayload[] {
            new RunCreated(run, sessionId, objective, mode, ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
                budget, task, DateTimeOffset.UtcNow),
            new RunStarted(run),
            new TaskCreated(task, run, objective, Array.Empty<TaskDependency>(), budget),
            new TaskReady(task),
            new LaneCreated(lane, task, agentProfile ?? ProfileId.New()),
            new LaneStarted(lane),
            new TaskStarted(task, lane),
        }, DurabilityClass.Standard);
        return new Opened(sessionId, run, task, lane);
    }

    /// <summary>Atajo: solo el RunId del Run abierto.</summary>
    public static RunId OpenRun(IEventStore store, SessionId sessionId) => Open(store, sessionId).RunId;
}
