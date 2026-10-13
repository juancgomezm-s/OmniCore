using System.Collections.ObjectModel;
using OmniCore.Client;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void ShowCommandPreferences()
    {
        if (_window is null) return;
        CloseOverlay();
        var rows = CommandCatalogPresentation.Read(_client.Query("commands", CancellationToken.None)?.Json, _localization);
        var frame = OverlayFrame(_localization.Resolve("catalog.title"), Math.Max(16, _window.Frame.Height - 3));
        frame.Id = "command-preferences"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var search = new TextField { Id = "commands-filter", X = 2, Y = 2, Width = Dim.Fill(4), Height = 1 };
        var list = new ListView { Id = "commands-list", X = 2, Y = 4, Width = Dim.Fill(4), Height = Dim.Percent(30), ShowMarks = false };
#pragma warning disable CS0618
        var detail = new TextView { Id = "commands-detail", X = 2, Y = Pos.Bottom(list) + 1,
            Width = Dim.Fill(4), Height = Dim.Fill(5), ReadOnly = true, WordWrap = true };
#pragma warning restore CS0618
        var prepare = new Button { Id = "commands-prepare", X = 2, Y = Pos.AnchorEnd(2), Text = _localization.Resolve("catalog.prepare") };
        var back = new Button { Id = "commands-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = Pref("back") };
        var visible = rows.ToArray();
        CommandMenuRow? Selected() => list.SelectedItem is int i && i >= 0 && i < visible.Length ? visible[i] : null;
        void Select()
        {
            var row = Selected(); prepare.Enabled = row?.Enabled == true;
            detail.Text = row is null ? _localization.Resolve("tools.ui.no_matches") : string.Join(Environment.NewLine,
                row.Description, "", _localization.Resolve("catalog.canonical") + ": /" + row.Name,
                _localization.Resolve("catalog.identity") + ": " + row.Id,
                _localization.Resolve("tools.ui.source") + CommandCatalogPresentation.DisplaySource(row, _localization),
                _localization.Resolve("catalog.arguments") + ": " + string.Join(" ", row.Arguments),
                row.ExternalDescription ? _localization.Resolve("catalog.external") : "",
                row.Enabled ? _localization.Resolve("catalog.ready") : _localization.Resolve("catalog.blocked"),
                row.DisabledReason ?? "");
        }
        void Filter()
        {
            var query = search.Text.ToString();
            visible = rows.Where(row => string.Join(" ", row.Name, row.DisplayName, row.Description, row.Id)
                .Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            list.SetSource(new ObservableCollection<string>(visible.Select(row => "/" + row.DisplayName + " · " + row.Description)));
            if (visible.Length > 0) list.SelectedItem = 0;
            Select();
        }
        search.TextChanged += (_, _) => Filter(); list.ValueChanged += (_, _) => Select();
        list.Accepted += (_, e) => { e.Handled = true; detail.SetFocus(); };
        prepare.Accepted += (_, _) =>
        {
            if (Selected() is not { Enabled: true } row || _composer is null) return;
            // Never overwrite an unsent message. Preparing does not execute any action.
            if (!string.IsNullOrWhiteSpace(_composer.Text.ToString()))
            { detail.Text = _localization.Resolve("catalog.draft_exists"); return; }
            CloseOverlay(); _composer.Text = "/" + row.DisplayName + " "; _composer.MoveEnd(); _composer.SetFocus();
        };
        back.Accepted += (_, _) => { if (!ReturnToChatIfOpenedFromChat()) { CloseOverlay(); ShowPreferences("extensions_skills", categoryPage: true); } };
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 1, Text = _localization.Resolve("catalog.purpose") }, search, list, detail, prepare, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true;
        Filter(); search.SetFocus();
    }
}
