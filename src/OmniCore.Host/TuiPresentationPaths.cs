namespace OmniCore.Host;

/// <summary>Primitive Host facade: visual clients need no reference to platform-path abstractions.</summary>
public static class TuiPresentationPaths
{
    /// <summary>Dedicated user presentation config, outside the repository and account files.</summary>
    public static string AnimationSettingsPath() =>
        Path.Combine(OmniHost.CreatePlatformPaths().ConfigDirectory, "tui-animations.json");
}
