namespace OmniCore.Tools;

using OmniCore.Domain;

/// <summary>Stable resource-key/argument pair for argument diagnostics crossing the Host boundary.</summary>
internal static class ToolArgumentDiagnostic
{
    public static string Invalid(string key, params (string Name, string Value)[] arguments)
    {
        return LocalizedText.Of(key, arguments).Render();
    }
}
