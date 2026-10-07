using System.Text.Json.Nodes;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class ModeSwitchPlanCoverageContractTests
{
    private static ModeSwitchAuthorization Authorization(ModeSwitchPlanCoverage? coverage) =>
        new(Guid.NewGuid(), 2, 1, "objective-digest", 3, [RunMode.Plan, RunMode.Act],
            new ModeSwitchLimits(1, 1, 3, 8, 120, 2m), DateTimeOffset.UtcNow, coverage);

    private static RunModeAuthority Authority(RunId run, ModeSwitchAuthorization authorization) =>
        new(run, 2, RunMode.Plan, ExecutionStrategy.Direct, ProductEffort.UltraCode,
            false, true, 1, "objective-digest", 3, authorization);

    [Fact]
    public void Coverage_is_optional_legacy_scope_and_with_copy_preserve_exact_plan_identity()
    {
        var run = RunId.New();
        var coverage = new ModeSwitchPlanCoverage(run, PlanId.New(), 7, TaskId.New());
        var authorization = Authorization(coverage);
        var authority = Authority(run, authorization);

        authority.Validate();
        Assert.Equal(coverage, authorization.PlanCoverage);
        Assert.Equal(7, authorization.PlanCoverage?.PlanRevision);

        var changed = authorization with
        {
            PlanCoverage = coverage with { PlanRevision = 8 },
        };
        changed.Validate();
        Assert.Equal(7, authorization.PlanCoverage?.PlanRevision);
        Assert.Equal(8, changed.PlanCoverage?.PlanRevision);

        var legacy = Authorization(null);
        Assert.Null(legacy.PlanCoverage);
        Assert.Null((legacy with { }).PlanCoverage);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("plan")]
    [InlineData("root-task")]
    [InlineData("revision-zero")]
    [InlineData("revision-negative")]
    public void Invalid_coverage_shape_is_rejected(string invalidField)
    {
        var coverage = new ModeSwitchPlanCoverage(RunId.New(), PlanId.New(), 1, TaskId.New());
        coverage = invalidField switch
        {
            "run" => coverage with { RunId = new RunId(Guid.Empty) },
            "plan" => coverage with { PlanId = new PlanId(Guid.Empty) },
            "root-task" => coverage with { RootTaskId = new TaskId(Guid.Empty) },
            "revision-zero" => coverage with { PlanRevision = 0 },
            "revision-negative" => coverage with { PlanRevision = -1 },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField)),
        };

        Assert.Throws<ArgumentException>(() => Authorization(coverage).Validate());
    }

    [Fact]
    public void Authority_rejects_plan_coverage_for_another_run()
    {
        var run = RunId.New();
        var authorization = Authorization(new ModeSwitchPlanCoverage(
            RunId.New(), PlanId.New(), 1, TaskId.New()));
        var authority = Authority(run, authorization);

        Assert.Throws<ArgumentException>(authority.Validate);
    }

    [Fact]
    public void Selected_authority_codec_round_trips_plan_coverage_and_legacy_payload_defaults_null()
    {
        var codecs = EventCodecs.Create();
        var run = RunId.New();
        var coverage = new ModeSwitchPlanCoverage(run, PlanId.New(), 4, TaskId.New());
        var selected = new RunModeAuthoritySelected(Authority(run, Authorization(coverage)),
            "fixture-command", "User");
        var codec = codecs.CodecFor(selected.Type());

        var currentJson = codec.Encode(selected);
        var current = Assert.IsType<RunModeAuthoritySelected>(codec.Decode(selected.Type(), currentJson));
        Assert.Equal(coverage, current.Authority.Authorization?.PlanCoverage);

        var legacyJson = JsonNode.Parse(codec.Encode(new RunModeAuthoritySelected(
            Authority(run, Authorization(null)), "legacy-command", "User")))!.AsObject();
        legacyJson["Authority"]!["Authorization"]!.AsObject().Remove("PlanCoverage");
        var legacy = Assert.IsType<RunModeAuthoritySelected>(codec.Decode(selected.Type(), legacyJson.ToJsonString()));

        Assert.Null(legacy.Authority.Authorization?.PlanCoverage);
        Assert.Equal(run, legacy.Authority.RunId);
    }
}
