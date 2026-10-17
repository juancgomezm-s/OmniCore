using System.Globalization;

namespace OmniCore.Client;

public static partial class AnimationCatalog
{
    public static int Rows(string effect) => effect switch
    {
        "square-snake" or "infinity-track" => 5,
        "fire3" or "matrix3" or "starfield3" or "aurora" => 3,
        _ => 1,
    };

    public static bool IsText(string effect) => effect is "text-pulse" or "text-scramble" or "text-bounce"
        or "glitch-text" or "crush" or "shimmer" or "typewriter";

    /// <summary>Fresh native frames from geometric patterns; no terminal escapes or external runtime.</summary>
    public static IReadOnlyList<IReadOnlyList<AnimationCell>> RenderFrame(string effect, long frame, int width,
        int height = 1, string label = "", bool moving = true, bool ascii = false)
    {
        width = Math.Clamp(width, 0, 80); height = Math.Clamp(height, 1, Rows(effect));
        if (IsText(effect)) return [RenderText(effect, label, frame, width, moving, ascii)];
        if (effect is "omega-pulse" or "orbit-dots" or "block-wave" or "conveyor" or "braille-spinner")
            return [Render(effect, frame, width, moving, ascii)];
        var tick = Mod(frame, 100_000);
        var rows = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat(new AnimationCell(" ", 0), width).ToArray()).ToArray();
        if (width == 0) return rows;
        rows[0][0] = new(ascii ? "*" : "✦", 5);
        if (!moving) return rows;
        var length = Math.Max(1, width - 2);
        var infinity = effect == "infinity-track" ? Enumerable.Range(0, 96).Select(step =>
        {
            var angle = step * Math.PI * 2 / 96;
            return ((int)Math.Round((length - 1) * (.5 + .48 * Math.Sin(angle))),
                (int)Math.Round((height - 1) * (.5 + .45 * Math.Sin(angle * 2))));
        }).ToHashSet() : null;
        for (var y = 0; y < height; y++)
        for (var x = 2; x < width; x++)
        {
            var p = x - 2;
            var glyph = " "; var strength = 0;
            switch (effect)
            {
                case "neural-pulse":
                    strength = Math.Max(1, 5 - Mod(p - tick, length));
                    glyph = p % 4 == 0 ? "●" : "─";
                    break;
                case "plasma-wave":
                    strength = Math.Clamp((int)(2.5 + 1.5 * Math.Sin(p * .6 + tick * .25) + Math.Sin(p * .2 - tick * .3)), 0, 5);
                    glyph = "▂▄▆█▆▄"[strength].ToString(); break;
                case "pacman":
                    var eater = tick % length;
                    glyph = p == eater ? tick % 2 == 0 ? "C" : "●" : p > eater && p % 2 == 0 ? "·" : " ";
                    strength = p == eater ? 5 : 2; break;
                case "matrix": case "matrix3":
                    var drop = Mod(tick - p * 3, height + 5);
                    strength = Math.Clamp(5 - Mod(drop - y, height + 5), 0, 5);
                    glyph = strength == 0 ? " " : "01:+*#"[Mod(p * 7 + y * 3 + tick / 2, 6)].ToString(); break;
                case "pipeline":
                    var stage = p / 3; var active = tick / 3 % Math.Max(1, (length + 2) / 3);
                    glyph = p % 3 == 1 ? stage < active ? "✓" : stage == active ? "◆" : "○" : "─";
                    strength = stage == active ? 5 : stage < active ? 3 : 1; break;
                case "starfield": case "starfield3":
                    var star = Mod(p * 17 + y * 23 + tick * (1 + y), 37);
                    glyph = star == 0 ? "✧" : star < 3 ? "·" : " "; strength = star == 0 ? 5 : 2; break;
                case "fire": case "fire3":
                    var heat = 2.3 + 1.6 * Math.Sin(p * 1.9 + tick * .8) + Math.Sin(p * .5 - tick * .6) + y * .65;
                    strength = Math.Clamp((int)heat, 0, 5); glyph = " ░▒▓██"[strength].ToString(); break;
                case "icon-morph":
                    glyph = p == length / 2 ? new[] { "◇", "◆", "○", "●", "✧", "✦" }[tick / 2 % 6] : " ";
                    strength = 5; break;
                case "brainstorm":
                    glyph = p == length / 2 ? new[] { "☁", "ϟ", "☂", "☀" }[tick / 3 % 4] : p % 4 == tick % 4 ? "·" : " ";
                    strength = p == length / 2 ? 5 : 2; break;
                case "dev-constellation":
                    glyph = p % 4 == 0 ? new[] { "{", "}", "#", ";", "/", "=" }[p / 4 % 6] : "·";
                    strength = Mod(p - tick, length) < 3 ? 5 : 1; break;
                case "pi-pulse":
                    glyph = p == length / 2 ? "π" : "·";
                    strength = Math.Clamp(5 - Math.Abs(p - length / 2) + tick % 5 - 2, 1, 5); break;
                case "neon-bounce":
                    glyph = p is 0 || p == length - 1 ? "│" : p == Bounce(tick, Math.Max(1, length - 2)) + 1 ? "●" : " ";
                    strength = 5; break;
                case "accordion":
                    var extent = Bounce(tick, length);
                    glyph = Math.Abs(p - length / 2) <= extent / 2 ? p % 2 == 0 ? "▐" : "▌" : " ";
                    strength = 2 + (p + tick) % 4; break;
                case "square-snake":
                    var perimeter = Math.Max(1, 2 * length + 2 * height - 4);
                    var edge = y == 0 ? p : p == length - 1 ? length + y - 1 : y == height - 1
                        ? length + height - 2 + length - 1 - p : p == 0 ? perimeter - y : -1;
                    strength = edge < 0 ? 0 : Math.Clamp(5 - Mod(tick - edge, perimeter), 0, 5);
                    glyph = strength > 0 ? "■" : " "; break;
                case "infinity-track":
                    glyph = infinity!.Contains((p, y)) ? "·" : " "; strength = 1;
                    var movingX = (int)Math.Round((length - 1) * (.5 + .48 * Math.Sin(tick * .15)));
                    var movingY = (int)Math.Round((height - 1) * (.5 + .45 * Math.Sin(tick * .3)));
                    if (p == movingX && y == movingY) { glyph = "●"; strength = 5; } break;
                case "aurora":
                    var ribbon = (height - 1) * (.5 + .4 * Math.Sin(p * .35 + tick * .12));
                    strength = Math.Clamp((int)(5 - Math.Abs(y - ribbon) * 2), 0, 5);
                    glyph = strength < 2 ? "·" : strength < 4 ? "▒" : "▓"; break;
            }
            if (ascii && glyph.Any(ch => ch > 126)) glyph = strength > 3 ? "#" : strength > 1 ? "+" : ".";
            rows[y][x] = new(glyph, strength);
        }
        return rows;
    }

    public static IReadOnlyList<AnimationCell> RenderText(string effect, string text, long frame, int width,
        bool moving = true, bool ascii = false)
    {
        var elements = new List<string>();
        var iterator = StringInfo.GetTextElementEnumerator(text);
        while (iterator.MoveNext() && elements.Count < Math.Clamp(width, 0, 80)) elements.Add(iterator.GetTextElement());
        var cells = elements.Select(glyph => new AnimationCell(glyph, 2)).ToArray();
        if (!moving || cells.Length == 0) return cells;
        var center = effect == "text-bounce" ? Bounce(frame, cells.Length) : Mod(frame, cells.Length + 4) - 2;
        for (var x = 0; x < cells.Length; x++)
        {
            var distance = Math.Abs(x - center);
            var glyph = cells[x].Glyph; var strength = 2;
            switch (effect)
            {
                case "text-pulse": case "text-scramble": case "text-bounce":
                    if (distance <= 1) strength = distance == 0 ? 5 : 4;
                    if (distance == 0 && !string.IsNullOrWhiteSpace(glyph))
                        glyph = ascii ? "*" : effect == "text-pulse" ? "✦" : new[] { "†", "‡", "§", "※", "✧" }[Mod(frame, 5)];
                    break;
                case "shimmer": strength = Math.Clamp(5 - distance, 1, 5); break;
                case "glitch-text":
                    if (Mod(x * 7 + frame, 13) < 2) { glyph = ascii ? "#" : "▓"; strength = 5; } break;
                case "crush":
                    if (distance < 2 && !string.IsNullOrWhiteSpace(glyph))
                    { glyph = new[] { "/", "#", "+", "*", "=" }[Mod(frame + x, 5)]; strength = 5; } break;
                case "typewriter":
                    if (x > Mod(frame, cells.Length + 5)) glyph = " ";
                    else strength = x == Mod(frame, cells.Length + 5) ? 5 : 2; break;
            }
            cells[x] = new(glyph, strength);
        }
        return cells;
    }

    private static int Mod(long value, int period) => (int)((value % period + period) % period);
    private static int Bounce(long value, int length)
    {
        if (length <= 1) return 0;
        var p = Mod(value, length * 2 - 2);
        return p < length ? p : length * 2 - 2 - p;
    }
}
