namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class InvocationReservationFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_after_durable_start_before_send_does_not_poison_next_turn(bool rejectMarker)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-before-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SqliteEventStore? journal = null;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var codecs = EventCodecs.Create();
            journal = new SqliteEventStore(Path.Combine(root, "journal.db"));
            IEventStore store = new CancellingStore(rejectMarker
                ? new RejectingStore(journal, "model_step.not_dispatched") : journal, cancellation);
            var session = SessionId.New();
            var run = TestRun.Open(new EventStream(store, codecs, session), session,
                taskBudget: new TaskBudget(1m, null, null, null));
            var ledgerPath = Path.Combine(root, "reservations.db");
            var catalog = new FakeCatalog();
            var calls = 0;
            ExplorerTurn MakeTurn() => new((_, _) =>
            {
                calls++;
                return new ModelResponse([new TextBlock("offline fixture")], StopReason.EndTurn,
                    new TokenUsage(100_000, 0, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "fixture", null), TokenUsageFields.All);
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 400_000, ToolMode.Direct, null, maxOutputTokens: 1000),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), spendReservations: new SqliteSpendReservationStore(ledgerPath),
                modelContextCapacity: 400_000, maximumGenerationRequestAttempts: 1);
            var first = MakeTurn().Ask("cancel", "system", session, run.RunId, run.RootLane, "", cancellation.Token);
            Assert.Equal(rejectMarker ? StopReason.Error : StopReason.Cancelled, first.StopReason);
            Assert.Equal(0, calls);
            Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepStarted>());
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>());
            Assert.Equal(rejectMarker ? 0 : 1, store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepNotDispatched>().Count());
            journal.Close();
            journal = new SqliteEventStore(Path.Combine(root, "journal.db"));
            store = journal;
            var second = MakeTurn().Ask("continue", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(rejectMarker ? StopReason.Cancelled : StopReason.EndTurn, second.StopReason);
            Assert.Equal(rejectMarker ? 0 : 1, calls);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ledgerPath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state,pending_usd FROM spend_reservations ORDER BY created_utc LIMIT 1";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(rejectMarker ? "reserved" : "released", reader.GetString(0));
            Assert.Equal(rejectMarker ? "0.401" : "0", reader.GetString(1));
        }
        finally
        {
            journal?.Close();
            using var pool = new SqliteConnection("DataSource=" + Path.Combine(root, "journal.db"));
            SqliteConnection.ClearPool(pool);
            Directory.Delete(root, true);
        }
    }

    private sealed class CancellingStore(IEventStore inner, CancellationTokenSource cancellation) : IEventStore
    {
        private bool cancelled;
        private void After(IEnumerable<DomainEvent> events)
        {
            if (cancelled || !events.Any(evt => evt.Type.ToString() == "model_step.started")) return;
            cancelled = true;
            cancellation.Cancel();
        }
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken token)
        {
            inner.Append(session, evt, durability, token);
            After([evt]);
        }
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability, CancellationToken token)
        {
            inner.AppendBatch(session, events, durability, token);
            After(events);
        }
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => inner.ReadFrom(session, from);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_barrier_before_dispatch_releases_but_failed_usage_receipt_keeps_bound(bool failReceipt)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-invocation-reservation-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var codecs = EventCodecs.Create();
            var store = new RejectingStore(new InMemoryEventStore(), failReceipt ? "model_step.completed" : "model_step.started");
            var session = SessionId.New();
            var run = TestRun.Open(new EventStream(store, codecs, session), session,
                taskBudget: new TaskBudget(1m, null, null, null));
            var ledgerPath = Path.Combine(root, "reservations.db");
            var reservations = new SqliteSpendReservationStore(ledgerPath);
            var catalog = new FakeCatalog();
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse([new TextBlock("offline fixture")], StopReason.EndTurn,
                    new TokenUsage(100_000, 0, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "fixture", null), TokenUsageFields.All);
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 400_000, ToolMode.Direct, null, maxOutputTokens: 1000),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), spendReservations: reservations,
                modelContextCapacity: 400_000, maximumGenerationRequestAttempts: 1);
            var result = turn.Ask("fixture", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.True(store.Rejected);
            Assert.Equal(failReceipt ? 1 : 0, calls);
            var payloads = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(failReceipt ? 1 : 0, payloads.OfType<ModelStepStarted>().Count());
            Assert.Empty(payloads.OfType<ModelStepCompleted>());
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ledgerPath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT state,pending_usd,actual_usd,receipt FROM spend_reservations";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(failReceipt ? "dispatched" : "released", reader.GetString(0));
                Assert.Equal(failReceipt ? 0.401m : 0m, decimal.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(reader.IsDBNull(2));
                Assert.True(reader.IsDBNull(3));
                Assert.False(reader.Read());
            }
            var subsequent = new SqliteSpendReservationStore(ledgerPath).TryReserve("subsequent", 0.401m,
                () => [new("run", run.RunId.ToString(), 0.5m, 0m)]);
            Assert.Equal(failReceipt ? SqliteSpendReservationStore.Admission.Insufficient
                : SqliteSpendReservationStore.Admission.Reserved, subsequent);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RejectingStore(IEventStore inner, string target) : IEventStore
    {
        public bool Rejected { get; private set; }
        private void Check(IEnumerable<DomainEvent> events, DurabilityClass durability)
        {
            if (!events.Any(evt => evt.Type.ToString() == target)) return;
            Assert.Equal(DurabilityClass.Barrier, durability);
            Rejected = true;
            throw new IOException("Fixture rejected durable invocation event.");
        }
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken token)
        {
            Check([evt], durability);
            inner.Append(session, evt, durability, token);
        }
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability, CancellationToken token)
        {
            Check(events, durability);
            inner.AppendBatch(session, events, durability, token);
        }
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => inner.ReadFrom(session, from);
    }
}
