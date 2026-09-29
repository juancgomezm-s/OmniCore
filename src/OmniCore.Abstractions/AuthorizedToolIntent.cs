namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Intent ya autorizado por el Permission Engine (ADR-0014 §3, INV-018). Es una clase sellada
/// con constructor <c>internal</c>: el único assembly con acceso a los internos de Abstractions es
/// OmniCore.Security (InternalsVisibleTo), así que ningún otro código puede fabricar una
/// autorización. <see cref="ITool.ExecuteAsync"/> solo acepta este tipo, de modo que ejecutar
/// una tool sin pasar por el Permission Engine no compila.
/// </summary>
public sealed class AuthorizedToolIntent
{
    public ToolIntent Intent { get; }

    public PermissionDecisionRecord Decision { get; }

    public string ConstructedBy { get; }

    internal AuthorizedToolIntent(ToolIntent intent, PermissionDecisionRecord decision, string constructedBy)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Final != PermissionDecision.Allow)
        {
            throw new ArgumentException("solo una decisión Allow produce un intent autorizado", nameof(decision));
        }

        Intent = intent;
        Decision = decision;
        ConstructedBy = constructedBy;
    }
}
