namespace OmniCore.Qualification;

using OmniCore.Domain;

/// <summary>
/// Errores tipados de la suite de cualificación (spec §71). No se usa Exception genérica para
/// estos fallos de dominio.
/// </summary>

/// <summary>
/// Suite de cualificación pedida que no existe (solo `quick` está implementada en M5).
/// </summary>
public sealed class UnsupportedQualificationSuiteException : Exception
{
    public string Suite { get; }

    public UnsupportedQualificationSuiteException(string suite)
        : base("suite de cualificación no soportada: " + suite)
    {
        Suite = suite;
    }
}

/// <summary>
/// La suite no se completó: algún probe terminó en Error/Timeout/NotRun. No se persiste nada:
/// una cualificación parcial no existe (ADR-0007 §4).
/// </summary>
public sealed class QualificationSuiteFailedException : Exception
{
    public IReadOnlyList<string> Failures { get; }

    public QualificationSuiteFailedException(IReadOnlyList<string> failures)
        : base("la suite de cualificación no se completó: " + string.Join("; ", failures))
    {
        Failures = failures;
    }
}

/// <summary>
/// El runner no tiene consentimiento explícito para ejecutar la suite. La suite nunca corre
/// automáticamente al descubrir un modelo (ADR-0007 §6, M5).
/// </summary>
public sealed class QualificationConsentRequiredException : Exception
{
    public QualificationConsentRequiredException(string reason)
        : base(reason)
    {
    }
}

/// <summary>
/// El tope de costo total de la suite no permite ejecutar el conjunto de probes pedido.
/// </summary>
public sealed class QualificationCostCapExceededException : Exception
{
    public decimal MaxTotalCostUsd { get; }

    public decimal EstimatedCostUsd { get; }

    public QualificationCostCapExceededException(decimal maxTotalCostUsd, decimal estimatedCostUsd)
        : base($"tope de costo total de la suite excedido: máximo {maxTotalCostUsd} USD, estimado {estimatedCostUsd} USD")
    {
        MaxTotalCostUsd = maxTotalCostUsd;
        EstimatedCostUsd = estimatedCostUsd;
    }
}

/// <summary>
/// El probe superó su tope de tiempo (timeout) antes de producir una respuesta completa.
/// </summary>
public sealed class ProbeTimeoutException : Exception
{
    public ProbeTimeoutException(string probeId)
        : base($"probe {probeId} superó su tope de tiempo")
    {
    }
}

/// <summary>
/// Un probe fue cancelado por el llamador después de iniciar. Conserva la evidencia de uso
/// parcial para contabilizarla sin convertir la cancelación en éxito.
/// </summary>
public sealed class ProbeCancelledException : OperationCanceledException
{
    public ProbeResult Result { get; }

    public TokenUsage? ObservedUsage { get; }

    public ProbeCancelledException(ProbeResult result, TokenUsage? observedUsage,
        OperationCanceledException innerException, CancellationToken cancellationToken)
        : base("probe cancelado: " + result.Id, innerException, cancellationToken)
    {
        Result = result;
        ObservedUsage = observedUsage;
    }
}

/// <summary>
/// Suite cancelada por el llamador. Incluye resultados ya observados, el probe interrumpido y
/// los restantes como NotRun; nunca representa una cualificación persistible.
/// </summary>
public sealed class QualificationCancelledException : OperationCanceledException
{
    public IReadOnlyList<ProbeResult> Results { get; }

    public TokenUsage? ObservedUsage { get; }

    public decimal KnownCostUsd { get; }

    public bool HasUnknownCost { get; }

    public QualificationCancelledException(IReadOnlyList<ProbeResult> results,
        TokenUsage? observedUsage, CancellationToken cancellationToken,
        OperationCanceledException innerException)
        : base("suite de cualificación cancelada; no se guardó perfil", innerException, cancellationToken)
    {
        Results = results;
        ObservedUsage = observedUsage;
        KnownCostUsd = results.Where(result => result.Status != OmniCore.Domain.ProbeStatus.NotRun)
            .Sum(result => result.CostUsd ?? 0m);
        HasUnknownCost = results.Any(result => result.Status != OmniCore.Domain.ProbeStatus.NotRun
            && result.CostUsd is null);
    }
}

/// <summary>
/// La configuración es un provider pago remoto sin precios declarados. La suite falla en cerrado
/// ANTES de invocar al provider: no se puede hacer cumplir el tope de costo y el costo desconocido
/// jamás se reporta como gratis (M5, ADR-0007 §7).
/// </summary>
public sealed class QualificationUnknownPricingException : Exception
{
    public string ProviderId { get; }

    public QualificationUnknownPricingException(string providerId = "")
        : base("pricing desconocido para un provider pago remoto: no se puede estimar ni hacer cumplir el tope de costo"
            + (string.IsNullOrEmpty(providerId) ? "" : " (" + providerId + ")"))
    {
        ProviderId = providerId;
    }
}
