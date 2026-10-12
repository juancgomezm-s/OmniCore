namespace OmniCore.Client;

using System.Text.Json;
using OmniCore.Protocol;

/// <summary>Literal external copy, bounded and allow-listed. Never interpolated or executed.</summary>
public static class ExternalUiResources
{
    public static IReadOnlyDictionary<string, string> ReadDescriptions(JsonElement element, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty(property, out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var locale in UiLocaleCodes.Supported)
                if (map.TryGetProperty(locale, out var value) && Read(value) is { } text) result[locale] = text;
        return result;
    }

    public static IReadOnlyDictionary<string, ExtensionUiText> ReadExtension(JsonElement element)
    {
        var result = new Dictionary<string, ExtensionUiText>(StringComparer.Ordinal);
        if (!element.TryGetProperty("uiResources", out var map) || map.ValueKind != JsonValueKind.Object) return result;
        foreach (var locale in UiLocaleCodes.Supported)
            if (map.TryGetProperty(locale, out var row) && row.ValueKind == JsonValueKind.Object)
                result[locale] = new(
                    row.TryGetProperty("extension.displayName", out var name) ? Read(name) : null,
                    row.TryGetProperty("extension.description", out var description) ? Read(description) : null);
        return result;
    }

    private static string? Read(JsonElement value) => value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 and <= 2048 } text && !string.IsNullOrWhiteSpace(text)
            ? new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()) : null;
}

public sealed record ExtensionUiText(string? DisplayName, string? Description);
