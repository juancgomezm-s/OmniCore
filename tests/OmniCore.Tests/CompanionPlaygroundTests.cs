using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class CompanionPlaygroundTests
{
    [Theory]
    [InlineData(14, 3)]
    [InlineData(14, 4)]
    [InlineData(14, 5)]
    [InlineData(48, 10)]
    [InlineData(120, 30)]
    public void Typing_chase_pursues_new_targets_stays_bounded_and_forgets_the_letter(int width, int height)
    {
        var chase = new CompanionTypingChase();
        chase.Follow("é", width);
        for (var frame = 0; frame < 11; frame++)
        {
            chase.Advance(0, width);
            var pose = chase.Pose(0, width, height, frame);
            Assert.True(pose.Outside); Assert.InRange(pose.X, 0, width - 14);
            Assert.InRange(pose.Y, 0, Math.Max(0, height - CompanionPetPlayground.SpriteHeight));
        }
        var previous = chase.Pose(0, width, height, 11).X;
        chase.Follow("🐾", 0); // New typing extends the chase without teleporting the sprite.
        Assert.Equal(previous, chase.Pose(width - 2, width, height, 11).X);
        for (var i = 0; i < 12; i++) chase.Advance(width - 2, width);
        Assert.False(chase.Active); Assert.Null(chase.Glyph);
        chase.Follow("a", 0); chase.Clear();
        Assert.False(chase.Active); Assert.Null(chase.Glyph);
    }

    [Theory]
    [InlineData(14, 3)]
    [InlineData(14, 4)]
    [InlineData(14, 5)]
    [InlineData(40, 16)]
    [InlineData(120, 32)]
    public void Local_outings_stay_in_bounds_visit_both_edges_and_return_home(int width, int height)
    {
        var playground = new CompanionPetPlayground();
        var poses = new List<CompanionPlayPose>();
        for (var i = 0; i < 120; i++) { poses.Add(playground.Pose(width, height)); playground.Advance(); }
        var outside = poses.Where(p => p.Outside).ToArray();
        Assert.NotEmpty(outside);
        Assert.All(outside, pose => Assert.InRange(pose.X, 0, width - CompanionPetPlayground.SpriteWidth - 2));
        Assert.All(outside, pose => Assert.InRange(pose.Y, 0, Math.Max(0, height - CompanionPetPlayground.SpriteHeight)));
        if (height > CompanionPetPlayground.SpriteHeight) Assert.True(outside.Select(pose => pose.Y).Distinct().Count() > 1);
        Assert.Contains(outside, pose => pose.X == 0);
        Assert.Contains(outside, pose => pose.X == Math.Max(0, width - CompanionPetPlayground.CanvasWidth));
        Assert.Equal(new[] { "dance", "dash", "hide", "hop", "peek", "pounce", "roll", "run", "weave" },
            outside.Select(p => p.Animation).Distinct().Order().ToArray());
        Assert.False(poses[0].Outside); Assert.False(poses[^1].Outside);
        playground.Play(); Assert.True(playground.Pose(width).Outside);
        playground.Home(); playground.Advance();
        Assert.Equal(0, playground.Frame); Assert.False(playground.Pose(width).Outside);
    }

    [Fact]
    public void Roaming_silhouettes_paint_five_rows_while_panel_sprites_keep_their_size()
    {
        var pet = new CompanionPetSnapshot(true, 0, "Omni", null, "egg.v1", "warm.spots-0", 0, 0, 70,
            "egg", 0, 20, "fixture", new());
        foreach (var (species, guest) in MiniDesigns())
        {
            var snapshot = pet with { Species = species, GuestId = guest };
            var roaming = CompanionSpriteCatalog.RenderMini(snapshot, "run", 0, true);
            Assert.Equal(5, roaming.Count);
            Assert.All(roaming, row => Assert.Contains(row, cell => !string.IsNullOrWhiteSpace(cell.Glyph)));
            Assert.Equal(8, CompanionSpriteCatalog.Render(snapshot, "idle", 0).Count);
        }
    }

    [Fact]
    public void All_nine_small_sprites_render_both_directions_and_playful_poses_without_changing_state()
    {
        var pet = new CompanionPetSnapshot(true, 0, "Omni", null, "egg.v1", "warm.spots-0", 0, 0, 70,
            "egg", 0, 20, "fixture", new());
        foreach (var (species, guest) in MiniDesigns())
        foreach (var animation in new[] { "run", "peek", "weave", "hop", "roll", "pounce", "dance", "dash", "hide" })
        foreach (var facingLeft in new[] { true, false })
        for (var frame = 0; frame < 8; frame++)
        {
            var snapshot = pet with { Species = species, GuestId = guest };
            var pixels = CompanionSpriteCatalog.RenderMini(snapshot, animation, frame, facingLeft);
            Assert.Equal(CompanionPetPlayground.SpriteHeight, pixels.Count);
            Assert.All(pixels, row => Assert.Equal(CompanionPetPlayground.SpriteWidth, row.Count));
            Assert.Contains(pixels.SelectMany(row => row), cell => cell.Glyph != " ");
            Assert.Equal(0, snapshot.Xp);
        }
    }

    private static IEnumerable<(string? Species, string? Guest)> MiniDesigns()
    {
        foreach (var species in new string?[] { null, "ox", "fox", "owl", "beaver", "cat", "capybara" })
            yield return (species, null);
        yield return ("fox", "claude-crab");
        yield return ("fox", "codex-robot");
    }

    [Fact]
    public void Edge_hiding_keeps_the_complete_silhouette_instead_of_amputating_the_body()
    {
        var pet = new CompanionPetSnapshot(true, 0, "Omni", null, "egg.v1", "warm.spots-0", 0, 0, 70,
            "egg", 0, 20, "fixture", new());
        foreach (var (species, guest) in MiniDesigns())
        foreach (var left in new[] { true, false })
        {
            var state = pet with { Species = species, GuestId = guest };
            var silhouette = CompanionSpriteCatalog.RenderMini(state, "idle", 0, left)
                .SelectMany(row => row).Select(cell => !string.IsNullOrWhiteSpace(cell.Glyph)).ToArray();
            for (var frame = 0; frame < 4; frame++)
                Assert.Equal(silhouette, CompanionSpriteCatalog.RenderMini(state, "hide", frame, left)
                    .SelectMany(row => row).Select(cell => !string.IsNullOrWhiteSpace(cell.Glyph)).ToArray());
        }
    }

    [Fact]
    public void Mischief_weaves_between_rows_reverses_direction_and_varies_across_outings()
    {
        var playground = new CompanionPetPlayground();
        var outings = new List<CompanionPlayPose>[3];
        for (var cycle = 0; cycle < outings.Length; cycle++)
        {
            outings[cycle] = [];
            for (var step = 0; step < 120; step++)
            {
                var pose = playground.Pose(100, 30);
                if (pose.Outside) outings[cycle].Add(pose);
                playground.Advance();
            }
        }
        var weave = outings[0].Where(p => p.Animation == "weave").ToArray();
        Assert.True(weave.Select(p => p.Y).Distinct().Count() >= 3);
        Assert.True(weave.Zip(weave.Skip(1)).Count(pair => pair.First.FacingLeft != pair.Second.FacingLeft) >= 3);
        Assert.NotEqual(outings[0].Select(p => p.Y).ToArray(), outings[1].Select(p => p.Y).ToArray());
        Assert.NotEqual(outings[1].Select(p => p.Y).ToArray(), outings[2].Select(p => p.Y).ToArray());
        var pet = new CompanionPetSnapshot(true, 0, "Omni", "fox", "fox.v1", "warm.spots-0", 0, 1, 70,
            "baby", 20, 20, "fixture", new());
        var run = CompanionSpriteCatalog.RenderMini(pet, "run", 0, true);
        foreach (var animation in new[] { "roll", "pounce", "dance", "hide" })
            Assert.NotEqual(Flatten(run), Flatten(CompanionSpriteCatalog.RenderMini(pet, animation, animation == "roll" ? 1 : 4, true)));
        static string Flatten(IReadOnlyList<IReadOnlyList<PixelCell>> pixels) => string.Join("", pixels.SelectMany(row => row));
    }
}
