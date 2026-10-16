using System.Collections.ObjectModel;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void ShowMappingPreferences(string category)
    {
        if (_window is null || _overlay is not null) return;
        var tools = category == "tools_security";
        var host = _policyPreferencesOverride ?? PolicyPreferencesHost.Create(Environment.CurrentDirectory);
        PolicyPreferencesSnapshot snapshot;
        try { snapshot = host.Read(); }
        catch (Exception) { ShowPreferenceText(PolicyPref("title"), PolicyPref("unavailable")); return; }
        var draft = new Dictionary<string, string>(tools ? snapshot.ToolPreferences : snapshot.CommandAliases, StringComparer.Ordinal);
        var frame = OverlayFrame(PolicyPref(tools ? "section_tool_preferences" : "section_command_aliases"), _window.Frame.Height - 3);
        frame.Id = "mapping-preferences"; frame.Width = Dim.Percent(92); frame.Height = Dim.Fill(2);
        var list = new ListView { Id = "mapping-list", X = 2, Y = 3, Width = Dim.Fill(4), Height = Dim.Fill(13), ShowMarks = false };
        var source = new TextField { Id = "mapping-source", X = 2, Y = Pos.AnchorEnd(9), Width = Dim.Percent(44), Enabled = snapshot.Available };
        var target = new TextField { Id = "mapping-target", X = Pos.Percent(50), Y = Pos.AnchorEnd(9), Width = Dim.Fill(3), Enabled = snapshot.Available };
        var note = new Label { Id = "mapping-notice", X = 2, Y = Pos.AnchorEnd(5), Width = Dim.Fill(4), Height = 2,
            Text = snapshot.Available ? "" : PolicyPref("unavailable") };
        var apply = new Button { Id = "mapping-apply", X = 2, Y = Pos.AnchorEnd(7), Text = PolicyPref("mapping_apply"), Enabled = snapshot.Available };
        var remove = new Button { Id = "mapping-remove", X = Pos.Percent(52), Y = Pos.AnchorEnd(7), Text = PolicyPref("mapping_remove"), Enabled = snapshot.Available };
        var save = new Button { Id = "mapping-save", X = 2, Y = Pos.AnchorEnd(2), Text = PolicyPref("save"), Enabled = snapshot.Available };
        var back = new Button { Id = "mapping-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = PolicyPref("back") };
        KeyValuePair<string, string>[] rows = [];
        void Refresh()
        {
            rows = draft.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            list.SetSource(new ObservableCollection<string>(rows.Length == 0
                ? [PolicyPref("mapping_empty")] : rows.Select(pair => pair.Key + " → " + pair.Value)));
            remove.Enabled = snapshot.Available && rows.Length > 0;
        }
        void Select()
        {
            if (list.SelectedItem is not int at || at < 0 || at >= rows.Length) return;
            source.Text = rows[at].Key; target.Text = rows[at].Value;
        }
        bool ApplyRow()
        {
            var from = source.Text.ToString().Trim(); var to = target.Text.ToString().Trim();
            try
            {
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) throw new ArgumentException();
                if (tools)
                {
                    if (new[] { from, to }.Any(value => value.Any(ch => char.IsWhiteSpace(ch) || ch is ',' or '='))) throw new ArgumentException();
                    ParseToolPreferences(from + "=" + to);
                }
                else OmniCore.Protocol.UserCommandAliases.ValidatePair(from, to);
                draft[from] = to; Refresh(); note.Text = PolicyPref("mapping_pending"); return true;
            }
            catch (ArgumentException) { note.Text = PolicyPref(tools ? "invalid_tool_preferences" : "invalid_command_aliases"); return false; }
        }
        list.ValueChanged += (_, _) => Select();
        apply.Accepted += (_, _) => ApplyRow();
        foreach (var field in new[] { source, target }) field.KeyDown += (_, key) =>
        { if (key.KeyCode == KeyCode.Enter) { ApplyRow(); key.Handled = true; } };
        remove.Accepted += (_, _) =>
        {
            if (list.SelectedItem is not int at || at < 0 || at >= rows.Length) return;
            draft.Remove(rows[at].Key); source.Text = target.Text = ""; Refresh(); note.Text = PolicyPref("mapping_pending");
        };
        save.Accepted += (_, _) =>
        {
            if ((!string.IsNullOrWhiteSpace(source.Text.ToString()) || !string.IsNullOrWhiteSpace(target.Text.ToString())) && !ApplyRow()) return;
            try
            {
                snapshot = tools ? host.SaveToolPreferences(snapshot.Revision, draft) : host.SaveCommandAliases(snapshot.Revision, draft);
                note.Text = PolicyPref(snapshot.Available ? "saved" : "saved_but_invalid");
            }
            catch (InvalidOperationException exception) when (exception.Message == "policyPreferences.error.stale") { note.Text = PolicyPref("stale"); }
            catch (ArgumentException) { note.Text = PolicyPref(tools ? "invalid_tool_preferences" : "invalid_command_aliases"); }
            catch (Exception) { note.Text = PolicyPref("save_failed"); }
        };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences(category, categoryPage: true); };
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 2, Text = PolicyPref("mapping_help") }, list,
            new Label { X = 2, Y = Pos.AnchorEnd(10), Width = Dim.Percent(44), Text = PolicyPref(tools ? "mapping_source" : "mapping_alias") }, source,
            new Label { X = Pos.Percent(50), Y = Pos.AnchorEnd(10), Width = Dim.Fill(3), Text = PolicyPref("mapping_target") }, target,
            apply, remove, note, save, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true;
        Refresh(); source.SetFocus();
    }

    private void ShowServiceInspection(string query)
    {
        string text;
        try
        {
            var json = _client.Query(query, CancellationToken.None)?.Json;
            text = json is null ? Pref("unavailable") : string.Join(Environment.NewLine, FormatPreferenceInspection(query, json));
        }
        catch (Exception) { text = Pref("unavailable"); }
        ShowPreferenceText(query == "hooks" ? PolicyPref("service.hooks") : Pref("mcp_open"), text);
    }
}
