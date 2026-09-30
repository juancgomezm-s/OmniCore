namespace OmniCore.Qualification;

/// <summary>
/// Errores tipados de la suite de cualificación (spec §71). No se usa Exception genérica para
/// estos fallos de dominio.
/// </summary>

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
