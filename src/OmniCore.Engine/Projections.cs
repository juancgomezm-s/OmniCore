namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Proyección pura de un Run desde su historial de eventos canónicos (ADR-0001 §1, INV-021).
/// Nunca se muta; cada evento produce un snapshot inmutable nuevo. El WorkingState se deriva
/// de estas proyecciones en cada Turn (ADR-0016 §7).
/// </summary>
public sealed class RunProjection
{
    public SessionId SessionId { get; }

    public RunId Id { get; }

    public DateTimeOffset CreatedAt { get; }

    public string? Objective { get; }

    public RunMode? Mode { get; }

    public ExecutionStrategy? Strategy { get; }

    public FailurePolicy? FailurePolicy { get; }

    public RunState State { get; }

    public TaskId? RootTask { get; }

    public bool HasPlan { get; }

    private RunProjection(SessionId sessionId, RunId id, DateTimeOffset createdAt, string? objective,
        RunMode? mode, ExecutionStrategy? strategy, FailurePolicy? failurePolicy, RunState state,
        TaskId? rootTask, bool hasPlan)
    {
        SessionId = sessionId;
        Id = id;
        CreatedAt = createdAt;
        Objective = objective;
        Mode = mode;
        Strategy = strategy;
        FailurePolicy = failurePolicy;
        State = state;
        RootTask = rootTask;
        HasPlan = hasPlan;
    }

    /// <summary>
    /// Reconstruye la proyección aplicando en orden los eventos de ESTE Run (una sesión puede tener
    /// varios). El estado se calcula con <see cref="StateMachines.ApplyRun"/>: un journal con una
    /// transición inválida lanza <see cref="InvalidStateTransitionException"/> (ADR-0036).
    /// </summary>
    public static RunProjection Replay(SessionId sessionId, RunId id, IEventCodecRegistry registry,
        IReadOnlyList<DomainEvent> evts)
    {
        var state = RunState.Created;
        var createdAt = DateTimeOffset.Now;
        string? objective = null;
        RunMode? mode = null;
        ExecutionStrategy? strategy = null;
        FailurePolicy? failurePolicy = null;
        TaskId? rootTask = null;
        var hasPlan = false;

        var created = false;
        foreach (var evt in evts)
        {
            var payload = registry.Decode(evt);
            if (payload is RunCreated runCreated)
            {
                if (!runCreated.RunId.Equals(id))
                {
                    continue;
                }

                if (created)
                {
                    throw new InvalidStateTransitionException("run", state, payload.Type().ToString());
                }

                created = true;
                objective = runCreated.Objective;
                mode = runCreated.Mode;
                strategy = runCreated.Strategy;
                failurePolicy = runCreated.FailurePolicy;
                rootTask = runCreated.RootTask;
                createdAt = runCreated.CreatedAt;
            }
            else if (RunOf(payload) is { } runId && runId.Equals(id))
            {
                if (!created)
                {
                    throw new InvalidStateTransitionException("run", "inexistente", payload.Type().ToString());
                }

                state = StateMachines.ApplyRun(state, payload);
            }
            else if (payload is RunModeChanged changed && changed.RunId.Equals(id))
            {
                mode = changed.To;
            }
            else if (payload is PlanCreated plan && plan.RunId.Equals(id))
            {
                hasPlan = true;
            }
        }

        return new RunProjection(sessionId, id, createdAt, objective, mode, strategy, failurePolicy, state,
            rootTask, hasPlan);
    }

    /// <summary>RunId de los eventos que cambian el estado del Run (ADR-0036 §1).</summary>
    private static RunId? RunOf(DomainEventPayload payload) => payload switch
    {
        RunStarted e => e.RunId,
        RunAwaitingInput e => e.RunId,
        UserInputReceived e => e.RunId,
        RunValidationStarted e => e.RunId,
        RunValidationRejected e => e.RunId,
        RunCompleted e => e.RunId,
        RunFailed e => e.RunId,
        RunCancelled e => e.RunId,
        _ => null,
    };

    public bool IsTerminal() => StateMachines.IsRunTerminal(State);

    public bool IsActiveNonTerminal() => !IsTerminal();

    /// <summary>Construye una proyección Run mínima para samples/demos (M2).</summary>
    public static RunProjection ForSample(SessionId sessionId, RunId id, string objective) =>
        new RunProjection(sessionId, id, DateTimeOffset.Now, objective, RunMode.Act, ExecutionStrategy.Direct,
            OmniCore.Domain.FailurePolicy.BlockDependents, RunState.Running, null, true);
}

/// <summary>Proyección del TaskGraph (spec §9, ADR-0036 §2).</summary>
public sealed class TaskGraphProjection
{
    private readonly Dictionary<TaskId, Task> _tasks = new();

    private TaskGraphProjection() { }

    /// <summary>Proyección desde payloads en memoria (estado vivo, sin journal).</summary>
    public static TaskGraphProjection FromPayloads(IEnumerable<DomainEventPayload> payloads)
    {
        var projection = new TaskGraphProjection();
        foreach (var payload in payloads)
        {
            projection.Apply(payload);
        }

        return projection;
    }

    public static TaskGraphProjection Replay(IEventCodecRegistry registry, IReadOnlyList<DomainEvent> evts)
    {
        var projection = new TaskGraphProjection();
        foreach (var evt in evts)
        {
            projection.Apply(registry.Decode(evt));
        }

        return projection;
    }

    public IReadOnlyList<Task> Tasks() => _tasks.Values.ToArray();

    public Task? Get(TaskId id) => _tasks.TryGetValue(id, out var task) ? task : null;

    public TaskState? StateOf(TaskId id)
    {
        var task = Get(id);
        return task is null ? null : task.State;
    }

    private void Apply(DomainEventPayload payload)
    {
        if (payload is TaskCreated created)
        {
            _tasks[created.TaskId] = new Task(created.TaskId, created.Objective, TaskState.Pending,
                created.Dependencies, created.Budget);
        }
        else if (TaskOf(payload) is { } id)
        {
            var task = Get(id)
                ?? throw new InvalidStateTransitionException("task", "inexistente", payload.Type().ToString());
            _tasks[id] = new Task(task.Id, task.Objective, StateMachines.ApplyTask(task.State, payload),
                task.Dependencies, task.Budget);
        }
    }

    private static TaskId? TaskOf(DomainEventPayload payload) => payload switch
    {
        TaskReady e => e.TaskId,
        TaskStarted e => e.TaskId,
        TaskBlocked e => e.TaskId,
        TaskUnblocked e => e.TaskId,
        TaskCompleted e => e.TaskId,
        TaskFailed e => e.TaskId,
        TaskSkipped e => e.TaskId,
        TaskCancelled e => e.TaskId,
        _ => null,
    };
}

/// <summary>Proyección de las Lanes (spec §10, ADR-0036 §3).</summary>
public sealed class LaneProjection
{
    private readonly Dictionary<LaneId, Lane> _lanes = new();

    private LaneProjection() { }

    public static LaneProjection Empty() => new LaneProjection();

    /// <summary>Proyección desde payloads en memoria (estado vivo, sin journal).</summary>
    public static LaneProjection FromPayloads(IEnumerable<DomainEventPayload> payloads)
    {
        var projection = new LaneProjection();
        foreach (var payload in payloads)
        {
            projection.Apply(payload);
        }

        return projection;
    }

    public static LaneProjection Replay(IEventCodecRegistry registry, IReadOnlyList<DomainEvent> evts)
    {
        var projection = new LaneProjection();
        foreach (var evt in evts)
        {
            projection.Apply(registry.Decode(evt));
        }

        return projection;
    }

    public IReadOnlyList<Lane> Lanes() => _lanes.Values.ToArray();

    public Lane? Get(LaneId id) => _lanes.TryGetValue(id, out var lane) ? lane : null;

    public LaneState? StateOf(LaneId id)
    {
        var lane = Get(id);
        return lane is null ? null : lane.State;
    }

    public IReadOnlyList<Lane> ForTask(TaskId taskId)
    {
        var result = new List<Lane>();
        foreach (var lane in _lanes.Values)
        {
            if (lane.TaskId.Equals(taskId))
            {
                result.Add(lane);
            }
        }

        return result.ToArray();
    }

    private void Apply(DomainEventPayload payload)
    {
        if (payload is LaneCreated created)
        {
            _lanes[created.LaneId] = new Lane(created.LaneId, created.TaskId, LaneState.Queued,
                created.AgentProfile, null);
        }
        else if (LaneOf(payload) is { } id)
        {
            var lane = Get(id)
                ?? throw new InvalidStateTransitionException("lane", "inexistente", payload.Type().ToString());
            _lanes[id] = new Lane(lane.Id, lane.TaskId, StateMachines.ApplyLane(lane.State, payload),
                lane.AgentProfile, lane.LastHeartbeatAt);
        }
    }

    private static LaneId? LaneOf(DomainEventPayload payload) => payload switch
    {
        LaneProvisioning e => e.LaneId,
        LaneStarted e => e.LaneId,
        LaneBlocked e => e.LaneId,
        LaneUnblocked e => e.LaneId,
        LaneCompleted e => e.LaneId,
        LaneFailed e => e.LaneId,
        LaneCancelled e => e.LaneId,
        _ => null,
    };
}