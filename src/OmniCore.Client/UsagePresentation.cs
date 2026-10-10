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

    /// <summary>Account/subscription readings stay distinct from response token/request rate limits.
    /// Unknown values remain unknown; the full snapshot retains source, account, measurement date and resets.</summary>
    public static string? AccountQuota(ProviderQuotaSnapshot? quota)
    {
        if (quota is null || quota.Availability == MetricAvailability.NotApplicable) return null;
        var prefix = quota.ProviderId;
        var parts = new List<string>();
        foreach (var window in quota.Windows)
        {
            var duration = window.DurationMinutes;
            var label = duration is > 0 && duration % 1440 == 0 ? (duration / 1440).Value.ToString(CultureInfo.InvariantCulture) + "d"
                : duration is > 0 && duration % 60 == 0 ? (duration / 60).Value.ToString(CultureInfo.InvariantCulture) + "h"
                : duration is > 0 ? duration.Value.ToString(CultureInfo.InvariantCulture) + "m" : window.Id;
            var metric = window.RemainingPercent;
            var reported = quota.Availability is MetricAvailability.Reported or MetricAvailability.Stale
                && metric.Availability is MetricAvailability.Reported or MetricAvailability.Stale;
            var text = reported && metric.Value is { } value && double.IsFinite(value) && value is >= 0 and <= 100
                ? value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : Missing;
            var stale = quota.Availability == MetricAvailability.Stale || metric.Availability == MetricAvailability.Stale;
            parts.Add(prefix + " " + label + " remaining " + text + (stale ? " stale" : ""));
        }
        foreach (var credit in quota.Credits)
        {
            if (!Enum.IsDefined(credit.Scope)) continue;
            var amount = credit.Amount;
            var text = amount.Availability is MetricAvailability.Reported or MetricAvailability.Stale
                && amount.Value is >= 0 ? amount.Value.Value.ToString("0.##", CultureInfo.InvariantCulture) : Missing;
            var scope = credit.Scope == CreditScope.AccountBalance ? "account balance" : "key limit";
            var stale = quota.Availability == MetricAvailability.Stale || amount.Availability == MetricAvailability.Stale;
            parts.Add(prefix + " " + scope + " " + text + " " + credit.Unit
                + (credit.Currency is null ? "" : " " + credit.Currency) + (stale ? " stale" : ""));
        }
        return parts.Count == 0 ? prefix + " quota " + Missing : string.Join(" · ", parts);
    }

    public static string? Account(AccountConnectionSnapshot? account, Localization localization)
    {
        if (account is null) return null;
        return localization.Resolve("claude.oauth.account.status", new Dictionary<string, string>
        {
            ["provider"] = account.ProviderId, ["plan"] = account.Plan ?? Missing,
            ["tier"] = account.Tier ?? Missing,
            ["expires"] = account.ExpiresAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) ?? Missing,
        });
    }

    public static string Tokens(TokenTotals totals)
    {
        if (totals.Input < 0 || totals.Output < 0 || totals.CacheRead < 0 || totals.CacheWrite < 0)
            return "session " + Missing + " tok";
        long total;
        try { total = checked(totals.Input + totals.Output); }
        catch (OverflowException) { return "session " + Missing + " tok"; }
        if (total < 1000) return "session " + total.ToString(CultureInfo.InvariantCulture) + " tok";
        var thousands = Math.Round(total / 1000d, 1);
        return "session " + thousands.ToString("0.#", CultureInfo.InvariantCulture) + "k tok";
    }

    public static string Tokens(Metric<TokenTotals> measurement) => measurement.Availability switch
    {
        MetricAvailability.Reported or MetricAvailability.Stale when measurement.Value is not null => Tokens(measurement.Value),
        MetricAvailability.Estimated when measurement.Value is not null => "≈" + Tokens(measurement.Value),
        _ => "session " + Missing + " tok",
    };

    private static string Dollars(decimal amount) =>
        "$" + amount.ToString(amount >= 0.01m ? "0.00" : "0.0000", CultureInfo.InvariantCulture);
}
