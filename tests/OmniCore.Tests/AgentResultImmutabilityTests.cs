using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Engine;

namespace OmniCore.Tests;

public sealed class AgentResultImmutabilityTests
{
    [Fact]
    public void Result_with_plan_proposal_survives_sqlite_reopen_without_completing_task()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-result-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session);
            var item = PlanItemId.New();
            var result = new AgentResult(AgentOutcome.Succeeded, "fixture result", ["finding"], [], [], [],
                ConfidenceLevel.High, [PlanMutation.Reorder([item], MutationCause.Model)]);
            stream.Append(new LaneCompleted(run.RootLane, result));
            var sequence = store.CurrentSequence(session);
            store.Close();
            store = new SqliteEventStore(journal);
            var events = store.ReadFrom(session, 1);
            var decoded = Assert.Single(events.Select(codecs.Decode).OfType<LaneCompleted>()).Result!;
            Assert.Equal(item, Assert.Single(Assert.Single(decoded.ProposedPlanMutations).Target.ReorderList));
            Assert.Throws<NotSupportedException>(() => ((IList<PlanMutation>)decoded.ProposedPlanMutations).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<PlanItemId>)decoded.ProposedPlanMutations[0].Target.ReorderList).Clear());
            Assert.Equal(sequence, store.CurrentSequence(session));
            var replay = CanonicalStateTracker.Replay(codecs, events);
            Assert.Equal(TaskState.Running, replay.Task(run.RootTask));
            Assert.Equal(RunState.Running, replay.Run(run.RunId));
        }
        finally
        {
            store?.Close();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + journal);
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Caller_lists_and_with_assignments_cannot_rewrite_a_created_result()
    {
        var findings = new List<string> { "original" };
        var refs = new List<ArtifactRef>();
        var files = new List<string> { "file.cs" };
        var issues = new List<string> { "issue" };
        var mutations = new List<PlanMutation>();
        var result = new AgentResult(AgentOutcome.Succeeded, "summary", findings, refs, files, issues,
            ConfidenceLevel.High, mutations);
        findings[0] = "changed";
        files.Clear();
        issues.Clear();
        refs.Add(new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('a', 64)),
            1, "text/plain", ArtifactKind.Other, Sensitivity.Normal));
        mutations.Add(PlanMutation.Reorder([PlanItemId.New()], default));
        Assert.Equal("original", Assert.Single(result.Findings));
        Assert.Equal("file.cs", Assert.Single(result.FilesChanged));
        Assert.Equal("issue", Assert.Single(result.RemainingIssues));
        Assert.Empty(result.ArtifactRefs);
        Assert.Empty(result.ProposedPlanMutations);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Findings)[0] = "rewrite");
        Assert.Throws<NotSupportedException>(() => ((IList<ArtifactRef>)result.ArtifactRefs).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PlanMutation>)result.ProposedPlanMutations).Clear());
        var replacement = new List<string> { "rework" };
        var rework = result with { Findings = replacement };
        replacement[0] = "changed again";
        Assert.Equal("rework", Assert.Single(rework.Findings));
        Assert.Equal("original", Assert.Single(result.Findings));
    }

    [Fact]
    public void Proposed_mutation_nested_lists_are_immutable_snapshots()
    {
        var item = PlanItemId.New();
        var dependencies = new List<PlanItemId> { item };
        var order = new List<PlanItemId> { item };
        var parts = new List<string> { "original part" };
        var target = new MutationTarget(true, 1, "text", null, dependencies, order) { SplitParts = parts };
        dependencies.Clear();
        order.Clear();
        parts[0] = "changed";
        Assert.Equal(item, Assert.Single(target.AddDependsOn));
        Assert.Equal(item, Assert.Single(target.ReorderList));
        Assert.Equal("original part", Assert.Single(target.SplitParts));
        Assert.Throws<NotSupportedException>(() => ((IList<PlanItemId>)target.AddDependsOn).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PlanItemId>)target.ReorderList).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)target.SplitParts).Clear());
        var replacement = new List<string> { "rework" };
        var rework = target with { SplitParts = replacement };
        replacement.Clear();
        Assert.Equal("rework", Assert.Single(rework.SplitParts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_event_schema_roundtrip_preserves_results_and_legacy_null_lists(bool legacyNull)
    {
        var item = PlanItemId.New();
        var result = legacyNull
            ? new AgentResult(AgentOutcome.Succeeded, "summary", null!, null!, null!, null!, ConfidenceLevel.High, null!)
            : new AgentResult(AgentOutcome.Succeeded, "summary", ["finding"], [], [], [], ConfidenceLevel.High,
                [PlanMutation.Reorder([item], default)]);
        var payload = new LaneCompleted(LaneId.New(), result);
        var codecs = EventCodecs.Create();
        var envelope = DomainEvent.Create(SessionId.New(), payload.Type(), payload.SchemaVersion(),
            null, null, null, null, null, null, null, null, [], codecs.CodecFor(payload.Type()).Encode(payload));
        var decoded = Assert.IsType<LaneCompleted>(codecs.Decode(envelope)).Result!;
        Assert.Equal(result.Outcome, decoded.Outcome);
        Assert.Equal(result.Summary, decoded.Summary);
        if (legacyNull)
        {
            Assert.Null(decoded.Findings);
            Assert.Null(decoded.ArtifactRefs);
            Assert.Null(decoded.FilesChanged);
            Assert.Null(decoded.RemainingIssues);
            Assert.Null(decoded.ProposedPlanMutations);
        }
        else
        {
            Assert.Equal("finding", Assert.Single(decoded.Findings));
            Assert.Equal(item, Assert.Single(Assert.Single(decoded.ProposedPlanMutations).Target.ReorderList));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)decoded.Findings).Clear());
        }
    }
}
