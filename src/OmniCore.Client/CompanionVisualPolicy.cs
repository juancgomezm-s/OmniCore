namespace OmniCore.Client;

/// <summary>Evidence supplied by a renderer, never inferred from an OS or terminal name.</summary>
public sealed record CompanionVisualCapabilities(bool Interactive, bool Unicode, bool Sixel = false,
    bool Kitty = false, bool Color = true, string Evidence = "unprobed");
public sealed record CompanionVisualSelection(string Requested, string Effective, string? Backend, string Reason);

/// <summary>Portable rendering policy, also usable by a future graphical client.</summary>
public sealed class CompanionVisualPolicy
{
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
    // Two frames per expression; static rest, with one brief blink every twenty seconds.
    public static int SpriteFrame(string animation, long tick, bool reducedMotion) => (animation switch
    {
        "working" => 2, "waiting" => 4, "success" => 6, "failure" => 8, "pet" => 10, "sleep" => 12, _ => 0,
    }) + (animation == "sleep" || reducedMotion ? 0 : animation is "idle" ? tick % 50 == 49 ? 1 : 0
        : animation is "working" or "waiting" && tick % 20 >= 4 ? 0 : (int)(tick % 2));
}
