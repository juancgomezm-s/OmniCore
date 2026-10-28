using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed partial class CompanionPetTests
{
    private static SessionId GuestSession(int index) => SessionId.Parse($"00000000-0000-0000-0000-{index:000000000000}");

    [Fact]
    public void Guest_lottery_is_rare_and_model_specific_without_switching_existing_visitors()
    {
        var at = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        var regular = new List<string?>(); var claude = new List<string?>(); var gpt = new List<string?>();
        for (var i = 0; i < 10000; i++)
        {
            var session = GuestSession(i);
            var baseline = CompanionGuestPolicy.Select(session, at);
            var withClaude = CompanionGuestPolicy.Select(session, at, CompanionGuestPolicy.Claude);
            var withGpt = CompanionGuestPolicy.Select(session, at, CompanionGuestPolicy.Gpt);
            var both = CompanionGuestPolicy.Select(session, at, 3);
            regular.Add(baseline); claude.Add(withClaude); gpt.Add(withGpt);
            if (baseline is not null) { Assert.Equal(baseline, withClaude); Assert.Equal(baseline, withGpt); }
            if (withClaude is not null) Assert.Equal(withClaude, both);
            if (withGpt is not null) Assert.Equal(withGpt, both);
            Assert.Equal(baseline, CompanionGuestPolicy.Select(session, at));
        }
        Assert.InRange(regular.Count(id => id is not null), 400, 600);
        Assert.InRange(claude.Count(id => id is not null), 1300, 1700);
        Assert.InRange(gpt.Count(id => id is not null), 1300, 1700);
        Assert.Equal(regular.Count(id => id == "codex-robot"), claude.Count(id => id == "codex-robot"));
        Assert.Equal(regular.Count(id => id == "claude-crab"), gpt.Count(id => id == "claude-crab"));
        Assert.Null(CompanionGuestPolicy.Select(null, at, 3));
    }

    [Theory]
    [InlineData("claude-crab")]
    [InlineData("codex-robot")]
    public void Guests_require_a_hatched_pet_and_preserve_identity_progress_and_resume(string guest)
    {
        using var fx = new Fixture(); fx.Discover();
        var session = Enumerable.Range(0, 10000).Select(GuestSession)
            .First(id => CompanionGuestPolicy.Select(id, fx.Now) == guest);
        Assert.Null(fx.Pet.Status(session: session).GuestId);
        fx.Hatch(); var original = fx.Pet.Status();
        var visiting = fx.Pet.Status(session: session);
        Assert.Equal(guest, visiting.GuestId); Assert.Equal(guest + ".v1", visiting.AppearanceId);
        Assert.Equal(original, visiting with { GuestId = null, AppearanceId = original.AppearanceId });
        Assert.Equal(visiting, CompanionPetProtocol.Decode(CompanionPetProtocol.Encode(visiting)));
        for (var poll = 0; poll < 20; poll++) Assert.Equal(visiting, fx.Pet.Status(session: session));
        Assert.Equal(visiting, new CompanionPetService(fx.Root).Status(session: session));
        var next = Enumerable.Range(0, 10000).Select(GuestSession).First(id => CompanionGuestPolicy.Select(id, fx.Now) is null);
        Assert.Equal(original, fx.Pet.Status(session: next));
        Assert.Equal(original, fx.Pet.Status());
        Assert.Contains("De visita", CompanionPetPresentation.Summary(visiting));
        Assert.Equal(guest, CompanionPetPresentation.VisualId(visiting));
        Assert.DoesNotContain(guest, CompanionPetProtocol.Species);
        Assert.Equal(original.Species, CompanionPetPresentation.VisualId(visiting with { GuestId = "unknown" }));
    }

    [Theory]
    [InlineData("claude-sonnet-4-6", 1, "claude-crab")]
    [InlineData("anthropic/claude-opus-4-6", 1, "claude-crab")]
    [InlineData("gpt-5", 2, "codex-robot")]
    [InlineData("openai/gpt-5-mini", 2, "codex-robot")]
    public void Confirmed_root_conversation_model_use_boosts_only_matching_guest_and_survives_replay(string model, int family, string guest)
    {
        using var fx = new Fixture(); fx.Hatch(); var original = fx.Pet.Status();
        var session = Enumerable.Range(0, 10000).Select(GuestSession).First(id => CompanionGuestPolicy.Select(id, fx.Now) is null
            && CompanionGuestPolicy.Select(id, fx.Now, family) == guest);
        var task = TaskId.New(); var lane = LaneId.New(); var turn = TurnId.New();
        fx.Add(new RunCreated(fx.Run, session, "Conversar", RunMode.Act, ExecutionStrategy.Direct,
            FailurePolicy.FailRun, new(null, null, null, null), task, fx.Now), session);
        fx.Add(new LaneCreated(lane, task, ProfileId.New()), session);
        fx.Add(new TurnStarted(turn, lane), session);
        fx.Add(new ModelStepStarted(turn, 0, model, 8192, "Discovered", null, null, null), session);
        fx.Sync(session); Assert.Null(fx.Pet.Status(session: session).GuestId); // Selection/start alone is insufficient.
        fx.Add(new ModelStepCompleted(turn, 0, new(12, 4, 0, 0, 0), StopReason.EndTurn, null, "2026-10-22", null), session);
        fx.Sync(session);
        var visiting = fx.Pet.Status(session: session); Assert.Equal(guest, visiting.GuestId);
        Assert.Equal(original.Xp, visiting.Xp); Assert.Equal(original.Mood, visiting.Mood); Assert.Equal(original.Species, visiting.Species);
        Assert.Equal(visiting, new CompanionPetService(fx.Root).Status(session: session));
        fx.Sync(session); Assert.Equal(visiting, fx.Pet.Status(session: session));
        fx.Add(new ModelStepCompleted(turn, 0, new(12, 4, 0, 0, 0), StopReason.EndTurn, null, "2026-10-22", null), session);
        fx.Sync(session); Assert.Equal(visiting, fx.Pet.Status(session: session));
        Assert.Null(fx.Pet.Status().GuestId);
    }

    [Fact]
    public void Model_mentions_unconfirmed_calls_and_worker_models_do_not_boost_guest_odds()
    {
        using var fx = new Fixture(); fx.Hatch(); var original = fx.Pet.Status();
        var observed = -1;
        fx.Pet.GuestSelector = (_, _, models) => { observed = models; return null; };
        var root = TaskId.New(); var worker = TaskId.New(); var lane = LaneId.New(); var turn = TurnId.New();
        fx.Add(new RunCreated(fx.Run, fx.Session, "Conversar", RunMode.Act, ExecutionStrategy.Direct,
            FailurePolicy.FailRun, new(null, null, null, null), root, fx.Now));
        fx.Message("Claude y GPT aparecen en este texto de prueba");
        fx.Add(new LaneCreated(lane, worker, ProfileId.New())); fx.Add(new TurnStarted(turn, lane));
        fx.Add(new ModelStepStarted(turn, 0, "claude-sonnet", 8192, "Discovered", null, null, null));
        fx.Add(new ModelStepCompleted(turn, 0, new(12, 4, 0, 0, 0), StopReason.EndTurn, null, "2026-10-22", null));
        fx.Sync(); fx.Pet.Status(session: fx.Session); Assert.Equal(0, observed);
        Assert.Equal(original.Revision, fx.Pet.Status().Revision);
        foreach (var model in new[] { "qwen3", "claudeish-model", "not-gpt", "gptish", "claude/other-model" })
            Assert.Equal(0, CompanionGuestPolicy.ModelFamily(model));
    }

    [Theory]
    [InlineData("claude-crab")]
    [InlineData("codex-robot")]
    public void Guest_unicode_and_roaming_sprites_have_distinct_shapes_and_animate(string guest)
    {
        var regular = new CompanionPetSnapshot(true, 1, "Omni", "ox", "ox.v1", "cool.spots-2", 100, 3, 70,
            "young", 20, 20, "fixture", new());
        var visitor = regular with { GuestId = guest, AppearanceId = guest + ".v1" };
        static string Flat(IReadOnlyList<IReadOnlyList<PixelCell>> cells) => string.Join("", cells.SelectMany(row => row));
        var idle = CompanionSpriteCatalog.Render(visitor, "idle", 1);
        Assert.NotEqual(Flat(CompanionSpriteCatalog.Render(regular, "idle", 1)), Flat(idle));
        Assert.NotEqual(Flat(idle), Flat(CompanionSpriteCatalog.Render(visitor, "pet", 1)));
        foreach (var pose in new[] { "run", "peek", "weave", "hop", "roll", "pounce", "dance", "dash", "hide" })
        foreach (var left in new[] { true, false })
        for (var frame = 0; frame < 8; frame++)
        {
            var mini = CompanionSpriteCatalog.RenderMini(visitor, pose, frame, left);
            Assert.Equal(CompanionPetPlayground.SpriteHeight, mini.Count); Assert.All(mini, row => Assert.Equal(12, row.Count));
            Assert.Contains(mini.SelectMany(row => row), pixel => pixel.Glyph != " ");
        }
        Assert.Equal(regular.Species, visitor.Species); Assert.Equal(regular.Xp, visitor.Xp);
    }
}
