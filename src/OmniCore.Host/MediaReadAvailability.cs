namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Models;

/// <summary>Effective capability gate for the native image-reading tool.</summary>
internal static class MediaReadAvailability
{
    internal static bool CanExpose(ProviderDescriptor? provider, EffectiveModelProfile profile, HarnessPolicy harness) =>
        provider is { SupportsNativeToolCalls: true }
        && (provider.Family is ProviderFamily.OpenAIResponses or ProviderFamily.AnthropicMessages
            or ProviderFamily.OpenAiChatCompatible)
        && profile.InputModalities.Contains("image", StringComparer.Ordinal)
        && profile.ToolCallFormats.Contains(ToolCallFormat.Native)
        && harness.ToolCallFormat == ToolCallFormat.Native;
}
