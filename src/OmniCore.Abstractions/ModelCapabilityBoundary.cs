namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>Decisión de la frontera de capacidad para un ToolIntent (ADR-0044 §5).</summary>
public sealed class ModelCapabilityDecision
{
    /// <summary>True si la frontera no rechaza (Allow o Ask). Un Ask no autoriza: solo baja un Allow.</summary>
    public bool Allowed { get; }

    /// <summary>True cuando la política destructiva es Ask: el pipeline debe pedir confirmación
    /// (mínimo Deny &lt; Ask &lt; Allow, INV-028); jamás sube un Deny.</summary>
    public bool RequiresAsk { get; }

    /// <summary>Razón legible cuando rechaza; null cuando permite.</summary>
    public string? Reason { get; }

    /// <summary>Capacidad que la tool requiere; null si la tool no está clasificada.</summary>
    public ModelToolCapability? Capability { get; }

    public ModelCapabilityDecision(bool allowed, string? reason, ModelToolCapability? capability,
        bool requiresAsk = false)
    {
        Allowed = allowed;
        RequiresAsk = requiresAsk;
        Reason = reason;
        Capability = capability;
    }

    public static ModelCapabilityDecision Allow() => new(true, null, null);

    public static ModelCapabilityDecision Ask(string reason, ModelToolCapability? capability) =>
        new(true, reason, capability, true);

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
public sealed class ModelCapabilityBoundary : IToolIntentBoundary
{
    /// <summary>Clasificación de tools Core por capacidad (ADR-0044 §3). Versionable y
    /// reemplazable en composición; la frontera no la inventa por nombre de modelo.</summary>
    public static readonly IReadOnlyDictionary<string, ModelToolCapability> CoreTools =
        new Dictionary<string, ModelToolCapability>
        {
            { "filesystem.read", ModelToolCapability.WorkspaceRead },
            { "media.read", ModelToolCapability.WorkspaceRead },
            { "filesystem.list", ModelToolCapability.WorkspaceRead },
            { "artifact.read", ModelToolCapability.WorkspaceRead },
            { "filesystem.search", ModelToolCapability.Search },
            { "search.text", ModelToolCapability.Search },
            { "tool.search", ModelToolCapability.Search },
            { "reference.resolve", ModelToolCapability.ReferenceResolve },
            { "plan.propose", ModelToolCapability.PlanProposal },
            { "mode.propose", ModelToolCapability.PlanProposal },
            // Activates one already eligible skill for this Session's next turn. It is a
            // bounded session-scoped proposal and uses the existing PlanProposal ceiling;
            // it grants neither filesystem/process capabilities nor permission grants.
            { "skill.load", ModelToolCapability.PlanProposal },
            { "filesystem.patch", ModelToolCapability.PatchExisting },
            { "filesystem.write", ModelToolCapability.CreateFile },
            { "filesystem.delete", ModelToolCapability.DeleteFile },
            { "filesystem.move", ModelToolCapability.MoveOrRename },
            // Batch integration can create, replace, and delete. Classify it at the strictest
            // existing destructive capability so only Full mutation policy can expose it and
            // Delete=Ask/Deny is honored for the entire batch.
            { "worktree.integrate", ModelToolCapability.DeleteFile },
            { "extension.batch.apply", ModelToolCapability.ReplaceFile },
            { "process.exec", ModelToolCapability.GeneralProcess },
            { "shell.exec", ModelToolCapability.Shell },
            { "user.ask", ModelToolCapability.PlanProposal },
            { "memory.propose", ModelToolCapability.PlanProposal },
            // A receive is a bounded, side-effect-free wait on the bound execution mailbox.
            { "core.agents.mailbox.receive", ModelToolCapability.AgentMailboxWait },
            // Tool de simulación de M1: escribe, se clasifica como reemplazo.
            { "fake.write", ModelToolCapability.ReplaceFile },
            { "fake.read", ModelToolCapability.WorkspaceRead },
            { "fake.test", ModelToolCapability.ValidationProcess },
        };

    private readonly IReadOnlyDictionary<string, ModelToolCapability> _toolCapabilities;

    private readonly EffectiveModelPolicy _policy;

    /// <summary>
    /// Registro de lecturas efectivas por-Run (ADR-0044 §5): lo alimentan los reads exitosos y
    /// lo consulta filesystem.patch antes de mutar. Vive EN la frontera (es el ámbito per-Run de
    /// la política del modelo) y viaja a los tools vía <c>ToolExecutionContext.ReadRegistry</c>.
    /// Nunca es un estado global entre sesiones.
    /// </summary>
    private readonly FileReadRegistry _readRegistry;
    private readonly Func<SessionSecurityPolicySet?>? _sessionSecurityPolicy;
    private readonly bool _trustedValidationProcess;

    public ModelCapabilityBoundary(EffectiveModelPolicy policy)
        : this(policy, CoreTools)
    {
    }

    public ModelCapabilityBoundary(EffectiveModelPolicy policy,
        IReadOnlyDictionary<string, ModelToolCapability> toolCapabilities)
        : this(policy, toolCapabilities, new FileReadRegistry())
    {
    }

    public ModelCapabilityBoundary(EffectiveModelPolicy policy,
        IReadOnlyDictionary<string, ModelToolCapability> toolCapabilities, FileReadRegistry readRegistry)
        : this(policy, toolCapabilities, readRegistry, null)
    {
    }

    public ModelCapabilityBoundary(EffectiveModelPolicy policy,
        IReadOnlyDictionary<string, ModelToolCapability> toolCapabilities, FileReadRegistry readRegistry,
        Func<SessionSecurityPolicySet?>? sessionSecurityPolicy, bool trustedValidationProcess = false)
    {
        _policy = policy;
        _toolCapabilities = toolCapabilities;
        _readRegistry = readRegistry;
        _sessionSecurityPolicy = sessionSecurityPolicy;
        _trustedValidationProcess = trustedValidationProcess;
        // La contabilidad de mutaciones por Run (EPIC-021) viaja con el registro de lecturas:
        // ambos son el estado per-Run de la política del modelo.
        if (_sessionSecurityPolicy is null) _readRegistry.Ledger.Bind(policy.MutationPolicy);
        else _readRegistry.Ledger.BindProvider(IntersectedMutationPolicy);
    }

    /// <summary>Registro de lecturas efectivas de este Run (ADR-0044 §5).</summary>
    public FileReadRegistry ReadRegistry() => _readRegistry;

    /// <summary>Marks a fixed Host-owned validator invocation. Its exact argv and permission
    /// checks remain in the Host process tool; model policy does not grant or veto this user
    /// configured gate, while the durable Session ceiling still bounds the effect.</summary>
    public ModelCapabilityBoundary ForTrustedValidationProcess()
    {
        var toolCapabilities = _toolCapabilities.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        toolCapabilities["process.exec"] = ModelToolCapability.ValidationProcess;
        return new ModelCapabilityBoundary(_policy, toolCapabilities, _readRegistry, _sessionSecurityPolicy,
            trustedValidationProcess: true);
    }

    /// <summary>
    /// Señala el inicio de un Turn en la contabilidad por Run (ADR-0044 §5): reinicia los
    /// contadores por Turn de la política de mutación (archivos y líneas). La invoca el Engine
    /// al abrir cada Turn. Sin esta señal, la contabilidad acumula monótonamente (conservador:
    /// rechaza de más, nunca de menos).
    /// </summary>
    public void BeginTurn() => _readRegistry.Ledger.BeginTurn();

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
        var privateExtensionStage = IsPrivateExtensionStageIntent(intent, toolName);
        var sessionDecision = EvaluateSessionSecurityCeiling(intent, toolName);
        if (sessionDecision is { Allowed: false }) return sessionDecision;

        // A configured completion gate is a User-owned, Host-bound argv. Do not let a model
        // profile disable that existing control, but require the durable Session ceiling above.
        if (_trustedValidationProcess && toolName == "process.exec"
            && _toolCapabilities.TryGetValue(toolName, out var trustedCapability)
            && trustedCapability == ModelToolCapability.ValidationProcess)
            return sessionDecision is { RequiresAsk: true } ? sessionDecision : ModelCapabilityDecision.Allow();

        // 1. Tool inventada u oculta: sin clasificación no hay autorización posible.
        if (!_toolCapabilities.TryGetValue(toolName, out var capability))
        {
            return ModelCapabilityDecision.Reject(
                "tool sin clasificar en la política de capacidad: " + toolName, null);
        }

        // ToolRuntime attaches this non-model-supplied classification only to a prepared
        // filesystem.write intent after checking target metadata. The policy ceiling follows
        // the effective operation (create, localized edit, or full replacement), not payload
        // shape. Namespaced extension tools cannot claim this core identity.
        if (toolName is "filesystem.write" or "filesystem.patch" or "extension.batch.apply"
            && intent.EffectiveMutationOperation is { } effectiveOperation)
            capability = effectiveOperation;

        if (toolName == "worktree.integrate")
        {
            // A batch may create, replace, or delete depending on the immutable proposal.
            // Prepare is pure and must not inspect the filesystem to narrow those operations,
            // so model-originated batches require the complete file mutation ceiling.
            var required = new[] { ModelToolCapability.CreateFile, ModelToolCapability.ReplaceFile,
                ModelToolCapability.DeleteFile };
            if (_policy.MutationPolicy.Mode != FileMutationMode.Full
                || required.Any(item => !_policy.ToolPolicy.Allows(item)))
                return ModelCapabilityDecision.Reject(
                    "worktree integration requires Full file mutation capabilities", capability);
            if (_policy.MutationPolicy.Delete == DestructiveActionPolicy.Deny)
                return ModelCapabilityDecision.Reject(
                    "worktree integration includes a destructive delete-capable batch denied by policy", capability);
            if (_policy.MutationPolicy.Delete == DestructiveActionPolicy.Ask)
                return ModelCapabilityDecision.Ask(
                    "worktree integration can create, replace, or delete workspace files", capability);
        }

        // Legacy replacement-classified integrations retain their historical mode mapping.
        // Core filesystem.write instead carries its runtime-classified effective operation.
        if (capability == ModelToolCapability.ReplaceFile
            && intent.EffectiveMutationOperation != ModelToolCapability.ReplaceFile)
        {
            var writeCapability = WriteCapabilityForMode();
            if (writeCapability is null)
            {
                return ModelCapabilityDecision.Reject(
                    "el modo de mutación " + _policy.MutationPolicy.Mode + " de la categoría "
                    + _policy.Category + " no permite escribir archivos completos; usa filesystem.patch"
                        + " para cambios localizados",
                    ModelToolCapability.ReplaceFile);
            }

            capability = writeCapability.Value;
        }

        if (!_policy.ToolPolicy.Allows(capability))
        {
            return ModelCapabilityDecision.Reject(
                "capacidad " + capability + " fuera del techo de la categoría " + _policy.Category
                    + (intent.ToolId.ToString() == "filesystem.write" ? "; usa filesystem.patch para editar archivos existentes" : ""),
                capability);
        }
        if (intent.RequiresPhysicalCreateCapability
            && (!_policy.ToolPolicy.Allows(ModelToolCapability.CreateFile)
                || !MutationAllowed(ModelToolCapability.CreateFile)))
            return ModelCapabilityDecision.Reject("recreating a tombstoned path also requires CreateFile capability",
                ModelToolCapability.CreateFile);

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
        if (intent.Claims.Writes.Count > 0 && !privateExtensionStage && !MutationAllowed(capability)
            && !IsBoundValidationProcess(capability, intent))
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

            if (rule == DestructiveActionPolicy.Ask)
            {
                return ModelCapabilityDecision.Ask(
                    "acción destructiva " + capability + " en Ask por la categoría " + _policy.Category,
                    capability);
            }
        }

        return sessionDecision is { RequiresAsk: true } ? sessionDecision : ModelCapabilityDecision.Allow();
    }

    private ModelCapabilityDecision? EvaluateSessionSecurityCeiling(ToolIntent intent, string toolName)
    {
        if (_sessionSecurityPolicy is null) return null;
        var requiresAsk = false;
        SessionSecurityPolicySet? ceiling;
        try
        {
            ceiling = _sessionSecurityPolicy();
            if (ceiling is null) return ModelCapabilityDecision.Reject("durable Session security policy is unavailable", null);
            ceiling.Validate();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            return ModelCapabilityDecision.Reject("durable Session security policy could not be verified", null);
        }

        if (!_toolCapabilities.TryGetValue(toolName, out var capability))
            return ModelCapabilityDecision.Reject("tool has no Session security classification: " + toolName, null);
        if (toolName is "filesystem.write" or "filesystem.patch" or "extension.batch.apply"
            && intent.EffectiveMutationOperation is { } effectiveOperation)
            capability = effectiveOperation;
        if (toolName == "worktree.integrate")
        {
            var required = new[] { ModelToolCapability.CreateFile, ModelToolCapability.ReplaceFile,
                ModelToolCapability.DeleteFile };
            if (ceiling.FileMutationMode != FileMutationMode.Full
                || required.Any(item => !ceiling.CapabilityCeiling.Contains(item)))
                return ModelCapabilityDecision.Reject("worktree integration exceeds the Session security ceiling", capability);
            if (ceiling.DeletePolicy == DestructiveActionPolicy.Deny)
                return ModelCapabilityDecision.Reject("Session security policy denies destructive integration", capability);
            if (ceiling.DeletePolicy == DestructiveActionPolicy.Ask)
                requiresAsk = true;
        }
        if (capability == ModelToolCapability.ReplaceFile
            && intent.EffectiveMutationOperation != ModelToolCapability.ReplaceFile)
        {
            if (ceiling.FileMutationMode == FileMutationMode.PatchAndCreate)
                capability = ModelToolCapability.CreateFile;
            else if (ceiling.FileMutationMode != FileMutationMode.Full)
                return ModelCapabilityDecision.Reject("Session security policy does not allow full-file writes", capability);
        }
        if (!ceiling.CapabilityCeiling.Contains(capability))
            return ModelCapabilityDecision.Reject("capability is outside the current Session security ceiling: " + capability,
                capability);
        if (intent.RequiresPhysicalCreateCapability
            && (!ceiling.CapabilityCeiling.Contains(ModelToolCapability.CreateFile)
                || !MutationAllowed(ceiling.FileMutationMode, ModelToolCapability.CreateFile)))
            return ModelCapabilityDecision.Reject("recreating a tombstoned path also requires CreateFile in the Session ceiling",
                ModelToolCapability.CreateFile);
        if (intent.Claims.Network.Count > 0
            && !ceiling.CapabilityCeiling.Contains(ModelToolCapability.Network))
            return ModelCapabilityDecision.Reject("network claims are outside the current Session security ceiling",
                ModelToolCapability.Network);

        var boundedValidation = capability == ModelToolCapability.ValidationProcess
            && intent.Effect == EffectClass.NonIdempotent && intent.Risk == ToolRisk.High
            && intent.Claims.Process is { NetworkRequired: false, EffectClass: "External",
                WorkingDirectory: { Length: > 0 } cwd } && intent.Claims.Writes.Count == 1
            && string.Equals(intent.Claims.Writes.Single(), cwd,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (intent.Claims.Writes.Count > 0 && !IsPrivateExtensionStageIntent(intent, toolName)
            && !MutationAllowed(ceiling.FileMutationMode, capability)
            && !(boundedValidation && ceiling.FileMutationMode is FileMutationMode.PatchAndCreate or FileMutationMode.Full))
            return ModelCapabilityDecision.Reject("file mutation is outside the current Session security ceiling", capability);
        if (capability is ModelToolCapability.DeleteFile or ModelToolCapability.MoveOrRename)
        {
            var rule = capability == ModelToolCapability.DeleteFile ? ceiling.DeletePolicy : ceiling.MoveOrRenamePolicy;
            if (rule == DestructiveActionPolicy.Deny)
                return ModelCapabilityDecision.Reject("destructive action is denied by Session security policy", capability);
            if (rule == DestructiveActionPolicy.Ask)
                requiresAsk = true;
        }
        return requiresAsk
            ? ModelCapabilityDecision.Ask("action requires confirmation under Session security policy", capability)
            : ModelCapabilityDecision.Allow();
    }

    private static bool IsPrivateExtensionStageIntent(ToolIntent intent, string toolName) =>
        toolName.StartsWith("ext.", StringComparison.Ordinal)
        && intent.Effect == EffectClass.NonIdempotent
        && intent.Claims.Process is { Isolation: ProcessIsolation.PrivateExtensionWorkspace,
            NetworkRequired: false, WorkingDirectory: { Length: > 0 } }
        && intent.Claims.Network.Count == 0 && intent.Claims.Writes.Count is > 0 and <= 64;

    private FileMutationPolicy? IntersectedMutationPolicy()
    {
        SessionSecurityPolicySet? ceiling;
        try
        {
            ceiling = _sessionSecurityPolicy?.Invoke();
            if (ceiling is null) return new FileMutationPolicy(FileMutationMode.None,
                DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny, 0, 0, 0,
                true, true, true, false);
            ceiling.Validate();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            return new FileMutationPolicy(FileMutationMode.None,
                DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny, 0, 0, 0,
                true, true, true, false);
        }

        var current = _policy.MutationPolicy;
        var candidateOperations = new[] { ModelToolCapability.PatchExisting, ModelToolCapability.CreateFile,
            ModelToolCapability.ReplaceFile, ModelToolCapability.DeleteFile, ModelToolCapability.MoveOrRename };
        var allowed = candidateOperations.Where(operation => MutationAllowed(current.Mode, operation)
            && MutationAllowed(ceiling.FileMutationMode, operation)).ToHashSet();
        var mode = Enum.GetValues<FileMutationMode>().Where(candidate => candidateOperations
                .Where(operation => MutationAllowed(candidate, operation)).All(allowed.Contains))
            .OrderByDescending(candidate => candidateOperations.Count(operation => MutationAllowed(candidate, operation)))
            .First();
        var maximumFiles = mode == FileMutationMode.None ? 0
            : Math.Min(current.MaxFilesPerTurn, ceiling.MaxFilesPerTurn);
        var maximumLines = mode == FileMutationMode.None ? 0
            : Math.Min(current.MaxChangedLinesPerTurn, ceiling.MaxChangedLinesPerTurn);
        static DestructiveActionPolicy Stricter(DestructiveActionPolicy first, DestructiveActionPolicy second) =>
            Rank(first) >= Rank(second) ? first : second;
        return new FileMutationPolicy(mode, Stricter(current.Delete, ceiling.DeletePolicy),
            Stricter(current.MoveOrRename, ceiling.MoveOrRenamePolicy), maximumFiles, maximumLines,
            Math.Min(current.MaxRewriteRatio, ceiling.MaxRewriteRatio),
            current.RequirePriorRead || ceiling.RequirePriorRead,
            current.RequireExpectedVersionToken || ceiling.RequireExpectedVersionToken,
            current.RequirePostEditValidation || ceiling.RequirePostEditValidation,
            current.AllowParallelMutations && ceiling.AllowParallelMutations);
    }

    private static bool MutationAllowed(FileMutationMode mode, ModelToolCapability capability) => mode switch
    {
        FileMutationMode.None => false,
        FileMutationMode.PatchExisting => capability == ModelToolCapability.PatchExisting,
        FileMutationMode.PatchAndCreate => capability is ModelToolCapability.PatchExisting or ModelToolCapability.CreateFile,
        FileMutationMode.Full => capability is ModelToolCapability.PatchExisting or ModelToolCapability.CreateFile
            or ModelToolCapability.ReplaceFile or ModelToolCapability.DeleteFile or ModelToolCapability.MoveOrRename,
        _ => false,
    };

    private static int Rank(DestructiveActionPolicy policy) => policy switch
    {
        DestructiveActionPolicy.Deny => 2,
        DestructiveActionPolicy.Ask => 1,
        DestructiveActionPolicy.Allow => 0,
        _ => -1,
    };

    private bool IsBoundValidationProcess(ModelToolCapability capability, ToolIntent intent)
    {
        // A host-classified ValidationProcess may write generated output beneath its own exact
        // cwd (e.g. build/test artifacts). Its executable/argv/cwd binding and grant remain the
        // responsibility of the registering Host tool; this does not grant filesystem tools or
        // arbitrary dynamic ids any mutation capability.
        if (capability != ModelToolCapability.ValidationProcess
            || _policy.MutationPolicy.Mode != FileMutationMode.Full
            || intent.Effect != EffectClass.NonIdempotent || intent.Risk != ToolRisk.High
            || intent.Claims.Process is not { NetworkRequired: false, EffectClass: "External",
                WorkingDirectory: { Length: > 0 } cwd }
            || intent.Claims.Writes.Count != 1) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(intent.Claims.Writes.Single(), cwd, comparison);
    }

    /// <summary>
    /// Máximo de tools visibles de la política efectiva (ya intersectada con el harness,
    /// ADR-0044 §4). Lo usa el ToolPlanner (<c>VisibleTools</c>) para no mostrar más tools de
    /// las que la política concede.
    /// </summary>
    public int MaxVisibleTools()
    {
        if (_sessionSecurityPolicy is null) return _policy.ToolPolicy.MaxVisibleTools;
        try
        {
            var session = _sessionSecurityPolicy();
            return session is null ? 0 : Math.Min(_policy.ToolPolicy.MaxVisibleTools, session.MaxVisibleTools);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
            or UnauthorizedAccessException or InvalidOperationException) { return 0; }
    }

    /// <summary>An explicit full-agent profile receives the eligible catalog immediately,
    /// without first having to discover basic editing/execution tools.</summary>
    public bool UsesFullToolCatalog() => _policy.Category == ModelPolicyCategory.FullAgent;

    /// <summary>Discloses permitted tools in a stable, useful order before applying the visible
    /// count. Basic workspace read/search and authorized file edits remain available ahead of
    /// optional references, proposals and process capabilities when the list is small.</summary>
    public IReadOnlyList<ToolDefinition> VisibleTools(IEnumerable<ToolDefinition> definitions,
        IReadOnlyDictionary<string, int>? preferredOrder = null,
        IReadOnlyList<string>? explicitPriority = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        preferredOrder ??= new Dictionary<string, int>(StringComparer.Ordinal);
        var explicitRanks = explicitPriority is null
            ? null
            : explicitPriority.Select((name, index) => (name, index))
                .GroupBy(item => item.name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        return definitions.Where(tool => IsToolVisible(tool.Name))
            .OrderBy(tool => explicitRanks?.GetValueOrDefault(tool.Name, Int32.MaxValue) ?? Int32.MaxValue)
            .ThenBy(tool => DisclosurePriority(tool.Name))
            .ThenBy(tool => preferredOrder.GetValueOrDefault(tool.Name, int.MaxValue))
            .ThenBy(tool => tool.Name, StringComparer.Ordinal)
            .Take(MaxVisibleTools()).ToArray();
    }

    private int DisclosurePriority(string toolName)
    {
        if (!_toolCapabilities.TryGetValue(toolName, out var capability)) return 100;
        // filesystem.write is conditional: an existing target may be a localized edit even
        // when creation is outside the profile. Keep it alongside the other authorized edits;
        // Evaluate classifies the concrete target and rejects any forbidden create/replace.
        if (toolName == "filesystem.write") capability = ModelToolCapability.PatchExisting;
        // In PatchOnly, write is the sole model-facing way to submit a complete desired file
        // that the runtime can normalize into the authorized localized edit. Keep it in the
        // primary read/edit disclosure tier so a small visible-tool cap cannot hide it behind
        // optional tools (while Execute still classifies creation separately).
        if (toolName == "filesystem.write"
            && _policy.MutationPolicy.Mode == FileMutationMode.PatchExisting) return 1;
        return capability switch
        {
            ModelToolCapability.PlanProposal when toolName == "user.ask" => 0,
            ModelToolCapability.Search when toolName == "tool.search" => 1,
            ModelToolCapability.WorkspaceRead when toolName == "artifact.read" => 3,
            ModelToolCapability.WorkspaceRead when toolName == "media.read" => 2,
            ModelToolCapability.WorkspaceRead or ModelToolCapability.Search
                or ModelToolCapability.AgentMailboxWait => 1,
            // In Direct mode a six-tool harness cannot discover a hidden file creator.
            // Keep read/list/search plus both permitted file operations ahead of optional
            // references. IsToolVisible still filters the user's mutation ceiling first.
            ModelToolCapability.PatchExisting or ModelToolCapability.CreateFile
                or ModelToolCapability.ReplaceFile or ModelToolCapability.ReferenceResolve => 2,
            // An explicitly authorized PlanProposal ceiling should not hide the advisory
            // mode proposal behind the newer memory proposal when MaxVisibleTools is tight.
            ModelToolCapability.PlanProposal when toolName == "mode.propose" => 3,
            ModelToolCapability.PlanProposal => 4,
            ModelToolCapability.DeleteFile or ModelToolCapability.MoveOrRename => 5,
            ModelToolCapability.ValidationProcess => 6,
            ModelToolCapability.GeneralProcess => 7,
            ModelToolCapability.Shell or ModelToolCapability.Network => 8,
            _ => 100,
        };
    }

    /// <summary>
    /// True si la tool está clasificada Y su capacidad está dentro del techo del modelo
    /// (ADR-0044 §5.1): el ToolPlanner oculta así las tools no permitidas, reduciendo la
    /// superficie. Una tool oculta/inventada sigue siendo rechazada por <c>Evaluate</c> si el
    /// modelo la emite de todos modos (defensa en profundidad).
    /// </summary>
    public bool IsToolVisible(string toolName)
    {
        if (!_toolCapabilities.TryGetValue(toolName, out var capability))
        {
            return false;
        }

        // filesystem.write is conditional on target existence. Disclose it when either a
        // create or localized edit is authorized; the runtime classifies the actual target
        // after Prepare, so exposing the call cannot grant the other operation.
        if (toolName == "filesystem.write")
            return IsCapabilityVisible(toolName, ModelToolCapability.CreateFile)
                || IsCapabilityVisible(toolName, ModelToolCapability.PatchExisting);

        return IsCapabilityVisible(toolName, capability);
    }

    private bool IsCapabilityVisible(string toolName, ModelToolCapability capability)
    {

        // Compatibility mapping for legacy replacement-classified tools.
        if (capability == ModelToolCapability.ReplaceFile)
        {
            var writeCapability = WriteCapabilityForMode();
            if (writeCapability is not null)
            {
                capability = writeCapability.Value;
            }
        }

        // Keep the planner aligned with both the capability ceiling and the mutation mode.
        // A FullAgent profile may be narrowed to PatchExisting without exposing delete/move
        // controls that the execution boundary would reject later.
        if ((capability is ModelToolCapability.PatchExisting or ModelToolCapability.CreateFile
            or ModelToolCapability.ReplaceFile or ModelToolCapability.DeleteFile
            or ModelToolCapability.MoveOrRename) && !MutationAllowed(capability))
            return false;

        if (_sessionSecurityPolicy is not null)
        {
            SessionSecurityPolicySet? session;
            try
            {
                session = _sessionSecurityPolicy();
                if (session is null) return false;
                session.Validate();
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException
                or UnauthorizedAccessException or InvalidOperationException) { return false; }
            if (capability == ModelToolCapability.ReplaceFile)
            {
                if (session.FileMutationMode == FileMutationMode.PatchAndCreate)
                    capability = ModelToolCapability.CreateFile;
                else if (session.FileMutationMode != FileMutationMode.Full) return false;
            }
            if (!session.CapabilityCeiling.Contains(capability)) return false;
            if ((capability is ModelToolCapability.PatchExisting or ModelToolCapability.CreateFile
                or ModelToolCapability.ReplaceFile or ModelToolCapability.DeleteFile or ModelToolCapability.MoveOrRename)
                && !MutationAllowed(session.FileMutationMode, capability)) return false;
        }

        return _policy.ToolPolicy.Allows(capability);
    }

    /// <summary>Concasión del techo de mutación con la capacidad concreta de la tool.</summary>
    public bool MutationAllowed(ModelToolCapability capability)
    {
        return FileMutationRules.ModeAllows(_policy.MutationPolicy.Mode, capability);
    }

    /// <summary>
    /// Capacidad efectiva que la frontera exige a una tool legacy de reemplazo según
    /// el modo de mutación: Full → ReplaceFile; PatchAndCreate → CreateFile (exposición
    /// create-only, ADR-0044 §5); resto → null (oculta/rechazada).
    /// </summary>
    private ModelToolCapability? WriteCapabilityForMode()
    {
        return _policy.MutationPolicy.Mode switch
        {
            FileMutationMode.Full => ModelToolCapability.ReplaceFile,
            FileMutationMode.PatchAndCreate => ModelToolCapability.CreateFile,
            _ => null,
        };
    }
}

/// <summary>
/// Reglas modo de mutación → capacidad (ADR-0044 §5). Punto único compartido por la frontera
/// de capacidad y la contabilidad por Run (<c>MutationLedger</c>): la defensa en profundidad
/// de ambas usa exactamente las mismas reglas.
/// </summary>
public static class FileMutationRules
{
    public static bool ModeAllows(FileMutationMode mode, ModelToolCapability capability)
    {
        return mode switch
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
