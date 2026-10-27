namespace OmniCore.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Read-only snapshot of active, consented extension shortcut declarations.</summary>
public sealed record ExtensionKeyBindingContribution(string Keys, string Command, IReadOnlyList<string> Arguments,
    string When, string Source, string ManifestSha256, string PackageSha256, string BindingFingerprint);
public sealed record ExtensionKeyBindingDiagnostic(string Code, string Source, string ExtensionId);
public sealed record ExtensionKeyBindingSnapshot(IReadOnlyList<ExtensionKeyBindingContribution> Bindings,
    IReadOnlyList<ExtensionKeyBindingDiagnostic> Diagnostics);

public static class ExtensionKeyBindingProtocol
{
    public static string Encode(ExtensionKeyBindingSnapshot value) =>
        JsonSerializer.Serialize(value, KeyBindingJsonContext.Default.ExtensionKeyBindingSnapshot);

    public static ExtensionKeyBindingSnapshot Decode(string json)
    {
        var value = JsonSerializer.Deserialize(json, KeyBindingJsonContext.Default.ExtensionKeyBindingSnapshot)
            ?? throw new JsonException("extension_keybindings_invalid");
        if (value.Bindings is null || value.Diagnostics is null || value.Bindings.Count > 256
            || value.Diagnostics.Count > 256
            || value.Bindings.Any(binding => binding is null || string.IsNullOrWhiteSpace(binding.Keys) || binding.Keys.Length > 256
                || string.IsNullOrWhiteSpace(binding.Command) || binding.Command.Length > 160 || binding.Arguments is null
                || binding.Arguments.Count > 16 || binding.Arguments.Any(argument => argument is null || argument.Length > 4096)
                || binding.When is null || binding.When.Length > 512 || string.IsNullOrWhiteSpace(binding.Source) || binding.Source.Length > 512
                || !Hash(binding.ManifestSha256) || !Hash(binding.PackageSha256) || !Hash(binding.BindingFingerprint))
            || value.Diagnostics.Any(issue => issue is null || string.IsNullOrWhiteSpace(issue.Code) || issue.Code.Length > 80
                || issue.Source is null || issue.Source.Length > 512 || issue.ExtensionId is null || issue.ExtensionId.Length > 160))
            throw new JsonException("extension_keybindings_invalid");
        return value;
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExtensionKeyBindingSnapshot))]
[JsonSerializable(typeof(ExtensionKeyBindingContribution))]
[JsonSerializable(typeof(ExtensionKeyBindingDiagnostic))]
internal sealed partial class KeyBindingJsonContext : JsonSerializerContext;
