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

    /// <summary>Reconstruye la proyección aplicando cada evento tipado en orden.</summary>
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

        foreach (var evt in evts)
        {
            var payload = registry.Decode(evt);
            if (payload is RunCreated created)
            {
                objective = created.Objective;
                mode = created.Mode;
                strategy = created.Strategy;
                failurePolicy = created.FailurePolicy;
                rootTask = created.RootTask;
                createdAt = created.CreatedAt;
            }
            else if (payload is RunStarted)
            {
                state = RunState.Running;
            }
            else if (payload is RunAwaitingInput)
            {
                state = RunState.AwaitingInput;
            }
            else if (payload is UserInputReceived)
            {
                state = RunState.Running;
            }
            else if (payload is RunValidationStarted)
            {
                state = RunState.Validating;
            }
            else if (payload is RunValidationRejected)
            {
                state = RunState.Running;
            }
            else if (payload is RunCompleted completed)
            {
                state = completed.Outcome == RunOutcome.CompletedWithIssues
                    ? RunState.CompletedWithIssues
                    : RunState.Completed;
            }
            else if (payload is RunFailed)
            {
                state = RunState.Failed;
            }
            else if (payload is RunCancelled)
            {
                state = RunState.Cancelled;
            }
            else if (payload is RunModeChanged changed)
            {
                mode = changed.To;
            }
            else if (payload is PlanCreated)
            {
                hasPlan = true;
            }
        }

        return new RunProjection(sessionId, id, createdAt, objective, mode, strategy, failurePolicy, state,
            rootTask, hasPlan);
    }

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
        else if (payload is TaskReady ready)
        {
            Replace(ready.TaskId, TaskState.Ready);
        }
        else if (payload is TaskStarted started)
        {
            Replace(started.TaskId, TaskState.Running);
        }
        else if (payload is TaskBlocked blocked)
        {
            Replace(blocked.TaskId, TaskState.Blocked);
        }
        else if (payload is TaskUnblocked unblocked)
        {
            Replace(unblocked.TaskId, unblocked.Requeue ? TaskState.Ready : TaskState.Running);
        }
        else if (payload is TaskCompleted completed)
        {
            Replace(completed.TaskId, TaskState.Completed);
        }
        else if (payload is TaskFailed failed)
        {
            Replace(failed.TaskId, TaskState.Failed);
        }
        else if (payload is TaskSkipped skipped)
        {
            Replace(skipped.TaskId, TaskState.Skipped);
        }
        else if (payload is TaskCancelled cancelled)
        {
            Replace(cancelled.TaskId, TaskState.Cancelled);
        }
    }

    private void Replace(TaskId id, TaskState newState)
    {
        var task = Get(id);
        if (task is not null)
        {
            _tasks[id] = new Task(task.Id, task.Objective, newState, task.Dependencies, task.Budget);
        }
    }
}

/// <summary>Proyección de las Lanes (spec §10, ADR-0036 §3).</summary>
public sealed class LaneProjection
{
    private readonly Dictionary<LaneId, Lane> _lanes = new();

    private LaneProjection() { }

    public static LaneProjection Empty() => new LaneProjection();

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
        else if (payload is LaneProvisioning p)
        {
            Replace(p.LaneId, LaneState.Provisioning);
        }
        else if (payload is LaneStarted s)
        {
            Replace(s.LaneId, LaneState.Running);
        }
        else if (payload is LaneBlocked b)
        {
            Replace(b.LaneId, LaneState.Blocked);
        }
        else if (payload is LaneUnblocked u)
        {
            Replace(u.LaneId, LaneState.Running);
        }
        else if (payload is LaneCompleted c)
        {
            Replace(c.LaneId, LaneState.Completed);
        }
        else if (payload is LaneFailed f)
        {
            Replace(f.LaneId, LaneState.Failed);
        }
        else if (payload is LaneCancelled cc)
        {
            Replace(cc.LaneId, LaneState.Cancelled);
        }
    }

    private void Replace(LaneId id, LaneState newState)
    {
        var lane = Get(id);
        if (lane is not null)
        {
            _lanes[id] = new Lane(lane.Id, lane.TaskId, newState, lane.AgentProfile, lane.LastHeartbeatAt);
        }
    }
}