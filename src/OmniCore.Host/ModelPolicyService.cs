namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Resultado de seleccionar un modelo (ADR-0044 §6): o hay política o se exige onboarding.</summary>
public sealed class ModelSelectionResult
{
    public ModelSelectionState? Selection { get; }

    /// <summary>True cuando la clave no tiene UserModelPolicy y el cliente debe abrir el onboarding.</summary>
    public bool NeedsOnboarding { get; }

    public StoredModelPolicy? Policy { get; }

    /// <summary>Draft con la categoría recomendada para el formulario de onboarding.</summary>
    public ModelPolicyDraft? Draft { get; }

    private ModelSelectionResult(ModelSelectionState? selection, bool needsOnboarding,
        StoredModelPolicy? policy, ModelPolicyDraft? draft)
    {
        Selection = selection;
        NeedsOnboarding = needsOnboarding;
        Policy = policy;
        Draft = draft;
    }

    /// <summary>Selección con política vigente: se persiste y no abre onboarding.</summary>
    public static ModelSelectionResult WithPolicy(ModelSelectionState selection, StoredModelPolicy policy) =>
        new(selection, false, policy, null);

    /// <summary>Selección desconocida: el draft alimenta el onboarding (ADR-0044 §6).</summary>
    public static ModelSelectionResult OnboardingRequired(ModelPolicyDraft draft) =>
        new(null, true, null, draft);

    /// <summary>Modo seguro efímero (sin TTY o "usar una vez en modo seguro").</summary>
    public static ModelSelectionResult EphemeralSafe(ModelSelectionState selection, ModelPolicyDraft draft) =>
        new(selection, false, null, draft);
}

/// <summary>Resumen de la cualificación utilizable de una configuración de modelo.</summary>
/// <param name="Traits">Traits empíricos por nombre (0..1).</param>
/// <param name="FileMutationSamples">Muestras que respaldan FileMutationReliability.</param>
/// <param name="State">Estado del perfil (Qualified, Calibrated o Stale).</param>
public sealed record QualificationEvidence(IReadOnlyDictionary<string, double> Traits, int FileMutationSamples,
    ModelQualificationState State);

/// <summary>
/// Servicio de política operativa de modelos (ADR-0044). Orquesta el store relacional por clave
/// exacta, construye drafts de onboarding, resuelve la política efectiva (techo ∩ harness) y
/// registra la selección por workspace. La recomendación sin evidencia empírica es siempre
/// conservadora (ObserveOnly): el tamaño del modelo no amplía autonomía (ADR-0044 §2).
/// </summary>
public sealed class ModelPolicyService : IDisposable
{
    private readonly IModelPolicyStore _store;

    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Evidencia de cualificación de una clave (ADR-0044 §6): la suite quick y, si existe, la
    /// FileMutationReliability medida en uso real. Sin ella el onboarding recomienda ObserveOnly.
    /// </summary>
    public Func<ModelPolicyKey, CancellationToken, QualificationEvidence?>? EvidenceLookup { get; set; }

    public ModelPolicyService(IModelPolicyStore store) : this(store, null)
    {
    }

    public ModelPolicyService(IModelPolicyStore store, Func<DateTimeOffset>? clock)
    {
        _store = store;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    public void Dispose() { (_store as IDisposable)?.Dispose(); GC.SuppressFinalize(this); }

    public StoredModelPolicy? Get(ModelPolicyKey key, CancellationToken cancellationToken) =>
        _store.Get(key, cancellationToken);

    public IReadOnlyList<StoredModelPolicy> List(CancellationToken cancellationToken) =>
        _store.List(cancellationToken);

    public StoredModelPolicy Set(ModelPolicyKey key, long expectedRevision, UserModelPolicy policy,
        CancellationToken cancellationToken) => _store.Set(key, expectedRevision, policy, cancellationToken);

    public void Delete(ModelPolicyKey key, long expectedRevision, CancellationToken cancellationToken) =>
        _store.Delete(key, expectedRevision, cancellationToken);

    public IReadOnlyList<ModelPolicyChange> History(ModelPolicyKey key, CancellationToken cancellationToken) =>
        _store.History(key, cancellationToken);

    public ModelSelectionState? CurrentSelection(string workspaceId, CancellationToken cancellationToken) =>
        _store.GetSelection(workspaceId, cancellationToken);

    /// <summary>
    /// Draft de onboarding para una clave sin política (ADR-0044 §6). Con evidencia de cualificación
    /// (M5) la categoría sale de <see cref="QualificationRecommender"/>; sin ella, o sin una
    /// cualificación utilizable, es ObserveOnly y así se declara en Warnings. Solo recomienda: la
    /// política nunca se guarda sin una acción del usuario.
    /// </summary>
    public ModelPolicyDraft Draft(ModelPolicyKey key, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var category = ModelPolicyCategory.ObserveOnly;
        var evidence = EvidenceLookup?.Invoke(key, cancellationToken);
        if (evidence is null)
        {
            warnings.Add("sin evidencia de cualificación: la recomendación es conservadora (ObserveOnly)");
        }
        else
        {
            var recommendation = QualificationRecommender.Recommend(evidence.Traits, evidence.FileMutationSamples);
            category = recommendation.Category;
            warnings.AddRange(recommendation.Notes);
            if (evidence.State == ModelQualificationState.Stale)
                warnings.Add("la cualificación está obsoleta (versión nueva de la suite): recualifica para confirmarla");
        }
        if (key.ChatTemplateHash is null)
        {
            warnings.Add("configuración sin hash de chat template: la identidad de la política es más gruesa");
        }

        return new ModelPolicyDraft(key, category, warnings, hasQualificationEvidence: evidence is not null);
    }

    /// <summary>
    /// Selecciona un modelo (ADR-0044 §6, §9): si la clave exacta tiene política, persiste la
    /// selección; si no, exige onboarding (interactivo) o usa ObserveOnly efímero (no
    /// interactivo). Nunca amplía capacidad en silencio: el ephemeral queda marcado.
    /// </summary>
    public ModelSelectionResult Select(string workspaceId, ModelPolicyKey key, string modelId,
        bool ephemeralSafe, CancellationToken cancellationToken)
    {
        var stored = _store.Get(key, cancellationToken);
        var draft = Draft(key, cancellationToken);
        if (stored is not null)
        {
            // El modo efímero seguro marca la selección aunque exista política guardada
            // (ADR-0044 §6): "usar una vez en modo seguro" aplica solo a esta selección.
            var state = new ModelSelectionState(workspaceId, key, modelId, ephemeralSafe, _clock());
            _store.SetSelection(state, cancellationToken);
            return ModelSelectionResult.WithPolicy(state, stored!);
        }

        if (ephemeralSafe)
        {
            var state = new ModelSelectionState(workspaceId, key, modelId, ephemeralObserveOnly: true,
                _clock());
            _store.SetSelection(state, cancellationToken);
            return ModelSelectionResult.EphemeralSafe(state, draft);
        }

        // Sin política y sin modo efímero: el cliente debe abrir el onboarding antes de
        // confirmar la selección (ADR-0044 §6). No se persiste nada todavía.
        return ModelSelectionResult.OnboardingRequired(draft);
    }

    /// <summary>
    /// Política efectiva para una clave (ADR-0044 §1): techo del usuario ∩ HarnessPolicy.
    /// Sin política guardada el fallback es ObserveOnly (ADR-0044 §10.1): una configuración
    /// desconocida jamás escribe.
    /// </summary>
    public EffectiveModelPolicy Effective(ModelPolicyKey key, HarnessPolicy harness,
        CancellationToken cancellationToken)
    {
        var stored = _store.Get(key, cancellationToken);
        return EffectiveModelPolicy.Resolve(key, stored, harness);
    }

    /// <summary>
    /// Política efectiva de la selección vigente de un workspace, respetando el modo efímero
    /// seguro: una selección efímera aplica ObserveOnly aunque exista una política guardada
    /// (ADR-0044 §6 "usar una vez en modo seguro" es solo para esta selección).
    /// </summary>
    public EffectiveModelPolicy EffectiveForSelection(string workspaceId, HarnessPolicy harness,
        CancellationToken cancellationToken)
    {
        var selection = _store.GetSelection(workspaceId, cancellationToken);
        if (selection is null)
        {
            throw new InvalidOperationException("no hay selección de modelo para el workspace " + workspaceId);
        }

        if (selection!.EphemeralObserveOnly)
        {
            var fallback = ModelPolicyPresets.ObserveOnly();
            return new EffectiveModelPolicy(selection!.Key, null, fallback.Category, fallback.ToolPolicy,
                fallback.MutationPolicy, isFallback: true);
        }

        return Effective(selection!.Key, harness, cancellationToken);
    }
}
