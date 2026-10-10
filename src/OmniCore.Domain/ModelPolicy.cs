namespace OmniCore.Domain;

using System.Security.Cryptography;
using System.Text;

/// <summary>Categoría de política operativa del usuario (ADR-0044 §3). Es un techo, no un grant.</summary>
public enum ModelPolicyCategory
{
    ObserveOnly,
    PatchOnly,
    ScopedCoder,
    FullAgent,
    Custom,
}

/// <summary>Capacidad de una tool que la política puede limitar (ADR-0044 §4).</summary>
public enum ModelToolCapability
{
    WorkspaceRead,
    Search,
    ReferenceResolve,
    PlanProposal,
    PatchExisting,
    CreateFile,
    ReplaceFile,
    DeleteFile,
    MoveOrRename,
    ValidationProcess,
    GeneralProcess,
    Shell,
    Network,
    /// <summary>Wait for one message from the execution's bound supervisor; no side effect.</summary>
    AgentMailboxWait,
}

/// <summary>Modo de mutación de archivos permitido por la política (ADR-0044 §4).</summary>
public enum FileMutationMode
{
    None,
    PatchExisting,
    PatchAndCreate,
    Full,
}

/// <summary>Acción destructiva: Deny corta antes del pipeline; Ask obliga a confirmar; Allow solo
/// deja pasar el intent al pipeline de permisos (ADR-0044 §4, INV-018).</summary>
public enum DestructiveActionPolicy
{
    Deny,
    Ask,
    Allow,
}

/// <summary>
/// Identidad exacta de una configuración de modelo (ADR-0044 §2). No es un nombre comercial:
/// un cambio de pesos, cuantización, adapters, backend relevante, chat template, adapter profile
/// o prompt profile produce una clave nueva y exige una decisión nueva. Excluye ToolMode y
/// ToolCallFormat para evitar el ciclo con la política (la ModelQualificationKey de ADR-0007 §5
/// sí los incluye). Igualdad por valor; los adapters se canonizan como conjunto ordenado.
/// </summary>
public sealed class ModelPolicyKey
{
    public const string DefaultAdapterProfile = "default";

    public const string DefaultPromptProfileVersion = "v1";

    public string ProviderId { get; }

    public string ModelId { get; }

    public string? ModelRevision { get; }

    public string? Quantization { get; }

    public IReadOnlyList<string> Adapters { get; }

    public string? Backend { get; }

    public string? BackendBuild { get; }

    public string? ChatTemplateHash { get; }

    public string AdapterProfile { get; }

    public string PromptProfileVersion { get; }

    public ModelPolicyKey(string providerId, string modelId, string? modelRevision, string? quantization,
        IReadOnlyList<string> adapters, string? backend, string? backendBuild, string? chatTemplateHash,
        string adapterProfile, string promptProfileVersion)
    {
        if (providerId is null || providerId!.Length == 0)
        {
            throw new ArgumentException("ProviderId es obligatorio", nameof(providerId));
        }

        if (modelId is null || modelId!.Length == 0)
        {
            throw new ArgumentException("ModelId es obligatorio", nameof(modelId));
        }

        ProviderId = providerId;
        ModelId = modelId;
        ModelRevision = modelRevision;
        Quantization = quantization;
        Adapters = CanonicalAdapters(adapters);
        Backend = backend;
        BackendBuild = backendBuild;
        ChatTemplateHash = chatTemplateHash;
        AdapterProfile = adapterIdValue(adapterProfile, DefaultAdapterProfile);
        PromptProfileVersion = adapterIdValue(promptProfileVersion, DefaultPromptProfileVersion);
    }

    /// <summary>Clave para una configuración sin metadatos finos (registro mínimo de M2).</summary>
    public static ModelPolicyKey For(string providerId, string modelId) =>
        new(providerId, modelId, null, null, Array.Empty<string>(), null, null, null,
            DefaultAdapterProfile, DefaultPromptProfileVersion);

    /// <summary>JSON canónico determinista (orden fijo de campos; null explícito).</summary>
    public string CanonicalJson()
    {
        var adapters = new StringBuilder();
        adapters.Append('[');
        for (var i = 0; i < Adapters.Count; i++)
        {
            if (i > 0)
            {
                adapters.Append(',');
            }

            adapters.Append('"').Append(Escape(Adapters[i])).Append('"');
        }

        adapters.Append(']');
        return "{\"providerId\":\"" + Escape(ProviderId) + "\""
            + ",\"modelId\":\"" + Escape(ModelId) + "\""
            + ",\"modelRevision\":" + Nullable(ModelRevision)
            + ",\"quantization\":" + Nullable(Quantization)
            + ",\"adapters\":" + adapters
            + ",\"backend\":" + Nullable(Backend)
            + ",\"backendBuild\":" + Nullable(BackendBuild)
            + ",\"chatTemplateHash\":" + Nullable(ChatTemplateHash)
            + ",\"adapterProfile\":\"" + Escape(AdapterProfile) + "\""
            + ",\"promptProfileVersion\":\"" + Escape(PromptProfileVersion) + "\"}";
    }

    /// <summary>Hash estable de la clave: PK del store relacional (ADR-0044 §8).</summary>
    public string PolicyKeyHash() => Hex(Sha256(Encoding.UTF8.GetBytes(CanonicalJson())));

    public override string ToString() => ProviderId + "/" + ModelId
        + (Quantization is null ? "" : ":" + Quantization)
        + (Adapters.Count == 0 ? "" : ":adapters=" + Adapters.Count)
        + (ChatTemplateHash is null ? "" : ":tpl=" + ChatTemplateHash)
        + ":" + AdapterProfile + ":" + PromptProfileVersion;

    public override bool Equals(object? other)
    {
        if (other is not ModelPolicyKey k)
        {
            return false;
        }

        return Ordinal(ProviderId, k.ProviderId) && Ordinal(ModelId, k.ModelId)
            && OrdinalNull(ModelRevision, k.ModelRevision) && OrdinalNull(Quantization, k.Quantization)
            && Adapters.SequenceEqual(k.Adapters) && OrdinalNull(Backend, k.Backend)
            && OrdinalNull(BackendBuild, k.BackendBuild) && OrdinalNull(ChatTemplateHash, k.ChatTemplateHash)
            && Ordinal(AdapterProfile, k.AdapterProfile) && Ordinal(PromptProfileVersion, k.PromptProfileVersion);
    }

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + ProviderId.GetHashCode(StringComparison.Ordinal);
        hash = hash * 31 + ModelId.GetHashCode(StringComparison.Ordinal);
        hash = hash * 31 + (ModelRevision?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + (Quantization?.GetHashCode(StringComparison.Ordinal) ?? 0);
        foreach (var a in Adapters)
        {
            hash = hash * 31 + a.GetHashCode(StringComparison.Ordinal);
        }

        hash = hash * 31 + (Backend?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + (BackendBuild?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + (ChatTemplateHash?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + AdapterProfile.GetHashCode(StringComparison.Ordinal);
        hash = hash * 31 + PromptProfileVersion.GetHashCode(StringComparison.Ordinal);
        return hash;
    }

    private static string adapterIdValue(string? value, string fallback) =>
        value is null || value!.Length == 0 ? fallback : value;

    private static IReadOnlyList<string> CanonicalAdapters(IReadOnlyList<string> adapters)
    {
        var copy = new List<string>();
        foreach (var a in adapters)
        {
            if (a is not null && a!.Length > 0)
            {
                copy.Add(a);
            }
        }

        copy.Sort(StringComparer.Ordinal);
        return copy;
    }

    private static string Nullable(string? value) =>
        value is null ? "null" : "\"" + Escape(value) + "\"";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private static bool Ordinal(string a, string b) => a.Equals(b, StringComparison.Ordinal);

    private static bool OrdinalNull(string? a, string? b) =>
        a is null ? b is null : b is not null && Ordinal(a!, b!);

    internal static byte[] Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes);
    }

    internal static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}

/// <summary>
/// Techo de superficie de tools para una configuración (ADR-0044 §4). El techo nunca es un
/// permiso: el Permission Engine sigue siendo la única autoridad (INV-002, INV-018).
/// </summary>
public sealed class ModelToolPolicy
{
    public ToolMode Mode { get; }

    public int MaxVisibleTools { get; }

    public bool AllowToolDiscovery { get; }

    public IReadOnlySet<ModelToolCapability> CapabilityCeiling { get; }

    public ModelToolPolicy(ToolMode mode, int maxVisibleTools, bool allowToolDiscovery,
        IReadOnlySet<ModelToolCapability> capabilityCeiling)
    {
        if (maxVisibleTools < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVisibleTools));
        }

        Mode = mode;
        MaxVisibleTools = maxVisibleTools;
        AllowToolDiscovery = allowToolDiscovery;
        CapabilityCeiling = new HashSet<ModelToolCapability>(capabilityCeiling);
    }

    public bool Allows(ModelToolCapability capability) => CapabilityCeiling.Contains(capability);

    public override bool Equals(object? other)
    {
        if (other is not ModelToolPolicy p)
        {
            return false;
        }

        return Mode == p.Mode && MaxVisibleTools == p.MaxVisibleTools
            && AllowToolDiscovery == p.AllowToolDiscovery && CapabilityCeiling.SetEquals(p.CapabilityCeiling);
    }

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + (int)Mode;
        hash = hash * 31 + MaxVisibleTools;
        hash = hash * 31 + (AllowToolDiscovery ? 1 : 0);
        foreach (var c in CapabilityCeiling)
        {
            hash = hash * 31 + (int)c;
        }

        return hash;
    }
}

/// <summary>Política tipada de mutación de archivos (ADR-0044 §4).</summary>
public sealed class FileMutationPolicy
{
    public FileMutationMode Mode { get; }

    public DestructiveActionPolicy Delete { get; }

    public DestructiveActionPolicy MoveOrRename { get; }

    public int MaxFilesPerTurn { get; }

    public int MaxChangedLinesPerTurn { get; }

    public double MaxRewriteRatio { get; }

    public bool RequirePriorRead { get; }

    public bool RequireExpectedVersionToken { get; }

    public bool RequirePostEditValidation { get; }

    public bool AllowParallelMutations { get; }

    public FileMutationPolicy(FileMutationMode mode, DestructiveActionPolicy delete,
        DestructiveActionPolicy moveOrRename, int maxFilesPerTurn, int maxChangedLinesPerTurn,
        double maxRewriteRatio, bool requirePriorRead, bool requireExpectedVersionToken,
        bool requirePostEditValidation, bool allowParallelMutations)
    {
        if (maxFilesPerTurn < 0 || maxChangedLinesPerTurn < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFilesPerTurn));
        }

        if (maxRewriteRatio < 0 || maxRewriteRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRewriteRatio), "ratio en [0,1]");
        }

        if (mode == FileMutationMode.None && (maxFilesPerTurn != 0 || maxChangedLinesPerTurn != 0))
        {
            throw new ArgumentException("FileMutationMode.None no admite presupuesto de mutación");
        }

        Mode = mode;
        Delete = delete;
        MoveOrRename = moveOrRename;
        MaxFilesPerTurn = maxFilesPerTurn;
        MaxChangedLinesPerTurn = maxChangedLinesPerTurn;
        MaxRewriteRatio = maxRewriteRatio;
        RequirePriorRead = requirePriorRead;
        RequireExpectedVersionToken = requireExpectedVersionToken;
        RequirePostEditValidation = requirePostEditValidation;
        AllowParallelMutations = allowParallelMutations;
    }

    public bool CanMutateFiles => Mode != FileMutationMode.None && MaxFilesPerTurn > 0;

    public override bool Equals(object? other)
    {
        if (other is not FileMutationPolicy p)
        {
            return false;
        }

        return Mode == p.Mode && Delete == p.Delete && MoveOrRename == p.MoveOrRename
            && MaxFilesPerTurn == p.MaxFilesPerTurn && MaxChangedLinesPerTurn == p.MaxChangedLinesPerTurn
            && MaxRewriteRatio.Equals(p.MaxRewriteRatio) && RequirePriorRead == p.RequirePriorRead
            && RequireExpectedVersionToken == p.RequireExpectedVersionToken
            && RequirePostEditValidation == p.RequirePostEditValidation
            && AllowParallelMutations == p.AllowParallelMutations;
    }

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + (int)Mode;
        hash = hash * 31 + (int)Delete;
        hash = hash * 31 + (int)MoveOrRename;
        hash = hash * 31 + MaxFilesPerTurn;
        hash = hash * 31 + MaxChangedLinesPerTurn;
        hash = hash * 31 + MaxRewriteRatio.GetHashCode();
        hash = hash * 31 + (RequirePriorRead ? 1 : 0);
        hash = hash * 31 + (RequireExpectedVersionToken ? 1 : 0);
        hash = hash * 31 + (RequirePostEditValidation ? 1 : 0);
        hash = hash * 31 + (AllowParallelMutations ? 1 : 0);
        return hash;
    }
}

/// <summary>
/// Preferencia operativa del usuario para una ModelPolicyKey exacta (ADR-0044 §1–§2). Es un
/// techo de autonomía, nunca un permiso. Solo la guarda una acción explícita del usuario.
/// </summary>
public sealed class UserModelPolicy
{
    public ModelPolicyCategory Category { get; }

    public ModelToolPolicy ToolPolicy { get; }

    public FileMutationPolicy MutationPolicy { get; }

    public string Source { get; }

    public string? Note { get; }

    public UserModelPolicy(ModelPolicyCategory category, ModelToolPolicy toolPolicy,
        FileMutationPolicy mutationPolicy, string source, string? note)
    {
        if (category == ModelPolicyCategory.ObserveOnly && mutationPolicy.Mode != FileMutationMode.None)
        {
            throw new ArgumentException("ObserveOnly no admite mutación de archivos");
        }

        Category = category;
        ToolPolicy = toolPolicy;
        MutationPolicy = mutationPolicy;
        Source = source;
        Note = note;
    }

    public override bool Equals(object? other)
    {
        if (other is not UserModelPolicy p)
        {
            return false;
        }

        return Category == p.Category && ToolPolicy.Equals(p.ToolPolicy)
            && MutationPolicy.Equals(p.MutationPolicy) && Source.Equals(p.Source, StringComparison.Ordinal)
            && (Note ?? "") == (p.Note ?? "");
    }

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + (int)Category;
        hash = hash * 31 + ToolPolicy.GetHashCode();
        hash = hash * 31 + MutationPolicy.GetHashCode();
        hash = hash * 31 + Source.GetHashCode(StringComparison.Ordinal);
        return hash;
    }
}

/// <summary>Política persistida con su revisión de concurrencia (ADR-0044 §8–§9).</summary>
public sealed class StoredModelPolicy
{
    public ModelPolicyKey Key { get; }

    public long Revision { get; }

    public UserModelPolicy Policy { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public StoredModelPolicy(ModelPolicyKey key, long revision, UserModelPolicy policy,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Key = key;
        Revision = revision;
        Policy = policy;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }
}

/// <summary>Cambio registrado en el historial de políticas (ADR-0044 §8).</summary>
public sealed class ModelPolicyChange
{
    public string PolicyKeyHash { get; }

    public long Revision { get; }

    public string ChangeKind { get; }

    public string? OldValueJson { get; }

    public string? NewValueJson { get; }

    public DateTimeOffset ChangedAt { get; }

    public ModelPolicyChange(string policyKeyHash, long revision, string changeKind, string? oldValueJson,
        string? newValueJson, DateTimeOffset changedAt)
    {
        PolicyKeyHash = policyKeyHash;
        Revision = revision;
        ChangeKind = changeKind;
        OldValueJson = oldValueJson;
        NewValueJson = newValueJson;
        ChangedAt = changedAt;
    }
}

/// <summary>Selección de modelo vigente para un workspace, con su modo efímero seguro si aplica.</summary>
public sealed class ModelSelectionState
{
    public string WorkspaceId { get; }

    public ModelPolicyKey Key { get; }

    public string ModelId { get; }

    /// <summary>True si la selección usa ObserveOnly solo para esta selección (ADR-0044 §6).</summary>
    public bool EphemeralObserveOnly { get; }

    public DateTimeOffset SelectedAt { get; }

    public ModelSelectionState(string workspaceId, ModelPolicyKey key, string modelId,
        bool ephemeralObserveOnly, DateTimeOffset selectedAt)
    {
        WorkspaceId = workspaceId;
        Key = key;
        ModelId = modelId;
        EphemeralObserveOnly = ephemeralObserveOnly;
        SelectedAt = selectedAt;
    }
}

/// <summary>
/// Presets visibles de categoría (ADR-0044 §3–§4). Defaults iniciales: umbrales versionados y
/// calibrables; no se resuelven por tamaño ni nombre de modelo.
/// </summary>
public static class ModelPolicyPresets
{
    private static readonly IReadOnlySet<ModelToolCapability> ObserveCaps = new HashSet<ModelToolCapability>
    {
        ModelToolCapability.WorkspaceRead,
        ModelToolCapability.Search,
        ModelToolCapability.ReferenceResolve,
        ModelToolCapability.PlanProposal,
        ModelToolCapability.AgentMailboxWait,
    };

    private static readonly IReadOnlySet<ModelToolCapability> PatchCaps = new HashSet<ModelToolCapability>(ObserveCaps)
    {
        ModelToolCapability.PatchExisting,
    };

    private static readonly IReadOnlySet<ModelToolCapability> ScopedCaps = new HashSet<ModelToolCapability>(PatchCaps)
    {
        ModelToolCapability.CreateFile,
        ModelToolCapability.ValidationProcess,
        ModelToolCapability.GeneralProcess,
    };

    private static readonly IReadOnlySet<ModelToolCapability> FullCaps = new HashSet<ModelToolCapability>(ScopedCaps)
    {
        ModelToolCapability.ReplaceFile,
        ModelToolCapability.DeleteFile,
        ModelToolCapability.MoveOrRename,
        ModelToolCapability.Shell,
        ModelToolCapability.Network,
    };

    /// <summary>Leer, listar, buscar, resolver referencias y proponer plan. Sin mutación.</summary>
    public static UserModelPolicy ObserveOnly() => new(
        ModelPolicyCategory.ObserveOnly,
        new ModelToolPolicy(ToolMode.Direct, 6, false, ObserveCaps),
        new FileMutationPolicy(FileMutationMode.None, DestructiveActionPolicy.Deny,
            DestructiveActionPolicy.Deny, 0, 0, 0, requirePriorRead: true, requireExpectedVersionToken: true,
            requirePostEditValidation: false, allowParallelMutations: false),
        "preset", null);

    /// <summary>Lo anterior + parche estructurado sobre archivos ya leídos. Sin crear/borrar/mover.</summary>
    public static UserModelPolicy PatchOnly() => new(
        ModelPolicyCategory.PatchOnly,
        new ModelToolPolicy(ToolMode.Direct, 7, false, PatchCaps),
        new FileMutationPolicy(FileMutationMode.PatchExisting, DestructiveActionPolicy.Deny,
            DestructiveActionPolicy.Deny, 2, 200, 0.25, requirePriorRead: true, requireExpectedVersionToken: true,
            requirePostEditValidation: true, allowParallelMutations: false),
        "preset", null);

    /// <summary>Patch + crear + build/test acotado al scope de la Task.</summary>
    public static UserModelPolicy ScopedCoder() => new(
        ModelPolicyCategory.ScopedCoder,
        new ModelToolPolicy(ToolMode.Discovered, 10, true, ScopedCaps),
        new FileMutationPolicy(FileMutationMode.PatchAndCreate, DestructiveActionPolicy.Ask,
            DestructiveActionPolicy.Ask, 5, 600, 0.50, requirePriorRead: true, requireExpectedVersionToken: true,
            requirePostEditValidation: true, allowParallelMutations: false),
        "preset", null);

    /// <summary>Techo explícito del usuario; intersecta con Task, AgentProfile y permisos (ADR-0044 §3).</summary>
    public static UserModelPolicy FullAgent() => new(
        ModelPolicyCategory.FullAgent,
        new ModelToolPolicy(ToolMode.Discovered, 16, true, FullCaps),
        new FileMutationPolicy(FileMutationMode.Full, DestructiveActionPolicy.Ask,
            DestructiveActionPolicy.Ask, 8, 2000, 1.0, requirePriorRead: true, requireExpectedVersionToken: true,
            requirePostEditValidation: true, allowParallelMutations: false),
        "preset", null);

    public static UserModelPolicy For(ModelPolicyCategory category)
    {
        return category switch
        {
            ModelPolicyCategory.ObserveOnly => ObserveOnly(),
            ModelPolicyCategory.PatchOnly => PatchOnly(),
            ModelPolicyCategory.ScopedCoder => ScopedCoder(),
            ModelPolicyCategory.FullAgent => FullAgent(),
            _ => throw new ArgumentException("Custom no tiene preset; se configuran campos explícitos",
                nameof(category)),
        };
    }
}

/// <summary>
/// Política efectiva que el runtime aplica en un Turn (ADR-0044 §1): el techo del usuario
/// intersectado con el HarnessPolicy del perfil (ADR-0007 §3). Sin política guardada cae en
/// el fallback ObserveOnly: una configuración desconocida nunca escribe (ADR-0044 §10.1).
/// </summary>
public sealed class EffectiveModelPolicy
{
    public ModelPolicyKey Key { get; }

    /// <summary>Revisión de la preferencia aplicada; null cuando es fallback ObserveOnly.</summary>
    public long? Revision { get; }

    public ModelPolicyCategory Category { get; }

    public ModelToolPolicy ToolPolicy { get; }

    public FileMutationPolicy MutationPolicy { get; }

    /// <summary>True si no hay UserModelPolicy guardada y se aplicó el fallback ObserveOnly.</summary>
    public bool IsFallback { get; }

    public EffectiveModelPolicy(ModelPolicyKey key, long? revision, ModelPolicyCategory category,
        ModelToolPolicy toolPolicy, FileMutationPolicy mutationPolicy, bool isFallback)
    {
        Key = key;
        Revision = revision;
        Category = category;
        ToolPolicy = toolPolicy;
        MutationPolicy = mutationPolicy;
        IsFallback = isFallback;
    }

    /// <summary>
    /// Resuelve la política efectiva: techo del usuario ∩ HarnessPolicy. La intersección es por
    /// mínimo: ToolMode por el más restrictivo (Direct, luego Discovered, luego Code), tools
    /// visibles por el menor, discovery solo si ambas capas lo admiten. La mutación es siempre
    /// la del techo.
    /// </summary>
    public static EffectiveModelPolicy Resolve(ModelPolicyKey key, StoredModelPolicy? stored,
        HarnessPolicy harness)
    {
        if (stored is null)
        {
            var fallback = ModelPolicyPresets.ObserveOnly();
            return new EffectiveModelPolicy(key, null, fallback.Category, fallback.ToolPolicy,
                fallback.MutationPolicy, isFallback: true);
        }

        var user = stored!.Policy;
        var mode = MinMode(user.ToolPolicy.Mode, harness.ToolMode);
        var visible = Math.Min(user.ToolPolicy.MaxVisibleTools, harness.MaxVisibleTools);
        // Direct = pocas tools explícitas y sin tool.search (ADR-0044 §4): el discovery solo
        // sobrevive si el harness confía en el modelo y el techo lo permite.
        var discovery = user.ToolPolicy.AllowToolDiscovery && harness.ToolMode != ToolMode.Direct
            && mode != ToolMode.Direct;
        var toolPolicy = new ModelToolPolicy(mode, visible, discovery, user.ToolPolicy.CapabilityCeiling);
        return new EffectiveModelPolicy(key, stored!.Revision, user.Category, toolPolicy,
            user.MutationPolicy, isFallback: false);
    }

    /// <summary>Fingerprint de ejecución (ADR-0044 §8): hash de clave, revisión, categoría y mutación.</summary>
    public string Fingerprint()
    {
        var payload = Key.CanonicalJson() + "|rev=" + (Revision?.ToString() ?? "fallback")
            + "|cat=" + Category + "|mutation=" + MutationPolicy.Mode;
        return ModelPolicyKey.Hex(ModelPolicyKey.Sha256(Encoding.UTF8.GetBytes(payload)));
    }

    public override string ToString() => (IsFallback ? "fallback:" : "") + Category
        + " mode=" + ToolPolicy.Mode + " visible=" + ToolPolicy.MaxVisibleTools
        + " mutation=" + MutationPolicy.Mode;

    private static ToolMode MinMode(ToolMode user, ToolMode harness)
    {
        var u = ModeOrder(user);
        var h = ModeOrder(harness);
        return u <= h ? user : harness;
    }

    private static int ModeOrder(ToolMode mode) => mode switch
    {
        ToolMode.Direct => 0,
        ToolMode.Discovered => 1,
        _ => 2,
    };
}

/// <summary>Draft que el Host devuelve al seleccionar una configuración sin política (ADR-0044 §6).</summary>
public sealed class ModelPolicyDraft
{
    public ModelPolicyKey Key { get; }

    /// <summary>Categoría recomendada; en M3 siempre conservadora: sin evidencia empírica (M5)
    /// la recomendación no puede ampliar autonomía (ADR-0044 §2).</summary>
    public ModelPolicyCategory RecommendedCategory { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>True cuando existe evidencia de cualificación (suite quick, M5); false en M3.</summary>
    public bool HasQualificationEvidence { get; }

    public ModelPolicyDraft(ModelPolicyKey key, ModelPolicyCategory recommendedCategory,
        IReadOnlyList<string> warnings, bool hasQualificationEvidence)
    {
        Key = key;
        RecommendedCategory = recommendedCategory;
        Warnings = warnings;
        HasQualificationEvidence = hasQualificationEvidence;
    }
}

/// <summary>Conflicto de revisión: dos clientes no pueden pisarse (ADR-0044 §9). Sin sobrescritura silenciosa.</summary>
public sealed class ModelPolicyRevisionConflictException : Exception
{
    public long ExpectedRevision { get; }

    public long ActualRevision { get; }

    public ModelPolicyRevisionConflictException(long expectedRevision, long actualRevision)
        : base("revisión de política obsoleta: esperaba " + expectedRevision + ", actual " + actualRevision)
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }
}

/// <summary>La política consultada no existe (p. ej. eliminar dos veces).</summary>
public sealed class ModelPolicyNotFoundException : Exception
{
    public string PolicyKeyHash { get; }

    public ModelPolicyNotFoundException(ModelPolicyKey key)
        : base("no hay política guardada para " + key)
    {
        PolicyKeyHash = key.PolicyKeyHash();
    }
}

/// <summary>
/// Una Task requiere una capacidad que la política vigente no concede: el runtime termina con
/// este error tipado en lugar de ampliar capacidad en silencio (ADR-0044 §6).
/// </summary>
public sealed class ModelPolicyRequiredException : Exception
{
    public ModelToolCapability RequiredCapability { get; }

    public string PolicyKeyHash { get; }

    /// <summary>Mensaje para el usuario (ADR-0040): qué capacidad falta, para qué modelo y cómo resolverlo.</summary>
    public LocalizedText UserMessage { get; }

    public ModelPolicyRequiredException(ModelPolicyKey key, ModelToolCapability requiredCapability)
        : base("la Task requiere " + requiredCapability + " y la política de " + key + " no la concede; "
            + "clasifica el modelo para continuar")
    {
        RequiredCapability = requiredCapability;
        PolicyKeyHash = key.PolicyKeyHash();
        UserMessage = LocalizedText.Of("modelPolicy.required", ("capability", requiredCapability.ToString()),
            ("model", key.ModelId));
    }
}
