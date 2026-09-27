namespace OmniCore.Abstractions;

/// <summary>
/// Valida que una ruta física quede dentro de una frontera (workspace). Interfaz en
/// Abstractions; la implementación vive en Sandbox/Execution (ADR-0008 §7, ADR-0009).
/// </summary>
public interface IPathBoundaryValidator
{
    /// <summary>Devuelve true si la ruta está dentro de la carpeta raíz.</summary>
    bool IsWithin(string physicalPath, string root);

    /// <summary>Devuelve true si la ruta candidata no escapa de la raíz (evita traversal).</summary>
    bool IsSafeRelative(string relativePath);
}