namespace OmniCore.Domain;

/// <summary>
/// Recomendación de política operativa derivada de evidencia de cualificación (ADR-0044 §6, M5).
/// Es solo una sugerencia visible: el Host nunca la aplica. Aplicarla exige el flujo explícito
/// <c>omni model policy set</c> (ADR-0044 §9). La evidencia puede recomendar pero nunca ampliar
/// automáticamente la política operativa del usuario (criterio de salida M5).
/// </summary>
public sealed record QualificationRecommendation(
    ModelPolicyCategory Category,
    FileMutationPolicy MutationPolicy,
    IReadOnlyList<string> Notes);

/// <summary>
/// Función pura de traits empíricos → categoría recomendada (ADR-0007 §3, ADR-0044 §6, M5).
/// Determinista y conservadora:
/// <list type="bullet">
/// <item>sin evidencia suficiente siempre recomienda ObserveOnly;</item>
/// <item><see cref="ModelPolicyCategory.FullAgent"/> jamás se recomienda: es un techo que solo el
/// usuario puede fijar de forma explícita;</item>
/// <item><c>FileMutationReliability</c> bajo fuerza ObserveOnly aunque el resto del quick pase, y
/// alto solo habilita ScopedCoder: el usuario sigue decidiendo.</item>
/// </list>
/// Los umbrales están fijados por test hasta que se muevan a configuración versionada (ADR-0007 §3).
/// </summary>
public static class QualificationRecommender
{
    public const string FullAgentNeverRecommendedNote =
        "FullAgent nunca se recomienda automáticamente: es un techo que solo el usuario fija de forma explícita";
    public const string NoGeneralFileMutationEvidenceNote =
        "sin evidencia de FileMutationReliability: las fixtures de cualificación no miden la fiabilidad general de las mutaciones de archivos.";
    public const string ConservativePatchOnlyNote =
        "recomendación conservadora: la evidencia de la suite no alcanza el umbral de PatchOnly";
    public const string LowFileMutationReliabilityNote =
        "FileMutationReliability por debajo del umbral: se recomienda ObserveOnly";
    public const string HighFileMutationReliabilityNote =
        "FileMutationReliability alta: se habilita recomendar ScopedCoder";
    public const string InsufficientFileMutationEvidencePrefix = "FileMutationReliability con ";
    public const string InsufficientFileMutationEvidenceMiddle = " muestras (mínimo ";
    public const string InsufficientFileMutationEvidenceSuffix = "): evidencia insuficiente, no se usa";

    /// <summary>Quick suite: cada probe es todo-o-nada; la recomendación exige pasar todo.</summary>
    public const double QuickPassThreshold = 0.9;

    /// <summary>FileMutationReliability que habilita recomendar ScopedCoder.</summary>
    public const double FileMutationScopedCoderThreshold = 0.8;

    /// <summary>FileMutationReliability por debajo de la cual se recomienda ObserveOnly.</summary>
    public const double FileMutationObserveOnlyThreshold = 0.5;

    /// <summary>
    /// Muestras mínimas para que FileMutationReliability cuente como evidencia: con menos, un único
    /// parche malo (o bueno) decidiría la recomendación. Es el 0.5 de confianza del trait.
    /// </summary>
    public const int MinimumFileMutationSamples = 5;

    /// <summary>Recomienda una categoría y su política de mutación a partir de los traits medidos.</summary>
    public static QualificationRecommendation Recommend(IReadOnlyDictionary<string, double> traits,
        int fileMutationSamples = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(traits);

        var notes = new List<string>
        {
            FullAgentNeverRecommendedNote,
        };

        var instruction = Trait(traits, "InstructionFollowing");
        var structured = Trait(traits, "StructuredOutputReliability");
        var hasFileMutation = traits.TryGetValue("FileMutationReliability", out var fileMutation);

        if (!hasFileMutation)
        {
            notes.Add(NoGeneralFileMutationEvidenceNote);
        }
        else if (fileMutationSamples < MinimumFileMutationSamples)
        {
            notes.Add(InsufficientFileMutationEvidencePrefix + fileMutationSamples
                + InsufficientFileMutationEvidenceMiddle + MinimumFileMutationSamples
                + InsufficientFileMutationEvidenceSuffix);
            hasFileMutation = false;
        }

        // Base: la suite quick (lectura/razonamiento/salida estructurada) solo habilita PatchOnly.
        var category = instruction >= QuickPassThreshold && structured >= QuickPassThreshold
            ? ModelPolicyCategory.PatchOnly
            : ModelPolicyCategory.ObserveOnly;
        if (category == ModelPolicyCategory.ObserveOnly)
        {
            notes.Add(ConservativePatchOnlyNote);
        }

        // FileMutationReliability solo puede restringir (suelo ObserveOnly) o habilitar un paso más
        // (ScopedCoder); nunca produce FullAgent ni autoriza nada por sí sola (ADR-0044 §1).
        if (hasFileMutation)
        {
            if (fileMutation < FileMutationObserveOnlyThreshold)
            {
                category = ModelPolicyCategory.ObserveOnly;
                notes.Add(LowFileMutationReliabilityNote);
            }
            else if (category == ModelPolicyCategory.PatchOnly && fileMutation >= FileMutationScopedCoderThreshold)
            {
                category = ModelPolicyCategory.ScopedCoder;
                notes.Add(HighFileMutationReliabilityNote);
            }
        }

        var preset = ModelPolicyPresets.For(category);
        return new QualificationRecommendation(category, preset.MutationPolicy, notes);
    }

    private static double Trait(IReadOnlyDictionary<string, double> traits, string name) =>
        traits.TryGetValue(name, out var value) ? value : 0.0;
}
