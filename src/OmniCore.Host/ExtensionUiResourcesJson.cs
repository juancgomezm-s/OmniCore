namespace OmniCore.Host;

using OmniCore.Protocol;

internal static class ExtensionUiResourcesJson
{
    public static string Encode(IReadOnlyDictionary<string, Dictionary<string, string>>? resources) => resources is null
        ? "null" : "{" + string.Join(",", resources.Select(locale => "\"" + JsonObj.Escape(locale.Key) + "\":"
            + EncodeDescriptions(locale.Value))) + "}";

    public static string EncodeDescriptions(IReadOnlyDictionary<string, string>? values) => values is null
        ? "null" : "{" + string.Join(",", values.Select(pair => JsonObj.Field(pair.Key, pair.Value))) + "}";
}
