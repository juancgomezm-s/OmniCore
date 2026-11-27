#pragma warning disable CS0618 // Pinned Terminal.Gui multiline editor.
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private string ClaudeSetupText(string key) => ClaudeAccountSetupResources.Get(_locale, key);
    private string ClaudeSetupError(Exception error) => ClaudeSetupText(error.Message.StartsWith("claudeSetup.", StringComparison.Ordinal)
        ? error.Message[12..] : error is ConfigValidationException or ArgumentException ? "invalid" : "unavailable");

    private void ShowClaudeAccountConfiguration(string? providerId, bool signInAfterSave)
    {
        if (_window is null || _claudeAccounts is not ITuiClaudeAccountConfigurationHost host) return;
        ClaudeAccountConfigurationSnapshot snapshot;
        try { snapshot = host.ReadConfiguration(providerId, CancellationToken.None); }
        catch (Exception error) { ShowMessage(ClaudeSetupError(error)); return; }
        CloseOverlay();
        var frame = PreferenceFrame(ClaudeSetupText("configure"), Math.Min(30, _window.Viewport.Height - 2));
        frame.Id = "claude-setup-preferences";
        frame.Width = Dim.Percent(92);
        frame.Height = Dim.Func(_ => Math.Min(30, Math.Max(1, _window.Viewport.Height - 2)));
        var panel = new View { Id = "claude-setup-fields", X = 2, Y = 4, Width = Dim.Fill(2), Height = Dim.Fill(8),
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        var draft = snapshot.Draft;
        var y = 0;
        TextField Field(string key, string value)
        {
            panel.Add(new Label { X = 0, Y = y++, Width = Dim.Fill(2), Height = 1, Text = ClaudeSetupText(key), TextAlignment = Alignment.Start });
            var field = new TextField { Id = "claude-setup-" + key, X = 0, Y = y++, Width = Dim.Fill(2), Text = value };
            panel.Add(field); return field;
        }
        var id = Field("provider", draft.ProviderId); id.ReadOnly = providerId is not null; id.CanFocus = providerId is null;
        var advanced = new CompactRowButton { Id = "claude-setup-advanced", X = 0, Y = y++, Height = 1,
            NoPadding = true, TextAlignment = Alignment.Start, Text = ClaudeSetupText("advanced") };
        panel.Add(advanced);
        var basicHeight = y;
        var start = panel.SubViews.Count;
        var baseUrl = Field("base", draft.BaseUrl);
        var client = Field("client", draft.ClientId);
        var agent = Field("agent", draft.UserAgent);
        var system = new TextView { Id = "claude-setup-system", X = 0, Y = y, Width = Dim.Fill(2), Height = 5,
            BorderStyle = LineStyle.Single, Multiline = true, TabKeyAddsTab = false, WordWrap = true,
            Title = ClaudeSetupText("system"), Text = draft.AgentSystemPrompt };
        panel.Add(system); y += 5;
        panel.Add(new Label { X = 2, Y = y, Width = Dim.Fill(4), Height = 4,
            Text = ClaudeSetupText("system_help"), TextAlignment = Alignment.Start });
        y += 4;
        system.KeyDown += (_, key) =>
        {
            if (key.Handled || key.KeyCode != (KeyCode.Enter | KeyCode.ShiftMask)) return;
            key.Handled = true; system.InsertText("\n");
        };
        var scopes = Field("scopes", string.Join(" ", draft.Scopes));
        var authorize = Field("authorize", draft.AuthorizeUrl);
        var token = Field("token", draft.TokenUrl);
        var profile = Field("profile", draft.ProfileUrl);
        var roles = Field("roles", draft.RolesUrl);
        var fields = panel.SubViews.Skip(start).ToArray();
        var expanded = false;
        var resize = ConfigurePreferenceScroll(panel, () => expanded ? y : basicHeight);
        void ShowAdvanced()
        {
            foreach (var field in fields) field.Visible = expanded;
            advanced.Text = ClaudeSetupText(expanded ? "less" : "advanced");
            resize(); frame.SetNeedsLayout();
        }
        void Toggle() { expanded = !expanded; ShowAdvanced(); }
        advanced.Accepted += (_, e) => { e.Handled = true; Toggle(); };
        advanced.KeyDown += (_, key) =>
        { if (!key.Handled && key.KeyCode == KeyCode.Space) { key.Handled = true; Toggle(); } };
        var status = new Label { Id = "claude-setup-status", X = 2, Y = Pos.AnchorEnd(7), Height = 3, Width = Dim.Fill(4),
            Text = ClaudeSetupText("navigation"), TextAlignment = Alignment.Start };
        var save = new CompactRowButton { Id = "claude-setup-save", X = 2, Y = Pos.AnchorEnd(4), Height = 1, NoPadding = true,
            TextAlignment = Alignment.Start, Text = ClaudeSetupText(signInAfterSave ? "save_login" : "save") };
        var cancel = new CompactRowButton { Id = "claude-setup-cancel", X = 2, Y = Pos.AnchorEnd(2), Height = 1, NoPadding = true,
            TextAlignment = Alignment.Start, Text = AccountUi("action.cancel") };
        void Return() { _returnToClaudeAccounts = false; CloseOverlay(); ShowClaudeAccounts(selectedProviderId: providerId); }
        cancel.Accepted += (_, _) => Return();
        save.Accepted += (_, _) =>
        {
            try
            {
                var next = new ClaudeAccountConfigurationDraft(id.Text.ToString().Trim(), baseUrl.Text.ToString().Trim(),
                    client.Text.ToString().Trim(), agent.Text.ToString().Trim(),
                    scopes.Text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
                    authorize.Text.ToString().Trim(), token.Text.ToString().Trim(), profile.Text.ToString().Trim(), roles.Text.ToString().Trim())
                    { AgentSystemPrompt = system.Text.ToString().ReplaceLineEndings("\n") };
                host.SaveConfiguration(snapshot.Revision, providerId, next, CancellationToken.None);
                if (signInAfterSave) StartClaudeLogin(next.ProviderId);
                else { _returnToClaudeAccounts = false; CloseOverlay(); ShowClaudeAccounts(ClaudeSetupText("saved"), next.ProviderId); }
            }
            catch (Exception error)
            {
                status.Text = ClaudeSetupError(error);
                if (error.Message == "claudeSetup.system_invalid") system.SetFocus();
            }
        };
        EnterNavigation.TraverseForm([id, advanced, baseUrl, client, agent, system, scopes, authorize, token, profile, roles], save, cancel);
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 3, Text = ClaudeSetupText("intro"), TextAlignment = Alignment.Start },
            panel, status, save, cancel);
        ShowAdvanced();
        OpenControlFrame(frame, providerId is null ? id : advanced);
        _returnToClaudeAccounts = true; _preferencesReturnToCategory = true;
    }
}
