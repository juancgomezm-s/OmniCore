using System.Collections.ObjectModel;
using System.Globalization;
using OmniCore.Protocol;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Terminal.Gui.Drawing;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void ShowCompanionModelSettings() => ShowCompanionSettings();
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
