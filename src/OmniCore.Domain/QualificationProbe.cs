namespace OmniCore.Domain;

/// <summary>
/// Tipo de probe de la suite de cualificación (ADR-0007 §6, M5). La suite `quick` inicial cubre
/// tres tipos de lectura/razonamiento estructurado puntuado por reglas exactas, sin LLM juez y
/// sin escribir repos reales.
/// </summary>
public enum ProbeKind
{
    Reading,
    Reasoning,
    StructuredOutput,
}

/// <summary>
/// Estado de un probe individual.
/// </summary>
public enum ProbeStatus
{
    Passed,
    Failed,
    Error,
    Timeout,
    NotRun,
}

/// <summary>
/// Identificador estable y nombrado de un probe. No es un UUID: es el nombre fijo de la suite
/// (p. ej. "reading-basic"). Dos ejecuciones con el mismo ProbeId y la misma suite version son
/// comparables.
/// </summary>
public sealed class ProbeId
{
    private readonly string _value;

    private ProbeId(string value)
    {
        if (value is null || value.Length == 0)
        {
            throw new ArgumentException("ProbeId no puede ser null ni vacío", nameof(value));
        }
        _value = value;
    }

    public static ProbeId Parse(string value) => new(value);

    public static ProbeId WellKnown(string name) => new(name);

    public override string ToString() => _value;

    public override bool Equals(object? other) =>
        other is ProbeId p && p._value.Equals(_value, StringComparison.Ordinal);

    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// Definición de un probe de cualificación (ADR-0007 §6, M5). El prompt está en inglés porque
/// el modelo recibe prompts en inglés (ADR-0040). El costo máximo es una estimación de tope
/// por probe, no un costo real: el runner lo compara contra el tope de costo total de la suite
/// para decidir si puede invocar el provider.
/// </summary>
public sealed class Probe
{
    public ProbeId Id { get; }

    public ProbeKind Kind { get; }

    public string Prompt { get; }

    /// <summary>
    /// Respuesta esperada para la regla exacta de puntuación. Para Reading/Reasoning es el texto
    /// esperado (comparación exacta, case-insensitive, trimmeada). Para StructuredOutput es el
    /// objeto JSON esperado (comparación profunda y determinista).
    /// </summary>
    public string Expected { get; }

    /// <summary>Estimación de costo máximo en USD para este probe (tope, no costo real).</summary>
    public decimal MaxCostUsd { get; }

    public Probe(ProbeId id, ProbeKind kind, string prompt, string expected, decimal maxCostUsd)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }
        if (prompt is null || prompt.Length == 0)
        {
            throw new ArgumentException("Probe.Prompt es obligatorio", nameof(prompt));
        }
        if (expected is null || expected.Length == 0)
        {
            throw new ArgumentException("Probe.Expected es obligatorio", nameof(expected));
        }
        if (maxCostUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCostUsd), "Probe.MaxCostUsd no puede ser negativo");
        }
        Id = id;
        Kind = kind;
        Prompt = prompt;
        Expected = expected;
        MaxCostUsd = maxCostUsd;
    }
}

/// <summary>
/// Resultado de ejecutar un probe. Score está en 0..1 y es calculado por reglas exactas, no por
/// un LLM juez. Output es la respuesta cruda del modelo (o null si no hay respuesta). Error es
/// el texto del error tipado si el estado es Error.
/// </summary>
public sealed class ProbeResult
{
    public ProbeId Id { get; }

    public ProbeStatus Status { get; }

    public double Score { get; }

    public string? Output { get; }

    public string? Error { get; }

    public TimeSpan Duration { get; }

    /// <summary>Cost computed from reported usage and explicit prices; null when unavailable.
    /// This is not an account debit or provider billing statement.</summary>
    public decimal? CostUsd { get; }

    public TokenUsage? Usage { get; }

    public TokenUsageFields ReportedUsageFields { get; }

    public ProbeResult(ProbeId id, ProbeStatus status, double score, string? output,
        string? error, TimeSpan duration, decimal? costUsd,
        TokenUsage? usage = null, TokenUsageFields reportedUsageFields = TokenUsageFields.None)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }
        if (score < 0.0 || score > 1.0 || !double.IsFinite(score))
        {
            throw new ArgumentOutOfRangeException(nameof(score), "ProbeResult.Score debe estar en 0..1 y ser finito");
        }
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "ProbeResult.Duration no puede ser negativo");
        }
        if (costUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(costUsd), "ProbeResult.CostUsd no puede ser negativo");
        }
        Id = id;
        Status = status;
        Score = score;
        Output = output;
        Error = error;
        Duration = duration;
        CostUsd = costUsd;
        Usage = usage;
        ReportedUsageFields = usage is null ? TokenUsageFields.None : reportedUsageFields;
    }
}

/// <summary>
/// Consentimiento explícito y tope de costo para ejecutar una suite de cualificación. La suite
/// nunca corre automáticamente al descubrir un modelo: exige consentimiento explícito y un tope
/// de costo total antes de invocar un provider pago.
/// </summary>
public sealed class QualificationConsent
{
    public bool ExplicitlyGiven { get; }

    public decimal MaxTotalCostUsd { get; }

    public QualificationConsent(bool explicitlyGiven, decimal maxTotalCostUsd)
    {
        if (maxTotalCostUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalCostUsd),
                "QualificationConsent.MaxTotalCostUsd no puede ser negativo");
        }
        ExplicitlyGiven = explicitlyGiven;
        MaxTotalCostUsd = maxTotalCostUsd;
    }

    /// <summary>Consentimiento para un provider local sin costo (fake o modelo local).</summary>
    public static QualificationConsent Local() => new(true, 0m);
}
