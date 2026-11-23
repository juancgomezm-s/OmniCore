#pragma warning disable CS0618 // Pinned Terminal.Gui multiline editor.
using System.Collections.ObjectModel;
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly IMcpPreferencesFacade? _mcpPreferencesOverride;
    private Action? _mcpEscape;
    private bool HandleMcpPreferencesEscape()
    {
        if (_mcpEscape is not { } cancel) return false;
        _mcpEscape = null; cancel(); return true;
    }
    private IMcpPreferencesFacade McpPreferences => _mcpPreferencesOverride ?? McpPreferencesHost.Create(Environment.CurrentDirectory);
    private string McpText(string key) => McpPreferencesResources.Get(_locale, key);
    private string McpError(Exception error) => McpText(error.Message.StartsWith("mcpPreferences.", StringComparison.Ordinal)
        ? error.Message[15..] : error is ArgumentException or InvalidDataException ? "invalid" : "failed");

    private FrameView McpFrame(string title, string id, int height)
    {
        var frame = PreferenceFrame(title, height);
        frame.Id = id; frame.Width = Dim.Percent(92);
        frame.Height = Dim.Func(_ => Math.Min(height, Math.Max(1, _window!.Viewport.Height - 2)));
        return frame;
    }

    private static CompactRowButton McpButton(string id, string text, Pos y) => new()
    { Id = id, Text = text, X = 2, Y = y, Height = 1, NoPadding = true, TextAlignment = Alignment.Start };

    private void OpenMcpFrame(FrameView frame, View focus, Action cancel)
    {
        frame.KeyDown += (_, key) =>
        { if (!key.Handled && key.KeyCode == KeyCode.Esc) { key.Handled = true; cancel(); } };
        OpenControlFrame(frame, focus);
        _mcpEscape = cancel;
        PreferenceNavigation.Attach(frame);
        _preferencesReturnToCategory = true;
    }

    internal void ShowMcpPreferences(string? message = null)
    {
        if (_window is null || _overlay is not null) return;
        var host = McpPreferences;
        McpPreferencesSnapshot snapshot;
        try { snapshot = host.Read(); }
        catch (Exception error) { ShowPreferenceText(McpText("title"), McpError(error)); return; }
        var frame = McpFrame(McpText("title"), "mcp-preferences", 38);
        var list = new ListView { Id = "mcp-servers", X = 2, Y = 4, Width = Dim.Fill(4),
            Height = Dim.Func(_ => Math.Max(2, frame.Viewport.Height - 22)), ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(snapshot.Servers.Select(server => server.Id + " · " + server.Transport
            + " · " + McpText(server.Scope == "user" ? "user" : "project") + " · " + McpText(server.Trusted ? "authorized" : "pending"))));
        if (snapshot.Servers.Count > 0) list.SelectedItem = 0;
        var note = new Label { Id = "mcp-notice", X = 2, Y = Pos.Bottom(list), Width = Dim.Fill(4), Height = 2,
            TextAlignment = Alignment.Start, Text = message ?? (snapshot.Servers.Count == 0 ? McpText("empty") : McpText("user_only")) };
        var add = McpButton("mcp-add", McpText("add"), Pos.Bottom(note) + 1);
        var edit = McpButton("mcp-edit", McpText("edit"), Pos.Bottom(add) + 1);
        var credential = McpButton("mcp-credential", McpText("credential"), Pos.Bottom(edit) + 1);
        var trust = McpButton("mcp-trust", McpText("trust"), Pos.Bottom(credential) + 1);
        var remove = McpButton("mcp-remove", McpText("remove"), Pos.Bottom(trust) + 1);
        var back = McpButton("mcp-back", McpText("back"), Pos.Bottom(remove) + 1);
        McpConfigurationRow? Selected() => list.SelectedItem is int at && at >= 0 && at < snapshot.Servers.Count ? snapshot.Servers[at] : null;
        void UpdateActions()
        {
            var selected = Selected();
            var user = selected?.Scope == "user";
            edit.Enabled = remove.Enabled = user;
            credential.Enabled = user && selected?.Transport == "http";
            credential.Text = McpText(selected?.HasCredential == true ? "credential_set" : "credential");
            trust.Enabled = selected is not null && (selected.Transport == "http" || selected.Scope != "user");
            trust.Text = McpText(selected?.Trusted == true ? "revoke" : "trust");
        }
        void Return() => ReturnFromPreferencesSubpage();
        add.Accepted += (_, _) => ShowMcpEditor(host, snapshot, null);
        edit.Accepted += (_, _) => { if (Selected() is { Scope: "user" } server) ShowMcpEditor(host, snapshot, server); };
        credential.Accepted += (_, _) => { if (Selected() is { Scope: "user", Transport: "http" } server) ShowMcpCredential(host, snapshot, server); };
        remove.Accepted += (_, _) => { if (Selected() is { Scope: "user" } server) ShowMcpConfirmation(host, snapshot, server, remove: true); };
        trust.Accepted += (_, _) => { if (Selected() is { } server) ShowMcpConfirmation(host, snapshot, server, remove: false); };
        back.Accepted += (_, _) => Return();
        list.ValueChanged += (_, _) => UpdateActions();
        EnterNavigation.Focus(() => edit.Enabled ? edit : trust.Enabled ? trust : add, list);
        EnterNavigation.TraverseTabs(list, add, edit, credential, trust, remove, back);
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 3, TextAlignment = Alignment.Start, Text = McpText("intro") }, list, note,
            add, edit, credential, trust, remove, back);
        UpdateActions();
        OpenMcpFrame(frame, snapshot.Servers.Count == 0 ? add : list, Return);
    }

    private void ShowMcpEditor(IMcpPreferencesFacade host, McpPreferencesSnapshot snapshot, McpConfigurationRow? existing)
    {
        CloseOverlay();
        var transport = existing?.Transport ?? "stdio";
        var frame = McpFrame(McpText("title") + " › " + (existing?.Id ?? McpText("add")), "mcp-editor", 36);
        var panel = new View { Id = "mcp-editor-fields", X = 2, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(8),
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        var y = 0;
        void Caption(string key) => panel.Add(new Label { X = 0, Y = y++, Width = Dim.Fill(2), Height = 1, TextAlignment = Alignment.Start, Text = McpText(key) });
        TextField Field(string key, string? value)
        {
            Caption(key);
            var field = new TextField { Id = "mcp-" + key, X = 0, Y = y++, Width = Dim.Fill(2), Text = value ?? "" };
            panel.Add(field); return field;
        }
        var id = Field("id", existing?.Id); id.ReadOnly = existing is not null; id.CanFocus = existing is null;
        Caption("transport");
        var mode = McpButton("mcp-transport", "", y++); mode.X = 0; panel.Add(mode);
        var endpoint = Field("endpoint", existing?.Endpoint);
        var command = Field("command", existing?.Command);
        Caption("arguments");
        var arguments = new TextView { Id = "mcp-arguments", X = 0, Y = y, Width = Dim.Fill(2), Height = 4,
            BorderStyle = LineStyle.Single, Multiline = true, TabKeyAddsTab = false,
            WordWrap = false, Text = EncodeLocalArguments(existing?.Arguments ?? []) };
        panel.Add(arguments); y += 4;
        var directory = Field("directory", existing?.WorkingDirectory);
        var reflow = ConfigurePreferenceScroll(panel, () => y);
        // Reflow a single column: no disabled transport-specific fields between focus targets.
        void Reflow()
        {
            mode.Text = McpText(transport) + "  ‹ ›";
            var row = 4;
            foreach (var field in panel.SubViews.Skip(4))
            {
                var http = field.Id == "mcp-endpoint" || field is Label && field.Text.ToString() == McpText("endpoint");
                field.Visible = http == (transport == "http");
                if (!field.Visible) continue;
                field.Y = row; row += ReferenceEquals(field, arguments) ? 4 : 1;
            }
            y = row; reflow(); frame.SetNeedsLayout();
        }
        void Toggle() { transport = transport == "http" ? "stdio" : "http"; Reflow(); }
        mode.Accepted += (_, e) => { e.Handled = true; Toggle(); };
        mode.KeyDown += (_, key) =>
        {
            if (key.Handled || key.KeyCode is not (KeyCode.Space or KeyCode.CursorLeft or KeyCode.CursorRight)) return;
            key.Handled = true; Toggle();
        };
        arguments.KeyDown += (_, key) =>
        {
            if (key.Handled || key.KeyCode != (KeyCode.Enter | KeyCode.ShiftMask)) return;
            key.Handled = true; arguments.InsertText("\n");
        };
        var notice = new Label { Id = "mcp-editor-notice", X = 2, Y = Pos.AnchorEnd(7), Width = Dim.Fill(4), Height = 3, TextAlignment = Alignment.Start, Text = McpText("navigation") };
        var save = McpButton("mcp-save", McpText("save"), Pos.AnchorEnd(4));
        var cancel = McpButton("mcp-cancel", McpText("cancel"), Pos.AnchorEnd(2));
        void Return() { CloseOverlay(); ShowMcpPreferences(); }
        cancel.Accepted += (_, _) => Return();
        save.Accepted += (_, _) =>
        {
            try
            {
                var draft = new McpConfigurationDraft(id.Text.ToString().Trim(), transport, command.Text.ToString().Trim(),
                    DecodeLocalArguments(arguments.Text.ToString()).ToArray(), directory.Text.ToString().Trim(), endpoint.Text.ToString().Trim());
                host.Save(snapshot.Revision, existing?.Id, draft);
                CloseOverlay(); ShowMcpPreferences(McpText("saved"));
            }
            catch (Exception error) { notice.Text = McpError(error); }
        };
        EnterNavigation.TraverseForm([id, mode, endpoint, command, arguments, directory], save, cancel);
        frame.Add(panel, notice, save, cancel); Reflow();
        OpenMcpFrame(frame, existing is null ? id : mode, Return);
    }

    private void ShowMcpConfirmation(IMcpPreferencesFacade host, McpPreferencesSnapshot snapshot, McpConfigurationRow server, bool remove)
    {
        CloseOverlay();
        var frame = McpFrame(McpText(remove ? "remove" : server.Trusted ? "revoke" : "trust"), "mcp-confirm", 19);
        var target = server.Transport == "http" ? server.Endpoint : server.Command + "\n"
            + string.Join("\n", server.Arguments.Select(argument => "  \"" + System.Text.Json.JsonEncodedText.Encode(argument) + "\""))
            + (server.WorkingDirectory.Length == 0 ? "" : "\n" + McpText("directory") + ": " + server.WorkingDirectory);
        var note = new TextView { Id = "mcp-confirm-details", X = 2, Y = 2, Width = Dim.Fill(4), Height = Dim.Fill(7),
            ReadOnly = true, WordWrap = true, ScrollBars = true, TabKeyAddsTab = false,
            Text = server.Id + " · " + McpText(server.Scope == "user" ? "user" : "project") + "\n" + target
                + "\n\n" + McpText(remove ? "remove_help" : "trust_help") };
        var cancel = McpButton("mcp-confirm-cancel", McpText("cancel"), Pos.AnchorEnd(4));
        var confirm = McpButton("mcp-confirm-apply", McpText("confirm"), Pos.AnchorEnd(2));
        void Return() { CloseOverlay(); ShowMcpPreferences(); }
        cancel.Accepted += (_, _) => Return();
        confirm.Accepted += (_, _) =>
        {
            try
            {
                if (remove) host.Remove(snapshot.Revision, server.Id);
                else host.SetTrust(snapshot.Revision, server.Id, !server.Trusted);
                CloseOverlay(); ShowMcpPreferences(remove ? McpText("removed") : McpText("saved"));
            }
            catch (Exception error) { note.Text = McpError(error); }
        };
        frame.Add(note, cancel, confirm); EnterNavigation.Focus(cancel, note); EnterNavigation.TraverseTabs(note, cancel, confirm);
        OpenMcpFrame(frame, cancel, Return);
    }

    private void ShowMcpCredential(IMcpPreferencesFacade host, McpPreferencesSnapshot snapshot, McpConfigurationRow server)
    {
        CloseOverlay();
        var frame = McpFrame(McpText("credential") + " › " + server.Id, "mcp-credential-editor", 18);
        var secret = new TextField { Id = "mcp-secret", X = 2, Y = 5, Width = Dim.Fill(4), Secret = true };
        var note = new Label { Id = "mcp-credential-notice", X = 2, Y = 7, Width = Dim.Fill(4), Height = 3, TextAlignment = Alignment.Start };
        var save = McpButton("mcp-credential-save", McpText("save"), Pos.AnchorEnd(4));
        var cancel = McpButton("mcp-credential-cancel", McpText("cancel"), Pos.AnchorEnd(2));
        void Return() { secret.Text = ""; CloseOverlay(); ShowMcpPreferences(); }
        cancel.Accepted += (_, _) => Return();
        save.Accepted += (_, _) =>
        {
            try
            {
                host.SetCredential(snapshot.Revision, server.Id, secret.Text.ToString());
                secret.Text = ""; CloseOverlay(); ShowMcpPreferences(McpText("saved"));
            }
            catch (Exception error) { secret.Text = ""; note.Text = McpError(error); }
        };
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 3, TextAlignment = Alignment.Start, Text = McpText("secret") }, secret, note, save, cancel);
        EnterNavigation.TraverseForm([secret], save, cancel);
        OpenMcpFrame(frame, secret, Return);
    }
}
