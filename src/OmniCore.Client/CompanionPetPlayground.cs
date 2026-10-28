namespace OmniCore.Client;

/// <summary>A local, deterministic playground clock. No profile, chat or execution state is changed.</summary>
public sealed class CompanionPetPlayground
{
    public const int SpriteWidth = 12;
    public const int SpriteHeight = 5;
    public long Frame { get; private set; }
    public bool AtHome { get; private set; }
    public void Advance()
    {
        if (!AtHome) Frame = Frame == long.MaxValue ? 0 : Frame + 1;
    }
    public void Play() { AtHome = false; Frame = 20; }
    public void Home() { AtHome = true; Frame = 0; }
    public CompanionPlayPose Pose(int availableWidth, int availableHeight = SpriteHeight)
    {
        var phase = (int)(Frame % 120) - 20;
        var span = Math.Max(0, availableWidth - SpriteWidth - 2);
        if (AtHome || phase is < 0 or >= 80) return new(false, span, false, "idle", Frame);
        var maxY = Math.Max(0, availableHeight - SpriteHeight);
        var upper = (int)(maxY * (1 + Frame / 120 % 3) / 4);
        var lineStep = Math.Min(2, maxY);
        var lower = Math.Min(maxY, upper + lineStep * 3);
        var alternate = Frame / 120 % 2 == 1;
        int x, y; bool left; string animation;
        if (phase < 16)
        {
            x = span * (15 - phase) / 15; y = upper; left = true; animation = "run";
        }
        else if (phase < 22)
        {
            x = 0; y = upper; left = phase % 2 == 0; animation = "peek";
        }
        else if (phase < 34)
        {
            var step = phase - 22;
            var zigzag = step % 6;
            x = span / 3 * (zigzag <= 3 ? zigzag : 6 - zigzag) / 3;
            y = Math.Min(maxY, upper + step / 4 * lineStep);
            left = zigzag >= 3; animation = "weave";
        }
        else if (phase < 42)
        {
            var step = phase - 34;
            x = Lerp(span / 9, span / 2, step, 7);
            y = Math.Max(0, lower - (step % 4 is 1 or 2 ? lineStep : 0));
            left = false; animation = "hop";
        }
        else if (phase < 50)
        {
            x = Lerp(span / 2, span / 4, phase - 42, 7); y = lower;
            left = true; animation = "roll";
        }
        else if (phase < 58)
        {
            var step = phase - 50;
            x = Lerp(span / 4, span / 2, step, 7);
            y = Math.Max(0, Lerp(lower, upper, step, 7) - step * (7 - step) / 6);
            left = false; animation = "pounce";
        }
        else if (phase < 64)
        {
            x = Math.Clamp(span / 2 + (phase % 2 == 0 ? -1 : 1), 0, span);
            y = Math.Min(maxY, upper + phase % 2); left = phase % 2 == 0; animation = "dance";
        }
        else if (phase < 72)
        {
            x = Lerp(span / 2, span, phase - 64, 7);
            y = upper; left = false; animation = "dash";
        }
        else
        {
            x = span; y = Math.Min(maxY, upper + lineStep); left = true; animation = "hide";
        }
        // Successive outings weave through different bands in both directions.
        if (alternate) y = maxY - y;
        return new(true, x, left, animation, Frame, y);
    }
    private static int Lerp(int from, int to, int step, int length) => from + (to - from) * step / length;
}

public sealed record CompanionPlayPose(bool Outside, int X, bool FacingLeft, string Animation, long Frame, int Y = 0);
