using System.Collections.ObjectModel;
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void ShowToolPreferences()
    {
        if (_window is null || _overlay is not null) return;
        var host = _policyPreferencesOverride ?? PolicyPreferencesHost.Create(Environment.CurrentDirectory);
        PolicyPreferencesSnapshot snapshot;
        try { snapshot = host.Read(); }
        catch (Exception) { ShowPreferenceText(PolicyPref("section_tool_preferences"), PolicyPref("unavailable")); return; }
        ToolsInspection? catalog;
        try { catalog = ToolsInspectionJson.Decode(_client.Query("tools", CancellationToken.None)?.Json); }
        catch (Exception) { catalog = null; }
        var tools = catalog?.Tools.OrderBy(tool => tool.ToolId, StringComparer.Ordinal).ToArray() ?? [];
        var sources = tools.Where(source => tools.Any(target => ToolPreferencePresentation.IsAlternative(source, target))).ToArray();
        var draft = new Dictionary<string, string>(snapshot.ToolPreferences, StringComparer.Ordinal);
        KeyValuePair<string, string>[] savedRows = [];
        ToolInspectionRow[] alternatives = [];
        var describeTarget = false;
        var frame = OverlayFrame(PolicyPref("section_tool_preferences"), _window.Frame.Height - 3);
        frame.Id = "mapping-preferences"; frame.Width = Dim.Percent(92); frame.Height = Dim.Fill(2);
        var saved = new ListView { Id = "mapping-list", X = MenuIndent.Level1, Y = 4, Width = Dim.Fill(4), Height = 3, ShowMarks = false };
        var source = new ListView { Id = "mapping-source", X = MenuIndent.Level1, Y = 9, Width = Dim.Percent(44),
            Height = Dim.Func(_ => Math.Clamp((frame.Viewport.Height - 19) / 2, 2, 8)), ShowMarks = false,
            Visible = sources.Length > 0, Enabled = snapshot.Available };
        var target = new ListView { Id = "mapping-target", X = Pos.Percent(50) + 2, Y = 9, Width = Dim.Fill(3),
            Height = source.Height, ShowMarks = false, Visible = sources.Length > 0, Enabled = snapshot.Available };
        var detail = new StyledTextPresenter { Id = "mapping-detail", X = MenuIndent.Information, Y = sources.Length > 0 ? Pos.Bottom(source) + 1 : 8,
            Width = Dim.Fill(4), Height = sources.Length > 0 ? 3 : 4, CanFocus = true };
        var apply = new Button { Id = "mapping-apply", X = 2, Y = Pos.Bottom(detail),
            Text = PolicyPref("tool_choice_add"), Enabled = snapshot.Available && sources.Length > 0, Visible = sources.Length > 0 };
        var remove = new Button { Id = "mapping-remove", X = Pos.Percent(52), Y = Pos.Bottom(detail),
            Text = PolicyPref("tool_choice_remove") };
        var notice = new Label { Id = "mapping-notice", X = 2, Y = Pos.Bottom(apply), Width = Dim.Fill(4), Height = 2 };
        var save = new Button { Id = "mapping-save", X = 2, Y = Pos.AnchorEnd(2), Text = PolicyPref("save"), Enabled = false };
        var back = new Button { Id = "mapping-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = PolicyPref("back") };

        ToolInspectionRow? ChosenSource() => source.SelectedItem is int at && at >= 0 && at < sources.Length ? sources[at] : null;
        ToolInspectionRow? ChosenTarget() => target.SelectedItem is int at && at >= 0 && at < alternatives.Length ? alternatives[at] : null;
        string Description(ToolInspectionRow tool) => ToolDisplayText(ToolInspectionPresentation.Description(tool, _localization));
        void Details()
        {
            detail.Text = (describeTarget ? ChosenTarget() : ChosenSource()) is { } tool
                ? ToolDisplayText(tool.VisibleName) + ": " + Description(tool) + "\n\n" + PolicyPref("tool_choice_runtime")
                : PolicyPref(catalog is null ? "tool_choice_unavailable"
                    : tools.Any(tool => tool.Tags is null || tool.Risk is null) ? "tool_choice_no_metadata" : "tool_choice_none");
        }
        void RefreshSaved()
        {
            savedRows = draft.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            saved.SetSource(new ObservableCollection<string>(savedRows.Length == 0
                ? [PolicyPref("tool_choice_empty")] : savedRows.Select(pair => ToolDisplayText(pair.Key) + " → " + ToolDisplayText(pair.Value))));
            saved.CanFocus = savedRows.Length > 0;
            if (savedRows.Length > 0) saved.SelectedItem = 0;
            remove.Enabled = snapshot.Available && savedRows.Length > 0;
            remove.Visible = savedRows.Length > 0;
            save.Enabled = snapshot.Available && (draft.Count != snapshot.ToolPreferences.Count
                || draft.Any(pair => snapshot.ToolPreferences.GetValueOrDefault(pair.Key) != pair.Value));
            save.Visible = sources.Length > 0 || savedRows.Length > 0 || save.Enabled;
        }
        void RefreshAlternatives()
        {
            alternatives = ChosenSource() is { } from ? tools.Where(to => ToolPreferencePresentation.IsAlternative(from, to)).ToArray() : [];
            target.SetSource(new ObservableCollection<string>(alternatives.Select(tool => ToolDisplayText(tool.VisibleName))));
            target.CanFocus = alternatives.Length > 0;
            if (alternatives.Length > 0) target.SelectedItem = 0;
            Details();
        }
        void AddPreference()
        {
            if (!snapshot.Available || ChosenSource() is not { } from || ChosenTarget() is not { } to
                || !ToolPreferencePresentation.IsAlternative(from, to)) return;
            draft[from.ToolId] = to.ToolId;
            RefreshSaved(); notice.Text = PolicyPref(save.Enabled ? "mapping_pending" : "saved");
        }
        source.ValueChanged += (_, _) => RefreshAlternatives();
        target.ValueChanged += (_, _) => Details();
        source.HasFocusChanged += (_, _) => { if (source.HasFocus) { describeTarget = false; Details(); } };
        target.HasFocusChanged += (_, _) => { if (target.HasFocus) { describeTarget = true; Details(); } };
        source.Accepted += (_, args) => { args.Handled = true; target.SetFocus(); };
        target.Accepted += (_, args) => { args.Handled = true; AddPreference(); };
        apply.Accepted += (_, _) => AddPreference();
        remove.Accepted += (_, _) =>
        {
            if (saved.SelectedItem is not int at || at < 0 || at >= savedRows.Length) return;
            draft.Remove(savedRows[at].Key); RefreshSaved(); notice.Text = PolicyPref("mapping_pending");
        };
        save.Accepted += (_, _) =>
        {
            if (!save.Enabled) return;
            try
            {
                snapshot = host.SaveToolPreferences(snapshot.Revision, draft);
                RefreshSaved(); notice.Text = PolicyPref(snapshot.Available ? "saved" : "saved_but_invalid");
            }
            catch (InvalidOperationException error) when (error.Message == "policyPreferences.error.stale") { notice.Text = PolicyPref("stale"); }
            catch (ArgumentException) { notice.Text = PolicyPref("invalid_tool_preferences"); }
            catch (Exception) { notice.Text = PolicyPref("save_failed"); }
        };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("tools_security", categoryPage: true); };
        frame.Add(MenuIndent.Help(PolicyPref("tool_choice_purpose"), 1, 2, "mapping-intro"),
            MenuIndent.Heading(PolicyPref("tool_choice_saved"), 3), saved,
            new Label { Id = "omni-section", X = 2, Y = 8, Width = Dim.Percent(46), Height = 1, Text = "│ " + PolicyPref("tool_choice_source"), Visible = sources.Length > 0 }, source,
            new Label { Id = "omni-section", X = Pos.Percent(50), Y = 8, Width = Dim.Fill(3), Height = 1, Text = "│ " + PolicyPref("tool_choice_target"), Visible = sources.Length > 0 }, target,
            detail, apply, remove, notice, save, back);
        source.SetSource(new ObservableCollection<string>(sources.Select(tool => ToolDisplayText(tool.VisibleName))));
        source.CanFocus = sources.Length > 0;
        if (sources.Length > 0) source.SelectedItem = 0;
        RefreshAlternatives(); RefreshSaved();
        notice.Text = !snapshot.Available ? PolicyPref("unavailable") : sources.Length > 0 ? PolicyPref("tool_choice_pick") : "";
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true;
        if (sources.Length > 0 && snapshot.Available) source.SetFocus();
        else if (savedRows.Length > 0) saved.SetFocus();
        else detail.SetFocus();
    }
}
