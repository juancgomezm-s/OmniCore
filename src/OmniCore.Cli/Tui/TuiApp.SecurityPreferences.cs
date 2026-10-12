namespace OmniCore.Cli;

using System.Collections.ObjectModel;
using OmniCore.Host;
using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiApp
{
    private Action? _cancelSecurityConfirmation;
    private View? _suspendedSecurityForm;

    private void CancelSecurityConfirmation()
    {
        var cancel = _cancelSecurityConfirmation;
        _cancelSecurityConfirmation = null;
        if (cancel is not null) cancel();
        else { CloseOverlay(); ShowSecurityPreferences("tools_security"); }
    }

    internal void ShowSecurityPreferences(string queryName)
    {
        if (_window is null || _overlay is not null || queryName != "tools_security") return;
        var host = _securityPreferencesOverride ?? SecurityPreferencesHost.Create(Environment.CurrentDirectory);
        SecurityPreferencesSnapshot snapshot;
        try { snapshot = host.Read(); }
        catch (Exception) { ShowMessage(SecurityPref("unavailable")); return; }
        var frame = OverlayFrame(SecurityPref("title"), _window.Frame.Height - 3);
        frame.Id = "security-preferences"; frame.Width = Dim.Percent(92); frame.Height = Dim.Fill(2);
        var panel = new View { Id = "security-preferences-form", X = 2, Y = 2, Width = Dim.Fill(4), Height = Dim.Fill(9),
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        var rules = snapshot.Rules.ToDictionary(row => row.ToolId, row => row.Decision, StringComparer.Ordinal);
        var note = new Label { Id = "security-preferences-notice", X = 2, Y = Pos.AnchorEnd(6), Width = Dim.Fill(4), Height = 2,
            Text = SecurityPref("scope_note") };
        panel.Add(new Label { X = 2, Y = 0, Width = 22, Height = 1, Text = SecurityPref("profile") });
        var profile = new TextField { Id = "security-profile", X = 25, Y = 0, Width = Dim.Fill(4), Height = 1,
            Text = UiCompletionResources.EnumLabel("security.profile", snapshot.Profile, _locale) };
        panel.Add(profile, new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 1,
            Text = string.Join(" / ", new[] { "Conservative", "Balanced", "Autonomous" }
                .Select(value => UiCompletionResources.EnumLabel("security.profile", value, _locale))) });
        panel.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 1,
            Text = SecurityPref("trust_state") + ": " + SecurityPref(snapshot.WorkspaceTrusted ? "trusted" : "untrusted") });
        var ruleList = new ListView { Id = "security-rules", X = 2, Y = 4, Width = Dim.Fill(4), Height = 5, ShowMarks = false };
        ruleList.SetSource(new ObservableCollection<string>(snapshot.Rules.Select(row => row.ToolId + " · "
            + UiCompletionResources.EnumLabel("security.decision", row.Decision, _locale))));
        var selectedRule = snapshot.Rules.FirstOrDefault();
        var tool = new TextField { Id = "security-tool-id", X = 2, Y = 12, Width = Dim.Fill(4), Height = 1, Text = selectedRule?.ToolId ?? "" };
        var decision = new TextField { Id = "security-decision", X = 2, Y = 15, Width = Dim.Fill(4), Height = 1,
            Text = UiCompletionResources.EnumLabel("security.decision", selectedRule?.Decision ?? "Ask", _locale) };
        panel.Add(ruleList,
            new Label { X = 2, Y = 10, Width = Dim.Fill(4), Height = 1, Text = SecurityPref("tool_id") }, tool,
            new Label { X = 2, Y = 13, Width = Dim.Fill(4), Height = 1, Text = SecurityPref("decision") }, decision,
            new Label { X = 2, Y = 17, Width = Dim.Fill(4), Height = 2, Text = SecurityPref("rule_note") });
        ruleList.ValueChanged += (_, _) =>
        {
            if (ruleList.SelectedItem is not int index || index < 0 || index >= snapshot.Rules.Count) return;
            selectedRule = snapshot.Rules[index]; tool.Text = selectedRule.ToolId;
            decision.Text = UiCompletionResources.EnumLabel("security.decision", selectedRule.Decision, _locale);
        };
        var addRule = new Button { Id = "security-rule-add", X = 2, Y = 19, Text = SecurityPref("rule_add") };
        addRule.Accepted += (_, _) => { tool.Text = ""; decision.Text = UiCompletionResources.EnumLabel("security.decision", "Ask", _locale); tool.SetFocus(); };
        var save = new Button { Id = "security-save", X = 2, Y = Pos.AnchorEnd(4), Text = SecurityPref("save") };
        save.Accepted += (_, _) =>
        {
            var ruleId = tool.Text.ToString().Trim();
            var value = PreferenceInput.CanonicalValue("security.decision", decision.Text.ToString(), _locale, "Allow", "Ask", "Deny");
            if (ruleId.Length > 0)
            {
                if (value is not ("Allow" or "Ask" or "Deny")) { note.Text = SecurityPref("invalid_decision"); return; }
                rules[ruleId] = value;
            }
            var proposedProfile = PreferenceInput.CanonicalValue("security.profile", profile.Text.ToString(), _locale,
                "Conservative", "Balanced", "Autonomous");
            ConfirmSecurityChange(SecurityPref("permissions_title"), SecurityPref("permissions_confirm"), () =>
            {
                try
                {
                    snapshot = host.SaveUserPermissions(snapshot.Revision, proposedProfile, rules);
                    note.Text = SecurityPref("saved"); CloseOverlay(); ShowSecurityPreferences("tools_security");
                }
                catch (InvalidOperationException exception) when (exception.Message == "securityPreferences.error.stale")
                { CloseOverlay(); ShowMessage(SecurityPref("stale")); }
                catch (ArgumentException) { CloseOverlay(); ShowMessage(SecurityPref("invalid_values")); }
                catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("save_failed")); }
            });
        };
        var trust = new Button { Id = "security-trust", X = 2, Y = 23, Text = SecurityPref(snapshot.WorkspaceTrusted ? "revoke_trust" : "trust_workspace") };
        trust.Accepted += (_, _) => ConfirmSecurityChange(SecurityPref("trust_title"), SecurityPref("trust_confirm"), () =>
        {
            try { _ = host.SetWorkspaceTrust(snapshot.WorkspaceTrusted, !snapshot.WorkspaceTrusted); CloseOverlay(); ShowSecurityPreferences("tools_security"); }
            catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("stale")); }
        });
        var grants = new ListView { Id = "security-grants", X = 2, Y = 27, Width = Dim.Fill(4), Height = 4, ShowMarks = false };
        grants.SetSource(new ObservableCollection<string>(snapshot.Grants.Select(g => g.ToolId + " · "
            + UiCompletionResources.EnumLabel("lifetime", g.Lifetime, _locale) + " · " + g.Id)));
        panel.Add(new Label { Id = "security-grants-label", X = 2, Y = 25, Width = Dim.Fill(4), Height = 1, Text = SecurityPref("grants") });
        var revoke = new Button { Id = "security-grant-revoke", X = 2, Y = 32, Text = SecurityPref("revoke_grant"), Enabled = snapshot.Grants.Count > 0 };
        revoke.Accepted += (_, _) =>
        {
            if (grants.SelectedItem is not int index || index < 0 || index >= snapshot.Grants.Count) return;
            var grant = snapshot.Grants[index];
            ConfirmSecurityChange(SecurityPref("grant_title"), SecurityPref("grant_confirm") + Environment.NewLine + grant.ToolId,
                () => { try { _ = host.RevokeGrant(grant.Id); CloseOverlay(); ShowSecurityPreferences("tools_security"); }
                    catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("stale")); } });
        };
        var back = new Button { Id = "security-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = SecurityPref("back") };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("tools_security", categoryPage: true); };
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; CloseOverlay(); ShowPreferences("tools_security", categoryPage: true); } };
        panel.Add(addRule, trust, grants, revoke);
        panel.Add(new Label { X = 2, Y = 34, Width = Dim.Fill(4), Height = 3, Text = SecurityPref("help") });
        ConfigurePreferenceScroll(panel, 38);
        frame.Add(panel, save, back, note);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); profile.SetFocus();
    }

    private void ConfirmSecurityChange(string title, string description, Action confirm)
    {
        if (_window is null) return;
        // Keep the editable form itself while confirming; cancel must retain the draft,
        // selection, viewport and revision rather than silently re-reading saved settings.
        var draft = _overlay;
        if (draft is not null) _window.Remove(draft);
        _suspendedSecurityForm = draft;
        _overlay = null;
        var frame = OverlayFrame(title, 12); frame.Id = "security-preferences-confirm";
        _cancelSecurityConfirmation = () =>
        {
            CloseOverlay();
            if (draft is null) { ShowSecurityPreferences("tools_security"); return; }
            _retiredOverlays.Remove(draft);
            _overlay = draft; _window.Add(draft); draft.SetFocus();
        };
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 4, Text = description });
        var cancel = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = SecurityPref("cancel") };
        cancel.Accepted += (_, _) => CancelSecurityConfirmation();
        var accept = new Button { X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = SecurityPref("confirm") };
        accept.Accepted += (_, _) =>
        {
            _cancelSecurityConfirmation = null;
            confirm();
        };
        frame.Add(cancel, accept); ApplyTheme(frame); _overlay = frame; _window.Add(frame); cancel.SetFocus();
    }

    private string SecurityPref(string key) => _localization.Resolve("securityPreferences." + key);
}
