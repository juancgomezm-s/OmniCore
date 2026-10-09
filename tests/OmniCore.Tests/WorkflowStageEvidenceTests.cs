using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Abstractions;
using OmniCore.Engine;

namespace OmniCore.Tests;

public sealed class WorkflowStageEvidenceTests
{
    private const string Cwd = "C:\\workflow-workspace";
    private const string CheckArguments = "{\"executable\":\"dotnet\",\"argv\":[\"test\",\"tests/Connected.csproj\"],"
        + "\"cwd\":\"C:\\\\workflow-workspace\",\"timeoutSeconds\":120,\"networkRequired\":false}";

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

        internal Fixture()
        {
            _contract = new WorkflowStageContract(CompiledWorkflowCatalog.ExploreImplementVerify.Reference,
                "fixture-instance", CompiledWorkflowCatalog.Stage(WorkflowStage.Verify), _verifyTask, _verifyLane,
                "dotnet", "[\"test\",\"tests/Connected.csproj\"]", Cwd,
                "Verify fixture", "profile-fixture", _verifyExecution, 0, "", "");
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
