using System.Collections.ObjectModel;
using System.Globalization;
using OmniCore.Protocol;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Terminal.Gui.Drawing;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void ShowCompanionModelSettings()
    {
        if (_window is null || _turnHost?.CompanionHub is not { } hub) { ShowMessage("Esta sesión no ofrece configuración de la mascota."); return; }
        CloseCompanionHub(); CloseOverlay();
        var frame = OverlayFrame("Mascota · Modelo y consumo", 28); frame.Id = "companion-model-settings";
        var status = new Label { Id = "companion-model-status", X = 2, Y = 1, Width = Dim.Fill(2), Height = 2, Text = "Cargando configuración propia…" };
        var local = new CompanionRowButton { Id = "companion-model-local", X = 2, Y = 3, Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None, Text = "Solo local: No", Enabled = false };
        var list = new ListView { Id = "companion-model-list", X = 2, Y = 4, Width = Dim.Fill(2), Height = 5 };
#pragma warning disable CS0618 // Same pinned scrollable presenter as the companion chat.
        var usage = new TextView { Id = "companion-model-usage", X = 2, Y = 10, Width = Dim.Fill(2), Height = Dim.Fill(3), ReadOnly = true, WordWrap = true };
#pragma warning restore CS0618
        var save = new CompanionRowButton { Id = "companion-model-save", X = 2, Y = Pos.AnchorEnd(2), Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None, Text = "Guardar modelo", Enabled = false };
        var refresh = new CompanionRowButton { Id = "companion-model-refresh", X = Pos.Right(save) + 1, Y = Pos.AnchorEnd(2), Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None, Text = "Actualizar consumo" };
        refresh.Accepted += (_, args) => { args.Handled = true; ShowCompanionModelSettings(); };
        var close = new CompanionRowButton { Id = "companion-model-close", X = 2, Y = Pos.AnchorEnd(1), Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None, Text = "Cerrar" };
        close.Accepted += (_, args) => { args.Handled = true; CloseOverlay(); };
        frame.Add(status, local, list, usage, save, refresh, close,
            new Label { X = 2, Y = 9, Width = Dim.Fill(2), Text = "Sin cuotas · Herramientas de mascota" });
        CompanionModelOption[] models = [], filtered = []; var localOnly = false;
        string? Selected() => list.SelectedItem is { } n && n >= 0 && n < filtered.Length ? filtered[n].Id : null;
        void Refresh(string? selected)
        {
            filtered = models.Where(m => !localOnly || m.Local).ToArray();
            list.SetSource(new ObservableCollection<string>(filtered.Select(m => m.Id + " · " + m.Provider + (m.Local ? " · Local" : ""))));
            if (filtered.Length > 0) list.SelectedItem = Math.Max(0, Array.FindIndex(filtered, m => m.Id == selected));
            local.Text = localOnly ? "Solo local: Sí" : "Solo local: No"; save.Enabled = filtered.Length > 0;
            if (filtered.Length == 0) status.Text = localOnly ? "No hay modelos locales configurados.\nPuedes añadirlos en Motores locales." : "No hay modelos configurados.";
        }
        local.Accepted += (_, args) => { args.Handled = true; var selected = Selected(); localOnly = !localOnly; Refresh(selected); };
        save.Accepted += (_, args) =>
        {
            args.Handled = true; var settings = new CompanionChatSettings(Selected(), localOnly); save.Enabled = local.Enabled = list.Enabled = false;
            _ = Task.Run(() =>
            {
                try
                {
                    hub.SaveChatSettings(settings, _turnHost.CompanionModels());
                    _uiActions.Enqueue(() => { if (_overlay != frame) return; status.Text = "Modelo propio: " + settings.ModelId + "\nConsumo independiente, sin cuotas."; save.Enabled = local.Enabled = list.Enabled = true; });
                }
                catch (Exception error) { _uiActions.Enqueue(() => { if (_overlay == frame) { status.Text = error.Message; save.Enabled = local.Enabled = list.Enabled = true; } }); }
            });
        };
        _overlay = frame; _window.Add(frame); ApplyTheme(frame); CompactCompanionButtons(frame); frame.SetFocus();
        _ = Task.Run(() =>
        {
            try
            {
                var settings = hub.ChatSettings(); var options = _turnHost.CompanionModels(); var totals = hub.ChatUsage();
                var report = FormatCompanionUsage(totals);
                _uiActions.Enqueue(() =>
                {
                    if (_overlay != frame) return;
                    models = options; localOnly = settings.LocalOnly; local.Enabled = true;
                    status.Text = "Modelo propio: " + (settings.ModelId ?? "sin elegir") + "\nIndependiente del chat principal.";
                    usage.Text = report; Refresh(settings.ModelId);
                });
            }
            catch (Exception error) { _uiActions.Enqueue(() => { if (_overlay == frame) status.Text = error.Message; }); }
        });
    }
    internal static string FormatCompanionUsage(IReadOnlyList<CompanionChatUsage> totals)
    {
        if (totals.Count == 0) return "Consumo de la mascota\nTodavía no hay consultas.";
        static string Money(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture) + " USD";
        return "Consumo acumulado de la mascota\n" + string.Join("\n", totals.Select(t =>
            $"{t.Model} · {t.Provider}\nConsultas: {t.Requests} · Respuestas: {t.Responses} · Fallos: {t.Failed}\n"
            + $"Tokens reportados: {t.Input} entrada / {t.Output} salida\n"
            + $"Detalle incluido: caché {t.CacheRead}/{t.CacheWrite} · razonamiento {t.Reasoning}\n"
            + $"Sin tokens completos: {t.Requests - t.MeasuredTokenRequests}\n"
            + "Estimación tarifaria: " + (t.PricedResponses > 0 ? Money(t.EstimatedUsd) : "no disponible") + $" · Sin precio: {t.Requests - t.PricedResponses}\n"
            + "Coste informado por proveedor: " + (t.ReportedCostResponses > 0 ? Money(t.ReportedUsd) : "no disponible")));
    }
}
