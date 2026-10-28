using OmniCore.Client;
using OmniCore.Cli;

namespace OmniCore.Tests;

public sealed class CompanionVisualTests
{
    [Fact]
    public void Framework_reader_accepts_recorded_xterm_replies_without_consuming_user_typing()
    {
        // Exact replies captured from the actual xterm.js terminal. Check the pinned
        // framework's detector predicate and shared input reader, including interleaved typing.
        var parserType = typeof(Terminal.Gui.Views.ImageView).Assembly.GetType("Terminal.Gui.Drivers.AnsiResponseParser")!;
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var clockType = parserType.Assembly.GetType("Terminal.Gui.Time.SystemTimeProvider")!;
        var clock = Activator.CreateInstance(clockType, nonPublic: true)!;
        var parser = Activator.CreateInstance(parserType, flags, null, [clock], null)!;
        string? device = null, metrics = null;
        var expect = parserType.GetMethod("ExpectResponse", flags)!;
        expect.Invoke(parser, ["c", "?62", (Action<string?>)(value => device = value), (Action)(() => { }), false]);
        expect.Invoke(parser, ["t", "6", (Action<string?>)(value => metrics = value), (Action)(() => { }), false]);
        var typing = parserType.GetMethod("ProcessInput", flags)!.Invoke(parser, ["abc\u001b[?62;4;9;22cdef\u001b[6;17;8tghi"]);
        Assert.Equal("abcdefghi", typing); Assert.Equal("\u001b[6;17;8t", metrics);
        var predicate = typeof(Terminal.Gui.Drawing.SixelSupportDetector).GetMethod("ResponseIndicatesSupport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.True((bool)predicate.Invoke(null, [device])!);
    }
    [Theory]
    [InlineData("auto", true, true, true, true, "graphics")]
    [InlineData("auto", true, true, false, true, "unicode")]
    [InlineData("auto", true, false, false, true, "text")]
    [InlineData("graphics", true, true, false, true, "unicode")]
    [InlineData("graphics", false, true, true, true, "text")]
    [InlineData("graphics", true, true, true, false, "unicode")]
    [InlineData("unicode", true, false, true, true, "text")]
    [InlineData("text", true, true, true, true, "text")]
    [InlineData("hidden", true, true, true, true, "hidden")]
    public void Selects_only_verified_and_compatible_visual_modes(string requested, bool interactive, bool unicode, bool sixel, bool color, string expected)
    {
        var policy = new CompanionVisualPolicy();
        var result = policy.Select(requested, true, new(interactive, unicode, sixel, Color: color));
        Assert.Equal(requested, result.Requested); Assert.Equal(expected, result.Effective);
        Assert.Equal(expected == "graphics" ? "sixel" : null, result.Backend);
        Assert.Equal("hidden", policy.Select(requested, false, new(true, true, true)).Effective);
    }
    [Fact]
    public void Failure_is_latched_until_explicit_redetection_and_idle_gestures_keep_long_still_pauses()
    {
        var policy = new CompanionVisualPolicy(); var caps = new CompanionVisualCapabilities(true, true, true);
        policy.FailGraphics();
        for (var tick = 0; tick < 100; tick++) Assert.Equal("unicode", policy.Select("auto", true, caps).Effective);
        policy.Redetect(); Assert.Equal("graphics", policy.Select("auto", true, caps).Effective);
        var idle = Enumerable.Range(0, 64).Select(tick => CompanionVisualPolicy.SpriteFrame("idle", tick, false)).ToArray();
        Assert.All(idle, frame => Assert.InRange(frame, 0, CompanionVisualPolicy.GraphicFrameCount - 1));
        Assert.True(idle.Count(frame => frame == 0) >= idle.Length * .75);
        Assert.Equal(new[] { 0, 1, 13, 14, 15, 16 }, idle.Distinct().Order().ToArray());
        Assert.True(idle.Zip(idle.Skip(1)).Count(pair => pair.First != pair.Second) < idle.Length / 2);
        Assert.Equal(new[] { 12, 17 }, Enumerable.Range(0, 24)
            .Select(tick => CompanionVisualPolicy.SpriteFrame("sleep", tick, false)).Distinct().Order().ToArray());
        foreach (var pose in new[] { "idle", "working", "waiting", "success", "failure", "pet", "sleep" })
            Assert.Single(Enumerable.Range(0, 64).Select(tick => CompanionVisualPolicy.SpriteFrame(pose, tick, true)).Distinct());
        Assert.Equal("graphics", CompanionVisualPolicy.Normalize("gráfico"));
        Assert.Equal("text", CompanionVisualPolicy.Normalize("texto"));
        Assert.Throws<ArgumentException>(() => CompanionVisualPolicy.Normalize("surprise"));
    }
    [Fact]
    public async Task All_original_atlases_decode_six_states_have_real_detail_and_cache_is_bounded()
    {
        var assets = new CompanionGraphicAssets();
        foreach (var species in new[] { "ox", "fox", "owl", "beaver", "cat", "capybara", "egg", "claude-crab", "codex-robot" })
        {
            var first = assets.Load(species); Assert.Same(first, assets.Load(species));
            var frames = await first;
            Assert.Equal(CompanionVisualPolicy.GraphicFrameCount, frames.Length); Assert.True(assets.Count <= 2);
            foreach (var frame in frames)
            {
                Assert.Equal(128, frame.GetLength(0)); Assert.Equal(128, frame.GetLength(1));
                Assert.True(frame.Cast<Terminal.Gui.Drawing.Color>().Distinct().Count() >= 24);
            }
            Assert.Equal(6, new[] { 0, 2, 4, 6, 8, 10 }.Select(index => string.Join(",", frames[index]
                .Cast<Terminal.Gui.Drawing.Color>().Select(color => $"{color.R:X2}{color.G:X2}{color.B:X2}"))).Distinct().Count());
            Assert.Equal(6, new[] { 0, 1, 13, 14, 15, 16 }.Select(index => string.Join(",", frames[index]
                .Cast<Terminal.Gui.Drawing.Color>().Select(color => $"{color.R:X2}{color.G:X2}{color.B:X2}"))).Distinct().Count());
            Assert.False(frames[12].Cast<Terminal.Gui.Drawing.Color>().SequenceEqual(frames[17].Cast<Terminal.Gui.Drawing.Color>()));
        }
        assets.Clear(); Assert.Equal(0, assets.Count);
        var warm = await assets.Load("ox");
        var cool = await assets.Load("ox", "cool.spots-1", true);
        Assert.NotEqual(warm[0][64, 62], cool[0][64, 62]);
        Assert.Equal(new Terminal.Gui.Drawing.Color(246, 207, 104), cool[0][64, 20]);
    }
}
