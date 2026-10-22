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
            "                    ", "        CCCC        ", "      CCCCCCCC      ", "     CCCCCCCCCC     ",
            "     CCCSSCCCCC     ", "    CCCSSSCCCCCC    ", "    CCCCCCCCCCCC    ", "    CCCCCCCSSCCC    ",
            "    CCCCCCSSSCCC    ", "    CCCCCCCCCCCC    ", "    CCCSSCCCCCCC    ", "     CCSSCCCCCC     ",
            "     CCCCCCCCCC     ", "      CCCCCCCC      ", "        DDDD        ", "                    "],
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
        if (animation == "pet") Stamp(pixels, phase % 2 == 0 ? 15 : 14, 0, ["H H", "HHH", " H "]);
        if (animation == "sleep") Stamp(pixels, 16, 0, ["CCC", " C ", "CCC"]);
        if (animation == "waiting") Stamp(pixels, 17, 0, ["C", "C", " ", "C"]);
        if (animation == "failure") Stamp(pixels, 17, 0, ["NNN", "  N", " N ", "   ", " N "]);
        if (state.Stage == "legendary") Stamp(pixels, 6, 0, ["N N N", "NNNNN"]);
        var palette = Palette(state.Species, state.Variant.StartsWith("cool", StringComparison.Ordinal));
        var rows = new List<IReadOnlyList<PixelCell>>();
        for (var y = 0; y < 16; y += 2)
        {
            var row = new List<PixelCell>();
            for (var x = 0; x < 20; x++)
            {
                var top = palette.GetValueOrDefault(pixels[y, x]) ?? Background;
                var bottom = palette.GetValueOrDefault(pixels[y + 1, x]) ?? Background;
                row.Add(top == bottom ? new(top == Background ? " " : "█", top, Background)
                    : top == Background ? new("▄", bottom, top) : new("▀", top, bottom));
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
        return new() { ['B'] = body, ['C'] = "#F2E4C8", ['D'] = "#665043", ['E'] = "#161A20",
            ['N'] = species == "owl" ? "#E6B54E" : "#443B39", ['S'] = "#AD9372", ['H'] = "#EB8F9A" };
    }
}
