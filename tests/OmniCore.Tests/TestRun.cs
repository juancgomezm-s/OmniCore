using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
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

    /// <summary>Creates a canonical Session envelope before opening a Run for Host recovery tests.</summary>
    public static Opened OpenInSession(IEventStore store, SessionId sessionId, string workspaceRoot,
        string objective = "objetivo de test", RunMode mode = RunMode.Act) =>
        OpenInSession(new EventStream(store, EventCodecs.Create(), sessionId), sessionId, workspaceRoot, objective, mode);

    public static Opened OpenInSession(EventStream stream, SessionId sessionId, string workspaceRoot,
        string objective = "objetivo de test", RunMode mode = RunMode.Act)
    {
        var canonicalWorkspace = ProjectIdentity.CanonicalWorkspacePath(Path.GetFullPath(workspaceRoot));
        stream.Append(new SessionCreated(sessionId, WorkspaceId.Of(canonicalWorkspace).ToString(), canonicalWorkspace,
            ProfileId.New(), DateTimeOffset.UtcNow));
        return Open(stream, sessionId, objective, mode);
    }

    /// <summary>Creates a native Session with an explicit initial security snapshot before its Run.</summary>
    public static Opened OpenNativeInSession(IEventStore store, SessionId sessionId, string workspaceRoot,
        string objective = "objetivo de test", RunMode mode = RunMode.Act,
        ModelPolicyCategory category = ModelPolicyCategory.ObserveOnly)
    {
        var codecs = EventCodecs.Create();
        var stream = new EventStream(store, codecs, sessionId);
        var canonicalWorkspace = ProjectIdentity.CanonicalWorkspacePath(Path.GetFullPath(workspaceRoot));
        stream.Append(new SessionCreated(sessionId, WorkspaceId.Of(canonicalWorkspace).ToString(), canonicalWorkspace,
            ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(SessionSecurityPolicySet.FromPreset(sessionId, 1, category,
            SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow));
        return Open(stream, sessionId, objective, mode);
    }

    public static Opened Open(IEventStore store, SessionId sessionId, string objective = "objetivo de test",
        RunMode mode = RunMode.Act) =>
        Open(new EventStream(store, EventCodecs.Create(), sessionId), sessionId, objective, mode);

    /// <summary>Igual, escribiendo con un stream dado (p. ej. para que sea el único escritor).</summary>
    public static Opened Open(EventStream stream, SessionId sessionId, string objective = "objetivo de test",
        RunMode mode = RunMode.Act, ProfileId? agentProfile = null, TaskBudget? taskBudget = null)
    {
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var budget = taskBudget ?? new TaskBudget(null, null, null, null);
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
