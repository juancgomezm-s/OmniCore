namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>Telemetry is local, content-free execution metadata, never a canonical domain event.</summary>
public enum TelemetryKind
{
    Delta,
    Progress,
    Heartbeat,
    Sample,
}

/// <summary>Closed vocabulary for telemetry measurements; arbitrary labels are intentionally absent.</summary>
public enum TelemetrySignal
{
    TextDeltaCharacters,
    ReasoningDeltaCharacters,
    ToolArgumentDeltaCharacters,
    ResponseStartedCount,
    BlockStartedCount,
    BlockCompletedCount,
    ResponseCompletedCount,
    ResponseFailedCount,
    InputTokenCount,
    OutputTokenCount,
    CacheReadTokenCount,
    CacheWriteTokenCount,
    ReasoningTokenCount,
    HeartbeatCount,
}

/// <summary>A numeric observation with bounded dimensions, entity IDs, and a UTC timestamp only.</summary>
public sealed class TelemetryRecord
{
    public TelemetryKind Kind { get; }
    public TelemetrySignal Signal { get; }
    public long Value { get; }
    public DateTimeOffset TimestampUtc { get; }
    public RunId? RunId { get; }
    public TaskId? TaskId { get; }
    public LaneId? LaneId { get; }
    public TurnId? TurnId { get; }
    public ToolCallId? ToolCallId { get; }
    public ExecutionId? ExecutionId { get; }

    public TelemetryRecord(TelemetryKind kind, TelemetrySignal signal, long value,
        DateTimeOffset timestampUtc, RunId? runId = null, TaskId? taskId = null,
        LaneId? laneId = null, TurnId? turnId = null, ToolCallId? toolCallId = null,
        ExecutionId? executionId = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(signal)) throw new ArgumentOutOfRangeException(nameof(signal));
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (timestampUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Telemetry timestamps must be UTC.", nameof(timestampUtc));

        Kind = kind;
        Signal = signal;
        Value = value;
        TimestampUtc = timestampUtc;
        RunId = runId;
        TaskId = taskId;
        LaneId = laneId;
        TurnId = turnId;
        ToolCallId = toolCallId;
        ExecutionId = executionId;
    }
}

/// <summary>Local observer for non-canonical measurements. Implementations must not affect execution.</summary>
public interface ITelemetrySink
{
    void Record(TelemetryRecord record);
}

/// <summary>Default telemetry sink; records nothing and performs no I/O.</summary>
public sealed class NoOpTelemetrySink : ITelemetrySink
{
    public static NoOpTelemetrySink Instance { get; } = new();

    private NoOpTelemetrySink() { }

    public void Record(TelemetryRecord record) { }
}
