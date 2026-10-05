namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;

/// <summary>Small process-local telemetry buffer; old samples are dropped at capacity.</summary>
public sealed class InMemoryTelemetrySink : ITelemetrySink
{
    private readonly object _gate = new();
    private readonly Queue<TelemetryRecord> _records = new();
    private readonly int _capacity;

    public InMemoryTelemetrySink(int capacity = 4096)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Record(TelemetryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_records.Count == _capacity) _records.Dequeue();
            _records.Enqueue(record);
        }
    }

    public IReadOnlyList<TelemetryRecord> Snapshot()
    {
        lock (_gate) return _records.ToArray();
    }
}
