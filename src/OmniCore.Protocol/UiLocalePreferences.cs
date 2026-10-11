namespace OmniCore.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Canonical UI locale codes shared by the Client and configuration Host.</summary>
public static class UiLocaleCodes
{
    public static IReadOnlyList<string> Supported { get; } = ["es", "en", "fr", "it"];

    public static bool TryNormalize(string? value, out string locale)
    {
        locale = "es";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Replace('_', '-').Split('-');
        if (parts.Length == 0 || parts.Any(part => part.Length == 0
                || part.Any(character => !char.IsAsciiLetterOrDigit(character)))) return false;
        var language = parts[0].ToLowerInvariant();
        if (!Supported.Contains(language, StringComparer.Ordinal)) return false;
        locale = language;
        return true;
    }

    public static string NormalizeOrDefault(string? value) => TryNormalize(value, out var locale) ? locale : "es";
}

/// <summary>User-only UI language setting; it is deliberately not a scoped workspace preference.</summary>
public sealed record UiLocalePreferencesSnapshot(string Revision, string Locale,
    IReadOnlyList<string> Diagnostics);

/// <summary>Request for the trusted Host operation that persists User <c>ui.locale</c>.</summary>
public sealed record UiLocaleUpdateRequest(string Scope, string Locale, string Revision);

public static class UiLocalePreferencesProtocol
{
    public const string QueryName = "uiLocaleSettings";
    public const string ConfigureCommand = "ui.locale.configure";

    public static string Encode(UiLocalePreferencesSnapshot value) =>
        JsonSerializer.Serialize(value, UiLocaleJsonContext.Default.UiLocalePreferencesSnapshot);

    public static UiLocalePreferencesSnapshot? Decode(string json) =>
        JsonSerializer.Deserialize(json, UiLocaleJsonContext.Default.UiLocalePreferencesSnapshot);

    public static string Encode(UiLocaleUpdateRequest value) =>
        JsonSerializer.Serialize(value, UiLocaleJsonContext.Default.UiLocaleUpdateRequest);

    public static UiLocaleUpdateRequest? DecodeUpdate(string json) =>
        JsonSerializer.Deserialize(json, UiLocaleJsonContext.Default.UiLocaleUpdateRequest);

    public static WireEnvelope Configure(string scope, string locale, string revision) =>
        WireEnvelope.Command(Ids.NewV7(), "{" + string.Join(",", new[]
        {
            JsonObj.Field("cmd", ConfigureCommand), JsonObj.Field("scope", scope),
            JsonObj.Field("locale", locale), JsonObj.Field("revision", revision),
        }) + "}");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UiLocalePreferencesSnapshot))]
[JsonSerializable(typeof(UiLocaleUpdateRequest))]
internal partial class UiLocaleJsonContext : JsonSerializerContext;
