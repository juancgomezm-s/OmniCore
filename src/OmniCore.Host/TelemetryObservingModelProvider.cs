namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Engine;

/// <summary>Observes numeric stream metadata while forwarding each provider event unchanged.</summary>
public sealed class TelemetryObservingModelProvider : IModelProvider
{
    private readonly IModelProvider _inner;
    private readonly ITelemetrySink _sink;

    public TelemetryObservingModelProvider(IModelProvider inner, ITelemetrySink? sink = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _sink = sink ?? NoOpTelemetrySink.Instance;
    }

    public ProviderCapabilities Capabilities => _inner.Capabilities;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _inner.StreamAsync(request, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            Observe(item);
            yield return item;
        }
    }

    private void Observe(ModelStreamEvent item)
    {
        var scope = ExecutionScope.Current;
        void Record(TelemetryKind kind, TelemetrySignal signal, long value)
        {
            try
            {
                _sink.Record(new TelemetryRecord(kind, signal, value, DateTimeOffset.UtcNow,
                    scope?.RunId, scope?.TaskId, scope?.LaneId, scope?.TurnId, scope?.ToolCallId));
            }
            catch (Exception)
            {
                // Telemetry is best-effort and cannot change the canonical model result.
            }
        }

        switch (item)
        {
            case ResponseStarted: Record(TelemetryKind.Progress, TelemetrySignal.ResponseStartedCount, 1); break;
            case BlockStarted: Record(TelemetryKind.Progress, TelemetrySignal.BlockStartedCount, 1); break;
            case TextDelta delta: Record(TelemetryKind.Delta, TelemetrySignal.TextDeltaCharacters, delta.Text.Length); break;
            case ReasoningDelta delta: Record(TelemetryKind.Delta, TelemetrySignal.ReasoningDeltaCharacters, delta.Text.Length); break;
            case ToolArgumentsDelta delta: Record(TelemetryKind.Delta, TelemetrySignal.ToolArgumentDeltaCharacters,
                delta.PartialJson.Length); break;
            case BlockCompleted: Record(TelemetryKind.Progress, TelemetrySignal.BlockCompletedCount, 1); break;
            case UsageUpdated usage:
                Record(TelemetryKind.Sample, TelemetrySignal.InputTokenCount, usage.Usage.Input);
                Record(TelemetryKind.Sample, TelemetrySignal.OutputTokenCount, usage.Usage.Output);
                Record(TelemetryKind.Sample, TelemetrySignal.CacheReadTokenCount, usage.Usage.CacheRead);
                Record(TelemetryKind.Sample, TelemetrySignal.CacheWriteTokenCount, usage.Usage.CacheWrite);
                Record(TelemetryKind.Sample, TelemetrySignal.ReasoningTokenCount, usage.Usage.Reasoning);
                break;
            case ResponseCompleted: Record(TelemetryKind.Progress, TelemetrySignal.ResponseCompletedCount, 1); break;
            case ResponseFailed: Record(TelemetryKind.Progress, TelemetrySignal.ResponseFailedCount, 1); break;
        }
    }
}
