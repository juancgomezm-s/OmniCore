using System;
using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Xunit;

namespace OmniCore.Tests;

/// <summary>
/// M5.5 Phase A — Bug 1: UTC envelope timestamps (ADR-0001 §3).
/// Verifica que los timestamps del envelope sean UTC y se conserven en SQLite sin reescritura de journal.
/// </summary>
public sealed class UtcEnvelopeTests
{
    private static string TempJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-utc-envelope");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
    }

    private static DomainEventPayload Neutral() => new InteractionExpired(InteractionId.New());

    // ── DomainEvent.Create produce UTC ────────────────────────────────────────────────

    [Fact]
    public void DomainEvent_Create_produces_utc_timestamp_with_zero_offset()
    {
        var before = DateTimeOffset.UtcNow;
        var evt = DomainEvent.Create(
            SessionId.New(),
            EventType.Of("test.event"),
            1,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<ArtifactRef>(),
            "{}");
        var after = DateTimeOffset.UtcNow;

        // Offset cero (UTC)
        Assert.Equal(TimeSpan.Zero, evt.Timestamp.Offset);

        // Instante correcto: entre before y after (con tolerancia de reloj)
        Assert.True(evt.Timestamp >= before - TimeSpan.FromSeconds(1));
        Assert.True(evt.Timestamp <= after + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DomainEvent_Create_multiple_events_have_monotonic_timestamps()
    {
        var events = new DomainEvent[5];
        for (var i = 0; i < events.Length; i++)
        {
            events[i] = DomainEvent.Create(
                SessionId.New(),
                EventType.Of("test.event"),
                1,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                Array.Empty<ArtifactRef>(),
                "{}");
            // Pequeña pausa para evitar colisiones de reloj en CI rápido
            Thread.Sleep(1);
        }

        for (var i = 1; i < events.Length; i++)
        {
            Assert.True(events[i].Timestamp >= events[i - 1].Timestamp);
        }

        Assert.All(events, e => Assert.Equal(TimeSpan.Zero, e.Timestamp.Offset));
    }

    // ── DomainEvent.Stored preserva offset e instante (replay histórico) ──────────────

    [Fact]
    public void DomainEvent_Stored_preserves_supplied_nonzero_offset_and_instant()
    {
        // Timestamp con offset no-cero (p. ej. -05:00)
        var original = new DateTimeOffset(2024, 6, 15, 12, 30, 45, TimeSpan.FromHours(-5));

        var evt = DomainEvent.Stored(
            EventId.New(),
            SessionId.New(),
            42,
            EventType.Of("historical.event"),
            2,
            original,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<ArtifactRef>(),
            "{}");

        Assert.Equal(original, evt.Timestamp);
        Assert.Equal(TimeSpan.FromHours(-5), evt.Timestamp.Offset);
    }

    [Fact]
    public void DomainEvent_Stored_preserves_utc_timestamp_exactly()
    {
        var original = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var evt = DomainEvent.Stored(
            EventId.New(),
            SessionId.New(),
            1,
            EventType.Of("historical.event"),
            1,
            original,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<ArtifactRef>(),
            "{}");

        Assert.Equal(original, evt.Timestamp);
        Assert.Equal(TimeSpan.Zero, evt.Timestamp.Offset);
    }

    // ── SQLite round-trip conserva UTC y campos del envelope ──────────────────────────

    [Fact]
    public void Sqlite_round_trip_preserves_utc_timestamp_and_envelope_fields()
    {
        var path = TempJournal();
        var session = SessionId.New();
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var call = ToolCallId.New();
        var command = CommandId.New();

        var store = new SqliteEventStore(path);
        var stream = new EventStream(store, EventCodecs.Create(), session);

        using (CausationScope.Begin(new CommandCausation(command)))
        {
            stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
            stream.Append(new RunCreated(run, session, "objetivo", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null), task, DateTimeOffset.UtcNow));
            stream.Append(new TaskCreated(task, run, "tarea", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null)));
            stream.Append(new LaneCreated(lane, task, ProfileId.New()));
            stream.Append(new ToolCallRequested(call, "pc-1", "fake.read", "{}"));
        }

        var written = store.ReadFrom(session, 1);
        store.Close();

        // Reabrir y releer
        var reopened = new SqliteEventStore(path);
        var read = reopened.ReadFrom(session, 1);
        reopened.Close();

        Assert.Equal(written.Count, read.Count);

        for (var i = 0; i < written.Count; i++)
        {
            var w = written[i];
            var r = read[i];

            // IDs y secuencia
            Assert.Equal(w.EventId, r.EventId);
            Assert.Equal(w.Sequence, r.Sequence);
            Assert.Equal(w.Type, r.Type);
            Assert.Equal(w.SchemaVersion, r.SchemaVersion);

            // Timestamp: UTC con offset cero, mismo instante
            Assert.Equal(w.Timestamp, r.Timestamp);
            Assert.Equal(TimeSpan.Zero, r.Timestamp.Offset);

            // Envelope completo
            Assert.Equal(w.Causation, r.Causation);
            Assert.Equal(w.CorrelationId, r.CorrelationId);
            Assert.Equal(w.RunId, r.RunId);
            Assert.Equal(w.TaskId, r.TaskId);
            Assert.Equal(w.LaneId, r.LaneId);
            Assert.Equal(w.TurnId, r.TurnId);
            Assert.Equal(w.PlanItemId, r.PlanItemId);
            Assert.Equal(w.ToolCallId, r.ToolCallId);
            Assert.Equal(w.PayloadJson, r.PayloadJson);
        }
    }

    [Fact]
    public void Sqlite_round_trip_preserves_historical_nonzero_offset_without_migration()
    {
        // Simula un journal antiguo escrito con offset no-cero (antes de la corrección)
        // Insertando directamente a través de DomainEvent.Stored vía InMemoryEventStore
        // y luego verificando que SqliteEventStore lo lee tal cual.

        var path = TempJournal();
        var session = SessionId.New();

        // Crear store y escribir evento con offset histórico (-03:00)
        var store = new SqliteEventStore(path);
        var historical = new DateTimeOffset(2023, 12, 25, 10, 0, 0, TimeSpan.FromHours(-3));

        var evt = DomainEvent.Stored(
            EventId.New(),
            session,
            1,
            EventType.Of("legacy.event"),
            1,
            historical,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<ArtifactRef>(),
            "{}");

        store.AppendBatch(session, [evt], DurabilityClass.Standard, CancellationToken.None);
        store.Close();

        // Reabrir y leer: el offset histórico debe conservarse SIN conversión a UTC
        var reopened = new SqliteEventStore(path);
        var read = reopened.ReadFrom(session, 1);
        reopened.Close();

        Assert.Single(read);
        Assert.Equal(historical, read[0].Timestamp);
        Assert.Equal(TimeSpan.FromHours(-3), read[0].Timestamp.Offset);
    }

    [Fact]
    public void Sqlite_timestamps_are_culture_invariant_on_write_and_read()
    {
        var path = TempJournal();
        var session = SessionId.New();
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            // Escribir con cultura es-MX
            CultureInfo.CurrentCulture = new CultureInfo("es-MX");
            var store = new SqliteEventStore(path);
            new EventStream(store, EventCodecs.Create(), session).Append(Neutral());
            var written = store.ReadFrom(session, 1)[0].Timestamp;
            store.Close();

            // Leer con cultura en-US
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            var reopened = new SqliteEventStore(path);
            var read = reopened.ReadFrom(session, 1)[0].Timestamp;
            reopened.Close();

            // El instante y offset deben ser idénticos
            Assert.Equal(written, read);
            Assert.Equal(TimeSpan.Zero, read.Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    // ── EventStream.Append vía BuildEnvelope usa DomainEvent.Create → UTC ──────────────

    [Fact]
    public void EventStream_Append_produces_utc_timestamps_via_DomainEvent_Create()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);

        stream.Append(Neutral());
        stream.Append(Neutral());

        var events = store.ReadFrom(session, 1);
        Assert.Equal(2, events.Count);

        Assert.All(events, e =>
        {
            Assert.Equal(TimeSpan.Zero, e.Timestamp.Offset);
            Assert.True(e.Timestamp <= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));
        });
    }

    // ── Orden y secuencia se conservan tras cerrar/reabrir ────────────────────────────

    [Fact]
    public void Sequence_and_ordering_preserved_after_close_reopen()
    {
        var path = TempJournal();
        var session = SessionId.New();

        // Primera escritura: 3 eventos
        var store1 = new SqliteEventStore(path);
        var stream1 = new EventStream(store1, EventCodecs.Create(), session);
        stream1.Append(Neutral());
        stream1.Append(Neutral());
        stream1.Append(Neutral());
        var firstRead = store1.ReadFrom(session, 1);
        store1.Close();

        // Segunda escritura: 2 eventos más (distinto proceso simulado)
        var store2 = new SqliteEventStore(path);
        var stream2 = new EventStream(store2, EventCodecs.Create(), session);
        stream2.Append(Neutral());
        stream2.Append(Neutral());
        store2.Close();

        // Lectura final: 5 eventos en orden, secuencia 1..5
        var reader = new SqliteEventStore(path);
        var all = reader.ReadFrom(session, 1);
        reader.Close();

        Assert.Equal(5, all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            Assert.Equal(i + 1, all[i].Sequence);
            Assert.Equal(TimeSpan.Zero, all[i].Timestamp.Offset);
        }
    }
}