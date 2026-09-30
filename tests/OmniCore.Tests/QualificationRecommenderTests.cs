using OmniCore.Domain;

namespace OmniCore.Tests;

/// <summary>
/// Tests de la función pura de recomendación (ADR-0044 §6, M5): de los traits empíricos sale una
/// categoría recomendada que se MUESTRA pero solo se aplica por el flujo explícito
/// `omni model policy set`. FullAgent jamás se recomienda: es un techo que solo el usuario fija.
/// </summary>
public sealed class QualificationRecommenderTests
{
    private static IReadOnlyDictionary<string, double> Traits(
        double instruction = 1.0, double structured = 1.0, double? fileMutation = null)
    {
        var map = new Dictionary<string, double>
        {
            ["InstructionFollowing"] = instruction,
            ["StructuredOutputReliability"] = structured,
        };
        if (fileMutation is { } mutation)
        {
            map["FileMutationReliability"] = mutation;
        }

        return map;
    }

    [Fact]
    public void Quick_pass_recommends_PatchOnly_with_its_preset()
    {
        var recommendation = QualificationRecommender.Recommend(Traits());

        Assert.Equal(ModelPolicyCategory.PatchOnly, recommendation.Category);
        Assert.Equal(FileMutationMode.PatchExisting, recommendation.MutationPolicy.Mode);
        Assert.Equal(2, recommendation.MutationPolicy.MaxFilesPerTurn);
    }

    [Fact]
    public void Weak_evidence_recommends_ObserveOnly()
    {
        var recommendation = QualificationRecommender.Recommend(Traits(instruction: 0.5, structured: 0.2));

        Assert.Equal(ModelPolicyCategory.ObserveOnly, recommendation.Category);
        Assert.Equal(FileMutationMode.None, recommendation.MutationPolicy.Mode);
    }

    [Fact]
    public void Missing_traits_recommends_ObserveOnly()
    {
        var recommendation = QualificationRecommender.Recommend(new Dictionary<string, double>());

        Assert.Equal(ModelPolicyCategory.ObserveOnly, recommendation.Category);
    }

    [Fact]
    public void High_FileMutationReliability_enables_ScopedCoder_recommendation()
    {
        var recommendation = QualificationRecommender.Recommend(Traits(fileMutation: 0.9));

        Assert.Equal(ModelPolicyCategory.ScopedCoder, recommendation.Category);
        Assert.Equal(FileMutationMode.PatchAndCreate, recommendation.MutationPolicy.Mode);
    }

    [Fact]
    public void Low_FileMutationReliability_forces_ObserveOnly_even_if_quick_passed()
    {
        var recommendation = QualificationRecommender.Recommend(Traits(fileMutation: 0.3));

        Assert.Equal(ModelPolicyCategory.ObserveOnly, recommendation.Category);
    }

    [Fact]
    public void FullAgent_is_never_recommended_even_with_perfect_traits()
    {
        var recommendation = QualificationRecommender.Recommend(Traits(fileMutation: 1.0));

        Assert.NotEqual(ModelPolicyCategory.FullAgent, recommendation.Category);
        Assert.Equal(ModelPolicyCategory.ScopedCoder, recommendation.Category);
    }

    [Fact]
    public void Recommendation_is_deterministic_and_documents_itself()
    {
        var first = QualificationRecommender.Recommend(Traits());
        var second = QualificationRecommender.Recommend(Traits());

        // La recomendación es una función pura de los traits: misma entrada, mismo resultado.
        Assert.Equal(first.Category, second.Category);
        Assert.Equal(first.MutationPolicy.Mode, second.MutationPolicy.Mode);
        Assert.Equal(first.MutationPolicy.MaxFilesPerTurn, second.MutationPolicy.MaxFilesPerTurn);
        Assert.Equal(first.MutationPolicy.MaxChangedLinesPerTurn, second.MutationPolicy.MaxChangedLinesPerTurn);
        Assert.Equal(first.Notes, second.Notes);
        Assert.Contains(first.Notes, note => note.Contains("FullAgent"));
    }
}
