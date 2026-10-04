using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class PendingValidationGateConsumptionTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("failed-test")]
    [InlineData("passed-lint")]
    [InlineData("mixed-build-test")]
    public void Gate_consumption_is_distinct_from_overall_completion(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-pending-gate-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var ledger = new FileReadRegistry().Ledger;
            ledger.Bind(new FileMutationPolicy(FileMutationMode.PatchExisting,
                DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny,
                3, 200, 0.5, true, true, true, false));
            ledger.BeginTurn();
            ledger.RecordMutation("fixture.cs", 1, 2, ToolCallId.New());
            Assert.Single(ledger.PendingValidations());
            ExternalCompletionGateResult[] results = scenario switch
            {
                "none" => Array.Empty<ExternalCompletionGateResult>(),
                "failed-test" => new[] { new ExternalCompletionGateResult("test", false, "synthetic failure") },
                "passed-lint" => new[] { new ExternalCompletionGateResult("lint", true, "synthetic pass") },
                "mixed-build-test" => new[] { new ExternalCompletionGateResult("build", true, "synthetic pass"),
                    new ExternalCompletionGateResult("test", false, "synthetic failure") },
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
            var events = store.ReadFrom(session, 1);
            var projection = RunProjection.Replay(session, run.RunId, codecs, events);
            var called = false;
            var completed = new RunCoupon(projection, TaskGraphProjection.Replay(codecs, events),
                PlanProjection.Replay(codecs, events)).CheckCompletionAndGate(new PlanService(),
                new ProgressReconciler(), store, codecs, session, new EventStream(store, codecs, session),
                runExternalGates: () =>
                {
                    called = true;
                    // Invoked after validation starts; this is not proof of a real host process.
                    var started = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<RunValidationStarted>());
                    Assert.Equal(run.RunId, started.RunId);
                    return results;
                }, mutationLedger: ledger);
            Assert.True(called);
            Assert.False(completed);
            var payloads = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var rejected = Assert.Single(payloads.OfType<RunValidationRejected>());
            Assert.DoesNotContain(payloads, evt => evt is RunCompleted);
            if (scenario == "mixed-build-test")
            {
                Assert.Empty(ledger.PendingValidations());
                Assert.Contains("test", rejected.Gates);
                Assert.DoesNotContain("post-edit-validation", rejected.Gates);
            }
            else
            {
                Assert.Single(ledger.PendingValidations());
                Assert.Contains("post-edit-validation", rejected.Gates);
                if (scenario == "failed-test") Assert.Contains("test", rejected.Gates);
            }
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
