namespace OmniCore.Qualification;

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
/// La suma declarada no es representable: no se puede autorizar gasto con ese estimate.
/// </summary>
public sealed class QualificationCostEstimateUnavailableException : Exception
{
    public QualificationCostEstimateUnavailableException()
        : base("la estimación de coste de la suite no es representable") { }
}

/// <summary>El probe superó su tope de tiempo antes de producir una respuesta completa.</summary>
public sealed class ProbeTimeoutException : Exception
{
    public ProbeTimeoutException(string probeId)
        : base($"probe {probeId} superó su tope de tiempo")
    {
    }
}
