namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Models;

internal sealed record SessionModelCatalogEntry(ModelDefinition Model, ProviderDescriptor Provider,
    ModelRoute Route, BillingMode BillingMode, long PolicyRevision, string? PolicyCategory);

/// <summary>Read-only, local catalog used by the native Session model selector.</summary>
internal static class SessionModelCatalog
{
    internal sealed record Snapshot(string Revision, IReadOnlyList<SessionModelCatalogEntry> Models);

    public static Snapshot Read(IPlatformPaths paths, CancellationToken cancellationToken)
    {
        var loaded = OmniHost.LoadUserConfiguration(paths);
        using var policies = new ModelPolicyService(new SqliteModelPolicyStore(paths.UserDatabasePath));
        var entries = loaded.Registry.Models().Select(model =>
        {
            var provider = loaded.Registry.Provider(model.ProviderId)
                ?? throw new InvalidDataException("Configured model provider is missing.");
            var route = ModelRoutingHost.RouteFor(model, provider);
            var stored = policies.Get(ModelPolicyKey.For(model.ProviderId, model.Id), cancellationToken);
            return new SessionModelCatalogEntry(model, provider, route, provider.BillingMode,
                stored?.Revision ?? 0, stored?.Policy.Category.ToString());
        }).OrderBy(entry => entry.Model.Id, StringComparer.Ordinal)
          .ThenBy(entry => entry.Provider.Id, StringComparer.Ordinal).ToArray();

        var canonical = CanonicalBytes(entries);
        var revision = Convert.ToHexStringLower(SHA256.HashData(canonical));
        return new Snapshot(revision, entries);
    }

    private static byte[] CanonicalBytes(IReadOnlyList<SessionModelCatalogEntry> entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                var model = entry.Model;
                var reasoning = model.ReasoningCapability;
                writer.WriteStartObject();
                writer.WriteString("id", model.Id);
                writer.WriteString("provider", model.ProviderId);
                writer.WriteString("route", entry.Route.Id.Value);
                writer.WriteString("routeIdentity", Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(entry.Route.CanonicalJson()))));
                writer.WriteString("billing", entry.BillingMode.ToString());
                writer.WriteNumber("context", model.ContextWindow);
                writer.WriteNumber("recommendedContext", model.RecommendedUsableContext);
                writer.WriteNumber("maxOutput", model.MaxOutputTokens);
                writer.WriteBoolean("outputLimitConfigured", model.OutputLimitConfigured);
                if (model.ParameterCountBillions is { } parameters) writer.WriteNumber("parameters", parameters);
                else writer.WriteNull("parameters");
                if (model.ImageGenerationMaxCostUsd is { } imageCost) writer.WriteNumber("imageCost", imageCost);
                else writer.WriteNull("imageCost");
                WriteStrings(writer, "input", model.InputModalities);
                WriteStrings(writer, "output", model.OutputModalities);
                if (reasoning.Supported is { } supported) writer.WriteBoolean("reasoningSupported", supported);
                else writer.WriteNull("reasoningSupported");
                if (reasoning.EffortLevels is { } levels) WriteStrings(writer, "reasoningEfforts", levels);
                else writer.WriteNull("reasoningEfforts");
                if (reasoning.ReplayPolicy is { } replay) writer.WriteString("reasoningReplay", replay.ToString());
                else writer.WriteNull("reasoningReplay");
                if (reasoning.UltraCodeBudgetTokens is { } budget) writer.WriteNumber("ultraBudget", budget);
                else writer.WriteNull("ultraBudget");
                if (reasoning.UltraCodeOutputReserveTokens is { } reserve) writer.WriteNumber("ultraReserve", reserve);
                else writer.WriteNull("ultraReserve");
                writer.WriteNumber("policyRevision", entry.PolicyRevision);
                if (entry.PolicyCategory is null) writer.WriteNull("policyCategory");
                else writer.WriteString("policyCategory", entry.PolicyCategory);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return buffer.ToArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values.Order(StringComparer.Ordinal)) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    public static SessionModelCatalogEntry? Resolve(Snapshot snapshot, string modelId) =>
        snapshot.Models.SingleOrDefault(entry => StringComparer.Ordinal.Equals(entry.Model.Id, modelId));
}
