namespace OmniCore.Client;

/// <summary>Evidence supplied by a renderer, never inferred from an OS or terminal name.</summary>
public sealed record CompanionVisualCapabilities(bool Interactive, bool Unicode, bool Sixel = false,
    bool Kitty = false, bool Color = true, string Evidence = "unprobed");
public sealed record CompanionVisualSelection(string Requested, string Effective, string? Backend, string Reason);

/// <summary>Portable rendering policy, also usable by a future graphical client.</summary>
public sealed class CompanionVisualPolicy
{
    public const int GraphicFrameCount = 20;
    public bool GraphicsFailed { get; private set; }
    public void FailGraphics() => GraphicsFailed = true;
    public void Redetect() => GraphicsFailed = false;
    public CompanionVisualSelection Select(string requested, bool visible, CompanionVisualCapabilities capabilities)
    {
        requested = Normalize(requested);
        if (!visible || requested == "hidden") return new(requested, "hidden", null, "hidden");
        if (!capabilities.Interactive) return new(requested, "text", null, "non-interactive");
        if (requested is "auto" or "graphics" && !GraphicsFailed && capabilities.Color && (capabilities.Kitty || capabilities.Sixel))
            return new(requested, "graphics", capabilities.Kitty ? "kitty" : "sixel", capabilities.Evidence);
        var effective = requested != "text" && capabilities.Unicode ? "unicode" : "text";
        return new(requested, effective, null, GraphicsFailed ? "graphics-failed" : capabilities.Evidence);
    }
    public static string Normalize(string value) => value.Trim().ToLowerInvariant() switch
    {
        "auto" => "auto", "graphics" or "graphic" or "gráfico" or "grafico" => "graphics",
        "unicode" => "unicode", "text" or "texto" => "text", "hidden" or "oculto" => "hidden",
        _ => throw new ArgumentException("/pet mode auto | gráfico | unicode | texto | oculto"),
    };
    // Brief gestures at the existing panel cadence, separated by long still pauses.
    // Original expression IDs 0..12 remain stable; 13..17 add breathing and glances.
    public static int SpriteFrame(string animation, long tick, bool reducedMotion)
    {
        var first = animation switch
        {
            "working" => 2, "waiting" => 4, "success" => 6, "failure" => 8, "pet" => 10, "sleep" => 12, "sad" => 18, _ => 0,
        };
        if (reducedMotion) return first;
        tick = Math.Max(0, tick);
        if (animation == "idle") return (tick % 32) switch
        {
            3 or 5 => 13, 4 => 14, 12 => 1,
            20 or 22 => 15, 21 => 16, _ => 0,
        };
        if (animation == "sleep") return tick % 12 is 6 or 7 ? 17 : 12;
        return first + (animation is "working" or "waiting" && tick % 20 >= 4 ? 0 : (int)(tick % 2));
    }
}
