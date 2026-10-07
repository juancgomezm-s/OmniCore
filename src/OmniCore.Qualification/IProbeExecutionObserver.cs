namespace OmniCore.Qualification;

using OmniCore.Domain;

/// <summary>Host-owned admission and receipt boundary. Not telemetry or a billing statement.
/// Admission must finish before entering the provider. Receipt failure aborts the suite;
/// it must never be converted into a scored provider error or permit the next probe.</summary>
public interface IProbeExecutionObserver
{
    ValueTask BeforeDispatchAsync(ProbeRequest request, CancellationToken cancellationToken);

    /// <summary>Called once for an entered provider, including cancellation and timeout.
    /// No caller cancellation token: the Host must preserve a receipt even after cancellation.
    /// A zero observed send count means unobserved, not proof of no dispatch or zero cost.
    /// Any retained text must pass through the Host's redactor/CAS writer.</summary>
    ValueTask CompletedAsync(ProbeRequest request, ProbeExecutionObservation observation);
}

/// <summary>Stream termination, distinct from Passed/Failed scoring: a wrong but
/// complete answer is Completed with ProbeStatus.Failed; a provider failure is Failed.</summary>
public enum ProbeExecutionTermination { Completed, Cancelled, TimedOut, Failed }

/// <summary>Per-probe observation; timestamps are UTC, usage/cost retain their existing
/// reported/unknown semantics. This record alone is not a durable receipt.</summary>
public sealed record ProbeExecutionObservation(ProbeResult Result,
    ProbeExecutionTermination Termination, DateTimeOffset CompletedAtUtc, long ObservedGenerationSends);
