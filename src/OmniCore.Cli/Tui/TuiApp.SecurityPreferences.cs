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

    private List<string> ReadKnownToolIds()
    {
        try
        {
            var json = _client.Query("tools", CancellationToken.None)?.Json;
            if (json is null) return [];
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("tools", out var tools) && tools.ValueKind == System.Text.Json.JsonValueKind.Array
                ? tools.EnumerateArray().Select(tool => tool.TryGetProperty("toolId", out var id) ? id.GetString() : null)
                    .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception) { return []; }
    }

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
        // Autonomy level: three explicit choices instead of typing a profile ID.
        panel.Add(MenuIndent.Heading(SecurityPref("level_heading"), 0));
        var profile = new TextField { Id = "security-profile", X = 0, Y = 0, Width = 1, Height = 1, Visible = false,
            Text = UiCompletionResources.EnumLabel("security.profile", snapshot.Profile, _locale) };
        var levelDescription = new Label { Id = "omni-detail", X = MenuIndent.Level1, Y = 3, Width = Dim.Fill(4), Height = 2 };
        var levels = new[] { ("Conservative", "level_restrict"), ("Balanced", "level_ask"), ("Autonomous", "level_free") };
        var levelButtons = new List<Button>();
        string CurrentLevel() => PreferenceInput.CanonicalValue("security.profile", profile.Text.ToString(), _locale,
            "Conservative", "Balanced", "Autonomous") ?? snapshot.Profile;
        void SyncLevels()
        {
            var current = CurrentLevel();
            for (var index = 0; index < levels.Length; index++)
                levelButtons[index].Text = (levels[index].Item1 == current ? "● " : "○ ") + SecurityPref(levels[index].Item2);
            var selectedLevel = levels.FirstOrDefault(level => level.Item1 == current);
            levelDescription.Text = selectedLevel.Item2 is null ? "" : SecurityPref(selectedLevel.Item2 + "_help");
        }
        View previousLevel = null!;
        foreach (var (value, key) in levels)
        {
            var button = new Button { Id = "security-level-" + value.ToLowerInvariant(), Y = 1, Text = SecurityPref(key),
                X = levelButtons.Count == 0 ? MenuIndent.Level1 : Pos.Right(previousLevel) + 2 };
            button.Accepted += (_, _) => { profile.Text = UiCompletionResources.EnumLabel("security.profile", value, _locale); SyncLevels(); };
            levelButtons.Add(button); previousLevel = button;
        }
        panel.Add(profile, levelDescription); foreach (var button in levelButtons) panel.Add(button);
        SyncLevels();
        panel.Add(MenuIndent.Heading(SecurityPref("rules_heading"), 5));
        var ruleList = new ListView { Id = "security-rules", X = MenuIndent.Level1, Y = 6, Width = Dim.Fill(4), Height = 4, ShowMarks = false };
        ruleList.SetSource(new ObservableCollection<string>(snapshot.Rules.Select(row => row.ToolId + " · "
            + UiCompletionResources.EnumLabel("security.decision", row.Decision, _locale))));
        var selectedRule = snapshot.Rules.FirstOrDefault();
        var tool = new TextField { Id = "security-tool-id", X = MenuIndent.Level2 + 26, Y = 11, Width = Dim.Fill(4), Height = 1, Text = selectedRule?.ToolId ?? "" };
        var decision = new TextField { Id = "security-decision", X = MenuIndent.Level2 + 26, Y = 12, Width = Dim.Fill(4), Height = 1,
            Text = UiCompletionResources.EnumLabel("security.decision", selectedRule?.Decision ?? "Ask", _locale) };
        // Known tool IDs come from the same inspector as /tools, so a rule never needs a typed guess.
        var knownTools = ReadKnownToolIds();
        var pickTool = new Button { Id = "security-tool-pick", X = MenuIndent.Level2, Y = 13, Text = SecurityPref("tool_pick"),
            Enabled = knownTools.Count > 0 };
        pickTool.Accepted += (_, _) =>
        {
            var at = knownTools.IndexOf(tool.Text.ToString().Trim());
            tool.Text = knownTools[(at + 1) % knownTools.Count];
        };
        var cycleDecision = new Button { Id = "security-decision-cycle", X = Pos.Right(pickTool) + 2, Y = 13, Text = SecurityPref("decision_cycle") };
        cycleDecision.Accepted += (_, _) =>
        {
            var values = new[] { "Allow", "Ask", "Deny" };
            var current = PreferenceInput.CanonicalValue("security.decision", decision.Text.ToString(), _locale, values);
            decision.Text = UiCompletionResources.EnumLabel("security.decision", values[(Array.IndexOf(values, current) + 1) % values.Length], _locale);
        };
        panel.Add(ruleList,
            new Label { X = MenuIndent.Level2, Y = 11, Width = 24, Height = 1, Text = SecurityPref("tool_id") }, tool,
            new Label { X = MenuIndent.Level2, Y = 12, Width = 24, Height = 1, Text = SecurityPref("decision_short") }, decision,
            pickTool, cycleDecision,
            new Label { X = MenuIndent.Level2, Y = 15, Width = Dim.Fill(4), Height = 2, Text = SecurityPref("rule_note") });
        ruleList.ValueChanged += (_, _) =>
        {
            if (ruleList.SelectedItem is not int index || index < 0 || index >= snapshot.Rules.Count) return;
            selectedRule = snapshot.Rules[index]; tool.Text = selectedRule.ToolId;
            decision.Text = UiCompletionResources.EnumLabel("security.decision", selectedRule.Decision, _locale);
        };
        var addRule = new Button { Id = "security-rule-add", X = MenuIndent.Level1, Y = 17, Text = SecurityPref("rule_add") };
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
                    InvalidatePermissionLevel();
                    note.Text = SecurityPref("saved"); CloseOverlay(); ShowSecurityPreferences("tools_security");
                }
                catch (InvalidOperationException exception) when (exception.Message == "securityPreferences.error.stale")
                { CloseOverlay(); ShowMessage(SecurityPref("stale")); }
                catch (ArgumentException) { CloseOverlay(); ShowMessage(SecurityPref("invalid_values")); }
                catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("save_failed")); }
            });
        };
        panel.Add(MenuIndent.Heading(SecurityPref("trust_heading"), 20));
        panel.Add(new Label { X = MenuIndent.Level1, Y = 21, Width = Dim.Fill(4), Height = 1,
            Text = SecurityPref("trust_state") + ": " + SecurityPref(snapshot.WorkspaceTrusted ? "trusted" : "untrusted") });
        var trust = new Button { Id = "security-trust", X = MenuIndent.Level1, Y = 22, Text = SecurityPref(snapshot.WorkspaceTrusted ? "revoke_trust" : "trust_workspace") };
        trust.Accepted += (_, _) => ConfirmSecurityChange(SecurityPref("trust_title"), SecurityPref("trust_confirm"), () =>
        {
            try { _ = host.SetWorkspaceTrust(snapshot.WorkspaceTrusted, !snapshot.WorkspaceTrusted); CloseOverlay(); ShowSecurityPreferences("tools_security"); }
            catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("stale")); }
        });
        var grants = new ListView { Id = "security-grants", X = MenuIndent.Level1, Y = 26, Width = Dim.Fill(4), Height = 4, ShowMarks = false };
        grants.SetSource(new ObservableCollection<string>(snapshot.Grants.Select(g => g.ToolId + " · "
            + UiCompletionResources.EnumLabel("lifetime", g.Lifetime, _locale) + " · " + g.Id)));
        panel.Add(MenuIndent.Heading(SecurityPref("grants"), 25));
        var revoke = new Button { Id = "security-grant-revoke", X = MenuIndent.Level1, Y = 30, Text = SecurityPref("revoke_grant"), Enabled = snapshot.Grants.Count > 0 };
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
        panel.Add(new Label { X = MenuIndent.Level0, Y = 33, Width = Dim.Fill(4), Height = 3, Text = SecurityPref("help") });
        ConfigurePreferenceScroll(panel, 37);
        frame.Add(panel, save, back, note);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); levelButtons[0].SetFocus();
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
