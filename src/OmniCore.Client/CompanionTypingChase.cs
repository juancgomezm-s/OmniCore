namespace OmniCore.Client;

/// <summary>A short lived visual target. Only the latest grapheme is retained; never a draft.</summary>
public sealed class CompanionTypingChase
{
    private int _remaining;
    private int _x;
    public string? Glyph { get; private set; }
    public bool Active => _remaining > 0;
    public void Follow(string glyph, int startX)
    {
        if (!Active) _x = startX;
        Glyph = glyph; _remaining = 12;
    }
    public void Clear() { _remaining = 0; Glyph = null; }
    public void Advance(int targetX, int width)
    {
        if (!Active) return;
        var destination = Destination(targetX, width);
        var distance = destination - _x;
        _x += Math.Sign(distance) * Math.Min(Math.Abs(distance), Math.Max(2, Math.Abs(distance) / 3));
        _x = Math.Clamp(_x, 0, Math.Max(0, width - CompanionPetPlayground.SpriteWidth - 2));
        if (--_remaining == 0) Clear();
    }
    public CompanionPlayPose Pose(int targetX, int width, int height, long frame)
    {
        var destination = Destination(targetX, width);
        var x = Math.Clamp(_x, 0, Math.Max(0, width - CompanionPetPlayground.SpriteWidth - 2));
        return new(Active, x, targetX < x, Math.Abs(destination - x) <= 2 ? "pounce" : "dash", frame,
            Math.Max(0, height - CompanionPetPlayground.SpriteHeight));
    }
    private static int Destination(int targetX, int width) => Math.Clamp(
        targetX >= CompanionPetPlayground.SpriteWidth + 2 ? targetX - CompanionPetPlayground.SpriteWidth - 2 : targetX + 3,
        0, Math.Max(0, width - CompanionPetPlayground.SpriteWidth - 2));
}
