using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly TuiThemeStore _themeStore;
    private TuiTheme _theme;
    private TuiTheme? _themeBeforePreview;
    internal string ThemeId => _theme.Id;

    private void ShowThemePreferences()
    {
        if (_window is null || _overlay is not null) return;
        _themeBeforePreview = _theme;
        var frame = OverlayFrame(_localization.Resolve("shell.theme.title"), _window.Frame.Height - 3);
        frame.Id = "theme-preferences"; frame.Width = Dim.Fill(4); frame.Height = Dim.Fill(2);
        var list = new ListView { Id = "theme-list", X = MenuIndent.Level1, Y = 3,
            Width = Dim.Percent(43), Height = Dim.Fill(6), ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(TuiTheme.All.Select(theme => theme.Name)));
        list.SelectedItem = TuiTheme.All.ToList().FindIndex(theme => theme.Id == _theme.Id);
        var chosen = list.SelectedItem ?? 0;
        var heading = new Label { Id = "theme-preview-heading", X = Pos.Percent(52), Y = 3, Width = Dim.Fill(4), Height = 1 };
        var sample = new StyledTextPresenter { Id = "theme-preview-sample", X = Pos.Percent(52), Y = 6,
            Width = Dim.Fill(4), Height = Dim.Fill(6), CanFocus = false, ScrollBars = false };
        sample.LoadStyled([
            [new(_localization.Resolve("shell.theme.sample_user"), ConversationStyle.UserAccent)],
            [new(_localization.Resolve("shell.theme.sample_reply"), ConversationStyle.Text)],
            [new("", ConversationStyle.Text)],
            [new("C#", ConversationStyle.CodeHeader)],
            [new("var ", ConversationStyle.SyntaxKeyword), new("theme", ConversationStyle.SyntaxVariable), new(" = ", ConversationStyle.SyntaxPunctuation), new("\"OmniCore\";", ConversationStyle.SyntaxString)],
            [new("", ConversationStyle.Text)],
            [new(_localization.Resolve("shell.theme.sample_tool"), ConversationStyle.ToolAccent)],
            [new(_localization.Resolve("shell.theme.sample_note"), ConversationStyle.Muted)],
        ], Math.Max(16, _window.Frame.Width / 2 - 12), Environment.GetEnvironmentVariable("NO_COLOR") is not null);
        void Preview()
        {
            chosen = list.SelectedItem ?? chosen;
            _theme = TuiTheme.All[chosen]; heading.Text = _theme.Name;
            ApplyTheme(_window); _window.SetNeedsDraw();
        }
        list.ValueChanged += (_, _) => Preview();
        list.Accepted += (_, args) => { args.Handled = true; Preview(); };
        list.MouseEvent += (_, mouse) =>
        {
            if (!mouse.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.LeftButtonClicked) || mouse.Position is not { } point) return;
            var index = point.Y + list.Viewport.Y;
            if (index < 0 || index >= TuiTheme.All.Count) return;
            list.SelectedItem = index; Preview(); list.SetFocus(); mouse.Handled = true;
        };
        var status = MenuIndent.Help("", Pos.AnchorEnd(4), 1, "theme-status");
        var save = new Button { Id = "theme-save", X = 4, Y = Pos.AnchorEnd(2), Text = Pref("save") };
        var cancel = new Button { Id = "theme-cancel", X = Pos.AnchorEnd(16), Y = Pos.AnchorEnd(2), Text = Pref("cancel") };
        save.Accepted += (_, _) =>
        {
            try { _themeStore.Save(TuiTheme.All[chosen]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { status.Text = _localization.Resolve("shell.theme.save_failed"); return; }
            _theme = TuiTheme.All[chosen];
            _themeBeforePreview = null;
            ReturnFromPreferencesSubpage();
        };
        cancel.Accepted += (_, _) => ReturnFromPreferencesSubpage();
        frame.Add(list, heading, sample, status, save, cancel); ApplyTheme(frame);
        _overlay = frame; _window.Add(frame); Preview(); list.SetFocus();
    }
}
