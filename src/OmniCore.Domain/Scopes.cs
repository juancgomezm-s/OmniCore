namespace OmniCore.Domain;

/// <summary>
/// Alcance jerárquico de la configuración y los datos (ADR-0022, arquitectura §25).
/// </summary>
public enum ScopeLevel
{
    /// <summary>Núcleo del runtime; nunca se configura desde fuera.</summary>
    BuiltIn,

    /// <summary>Usuario global (máquinas, memoria global, perfiles de modelo).</summary>
    User,

    /// <summary>Identidad del repositorio; compartida por clones y worktrees.</summary>
    Project,

    /// <summary>Carpeta raíz local abierta; un workspace contiene 1..N proyectos.</summary>
    Workspace,

    /// <summary>Una sesión durable.</summary>
    Session,

    /// <summary>Un Run.</summary>
    Run,

    /// <summary>Una Task.</summary>
    Task,

    /// <summary>Una Lane.</summary>
    Lane,
}

/// <summary>Nivel de confianza de una extensión o skill (ADR-0023).</summary>
public enum TrustLevel
{
    Core,
    Trusted,
    Project,
    ThirdParty,
    Untrusted,
}

/// <summary>Origen de un componente de software (tools, skills, comandos; ADR-0023, ADR-0027).</summary>
public enum SourceKind
{
    BuiltIn,
    Project,
    Skill,
    Extension,
    Mcp,
    Dynamic,
}