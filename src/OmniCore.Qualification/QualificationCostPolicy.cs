namespace OmniCore.Qualification;

using OmniCore.Domain;

/// <summary>
/// Clasificación de costo de una configuración de modelo para la suite de cualificación (M5,
/// ADR-0007 §7). <see cref="LocalZeroCost"/> es gratis de forma EXPLÍCITA (provider local o fake
/// inyectado); <see cref="DeclaredPricing"/> tiene precios de entrada/salida declarados y completos;
/// <see cref="UnknownPaidPricing"/> es un provider remoto con credencial y sin precios declarados:
/// la suite falla en cerrado y no gasta (nunca se asume gratis).
/// </summary>
public enum QualificationCostKind
{
    LocalZeroCost,
    DeclaredPricing,
    UnknownPaidPricing,
}

/// <summary>
/// Precios declarados por millón de tokens (USD). Es la copia local del contrato de pricing del
/// Host (mismo shape que <c>ModelPricing</c>): Qualification no puede depender de Host ni de
/// Models (frontera de ADR-0009). <see cref="IsComplete"/> exige ambos precios.
/// </summary>
public sealed record QualificationPricing(decimal? InputPricePerMillionUsd, decimal? OutputPricePerMillionUsd)
{
    public bool IsComplete => InputPricePerMillionUsd is not null && OutputPricePerMillionUsd is not null;

    public decimal? CostUsd(long inputTokens, long outputTokens)
    {
        if (!IsComplete)
        {
            return null;
        }

        return inputTokens / 1_000_000m * InputPricePerMillionUsd!.Value
            + outputTokens / 1_000_000m * OutputPricePerMillionUsd!.Value;
    }

    public decimal? CostUsd(TokenUsage usage) => CostUsd(usage.Input, usage.Output);
}

/// <summary>
/// Política de costo de una ejecución de cualificación (M5): clasifica el provider, acota los
/// tokens de salida del request y estima el costo MÁXIMO conservador por probe. La estimación
/// conservadora multiplica una cota superior de tokens de entrada por el precio de entrada, más
/// <see cref="MaxOutputTokens"/> por el precio de salida: nunca subestima el gasto de un probe.
/// </summary>
public sealed class QualificationCostPolicy
{
    /// <summary>Tope de salida por defecto cuando el modelo no declara uno utilizable.</summary>
    public const long FallbackMaxOutputTokens = 4096;

    /// <summary>
    /// Margen fijo de tokens suplementarios por request (rol de mensaje, framing del chat) sumado
    /// a la estimación por caracteres. Conservador a propósito.
    /// </summary>
    public const long InputFramingTokens = 64;

    public QualificationCostKind Kind { get; }

    public QualificationPricing? Pricing { get; }

    public long MaxOutputTokens { get; }

    private QualificationCostPolicy(QualificationCostKind kind, QualificationPricing? pricing, long maxOutputTokens)
    {
        if (maxOutputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens),
                "QualificationCostPolicy.MaxOutputTokens debe ser positivo");
        }

        if (kind == QualificationCostKind.DeclaredPricing && pricing is not { IsComplete: true })
        {
            throw new ArgumentException("DeclaredPricing exige un QualificationPricing completo", nameof(pricing));
        }

        Kind = kind;
        Pricing = pricing;
        MaxOutputTokens = maxOutputTokens;
    }

    /// <summary>Provider local o fake inyectado: gratis de forma explícita, nunca se asume costo.</summary>
    public static QualificationCostPolicy LocalZeroCost(long maxOutputTokens = FallbackMaxOutputTokens) =>
        new(QualificationCostKind.LocalZeroCost, null, maxOutputTokens);

    /// <summary>Provider con precios declarados y completos: se estima y se mide el costo real.</summary>
    public static QualificationCostPolicy Declared(QualificationPricing pricing, long maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        if (!pricing.IsComplete)
        {
            throw new ArgumentException("Declared exige precios de entrada y salida", nameof(pricing));
        }

        return new QualificationCostPolicy(QualificationCostKind.DeclaredPricing, pricing, maxOutputTokens);
    }

    /// <summary>
    /// Provider remoto con credencial y sin precios declarados. La suite debe fallar en cerrado:
    /// construir la política es válido, pero ejecutarla lanza
    /// <see cref="QualificationUnknownPricingException"/> sin emitir ninguna llamada.
    /// </summary>
    public static QualificationCostPolicy UnknownPaid(long maxOutputTokens = FallbackMaxOutputTokens) =>
        new(QualificationCostKind.UnknownPaidPricing, null, maxOutputTokens);

    /// <summary>
    /// Cota superior determinista de tokens de entrada de un prompt: caracteres/3 (más conservador
    /// que el heurístico caracteres/4 del runtime) más el framing fijo. No usa el tokenizer real:
    /// la cualificación debe poder estimar sin Infrastructure (frontera de ADR-0009).
    /// </summary>
    public static long EstimateInputTokens(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return (long)Math.Ceiling(prompt.Length / 3.0) + InputFramingTokens;
    }

    /// <summary>
    /// Costo MÁXIMO conservador de un probe, o null si el pricing es desconocido. Para local es 0
    /// explícito. Toma el mayor de <see cref="Probe.MaxCostUsd"/> y la estimación por tokens para
    /// no relajar topes declarados por la suite.
    /// </summary>
    public decimal? EstimateProbeMaxCostUsd(Probe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        switch (Kind)
        {
            case QualificationCostKind.LocalZeroCost:
                return Math.Max(probe.MaxCostUsd, 0m);
            case QualificationCostKind.UnknownPaidPricing:
                return null;
            default:
                var inputTokens = EstimateInputTokens(probe.Prompt);
                var priced = Pricing!.CostUsd(inputTokens, MaxOutputTokens) ?? 0m;
                return Math.Max(probe.MaxCostUsd, priced);
        }
    }

    /// <summary>
    /// Costo MÁXIMO conservador de una suite completa, o null si el pricing es desconocido. Es el
    /// valor que se compara con el tope aceptado ANTES de invocar al provider.
    /// </summary>
    public decimal? EstimateSuiteMaxCostUsd(IEnumerable<Probe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        if (Kind == QualificationCostKind.UnknownPaidPricing)
        {
            return null;
        }

        decimal total = 0m;
        foreach (var probe in probes)
        {
            total += EstimateProbeMaxCostUsd(probe) ?? 0m;
        }

        return total;
    }

    /// <summary>
    /// Costo REAL con la evidencia de uso del provider, o null si no se puede calcular. Para local
    /// es 0 explícito (aunque el provider no reporte uso). Para pricing declarado exige uso
    /// reportado y no nulo: uso incompleto → null, jamás 0.
    /// </summary>
    public decimal? ActualCostUsd(TokenUsage? usage)
    {
        if (Kind == QualificationCostKind.LocalZeroCost)
        {
            return 0m;
        }

        if (Kind != QualificationCostKind.DeclaredPricing || usage is null)
        {
            return null;
        }

        if (usage.Input <= 0 && usage.Output <= 0)
        {
            return null;
        }

        return Pricing!.CostUsd(usage);
    }
}
