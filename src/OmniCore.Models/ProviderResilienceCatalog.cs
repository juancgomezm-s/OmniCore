namespace OmniCore.Models;

using System.Collections.Concurrent;

/// <summary>Immutable point-in-time view of a provider circuit.</summary>
public sealed record ProviderCircuitSnapshot(
    DateTimeOffset MeasuredAt,
    DateTimeOffset? OpenUntil,
    bool ProbeInFlight,
    bool CanAttempt);

/// <summary>
/// In-memory, explicitly scoped sharing of provider circuit handles between adapter instances.
/// The owner controls its lifetime; this catalog has no process-global state or persistence.
/// </summary>
public sealed class ProviderResilienceCatalog
{
    private readonly ConcurrentDictionary<string, ProviderResilience> _circuits = new(StringComparer.Ordinal);

    internal ProviderResilience Acquire(string providerId, OpenAiProviderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(options);
        return _circuits.GetOrAdd(providerId, key => new ProviderResilience(options, key));
    }

    /// <summary>Returns null until a handle for this provider has been acquired.</summary>
    public ProviderCircuitSnapshot? Snapshot(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _circuits.TryGetValue(providerId, out var circuit) ? circuit.Snapshot() : null;
    }
}
