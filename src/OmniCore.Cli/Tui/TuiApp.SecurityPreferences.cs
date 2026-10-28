namespace OmniCore.Cli;

using System.Collections.ObjectModel;
using OmniCore.Host;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiApp
{
    private Action? _cancelSecurityConfirmation;
    private View? _suspendedSecurityForm;

    private string ControlText(string key) => ExecutionControlsResources.Get(_locale, key);

    private FrameView ControlFrame(string title, string id)
    {
        var frame = OverlayFrame(title, Math.Min(22, _window!.Frame.Height - 2));
        frame.Id = id;
        frame.Width = Dim.Percent(94);
        return frame;
    }

    private void OpenControlFrame(FrameView frame, View focus)
    {
        ApplyTheme(frame, preferences: true); _overlay = frame; _window!.Add(frame); focus.SetFocus();
    }

    private Button ControlBack(FrameView frame, Action? back = null)
    {
        void Close() { CloseOverlay(); back?.Invoke(); }
        var button = new Button { Id = "security-back", X = 2, Y = Pos.AnchorEnd(2), Text = SecurityPref("back") };
        button.Accepted += (_, _) => Close();
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; Close(); } };
        frame.Add(button);
        return button;
    }

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
        else { CloseOverlay(); ReturnToExecutionPermissions(); }
    }

    internal void ShowSecurityPreferences(string queryName)
    {
        if (queryName == "tools_security") { _securityReturnToPreferences = true; ReturnToExecutionPermissions(); }
    }

    private bool _securityReturnToPreferences;

    internal void ShowExecutionPermissions()
    {
        if (_window is null || _overlay is not null) return;
        _securityReturnToPreferences = false;
        ReturnToExecutionPermissions();
    }

    internal void CloseExecutionPermissions()
    {
        CloseOverlay();
        if (_securityReturnToPreferences) ShowPreferences("tools_security", categoryPage: true);
    }

    internal void ReturnToExecutionPermissions()
    {
        if (_window is null || _overlay is not null) return;
        var host = _securityPreferencesOverride ?? SecurityPreferencesHost.Create(Environment.CurrentDirectory);
        SecurityPreferencesSnapshot snapshot;
        try { snapshot = host.Read(); }
        catch (Exception) { ShowMessage(SecurityPref("unavailable")); return; }
        var policy = ReadSecurityPolicyPresentation();
        var defaults = ReadSecurityDefaultPresentation();
        var frame = ControlFrame(ControlText("permissions"), "security-preferences");
        frame.Add(new Label { Id = "security-summary", X = 2, Y = 1, Width = Dim.Fill(2), Height = 3,
            Text = ControlText("permission_layers") });
        var buttons = new List<Button>();
        void Row(string id, int y, string text, Action action)
        {
            var button = new Button { Id = id, X = 2, Y = y, Width = Dim.Fill(2), Text = text,
                TextAlignment = Alignment.Start };
            button.Accepted += (_, _) => { CloseOverlay(); action(); };
            frame.Add(button); buttons.Add(button);
        }
        Row("session-security-change", 5, ControlText("this_session") + ": "
            + (policy.Category is null ? "—" : SecurityPref("category." + policy.Category)) + " ›", () => ShowSecurityCeiling(false));
        Row("session-security-default-change", 7, ControlText("new_sessions") + ": "
            + (defaults.Revision == "unavailable" ? "—" : SecurityPref("category." + defaults.Category)) + " ›", () => ShowSecurityCeiling(true));
        Row("security-profile-open", 9, SecurityPref("profile") + ": "
            + UiCompletionResources.EnumLabel("security.profile", snapshot.Profile, _locale) + " ›", () => ShowPermissionProfile(host, snapshot));
        Row("security-rules-open", 11, SecurityPref("rules_heading") + " · " + snapshot.Rules.Count + " ›", () => ShowPermissionRules(host, snapshot));
        Row("security-workspace-open", 13, SecurityPref("trust_heading") + " / " + ControlText("saved_grants") + " ›", () => ShowWorkspacePermissions(host, snapshot));
        ControlBack(frame, CloseExecutionPermissions);
        OpenControlFrame(frame, buttons[0]);
    }

    private void ShowSecurityCeiling(bool forNewSessions)
    {
        var policy = ReadSecurityPolicyPresentation();
        var defaults = ReadSecurityDefaultPresentation();
        var current = forNewSessions ? defaults.Category : policy.Category;
        var available = _client is ITrustedUserActionClient && (forNewSessions
            ? defaults.Revision != "unavailable" : policy.SessionId is not null && policy.Revision is not null);
        var frame = ControlFrame(SecurityPref(forNewSessions ? "default_ceiling" : "session_ceiling"), "security-ceiling");
        var categories = new[] { "ObserveOnly", "PatchOnly", "ScopedCoder", "FullAgent" };
        var list = new ListView { Id = "security-ceiling-options", X = 2, Y = 4, Width = Dim.Fill(2), Height = 4, ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(categories.Select(value =>
            (value == current ? "● " : "○ ") + SecurityPref("category." + value))));
        list.SelectedItem = Math.Max(0, Array.IndexOf(categories, current));
        frame.Add(new Label { Id = "session-security-status", X = 2, Y = 1, Width = Dim.Fill(2), Height = 2,
            Text = !available ? SecurityPref("session_security_unavailable") : ControlText("current") + ": " + SecurityPref("category." + current)
                + (forNewSessions ? "" : " · " + SecurityPref("policy_revision") + " " + policy.Revision) });
        var detail = new Label { Id = "security-ceiling-help", X = 2, Y = 9, Width = Dim.Fill(2), Height = 3 };
        void Describe() => detail.Text = ControlText("ceiling." + categories[list.SelectedItem ?? 0]);
        list.ValueChanged += (_, _) => Describe(); Describe();
        frame.Add(list, detail, new Label { X = 2, Y = 13, Width = Dim.Fill(2), Height = 3,
            Text = ControlText(forNewSessions ? "default_scope" : "session_scope") });
        ControlBack(frame, ReturnToExecutionPermissions);
        var apply = new Button { Id = "security-ceiling-apply", X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(2), Text = ControlText("apply"), Enabled = available };
        apply.Accepted += (_, _) =>
        {
            var chosen = categories[list.SelectedItem ?? 0];
            ConfirmSecurityChange(frame.Title.ToString(), SecurityPref(forNewSessions ? "default_security_confirm" : "session_security_confirm")
                + "\n" + SecurityPref("category." + chosen), () =>
            {
                if (_client is not ITrustedUserActionClient trusted) return;
                var fields = JsonObj.Field("cmd", forNewSessions ? "session.security.default.set" : "session.security.set")
                    + "," + JsonObj.Field("category", chosen) + "," + JsonObj.Field("expectedRevision", forNewSessions
                        ? defaults.Revision : policy.Revision!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!forNewSessions) fields += "," + JsonObj.Field("sessionId", policy.SessionId!);
                try
                {
                    var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + fields + "}"), CancellationToken.None);
                    CloseOverlay();
                    if (ack.Status == "ok") { InvalidatePermissionLevel(); ReturnToExecutionPermissions(); }
                    else ShowMessage(_localization.ResolveWire(ack.Error ?? "securityPreferences.stale"));
                }
                catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("save_failed")); }
            });
        };
        frame.Add(apply); OpenControlFrame(frame, list);
    }

    private void SavePermissions(ISecurityPreferencesFacade host, SecurityPreferencesSnapshot snapshot, string profile,
        IReadOnlyDictionary<string, string> rules)
    {
        try { _ = host.SaveUserPermissions(snapshot.Revision, profile, rules); InvalidatePermissionLevel(); CloseOverlay(); ReturnToExecutionPermissions(); }
        catch (InvalidOperationException) { CloseOverlay(); ShowMessage(SecurityPref("stale")); }
        catch (ArgumentException) { CloseOverlay(); ShowMessage(SecurityPref("invalid_values")); }
        catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("save_failed")); }
    }

    private void ShowPermissionProfile(ISecurityPreferencesFacade host, SecurityPreferencesSnapshot snapshot)
    {
        var frame = ControlFrame(SecurityPref("level_heading"), "security-profile-page");
        var levels = new[] { ("Conservative", "level_restrict"), ("Balanced", "level_ask"), ("Autonomous", "level_free") };
        var selected = snapshot.Profile;
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(2), Height = 2, Text = ControlText("profile_scope") });
        var detail = new Label { Id = "omni-detail", X = 2, Y = 10, Width = Dim.Fill(2), Height = 4 };
        var buttons = new List<Button>();
        void Sync()
        {
            for (var i = 0; i < levels.Length; i++) buttons[i].Text = (selected == levels[i].Item1 ? "● " : "○ ") + SecurityPref(levels[i].Item2);
            detail.Text = SecurityPref(levels.Single(level => level.Item1 == selected).Item2 + "_help");
        }
        foreach (var (value, key) in levels)
        {
            var button = new Button { Id = "security-level-" + value.ToLowerInvariant(), X = 2, Y = 4 + buttons.Count * 2, Width = Dim.Fill(2),
                TextAlignment = Alignment.Start };
            button.Accepted += (_, _) => { selected = value; Sync(); };
            buttons.Add(button); frame.Add(button);
        }
        frame.Add(detail); Sync(); ControlBack(frame, ReturnToExecutionPermissions);
        var save = new Button { Id = "security-save", X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(2), Text = ControlText("apply") };
        save.Accepted += (_, _) => ConfirmSecurityChange(SecurityPref("permissions_title"), SecurityPref("permissions_confirm")
            + "\n" + UiCompletionResources.EnumLabel("security.profile", selected, _locale),
            () => SavePermissions(host, snapshot, selected, snapshot.Rules.ToDictionary(row => row.ToolId, row => row.Decision, StringComparer.Ordinal)));
        frame.Add(save); OpenControlFrame(frame, buttons[0]);
    }

    private void ShowPermissionRules(ISecurityPreferencesFacade host, SecurityPreferencesSnapshot snapshot)
    {
        var frame = ControlFrame(SecurityPref("rules_heading"), "security-rules-page");
        var ids = ReadKnownToolIds().Concat(snapshot.Rules.Select(row => row.ToolId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var list = new ListView { Id = "security-rules", X = 2, Y = 3, Width = Dim.Fill(2), Height = 5, ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(ids.Select(id => id + " · "
            + (snapshot.Rules.FirstOrDefault(row => row.ToolId == id) is { } rule
                ? UiCompletionResources.EnumLabel("security.decision", rule.Decision, _locale) : ControlText("inherit")))));
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(2), Height = 2, Text = SecurityPref("rule_note") }, list);
        var tool = new TextField { Id = "security-tool-id", X = 2, Y = 9, Width = Dim.Fill(2), Text = ids.FirstOrDefault() ?? "" };
        var decisions = new[] { "inherit", "Allow", "Ask", "Deny" };
        var decision = new ListView { Id = "security-decision", X = 2, Y = 11, Width = Dim.Fill(2), Height = 4, ShowMarks = false };
        decision.SetSource(new ObservableCollection<string>(decisions.Select(value => value == "inherit" ? ControlText("inherit") : UiCompletionResources.EnumLabel("security.decision", value, _locale))));
        void SelectRule()
        {
            if (list.SelectedItem is not int index || index < 0 || index >= ids.Length) return;
            tool.Text = ids[index];
            var value = snapshot.Rules.FirstOrDefault(row => row.ToolId == ids[index])?.Decision ?? "inherit";
            decision.SelectedItem = Array.IndexOf(decisions, value);
        }
        list.ValueChanged += (_, _) => SelectRule(); SelectRule();
        var notice = new Label { Id = "security-rule-notice", X = 2, Y = 16, Width = Dim.Fill(2), Height = 1 };
        frame.Add(new Label { X = 2, Y = 8, Width = Dim.Fill(2), Height = 1, Text = SecurityPref("tool_id") },
            tool, decision, notice); ControlBack(frame, ReturnToExecutionPermissions);
        var save = new Button { Id = "security-save", X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(2), Text = ControlText("apply") };
        save.Accepted += (_, _) =>
        {
            var id = tool.Text.ToString().Trim();
            if (id.Length == 0) { notice.Text = SecurityPref("tool_id"); return; }
            var chosen = decisions[decision.SelectedItem ?? 0];
            var rules = snapshot.Rules.ToDictionary(row => row.ToolId, row => row.Decision, StringComparer.Ordinal);
            if (chosen == "inherit") rules.Remove(id); else rules[id] = chosen;
            ConfirmSecurityChange(SecurityPref("permissions_title"), id + " → " + (chosen == "inherit" ? ControlText("inherit")
                : UiCompletionResources.EnumLabel("security.decision", chosen, _locale)) + "\n" + ControlText("profile_scope"),
                () => SavePermissions(host, snapshot, snapshot.Profile, rules));
        };
        frame.Add(save); OpenControlFrame(frame, ids.Length > 0 ? list : tool);
    }

    private void ShowWorkspacePermissions(ISecurityPreferencesFacade host, SecurityPreferencesSnapshot snapshot)
    {
        var frame = ControlFrame(SecurityPref("trust_heading"), "security-workspace-page");
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(2), Height = 2,
            Text = SecurityPref("trust_state") + ": " + SecurityPref(snapshot.WorkspaceTrusted ? "trusted" : "untrusted") });
        var trust = new Button { Id = "security-trust", X = 2, Y = 3,
            Text = SecurityPref(snapshot.WorkspaceTrusted ? "revoke_trust" : "trust_workspace") };
        trust.Accepted += (_, _) => ConfirmSecurityChange(SecurityPref("trust_title"), SecurityPref("trust_confirm"), () =>
        {
            try { _ = host.SetWorkspaceTrust(snapshot.WorkspaceTrusted, !snapshot.WorkspaceTrusted); CloseOverlay(); ReturnToExecutionPermissions(); }
            catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("stale")); }
        });
        var list = new ListView { Id = "security-grants", X = 2, Y = 7, Width = Dim.Fill(2), Height = 4, ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(snapshot.Grants.Select(grant => grant.ToolId + " · "
            + UiCompletionResources.EnumLabel("lifetime", grant.Lifetime, _locale) + " · " + grant.Id)));
        var revoke = new Button { Id = "security-grant-revoke", X = 2, Y = 12, Text = SecurityPref("revoke_grant"), Enabled = snapshot.Grants.Count > 0 };
        revoke.Accepted += (_, _) =>
        {
            if (list.SelectedItem is not int index || index < 0 || index >= snapshot.Grants.Count) return;
            var grant = snapshot.Grants[index];
            ConfirmSecurityChange(SecurityPref("grant_title"), SecurityPref("grant_confirm") + "\n" + grant.ToolId + "\n" + grant.Id, () =>
            {
                try { _ = host.RevokeGrant(grant.Id); CloseOverlay(); ReturnToExecutionPermissions(); }
                catch (Exception) { CloseOverlay(); ShowMessage(SecurityPref("stale")); }
            });
        };
        frame.Add(trust, new Label { X = 2, Y = 5, Width = Dim.Fill(2), Text = SecurityPref("grants") }, list, revoke,
            new Label { X = 2, Y = 14, Width = Dim.Fill(2), Height = 2, Text = SecurityPref("help") });
        ControlBack(frame, ReturnToExecutionPermissions); OpenControlFrame(frame, trust);
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
        var frame = OverlayFrame(title, 14); frame.Id = "security-preferences-confirm";
        _cancelSecurityConfirmation = () =>
        {
            CloseOverlay();
            if (draft is null) { ShowSecurityPreferences("tools_security"); return; }
            _retiredOverlays.Remove(draft);
            _overlay = draft; _window.Add(draft); draft.SetFocus();
        };
        frame.Add(MenuIndent.Help(description, 2, 7, "security-confirm-help"));
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

    private (string? SessionId, long? Revision, string? Category, string? Provenance) ReadSecurityPolicyPresentation()
    {
        try
        {
            var json = _client.Query("sessionSecurity", CancellationToken.None)?.Json;
            if (string.IsNullOrWhiteSpace(json)) return default;
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return default;
            var sessionId = root.TryGetProperty("sessionId", out var id) ? id.GetString() : null;
            var category = root.TryGetProperty("category", out var categoryValue) ? categoryValue.GetString() : null;
            var provenance = root.TryGetProperty("provenance", out var provenanceValue) ? provenanceValue.GetString() : null;
            var revision = root.TryGetProperty("revision", out var revisionValue) && revisionValue.TryGetInt64(out var parsed)
                ? parsed : (long?)null;
            if (!Guid.TryParse(sessionId, out _) || category is not ("ObserveOnly" or "PatchOnly" or "ScopedCoder" or "FullAgent")
                || provenance is null || revision is null or < 1) return default;
            return (sessionId, revision, category, provenance);
        }
        catch (Exception) { return default; }
    }

    private (string Category, string Revision, bool IsConfigured) ReadSecurityDefaultPresentation()
    {
        try
        {
            var json = _client.Query("sessionSecurityDefault", CancellationToken.None)?.Json;
            if (string.IsNullOrWhiteSpace(json)) return ("ObserveOnly", "unavailable", false);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            var category = root.TryGetProperty("category", out var categoryValue) ? categoryValue.GetString() : null;
            var revision = root.TryGetProperty("revision", out var revisionValue) ? revisionValue.GetString() : null;
            var configured = root.TryGetProperty("isConfigured", out var configuredValue)
                && configuredValue.ValueKind == System.Text.Json.JsonValueKind.True;
            if (category is not ("ObserveOnly" or "PatchOnly" or "ScopedCoder" or "FullAgent")
                || string.IsNullOrWhiteSpace(revision)) return ("ObserveOnly", "unavailable", false);
            return (category, revision, configured);
        }
        catch (Exception) { return ("ObserveOnly", "unavailable", false); }
    }
}
