namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Qualification;

/// <summary>Canonical User usage observation, independent from monetary admission.
/// No Session/Run/Turn is manufactured. Invalid counters remain raw CAS evidence.</summary>
internal static class QualificationReceiptWriter
{
    internal static QualificationProbeReceipt Record(SqliteModelQualificationStore store,
        Guid executionId, string keyHash, string taskSetHash,
        ProbeRequest request, int ordinal, string admissionId, DateTimeOffset started,
        BillingMode billing, decimal maximumUsd, long attemptBound,
        ProbeExecutionObservation observation)
    {
        var result = observation.Result;
        var invalidUsage = result.Usage is { } raw && TokenUsageValidation.IsInvalid(raw, result.ReportedUsageFields);
        var receipt = new QualificationProbeReceipt(executionId, request.Probe.Id.ToString(), ordinal,
            admissionId, keyHash, QuickProbeSuite.SuiteId, QuickProbeSuite.SuiteVersion, taskSetHash,
            started, observation.CompletedAtUtc, result.Status, observation.Termination,
            billing, maximumUsd, attemptBound, observation.ObservedGenerationSends,
            invalidUsage || billing == BillingMode.IncludedQuota ? null : result.CostUsd,
            invalidUsage ? null : result.Usage,
            invalidUsage ? TokenUsageFields.None : result.ReportedUsageFields, null!);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("schema", QualificationProbeReceipt.EvidenceSchema);
            writer.WritePropertyName("receipt"); writer.WriteRawValue(receipt.CanonicalObservationJson());
            writer.WriteString("output", result.Output); writer.WriteString("error", result.Error);
            if (invalidUsage && result.Usage is { } rejected)
            {
                writer.WritePropertyName("invalidReportedUsage"); writer.WriteStartObject();
                writer.WriteNumber("fields", (int)result.ReportedUsageFields);
                writer.WriteNumber("input", rejected.Input); writer.WriteNumber("output", rejected.Output);
                writer.WriteNumber("cacheRead", rejected.CacheRead); writer.WriteNumber("cacheWrite", rejected.CacheWrite);
                writer.WriteNumber("reasoning", rejected.Reasoning); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        // Cancellation cannot discard consumption already observed. FULL commit precedes any settlement.
        return store.PublishProbeReceipt(receipt, artifacts => artifacts.PutText(json,
            "application/json", ArtifactKind.Other, Sensitivity.Sensitive), CancellationToken.None);
    }
}
