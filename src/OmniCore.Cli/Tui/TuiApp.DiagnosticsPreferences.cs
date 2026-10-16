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
    internal void ShowDiagnosticsPreferences(string queryName)
    {
        if (_window is null || _overlay is not null || queryName != "diagnostics_data") return;
        var host = _diagnosticsPreferencesOverride ?? DiagnosticsPreferencesHost.Create(Environment.CurrentDirectory);
        DiagnosticsPreferencesSnapshot snapshot;
        try { snapshot = host.Read(); }
        catch (Exception) { ShowPreferenceText(DiagnosticsPref("title"), DiagnosticsPref("operation_failed")); return; }
        var frame = OverlayFrame(DiagnosticsPref("title"), _window.Frame.Height - 3);
        frame.Id = "diagnostics-preferences"; frame.Width = Dim.Percent(92); frame.Height = Dim.Fill(2);
        var panel = new View { Id = "diagnostics-content", X = 2, Y = 2, Width = Dim.Fill(4), Height = Dim.Fill(9),
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        var rows = snapshot.Configuration.Select(entry => new DiagnosticRow(
            DiagnosticsPref("file") + ": " + entry.File + " · " + DiagnosticsPref("key") + ": " + entry.Key
                + (entry.Line is { } line ? " · " + DiagnosticsPref("line") + ": " + line : ""),
            entry.Message is { } message ? _localization.Resolve(message.Key, message.Args) : DiagnosticsPref("configuration_issue"),
            entry.KeyPath)).ToList();
        if (rows.Count == 0) rows.Add(new(DiagnosticsPref("configuration_ok"), DiagnosticsPref("configuration_ok_detail"), ""));
        var list = new ListView { Id = "diagnostics-configuration", X = 2, Y = 3, Width = Dim.Fill(4), Height = 5, ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(rows.Select(row => row.Title)));
#pragma warning disable CS0618
        var details = new TextView { Id = "diagnostics-details", X = 2, Y = 9,
            Width = Dim.Fill(4), Height = 8, ReadOnly = true, WordWrap = true,
            Text = DiagnosticDetail(rows[0]) };
#pragma warning restore CS0618
        list.ValueChanged += (_, _) =>
        {
            if (list.SelectedItem is int index && index >= 0 && index < rows.Count) details.Text = DiagnosticDetail(rows[index]);
        };
        var summary = new Label { Id = "diagnostics-summary", X = 2, Y = 0, Width = Dim.Fill(4), Height = 2,
            Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, DiagnosticsPref("summary"),
                snapshot.ModelCount, snapshot.ProviderCount, snapshot.DefaultModel ?? DiagnosticsPref("not_set"),
                snapshot.DefaultModelScope is { } scope ? UiCompletionResources.EnumLabel("scope", scope, _locale)
                    : DiagnosticsPref("default")) };
        var notice = new Label { Id = "diagnostics-notice", X = 2, Y = Pos.AnchorEnd(8), Width = Dim.Fill(4), Height = 2,
            Text = snapshot.MaintenanceWithoutUiFacade.Count == 0 ? "" : DiagnosticsPref("cli_only") + ": "
                + string.Join(", ", snapshot.MaintenanceWithoutUiFacade.Select(value =>
                    UiCompletionResources.EnumLabel("maintenance", value, _locale))) };
        var verify = new Button { Id = "diagnostics-journal-verify", X = 2, Y = Pos.AnchorEnd(6), Text = DiagnosticsPref("verify_journal"), Enabled = snapshot.JournalPresent };
        verify.Accepted += (_, _) =>
        {
            try
            {
                var report = host.VerifyJournal();
                notice.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, DiagnosticsPref(report.Ok ? "journal_ok" : "journal_issues"),
                    report.SessionCount, report.EventCount, report.TotalIssueCount);
                if (report.Issues.Count > 0)
                    details.Text = string.Join(Environment.NewLine, report.Issues.Take(12).Select(issue =>
                        DiagnosticsPref("issueCode") + ": " + issue.Code
                        + (issue.Sequence is { } seq ? " · " + DiagnosticsPref("sequence") + ": " + seq : "")
                        + (string.IsNullOrWhiteSpace(issue.EventType) ? "" : " · " + DiagnosticsPref("eventType") + ": " + issue.EventType)
                        + (string.IsNullOrWhiteSpace(issue.Field) ? "" : " · " + DiagnosticsPref("field") + ": " + issue.Field)));
            }
            catch (Exception) { notice.Text = DiagnosticsPref("operation_failed"); }
        };
        var gc = new Button { Id = "diagnostics-gc-preview", X = Pos.Percent(51), Y = Pos.AnchorEnd(6), Text = DiagnosticsPref("preview_gc") };
        gc.Accepted += (_, _) =>
        {
            try
            {
                var preview = host.PreviewWorkspaceGarbageCollection();
                var message = string.Format(System.Globalization.CultureInfo.InvariantCulture, DiagnosticsPref("gc_preview"),
                    preview.Scanned, preview.LiveReferenced, preview.KeptByGrace, preview.ReclaimableBytes);
                ShowDiagnosticsConfirmation(message, () =>
                {
                    try
                    {
                        var result = host.CollectWorkspaceGarbage(snapshot.WorkspaceRoot);
                        CloseOverlay(); ShowDiagnosticsPreferences("diagnostics_data");
                        ShowMessage(string.Format(System.Globalization.CultureInfo.InvariantCulture, DiagnosticsPref("gc_done"), result.ReclaimableBytes));
                    }
                    catch (Exception) { CloseOverlay(); ShowMessage(DiagnosticsPref("operation_failed")); }
                });
            }
            catch (Exception) { notice.Text = DiagnosticsPref("operation_failed"); }
        };
        var back = new Button { Id = "diagnostics-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = DiagnosticsPref("back") };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("diagnostics_data", categoryPage: true); };
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; CloseOverlay(); ShowPreferences("diagnostics_data", categoryPage: true); } };
        panel.Add(summary, list, details, new Label { Id = "diagnostics-help", X = 2, Y = 18,
            Width = Dim.Fill(4), Height = 3, Text = DiagnosticsPref("help") });
        ConfigurePreferenceScroll(panel, 22);
        frame.Add(panel, notice, verify, gc, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); list.SetFocus();
    }

    private void ShowDiagnosticsConfirmation(string description, Action confirm)
    {
        if (_window is null) return;
        CloseOverlay(); var frame = OverlayFrame(DiagnosticsPref("confirm_title"), 12); frame.Id = "diagnostics-confirm";
        frame.Add(MenuIndent.Help(description, 2, 5, "diagnostics-confirm-help"));
        var cancel = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = DiagnosticsPref("cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowDiagnosticsPreferences("diagnostics_data"); };
        var accept = new Button { X = Pos.Percent(35), Y = Pos.AnchorEnd(2), Width = Dim.Fill(2), Text = DiagnosticsPref("confirm_gc") };
        accept.Accepted += (_, _) => confirm(); frame.Add(cancel, accept); ApplyTheme(frame); _overlay = frame; _window.Add(frame); cancel.SetFocus();
    }

    private string DiagnosticDetail(DiagnosticRow row) => string.Join(Environment.NewLine,
        row.Title, row.Message, string.IsNullOrEmpty(row.KeyPath) ? "" : DiagnosticsPref("path") + ": " + row.KeyPath);
    private string DiagnosticsPref(string key) => _localization.Resolve("diagnosticsPreferences." + key);
    private sealed record DiagnosticRow(string Title, string Message, string KeyPath);
}
