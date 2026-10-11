using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ActivityPresentationTests
{
    private static ChatActivityEvent Event(ChatActivityPhase phase, long seq = 1,
        string session = "s", string run = "r", string lane = "root", string turn = "t", bool live = true) =>
        new(session, turn, run, lane, seq, phase, phase == ChatActivityPhase.AnswerText,
            DateTimeOffset.UnixEpoch.AddSeconds(seq), "fixture", live);

    [Theory]
    [InlineData(ChatActivityPhase.WaitingForResponse, "Esperando respuesta", "Waiting for response")]
    [InlineData(ChatActivityPhase.Reasoning, "Razonando", "Reasoning")]
    [InlineData(ChatActivityPhase.AnswerText, "Respondiendo", "Responding")]
    [InlineData(ChatActivityPhase.Tools, "Herramientas", "Tools")]
    [InlineData(ChatActivityPhase.WaitingForApproval, "Requiere tu respuesta", "Input required")]
    public void Reports_only_the_observed_principal_phase(ChatActivityPhase phase, string es, string en)
    {
        var view = ActivityPresentation.From("s", "r", "root", [Event(phase)]);
        Assert.True(view.Visible);
        Assert.Equal(phase, view.Phase);
        Assert.Equal(es, view.Label());
        Assert.Equal(en, view.Label("en"));
        Assert.Equal(phase != ChatActivityPhase.WaitingForApproval, view.Moving);
    }

    [Theory]
    [InlineData(ChatActivityPhase.Completed)]
    [InlineData(ChatActivityPhase.Cancelled)]
    [InlineData(ChatActivityPhase.Failed)]
    public void Terminal_hides_indicator_even_if_local_task_completion_is_still_queued(ChatActivityPhase terminal)
    {
        var view = ActivityPresentation.From("s", "r", "root", [Event(terminal, 2), Event(ChatActivityPhase.Reasoning)], true);
        Assert.False(view.Visible);
        Assert.Null(view.Phase);
    }

    [Fact]
    public void Other_sessions_runs_replay_and_children_do_not_impersonate_principal()
    {
        var view = ActivityPresentation.From("s", "r", "root",
        [Event(ChatActivityPhase.Reasoning, session: "other"), Event(ChatActivityPhase.Reasoning, run: "other"),
            Event(ChatActivityPhase.Reasoning, live: false), Event(ChatActivityPhase.Reasoning, lane: "child", turn: "child-turn")]);
        Assert.Null(view.Phase);
        Assert.Equal(1, view.BackgroundActivities);
        Assert.DoesNotContain("Razonando", view.Label());
    }

    [Fact]
    public void Counts_children_by_lane_without_summing_local_and_observed_work_twice()
    {
        var view = ActivityPresentation.From("s", "r", "root",
        [Event(ChatActivityPhase.Reasoning), Event(ChatActivityPhase.Reasoning, 2, lane: "child", turn: "c1"),
            Event(ChatActivityPhase.AnswerText, 3, lane: "child", turn: "c2"),
            Event(ChatActivityPhase.Completed, 4, lane: "done", turn: "d")], pendingChildren: 1);
        Assert.Equal(ChatActivityPhase.Reasoning, view.Phase);
        Assert.Equal(1, view.BackgroundActivities);
    }

    [Fact]
    public void Unknown_scope_does_not_pick_an_arbitrary_lane_or_invent_reasoning()
    {
        var view = ActivityPresentation.From("s", "r", null, [Event(ChatActivityPhase.Reasoning)], true);
        Assert.Null(view.Phase);
        Assert.Equal("Procesando…", view.Label());
        Assert.False(ActivityPresentation.From("new-session", "r", "root", [Event(ChatActivityPhase.Reasoning)]).Visible);
    }

    [Fact]
    public void Completed_previous_turn_does_not_hide_a_new_observed_turn()
    {
        var view = ActivityPresentation.From("s", "r", "root",
            [Event(ChatActivityPhase.Completed), Event(ChatActivityPhase.WaitingForResponse, 2, turn: "new")]);
        Assert.Equal(ChatActivityPhase.WaitingForResponse, view.Phase);
    }

    [Theory]
    [InlineData("omega-pulse")]
    [InlineData("orbit-dots")]
    [InlineData("block-wave")]
    [InlineData("conveyor")]
    [InlineData("braille-spinner")]
    public void Frames_are_bounded_deterministic_native_and_star_branded(string effect)
    {
        foreach (var width in new[] { -1, 0, 1, 2, 3, 11, 80, 1000 })
        foreach (var frame in new long[] { -1, 0, 1, 20, long.MaxValue })
        {
            var cells = AnimationCatalog.Render(effect, frame, width);
            Assert.Equal(Math.Clamp(width, 0, 80), cells.Count);
            Assert.Equal(cells, AnimationCatalog.Render(effect, frame, width));
            Assert.All(cells, cell => { Assert.InRange(cell.Intensity, 0, 5); Assert.DoesNotContain('\x1b', cell.Glyph); });
            if (width > 0) Assert.Equal("✦", cells[0].Glyph);
        }
        Assert.NotEqual(AnimationCatalog.Render(effect, 0, 11), AnimationCatalog.Render(effect, 5, 11));
        Assert.Equal(AnimationCatalog.Render(effect, 0, 11, false), AnimationCatalog.Render(effect, 20, 11, false));
        Assert.All(AnimationCatalog.Render(effect, 0, 11, ascii: true), cell => Assert.All(cell.Glyph, ch => Assert.InRange((int)ch, 32, 126)));
        Assert.Equal("*", AnimationCatalog.Render(effect, 0, 11, ascii: true)[0].Glyph);
    }

    [Fact]
    public void Braille_spinner_matches_the_ten_frame_loader_cycle_and_keeps_the_rest_blank()
    {
        string[] frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        string[] ascii = ["|", "/", "-", "\\"];
        Assert.Contains("braille-spinner", AnimationCatalog.Names);
        Assert.True((new AnimationPreferences() with { Waiting = "braille-spinner" }).IsValid);
        for (var tick = 0; tick < 120; tick++)
        {
            var cells = AnimationCatalog.Render("braille-spinner", tick, 11);
            Assert.Equal(new AnimationCell(frames[tick % frames.Length], 5), cells[2]);
            Assert.All(cells.Skip(3), cell => Assert.Equal(new AnimationCell(" ", 0), cell));
            Assert.Equal(ascii[tick % ascii.Length], AnimationCatalog.Render("braille-spinner", tick, 11, ascii: true)[2].Glyph);
        }
        Assert.Equal(AnimationCatalog.Render("braille-spinner", 0, 11), AnimationCatalog.Render("braille-spinner", 120, 11));
        Assert.Equal("⠏", AnimationCatalog.Render("braille-spinner", -1, 3)[2].Glyph);
        Assert.All(AnimationCatalog.Render("braille-spinner", 42, 11, moving: false).Skip(1),
            cell => Assert.Equal(new AnimationCell(" ", 0), cell));
    }

    [Fact]
    public void Preferences_accept_only_native_effects_and_bounded_speed()
    {
        Assert.True(new AnimationPreferences().IsValid);
        Assert.False((new AnimationPreferences() with { Waiting = "pi-pulse" }).IsValid);
        Assert.False((new AnimationPreferences() with { IntervalMilliseconds = 0 }).IsValid);
        Assert.False((new AnimationPreferences() with { Reasoning = null! }).IsValid);
    }
}
