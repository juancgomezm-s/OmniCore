using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Tools;
using OmniCore.Execution;

namespace OmniCore.Tests;

public sealed class WorkflowStageEvidenceTests
{
    private const string Cwd = "C:\\workflow-workspace";
    private const string CheckArguments = "{\"executable\":\"dotnet\",\"argv\":[\"test\",\"tests/Connected.csproj\"],"
        + "\"cwd\":\"C:\\\\workflow-workspace\",\"timeoutSeconds\":120,\"networkRequired\":false}";

    [Fact]
    public void Explore_accepts_a_successful_receipt_from_the_registered_filesystem_search_tool()
    {
        var fixture = new Fixture(WorkflowStage.Explore);
        fixture.AppendSearch(sibling: false);

        var evidence = fixture.Evaluate();

        Assert.True(evidence.Passed, evidence.Reason);
        Assert.Single(evidence.Receipts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_read_only_filesystem_search_does_not_invalidate_verification(bool completed)
    {
        var fixture = new Fixture();
        fixture.AppendVerification();
        fixture.AppendSearch(sibling: true, completed: completed);

        var evidence = fixture.Evaluate();

        Assert.True(evidence.Passed, evidence.Reason);
        Assert.Single(evidence.Receipts);
    }

    [Fact]
    public void Verify_receipt_remains_current_when_no_workspace_mutation_follows()
    {
        var fixture = new Fixture();
        fixture.AppendVerification();

        var evidence = fixture.Evaluate();

        Assert.True(evidence.Passed, evidence.Reason);
        Assert.Single(evidence.Receipts);
    }

    [Fact]
    public void Verify_receipt_is_rejected_when_a_sibling_starts_a_workspace_mutation_afterward()
    {
        var fixture = new Fixture();
        fixture.AppendVerification();
        fixture.AppendSiblingMutation();

        var evidence = fixture.Evaluate();

        Assert.False(evidence.Passed);
        Assert.Contains("stale", evidence.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(evidence.Receipts);
    }

    [Fact]
    public void Verify_receipt_is_rejected_when_a_sibling_mutation_is_still_pending_at_check_time()
    {
        var fixture = new Fixture();
        fixture.AppendSiblingMutation();
        fixture.AppendVerification();

        var evidence = fixture.Evaluate();

        Assert.False(evidence.Passed);
        Assert.Contains("stale", evidence.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sibling_declared_verifier_request_after_the_receipt_is_potentially_mutating_even_before_start()
    {
        var fixture = new Fixture();
        fixture.AppendVerification();
        fixture.AppendSiblingVerifier(start: false);

        var evidence = fixture.Evaluate();

        Assert.False(evidence.Passed);
        Assert.Contains("stale", evidence.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sibling_declared_verifier_started_before_the_receipt_blocks_currentness_until_settled()
    {
        var fixture = new Fixture();
        fixture.AppendSiblingVerifier(start: true);
        fixture.AppendVerification();

        var evidence = fixture.Evaluate();

        Assert.False(evidence.Passed);
        Assert.Contains("stale", evidence.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture
    {
        private readonly InMemoryEventStore _store = new();
        private readonly IEventCodecRegistry _codecs = EventCodecs.Create();
        private readonly SessionId _session = SessionId.New();
        private readonly RunId _run = RunId.New();
        private readonly TaskId _verifyTask = TaskId.New();
        private readonly LaneId _verifyLane = LaneId.New();
        private readonly ExecutionId _verifyExecution = ExecutionId.New();
        private readonly TaskId _siblingTask = TaskId.New();
        private readonly LaneId _siblingLane = LaneId.New();
        private readonly ExecutionId _siblingExecution = ExecutionId.New();
        private readonly WorkflowStageContract _contract;

        internal Fixture(WorkflowStage stage = WorkflowStage.Verify)
        {
            _contract = new WorkflowStageContract(CompiledWorkflowCatalog.ExploreImplementVerify.Reference,
                "fixture-instance", CompiledWorkflowCatalog.Stage(stage), _verifyTask, _verifyLane,
                "dotnet", "[\"test\",\"tests/Connected.csproj\"]", Cwd,
                "Verify fixture", "profile-fixture", _verifyExecution, 0, "", "");
        }

        internal void AppendSearch(bool sibling, bool completed = true)
        {
            var call = ToolCallId.New();
            var task = sibling ? _siblingTask : _verifyTask;
            var lane = sibling ? _siblingLane : _verifyLane;
            var execution = sibling ? _siblingExecution : _verifyExecution;
            const string arguments = "{\"pattern\":\"connected\",\"path\":\"src\"}";
            var tool = new SearchTextTool(new PathBoundaryValidator());
            Append(task, lane, execution,
                new ToolCallRequested(call, "provider-search", tool.Descriptor.Id.ToString(), arguments));
            if (!completed) return;
            Append(task, lane, execution, new ToolCallPrepared(call, arguments));
            Append(task, lane, execution, new ToolCallAuthorized(call));
            Append(task, lane, execution, new ToolCallStarted(call, EffectClass.None, null));
            Append(task, lane, execution, new ToolCallSucceeded(call, "{\"matches\":[]}"));
        }

        internal void AppendVerification()
        {
            var call = ToolCallId.New();
            Append(_verifyTask, _verifyLane, _verifyExecution,
                new ToolCallRequested(call, "provider-check", "dyn.core.verify_integration", CheckArguments));
            Append(_verifyTask, _verifyLane, _verifyExecution, new ToolCallPrepared(call, CheckArguments));
            Append(_verifyTask, _verifyLane, _verifyExecution, new ToolCallAuthorized(call));
            Append(_verifyTask, _verifyLane, _verifyExecution,
                new ToolCallStarted(call, EffectClass.NonIdempotent, null));
            Append(_verifyTask, _verifyLane, _verifyExecution,
                new ToolCallSucceeded(call, "{\"exitCode\":0}"));
        }

        internal void AppendSiblingMutation()
        {
            var call = ToolCallId.New();
            Append(_siblingTask, _siblingLane, _siblingExecution,
                new ToolCallRequested(call, "provider-write", "filesystem.write",
                    "{\"path\":\"src/connected.cs\",\"content\":\"changed\"}"));
            Append(_siblingTask, _siblingLane, _siblingExecution, new ToolCallPrepared(call, "{}"));
            Append(_siblingTask, _siblingLane, _siblingExecution, new ToolCallAuthorized(call));
            Append(_siblingTask, _siblingLane, _siblingExecution,
                new ToolCallStarted(call, EffectClass.NonIdempotent, "{}"));
        }

        internal void AppendSiblingVerifier(bool start)
        {
            var call = ToolCallId.New();
            Append(_siblingTask, _siblingLane, _siblingExecution,
                new ToolCallRequested(call, "sibling-verifier", "dyn.core.verify_integration", CheckArguments));
            if (!start) return;
            Append(_siblingTask, _siblingLane, _siblingExecution, new ToolCallPrepared(call, CheckArguments));
            Append(_siblingTask, _siblingLane, _siblingExecution, new ToolCallAuthorized(call));
            Append(_siblingTask, _siblingLane, _siblingExecution,
                new ToolCallStarted(call, EffectClass.NonIdempotent, null));
        }

        internal WorkflowStageEvidence Evaluate() => WorkflowStageEvidenceEvaluator.Evaluate(_contract,
            _session, _run, _verifyTask, _verifyLane, _verifyExecution,
            _store.ReadFrom(_session, 1), _codecs);

        private void Append(TaskId task, LaneId lane, ExecutionId execution, DomainEventPayload payload)
        {
            using var scope = ExecutionScope.Begin(new(_run, task, lane, ExecutionId: execution));
            new EventStream(_store, _codecs, _session).Append(payload);
        }
    }
}
