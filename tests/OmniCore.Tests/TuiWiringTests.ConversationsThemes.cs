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
        Assert.Equal("omnicore", fx.App.ThemeId);
        ClickId(fx, "theme-save"); fx.Wait(() => fx.App.ThemeId == id, "saved theme applies");
        Assert.Equal(id, new TuiThemeStore(Path.Combine(fx.Root, "tui-animations.theme")).Read().Id);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "return to conversation");
        Assert.Equal(text, fx.App.Conversation.Text.ToString()); Assert.Equal(bounds, fx.App.Composer.Frame);
        Assert.Equal(new Terminal.Gui.Drawing.Color(TuiTheme.Find(id).Background), fx.App.MainWindow!.GetScheme().Normal.Background);
        ExportPreferenceFrame(fx, 120, 40, "theme-" + id, "");
    }, noColor: false);
}
