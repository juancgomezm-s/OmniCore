namespace OmniCore.Host;

using OmniCore.Domain;

/// <summary>Raw runtime counters for one exact configuration; absent evidence remains absent.</summary>
public sealed record QualificationCalibrationSnapshot(string KeyHash, string State,
    IReadOnlyDictionary<string, long?> Counters, IReadOnlyList<QualificationTraitValue> Traits);

public sealed partial class ModelQualificationHost
{
    public QualificationCalibrationSnapshot Calibration(string modelId, CancellationToken token)
    {
        var model = _registry.Model(modelId) ?? throw new ArgumentException("Unknown model.", nameof(modelId));
        var key = QualificationKeyFor(model, _registry.Provider(model.ProviderId));
        var profile = _store.Get(key, token);
        var observed = _store.ReadRuntimeCalibration(key, token);
        var knownArguments = observed.ContainsKey("valid_tool_arguments") || observed.ContainsKey("invalid_tool_arguments");
        var counters = RuntimeCalibrationCounters.Names.ToDictionary(name => name, name => observed.TryGetValue(name, out var count)
            ? (long?)count : name is "invalid_tool_arguments" or "valid_tool_arguments" && knownArguments ? 0L : null, StringComparer.Ordinal);
        return new(key.QualificationKeyHash(), profile?.State.ToString() ?? "Unknown", counters,
            profile is null ? [] : _store.Traits(key, profile.ProfileRevision, token)
                .Select(t => new QualificationTraitValue(t.Trait, t.Value, t.Confidence, t.Samples, t.Source)).ToArray());
    }
}