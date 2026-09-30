namespace OmniCore.Client;

using System.Globalization;
using OmniCore.Protocol;

/// <summary>
/// Formato de las métricas de uso de la status line (ADR-0031 §3). La cuota nunca se estima: sin dato
/// informado se muestra <c>—</c>; lo que no aplica (costo de un modelo local) se omite (<c>null</c>).
/// </summary>
public static class UsagePresentation
{
    public const string Missing = "—";

    public static string? Cost(Metric<Money> cost) => cost.Availability switch
    {
        MetricAvailability.Reported or MetricAvailability.Stale when cost.Value is not null => Dollars(cost.Value.Amount),
        MetricAvailability.Estimated when cost.Value is not null => "≈" + Dollars(cost.Value.Amount),
        MetricAvailability.NotApplicable => null,
        _ => Missing,
    };

    public static string? Remaining(Metric<QuotaInfo> quota)
    {
        if (quota.Availability == MetricAvailability.NotApplicable) return null;
        if (quota.Availability is not (MetricAvailability.Reported or MetricAvailability.Stale) || quota.Value is null)
            return Missing;
        var info = quota.Value;
        switch (info.Kind)
        {
            case QuotaKind.Credits when info.Remaining is { } credits:
                return "credits $" + credits.ToString("0.00", CultureInfo.InvariantCulture);
            case QuotaKind.RateLimitWindow when info.Remaining is { } left && info.Limit is { } limit && limit > 0:
                var percent = (int)Math.Floor(left / limit * 100m);
                return info.Unit + " " + percent.ToString(CultureInfo.InvariantCulture) + "%";
            case QuotaKind.TokenAllowance when info.Remaining is { } allowance:
                return allowance.ToString("0.##", CultureInfo.InvariantCulture) + " " + info.Unit;
            default:
                return Missing;
        }
    }

    public static string Tokens(TokenTotals totals)
    {
        var total = totals.Input + totals.Output;
        if (total < 1000) return "session " + total.ToString(CultureInfo.InvariantCulture) + " tok";
        var thousands = Math.Round(total / 1000d, 1);
        return "session " + thousands.ToString("0.#", CultureInfo.InvariantCulture) + "k tok";
    }

    private static string Dollars(decimal amount) =>
        "$" + amount.ToString(amount >= 0.01m ? "0.00" : "0.0000", CultureInfo.InvariantCulture);
}
