using OmniCore.Protocol;

namespace OmniCore.Client;

/// <summary>Original 20×16 pixel silhouettes. Every frame is produced by this same portable renderer.</summary>
public static class CompanionSpriteCatalog
{
    public const int Width = 20;
    public const int Height = 8;
    public const string Background = "#202C3B";
    private static readonly IReadOnlyDictionary<string, string[]> Art = new Dictionary<string, string[]>
    {
        ["egg"] = [
            "                    ", "       CCC          ", "      LCCCQ         ", "     LLCCCCQ        ",
            "     LLCCCCQ        ", "    CLCCCCCQQ       ", "    CLCCCCCQQ       ", "   CLCCCCCCCQQ      ",
            "   CLCCCCCCCQQ      ", "   CCCCCCCCCQQ      ", "   CCCCCCCCCQQ      ", "    CCCCCCCQQ       ",
            "    CCCCCCCQQ       ", "     CCCCCQQ        ", "      CQQQQ         ", "                    "],
        ["ox"] = [
            "                    ", "  CC          CC    ", "  CCC        CCC    ", "   CCBBBBBBBBCC     ",
            "  BBBBBBBBBBBBBB    ", "   BBBBBBBBBBBB     ", "   BBE BBBB EBB     ", "   BBBBBBBBBBBB     ",
            "   CCCCCCCCCCCCBB   ", "   CCNCCCCCCNCCBBB  ", "   CCCCCCCCCCCCBBB  ", "    DDDDDDDDDDBBBB  ",
            "      BBBBBBBBBBBB  ", "      BB BBBB  BB   ", "      DD DDDD  DD   ", "                    "],
        ["fox"] = [
            "                    ", "   B      B         ", "   BB    BB         ", "   BCB  BCB         ",
            "   BBBBBBBB         ", "  BBBBBBBBBB    CC  ", "  BBE BBB EBB  CCCC ", "   CCCBBCCC  BBCCCC ",
            "    CCCNCC  BBBBBB  ", "     CCCBBBBBBBBB   ", "     BBBBBBBBBBBB   ", "    BBBBBBBBBBBB    ",
            "    BBBBBBBBBBB     ", "    BB  BBB         ", "    DD  DDD         ", "                    "],
        ["owl"] = [
            "                    ", "     B        B     ", "     BBBBBBBBBB     ", "    BBBBBBBBBBBB    ",
            "   BBBBBBBBBBBBBB   ", "   BBCCCCBBCCCCBB   ", "   BBCECCBBCECCBB   ", "   BBCCCCBBCCCCBB   ",
            "   BBBBBBNNBBBBBB   ", "   DBBBBBBBBBBBBD   ", "   DBBCCBBCCBBBCD   ", "   DDBBCCBBCCBBDD   ",
            "    DDBBBBBBBBDD    ", "     DDDDDDDDDD     ", "      NN  NN        ", "                    "],
        ["beaver"] = [
            "                    ", "                    ", "    BB    BB        ", "   BCBBBBBBCB       ",
            "   BBBBBBBBBB       ", "  BBBBBBBBBBBB      ", "  BBE BBBB EBB      ", "  BBBBCCBBBBBB      ",
            "   CCCCCNCCCC       ", "    CCC C CCB       ", "    BBBBBBBBBBB     ", "   BBBBBBBBBBBB DDD ",
            "   BBBBBBBBBBBBDDDD ", "    BB BBBBBBDDDDDD ", "    DD DDDDD DDDDD  ", "                    "],
        ["cat"] = [
            "                    ", "   B      B         ", "   BB    BB         ", "   BCB  BCB         ",
            "   BBBBBBBB         ", "  BBBBBBBBBB        ", "  BBE BBB EBB   BB  ", "  BBBBBBBBBB   BBBB ",
            "   CCNCCBBB    BB B ", "    CCCBBBB    BB   ", "    BBBBBBBB   BB   ", "    BBBBBBBB  BBB   ",
            "   BBBBBBBBBBBBB    ", "   BB BBBBBBBBB     ", "   DD DDDDDD        ", "                    "],
        ["capybara"] = [
            "                    ", "                    ", "     BB   BB        ", "    BCBBBBBCB       ",
            "   BBBBBBBBBBB      ", " BBBBBBBBBBBBBB     ", "BBBBBBBBE BBBBB     ", "BBNBBBBBBBBBBBBB    ",
            "BBBBBBBBBBBBBBBBBB  ", " CCCCCBBBBBBBBBBBBB ", "   CCCBBBBBBBBBBBBB ", "      BBBBBBBBBBBBB ",
            "      BBBBBBBBBBBBB ", "      BB  BB   BB   ", "      DD  DD   DD   ", "                    "],
    };
    private static readonly IReadOnlyDictionary<string, string[]> MiniArt = new Dictionary<string, string[]>
    {
        ["egg"] = ["            ", "     CC     ", "    LCCQ    ", "   LCCCQQ   ", "   CCCCQQ   ", "    CQQQ    "],
        ["ox"] = ["            ", " C      C   ", "  BBBBBB    ", "  EBBBBE    ", "  CCNNCCBB  ", "   D  D DD  "],
        ["fox"] = ["            ", " B    B     ", " BBBBBB     ", " BEBBEB  CC ", "  CCNBBBBCC ", "   D  D     "],
        ["owl"] = ["            ", "  B    B    ", "  CCCCCC    ", "  CECCEC    ", "  BBBNBB    ", "   N  N     "],
        ["beaver"] = ["            ", "  B   B     ", "  BBBBBB    ", "  EBBBEB    ", "  CCNCCC DD ", "   D  D DDD "],
        ["cat"] = ["            ", " B    B     ", " BBBBBB     ", " BEBBEB  B  ", " CCNCCBBBB  ", "  D  D      "],
        ["capybara"] = ["            ", "   B  B     ", "  BBBBBBB   ", " BBE BBBBB  ", " NNBBBBBBBB ", "   D  D  D  "],
    };

    /// <summary>Small original sprites and mischievous poses superimposed on the conversation.</summary>
    public static IReadOnlyList<IReadOnlyList<PixelCell>> RenderMini(CompanionPetSnapshot state,
        string animation, long frame, bool facingLeft)
    {
        var art = MiniArt.GetValueOrDefault(state.Species ?? "egg") ?? MiniArt["egg"];
        var pixels = new char[6, 12];
        var lift = animation is "hop" or "dash" or "weave" && frame % 2 == 1 ? 1 : 0;
        var rolling = animation == "roll" && frame % 4 is 1 or 2;
        var crouch = animation == "pounce" && frame % 4 == 0;
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < 12; x++)
        {
            var sourceY = y + lift;
            var sourceX = facingLeft ? x : 11 - x;
            if (rolling) { sourceY = 5 - y; sourceX = 11 - sourceX; }
            if (crouch) sourceY = y < 3 ? 6 : 1 + (y - 3) * 2;
            if (animation == "dance") sourceX += frame % 2 == 0 ? -1 : 1;
            var pixel = sourceY < 6 && sourceX is >= 0 and < 12 ? art[sourceY][sourceX] : ' ';
            if (pixel == 'E' && animation == "peek" && frame % 4 < 2) pixel = 'C';
            if (animation == "hide" && y >= (frame % 4 < 2 ? 3 : 4)) pixel = ' ';
            if (pixel is 'D' or 'N' && sourceY == 5 && frame % 2 == 1 && animation is "run" or "dash" or "dance")
                pixel = ' ';
            pixels[y, x] = pixel;
        }
        return Cells(pixels, Palette(state.Species, state.Variant.StartsWith("cool", StringComparison.Ordinal)), "#061822");
    }
    public static IReadOnlyList<IReadOnlyList<PixelCell>> Render(CompanionPetSnapshot state, string animation, long frame)
    {
        if (!Art.TryGetValue(state.Species ?? "egg", out var art)) art = Art["egg"];
        var pixels = new char[16, 20];
        var phase = (int)(Math.Max(0, frame) % 16);
        var blink = animation == "sleep" || animation == "idle" && phase == 0 || animation == "working" && phase % 4 == 1;
        var lift = animation == "success" && phase % 2 == 1 ? -1 : animation == "working" && phase % 4 == 2
            || state.Species is null && animation == "idle" && phase % 8 == 2 ? 1 : 0;
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 20; x++)
        {
            var source = y - lift;
            var pixel = source >= 0 && source < 16 && x < art[source].Length ? art[source][x] : ' ';
            if (pixel == 'E' && blink) pixel = 'D';
            if (pixel == 'E' && animation == "pet") pixel = 'C';
            if (pixel == 'B' && state.Variant.EndsWith("1", StringComparison.Ordinal) && x is 8 or 9 && y is 10 or 11) pixel = 'S';
            if (pixel == 'B' && state.Variant.EndsWith("2", StringComparison.Ordinal) && (x + y) % 11 == 0) pixel = 'S';
            pixels[y, x] = pixel;
        }
        // Two clear lobes and a tapered point; float upward without shaking the shell sideways.
        if (animation == "pet") Stamp(pixels, 15, 2 - phase / 2 % 3, [" H H ", "HHHHH", "HHHHH", " HHH ", "  H  "]);
        if (animation == "sleep") Stamp(pixels, 16, 0, ["CCC", " C ", "CCC"]);
        if (animation == "waiting") Stamp(pixels, 17, 0, ["C", "C", " ", "C"]);
        if (animation == "failure") Stamp(pixels, 17, 0, ["NNN", "  N", " N ", "   ", " N "]);
        if (state.Stage == "legendary") Stamp(pixels, 6, 0, ["N N N", "NNNNN"]);
        var palette = Palette(state.Species, state.Variant.StartsWith("cool", StringComparison.Ordinal));
        return Cells(pixels, palette, Background);
    }
    private static IReadOnlyList<IReadOnlyList<PixelCell>> Cells(char[,] pixels, Dictionary<char, string> palette, string background)
    {
        var rows = new List<IReadOnlyList<PixelCell>>();
        for (var y = 0; y < pixels.GetLength(0); y += 2)
        {
            var row = new List<PixelCell>();
            for (var x = 0; x < pixels.GetLength(1); x++)
            {
                var top = palette.GetValueOrDefault(pixels[y, x]) ?? background;
                var bottom = palette.GetValueOrDefault(pixels[y + 1, x]) ?? background;
                row.Add(top == bottom ? new(top == background ? " " : "█", top, background)
                    : top == background ? new("▄", bottom, top) : new("▀", top, bottom));
            }
            rows.Add(row);
        }
        return rows;
    }
    private static void Stamp(char[,] pixels, int x, int y, string[] stamp)
    {
        for (var row = 0; row < stamp.Length; row++)
        for (var column = 0; column < stamp[row].Length; column++)
            if (x + column < 20 && y + row < 16 && stamp[row][column] != ' ') pixels[y + row, x + column] = stamp[row][column];
    }
    private static Dictionary<char, string> Palette(string? species, bool cool)
    {
        var body = species switch
        {
            "ox" => cool ? "#91634B" : "#A16B41", "fox" => cool ? "#D98742" : "#EF943F",
            "owl" => cool ? "#9C8E7A" : "#B99565", "beaver" => cool ? "#967452" : "#B08654",
            "cat" => cool ? "#889AAE" : "#C59B72", "capybara" => cool ? "#B7A489" : "#D1B48B", _ => "#D9CBB2",
        };
        return new() { ['B'] = body, ['C'] = species is null ? "#F7F1DF" : "#F2E4C8",
            ['L'] = "#FFFCF1", ['Q'] = "#D5C9AD", ['D'] = "#665043", ['E'] = "#161A20",
            ['N'] = species == "owl" ? "#E6B54E" : "#443B39", ['S'] = "#AD9372", ['H'] = "#EB8F9A" };
    }
}
