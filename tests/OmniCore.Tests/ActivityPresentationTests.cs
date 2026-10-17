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
        Assert.True((new AnimationPreferences() with { Waiting = "pi-pulse" }).IsValid);
        Assert.False((new AnimationPreferences() with { Waiting = "unknown-effect" }).IsValid);
        Assert.False((new AnimationPreferences() with { IntervalMilliseconds = 0 }).IsValid);
        Assert.False((new AnimationPreferences() with { Reasoning = null! }).IsValid);
    }

    [Fact]
    public void All_catalog_effects_have_bounded_native_frames_and_static_accessible_fallbacks()
    {
        Assert.Equal(31, AnimationCatalog.Names.Distinct().Count());
        foreach (var effect in AnimationCatalog.Names)
        foreach (var width in new[] { 0, 1, 11, 80, 1000 })
        foreach (var tick in new[] { -1L, 0, 11, long.MaxValue })
        {
            var rows = AnimationCatalog.RenderFrame(effect, tick, width, 5, "Pensando...", ascii: true);
            Assert.InRange(rows.Count, 1, 5);
            Assert.All(rows, row =>
            {
                Assert.InRange(row.Count, 0, Math.Clamp(width, 0, 80));
                Assert.All(row, cell =>
                {
                    Assert.InRange(cell.Intensity, 0, 5);
                    Assert.All(cell.Glyph, ch => Assert.InRange((int)ch, 32, 126));
                });
            });
            Assert.Equal(rows.SelectMany(row => row), AnimationCatalog.RenderFrame(effect, tick, width, 5, "Pensando...", ascii: true).SelectMany(row => row));
            Assert.Equal(AnimationCatalog.RenderFrame(effect, 0, width, 5, "Pensando...", moving: false).SelectMany(row => row),
                AnimationCatalog.RenderFrame(effect, 99, width, 5, "Pensando...", moving: false).SelectMany(row => row));
        }
    }

    [Theory]
    [InlineData("text-pulse")]
    [InlineData("text-scramble")]
    [InlineData("text-bounce")]
    public void Three_character_pulse_restores_original_graphemes_as_it_advances(string effect)
    {
        const string text = "Pensando...";
        var seen = new HashSet<int>();
        for (var tick = 0; tick < 45; tick++)
        {
            var cells = AnimationCatalog.RenderText(effect, text, tick, 80);
            var changed = cells.Select((cell, index) => (cell, index)).Where(pair => pair.cell.Glyph != text[pair.index].ToString()).ToArray();
            Assert.InRange(changed.Length, 0, 1);
            Assert.InRange(cells.Count(cell => cell.Intensity > 2), 0, 3);
            foreach (var pair in changed) seen.Add(pair.index);
            for (var index = 0; index < cells.Count; index++)
                if (cells[index].Intensity == 2) Assert.Equal(text[index].ToString(), cells[index].Glyph);
        }
        Assert.Equal(text.Length, seen.Count);
        Assert.Equal(text, string.Concat(AnimationCatalog.RenderText(effect, text, 3, 80, moving: false).Select(cell => cell.Glyph)));
        var unicode = AnimationCatalog.RenderText(effect, "e\u0301界", 0, 80, moving: false);
        Assert.Equal(new[] { "e\u0301", "界" }, unicode.Select(cell => cell.Glyph));
    }
}
