#pragma warning disable CS0618 // The help viewer is the pinned Terminal.Gui TextView.
namespace OmniCore.Tests;

using OmniCore.Cli;
using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    private static string FooterText(TuiFixture fx) =>
        fx.App.MainWindow!.SubViews.OfType<Label>().Last(view => view.Id == "omni-help").Text.ToString();

    [Fact]
    public void Bare_commands_explain_their_usage_instead_of_reporting_unknown_keys() => RunTuiTest(fx =>
    {
        fx.StartTui();
        foreach (var command in new[] { "/act", "/runmode", "/ultracode" })
        {
            Type(fx, command);
            KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, command + " abre una explicación");
            var text = OverlayText(fx.App.Overlay!);
            Assert.DoesNotContain("command.prompt_not_found", text);
            Assert.Contains(command, text);
            KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc cierra la explicación");
        }
    });

    [Fact]
    public void Inspection_commands_open_their_screens_and_return_to_the_chat() => RunTuiTest(fx =>
    {
        fx.StartTui();
        Type(fx, "/context");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id?.StartsWith("session-preferences", StringComparison.Ordinal) == true,
            "/context abre la vista de contexto");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc vuelve al chat, no a Preferencias");
        fx.Wait(() => fx.App.Composer!.HasFocus, "el foco vuelve al composer");

        Type(fx, "/commands");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "command-preferences", "/commands abre el catálogo");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc vuelve al chat");

        Type(fx, "/help");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "/help abre la ayuda");
        var help = Descendants(fx.App.Overlay!).OfType<TextView>().Single(view => view.Id == "preferences-inspection");
        var text = help.Text.ToString();
        foreach (var command in new[] { "/runmode", "/mode", "/act", "/models", "/context" }) Assert.Contains(command, text);
        Assert.Contains("Shift+Tab", text);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc cierra la ayuda");
    });

    [Fact]
    public void Shift_tab_cycles_the_mode_and_the_footer_shows_it() => RunTuiTest(fx =>
    {
        fx.StartTui();
        fx.Wait(() => FooterText(fx).StartsWith("act", StringComparison.Ordinal), "el pie arranca en ACT");
        Assert.Contains("Permisos: Libre", FooterText(fx));
        Assert.Contains("⇧Tab", FooterText(fx) + CompactHelpProbe());
        KeyWithEffect(fx, KeyCode.Tab | KeyCode.ShiftMask, () => FooterText(fx).StartsWith("orq", StringComparison.OrdinalIgnoreCase)
            || FooterText(fx).StartsWith(StatusLinePresentation.ModeLabel("orchestrate", "es"), StringComparison.Ordinal), "ACT → ORQ");
        KeyWithEffect(fx, KeyCode.Tab | KeyCode.ShiftMask, () => FooterText(fx).StartsWith(StatusLinePresentation.ModeLabel("plan", "es"), StringComparison.Ordinal), "ORQ → PLAN");
        KeyWithEffect(fx, KeyCode.Tab | KeyCode.ShiftMask, () => FooterText(fx).StartsWith("act", StringComparison.Ordinal), "PLAN → ACT");
    });

    private static string CompactHelpProbe() => TuiApp.CompactHelp("act", "es", 200, "Permisos: Libre");

    [Fact]
    public void Compact_help_shows_mode_and_permission_level_before_key_hints()
    {
        var text = TuiApp.CompactHelp("plan", "es", 200, "Permisos: Restringir");
        Assert.StartsWith(StatusLinePresentation.ModeLabel("plan", "es") + " · Permisos: Restringir · Enter enviar", text);
        Assert.Contains("⇧Tab modo", text);
        Assert.DoesNotContain("Permisos", TuiApp.CompactHelp("plan", "es", 200));
    }
}
