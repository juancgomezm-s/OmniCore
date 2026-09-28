namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>Decisión de la frontera de capacidad para un ToolIntent (ADR-0044 §5).</summary>
public sealed class ModelCapabilityDecision
{
    public bool Allowed { get; }

    /// <summary>Razón legible cuando rechaza; null cuando permite.</summary>
    public string? Reason { get; }

    /// <summary>Capacidad que la tool requiere; null si la tool no está clasificada.</summary>
    public ModelToolCapability? Capability { get; }

    public ModelCapabilityDecision(bool allowed, string? reason, ModelToolCapability? capability)
    {
        Allowed = allowed;
        Reason = reason;
        Capability = capability;
    }

    public static ModelCapabilityDecision Allow() => new(true, null, null);

    public static ModelCapabilityDecision Reject(string reason, ModelToolCapability? capability) =>
        new(false, reason, capability);
}

/// <summary>
/// Frontera de capacidad del modelo (ADR-0044 §5): valida cada ToolIntent contra la política
/// efectiva antes del Permission Engine y de nuevo antes de ejecutar. Una tool inventada,
/// oculta o una mutación fuera de categoría se rechaza aunque el modelo logre emitirla.
/// La frontera restringe; jamás autoriza: Security conserva la única autoridad (INV-018) y
/// una categoría nunca otorga permisos por sí sola (spec §20, ADR-0044 §10.4).
/// Vive junto a IPathBoundaryValidator en Abstractions (ADR-0009 §2.2) y es pura.
/// </summary>
public sealed class ModelCapabilityBoundary
{
    /// <summary>Clasificación de tools Core por capacidad (ADR-0044 §3). Versionable y
    /// reemplazable en composición; la frontera no la inventa por nombre de modelo.</summary>
    public static readonly IReadOnlyDictionary<string, ModelToolCapability> CoreTools =
        new Dictionary<string, ModelToolCapability>
        {
            { "filesystem.read", ModelToolCapability.WorkspaceRead },
            { "filesystem.list", ModelToolCapability.WorkspaceRead },
            { "filesystem.search", ModelToolCapability.Search },
            { "reference.resolve", ModelToolCapability.ReferenceResolve },
            { "plan.propose", ModelToolCapability.PlanProposal },
            { "filesystem.patch", ModelToolCapability.PatchExisting },
            { "filesystem.write", ModelToolCapability.ReplaceFile },
            { "filesystem.delete", ModelToolCapability.DeleteFile },
            { "filesystem.move", ModelToolCapability.MoveOrRename },
            { "process.exec", ModelToolCapability.GeneralProcess },
            { "shell.exec", ModelToolCapability.Shell },
            { "user.ask", ModelToolCapability.PlanProposal },
            // Tool de simulación de M1: escribe, se clasifica como reemplazo.
            { "fake.write", ModelToolCapability.ReplaceFile },
            { "fake.read", ModelToolCapability.WorkspaceRead },
            { "fake.test", ModelToolCapability.ValidationProcess },
        };

    private readonly IReadOnlyDictionary<string, ModelToolCapability> _toolCapabilities;

    private readonly EffectiveModelPolicy _policy;

    public ModelCapabilityBoundary(EffectiveModelPolicy policy)
        : this(policy, CoreTools)
    {
    }

    public ModelCapabilityBoundary(EffectiveModelPolicy policy,
        IReadOnlyDictionary<string, ModelToolCapability> toolCapabilities)
    {
        _policy = policy;
        _toolCapabilities = toolCapabilities;
    }

    /// <summary>
    /// Valida un ToolIntent contra la política efectiva. Reglas (ADR-0044 §5):
    /// 1. la tool debe estar clasificada y su capacidad dentro del techo;
    /// 2. cualquier claim de escritura exige un modo de mutación que la permita;
    /// 3. borrar/mover/renombrar con política Deny se corta aquí, antes del pipeline;
    /// 4. secretos: ninguna tool con claims de secretos pasa la frontera (ADR-0018);
    /// 5. red: exige la capacidad Network del techo;
    /// 6. ObserveOnly no permite ningún efecto de escritura aunque la tool esté clasificada.
    /// </summary>
    public ModelCapabilityDecision Evaluate(ToolIntent intent)
    {
        var toolName = intent.ToolId.ToString();

        // 1. Tool inventada u oculta: sin clasificación no hay autorización posible.
        if (!_toolCapabilities.TryGetValue(toolName, out var capability))
        {
            return ModelCapabilityDecision.Reject(
                "tool sin clasificar en la política de capacidad: " + toolName, null);
        }

        if (!_policy.ToolPolicy.Allows(capability))
        {
            return ModelCapabilityDecision.Reject(
                "capacidad " + capability + " fuera del techo de la categoría " + _policy.Category,
                capability);
        }

        // 4. Secretos: los secretos van por ISecretProvider (ADR-0018), nunca por tools del modelo.
        if (intent.Claims.Secrets.Count > 0)
        {
            return ModelCapabilityDecision.Reject("la política de modelos no permite claims de secretos",
                capability);
        }

        // 5. Red: capacidad explícita del techo.
        if (intent.Claims.Network.Count > 0 && !_policy.ToolPolicy.Allows(ModelToolCapability.Network))
        {
            return ModelCapabilityDecision.Reject("red fuera del techo de la categoría " + _policy.Category,
                ModelToolCapability.Network);
        }

        // 2/6. Mutación: cualquier claim de escritura exige mutación permitida por el techo.
        if (intent.Claims.Writes.Count > 0 && !MutationAllowed(capability))
        {
            return ModelCapabilityDecision.Reject(
                "mutación de archivos no permitida por la categoría " + _policy.Category
                + " (modo " + _policy.MutationPolicy.Mode + ")", capability);
        }

        // 3. Destructivas con Deny: se cortan antes del pipeline de permisos (ADR-0044 §4).
        if (capability is ModelToolCapability.DeleteFile or ModelToolCapability.MoveOrRename)
        {
            var rule = capability == ModelToolCapability.DeleteFile
                ? _policy.MutationPolicy.Delete
                : _policy.MutationPolicy.MoveOrRename;
            if (rule == DestructiveActionPolicy.Deny)
            {
                return ModelCapabilityDecision.Reject(
                    "acción destructiva " + capability + " en Deny por la categoría " + _policy.Category,
                    capability);
            }
        }

        return ModelCapabilityDecision.Allow();
    }

    /// <summary>Concasión del techo de mutación con la capacidad concreta de la tool.</summary>
    public bool MutationAllowed(ModelToolCapability capability)
    {
        return _policy.MutationPolicy.Mode switch
        {
            FileMutationMode.None => false,
            FileMutationMode.PatchExisting => capability == ModelToolCapability.PatchExisting,
            FileMutationMode.PatchAndCreate => capability is ModelToolCapability.PatchExisting
                or ModelToolCapability.CreateFile,
            FileMutationMode.Full => capability is ModelToolCapability.PatchExisting
                or ModelToolCapability.CreateFile
                or ModelToolCapability.ReplaceFile
                or ModelToolCapability.DeleteFile
                or ModelToolCapability.MoveOrRename,
            _ => false,
        };
    }
}
