#pragma warning disable CS0618 // Physical navigation of the pinned multiline editor.
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("en", 120, 40)]
    [InlineData("fr", 80, 25)]
    [InlineData("it", 120, 40)]
    public void Claude_agent_system_editor_saves_multiline_text_and_keeps_Enter_Tab_and_cancel_navigation(string locale, int columns, int rows) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "agent-message"));
        var store = new TuiClaudeAccountHost(paths, _ => null);
        var setup = store.ReadConfiguration(null, default);
        store.SaveConfiguration(setup.Revision, null, setup.Draft, default);
        var host = new ConfigurableClaudeAccountFixture(store);
        fx.StartTui(claudeAccounts: host, initialColumns: columns, initialRows: rows,
            localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        void OpenEditor()
        {
            if (fx.App.Overlay is null)
            {
                KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferencias");
                ChoosePreferenceCategory(fx, "accounts_connections"); ClickId(fx, "preferences-claude-open");
            }
            ClickId(fx, "claude-account-configure");
            ClickId(fx, "claude-setup-advanced");
        }
        OpenEditor();
        var editor = (TextView)EnterControl(fx, "claude-setup-system");
        Assert.False(editor.TabKeyAddsTab);
        fx.Invoke(() => { editor.Text = "Use a teaching role."; editor.SetFocus(); });
        fx.InjectKey(new Key(KeyCode.Enter | KeyCode.ShiftMask));
        fx.Wait(() => editor.Text.ToString().Contains('\n'), "Shift+Enter inserta una línea");
        fx.Invoke(() => editor.Text = "Use a teaching role.\nRespond in Spanish.");
        fx.InjectKey(new Key(KeyCode.Tab));
        fx.Wait(() => EnterControl(fx, "claude-setup-scopes").HasFocus, "Tab sale del editor");
        fx.InjectKey(new Key(KeyCode.Tab | KeyCode.ShiftMask));
        fx.Wait(() => editor.HasFocus, "Shift+Tab regresa al editor");
        Assert.DoesNotContain('\t', editor.Text.ToString());
        ExportPreferenceFrame(fx, columns, rows, "agent-system-" + locale, ClaudeAccountSetupResources.Get(locale, "system"));
        EnterOnce(fx, () => EnterControl(fx, "claude-setup-scopes").HasFocus, "Enter avanza sin guardar");
        Assert.Equal("", store.ReadConfiguration("anthropic", default).Draft.AgentSystemPrompt);
        ClickId(fx, "claude-setup-save");
        var saved = store.ReadConfiguration("anthropic", default);
        Assert.Equal("Use a teaching role.\nRespond in Spanish.", saved.Draft.AgentSystemPrompt);
        Assert.Equal(setup.Draft.ClientId, saved.Draft.ClientId);
        Assert.Equal(0, host.LoginCalls);
        OpenEditor();
        editor = (TextView)EnterControl(fx, "claude-setup-system");
        Assert.Equal(saved.Draft.AgentSystemPrompt, editor.Text.ToString().ReplaceLineEndings("\n"));
        fx.Invoke(() => editor.Text = "unsaved draft");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "claude-account-preferences", "Esc descarta el borrador");
        Assert.Equal(saved.Draft.AgentSystemPrompt, store.ReadConfiguration("anthropic", default).Draft.AgentSystemPrompt);
        OpenEditor();
        fx.Invoke(() => ((TextView)EnterControl(fx, "claude-setup-system")).Text = "");
        ClickId(fx, "claude-setup-save");
        Assert.Equal("", store.ReadConfiguration("anthropic", default).Draft.AgentSystemPrompt);
        Assert.Equal(0, host.LoginCalls);
    });
}
