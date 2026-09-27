namespace OmniCore.Domain;

/// <summary>
/// Error tipado de dominio cuando una autorización no es posible (spec §71, ADR-0037 §2).
/// Se lanza desde OmniCore.Security; Tools y Engine lo capturan sin depender de Security.
/// </summary>
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