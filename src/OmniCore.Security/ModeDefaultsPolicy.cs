namespace OmniCore.Security;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Capa de defaults por modo (ADR-0037 §4, M2): el perfil por defecto es "autónomo".
/// Produce una decisión por recurso según el RunMode. Las decisiones por capa se combinan
/// con el mínimo (Deny &lt; Ask &lt; Allow; ADR-0037 §2).
/// </summary>
public sealed class ModeDefaultsPolicy
{
    /// <summary>Decisión por recurso según el modo (tabla ADR-0037 §4).</summary>
    public PermissionDecision ForRunMode(RunMode mode, PermissionResource resource)
    {
        if (resource == PermissionResource.ReadInsideWorkspace) return PermissionDecision.Allow;
        if (resource == PermissionResource.ReadOutsideWorkspace) return PermissionDecision.Ask;
        if (resource == PermissionResource.WriteInsideWorkspace)
        {
            return mode == RunMode.Plan ? PermissionDecision.Deny : PermissionDecision.Allow;
        }

        if (resource == PermissionResource.WriteOutsideWorkspace)
        {
            return mode == RunMode.Plan ? PermissionDecision.Deny : PermissionDecision.Ask;
        }

        if (resource == PermissionResource.ObservationalProcess) return PermissionDecision.Allow;
        if (resource == PermissionResource.BuildTestProcess)
        {
            return mode == RunMode.Plan ? PermissionDecision.Deny : PermissionDecision.Allow;
        }

        if (resource == PermissionResource.ExternalOrUnknownProcess)
        {
            return mode == RunMode.Plan ? PermissionDecision.Deny : PermissionDecision.Ask;
        }

        if (resource == PermissionResource.BuildTestNetwork)
        {
            return mode == RunMode.Plan ? PermissionDecision.Deny : PermissionDecision.Allow;
        }

        if (resource == PermissionResource.OtherNetwork)
        {
            return mode == RunMode.Plan ? PermissionDecision.Deny : PermissionDecision.Ask;
        }

        if (resource == PermissionResource.Shell)
        {
            return mode == RunMode.Plan ? PermissionDecision.Deny : PermissionDecision.Ask;
        }

        if (resource == PermissionResource.SecretPaths) return PermissionDecision.Deny;
        if (resource == PermissionResource.PlanAndReferenceTools) return PermissionDecision.Allow;
        return PermissionDecision.Ask;
    }

    public static ModeDefaultsPolicy Instance() => new();
}

/// <summary>Categorías de recursos para la política por modo (ADR-0037 §4).</summary>
public enum PermissionResource
{
    ReadInsideWorkspace,
    ReadOutsideWorkspace,
    WriteInsideWorkspace,
    WriteOutsideWorkspace,
    ObservationalProcess,
    BuildTestProcess,
    ExternalOrUnknownProcess,
    BuildTestNetwork,
    OtherNetwork,
    Shell,
    SecretPaths,
    PlanAndReferenceTools,
}