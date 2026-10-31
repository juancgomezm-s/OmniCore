namespace OmniCore.Host;

using System.Text.Json;

public sealed partial class OmniCliRuntime
{
    private static string ReadSkillInvocationText(string? inputPartsJson)
    {
        if (inputPartsJson is null) return "";
        using var document = JsonDocument.Parse(inputPartsJson);
        var value = document.RootElement;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", value.EnumerateArray().Select(part =>
            part.ValueKind == JsonValueKind.String ? part.GetString() ?? ""
                : part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : ""));
    }
}
