using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly TuiThemeStore _themeStore;
    private TuiTheme _theme;
    internal string ThemeId => _theme.Id;

    private void ShowThemePreferences()
    {
        if (_window is null || _overlay is not null) return;
        var frame = OverlayFrame(_localization.Resolve("shell.theme.title"), 15);
        frame.Id = "theme-preferences";
        var list = new ListView { Id = "theme-list", X = MenuIndent.Level1, Y = 3,
            Width = Dim.Fill(4), Height = 4, ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(TuiTheme.All.Select(theme => theme.Name)));
        list.SelectedItem = TuiTheme.All.ToList().FindIndex(theme => theme.Id == _theme.Id);
        var chosen = list.SelectedItem ?? 0;
        list.Accepted += (_, args) => { args.Handled = true; chosen = list.SelectedItem ?? chosen; };
        list.MouseEvent += (_, mouse) =>
        {
            if (!mouse.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.LeftButtonClicked) || mouse.Position is not { } point) return;
            var index = point.Y + list.Viewport.Y;
            if (index < 0 || index >= TuiTheme.All.Count) return;
            list.SelectedItem = chosen = index; list.SetFocus(); mouse.Handled = true;
        };
        var status = MenuIndent.Help("", 8, 2, "theme-status");
        var save = new Button { Id = "theme-save", X = 4, Y = 11, Text = Pref("save") };
        var cancel = new Button { Id = "theme-cancel", X = 22, Y = 11, Text = Pref("cancel") };
        save.Accepted += (_, _) =>
        {
            try { _themeStore.Save(TuiTheme.All[chosen]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { status.Text = _localization.Resolve("shell.theme.save_failed"); return; }
            _theme = TuiTheme.All[chosen];
            ApplyTheme(_window); _renderedConversation = null; RenderState();
            ReturnFromPreferencesSubpage();
        };
        cancel.Accepted += (_, _) => ReturnFromPreferencesSubpage();
        frame.Add(list, status, save, cancel); ApplyTheme(frame);
        _overlay = frame; _window.Add(frame); list.SetFocus();
    }
}
