namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Host;

public sealed class ClaudeLaneAgentResultTests
{
    private const string Valid = "{\"outcome\":\"Succeeded\",\"summary\":\"done\",\"findings\":[\"checked\"],\"filesChanged\":[\"src/a.cs\"],\"remainingIssues\":[],\"confidence\":\"Medium\",\"proposedPlanMutations\":[]}";

    [Fact]
    public void Valid_structured_result_is_mapped_to_native_agent_result()
    {
        var validated = ClaudeLaneAgentResultValidator.Validate(Valid);
        Assert.NotNull(validated);
        Assert.Equal(ClaudeLaneAgentResultValidator.SchemaId, validated.SchemaId);
        Assert.Equal(AgentOutcome.Succeeded, validated.Result.Outcome);
        Assert.Equal("done", validated.Result.Summary);
        Assert.Equal("src/a.cs", Assert.Single(validated.Result.FilesChanged));
        Assert.Empty(validated.Result.ArtifactRefs);
        Assert.Empty(validated.Result.ProposedPlanMutations);
    }

    [Theory]
    [InlineData("{\"outcome\":\"Succeeded\",\"summary\":\"done\",\"findings\":[],\"filesChanged\":[\".env\"],\"remainingIssues\":[],\"confidence\":\"Medium\",\"proposedPlanMutations\":[]}")]
    [InlineData("{\"outcome\":\"Succeeded\",\"summary\":\"done\",\"findings\":[],\"filesChanged\":[],\"remainingIssues\":[],\"confidence\":\"Medium\",\"proposedPlanMutations\":[{}]}")]
    [InlineData("{\"outcome\":\"Succeeded\",\"summary\":\"done\",\"findings\":[],\"filesChanged\":[],\"remainingIssues\":[],\"confidence\":\"Medium\",\"proposedPlanMutations\":[],\"unexpected\":true}")]
    [InlineData("{\"outcome\":\"Succeeded\",\"outcome\":\"Failed\",\"summary\":\"done\",\"findings\":[],\"filesChanged\":[],\"remainingIssues\":[],\"confidence\":\"Medium\",\"proposedPlanMutations\":[]}")]
    public void Invalid_or_unsupported_structured_results_fail_closed(string json) =>
        Assert.Null(ClaudeLaneAgentResultValidator.Validate(json));
}
