using System.Drawing;
using OmniCore.Cli;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Help_has_About_at_the_bottom_with_contact_and_explicit_GitHub_activation() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 40);
        var links = new RecordingWebLinkHost(); fx.App.LinkHost = links;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences");
        ChoosePreferenceCategory(fx, "diagnostics_data"); ClickId(fx, "preferences-help-open");
        fx.Wait(() => fx.App.Overlay?.Id == "help-screen", "help screen");
        fx.Invoke(() =>
        {
            var about = Descendants(fx.App.Overlay!).Single(view => view.Id == "help-about");
            var content = Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-inspection");
            Assert.True(about.Frame.Y >= content.Frame.Bottom);
            var help = content.Text;
            Assert.Contains("/nuevo", help); Assert.Contains("/retomar", help); Assert.Contains("/compactar", help);
            Assert.Contains("F6", help);
        });
        ClickId(fx, "help-about"); fx.Wait(() => fx.App.Overlay?.Id == "about-screen", "about screen");
        Assert.Equal("juancgomezm@gmail.com", Descendants(fx.App.Overlay!).OfType<TextField>().Single().Text);
        Assert.Empty(links.Opened);
        ClickId(fx, "about-github"); fx.Wait(() => links.Opened.Count == 1, "explicit project link");
        Assert.Equal("https://github.com/juancgomezm-s/OmniCore", Assert.Single(links.Opened));
        ExportPreferenceFrame(fx, 120, 40, "about", "Acerca de OmniCore");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "help-screen", "Esc returns to help");
        ClickId(fx, "preferences-inspection-close");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "preferences-help-open"), "return to diagnostics submenu");
    }, noColor: false);

    [Fact]
    public void Conversation_commands_clear_only_the_view_create_and_resume_with_real_keys() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 40);
        var original = fx.Server.LastSessionId()!.ToString();
        var sequence = fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!);
        Assert.NotEmpty(fx.App.ProjectionState.Conversation.Blocks);
        Type(fx, "/limpiar"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.ProjectionState.Conversation.Blocks.Count == 0, "clear view");
        Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!));
        Type(fx, "/nuevo"); KeyWithEffect(fx, KeyCode.Enter, () => fx.Server.LastSessionId()!.ToString() != original, "new session");
        Assert.Empty(fx.App.ProjectionState.Conversation.Blocks);
        Type(fx, "/retomar"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "conversation-selector", "session selector");
        var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        fx.Invoke(() => { list.SelectedItem = 1; list.SetFocus(); });
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is null && fx.Server.LastSessionId()!.ToString() == original, "resume selected conversation");
        Assert.NotEmpty(fx.App.ProjectionState.Conversation.Blocks);
        KeyWithEffect(fx, KeyCode.F6, () => fx.App.Overlay?.Id == "conversation-selector", "F6 opens conversations");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "leave selector without switching");
        Assert.Equal(original, fx.Server.LastSessionId()!.ToString());
    });

    [Theory]
    [InlineData(1, "opencode")]
    [InlineData(2, "claude")]
    [InlineData(3, "codex")]
    public void Theme_menu_saves_colors_without_changing_main_layout_or_conversation(int index, string id) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 40);
        var text = fx.App.Conversation!.Text.ToString(); var bounds = fx.App.Composer!.Frame;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences");
        ChoosePreferenceCategory(fx, "general"); ClickId(fx, "preferences-theme-open");
        fx.Wait(() => fx.App.Overlay?.Id == "theme-preferences", "theme menu");
        var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        fx.Invoke(() =>
        {
            var point = list.ViewportToScreen(new Point(2, index));
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = point, Flags = MouseFlags.LeftButtonClicked });
        });
        fx.Wait(() => fx.App.ThemeId == id, "theme previews before saving");
        Assert.False(File.Exists(Path.Combine(fx.Root, "tui-animations.theme")));
        ClickId(fx, "theme-save"); fx.Wait(() => fx.App.ThemeId == id, "saved theme applies");
        Assert.Equal(id, new TuiThemeStore(Path.Combine(fx.Root, "tui-animations.theme")).Read().Id);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "return to conversation");
        Assert.Equal(text, fx.App.Conversation.Text.ToString()); Assert.Equal(bounds, fx.App.Composer.Frame);
        Assert.Equal(new Terminal.Gui.Drawing.Color(TuiTheme.Find(id).Background), fx.App.MainWindow!.GetScheme().Normal.Background);
        ExportPreferenceFrame(fx, 120, 40, "theme-" + id, "");
    }, noColor: false);

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("f4")]
    public void Theme_preview_rolls_back_on_every_exit_without_writing_preferences(string exit) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 40);
        var store = new TuiThemeStore(Path.Combine(fx.Root, "tui-animations.theme"));
        store.Save(TuiTheme.Find("omnicore"));
        Type(fx, "borrador conservado");
        var draft = fx.App.Composer!.Text;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences");
        ChoosePreferenceCategory(fx, "general"); ClickId(fx, "preferences-theme-open");
        KeyWithEffect(fx, KeyCode.CursorDown, () => fx.App.ThemeId == "opencode", "arrow previews next palette");
        KeyWithEffect(fx, KeyCode.CursorDown, () => fx.App.ThemeId == "claude", "arrow previews light Claude");
        Assert.Equal("omnicore", store.Read().Id);
        if (exit == "cancel") ClickId(fx, "theme-cancel");
        else KeyWithEffect(fx, exit == "escape" ? KeyCode.Esc : KeyCode.F4,
            () => fx.App.Overlay?.Id != "theme-preferences", "leave theme preview");
        fx.Wait(() => fx.App.ThemeId == "omnicore", "original theme restored");
        Assert.Equal("omnicore", store.Read().Id);
        Assert.Equal(draft, fx.App.Composer.Text);
        Assert.Equal(new Terminal.Gui.Drawing.Color(TuiTheme.Find("omnicore").Background), fx.App.MainWindow!.GetScheme().Normal.Background);
    }, noColor: false);

    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 25)]
    public void Theme_catalog_previews_every_palette_with_real_arrows_and_keeps_actions_visible(int columns, int rows) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: columns, initialRows: rows);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences");
        ChoosePreferenceCategory(fx, "general"); ClickId(fx, "preferences-theme-open");
        for (var i = 0; i < TuiTheme.All.Count; i++)
        {
            var theme = TuiTheme.All[i];
            if (i > 0) KeyWithEffect(fx, KeyCode.CursorDown, () => fx.App.ThemeId == theme.Id, "preview " + theme.Id);
            fx.Invoke(() =>
            {
                var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
                var sample = Descendants(fx.App.Overlay!).OfType<StyledTextPresenter>().Single();
                var save = Descendants(fx.App.Overlay!).Single(view => view.Id == "theme-save");
                Assert.True(list.HasFocus);
                Assert.Equal(theme, sample.Theme);
                Assert.Equal(new Terminal.Gui.Drawing.Color(theme.Preferences), fx.App.Overlay!.GetScheme().Normal.Background);
                Assert.True(save.Frame.Bottom <= fx.App.Overlay.Viewport.Height);
                Assert.True(sample.Frame.Width >= 20);
            });
            Assert.False(File.Exists(Path.Combine(fx.Root, "tui-animations.theme")));
            ExportPreferenceFrame(fx, columns, rows, "preview-" + theme.Id, theme.Name);
        }
        ClickId(fx, "theme-save");
        fx.Wait(() => fx.App.Overlay?.Id != "theme-preferences", "save last palette");
        Assert.Equal(TuiTheme.All[^1].Id, new TuiThemeStore(Path.Combine(fx.Root, "tui-animations.theme")).Read().Id);
    }, noColor: false);

    [Fact]
    public void Theme_preview_respects_NO_COLOR() => RunTuiTest(fx =>
    {
        fx.StartTui();
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences");
        ChoosePreferenceCategory(fx, "general"); ClickId(fx, "preferences-theme-open");
        KeyWithEffect(fx, KeyCode.CursorDown, () => fx.App.ThemeId == "opencode", "preview without color");
        fx.Invoke(() =>
        {
            var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
            Assert.Equal(Terminal.Gui.Drawing.Color.None, list.GetScheme().Normal.Foreground);
            Assert.Equal(Terminal.Gui.Drawing.Color.None, list.GetScheme().Focus.Background);
            Assert.Equal(Terminal.Gui.Drawing.Color.None, fx.App.MainWindow!.GetScheme().Normal.Background);
        });
    }, noColor: true);

    [Fact]
    public void Theme_save_error_keeps_preview_open_and_cancel_restores_original() => RunTuiTest(fx =>
    {
        fx.StartTui();
        Directory.CreateDirectory(Path.Combine(fx.Root, "tui-animations.theme"));
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences");
        ChoosePreferenceCategory(fx, "general"); ClickId(fx, "preferences-theme-open");
        KeyWithEffect(fx, KeyCode.CursorDown, () => fx.App.ThemeId == "opencode", "preview");
        ClickId(fx, "theme-save");
        fx.Wait(() => Descendants(fx.App.Overlay!).Single(view => view.Id == "theme-status").Text.Length > 0, "save failure shown");
        Assert.Equal("theme-preferences", fx.App.Overlay!.Id);
        Assert.Equal("opencode", fx.App.ThemeId);
        ClickId(fx, "theme-cancel"); fx.Wait(() => fx.App.ThemeId == "omnicore", "rollback after failed save");
    }, noColor: false);

    [Fact]
    public void Theme_palettes_have_unique_surfaces_readable_text_and_focus_in_light_and_dark_modes()
    {
        Assert.Equal(TuiTheme.All.Count, TuiTheme.All.Select(theme => theme.Id).Distinct().Count());
        Assert.Equal(TuiTheme.All.Count, TuiTheme.All.Select(theme => (theme.Background, theme.Accent)).Distinct().Count());
        Assert.True(TuiTheme.Find("claude").IsLight);
        Assert.False(TuiTheme.Find("opencode").IsLight);
        foreach (var theme in TuiTheme.All)
        {
            foreach (var background in new[] { theme.Background, theme.Panel, theme.Preferences, theme.Menu, theme.Input })
            foreach (var foreground in new[] { theme.Foreground, theme.Muted })
                Check(foreground, background, theme.Id);
            foreach (var fill in new[] { theme.Selection, theme.Accent })
            {
                var foreground = TuiTheme.On(fill);
                Check($"#{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}", fill, theme.Id + " focus");
            }
        }
        static void Check(string foreground, string background, string id)
        {
            var a = ThemeLuminance(foreground); var b = ThemeLuminance(background);
            Assert.True((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5, id + " " + foreground + " on " + background);
        }
    }
}
