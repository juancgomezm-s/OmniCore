using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class DurablePostEditValidationTests
{
    [Theory]
    [InlineData("no-effect", false)]
    [InlineData("not-applied", false)]
    [InlineData("partial", true)]
    [InlineData("unknown", true)]
    [InlineData("applied", true)]
    [InlineData("conflict", true)]
    public void Replay_releases_only_known_no_effect_outcomes_and_ignores_other_runs(
        string outcome, bool remainsPending)
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var editId = ToolCallId.New();
        DomainEvent Envelope(DomainEventPayload payload, RunId owner) => DomainEvent.Create(session,
            payload.Type(), payload.SchemaVersion(), null, owner, owner, null, null, null, null, null,
            Array.Empty<ArtifactRef>(), codecs.CodecFor(payload.Type()).Encode(payload));
        DomainEventPayload result = outcome switch
        {
            "no-effect" => new ToolCallFailed(editId, "failure", EffectOutcome.None),
            "partial" => new ToolCallFailed(editId, "partial", EffectOutcome.Partial),
            "unknown" => new ToolCallEffectUnknown(editId, EffectClass.Reconcilable),
            "not-applied" => new ToolCallReconciled(editId, ReconciliationOutcome.NotApplied, "not applied"),
            "applied" => new ToolCallReconciled(editId, ReconciliationOutcome.Applied, "applied"),
            _ => new ToolCallReconciled(editId, ReconciliationOutcome.Conflict, "conflict")
        };
        var events = new[] {
            Envelope(new PostEditValidationPending(run, editId, new[] { "file.cs" }), run),
            Envelope(new PostEditValidationConsumed(RunId.New(), new[] { editId }, "build"), RunId.New()),
            Envelope(result, run)
        };
        var pending = PostEditValidationProjection.Pending(run, codecs, events);
        if (remainsPending) Assert.Equal(editId, Assert.Single(pending));
        else Assert.Empty(pending);
    }

    [Theory]
    [InlineData("none", false, false)]
    [InlineData("build", false, false)]
    [InlineData("lint", true, false)]
    [InlineData("build", true, false)]
    [InlineData("test", true, false)]
    [InlineData("build", true, true)]
    public void Reopened_store_consumes_only_pre_gate_ids_on_successful_build_or_test(
        string gate, bool passed, bool laterEdit)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-durable-debt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var first = ToolCallId.New();
            var later = ToolCallId.New();
            // Synthetic host reservation; actual file-effect ordering is covered by the
            // RecreatedBoundaryValidationRegressionTests real filesystem pipeline.
            new EventStream(store, codecs, session).Append(
                new PostEditValidationPending(run.RunId, first, new[] { "first.cs" }), DurabilityClass.Barrier);
            store.Close();
            store = new SqliteEventStore(journal);
            Assert.Equal(first, Assert.Single(PostEditValidationProjection.Pending(run.RunId, codecs,
                store.ReadFrom(session, 1))));
            Assert.Empty(PostEditValidationProjection.Pending(RunId.New(), codecs, store.ReadFrom(session, 1)));
            var events = store.ReadFrom(session, 1);
            var stream = new EventStream(store, codecs, session);
            var coupon = new RunCoupon(RunProjection.Replay(session, run.RunId, codecs, events),
                TaskGraphProjection.Replay(codecs, events), PlanProjection.Replay(codecs, events));
            var completed = coupon.CheckCompletionAndGate(new PlanService(), new ProgressReconciler(),
                store, codecs, session, stream, runExternalGates: () =>
                {
                    Assert.IsType<RunValidationStarted>(codecs.Decode(store.ReadFrom(session, 1).Last()));
                    if (laterEdit)
                        stream.Append(new PostEditValidationPending(run.RunId, later, new[] { "later.cs" }),
                            DurabilityClass.Barrier);
                    return gate == "none" ? Array.Empty<ExternalCompletionGateResult>()
                        : new[] { new ExternalCompletionGateResult(gate, passed, "injected gate result") };
                }); // Deliberately no in-memory ledger: journal is authoritative.
            var consumed = passed && gate is "build" or "test";
            Assert.Equal(consumed && !laterEdit, completed);
            store.Close();
            store = new SqliteEventStore(journal);
            var pending = PostEditValidationProjection.Pending(run.RunId, codecs, store.ReadFrom(session, 1));
            if (!consumed) Assert.Equal(first, Assert.Single(pending));
            else if (laterEdit) Assert.Equal(later, Assert.Single(pending));
            else Assert.Empty(pending);
            var consumption = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<PostEditValidationConsumed>().ToArray();
            if (consumed)
            {
                var evidence = Assert.Single(consumption);
                Assert.Equal(gate, evidence.Gate);
                Assert.Equal(first, Assert.Single(evidence.EditIds));
            }
            else Assert.Empty(consumption);
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
