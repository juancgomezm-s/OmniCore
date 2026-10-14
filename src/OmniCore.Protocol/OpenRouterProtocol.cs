namespace OmniCore.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Protocol surface for the user-scoped OpenRouter account and model catalog.</summary>
public static class OpenRouterProtocol
{
    public const string StatusQuery = "openrouter";
    public const string CreditsQuery = "openrouter.credits";
    public static string CreditsQueryFor(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 96
            || providerId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException("OpenRouter provider id is invalid.", nameof(providerId));
        return CreditsQuery + ":" + providerId;
    }

    public sealed record ProviderState(string ProviderId, string State, bool CanConnect,
        bool CanTest, bool CanDiscover, bool CanDisconnect, DateTimeOffset? ValidatedAt,
        DateTimeOffset? DiscoveredAt, int RegisteredModelCount);

    public sealed record CreditState(string ProviderId, string KeyAvailability, DateTimeOffset KeyAsOf,
        string Availability, DateTimeOffset? AccountBalanceAsOf, string? Limitation,
        decimal? KeyLimitUsd, decimal? KeyLimitRemainingUsd, decimal? KeyUsageUsd,
        decimal? AccountBalanceUsd, DateTimeOffset AsOf, DateTimeOffset LastAttemptAt,
        bool? IsManagementKey, string? LimitResetInterval, DateTimeOffset? LimitResetAt, string Currency,
        string KeySource, string? AccountSource);

    public static string EncodeStatus(IReadOnlyList<ProviderState> states) =>
        JsonSerializer.Serialize(states.ToArray(), OpenRouterJsonContext.Default.ProviderStateArray);

    public static string EncodeCredits(CreditState state) =>
        JsonSerializer.Serialize(state, OpenRouterJsonContext.Default.CreditState);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenRouterProtocol.ProviderState[]))]
[JsonSerializable(typeof(OpenRouterProtocol.CreditState))]
internal partial class OpenRouterJsonContext : JsonSerializerContext;
