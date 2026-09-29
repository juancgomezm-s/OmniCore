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

    /// <summary>
    /// Ruta física final con TODOS los enlaces resueltos (symlinks y junctions encadenados),
    /// o null si no puede resolverse (ciclo o profundidad excesiva). Las comprobaciones que
    /// dependen del destino real, como las rutas de secretos (ADR-0018 §3), se hacen sobre esta
    /// ruta y no sobre la que escribió el modelo.
    /// </summary>
    string? ResolvePhysical(string path);
}