namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Contrato del Permission Engine (ADR-0037 §2, INV-002, INV-003). El Engine es el único
/// llamador para intents de tools; Security lo implementa y es el único que materializa
/// <c>IAuthorizedToolIntent</c> (INV-018, ADR-0009 §2.1).
/// </summary>
public interface IPermissionPolicy
{
    /// <summary>Evalúa un intent contra todas las capas y devuelve la decisión con traza.</summary>
    PermissionDecisionRecord Evaluate(ToolIntent intent);

    /// <summary>
    /// Autoriza un intent si la decisión final es Allow; lanza una excepción tipada si no.
    /// Solo esta capa puede construir el <c>IAuthorizedToolIntent</c> concreto.
    /// </summary>
    IAuthorizedToolIntent Authorize(ToolIntent intent);

    /// <summary>
    /// Materializa el intent autorizado para un Ask ya aprobado por interacción (ADR-0034:
    /// el cliente devolvió allow). El runtime NUNCA re-evalúa la política aquí (INV-002);
    /// la decisión permitida ya fue aprobada por el humano. Consume un grant de lifetime
    /// Once si existe. Solo esta capa construye intents autorizados (INV-018).
    /// </summary>
    IAuthorizedToolIntent AuthorizeApproved(ToolIntent intent, GrantId? approvedGrant);
}