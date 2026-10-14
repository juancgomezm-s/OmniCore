namespace OmniCore.Cli;

using System.Collections.ObjectModel;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiApp
{
    private const string SessionPreferencesOverlayId = "session-preferences";
    private string _sessionPreferencesQuery = "";
    private SessionPreferencesData? _sessionPreferencesData;
    private int _sessionPreferencesSelectedIndex;
    private readonly List<SessionPreferenceRow> _sessionPreferencesRows = [];

    private sealed record SessionPreferenceRow(string Label, string Details, object? Value);

    private string SessionPref(string key) => _localization.Resolve("sessionPreferences." + key);

    private bool HandleSessionPreferencesEscape()
    {
        if (_overlay?.Id == SessionPreferencesOverlayId)
        {
            ReturnToPreferenceCategory();
            return true;
        }
        if (_overlay?.Id is "session-preferences-confirm" or "session-preferences-add-memory")
        {
            RenderSessionPreferences(_sessionPreferencesSelectedIndex);
            return true;
        }
        return false;
    }

    /// <summary>Shared navigation seam for context, project extensions/skills, and Session memory.</summary>
    internal void ShowSessionPreferences(string queryName)
    {
        if (_window is null || _overlay is not null
            || queryName is not ("context" or "extensions" or "skills" or "memory.session")) return;
        _sessionPreferencesQuery = queryName;
        var json = _client.Query(queryName, CancellationToken.None)?.Json;
        _sessionPreferencesData = json is null ? SessionPreferencesData.Unavailable(queryName)
            : SessionPreferencesPresentation.Decode(queryName, json);
        BuildSessionPreferenceRows();
        RenderSessionPreferences();
    }

    private void BuildSessionPreferenceRows()
    {
        _sessionPreferencesRows.Clear();
        if (_sessionPreferencesData is not { Available: true } data) return;
        switch (data.QueryName)
        {
            case "context":
                if (data.Context is not { } context) return;
                _sessionPreferencesRows.Add(new(SessionPref("context.snapshot"), ContextSummary(context), context));
                foreach (var item in context.Items)
                    _sessionPreferencesRows.Add(new(SessionPreferencesPresentation.SafeText(
                        string.Join(" · ", new[] { EnumLabel("context.kind", item.Kind), item.Category, item.Contributor }.Where(value => !string.IsNullOrWhiteSpace(value))), 140),
                        ContextItemDetails(item), item));
                foreach (var diagnostic in context.Diagnostics)
                    _sessionPreferencesRows.Add(new(SessionPreferencesPresentation.SafeText(
                        string.Join(" · ", new[] { EnumLabel("context.decision", diagnostic.Decision), diagnostic.ItemId }.Where(value => !string.IsNullOrWhiteSpace(value))), 140),
                        ContextDiagnosticDetails(diagnostic), diagnostic));
                break;
            case "extensions":
                foreach (var extension in data.Extensions)
                    _sessionPreferencesRows.Add(new(ExtensionName(extension)
                        + (string.IsNullOrWhiteSpace(extension.Id) ? "" : " · " + SessionPreferencesPresentation.SafeText(extension.Id, 80)),
                        ExtensionDetails(extension), extension));
                break;
            case "skills":
                foreach (var skill in data.Skills)
                    _sessionPreferencesRows.Add(new(SessionPreferencesPresentation.SafeText(skill.DisplayName, 100)
                        + " · " + SessionPreferencesPresentation.SafeText(skill.Id, 80), SkillDetails(skill), skill));
                break;
            case "memory.session":
                foreach (var memory in data.Memory)
                    _sessionPreferencesRows.Add(new(SessionPreferencesPresentation.SafeText(memory.Content, 112), MemoryDetails(memory), memory));
                foreach (var candidate in data.MemoryCandidates ?? [])
                    _sessionPreferencesRows.Add(new(SessionPref("memory.candidate") + " · "
                        + SessionPreferencesPresentation.SafeText(candidate.Content, 96), MemoryCandidateDetails(candidate), candidate));
                break;
        }
    }

    private void RenderSessionPreferences(int selectedIndex = 0, string? notice = null)
    {
        if (_window is null) return;
        CloseOverlay();
        var data = _sessionPreferencesData;
        var title = SessionPref(_sessionPreferencesQuery switch
        {
            "context" => "title.context", "extensions" => "title.extensions",
            "skills" => "title.skills", _ => "title.memory",
        });
        var frame = OverlayFrame(title, _window.Frame.Height - 3);
        frame.Id = SessionPreferencesOverlayId;
        frame.Width = Dim.Percent(92); frame.Height = Dim.Fill(2);
        var wide = _window.Frame.Width >= 100;
        var listWidth = wide ? Dim.Percent(43) : Dim.Fill(4);
        var listHeight = wide ? Dim.Fill(7) : Dim.Percent(30);
        var list = new ListView { Id = "session-preferences-list", X = 2, Y = 2,
            Width = listWidth, Height = listHeight, ShowMarks = false, CanFocus = true };
        var labels = _sessionPreferencesRows.Select(row => row.Label).ToList();
        if (!(data?.Available ?? false)) labels = [SessionPref("unavailable")];
        else if (labels.Count == 0) labels = [SessionPref(_sessionPreferencesQuery == "context" ? "context.none" : "empty")];
        list.SetSource(new ObservableCollection<string>(labels));
        list.SelectedItem = Math.Clamp(selectedIndex, 0, Math.Max(0, labels.Count - 1));
        _sessionPreferencesSelectedIndex = list.SelectedItem is int initial ? initial : 0;

#pragma warning disable CS0618 // Existing pinned Terminal.Gui read-only, scrollable text control; no editor dependency.
        var details = new TextView { Id = "session-preferences-details",
            X = wide ? Pos.Percent(46) : 2,
            Y = wide ? 2 : Pos.Bottom(list) + 1,
            Width = wide ? Dim.Fill(4) : Dim.Fill(4),
            Height = wide ? Dim.Fill(7) : Dim.Fill(8),
            ReadOnly = true, WordWrap = true,
            Text = DetailAt(list.SelectedItem is int selected ? selected : 0) };
#pragma warning restore CS0618
        var action = new Button { Id = "session-preferences-action", X = 2, Y = Pos.AnchorEnd(6),
            Width = Dim.Percent(44), TextAlignment = Alignment.Start, Text = "", Visible = false };
        var secondary = new Button { Id = "session-preferences-secondary", X = Pos.Percent(48), Y = Pos.AnchorEnd(6),
            Width = Dim.Percent(44), TextAlignment = Alignment.Start, Text = "", Visible = false };
        var addMemory = new Button { Id = "session-preferences-add", X = 2, Y = Pos.AnchorEnd(6),
            Width = Dim.Percent(44), TextAlignment = Alignment.Start, Text = SessionPref("memory.add"),
            Visible = _sessionPreferencesQuery == "memory.session", Enabled = data?.SessionId is not null };
        addMemory.X = Pos.Percent(48);
        var status = new Label { Id = "session-preferences-notice", X = 2, Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(4), Height = 1, Text = notice ?? "" };
        var back = new Button { Id = "session-preferences-back", X = 2, Y = Pos.AnchorEnd(2), Text = SessionPref("back") };

        void SyncSelection()
        {
            var index = list.SelectedItem is int selected && selected >= 0 && selected < _sessionPreferencesRows.Count ? selected : -1;
            details.Text = DetailAt(index);
            action.Visible = false; secondary.Visible = false;
            action.Enabled = false; secondary.Enabled = false;
            if (index < 0 || data is null) return;
            var value = _sessionPreferencesRows[index].Value;
            if (value is ExtensionPresentation extension && _sessionPreferencesQuery == "extensions")
            {
                var eligible = (extension.Scope == "User" || data.WorkspaceTrusted == true)
                    && !string.IsNullOrEmpty(data.SessionId)
                    && !string.IsNullOrWhiteSpace(extension.Id) && !extension.AmbiguousId
                    && (extension.Status is "Ready" or "ConsentRequired");
                if (extension.ConsentedCapabilities.Count > 0)
                {
                    action.Text = SessionPref("extension.revoke"); action.Visible = true; action.Enabled = eligible;
                }
                if (extension.RequestedCapabilities.Count > 0 && eligible)
                {
                    secondary.Text = SessionPref("extension.grant"); secondary.Visible = true; secondary.Enabled = true;
                }
            }
            else if (value is SkillPresentation skill && _sessionPreferencesQuery == "skills")
            {
                if (skill.State == "Activated")
                {
                    action.Text = SessionPref("skill.release"); action.Visible = true; action.Enabled = data.SessionId is not null;
                }
                else if (data.WorkspaceTrusted == true && skill.State is "Discovered" or "Eligible" or "Released")
                {
                    action.Text = SessionPref("skill.activate"); action.Visible = true; action.Enabled = data.SessionId is not null;
                }
            }
            else if (value is MemoryPresentation memory && _sessionPreferencesQuery == "memory.session")
            {
                action.Text = SessionPref("memory.forget"); action.Visible = true; action.Enabled = data.SessionId is not null;
            }
            else if (value is MemoryCandidatePresentation candidate && _sessionPreferencesQuery == "memory.session")
            {
                if (candidate.Decision == "ask")
                {
                    action.Text = SessionPref("memory.promote"); action.Visible = true; action.Enabled = data.SessionId is not null;
                    secondary.Text = SessionPref("memory.reject"); secondary.Visible = true; secondary.Enabled = data.SessionId is not null;
                }
            }
        }

        list.ValueChanged += (_, _) =>
        {
            if (list.SelectedItem is int selected) _sessionPreferencesSelectedIndex = selected;
            SyncSelection();
        };
        list.Accepted += (_, args) => { args.Handled = true; details.SetFocus(); };
        action.Accepted += (_, _) => ExecutePrimaryAction(list.SelectedItem is int i ? i : -1, action);
        secondary.Accepted += (_, _) => ExecuteGrantAction(list.SelectedItem is int i ? i : -1, secondary);
        addMemory.Accepted += (_, _) => ShowAddSessionMemory();
        back.Accepted += (_, _) => ReturnToPreferenceCategory();
        frame.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc) { key.Handled = true; ReturnToPreferenceCategory(); }
        };
        SyncSelection();
        frame.Add(list, details, action, secondary, addMemory, status, back,
            new Label { Id = "session-preferences-help", X = 2, Y = Pos.AnchorEnd(3), Width = Dim.Fill(4), Height = 1,
                Text = SessionPref("select") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); list.SetFocus();
    }

    private string DetailAt(int index)
    {
        if (_sessionPreferencesData is not { Available: true } data) return SessionPref("unavailable");
        var lines = new List<string>();
        if (data.QueryName != "context")
        {
            if (data.SessionId is { Length: > 0 } session) lines.Add(SessionPref("session") + ": " + session);
            else lines.Add(SessionPref("no_session"));
        }
        if (data.WorkspaceTrusted is { } trusted)
            lines.Add(SessionPref("trust") + ": " + SessionPref(trusted ? "trusted" : "untrusted"));
        if (index < 0 || index >= _sessionPreferencesRows.Count)
            lines.Add(SessionPref(_sessionPreferencesRows.Count == 0 ? "empty" : "select"));
        else lines.Add(_sessionPreferencesRows[index].Details);
        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private string ContextSummary(ContextSnapshotPresentation snapshot) => string.Join(Environment.NewLine,
        SessionPref("context.snapshot"),
        SessionPref("snapshot_id") + ": " + Safe(snapshot.SnapshotId),
        SessionPref("context.tokens") + ": " + (snapshot.TokenCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? SessionPref("unknown")),
        SessionPref("context.accuracy") + ": " + (snapshot.TokenAccuracy is { } accuracy ? EnumLabel("token_accuracy", accuracy) : SessionPref("unknown")),
        SessionPref("context.budget") + ": " + Safe(snapshot.TokenBudget ?? SessionPref("unknown")),
        SessionPref("context.overflow") + ": " + Bool(snapshot.Overflowed),
        SessionPref("context.items") + ": " + snapshot.Items.Count,
        SessionPref("context.diagnostics") + ": " + snapshot.Diagnostics.Count);

    private string ContextItemDetails(ContextItemPresentation item) => string.Join(Environment.NewLine,
        Safe(item.Id), SessionPref("item.kind") + ": " + EnumLabel("context.kind", item.Kind),
        SessionPref("item.source") + ": " + (item.Source is { } source
            ? UiCompletionResources.EnumLabel("source", source, _locale) : SessionPref("unknown")),
        SessionPref("item.scope") + ": " + EnumLabel("scope", item.Scope ?? SessionPref("unknown")),
        SessionPref("item.tokens") + ": " + (item.Tokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? SessionPref("unknown")),
        SessionPref("item.accuracy") + ": " + (item.TokenAccuracy is { } accuracy ? EnumLabel("token_accuracy", accuracy) : SessionPref("unknown")),
        SessionPref("item.priority") + ": " + (item.Priority is { } priority
            ? UiCompletionResources.EnumLabel("contextPriority", priority, _locale) : SessionPref("unknown")),
        SessionPref("item.sensitive") + ": " + Bool(item.Sensitive),
        item.References.Count == 0 ? "" : SessionPref("item.refs") + ": " + Safe(string.Join(", ", item.References), 512));

    private string ContextDiagnosticDetails(ContextDiagnosticPresentation item) => string.Join(Environment.NewLine,
        Safe(item.ItemId), SessionPref("item.reason") + ": " + (item.Reason is { } reason ? Safe(reason) : EnumLabel("context.decision", item.Decision)),
        SessionPref("item.source") + ": " + (item.Source is { } source
            ? UiCompletionResources.EnumLabel("source", source, _locale) : SessionPref("unknown")),
        SessionPref("item.scope") + ": " + EnumLabel("scope", item.Scope ?? SessionPref("unknown")),
        SessionPref("item.tokens") + ": " + (item.Tokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? SessionPref("unknown")),
        SessionPref("item.sensitive") + ": " + Bool(item.Sensitive));

    private string ExtensionName(ExtensionPresentation extension)
    {
        var text = extension.TextFor(_locale)?.DisplayName;
        if (!string.IsNullOrWhiteSpace(text)) return Safe(text, 100);
        return _localization.Resolve("uiCompletion.external.originalName") + ": " + Safe(extension.DisplayName, 100);
    }

    private string ExtensionDetails(ExtensionPresentation extension)
    {
        var lines = new List<string>
        {
            ExtensionName(extension),
            string.IsNullOrWhiteSpace(extension.Id) ? "" : SessionPref("id") + ": " + Safe(extension.Id),
            SessionPref("extension.version") + ": " + Safe(extension.Version ?? SessionPref("unknown")),
            SessionPref("scope") + ": " + EnumLabel("scope", extension.Scope),
            SessionPref("extension.status") + ": " + EnumLabel("extension.state", extension.Status),
            SessionPref("extension.requested") + ": " + (extension.RequestedCapabilities.Count == 0 ? SessionPref("unknown") : Safe(string.Join(", ", extension.RequestedCapabilities), 512)),
            SessionPref("extension.consented") + ": " + (extension.ConsentedCapabilities.Count == 0 ? SessionPref("no") : Safe(string.Join(", ", extension.ConsentedCapabilities), 512)),
            SessionPref("extension.hash") + ":", SessionPref("manifest_hash") + " " + Safe(extension.ManifestSha256, 128),
            SessionPref("package_hash") + " " + Safe(extension.PackageSha256, 128),
            extension.AmbiguousId ? SessionPref("extension.ambiguous") : "",
        };
        var description = extension.TextFor(_locale)?.Description;
        if (!string.IsNullOrWhiteSpace(description)) lines.Insert(1, Safe(description, 1024));
        else if (extension.UiResources?.Values.Select(value => value.Description)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) is { } originalDescription)
            lines.Insert(1, _localization.Resolve("uiCompletion.external.originalDescription") + ": " + Safe(originalDescription, 1024));
        return string.Join(Environment.NewLine, lines);
    }

    private string SkillDetails(SkillPresentation skill) => string.Join(Environment.NewLine,
        Safe(skill.DisplayName), SessionPref("id") + ": " + Safe(skill.Id), SessionPref("skill.version") + ": " + Safe(skill.Version ?? SessionPref("unknown")),
        SessionPref("skill.state") + ": " + EnumLabel("skill.state", skill.State), SessionPref("scope") + ": " + EnumLabel("scope", skill.Scope),
        SessionPref("trust_level") + ": " + EnumLabel("trust", skill.Trust),
        SessionPref("skill.tools") + ": " + (skill.Tools.Count == 0 ? SessionPref("unknown") : Safe(string.Join(", ", skill.Tools), 512)),
        SessionPref("skill.model_invocable") + ": " + Bool(skill.ModelInvocable),
        SessionPref("skill.sealed") + ": " + Bool(skill.Sealed), SessionPref("manifest_hash") + " " + Safe(skill.ManifestSha256, 128));

    private string MemoryDetails(MemoryPresentation memory) => string.Join(Environment.NewLine,
        SessionPref("id") + ": " + Safe(memory.Id), SessionPref("memory.content") + ":", SafeMultiline(memory.Content),
        SessionPref("memory.kind") + ": " + EnumLabel("memory.kind", memory.Kind),
        SessionPref("memory.source") + ": " + EnumLabel("memory.source", memory.Source), SessionPref("memory.created") + ": " + Safe(memory.CreatedAt ?? SessionPref("unknown")),
        SessionPref("importance") + ": " + (memory.Importance?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? SessionPref("unknown")),
        SessionPref("confidence") + ": " + (memory.Confidence?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? SessionPref("unknown")),
        SessionPref("memory.tags") + ": " + (memory.Tags.Count == 0 ? SessionPref("unknown") : Safe(string.Join(", ", memory.Tags), 512)));

    private string MemoryCandidateDetails(MemoryCandidatePresentation candidate) => string.Join(Environment.NewLine,
        SessionPref("memory.candidate") + ": " + Safe(candidate.Id),
        SessionPref("memory.content") + ":", SafeMultiline(candidate.Content),
        SessionPref("memory.kind") + ": " + EnumLabel("memory.kind", candidate.Kind),
        SessionPref("memory.decision") + ": " + EnumLabel("memory.candidate.state", candidate.Decision),
        SessionPref("memory.source") + ": " + EnumLabel("memory.origin", candidate.Origin),
        candidate.ConflictMemoryId is null ? "" : SessionPref("memory.conflict") + ": " + Safe(candidate.ConflictMemoryId),
        SessionPref("memory.created") + ": " + Safe(candidate.ProposedAt ?? SessionPref("unknown")));

    private string Safe(string value, int maximumLength = 240) => SessionPreferencesPresentation.SafeText(value, maximumLength);
    private string SafeMultiline(string value) => new string(value.Select(character =>
        char.IsControl(character) && character is not ('\r' or '\n' or '\t') ? ' ' : character).ToArray());
    private string Bool(bool? value) => value switch { true => SessionPref("yes"), false => SessionPref("no"), _ => SessionPref("unknown") };

    private string EnumLabel(string family, string value)
    {
        var key = "sessionPreferences.enum." + family + "." + value.ToLowerInvariant();
        return SessionPreferencesResources.TryTranslate(_locale, key, out var translated) ? translated : Safe(value, 100);
    }

    private void ExecutePrimaryAction(int index, Button primary)
    {
        if (!primary.Visible || !primary.Enabled || index < 0 || index >= _sessionPreferencesRows.Count
            || _sessionPreferencesData is not { } data) return;
        var value = _sessionPreferencesRows[index].Value;
        if (value is ExtensionPresentation extension)
        {
            if (primary.Text == SessionPref("extension.revoke"))
                ConfirmSessionChange(SessionPref("title.extensions"), ExtensionDetails(extension) + Environment.NewLine
                    + SessionPref("extension.confirm_revoke"),
                    () => SendExtensionConsent(data, extension, grant: false, []));
        }
        else if (value is SkillPresentation skill)
        {
            var activate = primary.Text == SessionPref("skill.activate");
            ConfirmSessionChange(SessionPref("title.skills"), SkillDetails(skill) + Environment.NewLine
                + SessionPref(activate ? "skill.confirm_activate" : "skill.confirm_release"),
                () => SendSkillChange(data, skill, activate));
        }
        else if (value is MemoryPresentation memory)
            ConfirmSessionChange(SessionPref("title.memory"), MemoryDetails(memory) + Environment.NewLine
                + SessionPref("memory.confirm_forget"),
                () => SendMemoryForget(data, memory));
        else if (value is MemoryCandidatePresentation candidate && primary.Text == SessionPref("memory.promote"))
            ConfirmSessionChange(SessionPref("title.memory"), MemoryCandidateDetails(candidate) + Environment.NewLine
                + SessionPref("memory.confirm_promote"), () => SendMemoryCandidate(data, "memory.session.promote", candidate));
    }

    private void ExecuteGrantAction(int index, Button secondary)
    {
        if (!secondary.Visible || !secondary.Enabled || index < 0 || index >= _sessionPreferencesRows.Count
            || _sessionPreferencesData is not { } data) return;
        if (_sessionPreferencesRows[index].Value is MemoryCandidatePresentation candidate
            && secondary.Text == SessionPref("memory.reject"))
        {
            ConfirmSessionChange(SessionPref("title.memory"), MemoryCandidateDetails(candidate) + Environment.NewLine
                + SessionPref("memory.confirm_reject"), () => SendMemoryCandidate(data, "memory.session.reject", candidate));
            return;
        }
        if (_sessionPreferencesRows[index].Value is not ExtensionPresentation extension) return;
        ConfirmSessionChange(SessionPref("title.extensions"), ExtensionDetails(extension) + Environment.NewLine
            + SessionPref("extension.confirm_grant") + Environment.NewLine
            + string.Join(", ", extension.RequestedCapabilities.Select(cap => Safe(cap, 128))),
            () => SendExtensionConsent(data, extension, grant: true, extension.RequestedCapabilities));
    }

    private void ConfirmSessionChange(string title, string description, Action confirm)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(title, _window.Frame.Height - 3);
        frame.Id = "session-preferences-confirm"; frame.Width = Dim.Percent(85); frame.Height = Dim.Percent(55);
#pragma warning disable CS0618 // Same read-only control as the details page.
        var message = new TextView { Id = "session-preferences-confirm-text", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = Dim.Fill(5), ReadOnly = true, WordWrap = true, Text = description };
#pragma warning restore CS0618
        var yes = new Button { Id = "session-preferences-confirm-yes", X = 2, Y = Pos.AnchorEnd(2), Text = SessionPref("confirm") };
        var no = new Button { Id = "session-preferences-confirm-no", X = Pos.Right(yes) + 2, Y = Pos.AnchorEnd(2), Text = SessionPref("cancel") };
        yes.Accepted += (_, _) => confirm();
        no.Accepted += (_, _) => RenderSessionPreferences(CurrentSessionPreferenceIndex());
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; RenderSessionPreferences(CurrentSessionPreferenceIndex()); } };
        frame.Add(message, yes, no); ApplyTheme(frame); _overlay = frame; _window.Add(frame); no.SetFocus();
    }

    private int CurrentSessionPreferenceIndex() => _overlay?.SubViews.OfType<ListView>()
        .FirstOrDefault(view => view.Id == "session-preferences-list")?.SelectedItem is int index
            ? index : _sessionPreferencesSelectedIndex;

    private void ShowAddSessionMemory()
    {
        if (_window is null || _sessionPreferencesData is not { SessionId: not null } sessionData) return;
        CloseOverlay();
        var frame = OverlayFrame(SessionPref("memory.add"), _window.Frame.Height - 3);
        frame.Id = "session-preferences-add-memory"; frame.Width = Dim.Percent(86); frame.Height = Dim.Percent(50);
        var label = new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 1, Text = SessionPref("memory.content") };
        var input = new TextField { Id = "session-preferences-memory-input", X = 2, Y = 4,
            Width = Dim.Fill(4), Height = 1, Text = "" };
        var save = new Button { Id = "session-preferences-memory-save", X = 2, Y = Pos.AnchorEnd(2), Text = SessionPref("confirm") };
        var cancel = new Button { Id = "session-preferences-memory-cancel", X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(2), Text = SessionPref("cancel") };
        save.Accepted += (_, _) =>
        {
            var content = input.Text.ToString();
            if (string.IsNullOrWhiteSpace(content)) { RenderSessionPreferences(_sessionPreferencesSelectedIndex, SessionPref("memory.empty_input")); return; }
            if (content.Length > 4096) { RenderSessionPreferences(_sessionPreferencesSelectedIndex, SessionPref("memory.too_long")); return; }
            ConfirmSessionChange(SessionPref("memory.add"), SessionPref("memory.confirm_add") + Environment.NewLine
                + SafeMultiline(content), () => SendMemoryAdd(sessionData, content));
        };
        cancel.Accepted += (_, _) => RenderSessionPreferences(_sessionPreferencesSelectedIndex);
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; RenderSessionPreferences(_sessionPreferencesSelectedIndex); } };
        frame.Add(label, input, save, cancel); ApplyTheme(frame); _overlay = frame; _window.Add(frame); input.SetFocus();
    }

    private void SendMemoryAdd(SessionPreferencesData data, string content) => SendSessionPreferenceCommand(
        data, "memory.session.add", "content", content);

    private void SendMemoryForget(SessionPreferencesData data, MemoryPresentation memory) => SendSessionPreferenceCommand(
        data, "memory.session.forget", "memoryId", memory.Id);

    private void SendMemoryCandidate(SessionPreferencesData data, string command, MemoryCandidatePresentation candidate)
    {
        var payload = "{" + JsonObj.Field("cmd", command) + "," + JsonObj.Field("candidateId", candidate.Id)
            + "," + JsonObj.Field("expectedSessionId", data.SessionId ?? "") + "}";
        SendSessionPreferencePayload(data, payload);
    }

    private void SendSkillChange(SessionPreferencesData data, SkillPresentation skill, bool activate)
    {
        var command = activate ? "skills.activate" : "skills.release";
        var payload = "{" + JsonObj.Field("cmd", command) + "," + JsonObj.Field("skillId", skill.Id)
            + "," + JsonObj.Field("expectedSessionId", data.SessionId ?? "")
            + "," + JsonObj.Field("expectedManifestSha256", skill.ManifestSha256) + "}";
        SendSessionPreferencePayload(data, payload);
    }

    private void SendExtensionConsent(SessionPreferencesData data, ExtensionPresentation extension, bool grant,
        IReadOnlyList<string> capabilities)
    {
        var caps = "[" + string.Join(",", capabilities.Select(capability => "\"" + JsonObj.Escape(capability) + "\"")) + "]";
        var payload = "{" + JsonObj.Field("cmd", "extensions.consent")
            + "," + JsonObj.Field("extensionId", extension.Id)
            + "," + JsonObj.Field("scope", extension.Scope)
            + "," + JsonObj.Field("version", extension.Version ?? "")
            + "," + JsonObj.Field("manifestSha256", extension.ManifestSha256)
            + "," + JsonObj.Field("packageSha256", extension.PackageSha256)
            + "," + JsonObj.Field("consentHash", extension.ConsentHash)
            + "," + JsonObj.FieldRaw("capabilities", caps)
            + "," + JsonObj.FieldBool("grant", grant)
            + "," + JsonObj.Field("expectedSessionId", data.SessionId ?? "") + "}";
        SendSessionPreferencePayload(data, payload);
    }

    private void SendSessionPreferenceCommand(SessionPreferencesData data, string command, string field, string value)
    {
        var payload = "{" + JsonObj.Field("cmd", command) + "," + JsonObj.Field(field, value)
            + "," + JsonObj.Field("expectedSessionId", data.SessionId ?? "") + "}";
        SendSessionPreferencePayload(data, payload);
    }

    private void SendSessionPreferencePayload(SessionPreferencesData data, string payload)
    {
        if (_client is not ITrustedUserActionClient trusted)
        {
            RenderSessionPreferences(CurrentSessionPreferenceIndex(), SessionPref("error.trusted_action_required"));
            return;
        }
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        var notice = ack.Status == "ok" ? SessionPref("success") : SessionPreferenceError(ack.Error);
        if (ack.Status == "ok")
        {
            var query = _client.Query(data.QueryName, CancellationToken.None)?.Json;
            _sessionPreferencesData = query is null ? SessionPreferencesData.Unavailable(data.QueryName)
                : SessionPreferencesPresentation.Decode(data.QueryName, query);
            BuildSessionPreferenceRows();
            RenderSessionPreferences(0, notice);
        }
        else RenderSessionPreferences(CurrentSessionPreferenceIndex(), notice);
    }

    private string SessionPreferenceError(string? error)
    {
        if (error is null) return SessionPref("error.operation_failed");
        var suffix = error.StartsWith("sessionPreferences.", StringComparison.Ordinal)
            ? error["sessionPreferences.".Length..] : "";
        return suffix switch
        {
            "error.session_changed" => SessionPref("error.session_changed"),
            "error.selection_changed" => SessionPref("error.selection_changed"),
            "error.session_unavailable" => SessionPref("error.session_unavailable"),
            "error.workspace_untrusted" => SessionPref("error.workspace_untrusted"),
            "error.trusted_action_required" => SessionPref("error.trusted_action_required"),
            "error.invalid_request" => SessionPref("error.invalid_request"),
            _ => SessionPref("error.operation_failed"),
        };
    }

    private void ReturnToPreferenceCategory()
    {
        if (ReturnToChatIfOpenedFromChat()) return;
        CloseOverlay();
        ShowPreferences(_preferencesCategory, categoryPage: true);
    }
}
