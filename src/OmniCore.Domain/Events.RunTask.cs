namespace OmniCore.Domain;

using System.Text.Json.Serialization;

/// <summary>RunCreated: el Run entra en Created (ADR-0036 §1).</summary>
public record RunCreated(
    RunId RunId,
    SessionId SessionId,
    string Objective,
    RunMode Mode,
    ExecutionStrategy Strategy,
    FailurePolicy FailurePolicy,
    TaskBudget Budget,
    TaskId RootTask,
    DateTimeOffset CreatedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.created");

    public int SchemaVersion() => 1;
}

/// <summary>RunStarted: Created → Running.</summary>
[JsonSerializable(typeof(RunStarted))]
public record RunStarted(RunId RunId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.started");

    public int SchemaVersion() => 1;
}

/// <summary>RunAwaitingInput: Running → AwaitingInput (la Lane raíz espera al usuario).</summary>
[JsonSerializable(typeof(RunAwaitingInput))]
public record RunAwaitingInput(RunId RunId, LaneId RootLaneId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.awaiting_input");

    public int SchemaVersion() => 1;
}

/// <summary>RunValidationStarted: Running → Validating.</summary>
[JsonSerializable(typeof(RunValidationStarted))]
public record RunValidationStarted(RunId RunId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.validation_started");

    public int SchemaVersion() => 1;
}

/// <summary>RunValidationRejected: Validating → Running con gates fallidos.</summary>
public record RunValidationRejected(RunId RunId, IReadOnlyList<string> Gates, IReadOnlyList<string> Missing)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.validation_rejected");

    public int SchemaVersion() => 1;
}

/// <summary>RunCompleted: Validating → Completed (con outcome; ADR-0036 §1).</summary>
[JsonSerializable(typeof(RunCompleted))]
public record RunCompleted(RunId RunId, RunOutcome Outcome) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.completed");

    public int SchemaVersion() => 1;
}

/// <summary>RunFailed: Running/Validating/AwaitingInput → Failed.</summary>
[JsonSerializable(typeof(RunFailed))]
public record RunFailed(RunId RunId, string Cause) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.failed");

    public int SchemaVersion() => 1;
}

/// <summary>RunCancelled: cualquier estado no terminal → Cancelled.</summary>
[JsonSerializable(typeof(RunCancelled))]
public record RunCancelled(RunId RunId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.cancelled");

    public int SchemaVersion() => 1;
}

/// <summary>TaskCreated: la Task entra en Pending.</summary>
public record TaskCreated(TaskId TaskId, RunId RunId, string Objective, IReadOnlyList<TaskDependency> Dependencies,
    TaskBudget Budget) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.created");

    public int SchemaVersion() => 1;
}

/// <summary>TaskReady: Pending → Ready (dependencias requeridas completadas).</summary>
[JsonSerializable(typeof(TaskReady))]
public record TaskReady(TaskId TaskId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.ready");

    public int SchemaVersion() => 1;
}

/// <summary>TaskStarted: Ready → Running (la primera Lane arranca).</summary>
[JsonSerializable(typeof(TaskStarted))]
public record TaskStarted(TaskId TaskId, LaneId LaneId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.started");

    public int SchemaVersion() => 1;
}

/// <summary>TaskBlocked: Running → Blocked.</summary>
[JsonSerializable(typeof(TaskBlocked))]
public record TaskBlocked(TaskId TaskId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.blocked");

    public int SchemaVersion() => 1;
}

/// <summary>TaskUnblocked: Blocked → Running, o Blocked → Ready con requeue.</summary>
[JsonSerializable(typeof(TaskUnblocked))]
public record TaskUnblocked(TaskId TaskId, bool Requeue) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.unblocked");

    public int SchemaVersion() => 1;
}

/// <summary>TaskCompleted: Running/Blocked → Completed.</summary>
[JsonSerializable(typeof(TaskCompleted))]
public record TaskCompleted(TaskId TaskId, AgentResult? Result) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.completed");

    public int SchemaVersion() => 1;
}

/// <summary>TaskFailed: Running/Blocked → Failed (RecoveryPolicy agotada).</summary>
[JsonSerializable(typeof(TaskFailed))]
public record TaskFailed(TaskId TaskId, string Cause) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.failed");

    public int SchemaVersion() => 1;
}

/// <summary>TaskSkipped: Pending/Ready/Blocked → Skipped.</summary>
[JsonSerializable(typeof(TaskSkipped))]
public record TaskSkipped(TaskId TaskId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.skipped");

    public int SchemaVersion() => 1;
}

/// <summary>TaskCancelled: cualquier estado no terminal → Cancelled.</summary>
[JsonSerializable(typeof(TaskCancelled))]
public record TaskCancelled(TaskId TaskId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("task.cancelled");

    public int SchemaVersion() => 1;
}