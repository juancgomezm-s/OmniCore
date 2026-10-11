using System.Collections.ObjectModel;
using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private string ToolUi(string key) => _localization.Resolve("tools.ui." + key);

    private void ShowTools(ToolsInspection snapshot)
    {
        if (_window is null) return;
        var fromPreferences = _preferencesCenterActive || _preferencesReturnToCategory;
        CloseOverlay();
        var frame = OverlayFrame(ToolUi("title"), Math.Max(14, _window.Frame.Height - 5));
        frame.Height = Dim.Func(parent => Math.Max(16, (parent?.Frame.Height ?? 25) - 4), _window);
        var summary = new Label { Id = "tools-summary", X = 2, Y = 1, Width = Dim.Fill(2), Height = 1 };
        var purpose = new Label { Id = "tools-purpose", X = 2, Y = 2, Width = Dim.Fill(2), Height = 2,
            Text = ToolUi("purpose") };
        var search = new TextField { Id = "tools-search", X = 10, Y = 4, Width = Dim.Fill(2) };
        var list = new ListView { Id = "tools-list", X = 2, Y = 6, Width = Dim.Fill(2),
            Height = Dim.Func(parent => Math.Max(1, ((parent?.Frame.Height ?? 21) - 10) / 2), frame),
            ShowMarks = false };
        var detail = new StyledTextPresenter { Id = "tools-detail", X = 2, Y = Pos.Bottom(list) + 1, Width = Dim.Fill(2),
            Height = Dim.Fill(3), CanFocus = true };
        var close = new Button { Id = "tools-close", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2),
            Text = fromPreferences ? Pref("back") : ToolUi("close") };
        close.Accepted += (_, _) =>
        {
            if (fromPreferences) ReturnFromPreferencesSubpage(); else CloseOverlay();
        };
        var rows = snapshot.Tools.ToArray();
        string Visibility(ToolInspectionRow row) => ToolUi(row.Visible ? "visible" : "hidden");
        string Description(ToolInspectionRow row) => ToolDisplayText(ToolInspectionPresentation.Description(row, _localization));
        string Detail(ToolInspectionRow row) => ToolDisplayText(row.VisibleName) + "\n" + Description(row)
            + "\n\n" + ToolUi("restriction") + ToolInspectionPresentation.Decision(row, _localization)
            + "\n" + Visibility(row) + " · " + ToolUi("not_permission")
            + "\n\n" + ToolUi("control")
            + "\n\nID: " + ToolDisplayText(row.ToolId)
            + "\n" + ToolUi("source") + ToolDisplayText(row.Source)
            + "\n" + ToolUi("effect") + ToolDisplayText(row.EffectClass)
            + "\n" + ToolUi("policy") + ToolDisplayText(row.Decision)
            + (string.IsNullOrWhiteSpace(row.Description) ? "" : "\n" + ToolUi("original") + ToolDisplayText(row.Description));
        void UpdateDetail()
        {
            summary.Text = ToolUi("mode") + ToolDisplayText(snapshot.Mode ?? ToolUi("unknown"))
                + $" · {rows.Length}/{snapshot.Tools.Count} · " + ToolUi("read_only");
            detail.Text = list.SelectedItem is int index && index >= 0 && index < rows.Length
                ? Detail(rows[index])
                : snapshot.Tools.Count == 0 ? ToolUi("empty") : ToolUi("no_matches");
        }
        void Filter()
        {
            var query = search.Text.ToString().Trim();
            rows = snapshot.Tools.Where(row => string.Join(" ", row.VisibleName, row.ToolId, row.Source,
                row.EffectClass, row.Decision, row.Description, Description(row), ToolInspectionPresentation.Decision(row, _localization))
                .Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SetSource(new ObservableCollection<string>(rows.Select(row =>
                ToolDisplayText(row.VisibleName) + " · " + Visibility(row))));
            list.CanFocus = rows.Length > 0;
            if (rows.Length > 0) list.SelectedItem = 0;
            list.EnsureSelectedItemVisible();
            UpdateDetail();
        }
        search.TextChanged += (_, _) => Filter();
        list.ValueChanged += (_, _) => UpdateDetail();
        list.Accepted += (_, args) => { args.Handled = true; detail.SetFocus(); };
        void SearchShortcut(Key key)
        {
            if (key.KeyCode == (KeyCode.F | KeyCode.CtrlMask)) { search.SetFocus(); key.Handled = true; }
        }
        frame.KeyDown += (_, key) => SearchShortcut(key);
        list.KeyDown += (_, key) => SearchShortcut(key);
        detail.KeyDown += (_, key) => SearchShortcut(key);
        search.KeyDown += (_, key) =>
        {
            SearchShortcut(key);
            if (key.Handled) return;
            if (rows.Length > 0 && key.KeyCode is KeyCode.CursorDown or KeyCode.Enter)
            { list.SetFocus(); key.Handled = true; }
        };
        frame.Add(summary, purpose, new Label { X = 2, Y = 4, Width = 8, Text = ToolUi("filter") },
            search, list, detail, close, new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2),
                Text = ToolUi("help") });
        ApplyTheme(_window, dimmed: true);
        if (_conversation is not null) { _conversation.Dimmed = true; _conversation.SetNeedsDraw(); }
        ApplyTheme(frame);
        _overlay = frame;
        _preferencesReturnToCategory = fromPreferences;
        _window.Add(frame);
        Filter();
        if (rows.Length > 0) list.SetFocus(); else search.SetFocus();
    }

    // Host diagnostic fields are data, never terminal control sequences or line breaks.
    private static string ToolDisplayText(string text) => string.Concat(text.Select(ch => char.IsControl(ch) ? ' ' : ch));
}
