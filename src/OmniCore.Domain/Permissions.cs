namespace OmniCore.Domain;

/// <summary>
/// Techo de una capa de permisos: Task, Lane, AgentProfile o UserPolicy (ADR-0037 §1).
/// </summary>
public sealed class PermissionScope
{
    /// <summary>Globs de lectura relativos al workspace + rutas absolutas explícitas.</summary>
    public IReadOnlyList<string> Reads { get; }

    /// <summary>Globs de escritura dentro del workspace + rutas absolutas.</summary>
    public IReadOnlyList<string> Writes { get; }

    /// <summary>Patrones de proceso (ejecutable resuelto + argv) → decisión.</summary>
    public IReadOnlyList<ProcessRule> Process { get; }

    /// <summary>Reglas de red host[:puerto] → decisión, con la categoría especial "build".</summary>
    public IReadOnlyList<NetworkRule> Network { get; }

    /// <summary>SecretRef permitidos (ADR-0018).</summary>
    public IReadOnlyList<string> Secrets { get; }

    /// <summary>Superficie shell (ADR-0015 §2): alto riesgo.</summary>
    public bool AllowShell { get; }

    private PermissionScope(
        IReadOnlyList<string> reads,
        IReadOnlyList<string> writes,
        IReadOnlyList<ProcessRule> process,
        IReadOnlyList<NetworkRule> network,
        IReadOnlyList<string> secrets,
        bool allowShell)
    {
        Reads = reads;
        Writes = writes;
        Process = process;
        Network = network;
        Secrets = secrets;
        AllowShell = allowShell;
    }

    public static PermissionScope Autonomous() =>
        new(new string[0], new string[0], new ProcessRule[0], new NetworkRule[0], new string[0], false);

    public static PermissionScope With(IReadOnlyList<string> reads, IReadOnlyList<string> writes,
        IReadOnlyList<ProcessRule> process, IReadOnlyList<NetworkRule> network, IReadOnlyList<string> secrets,
        bool allowShell) => new(reads, writes, process, network, secrets, allowShell);
}

/// <summary>Regla de proceso: patrón sobre ejecutable resuelto + argv → decisión.</summary>
public record ProcessRule(string ExecutablePattern, IReadOnlyList<string> ArgvPatterns, PermissionDecision Decision) { }

/// <summary>Regla de red: host[:puerto] o la categoría "build" → decisión.</summary>
public record NetworkRule(string HostPattern, PermissionDecision Decision) { }

/// <summary>Decisión de una capa con la regla que aplicó (para la traza).</summary>
public record LayerDecision(string Layer, PermissionDecision Decision, string Rule) { }

/// <summary>
/// Traza completa de una evaluación de permisos (ADR-0037 §2). Se persiste en
/// <c>PermissionEvaluated</c>.
/// </summary>
public sealed class PermissionDecisionRecord
{
    public PermissionDecision Final { get; }

    public IReadOnlyList<LayerDecision> Layers { get; }

    public GrantId? AppliedGrant { get; }

    public PermissionDecisionRecord(PermissionDecision final, IReadOnlyList<LayerDecision> layers,
        GrantId? appliedGrant)
    {
        Final = final;
        Layers = layers;
        AppliedGrant = appliedGrant;
    }
}

/// <summary>Grant persistido (ADR-0037 §1, §5). Un grant solo levanta un Ask de UserPolicy o del modo.</summary>
public sealed class Grant
{
    public GrantId Id { get; }

    public ResourceClaims Claims { get; }

    public GrantLifetime Lifetime { get; }

    public WorkspaceId? WorkspaceKey { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public Grant(GrantId id, ResourceClaims claims, GrantLifetime lifetime, WorkspaceId? workspaceKey,
        DateTimeOffset? expiresAt)
    {
        Id = id;
        Claims = claims;
        Lifetime = lifetime;
        WorkspaceKey = workspaceKey;
        ExpiresAt = expiresAt;
    }
}

/// <summary>Claims de recursos que un intent va a tocar (ADR-0014 §2).</summary>
public sealed class ResourceClaims
{
    public IReadOnlyList<string> Reads { get; }

    public IReadOnlyList<string> Writes { get; }

    public IReadOnlyList<NetworkGrant> Network { get; }

    public ProcessClaim? Process { get; }

    public IReadOnlyList<string> Secrets { get; }

    public ResourceClaims(IReadOnlyList<string> reads, IReadOnlyList<string> writes,
        IReadOnlyList<NetworkGrant> network, ProcessClaim? process, IReadOnlyList<string> secrets)
    {
        Reads = reads;
        Writes = writes;
        Network = network;
        Process = process;
        Secrets = secrets;
    }

    public static ResourceClaims Empty() =>
        new(new string[0], new string[0], new NetworkGrant[0], null, new string[0]);
}

/// <summary>Destino de red reclamado.</summary>
public record NetworkGrant(string Host, int? Port) { }

/// <summary>Reclamación de proceso con su clase de efecto (ADR-0015 §1).</summary>
public record ProcessClaim(string Executable, IReadOnlyList<string> Args, string EffectClass) { }

/// <summary>Reclamación de un recurso de red.</summary>
public record NetworkClaim(string Host, int? Port) { }

/// <summary>Referencia a un secreto sin su valor (ADR-0018). Nunca serializable.</summary>
public sealed class SecretRef
{
    public string Id { get; }

    private SecretRef(string id) => Id = id;

    public static SecretRef Of(string id) => new(id);

    /// <inheritdoc />
    public override string ToString() => "***";
}