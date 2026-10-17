using System.Collections.ObjectModel;
using OmniCore.Client;
using OmniCore.Protocol;
using OmniCore.Host;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly Dictionary<string, (string Text, TuiAttachmentReference[] Attachments)> _conversationDrafts = new();

    private bool HandleConversationSlash(string input)
    {
        if (!CommandLineParser.TryParse(input, out var parsed) || parsed is null
            || parsed.Name is not ("new" or "sessions" or "resume-session" or "rename-session" or "clear" or "compact")) return false;
        _composer!.Text = ""; HideCommandHelper();
        if (HasActiveModelWork) { ShowMessage(_localization.Resolve("shell.conversation.busy")); return true; }
        if (parsed.Name == "clear" && parsed.Arguments.Count == 0)
        {
            _state = new(_state.Header, ConversationModel.Empty(), _state.Sidebar, _state.Composer,
                _state.StatusLine, _state.Overlays, _state.Connection);
            _renderedConversation = null; RenderState(); return true;
        }
        if (parsed.Name == "new" && parsed.Arguments.Count == 0) { ChangeConversation("conversation.new"); return true; }
        if (parsed.Name is "sessions" or "resume-session" && parsed.Arguments.Count == 0)
        { ShowConversations(); return true; }
        if (parsed.Name == "resume-session" && parsed.Arguments.Count == 1)
        { ChangeConversation("conversation.resume", parsed.Arguments[0]); return true; }
        if (parsed.Name == "compact" && parsed.Arguments.Count == 0)
        {
            var ack = ConversationCommand("conversation.compact");
            if (ack.Status != "ok") ShowMessage(_localization.ResolveWire(ack.Error ?? "conversation.failed"));
            else { _turnStatus = _localization.Resolve(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.NoOp
                ? "shell.conversation.nothing_to_compact" : "shell.conversation.compacted"); PollEvents(); }
            return true;
        }
        if (parsed.Name == "rename-session" && parsed.Arguments.Count > 0)
        {
            RenameCurrentConversation(string.Join(' ', parsed.Arguments));
            return true;
        }
        ShowMessage(_localization.Resolve("shell.conversation.arguments")); return true;
    }

    private void RenameCurrentConversation(string title)
    {
        title = title.Trim();
        if (title.Length is 0 or > 120 || title.Any(char.IsControl))
        { ShowMessage(_localization.Resolve("shell.conversation.arguments")); return; }
        try
        {
            var row = ConversationJson.Decode(_client.Query("conversations", CancellationToken.None)?.Json ?? "{}")
                ?.Conversations.FirstOrDefault(item => item.Current);
            if (row is null) { ShowMessage(_localization.Resolve("conversation.failed")); return; }
            if (_client is not ITrustedUserActionClient trusted)
            { ShowMessage(_localization.Resolve("conversation.trusted_required")); return; }
            var payload = "{\"cmd\":\"conversation.rename\",\"sessionId\":"
                + System.Text.Json.JsonSerializer.Serialize(row.Id) + ",\"title\":"
                + System.Text.Json.JsonSerializer.Serialize(title) + ",\"expectedSequence\":"
                + row.JournalSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
            if (ack.Status != "ok") { ShowMessage(_localization.ResolveWire(ack.Error ?? "conversation.failed")); return; }
            _turnStatus = _localization.Resolve("shell.conversation.renamed");
            _state = RefreshState(); RenderState();
        }
        catch (System.Text.Json.JsonException)
        { ShowMessage(_localization.Resolve("conversation.failed")); }
    }

    private CommandAck ConversationCommand(string name, string? session = null)
    {
        if (_client is not ITrustedUserActionClient trusted)
            return new(Ids.NewV7(), "error", "conversation.trusted_required", RuntimeCommandOutcome.Rejected());
        return trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", name)
            + (session is null ? "" : "," + JsonObj.Field("sessionId", session)) + "}"), CancellationToken.None);
    }

    private bool ChangeConversation(string command, string? session = null, Label? errorLabel = null)
    {
        if (HasActiveModelWork) { ShowMessage(_localization.Resolve("shell.conversation.busy")); return false; }
        var ack = ConversationCommand(command, session);
        if (ack.Status != "ok")
        {
            var message = _localization.ResolveWire(ack.Error ?? "conversation.failed");
            if (errorLabel is not null) errorLabel.Text = message; else ShowMessage(message);
            return false;
        }
        _conversationDrafts[_cursorSession ?? ""] = (_composer?.Text ?? "", _pendingAttachments.ToArray());
        _lastSequence = 0; _observedModel = null; _turnUsage = null; _turnStatus = null;
        _activeInteraction = null; _pendingQuotaResume = null; _pendingEscalationResume = null;
        _state = RefreshState();
        _pendingAttachments.Clear();
        if (_conversationDrafts.TryGetValue(_cursorSession ?? "", out var draft))
        { _composer!.Text = draft.Text; _pendingAttachments.AddRange(draft.Attachments); }
        else _composer!.Text = "";
        _renderedConversation = null; _followConversationEnd = true; RenderAttachmentChips(); RenderState();
        return true;
    }

    private void ShowConversations()
    {
        if (_window is null || _overlay is not null) return;
        if (HasActiveModelWork) { ShowMessage(_localization.Resolve("shell.conversation.busy")); return; }
        ConversationRow[] rows;
        try { rows = ConversationJson.Decode(_client.Query("conversations", CancellationToken.None)?.Json ?? "{}")?.Conversations.ToArray() ?? []; }
        catch (System.Text.Json.JsonException) { ShowMessage(_localization.Resolve("conversation.failed")); return; }
        var frame = OverlayFrame(_localization.Resolve("shell.conversation.title"), _window.Frame.Height - 3);
        frame.Id = "conversation-selector";
        var search = new TextField { Id = "conversation-search", X = 4, Y = 2, Width = Dim.Fill(4), Height = 1 };
        var searchLabel = new Label { X = 4, Y = 1, Text = Pref("search"), CanFocus = false };
        var list = new ListView { Id = "conversation-list", X = 4, Y = 4, Width = Dim.Fill(4), Height = Dim.Fill(7), ShowMarks = false };
        var notice = MenuIndent.Help("", Pos.AnchorEnd(6), 2, "conversation-notice");
        var filtered = rows;
        void Update()
        {
            filtered = rows.Where(row => row.Title.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)
                || row.Id.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SetSource(new ObservableCollection<string>(filtered.Select(row => (row.Current ? "✓ " : "  ")
                + row.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · "
                + (row.Title.Length > 0 ? row.Title : _localization.Resolve("shell.new_session")))));
            if (filtered.Length > 0) list.SelectedItem = 0;
            notice.Text = filtered.Length == 0 ? _localization.Resolve("shell.conversation.empty") : "";
        }
        search.TextChanged += (_, _) => Update();
        void Open()
        {
            if (list.SelectedItem is not int index || index < 0 || index >= filtered.Length) return;
            if (ChangeConversation("conversation.resume", filtered[index].Id, notice)) { CloseOverlay(); _composer!.SetFocus(); }
        }
        list.Accepted += (_, args) => { args.Handled = true; Open(); };
        var open = new Button { Id = "conversation-open", X = 4, Y = Pos.AnchorEnd(3), Text = _localization.Resolve("shell.conversation.resume") };
        open.Accepted += (_, _) => Open();
        var create = new Button { Id = "conversation-new", X = 24, Y = Pos.AnchorEnd(3), Text = _localization.Resolve("shell.conversation.new") };
        create.Accepted += (_, _) => { if (ChangeConversation("conversation.new", errorLabel: notice)) { CloseOverlay(); _composer!.SetFocus(); } };
        var close = new Button { Id = "conversation-close", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(3), Text = Pref("close") };
        close.Accepted += (_, _) => CloseOverlay();
        frame.Add(searchLabel, search, list, notice, open, create, close); Update(); ApplyTheme(frame);
        _overlay = frame; _window.Add(frame); list.SetFocus();
    }
}
