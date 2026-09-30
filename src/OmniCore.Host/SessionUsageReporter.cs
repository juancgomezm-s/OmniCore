namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using OmniCore.Protocol;

/// <summary>
/// Construye el <see cref="UsageSnapshot"/> de la status line (ADR-0031 §3) a partir de datos reales:
/// tokens y costo del journal (artifacts de uso de cada Turn), costo estimado con el precio declarado
/// y cuota solo si el provider la informó (cabeceras de rate limit). Nada se inventa.
/// </summary>
public static class SessionUsageReporter
{
    /// <summary>Suma el uso de la sesión desde los <c>model.completed</c> del journal.</summary>
    public static (TokenTotals Tokens, decimal Cost, bool CostComplete) ReadSessionTotals(IEventStore store,
        IEventCodecRegistry codecs, IArtifactStore artifacts, SessionId sessionId)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        decimal cost = 0m;
        var costComplete = true;
        foreach (var evt in store.ReadFrom(sessionId, 1))
        {
            if (!evt.Type.ToString().Equals("model.completed", StringComparison.Ordinal)) continue;
            if (codecs.Decode(evt) is not ModelCompleted { ResponseArtifact: { } artifact }) { costComplete = false; continue; }
            var text = artifacts.GetText(artifact.Hash);
            if (string.IsNullOrEmpty(text)) { costComplete = false; continue; }
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("omnicoreUsage", out _)) { costComplete = false; continue; }
                input += Long(root, "input");
                output += Long(root, "output");
                cacheRead += Long(root, "cacheRead");
                cacheWrite += Long(root, "cacheWrite");
                if (root.TryGetProperty("costUsd", out var c) && c.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(c.GetString(), System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    cost += value;
                else
                    costComplete = false;
            }
            catch (JsonException) { costComplete = false; }
        }
        return (new TokenTotals(input, output, cacheRead, cacheWrite), cost, costComplete);
    }

    /// <summary>
    /// Snapshot con las reglas de ADR-0031: costo <c>Estimated</c> solo con precio declarado completo;
    /// sin precio en un modelo local no aplica; sin precio en uno remoto no se conoce. La cuota es
    /// <c>Reported</c> solo si el provider envió una ventana con límite y restante.
    /// </summary>
    public static UsageSnapshot Build(TokenTotals tokens, decimal cost, bool costComplete, bool priceDeclared, bool localModel,
        IReadOnlyList<RateLimitWindow> windows, DateTimeOffset now)
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
        return new UsageSnapshot(tokens, costMetric, quota, now);
    }

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
