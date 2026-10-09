using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Tests;

public sealed class ValidationProcessBoundaryTests
{
    private const string Workspace = "C:\\workspace";

    [Fact]
    public void Exact_host_classified_validation_process_can_claim_only_its_exact_working_directory()
    {
        var decision = Boundary(FileMutationMode.Full).Evaluate(Verifier([Workspace], Workspace));

        Assert.True(decision.Allowed, decision.Reason);
    }

    [Theory]
    [InlineData("extra write claim")]
    [InlineData("mismatched working directory")]
    public void Validation_process_write_exception_rejects_claims_outside_the_exact_cwd(string mismatch)
    {
        var writes = mismatch == "extra write claim" ? new[] { Workspace, "C:\\other" } : new[] { Workspace };
        var cwd = mismatch == "extra write claim" ? Workspace : "C:\\other";

        var decision = Boundary(FileMutationMode.Full).Evaluate(Verifier(writes, cwd));

        Assert.False(decision.Allowed);
        Assert.Contains("mutación de archivos no permitida", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Filesystem_write_and_unknown_dynamic_tools_remain_outside_verify_capability_ceiling()
    {
        var boundary = Boundary(FileMutationMode.Full);
        var fileWrite = boundary.Evaluate(new ToolIntent(ToolCallId.New(), new ToolId("filesystem.write"), "{}",
            EffectClass.NonIdempotent, new ResourceClaims([], [Workspace], [], null, []), ToolRisk.High, null));
        var unknownDynamic = boundary.Evaluate(new ToolIntent(ToolCallId.New(), new ToolId("dyn.core.unregistered"), "{}",
            EffectClass.NonIdempotent, new ResourceClaims([], [Workspace], [], null, []), ToolRisk.High, null));

        Assert.False(fileWrite.Allowed);
        Assert.False(unknownDynamic.Allowed);
    }

    [Fact]
    public void Validation_process_does_not_raise_a_restrictive_mutation_policy()
    {
        var decision = Boundary(FileMutationMode.PatchExisting).Evaluate(Verifier([Workspace], Workspace));

        Assert.False(decision.Allowed);
        Assert.Contains("mutación de archivos no permitida", decision.Reason, StringComparison.Ordinal);
    }

    private static ToolIntent Verifier(IReadOnlyList<string> writes, string workingDirectory) =>
        new(ToolCallId.New(), new ToolId("dyn.core.verify_integration"), "{}", EffectClass.NonIdempotent,
            new ResourceClaims([], writes, [], new ProcessClaim("dotnet", ["test"], "External",
                NetworkRequired: false, WorkingDirectory: workingDirectory), []), ToolRisk.High, null);

    private static ModelCapabilityBoundary Boundary(FileMutationMode mutationMode)
    {
        var capabilities = new Dictionary<string, ModelToolCapability>(ModelCapabilityBoundary.CoreTools,
            StringComparer.Ordinal)
        {
            ["dyn.core.verify_integration"] = ModelToolCapability.ValidationProcess,
        };
        var key = ModelPolicyKey.For("fixture", "workflow");
        var toolPolicy = new ModelToolPolicy(ToolMode.Direct, 4, false,
            new HashSet<ModelToolCapability> { ModelToolCapability.ValidationProcess });
        var mutation = new FileMutationPolicy(mutationMode, DestructiveActionPolicy.Deny,
            DestructiveActionPolicy.Deny, 8, 2000, 1d, requirePriorRead: false,
            requireExpectedVersionToken: false, requirePostEditValidation: false, allowParallelMutations: false);
        var policy = new EffectiveModelPolicy(key, 1, ModelPolicyCategory.FullAgent, toolPolicy,
            mutation, isFallback: false);
        return new ModelCapabilityBoundary(policy, capabilities);
    }
}
