using System.Collections.ObjectModel;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private ChatActivityPhase _animationPickerPhase;
    private bool _animationPickerReturnToCategory;
    private string AnimationName(string effect) => _localization.Resolve("animation.effect." + effect);

    private void ReturnFromAnimationPicker()
    {
        var returnToCategory = _animationPickerReturnToCategory;
        var effect = _animationPreferences.Effect(_animationPickerPhase);
        CloseOverlay(); ShowAnimationSettings(effect); _preferencesReturnToCategory = returnToCategory;
    }

    private void ShowAnimationPicker(ChatActivityPhase phase, bool returnToCategory)
    {
        if (_window is null) return;
        _animationPickerPhase = phase; _animationPickerReturnToCategory = returnToCategory;
        var frame = OverlayFrame(_localization.Resolve("animation.choose"), _window.Frame.Height - 3);
        frame.Id = "animation-picker"; frame.Width = Dim.Fill(4); frame.Height = Dim.Fill(2);
        var search = new TextField { Id = "animation-search", X = 4, Y = 2, Width = Dim.Fill(4), Height = 1 };
        var list = new ListView { Id = "animation-list", X = 4, Y = 4, Width = Dim.Percent(46), Height = Dim.Fill(5), ShowMarks = false };
        var heading = new Label { Id = "animation-choice-title", X = Pos.Percent(55), Y = 4, Width = Dim.Fill(4), Height = 2 };
        _animationPreview = new ActivityView { Id = "animation-preview", X = Pos.Percent(55), Y = 7, Width = Dim.Fill(4), Height = 5 };
        var status = MenuIndent.Help("", Pos.AnchorEnd(4), 1, "animation-choice-status");
        var filtered = AnimationCatalog.Names.ToArray();
        void Preview()
        {
            if (list.SelectedItem is not int index || index < 0 || index >= filtered.Length) return;
            _previewEffect = filtered[index]; heading.Text = AnimationName(_previewEffect);
            RenderAnimationPreview();
        }
        void Filter()
        {
            filtered = AnimationCatalog.Names.Where(effect => AnimationName(effect).Contains(search.Text,
                StringComparison.CurrentCultureIgnoreCase) || effect.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SetSource(new ObservableCollection<string>(filtered.Select(AnimationName)));
            var current = Array.IndexOf(filtered, _animationPreferences.Effect(phase));
            if (filtered.Length > 0) list.SelectedItem = Math.Max(0, current);
            status.Text = filtered.Length == 0 ? Pref("no_data") : "";
            if (filtered.Length == 0) { heading.Text = ""; _animationPreview.Visible = false; }
            else { _animationPreview.Visible = true; Preview(); }
        }
        list.ValueChanged += (_, _) => Preview(); search.TextChanged += (_, _) => Filter();
        void Select()
        {
            if (list.SelectedItem is not int index || index < 0 || index >= filtered.Length) return;
            var next = filtered[index];
            var preferences = phase switch
            {
                ChatActivityPhase.WaitingForResponse => _animationPreferences with { Waiting = next },
                ChatActivityPhase.Reasoning => _animationPreferences with { Reasoning = next },
                ChatActivityPhase.AnswerText => _animationPreferences with { Answer = next },
                _ => _animationPreferences with { Tools = next },
            };
            if (!SaveAnimationPreferences(preferences))
            { status.Text = _localization.Resolve("shell.could_not_save_unchanged"); return; }
            ReturnFromAnimationPicker();
        }
        list.Accepted += (_, args) => { args.Handled = true; Select(); };
        var choose = new Button { Id = "animation-choose", X = 4, Y = Pos.AnchorEnd(2), Text = _localization.Resolve("animation.apply") };
        var cancel = new Button { Id = "animation-choice-cancel", X = Pos.AnchorEnd(16), Y = Pos.AnchorEnd(2), Text = Pref("cancel") };
        choose.Accepted += (_, _) => Select(); cancel.Accepted += (_, _) => ReturnFromAnimationPicker();
        frame.KeyDown += (_, key) =>
        {
            if (key.KeyCode != (KeyCode.F | KeyCode.CtrlMask)) return;
            search.SetFocus(); key.Handled = true;
        };
        frame.Add(new Label { X = 4, Y = 1, Text = Pref("search"), CanFocus = false }, search, list, heading,
            _animationPreview, status, choose, cancel);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); Filter(); list.SetFocus();
    }
}
