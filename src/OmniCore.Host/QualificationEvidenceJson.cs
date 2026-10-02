namespace OmniCore.Host;

using System.Text.Json.Serialization;
using OmniCore.Domain;

internal sealed record QualificationEvidenceBundle(
    BenchmarkIdentity BenchmarkIdentity,
    IReadOnlyList<QualificationEvidenceProbe> Probes);

internal sealed record QualificationEvidenceProbe(
    string Id,
    string Kind,
    string Prompt,
    string Expected,
    string Status,
    double Score,
    string? Output,
    string? Error,
    long DurationTicks,
    decimal? CostUsd);

[JsonSerializable(typeof(QualificationEvidenceBundle))]
internal sealed partial class QualificationEvidenceJsonContext : JsonSerializerContext;
