namespace OmniCore.Domain;

/// <summary>
/// Error tipado de dominio cuando una autorización no es posible (spec §71, ADR-0037 §2).
/// Se lanza desde OmniCore.Security; Tools y Engine lo capturan sin depender de Security.
/// </summary>
public sealed class SecretValueTooShortException : ArgumentException
{
    public int MinimumLength { get; }

    public LocalizedText UserMessage => LocalizedText.Of("secrets.tooShort", "minimumLength", MinimumLength.ToString());

    public SecretValueTooShortException(int minimumLength)
        : base("Secret values must contain at least " + minimumLength + " characters.") =>
        MinimumLength = minimumLength;
}

/// <summary>Error tipado de dominio cuando una autorización no es posible (spec §71, ADR-0037 §2).</summary>
public sealed class PermissionDeniedException : InvalidOperationException
{
    public string ToolName { get; }

    public string Reason { get; }

    public PermissionDeniedException(string toolName, string reason)
    {
        ToolName = toolName;
        Reason = reason;
    }
}