using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// EPIC-002: envelope completo (correlación, causación, ids de entidad), upcasters al leer
/// (ADR-0013), commit Barrier confirmado en FULL y escritores concurrentes sobre el mismo journal.
/// </summary>
public sealed class JournalEnvelopeTests
{
    private static string TempJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-journal-envelope");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
    }

    /// <summary>Evento sin entidad canónica (no participa en las máquinas de estado).</summary>
    private static DomainEventPayload Neutral() => new InteractionExpired(InteractionId.New());

    private static RunCreated NewRun(SessionId session, RunId run, TaskId rootTask) =>
        new(run, session, "objetivo", RunMode.Act, ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
            new TaskBudget(null, null, null, null), rootTask, DateTimeOffset.UtcNow);

    private static TaskCreated NewTask(TaskId task, RunId run) =>
        new(task, run, "tarea", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null));

    // ── Envelope ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_run_event_is_correlated_with_its_run_and_carries_entity_ids()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var call = ToolCallId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "tarea", Array.Empty<TaskDependency>(),
            new TaskBudget(null, null, null, null)));
        stream.Append(new LaneCreated(lane, task, ProfileId.New()));
        stream.Append(new ToolCallRequested(call, "pc-1", "fake.read", "{}"));

        var events = store.ReadFrom(session, 1);
        Assert.Null(events[0].CorrelationId); // antes del primer Run no hay correlación
        Assert.All(events.Skip(1), e => Assert.Equal(run, e.CorrelationId));
        Assert.All(events.Skip(1), e => Assert.Equal(run, e.RunId));
        Assert.Equal(task, events[2].TaskId);
        Assert.Equal(lane, events[3].LaneId);
        Assert.Equal(task, events[3].TaskId);
        Assert.Equal(call, events[4].ToolCallId);
    }

    [Fact]
    public void A_new_stream_on_an_existing_session_recovers_the_current_run()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = RunId.New();
        var task = TaskId.New();
        var first = new EventStream(store, EventCodecs.Create(), session);
        first.Append(NewRun(session, run, task));
        first.Append(NewTask(task, run));

        var second = new EventStream(store, EventCodecs.Create(), session);
        second.Append(new TaskReady(task)); // valida contra lo que escribió el primero

        Assert.Equal(run, store.ReadFrom(session, 1)[^1].CorrelationId);
    }

    [Fact]
    public void Causation_is_the_command_in_scope_or_else_the_previous_event()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var command = CommandId.New();

        stream.Append(Neutral());
        using (CausationScope.Begin(new CommandCausation(command)))
        {
            stream.Append(Neutral());
            stream.Append(Neutral());
        }

        stream.Append(Neutral());

        var events = store.ReadFrom(session, 1);
        Assert.Null(events[0].Causation); // raíz: sin comando ni evento previo
        Assert.Equal(new CommandCausation(command), events[1].Causation);
        Assert.Equal(new CommandCausation(command), events[2].Causation);
        Assert.Equal(new EventCausation(events[2].EventId), events[3].Causation);
        Assert.Null(CausationScope.Current); // el scope se restaura al salir
    }

    [Fact]
    public void Envelope_fields_survive_a_sqlite_round_trip()
    {
        var path = TempJournal();
        var session = SessionId.New();
        var run = RunId.New();
        var task = TaskId.New();
        var command = CommandId.New();
        var store = new SqliteEventStore(path);
        var stream = new EventStream(store, EventCodecs.Create(), session);
        using (CausationScope.Begin(new CommandCausation(command)))
        {
            stream.Append(NewRun(session, run, task));
            stream.Append(NewTask(task, run));
        }

        var written = store.ReadFrom(session, 1);
        store.Close();
        var reopened = new SqliteEventStore(path);
        var read = reopened.ReadFrom(session, 1);
        reopened.Close();

        Assert.Equal(2, read.Count);
        Assert.Equal(new CommandCausation(command), read[1].Causation);
        Assert.Equal(run, read[1].CorrelationId);
        Assert.Equal(task, read[1].TaskId);
        Assert.Null(read[1].LaneId);
        Assert.Equal(written[1].Timestamp, read[1].Timestamp);
    }

    [Fact]
    public void Timestamps_do_not_depend_on_the_current_culture()
    {
        var path = TempJournal();
        var session = SessionId.New();
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-MX");
            var store = new SqliteEventStore(path);
            new EventStream(store, EventCodecs.Create(), session).Append(Neutral());
            var written = store.ReadFrom(session, 1)[0].Timestamp;
            store.Close();

            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            var reopened = new SqliteEventStore(path);
            Assert.Equal(written, reopened.ReadFrom(session, 1)[0].Timestamp);
            reopened.Close();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── Barrier y concurrencia ─────────────────────────────────────────────────────────

    [Fact]
    public void Barrier_commits_with_synchronous_full_and_standard_with_normal()
    {
        var store = new SqliteEventStore(TempJournal());
        var stream = new EventStream(store, EventCodecs.Create(), SessionId.New());

        stream.Append(Neutral(), DurabilityClass.Barrier);
        Assert.Equal(2, store.LastCommitSynchronousLevel); // FULL

        stream.Append(Neutral(), DurabilityClass.Standard);
        Assert.Equal(1, store.LastCommitSynchronousLevel); // NORMAL
        store.Close();
    }

    [Fact]
    public async Task Two_writers_on_the_same_journal_never_reuse_a_sequence()
    {
        var path = TempJournal();
        var session = SessionId.New();
        new SqliteEventStore(path).Close(); // crea el esquema antes de competir
        const int perWriter = 40;

        void Write()
        {
            var store = new SqliteEventStore(path);
            var stream = new EventStream(store, EventCodecs.Create(), session);
            for (var i = 0; i < perWriter; i++)
            {
                stream.Append(Neutral());
            }

            store.Close();
        }

        await Task.WhenAll(Task.Run(Write, TestContext.Current.CancellationToken),
            Task.Run(Write, TestContext.Current.CancellationToken));

        var reader = new SqliteEventStore(path);
        var sequences = reader.ReadFrom(session, 1).Select(e => e.Sequence).ToArray();
        reader.Close();
        Assert.Equal(Enumerable.Range(1, 2 * perWriter).Select(i => (long) i), sequences);
    }

    // ── Transiciones canónicas (EPIC-003) ──────────────────────────────────────────────

    [Fact]
    public void An_invalid_transition_is_rejected_and_nothing_is_written()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = RunId.New();
        stream.Append(NewRun(session, run, TaskId.New()));

        // Created → Completed sin pasar por Running/Validating.
        var ex = Assert.Throws<InvalidStateTransitionException>(() =>
            stream.Append(new RunCompleted(run, RunOutcome.Completed)));

        Assert.Equal("run", ex.Entity);
        Assert.Single(store.ReadFrom(session, 1));
    }

    [Fact]
    public void An_event_about_an_entity_that_does_not_exist_is_rejected()
    {
        var store = new InMemoryEventStore();
        var stream = new EventStream(store, EventCodecs.Create(), SessionId.New());

        Assert.Throws<InvalidStateTransitionException>(() => stream.Append(new TaskReady(TaskId.New())));
        Assert.Throws<InvalidStateTransitionException>(() =>
            stream.Append(new ToolCallSucceeded(ToolCallId.New(), "{}")));
    }

    [Fact]
    public void A_batch_with_one_invalid_event_writes_nothing()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = RunId.New();
        stream.Append(NewRun(session, run, TaskId.New()));

        Assert.Throws<InvalidStateTransitionException>(() => stream.AppendBatch(new DomainEventPayload[] {
            new RunStarted(run),
            new RunStarted(run), // Running → Running no existe
        }, DurabilityClass.Standard));

        Assert.Single(store.ReadFrom(session, 1));
    }

    [Fact]
    public void Creating_the_same_entity_twice_is_rejected()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = RunId.New();
        stream.Append(NewRun(session, run, TaskId.New()));

        Assert.Throws<InvalidStateTransitionException>(() => stream.Append(NewRun(session, run, TaskId.New())));
    }

    [Fact]
    public void Completed_with_issues_outcome_projects_to_its_own_state()
    {
        var tracker = new CanonicalStateTracker();
        var session = SessionId.New();
        var run = RunId.New();
        tracker.Apply(NewRun(session, run, TaskId.New()));
        tracker.Apply(new RunStarted(run));
        tracker.Apply(new RunValidationStarted(run));
        tracker.Apply(new RunCompleted(run, RunOutcome.CompletedWithIssues));

        Assert.Equal(RunState.CompletedWithIssues, tracker.Run(run));
    }

    [Fact]
    public void User_input_while_running_is_a_message_not_an_invalid_transition()
    {
        var tracker = new CanonicalStateTracker();
        var session = SessionId.New();
        var run = RunId.New();
        tracker.Apply(NewRun(session, run, TaskId.New()));
        tracker.Apply(new RunStarted(run));
        tracker.Apply(new UserInputReceived(run, "[]", null)); // ADR-0035 §1

        Assert.Equal(RunState.Running, tracker.Run(run));
    }

    // ── Upcasters ──────────────────────────────────────────────────────────────────────

    private sealed record Probe(string Name) : DomainEventPayload
    {
        public EventType Type() => EventType.Of("test.probe");

        public int SchemaVersion() => 3;
    }

    /// <summary>Codec mínimo de prueba: el payload v3 es {"Name":"..."} y se lee a mano.</summary>
    private sealed class ProbeCodec : IDomainEventCodec
    {
        public DomainEventPayload Decode(EventType type, string payloadJson)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            return new Probe(doc.RootElement.GetProperty("Name").GetString()!);
        }

        public string Encode(DomainEventPayload payload) =>
            "{\"Name\":\"" + ((Probe) payload).Name + "\"}";
    }

    /// <summary>v1 {"Nombre":..} → v2 {"FullName":..} → v3 {"Name":..}.</summary>
    private sealed class RenameUpcaster(int from, string oldField, string newField) : IEventUpcaster
    {
        public EventType Type => EventType.Of("test.probe");

        public int FromVersion => from;

        public string Upcast(string payloadJson) => payloadJson.Replace("\"" + oldField + "\"", "\"" + newField + "\"");
    }

    private static DomainEvent Stored(int version, string json) =>
        DomainEvent.Stored(EventId.New(), SessionId.New(), 1, EventType.Of("test.probe"), version,
            DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, Array.Empty<ArtifactRef>(), json);

    private static EventCodecs ProbeRegistry() =>
        EventCodecs.Create().With(new Typed.CodecPair(EventType.Of("test.probe"), new ProbeCodec(), 3));

    [Fact]
    public void Old_events_are_upcast_step_by_step_when_read()
    {
        var codecs = ProbeRegistry()
            .WithUpcaster(new RenameUpcaster(1, "Nombre", "FullName"))
            .WithUpcaster(new RenameUpcaster(2, "FullName", "Name"));

        Assert.Equal(new Probe("ana"), codecs.Decode(Stored(1, "{\"Nombre\":\"ana\"}")));
        Assert.Equal(new Probe("luis"), codecs.Decode(Stored(2, "{\"FullName\":\"luis\"}")));
        Assert.Equal(new Probe("eva"), codecs.Decode(Stored(3, "{\"Name\":\"eva\"}")));
    }

    [Fact]
    public void A_missing_upcaster_fails_loudly_instead_of_guessing()
    {
        var codecs = ProbeRegistry().WithUpcaster(new RenameUpcaster(1, "Nombre", "FullName"));

        var ex = Assert.Throws<EventParseException>(() => codecs.Decode(Stored(1, "{\"Nombre\":\"ana\"}")));
        Assert.Contains("v2", ex.Detail);
    }

    [Fact]
    public void An_event_from_a_newer_version_is_rejected()
    {
        var ex = Assert.Throws<UnsupportedEventVersionException>(() =>
            ProbeRegistry().Decode(Stored(4, "{\"Name\":\"x\"}")));
        Assert.Equal(4, ex.StoredVersion);
        Assert.Equal(3, ex.CurrentVersion);
    }

    [Fact]
    public void A_v1_toolcall_started_still_decodes_through_the_trivial_upcaster()
    {
        var call = ToolCallId.New();
        var v1 = DomainEvent.Stored(EventId.New(), SessionId.New(), 1, EventType.Of("toolcall.started"), 1,
            DateTimeOffset.UtcNow, null, null, null, null, null, null, null, call, Array.Empty<ArtifactRef>(),
            "{\"ToolCallId\":{\"Value\":\"" + call.Value + "\"},\"EffectClass\":0}");

        var decoded = Assert.IsType<ToolCallStarted>(EventCodecs.Create().Decode(v1));
        Assert.Equal(call, decoded.ToolCallId);
        Assert.Null(decoded.ReconciliationJson);
    }

    [Fact]
    public void Writing_a_payload_whose_version_differs_from_its_codec_is_a_bug_caught_at_write()
    {
        var codecs = EventCodecs.Create().With(new Typed.CodecPair(EventType.Of("test.probe"), new ProbeCodec(), 2));
        var stream = new EventStream(new InMemoryEventStore(), codecs, SessionId.New());

        Assert.Throws<InvalidOperationException>(() => stream.Append(new Probe("x")));
    }
}
