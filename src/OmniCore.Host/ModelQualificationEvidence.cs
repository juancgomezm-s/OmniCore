namespace OmniCore.Host;

using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Qualification;

public sealed partial class ModelQualificationHost
{
    // Explicit writer avoids reflection serialization/AOT dependencies. Unknown usage/cost
    // remains null; reported cache/reasoning are subsets, not extra additive consumption.
    private static string QualificationEvidenceJson(ModelQualificationKey key, long revision,
        QualificationOptions options, bool suiteComplete, IReadOnlyList<Probe> probes, IReadOnlyList<ProbeResult> results,
        IReadOnlyList<QualificationTraitValue> traits, decimal estimate, string estimateSource)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "omnicore.model-qualification.v1");
            writer.WriteString("keyHash", key.QualificationKeyHash());
            writer.WritePropertyName("key");
            using (var json = JsonDocument.Parse(key.CanonicalJson())) json.RootElement.WriteTo(writer);
            writer.WriteNumber("sourceRunRevision", revision);
            writer.WriteString("recordedAt", DateTimeOffset.UtcNow);
            writer.WriteString("source", options.Provider is null ? "configured-provider" : "injected-provider");
            writer.WriteBoolean("probeSetOverride", options.Probes is not null);
            writer.WriteBoolean("suiteComplete", suiteComplete);
            writer.WritePropertyName("benchmarkIdentity"); writer.WriteStartObject();
            writer.WriteString("suiteId", QuickProbeSuite.SuiteId);
            writer.WriteString("suiteVersion", QuickProbeSuite.SuiteVersion);
            writer.WriteString("taskSetHash", ProbeScorer.TaskSetHash(probes));
            writer.WriteNull("seed"); writer.WriteNull("temperature");
            writer.WriteBoolean("samplingParametersSent", false);
            writer.WriteString("omniCoreVersion", RuntimeBuildIdentity.ForAssembly(typeof(ModelQualificationHost).Assembly));
            writer.WriteEndObject();
            writer.WriteNumber("estimatedCostUsd", estimate);
            writer.WriteString("estimatedCostSource", estimateSource);
            writer.WriteNumber("costCapUsd", options.MaxTotalCostUsd);
            writer.WriteString("currency", "USD");
            writer.WriteString("costSource", "computed-from-reported-usage-and-explicit-prices-not-account-debit");
            writer.WritePropertyName("probes"); writer.WriteStartArray();
            foreach (var probe in probes)
            {
                var result = results.Single(r => r.Id.Equals(probe.Id));
                writer.WriteStartObject();
                writer.WriteString("id", probe.Id.ToString()); writer.WriteString("kind", probe.Kind.ToString());
                writer.WriteString("prompt", probe.Prompt); writer.WriteString("expected", probe.Expected);
                writer.WriteNumber("declaredMaxCostUsd", probe.MaxCostUsd);
                writer.WriteString("status", result.Status.ToString()); writer.WriteNumber("score", result.Score);
                writer.WriteString("output", result.Output); writer.WriteString("error", result.Error);
                writer.WriteNumber("durationTicks", result.Duration.Ticks);
                if (result.CostUsd is { } cost) writer.WriteNumber("costUsd", cost); else writer.WriteNull("costUsd");
                writer.WriteNumber("reportedUsageFields", (int)result.ReportedUsageFields);
                writer.WritePropertyName("usage");
                if (ReportedUsage(result) is { } usage)
                {
                    writer.WriteStartObject();
                    WriteCount(writer, "input", usage.Input); WriteCount(writer, "output", usage.Output);
                    WriteCount(writer, "cacheRead", usage.CacheRead); WriteCount(writer, "cacheWrite", usage.CacheWrite);
                    WriteCount(writer, "reasoning", usage.Reasoning);
                    writer.WriteEndObject();
                }
                else writer.WriteNullValue();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("traits"); writer.WriteStartArray();
            foreach (var trait in traits)
            {
                writer.WriteStartObject(); writer.WriteString("trait", trait.Trait);
                writer.WriteNumber("value", trait.Value); writer.WriteNumber("confidence", trait.Confidence);
                writer.WriteNumber("samples", trait.Samples); writer.WriteString("source", trait.Source);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCount(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } count) writer.WriteNumber(name, count); else writer.WriteNull(name);
    }
}
