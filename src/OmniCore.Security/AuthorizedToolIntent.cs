namespace OmniCore.Security;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Intent autorizado concreto. Implementa la interfaz mínima de Abstractions. El constructor
/// es internal: SOLO este assembly (OmniCore.Security) puede construir intents autorizados
/// (INV-018, ADR-0009 §2.1 — un test de arquitectura verifica por IL que ninguna otra clase
/// referencia este constructor).
/// </summary>
public sealed class AuthorizedToolIntent : IAuthorizedToolIntent
{
    public ToolIntent Intent { get; }

    public PermissionDecisionRecord Decision { get; }

    public string ConstructedBy { get; }

    internal AuthorizedToolIntent(ToolIntent intent, PermissionDecisionRecord decision, string constructedBy)
    {
        Intent = intent;
        Decision = decision;
        ConstructedBy = constructedBy;
    }
}