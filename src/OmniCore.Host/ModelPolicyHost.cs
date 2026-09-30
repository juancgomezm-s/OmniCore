namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Models;

/// <summary>Descriptor seguro del registro de modelos para clientes que no deben depender de Models.</summary>
public sealed record ModelRegistryModelDescriptor(
    string Id,
    string ProviderId,
    long ContextWindow,
    long RecommendedUsableContext,
    long MaxOutputTokens,
    double? ParameterCountBillions = null);

/// <summary>Identidad primitiva de una política de modelo; el Host aplica valores predeterminados a los detalles opcionales.</summary>
public sealed record ModelPolicyKeyDto(
    string ProviderId,
    string ModelId,
    string? ModelRevision = null,
    string? Quantization = null,
    IReadOnlyList<string>? Adapters = null,
    string? Backend = null,
    string? BackendBuild = null,
    string? ChatTemplateHash = null,
    string AdapterProfile = "default",
    string PromptProfileVersion = "v1")
{
    public override string ToString() => ProviderId + "/" + ModelId
        + (Quantization is null ? "" : ":" + Quantization)
        + (Adapters is null || Adapters.Count == 0 ? "" : ":adapters=" + Adapters.Count)
        + (ChatTemplateHash is null ? "" : ":tpl=" + ChatTemplateHash)
        + ":" + AdapterProfile + ":" + PromptProfileVersion;
}

/// <summary>Descripción primitiva de una política de modelo almacenada.</summary>
public sealed record ModelPolicyRecordDto(
    ModelPolicyKeyDto Key,
    long Revision,
    string Category,
    string ToolMode,
    int MaxVisibleTools,
    bool AllowToolDiscovery,
    IReadOnlyList<string> CapabilityCeiling,
    string MutationMode,
    string DeletePolicy,
    string MoveOrRenamePolicy,
    int MaxFilesPerTurn,
    int MaxChangedLinesPerTurn,
    double MaxRewriteRatio,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Recomendación y avisos del onboarding sin exponer tipos de política del dominio.</summary>
public sealed record ModelPolicyDraftDto(
    ModelPolicyKeyDto Key,
    string RecommendedCategory,
    IReadOnlyList<string> Warnings,
    bool HasQualificationEvidence);

/// <summary>Resultado de seleccionar un modelo a través del Host.</summary>
public sealed record ModelPolicySelectionDto(
    bool NeedsOnboarding,
    bool IsEphemeralSafe,
    ModelPolicyRecordDto? Policy,
    ModelPolicyDraftDto? Draft);

/// <summary>Entrada primitiva del historial de políticas.</summary>
public sealed record ModelPolicyHistoryEntryDto(
    long Revision,
    string ChangeKind,
    DateTimeOffset ChangedAt);

/// <summary>Conflicto de revisión seguro para el Host, que sustituye la excepción de dominio en la frontera de la fachada.</summary>
public sealed class ModelPolicyRevisionConflict : Exception
{
    public long ExpectedRevision { get; }

    public long ActualRevision { get; }

    public ModelPolicyRevisionConflict(long expectedRevision, long actualRevision)
        : base("model policy revision conflict")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }
}

/// <summary>
/// API de políticas de modelo para clientes. Los tipos de dominio, persistencia y registro quedan
/// detrás de esta frontera; las operaciones propagan el token de cancelación al servicio subyacente.
/// </summary>
public sealed class ModelPolicyHost
{
    private readonly ModelPolicyService _service;
    private readonly IReadOnlyList<ModelRegistryModelDescriptor> _models;

    private ModelPolicyHost(ModelPolicyService service, ModelRegistry registry)
    {
        _service = service;
        _models = registry.Models().Select(ToDescriptor).ToArray();
    }

    /// <summary>Modelos configurados en el orden del registro, representados solo con campos primitivos.</summary>
    public IReadOnlyList<ModelRegistryModelDescriptor> Models => _models;

    /// <summary>Crea una fachada respaldada por el almacén de políticas y el registro de modelos del usuario.</summary>
    public static ModelPolicyHost Create(string? dataDirectoryOverride = null,
        IReadOnlyList<ModelRegistryModelDescriptor>? registryOverride = null)
    {
        var service = OmniHost.CreateModelPolicyService(dataDirectoryOverride);
        var registry = registryOverride is null
            ? OmniHost.LoadUserModelRegistry(OmniHost.CreatePlatformPaths(dataDirectoryOverride))
            : ToRegistry(registryOverride);
        return new ModelPolicyHost(service, registry);
    }

    /// <summary>Lista todas las políticas de modelo almacenadas.</summary>
    public IReadOnlyList<ModelPolicyRecordDto> List(CancellationToken cancellationToken) =>
        _service.List(cancellationToken).Select(ToRecord).ToArray();

    /// <summary>Obtiene la política exacta del modelo, o null si no hay ninguna almacenada.</summary>
    public ModelPolicyRecordDto? Get(ModelPolicyKeyDto key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _service.Get(ToDomainKey(key), cancellationToken) is { } stored ? ToRecord(stored) : null;
    }

    /// <summary>Prepara un borrador de onboarding para una identidad exacta de modelo.</summary>
    public ModelPolicyDraftDto Draft(ModelPolicyKeyDto key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ToDraft(_service.Draft(ToDomainKey(key), cancellationToken));
    }

    /// <summary>Selecciona un modelo y devuelve su política almacenada o una recomendación de onboarding.</summary>
    public ModelPolicySelectionDto Select(string workspaceId, ModelPolicyKeyDto key, string modelId,
        bool ephemeralSafe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var result = _service.Select(workspaceId, ToDomainKey(key), modelId, ephemeralSafe, cancellationToken);
        return new ModelPolicySelectionDto(result.NeedsOnboarding,
            result.Selection?.EphemeralObserveOnly ?? false,
            result.Policy is null ? null : ToRecord(result.Policy),
            result.Draft is null ? null : ToDraft(result.Draft));
    }

    /// <summary>Establece una política usando una de las categorías predefinidas.</summary>
    public ModelPolicyRecordDto Set(ModelPolicyKeyDto key, long expectedRevision, string category,
        string? note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Enum.TryParse<ModelPolicyCategory>(category, ignoreCase: false, out var parsed)
            || parsed == ModelPolicyCategory.Custom)
        {
            throw new ArgumentException("unsupported model policy category", nameof(category));
        }

        var preset = ModelPolicyPresets.For(parsed);
        var policy = note is null
            ? preset
            : new UserModelPolicy(preset.Category, preset.ToolPolicy, preset.MutationPolicy, preset.Source, note);
        try
        {
            return ToRecord(_service.Set(ToDomainKey(key), expectedRevision, policy, cancellationToken));
        }
        catch (ModelPolicyRevisionConflictException exception)
        {
            throw new ModelPolicyRevisionConflict(exception.ExpectedRevision, exception.ActualRevision);
        }
    }

    /// <summary>Elimina una política y conserva su historial.</summary>
    public void Delete(ModelPolicyKeyDto key, long expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            _service.Delete(ToDomainKey(key), expectedRevision, cancellationToken);
        }
        catch (ModelPolicyRevisionConflictException exception)
        {
            throw new ModelPolicyRevisionConflict(exception.ExpectedRevision, exception.ActualRevision);
        }
    }

    /// <summary>Lee los cambios de política para una identidad exacta de modelo.</summary>
    public IReadOnlyList<ModelPolicyHistoryEntryDto> History(ModelPolicyKeyDto key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _service.History(ToDomainKey(key), cancellationToken)
            .Select(change => new ModelPolicyHistoryEntryDto(change.Revision, change.ChangeKind, change.ChangedAt))
            .ToArray();
    }

    private static ModelRegistry ToRegistry(IReadOnlyList<ModelRegistryModelDescriptor> models)
    {
        var registry = new ModelRegistry();
        foreach (var model in models)
        {
            ArgumentNullException.ThrowIfNull(model);
            registry.AddModel(new ModelDefinition(model.Id, model.ProviderId, model.ContextWindow,
                model.RecommendedUsableContext, model.MaxOutputTokens, model.ParameterCountBillions));
        }

        return registry;
    }

    private static ModelRegistryModelDescriptor ToDescriptor(ModelDefinition model) => new(
        model.Id, model.ProviderId, model.ContextWindow, model.RecommendedUsableContext,
        model.MaxOutputTokens, model.ParameterCountBillions);

    private static ModelPolicyKey ToDomainKey(ModelPolicyKeyDto key) => new(
        key.ProviderId, key.ModelId, key.ModelRevision, key.Quantization,
        key.Adapters ?? Array.Empty<string>(), key.Backend, key.BackendBuild, key.ChatTemplateHash,
        key.AdapterProfile, key.PromptProfileVersion);

    private static ModelPolicyKeyDto ToDtoKey(ModelPolicyKey key) => new(
        key.ProviderId, key.ModelId, key.ModelRevision, key.Quantization, key.Adapters.ToArray(),
        key.Backend, key.BackendBuild, key.ChatTemplateHash, key.AdapterProfile, key.PromptProfileVersion);

    private static ModelPolicyDraftDto ToDraft(ModelPolicyDraft draft) => new(
        ToDtoKey(draft.Key), draft.RecommendedCategory.ToString(), draft.Warnings.ToArray(),
        draft.HasQualificationEvidence);

    private static ModelPolicyRecordDto ToRecord(StoredModelPolicy stored)
    {
        var policy = stored.Policy;
        return new ModelPolicyRecordDto(ToDtoKey(stored.Key), stored.Revision,
            policy.Category.ToString(), policy.ToolPolicy.Mode.ToString(), policy.ToolPolicy.MaxVisibleTools,
            policy.ToolPolicy.AllowToolDiscovery,
            policy.ToolPolicy.CapabilityCeiling.OrderBy(capability => (int)capability)
                .Select(capability => capability.ToString()).ToArray(),
            policy.MutationPolicy.Mode.ToString(), policy.MutationPolicy.Delete.ToString(),
            policy.MutationPolicy.MoveOrRename.ToString(), policy.MutationPolicy.MaxFilesPerTurn,
            policy.MutationPolicy.MaxChangedLinesPerTurn, policy.MutationPolicy.MaxRewriteRatio,
            policy.Note, stored.CreatedAt, stored.UpdatedAt);
    }
}
