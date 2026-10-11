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
        var purpose = new Label { Id = "tools-purpose", X = 2, Y = 2, Width = Dim.Fill(2), Height = 2,
            Text = Ui("Qué hace cada herramienta del agente.\nConsulta: no ejecuta ni activa/desactiva.",
                "What each agent tool does.\nRead-only: no execution or on/off switches.") };
        var search = new TextField { Id = "tools-search", X = 10, Y = 4, Width = Dim.Fill(2) };
        var list = new ListView { Id = "tools-list", X = 2, Y = 6, Width = Dim.Fill(2),
            Height = Dim.Func(parent => Math.Max(1, ((parent?.Frame.Height ?? 21) - 10) / 2), frame),
            ShowMarks = false };
        var detail = new StyledTextPresenter { Id = "tools-detail", X = 2, Y = Pos.Bottom(list) + 1, Width = Dim.Fill(2),
            Height = Dim.Fill(3), CanFocus = true };
        var close = new Button { X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Text = Ui("Cerrar", "Close") };
        close.Accepted += (_, _) => CloseOverlay();
        var rows = snapshot.Tools.ToArray();
        string Visibility(ToolInspectionRow row) => row.Visible ? Ui("Visible", "Visible") : Ui("No visible", "Not visible");
        string Description(ToolInspectionRow row) => ToolDisplayText(ToolInspectionPresentation.Description(row, _localization));
        string Detail(ToolInspectionRow row) => ToolDisplayText(row.VisibleName) + "\n" + Description(row)
            + "\n\n" + Ui("Restricción: ", "Restriction: ") + ToolInspectionPresentation.Decision(row, _localization)
            + "\n" + Visibility(row) + " · " + Ui("no equivale a permiso de ejecución", "does not grant execution permission")
            + "\n\n" + Ui("Control: las reglas de usuario en permissions.yaml permiten allow (permitir), ask (preguntar) y deny (denegar). No superan los límites del modo o del modelo. /permissions lista autorizaciones guardadas; no activa herramientas.",
                "Control: user rules in permissions.yaml support allow, ask and deny. They cannot override mode or model limits. /permissions lists saved grants; it does not enable tools.")
            + "\n\nID: " + ToolDisplayText(row.ToolId)
            + "\n" + Ui("Origen: ", "Source: ") + ToolDisplayText(row.Source)
            + "\n" + Ui("Clase de efecto (técnica): ", "Effect class (technical): ") + ToolDisplayText(row.EffectClass)
            + "\n" + Ui("Política reportada (técnica): ", "Reported policy (technical): ") + ToolDisplayText(row.Decision)
            + (string.IsNullOrWhiteSpace(row.Description) ? "" : "\n" + Ui("Definición original: ", "Original definition: ") + ToolDisplayText(row.Description));
        void UpdateDetail()
        {
            summary.Text = Ui("Modo: ", "Mode: ") + ToolDisplayText(snapshot.Mode ?? Ui("no reportado", "not reported"))
                + $" · {rows.Length}/{snapshot.Tools.Count} · " + Ui("sólo consulta", "read-only");
            detail.Text = list.SelectedItem is int index && index >= 0 && index < rows.Length
                ? Detail(rows[index])
                : snapshot.Tools.Count == 0 ? Ui("El Host no reportó herramientas.", "The Host reported no tools.")
                : Ui("Sin coincidencias. Cambia el filtro.", "No matches. Change the filter.");
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
        frame.Add(summary, purpose, new Label { X = 2, Y = 4, Width = 8, Text = Ui("Filtrar", "Filter") },
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
