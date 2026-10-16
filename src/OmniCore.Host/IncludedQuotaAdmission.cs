namespace OmniCore.Host;

using OmniCore.Protocol;
using OmniCore.Domain;

/// <summary>Reported low subscription windows suppress optional background model work.
/// Normal conversation is not gated by an artificial remaining-percentage threshold.</summary>
internal static class IncludedQuotaAdmission
{
    internal static bool AllowsMeta(OmniServer server, SessionId session, string provider) =>
        LowWindows(server.Observability.Quota(session, provider)).Length == 0;

    internal static ProviderUsageWindow[] LowWindows(ProviderQuotaSnapshot? quota)
    {
        var now = DateTimeOffset.UtcNow;
        if (quota is null || quota.AsOf > now
            || quota.Availability is not (MetricAvailability.Reported or MetricAvailability.Stale)) return [];
        return quota.Windows.Where(window => window.RemainingPercent.Availability == MetricAvailability.Reported
            && window.RemainingPercent.Value is >= 0 and < 10
            && (window.ResetsAt is null || window.ResetsAt > now)).OrderBy(window => window.Id, StringComparer.Ordinal).ToArray();
    }

}
