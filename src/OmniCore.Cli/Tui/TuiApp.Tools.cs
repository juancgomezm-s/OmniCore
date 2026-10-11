using System.Collections.ObjectModel;
using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void ShowTools(ToolsInspection snapshot)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(Ui("Herramientas", "Tools"), Math.Max(14, _window.Frame.Height - 5));
        frame.Height = Dim.Func(parent => Math.Max(16, (parent?.Frame.Height ?? 25) - 4), _window);
        var summary = new Label { Id = "tools-summary", X = 2, Y = 1, Width = Dim.Fill(2), Height = 1 };
        var search = new TextField { Id = "tools-search", X = 10, Y = 2, Width = Dim.Fill(2) };
        var list = new ListView { Id = "tools-list", X = 2, Y = 4, Width = Dim.Fill(2), Height = Dim.Fill(10),
            ShowMarks = false };
        var detail = new StyledTextPresenter { Id = "tools-detail", X = 2, Y = Pos.AnchorEnd(9), Width = Dim.Fill(2),
            Height = 6, CanFocus = true };
        var close = new Button { X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Text = Ui("Cerrar", "Close") };
        close.Accepted += (_, _) => CloseOverlay();
        var rows = snapshot.Tools.ToArray();
        string Visibility(ToolInspectionRow row) => row.Visible ? Ui("Visible", "Visible") : Ui("No visible", "Not visible");
        void UpdateDetail()
        {
            summary.Text = Ui("Modo: ", "Mode: ") + ToolDisplayText(snapshot.Mode ?? Ui("no reportado", "not reported"))
                + $" · {rows.Length}/{snapshot.Tools.Count} · " + Ui("sólo consulta", "read-only");
            detail.Text = list.SelectedItem is int index && index >= 0 && index < rows.Length
                ? ToolDisplayText(rows[index].VisibleName) + "\nID: " + ToolDisplayText(rows[index].ToolId)
                    + "\n" + Ui("Origen: ", "Source: ") + ToolDisplayText(rows[index].Source)
                    + "\n" + Ui("Efecto: ", "Effect: ") + ToolDisplayText(rows[index].EffectClass)
                    + "\n" + Ui("Política: ", "Policy: ") + ToolDisplayText(rows[index].Decision)
                    + "\n" + Visibility(rows[index]) + " · " + Ui("no equivale a permiso de ejecución", "does not grant execution permission")
                : snapshot.Tools.Count == 0 ? Ui("El Host no reportó herramientas.", "The Host reported no tools.")
                : Ui("Sin coincidencias. Cambia el filtro.", "No matches. Change the filter.");
        }
        void Filter()
        {
            var query = search.Text.ToString().Trim();
            rows = snapshot.Tools.Where(row => string.Join(" ", row.VisibleName, row.ToolId, row.Source,
                row.EffectClass, row.Decision).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SetSource(new ObservableCollection<string>(rows.Select(row =>
                Visibility(row) + " · " + ToolDisplayText(row.VisibleName) + " · " + ToolDisplayText(row.Decision))));
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
        frame.Add(summary, new Label { X = 2, Y = 2, Width = 8, Text = Ui("Filtrar", "Filter") },
            search, list, detail, close, new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2),
                Text = Ui("↑↓ · Enter detalle · Ctrl+F · Esc", "↑↓ · Enter details · Ctrl+F · Esc") });
        ApplyTheme(_window, dimmed: true);
        if (_conversation is not null) { _conversation.Dimmed = true; _conversation.SetNeedsDraw(); }
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        Filter();
        if (rows.Length > 0) list.SetFocus(); else search.SetFocus();
    }

    // Host diagnostic fields are data, never terminal control sequences or line breaks.
    private static string ToolDisplayText(string text) => string.Concat(text.Select(ch => char.IsControl(ch) ? ' ' : ch));
}
