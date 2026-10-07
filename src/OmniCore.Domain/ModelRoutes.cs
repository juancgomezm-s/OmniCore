namespace OmniCore.Domain;

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>Stable identity for a concrete provider route.</summary>
public sealed class RouteId : IEquatable<RouteId>
{
    public string Value { get; }

    public RouteId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("RouteId is required.", nameof(value));
        Value = value;
    }

    /// <summary>Uses the existing model id so a legacy ModelDefinition maps to its 1:1 default route.</summary>
    public static RouteId ForDefaultModel(string modelId) => new(modelId);

    /// <summary>Derives a deterministic id for a non-default provider route.</summary>
    public static RouteId ForRoute(string providerId, string endpoint, ProviderFamily protocol,
        string? profile, string providerModelName)
    {
        Required(providerId, nameof(providerId));
        Required(endpoint, nameof(endpoint));
        Required(providerModelName, nameof(providerModelName));
        if (!Enum.IsDefined(protocol)) throw new ArgumentOutOfRangeException(nameof(protocol));
        if (profile is not null && string.IsNullOrWhiteSpace(profile))
            throw new ArgumentException("Profile must be null or non-empty.", nameof(profile));
        var json = ModelRoute.IdentityJson(providerId, endpoint, protocol, profile, providerModelName);
        return new("route-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value;

    public override string ToString() => Value;
    public bool Equals(RouteId? other) => other is not null && StringComparer.Ordinal.Equals(Value, other.Value);
    public override bool Equals(object? obj) => obj is RouteId other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}

/// <summary>
/// A concrete way to invoke a logical model: provider, endpoint, protocol/profile, and the model
/// name understood by that provider (ADR-0046 §3). This contract does not decide availability,
/// billing authorization, or scheduling.
/// </summary>
public sealed class ModelRoute : IEquatable<ModelRoute>
{
    public RouteId Id { get; }
    public string ProviderId { get; }
    public string Endpoint { get; }
    public ProviderFamily Protocol { get; }
    public string? Profile { get; }
    public string ProviderModelName { get; }
    public ReasoningCapability ReasoningCapability { get; }

    public ModelRoute(string providerId, string endpoint, ProviderFamily protocol, string? profile,
        string providerModelName, RouteId? id = null, ReasoningCapability? reasoningCapability = null)
    {
        ProviderId = Required(providerId, nameof(providerId));
        Endpoint = Required(endpoint, nameof(endpoint));
        ProviderModelName = Required(providerModelName, nameof(providerModelName));
        if (profile is not null && string.IsNullOrWhiteSpace(profile))
            throw new ArgumentException("Profile must be null or non-empty.", nameof(profile));
        if (!Enum.IsDefined(protocol))
            throw new ArgumentOutOfRangeException(nameof(protocol));

        Protocol = protocol;
        Profile = profile;
        ReasoningCapability = reasoningCapability ?? OmniCore.Domain.ReasoningCapability.Unknown;
        Id = id ?? RouteId.ForRoute(ProviderId, Endpoint, Protocol, Profile, ProviderModelName);
    }

    /// <summary>
    /// Creates the migration route for an existing ModelDefinition. Its route id and provider-side
    /// model name both retain the legacy model id; endpoint/protocol/profile come from its provider.
    /// </summary>
    public static ModelRoute DefaultForModel(string modelId, string providerId, string endpoint,
        ProviderFamily protocol, string? profile = null, ReasoningCapability? reasoningCapability = null) =>
        new(providerId, endpoint, protocol, profile, modelId, RouteId.ForDefaultModel(modelId), reasoningCapability);

    /// <summary>Stable JSON representation of the route identity tuple.</summary>
    public string CanonicalJson() => IdentityJson(ProviderId, Endpoint, Protocol, Profile, ProviderModelName);

    public bool Equals(ModelRoute? other) => other is not null
        && StringComparer.Ordinal.Equals(CanonicalJson(), other.CanonicalJson());
    public override bool Equals(object? obj) => obj is ModelRoute other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalJson());

    internal static string IdentityJson(string providerId, string endpoint, ProviderFamily protocol,
        string? profile, string providerModelName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("providerId", providerId);
            writer.WriteString("endpoint", endpoint);
            writer.WriteString("protocol", protocol.ToString());
            if (profile is null) writer.WriteNull("profile"); else writer.WriteString("profile", profile);
            writer.WriteString("providerModelName", providerModelName);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value;
}

/// <summary>Declared policy for replaying reasoning state between ModelSteps (ADR-0046 §3).</summary>
public enum ReasoningReplayPolicy
{
    None,
    ProviderManaged,
    RequiredWithTools,
    PreserveAcrossSteps,
}
