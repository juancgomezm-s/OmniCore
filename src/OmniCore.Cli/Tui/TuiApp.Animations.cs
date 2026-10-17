using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void RenderAnimationPreview() => _animationPreview?.Render(_previewEffect, _activityPhase,
        _animationPreferences.Enabled && !_animationPreferences.ReducedMotion,
        Environment.GetEnvironmentVariable("NO_COLOR") is not null, ascii: _animationPreferences.Ascii,
        label: AnimationCatalog.IsText(_previewEffect) ? _localization.Resolve("animation.sample") : null);

    private bool SaveAnimationPreferences(AnimationPreferences preferences)
    {
        if (!_animationStore.Save(preferences)) return false;
        _animationPreferences = preferences;
        _activityTicks = 0;
        RenderActivity();
        RenderAnimationPreview();
        return true;
    }

    private void HandleAnimationCommand(string argument)
    {
        if (argument is "" or "showcase") { ShowAnimationSettings(); return; }
        AnimationPreferences? next = argument switch
        {
            "on" => _animationPreferences with { Enabled = true },
            "off" => _animationPreferences with { Enabled = false },
            "reduced" => _animationPreferences with { ReducedMotion = true },
            "full" => _animationPreferences with { ReducedMotion = false },
            "fast" => _animationPreferences with { IntervalMilliseconds = 100 },
            "normal" => _animationPreferences with { IntervalMilliseconds = 200 },
            "slow" => _animationPreferences with { IntervalMilliseconds = 300 },
            "ascii" => _animationPreferences with { Ascii = true },
            "unicode" => _animationPreferences with { Ascii = false },
            _ => null,
        };
        if (next is null)
        {
            var parts = argument.Split(':');
            var effect = parts[^1];
            if (AnimationCatalog.Names.Contains(effect))
                next = parts.Length == 1 ? _animationPreferences with
                { Waiting = effect, Reasoning = effect, Answer = effect, Tools = effect }
                : parts.Length == 2 ? parts[0] switch
                {
                    "waiting" => _animationPreferences with { Waiting = effect },
                    "thinking" => _animationPreferences with { Reasoning = effect },
                    "working" => _animationPreferences with { Answer = effect },
                    "tool" => _animationPreferences with { Tools = effect },
                    _ => null,
                } : null;
        }
        if (next is null)
        {
            ShowMessage(_localization.Resolve("shell.use_animation_showcase_on_off_reduced_full_fast_normal_slow_neffects")
                + string.Join(", ", AnimationCatalog.Names));
            return;
        }
        if (!SaveAnimationPreferences(next))
            ShowMessage(_localization.Resolve("shell.could_not_save_animation_preferences"));
    }

    private void ShowAnimationSettings(string? previewEffect = null)
    {
        if (_window is null || _overlay is not null) return;
        _previewEffect = previewEffect ?? _animationPreferences.Waiting;
        if (_window.Frame.Height < 18)
        {
            ShowMessage(_localization.Resolve("shell.enlarge_the_terminal_for_the_preview_n_animation_on_off_reduced_full_f"));
            return;
        }
        var frame = OverlayFrame(_localization.Resolve("shell.activity_and_animations"), Math.Min(19, _window.Frame.Height - 2));
        var notice = new Label { Id = "animation-notice", X = 2, Y = 1, Width = Dim.Fill(2), Height = 1 };
        if (_animationStore.Notice is not null)
            notice.Text = _localization.Resolve("shell.invalid_settings_using_safe_defaults");
        _animationPreview = new ActivityView { Id = "animation-preview", X = 2, Y = 2, Width = 12, Height = 1 };
        var description = new Label { X = 16, Y = 2, Width = Dim.Fill(2), Height = 1 };
        var effects = new List<(Button Button, ChatActivityPhase Phase, string Label)>();
        void AddEffect(string id, int y, ChatActivityPhase phase, string label)
        {
            var button = new Button { Id = id, X = 2, Y = y, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
            button.Accepted += (_, _) =>
            {
                var returnToCategory = _preferencesReturnToCategory;
                CloseOverlay(); ShowAnimationPicker(phase, returnToCategory);
            };
            effects.Add((button, phase, label));
            frame.Add(button);
        }
        var enabled = new Button { Id = "animation-enabled", X = 2, Y = 9, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        var reduced = new Button { Id = "animation-reduced", X = 2, Y = 10, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        var speed = new Button { Id = "animation-speed", X = 2, Y = 11, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        var ascii = new Button { Id = "animation-ascii", X = 2, Y = 12, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        AddEffect("animation-waiting", 4, ChatActivityPhase.WaitingForResponse, _localization.Resolve("shell.waiting"));
        AddEffect("animation-thinking", 5, ChatActivityPhase.Reasoning, _localization.Resolve("shell.reported_reasoning"));
        AddEffect("animation-working", 6, ChatActivityPhase.AnswerText, _localization.Resolve("shell.answer"));
        AddEffect("animation-tool", 7, ChatActivityPhase.Tools, _localization.Resolve("shell.tools"));
        enabled.Accepted += (_, _) => { Save(_animationPreferences with { Enabled = !_animationPreferences.Enabled }); Update(); };
        reduced.Accepted += (_, _) => { Save(_animationPreferences with { ReducedMotion = !_animationPreferences.ReducedMotion }); Update(); };
        speed.Accepted += (_, _) => { Save(_animationPreferences with { IntervalMilliseconds = _animationPreferences.IntervalMilliseconds == 300 ? 100 : _animationPreferences.IntervalMilliseconds + 100 }); Update(); };
        ascii.Accepted += (_, _) => { Save(_animationPreferences with { Ascii = !_animationPreferences.Ascii }); Update(); };
        void Save(AnimationPreferences preferences)
        {
            notice.Text = SaveAnimationPreferences(preferences) ? "" : _localization.Resolve("shell.could_not_save_unchanged");
        }
        void Update()
        {
            foreach (var item in effects) item.Button.Text = item.Label + " · " + AnimationName(_animationPreferences.Effect(item.Phase)) + "…";
            enabled.Text = _localization.Resolve("shell.animations") + " · " + _localization.Resolve(_animationPreferences.Enabled ? "shell.state.on" : "shell.state.off");
            reduced.Text = _localization.Resolve("shell.reduced_motion") + " · " + _localization.Resolve(_animationPreferences.ReducedMotion ? "shell.state.on" : "shell.state.off");
            speed.Text = _localization.Resolve("shell.interval") + " · " + _animationPreferences.IntervalMilliseconds + " ms";
            ascii.Text = _localization.Resolve("shell.characters") + " · " + (_animationPreferences.Ascii ? "ASCII *" : "Unicode ✦");
            description.Text = AnimationName(_previewEffect) + _localization.Resolve("shell.preview");
            RenderAnimationPreview();
        }
        var help = new Label { Id = "omni-help", X = 2, Y = 13, Width = Dim.Fill(2), Height = 2,
            Text = _localization.Resolve("shell.enter_changes_off_keeps_status_without_motion") };
        var close = new Button { Id = "animation-back", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Text = Pref("back") };
        close.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("general", categoryPage: true); };
        ApplyTheme(_window, dimmed: true);
        if (_conversation is not null) { _conversation.Dimmed = true; _conversation.SetNeedsDraw(); }
        frame.Add(notice, _animationPreview, description, enabled, reduced, speed, ascii, help, close);
        if (_window.Frame.Height < 21)
        {
            var panel = new View { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3), CanFocus = true };
            foreach (var view in frame.SubViews.Where(view => view != close && view.Id != "omni-menu-title").ToArray())
            { frame.Remove(view); panel.Add(view); }
            ConfigurePreferenceScroll(panel, 15); frame.Add(panel);
        }
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        Update();
        effects[0].Button.SetFocus();
    }
}
