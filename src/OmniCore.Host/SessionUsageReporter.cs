namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using OmniCore.Protocol;

/// <summary>
/// Construye el <see cref="UsageSnapshot"/> de la status line (ADR-0031 §3) a partir de datos reales:
/// tokens y costo del journal (artifacts de uso de cada Turn), costo estimado con el precio declarado
/// y cuota solo si el provider la informó (cabeceras de rate limit). Nada se inventa.
/// </summary>
public static partial class SessionUsageReporter
{
    /// <summary>Compatibility view over unique model-step and compaction invocation spending.</summary>
    public static (TokenTotals Tokens, decimal Cost, bool CostComplete) ReadSessionTotals(IEventStore store,
        IEventCodecRegistry codecs, IArtifactStore artifacts, SessionId sessionId)
    {
        var reading = ReadConversation(store, codecs, artifacts, sessionId);
        return (reading.Tokens.Value ?? new TokenTotals(0, 0, 0, 0), reading.Cost.Value?.Amount ?? 0m,
            reading.Cost.Availability == MetricAvailability.Estimated);
    }

    /// <summary>
    /// Snapshot con las reglas de ADR-0031: costo <c>Estimated</c> solo con precio declarado completo;
    /// sin precio en un modelo local no aplica; sin precio en uno remoto no se conoce. La cuota es
    /// <c>Reported</c> solo si el provider envió una ventana con límite y restante.
    /// </summary>
    public static UsageSnapshot Build(TokenTotals tokens, decimal cost, bool costComplete, bool priceDeclared, bool localModel,
        IReadOnlyList<RateLimitWindow> windows, DateTimeOffset now, Metric<TokenTotals>? tokenMeasurement = null)
    {
        Metric<Money> costMetric = priceDeclared && costComplete
            ? new(MetricAvailability.Estimated, new Money(cost, "USD"), "declared-price")
            : priceDeclared
                ? new(MetricAvailability.Unknown, null, "declared-price")
                : localModel
                    ? new(MetricAvailability.NotApplicable, null, null)
                    : new(MetricAvailability.NotSupported, null, null);

        var window = windows.Where(w => w.Limit is > 0 && w.Remaining is not null)
            .OrderBy(w => w.Kind switch
            {
                RateLimitWindowKind.Tokens => 0,
                RateLimitWindowKind.InputTokens => 1,
                RateLimitWindowKind.OutputTokens => 2,
                _ => 3,
            })
            .FirstOrDefault();
        Metric<QuotaInfo> quota = window is null
            ? new(MetricAvailability.NotSupported, null, null)
            : new(MetricAvailability.Reported, new QuotaInfo(QuotaKind.RateLimitWindow, window.Remaining, window.Limit,
                window.Kind == RateLimitWindowKind.Requests ? "requests" : "tokens", window.ResetsAt), "provider-rate-limit");
        return new UsageSnapshot(tokens, costMetric, quota, now, tokenMeasurement);
    }

}
