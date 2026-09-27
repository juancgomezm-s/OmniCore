namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Vista mínima de un intent ya autorizado (ADR-0014). Tools y Engine dependen solo de esta
/// interfaz: fabricar una instancia concreta es imposible fuera de OmniCore.Security, y un
/// test de arquitectura verifica por IL que solo ese assembly referencia la clase concreta.
/// </summary>
public interface IAuthorizedToolIntent
{
    ToolIntent Intent { get; }

    PermissionDecisionRecord Decision { get; }

    string ConstructedBy { get; }
}