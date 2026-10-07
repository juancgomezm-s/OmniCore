namespace OmniCore.Domain;

/// <summary>Stream termination, distinct from scoring: a wrong complete answer is
/// Completed with ProbeStatus.Failed; a provider error is Failed.</summary>
public enum ProbeExecutionTermination { Completed, Cancelled, TimedOut, Failed }

/// <summary>Canonical User-scoped observation of one qualification invocation, independent
/// of profile revisions. ExecutionId is a local qualification identity, not AgentExecution,
/// Session, Run or Turn. Cost is computed USD, not an account debit. Missing cost stays null.
/// Output/error text belongs only in the redacted CAS evidence, not this relational record.</summary>
/// <remarks>For IncludedQuota, ReservationId associates the invocation only; it does
/// not identify a monetary ledger reservation. MaximumUsd=0 grants no USD authority,
/// and CostUsd=null is unavailable, never a measured zero debit. MaximumGenerationAttempts
/// remains the declared provider ceiling; ObservedGenerationSends is observed separately.</remarks>
public sealed record QualificationProbeReceipt(
    Guid ExecutionId, string ProbeId, int Ordinal, string ReservationId, string KeyHash,
    string SuiteId, string SuiteVersion, string TaskSetHash,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    ProbeStatus Status, ProbeExecutionTermination Termination, BillingMode BillingMode,
    decimal MaximumUsd, long MaximumGenerationAttempts, long ObservedGenerationSends,
    decimal? CostUsd, TokenUsage? Usage, TokenUsageFields ReportedUsageFields, ArtifactRef Evidence)
{
    public const string EvidenceSchema = "omnicore.qualification-probe-receipt.v1";

    /// <summary>Deterministic receipt header for CAS cross-validation. Excludes Evidence
    /// itself to avoid a self-referential content hash. No response/error text is serialized.</summary>
    public string CanonicalObservationJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("executionId", ExecutionId.ToString("D")); writer.WriteString("probeId", ProbeId);
            writer.WriteNumber("ordinal", Ordinal); writer.WriteString("reservationId", ReservationId);
            writer.WriteString("keyHash", KeyHash); writer.WriteString("suiteId", SuiteId);
            writer.WriteString("suiteVersion", SuiteVersion); writer.WriteString("taskSetHash", TaskSetHash);
            writer.WriteString("startedAtUtc", StartedAtUtc.ToUniversalTime());
            writer.WriteString("completedAtUtc", CompletedAtUtc.ToUniversalTime());
            writer.WriteString("status", Status.ToString()); writer.WriteString("termination", Termination.ToString());
            writer.WriteString("billingMode", BillingMode.ToString()); Amount("maximumUsd", MaximumUsd);
            writer.WriteNumber("maximumGenerationAttempts", MaximumGenerationAttempts);
            writer.WriteNumber("observedGenerationSends", ObservedGenerationSends);
            writer.WriteString("currency", "USD");
            writer.WriteString("costSource", "computed-from-reported-usage-and-explicit-prices-not-account-debit");
            if (CostUsd is { } cost) Amount("costUsd", cost); else writer.WriteNull("costUsd");
            writer.WriteNumber("reportedUsageFields", (int)ReportedUsageFields);
            writer.WritePropertyName("usage");
            if (Usage is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                Count("input", Usage.Input, TokenUsageFields.Input); Count("output", Usage.Output, TokenUsageFields.Output);
                Count("cacheRead", Usage.CacheRead, TokenUsageFields.CacheRead); Count("cacheWrite", Usage.CacheWrite, TokenUsageFields.CacheWrite);
                Count("reasoning", Usage.Reasoning, TokenUsageFields.Reasoning);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            void Count(string name, long value, TokenUsageFields field)
            {
                if (ReportedUsageFields.HasFlag(field)) writer.WriteNumber(name, value); else writer.WriteNull(name);
            }
            void Amount(string name, decimal value)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
