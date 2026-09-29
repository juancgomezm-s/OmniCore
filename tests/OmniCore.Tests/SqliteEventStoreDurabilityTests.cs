using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Semántica de durabilidad de commits del Event Store en SQLite (ADR-0002 §2): la clase de
/// commit Barrier alterna synchronous=FULL alrededor del commit en la conexión escritora y
/// restaura NORMAL en finally; Standard conserva NORMAL. Se prueban persistencia/replay, el
/// estado del pragma tras un Barrier, la atomicidad de AppendBatch, la restauración de la
/// configuración ante error, y que EventStream puede pedir Barrier sin cambiar llamadas.
/// </summary>
public sealed class SqliteEventStoreDurabilityTests
{
    private const string FailSentinel = "__FORCE_FAIL__";

    // ---- PRAGMA synchronous: 0=OFF, 1=NORMAL, 2=FULL ----
    private const long Normal = 1;

    private static string TempJournal()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omnicore-sqlite-durability");
        System.IO.Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
    }

    private static DomainEvent Event(SessionId sessionId, string type, string? payload = null) =>
        DomainEvent.Create(sessionId, EventType.Of(type), 1, null, null, null, null, null, null,
            null, null, new ArtifactRef[0], payload ?? "{}");

    private static long SynchronousPragma(SqliteEventStore store)
    {
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void AssertSynchronousEquals(SqliteEventStore store, long expected, string message)
    {
        Assert.True(SynchronousPragma(store) == expected, message + " (real=" + SynchronousPragma(store) + ")");
    }

    [Fact]
    public void Standard_and_Barrier_persist_and_replay()
    {
        var journal = TempJournal();
        var store = new SqliteEventStore(journal);
        var reopened = new SqliteEventStore(journal);
        try
        {
            var session = SessionId.New();

            store.Append(session, Event(session, "test.standard"), DurabilityClass.Standard, CancellationToken.None);
            store.Append(session, Event(session, "test.barrier", "{\"n\":1}"), DurabilityClass.Barrier, CancellationToken.None);

            // Replay en la misma conexión.
            var replayed = store.ReadFrom(session, 1);
            Assert.Equal(2, replayed.Count);
            Assert.Equal("test.standard", replayed[0].Type.ToString());
            Assert.Equal("test.barrier", replayed[1].Type.ToString());
            Assert.Equal(1, replayed[0].Sequence);
            Assert.Equal(2, replayed[1].Sequence);
            Assert.Contains("\"n\":1", replayed[1].PayloadJson);

            // Persistencia en disco: un segundo store sobre el mismo journal relee.
            var tail = reopened.ReadFrom(session, 1);
            Assert.Equal(2, tail.Count);
            Assert.Equal("test.barrier", tail[1].Type.ToString());

            // La conexión escritora usa WAL (ADR-0002 §1) y synchronous=NORMAL tras el Barrier.
            using (var cmd = reopened.Connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode";
                Assert.Equal("wal", (Convert.ToString(cmd.ExecuteScalar()) ?? "").Trim());
            }

            AssertSynchronousEquals(reopened, Normal, "Tras el Barrier la conexión reabierta está en NORMAL");
        }
        finally
        {
            store.Close();
            reopened.Close();
            TryDelete(journal);
        }
    }

    [Fact]
    public void Barrier_followed_by_Standard_leaves_synchronous_NORMAL()
    {
        var journal = TempJournal();
        var store = new SqliteEventStore(journal);
        try
        {
            AssertSynchronousEquals(store, Normal, "La conexión arranca en synchronous=NORMAL");

            var session = SessionId.New();
            store.Append(session, Event(session, "test.barrier"), DurabilityClass.Barrier, CancellationToken.None);
            // Antes del commit Barrier la conexión estuvo en FULL (required para el fsync); el finally la restaura.
            AssertSynchronousEquals(store, Normal, "Tras un commit Barrier la conexión vuelve a NORMAL");

            store.Append(session, Event(session, "test.standard"), DurabilityClass.Standard, CancellationToken.None);
            AssertSynchronousEquals(store, Normal, "Un Standard posterior conserva NORMAL");
        }
        finally
        {
            store.Close();
            TryDelete(journal);
        }
    }

    [Fact]
    public void AppendBatch_is_atomic_on_mid_batch_failure()
    {
        var journal = TempJournal();
        var store = new SqliteEventStore(journal);
        try
        {
            var session = SessionId.New();
            InstallFailureTrigger(store, FailSentinel);

            var first = Event(session, "test.atomic.first");
            var second = Event(session, "test.atomic.second", "{\"boom\":\"" + FailSentinel + "\"}");

            var ex = Record.Exception(() =>
                store.AppendBatch(session, new[] { first, second }, DurabilityClass.Standard, CancellationToken.None));

            Assert.NotNull(ex);
            // NINGUNA fila del lote sobrevive: la transacción se revierte completa (atómico).
            Assert.Equal(0, store.CurrentSequence(session));
            Assert.Empty(store.ReadFrom(session, 1));
        }
        finally
        {
            store.Close();
            TryDelete(journal);
        }
    }

    [Fact]
    public void Barrier_error_restores_synchronous_NORMAL()
    {
        var journal = TempJournal();
        var store = new SqliteEventStore(journal);
        try
        {
            var session = SessionId.New();
            InstallFailureTrigger(store, FailSentinel);

            // Un commit Barrier falla a mitad: la conexión NO debe quedar en FULL.
            var broken = Event(session, "test.barrier.fail", "{\"boom\":\"" + FailSentinel + "\"}");
            var ex = Record.Exception(() =>
                store.Append(session, broken, DurabilityClass.Barrier, CancellationToken.None));
            Assert.NotNull(ex);

            AssertSynchronousEquals(store, Normal, "Un Barrier que falla restaura synchronous=NORMAL");

            // La conexión sigue utilizable para commits posteriores.
            store.Append(session, Event(session, "test.after.fail"), DurabilityClass.Standard, CancellationToken.None);
            Assert.Equal(1, store.CurrentSequence(session));
            AssertSynchronousEquals(store, Normal, "La conexión sigue en NORMAL tras el commit posterior");
        }
        finally
        {
            store.Close();
            TryDelete(journal);
        }
    }

    [Fact]
    public void EventStream_can_request_Barrier_without_changing_existing_calls()
    {
        var spy = new RecordingEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var stream = new EventStream(spy, codecs, session);
        var run = RunId.New();

        // Llamada existente (sin clase): sigue siendo Standard.
        stream.Append(new RunStarted(run));
        // Nueva capacidad: Barrier explícito.
        stream.Append(new RunCreated(run, session, "objetivo", RunMode.Act, ExecutionStrategy.Direct,
            FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null), TaskId.New(),
            DateTimeOffset.UtcNow), DurabilityClass.Barrier);

        Assert.Equal(
            new[] { DurabilityClass.Standard, DurabilityClass.Barrier },
            spy.Captured.ToArray());
    }

    /// <summary>
    /// Instala un trigger SQLite que falla con RAISE(ABORT) cualquier INSERT cuyo payload lleve
    /// <paramref name="sentinel"/>. Es la forma determinista de inducir un error del driver a
    /// mitad de un commit sin depender de concurrencia ni de límites del driver.
    /// </summary>
    private static void InstallFailureTrigger(SqliteEventStore store, string sentinel)
    {
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText =
            "CREATE TRIGGER IF NOT EXISTS omnicore_fail_forced\n" +
            "BEFORE INSERT ON events\n" +
            "WHEN NEW.payload LIKE '%" + sentinel + "%'\n" +
            "BEGIN\n" +
            "    SELECT RAISE(ABORT, 'forced durability test failure');\n" +
            "END;";
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string journal)
    {
        try
        {
            if (System.IO.File.Exists(journal)) System.IO.File.Delete(journal);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Spy de IEventStore que registra la clase de durabilidad de cada Append.</summary>
    private sealed class RecordingEventStore : IEventStore
    {
        public List<DurabilityClass> Captured { get; } = new();

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            Captured.Add(durability);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
        }

        public long CurrentSequence(SessionId sessionId) => 0;

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            new DomainEvent[0];
    }
}
