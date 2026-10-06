namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

/// <summary>Offline interaction fixtures; production server commands and real SQLite reopen.</summary>
public sealed class UserDailyBudgetContinuationTests
{
    [Fact]
    public void Request_and_resolution_in_different_journals_do_not_form_a_user_grant()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-user-split-consent-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "workspaces", "a");
        var b = Path.Combine(root, "workspaces", "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var journalA = Path.Combine(a, "journal.db");
        var journalB = Path.Combine(b, "journal.db");
        var storeA = new SqliteEventStore(journalA);
        var storeB = new SqliteEventStore(journalB);
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New(); // Deliberately copied identity, different journal.
            var runA = TestRun.Open(storeA, session);
            var runB = TestRun.Open(storeB, session);
            var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var request = Request(day, 0m);
            new EventStream(storeA, codecs, runA.SessionId).Append(request);
            var response = new InteractionResolved(request.InteractionId, "allow_plus", InteractionCause.User);
            // Malformed historical fixture bypasses append validation intentionally: another
            // workspace has only the resolution, never its matching request.
            var envelope = DomainEvent.Stored(EventId.New(), session, storeB.CurrentSequence(session) + 1,
                response.Type(), response.SchemaVersion(), DateTimeOffset.UtcNow, null, runB.RunId,
                runB.RunId, runB.RootTask, runB.RootLane, null, null, null, Array.Empty<ArtifactRef>(),
                codecs.CodecFor(response.Type()).Encode(response), source: "split-consent-fixture");
            storeB.AppendBatch(session, new[] { envelope }, DurabilityClass.Barrier, CancellationToken.None);
            storeA.Close();
            storeB.Close();
            var reader = new UserWorkspaceSpendReader(root, Path.Combine(root, "workspaces", "current"));
            Assert.Equal(0m, UserDailyBudgetContinuation.Limit(reader, codecs, day, 0m));
        }
        finally
        {
            storeA.Close();
            storeB.Close();
            using var connectionA = new SqliteConnection("DataSource=" + journalA);
            using var connectionB = new SqliteConnection("DataSource=" + journalB);
            SqliteConnection.ClearPool(connectionA);
            SqliteConnection.ClearPool(connectionB);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Second_workspace_validates_current_user_limit_and_rejects_stale_offer(bool stale)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-user-daily-consent-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "workspaces", "a");
        var b = Path.Combine(root, "workspaces", "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var journalA = Path.Combine(a, "journal.db");
        var journalB = Path.Combine(b, "journal.db");
        var storeA = new SqliteEventStore(journalA);
        SqliteEventStore storeB = new(journalB);
        var codecs = EventCodecs.Create();
        try
        {
            var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var runA = TestRun.Open(storeA, SessionId.New());
            var first = Request(day, 0m);
            new EventStream(storeA, codecs, runA.SessionId).Append(first);
            new RunControlService(storeA, codecs).Respond(runA.SessionId, first.InteractionId, "allow_plus");
            storeA.Close();
            var runB = TestRun.Open(storeB, SessionId.New());
            var next = Request(day, stale ? 0m : 10m);
            new EventStream(storeB, codecs, runB.SessionId).Append(next);
            var before = storeB.CurrentSequence(runB.SessionId);
            var state = Path.Combine(b, "lastsession.txt");
            File.WriteAllText(state, runB.SessionId + "\n" + runB.RunId);
            var server = new OmniServer(storeB, codecs, new InMemoryAuditSink(), state,
                new FileArtifactStore(b), new UserWorkspaceSpendReader(root, b));
            var ack = server.RespondToInteraction(next.InteractionId, "allow_plus");
            Assert.Equal(stale ? "error" : "ok", ack.Status);
            if (stale)
            {
                Assert.Equal(before, storeB.CurrentSequence(runB.SessionId));
                Assert.DoesNotContain(storeB.ReadFrom(runB.SessionId, 1).Select(codecs.Decode),
                    payload => payload is InteractionResolved resolved && resolved.InteractionId == next.InteractionId);
            }
            else
            {
                Assert.Equal(before + 1, ack.FirstSeq);
                Assert.Equal(before + 1, ack.LastSeq);
                Assert.Equal("error", server.RespondToInteraction(next.InteractionId, "allow_plus").Status);
            }
            storeB.Close();
            storeB = new SqliteEventStore(journalB);
            var reader = new UserWorkspaceSpendReader(root, Path.Combine(root, "workspaces", "c"));
            Assert.Equal(stale ? 10m : 20m, UserDailyBudgetContinuation.Limit(reader, codecs, day, 0m));
            Assert.Equal(stale ? 10m : 20m, UserDailyBudgetContinuation.Limit(reader, codecs, day, 0m));
            Assert.Equal(0m, UserDailyBudgetContinuation.Limit(reader, codecs,
                DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), 0m));
            Assert.Equal(1m, UserDailyBudgetContinuation.Limit(reader, codecs, day, 1m));
        }
        finally
        {
            storeA.Close();
            storeB.Close();
            using var connectionA = new SqliteConnection("DataSource=" + journalA);
            using var connectionB = new SqliteConnection("DataSource=" + journalB);
            SqliteConnection.ClearPool(connectionA);
            SqliteConnection.ClearPool(connectionB);
            Directory.Delete(root, true);
        }
    }

    private static InteractionRequested Request(string day, decimal current) => new(InteractionId.New(),
        InteractionKind.BudgetExceeded, BudgetContinuation.Context("fixture daily cap",
            new BudgetContinuationOffer("daily", 0m, current, null, day)),
        "[{\"id\":\"deny\"},{\"id\":\"allow_plus\",\"value\":10}]", "deny", null, null, null, null, 0, 1);
}
