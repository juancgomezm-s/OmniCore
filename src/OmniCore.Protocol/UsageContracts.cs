namespace OmniCore.Protocol;

/// <summary>Disponibilidad de una métrica de la status line (ADR-0031 §3): nunca se inventa un dato.</summary>
public enum MetricAvailability
{
    Reported,
    Estimated,
    NotSupported,
    NotApplicable,
    Unknown,
    Stale,
}

/// <summary>Tipo de cuota que informa un provider.</summary>
public enum QuotaKind
{
    Credits,
    RateLimitWindow,
    TokenAllowance,
}

/// <summary>Valor de una métrica con su disponibilidad y fuente (p. ej. <c>anthropic-ratelimit</c>).</summary>
public sealed record Metric<T>(MetricAvailability Availability, T? Value, string? Source, DateTimeOffset? AsOf = null);

public sealed record Money(decimal Amount, string Currency);

public sealed record QuotaInfo(QuotaKind Kind, decimal? Remaining, decimal? Limit, string Unit, DateTimeOffset? ResetsAt);

public sealed record TokenTotals(long Input, long Output, long CacheRead, long CacheWrite);

/// <summary>Uso de la sesión que viaja al cliente (ADR-0031 §3).</summary>
/// <remarks>SessionTokenMeasurement carries availability. New clients prefer it over the legacy
/// numeric slots, which cannot represent unknown consumption. Null retains legacy behavior.
/// AccountQuota is the cached account/subscription reading for the active session/provider,
/// independent of Remaining (response rate limits). Its own AsOf, source, availability,
/// windows, reset times and credit scopes are retained; absence is not a zero balance.</remarks>
public sealed record UsageSnapshot(TokenTotals SessionTokens, Metric<Money> SessionCost, Metric<QuotaInfo> Remaining,
    DateTimeOffset AsOf, Metric<TokenTotals>? SessionTokenMeasurement = null,
    ProviderQuotaSnapshot? AccountQuota = null);
