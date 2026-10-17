using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Tests;

public sealed class SessionSecurityPolicyBoundaryTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "omni-session-security-boundary");

    [Theory]
    [InlineData(DestructiveActionPolicy.Allow, DestructiveActionPolicy.Ask, true, true,
        DestructiveActionPolicy.Ask)]
    [InlineData(DestructiveActionPolicy.Ask, DestructiveActionPolicy.Allow, true, true,
        DestructiveActionPolicy.Ask)]
    [InlineData(DestructiveActionPolicy.Ask, DestructiveActionPolicy.Deny, false, false,
        DestructiveActionPolicy.Deny)]
    [InlineData(DestructiveActionPolicy.Deny, DestructiveActionPolicy.Ask, false, false,
        DestructiveActionPolicy.Deny)]
    public void Destructive_action_intersection_uses_the_more_restrictive_model_and_session_rule(
        DestructiveActionPolicy modelRule, DestructiveActionPolicy sessionRule,
        bool expectedAllowed, bool expectedAsk, DestructiveActionPolicy expectedEffectiveRule)
    {
        var session = Policy(ModelPolicyCategory.FullAgent) with { DeletePolicy = sessionRule };
        var boundary = Boundary(WithDeleteRule(ModelPolicyPresets.FullAgent(), modelRule), () => session);

        var decision = boundary.Evaluate(DeleteIntent());

        Assert.Equal(expectedAllowed, decision.Allowed);
        Assert.Equal(expectedAsk, decision.RequiresAsk);
        Assert.Equal(expectedEffectiveRule, boundary.ReadRegistry().Ledger.MutationPolicy!.Delete);
    }

    [Fact]
    public void Session_scoped_coder_allows_exact_validation_only_when_model_already_allows_it()
    {
        var scopedSession = Policy(ModelPolicyCategory.ScopedCoder);
        var exactValidation = Boundary(ModelPolicyPresets.FullAgent(), () => scopedSession)
            .Evaluate(ValidationIntent());
        var modelWithoutValidation = Boundary(ModelPolicyPresets.PatchOnly(), () =>
            Policy(ModelPolicyCategory.FullAgent)).Evaluate(ValidationIntent());
        var arbitraryProcess = Boundary(ModelPolicyPresets.FullAgent(), () => scopedSession)
            .Evaluate(GeneralProcessIntent());

        var noneSession = Policy(ModelPolicyCategory.ScopedCoder) with
        { FileMutationMode = FileMutationMode.None, MaxFilesPerTurn = 0, MaxChangedLinesPerTurn = 0 };
        var patchSession = Policy(ModelPolicyCategory.ScopedCoder) with { FileMutationMode = FileMutationMode.PatchExisting };
        noneSession.Validate();
        patchSession.Validate();
        var validationWithNone = Boundary(ModelPolicyPresets.FullAgent(), () => noneSession)
            .Evaluate(ValidationIntent());
        var validationWithPatch = Boundary(ModelPolicyPresets.FullAgent(), () => patchSession)
            .Evaluate(ValidationIntent());

        Assert.True(exactValidation.Allowed, exactValidation.Reason);
        Assert.False(modelWithoutValidation.Allowed);
        Assert.False(arbitraryProcess.Allowed);
        Assert.False(validationWithNone.Allowed);
        Assert.False(validationWithPatch.Allowed);
    }

    [Fact]
    public void Dynamic_session_limits_intersect_with_model_and_revision_change_keeps_run_counters()
    {
        var session = Policy(ModelPolicyCategory.FullAgent) with
        {
            MaxFilesPerTurn = 2,
            MaxChangedLinesPerTurn = 3,
            MaxRewriteRatio = 0.25,
            RequirePriorRead = true,
            RequireExpectedVersionToken = true,
            RequirePostEditValidation = true,
            AllowParallelMutations = false,
        };
        var model = ModelPolicyPresets.FullAgent();
        model = WithMutationPolicy(model, new FileMutationPolicy(FileMutationMode.Full,
            DestructiveActionPolicy.Ask, DestructiveActionPolicy.Ask, 4, 5, 0.5,
            requirePriorRead: false, requireExpectedVersionToken: false,
            requirePostEditValidation: false, allowParallelMutations: true));
        var boundary = Boundary(model, () => session);
        var ledger = boundary.ReadRegistry().Ledger;
        var effective = ledger.MutationPolicy!;

        Assert.Equal(2, effective.MaxFilesPerTurn);
        Assert.Equal(3, effective.MaxChangedLinesPerTurn);
        Assert.Equal(0.25, effective.MaxRewriteRatio);
        Assert.True(effective.RequirePriorRead);
        Assert.True(effective.RequireExpectedVersionToken);
        Assert.True(effective.RequirePostEditValidation);
        Assert.False(effective.AllowParallelMutations);
        Assert.NotNull(ledger.RefuseMutation(ModelToolCapability.PatchExisting,
            "large-rewrite.txt", targetExists: true, deletedLines: 3, insertedLines: 0, originalLines: 10));

        boundary.BeginTurn();
        ledger.RecordMutation("first.txt", 0, 2, operation: ModelToolCapability.PatchExisting);
        ledger.RecordMutation("second.txt", 0, 1, operation: ModelToolCapability.PatchExisting);
        Assert.Equal(2, ledger.PendingValidations().Count);
        Assert.Equal(2, ledger.FilesTouchedThisTurn);
        Assert.Equal(3, ledger.ChangedLinesThisTurn);

        session = session with { PolicyRevision = 2, MaxFilesPerTurn = 1,
            Source = SessionSecurityPolicySource.UserSelection };
        session.Validate();
        Assert.Equal(2, ledger.FilesTouchedThisTurn);
        Assert.Equal(2, ledger.FilesTouchedThisRun);
        Assert.Equal(3, ledger.ChangedLinesThisRun);
        Assert.Equal(1, ledger.MutationPolicy!.MaxFilesPerTurn);
        Assert.NotNull(ledger.RefuseMutation(ModelToolCapability.PatchExisting,
            "third.txt", targetExists: false, deletedLines: 0, insertedLines: 0, originalLines: 0));
        Assert.Equal(2, ledger.FilesTouchedThisRun);
        Assert.Equal(3, ledger.ChangedLinesThisRun);
    }

    private static SessionSecurityPolicySet Policy(ModelPolicyCategory category, long revision = 1) =>
        SessionSecurityPolicySet.FromPreset(SessionId.New(), revision, category,
            SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow);

    private static ModelCapabilityBoundary Boundary(UserModelPolicy model,
        Func<SessionSecurityPolicySet?> sessionPolicy)
    {
        var effectiveModel = new EffectiveModelPolicy(ModelPolicyKey.For("test", "session-security"), 1,
            model.Category, model.ToolPolicy, model.MutationPolicy, isFallback: false,
            model.MaxModelStepsPerTurn, model.MaxToolCallsPerTurn, model.AllowOrchestration,
            model.MaxGenerationSeconds);
        return new ModelCapabilityBoundary(effectiveModel, ModelCapabilityBoundary.CoreTools,
            new FileReadRegistry(Workspace), sessionSecurityPolicy: sessionPolicy);
    }

    private static UserModelPolicy WithDeleteRule(UserModelPolicy model, DestructiveActionPolicy delete)
    {
        var mutation = model.MutationPolicy;
        var revisedMutation = new FileMutationPolicy(mutation.Mode, delete, mutation.MoveOrRename,
            mutation.MaxFilesPerTurn, mutation.MaxChangedLinesPerTurn, mutation.MaxRewriteRatio,
            mutation.RequirePriorRead, mutation.RequireExpectedVersionToken,
            mutation.RequirePostEditValidation, mutation.AllowParallelMutations);
        return new UserModelPolicy(model.Category, model.ToolPolicy, revisedMutation, model.Source, model.Note,
            model.MaxModelStepsPerTurn, model.MaxToolCallsPerTurn, model.AllowOrchestration,
            model.MaxGenerationSeconds);
    }

    private static UserModelPolicy WithMutationPolicy(UserModelPolicy model, FileMutationPolicy mutation) =>
        new(model.Category, model.ToolPolicy, mutation, model.Source, model.Note,
            model.MaxModelStepsPerTurn, model.MaxToolCallsPerTurn, model.AllowOrchestration,
            model.MaxGenerationSeconds);

    private static ToolIntent DeleteIntent() => new(ToolCallId.New(), new ToolId("filesystem.delete"), "{}",
        EffectClass.NonIdempotent, new ResourceClaims([], [Path.Combine(Workspace, "delete.txt")], [], null, []),
        ToolRisk.High, null);

    private static ToolIntent ValidationIntent() => new(ToolCallId.New(),
        new ToolId("fake.test"), "{}", EffectClass.NonIdempotent,
        new ResourceClaims([], [Workspace], [], new ProcessClaim("dotnet", ["test"], "External",
            NetworkRequired: false, WorkingDirectory: Workspace), []), ToolRisk.High, null);

    private static ToolIntent GeneralProcessIntent() => new(ToolCallId.New(), new ToolId("process.exec"), "{}",
        EffectClass.NonIdempotent,
        new ResourceClaims([], [Workspace], [], new ProcessClaim("cmd", ["/c", "echo", "no"], "External",
            NetworkRequired: false, WorkingDirectory: Workspace), []), ToolRisk.High, null);
}
