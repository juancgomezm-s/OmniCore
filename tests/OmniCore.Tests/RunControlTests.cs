using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>
/// EPIC-004: control del Run por comandos (ADR-0035 §1, §4, §6; ADR-0036; ADR-0034).
///
/// Cubre <see cref="RunControlService"/> (<c>SendInput</c>, <c>StartRun</c>, <c>Interrupt</c>,
/// <c>CancelRun</c>, <c>Respond</c> y el efecto del <c>PlanApproval</c>) y los comandos de cable de
/// <c>OmniServer.RunControl</c> (<c>session.input</c>, <c>run.interrupt</c>, <c>run.cancel</c>,
/// <c>interaction.respond</c>). Todos los escenarios son deterministas: no hay red, sleeps ni
/// procesos externos, y cada uno termina comprobando que el journal completo replaya contra las
/// máquinas de estado canónicas sin transiciones inválidas (ADR-0036).
/// </summary>
public sealed class RunControlTests
{
    private static readonly EventCodecs Codecs = EventCodecs.Create();

    private const string PlanApprovalOptions =
        "[{\"id\":\"approve_execute\"},{\"id\":\"approve_only\"},{\"id\":\"reject\"}]";

    // ── Helpers de cable y de journal ─────────────────────────────────────────────────────────

    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";

    private static string SessionInput(string text, string? mode = null)
    {
        var fields = new List<string> { JsonObj.Field("cmd", "session.input"), JsonObj.Field("text", text) };
        if (mode is not null)
        {
            fields.Add(JsonObj.Field("mode", mode));
        }

        return Payload(fields.ToArray());
    }

    private static CommandAck Send(OmniServer server, string payload) =>
        server.Send(WireEnvelope.Command(Ids.NewV7(), payload), TestContext.Current.CancellationToken);

    private static RunControlService Control(IEventStore store) => new(store, Codecs);

    private static IReadOnlyList<DomainEvent> Journal(OmniServer server) =>
        server.AcquireStore().ReadFrom(server.LastSessionId()!, 1);

    private static IReadOnlyList<DomainEvent> Journal(IEventStore store, SessionId session) =>
        store.ReadFrom(session, 1);

    private static int Count(IReadOnlyList<DomainEvent> journal, string type) =>
        journal.Count(e => e.Type.ToString() == type);

    private static void AssertJournalIsValid(IEventStore store, SessionId session)
    {
        // ADR-0036: replayar el journal completo contra las máquinas de estado no puede lanzar.
        CanonicalStateTracker.Replay(Codecs, Journal(store, session));
    }

    private static void AssertJournalIsValid(OmniServer server)
    {
        if (server.LastSessionId() is { } session)
        {
            AssertJournalIsValid(server.AcquireStore(), session);
        }
    }

    private static LaneId RootLaneOf(IReadOnlyList<DomainEvent> journal, SessionId session, RunId run)
    {
        var rootTask = RunProjection.Replay(session, run, Codecs, journal).RootTask!;
        return Assert.Single(LaneProjection.Replay(Codecs, journal).ForTask(rootTask)).Id;
    }

    private static LaneId RootLaneOf(OmniServer server, RunId run) =>
        RootLaneOf(Journal(server), server.LastSessionId()!, run);

    /// <summary>Escribe la cadena válida de una ToolCall hasta el estado pedido (ADR-0036 §5).</summary>
    private static void ToolCallChain(EventStream stream, ToolCallId call, ToolCallState target)
    {
        var batch = new List<DomainEventPayload> { new ToolCallRequested(call, "pc", "fake.read", "{}") };
        if (target != ToolCallState.Requested)
        {
            batch.Add(new ToolCallPrepared(call, "{}"));
            if (target != ToolCallState.Prepared)
            {
                batch.Add(new PermissionEvaluated(call, PermissionDecision.Allow, "[]", null));
                batch.Add(new ToolCallAuthorized(call));
                if (target is ToolCallState.Started or ToolCallState.Succeeded)
                {
                    batch.Add(new ToolCallStarted(call, EffectClass.None, null));
                    if (target == ToolCallState.Succeeded)
                    {
                        batch.Add(new ToolCallSucceeded(call, "{}"));
                    }
                }
            }
        }

        stream.AppendBatch(batch, DurabilityClass.Standard);
    }

    private static InteractionId RequestInteraction(EventStream stream, LaneId lane, InteractionKind kind,
        string options, string defaultOption)
    {
        var interaction = InteractionId.New();
        stream.Append(new InteractionRequested(interaction, kind, "{}", options, defaultOption, null,
            lane, null, null, 0, 1));
        return interaction;
    }

    private static InteractionId PendingPermission(EventStream stream, LaneId lane) =>
        RequestInteraction(stream, lane, InteractionKind.Permission, "[{\"id\":\"allow_once\"},{\"id\":\"deny\"}]",
            "deny");

    private static InteractionId PendingPlanApproval(EventStream stream, TestRun.Opened run) =>
        RequestInteraction(stream, run.RootLane, InteractionKind.PlanApproval, PlanApprovalOptions, "reject");

    // ── session.input: abre o alimenta el Run de la conversación (ADR-0035 §1) ────────────────

    [Fact]
    public void Session_input_without_a_run_creates_a_running_run_with_the_text_as_objective()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput("explícame el journal")).Status);

        var session = server.LastSessionId();
        var run = server.LastRunId();
        Assert.NotNull(session);
        Assert.NotNull(run);
        var journal = Journal(server);

        var projection = RunProjection.Replay(session!, run!, Codecs, journal);
        Assert.Equal(RunState.Running, projection.State);
        Assert.Equal("explícame el journal", projection.Objective);
        Assert.Equal(RunMode.Act, projection.Mode);

        var tasks = TaskGraphProjection.Replay(Codecs, journal);
        Assert.Equal(TaskState.Running, tasks.StateOf(projection.RootTask!));
        var rootLane = Assert.Single(LaneProjection.Replay(Codecs, journal).ForTask(projection.RootTask!));
        Assert.Equal(LaneState.Running, rootLane.State);

        Assert.Single(PlanProjection.Replay(Codecs, journal).Items());
        Assert.Equal(1, Count(journal, "user_input.received"));
        AssertJournalIsValid(server);
    }

    [Fact]
    public void Session_input_with_an_active_run_appends_a_user_message_without_a_new_run()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput("primer mensaje")).Status);
        var first = server.LastRunId();
        Assert.Equal("ok", Send(server, SessionInput("segundo mensaje")).Status);
        Assert.Equal(first, server.LastRunId());

        var journal = Journal(server);
        Assert.Equal(1, Count(journal, "run.created"));
        var inputs = journal.Select(Codecs.Decode).OfType<UserInputReceived>().ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, input => Assert.Equal(first, input.RunId));
        Assert.Contains(inputs, input => input.InputPartsJson.Contains("segundo mensaje", StringComparison.Ordinal));
        AssertJournalIsValid(server);
    }

    [Fact]
    public void Session_input_reactivates_a_run_awaiting_input()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput("hola")).Status);
        var session = server.LastSessionId()!;
        var run = server.LastRunId()!;
        var lane = RootLaneOf(server, run);

        Assert.Equal("ok", Send(server, Payload(JsonObj.Field("cmd", "run.interrupt"))).Status);
        Assert.Equal(LaneActivity.WaitingForInput, LaneActivityProjection.Derive(Codecs, Journal(server), lane));

        Assert.Equal("ok", Send(server, SessionInput("sigamos")).Status);
        var journal = Journal(server);
        Assert.Equal(RunState.Running, RunProjection.Replay(session, run, Codecs, journal).State);
        Assert.NotEqual(LaneActivity.WaitingForInput, LaneActivityProjection.Derive(Codecs, journal, lane));
        AssertJournalIsValid(server);
    }

    [Fact]
    public void Session_input_after_a_terminal_run_starts_a_new_run_in_the_same_session()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput("primer tema")).Status);
        var session = server.LastSessionId();
        var first = server.LastRunId()!;

        Assert.Equal("ok", Send(server, Payload(JsonObj.Field("cmd", "run.cancel"),
            JsonObj.Field("runId", first.ToString()))).Status);
        Assert.Equal("ok", Send(server, SessionInput("segundo tema")).Status);

        var second = server.LastRunId();
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(session, server.LastSessionId());

        var journal = Journal(server);
        Assert.Equal(2, Count(journal, "run.created"));
        Assert.Equal(RunState.Cancelled, RunProjection.Replay(session!, first, Codecs, journal).State);
        var projection = RunProjection.Replay(session!, second!, Codecs, journal);
        Assert.Equal(RunState.Running, projection.State);
        Assert.Equal("segundo tema", projection.Objective);
        AssertJournalIsValid(server);
    }

    [Fact]
    public void Session_input_with_mode_plan_starts_the_run_in_plan_mode()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput("diseña el cambio", "plan")).Status);

        var projection = RunProjection.Replay(server.LastSessionId()!, server.LastRunId()!, Codecs,
            Journal(server));
        Assert.Equal(RunMode.Plan, projection.Mode);
        Assert.Equal(RunState.Running, projection.State);
        AssertJournalIsValid(server);
    }

    [Fact]
    public void Session_input_preserves_quotes_newlines_and_backslashes_in_the_objective()
    {
        var text = "línea 1\n\"entre comillas\"\tfin \\ barra";
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput(text)).Status);

        var projection = RunProjection.Replay(server.LastSessionId()!, server.LastRunId()!, Codecs,
            Journal(server));
        Assert.Equal(text, projection.Objective);
        AssertJournalIsValid(server);
    }

    [Fact]
    public void Session_input_with_empty_text_fails_and_writes_nothing()
    {
        var server = OmniHost.CreateInMemoryServer();
        var ack = Send(server, SessionInput(""));
        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrEmpty(ack.Error), "el ack de error informa el motivo");
        Assert.Null(server.LastSessionId()); // ni la sesión existe: no se escribió nada en el journal

        var store = new InMemoryEventStore();
        var session = SessionId.New();
        Assert.Throws<ArgumentException>(() => Control(store).SendInput(session, "", RunMode.Act));
        Assert.Equal(0, store.CurrentSequence(session));
    }

    // ── StartRun: una sola conversación activa por sesión (ADR-0035 §1) ───────────────────────

    [Fact]
    public void StartRun_while_a_run_is_active_fails_with_the_active_run_id()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var control = Control(store);
        var first = control.StartRun(session, "primer objetivo", RunMode.Act);
        Assert.Equal(first, control.ActiveRun(session));
        var written = store.CurrentSequence(session);

        var ex = Assert.Throws<RunAlreadyActiveException>(
            () => control.StartRun(session, "otro objetivo", RunMode.Act));
        Assert.Equal(first, ex.ActiveRun);
        Assert.Equal(written, store.CurrentSequence(session));
        AssertJournalIsValid(store, session);
    }

    // ── Interrupt: corta el Turn en curso y devuelve el control al usuario ───────────────────

    [Fact]
    public void Interrupt_cuts_the_open_turn_and_leaves_the_run_awaiting_input()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var turn = TurnId.New();
        stream.Append(new TurnStarted(turn, run.RootLane));

        Control(store).Interrupt(session, run.RunId);

        var journal = Journal(store, session);
        Assert.Equal(1, Count(journal, "turn.interrupted"));
        Assert.Equal(1, Count(journal, "run.awaiting_input"));
        var awaiting = journal.Select(Codecs.Decode).OfType<RunAwaitingInput>().Single();
        Assert.Equal(run.RootLane, awaiting.RootLaneId);
        Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(session, run.RunId, Codecs, journal).State);
        Assert.Equal(LaneActivity.WaitingForInput, LaneActivityProjection.Derive(Codecs, journal, run.RootLane));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Interrupt_cancels_pre_started_tool_calls_and_fails_the_started_one()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        stream.Append(new TurnStarted(TurnId.New(), run.RootLane));
        var requested = ToolCallId.New();
        var prepared = ToolCallId.New();
        var authorized = ToolCallId.New();
        var started = ToolCallId.New();
        var succeeded = ToolCallId.New();
        ToolCallChain(stream, requested, ToolCallState.Requested);
        ToolCallChain(stream, prepared, ToolCallState.Prepared);
        ToolCallChain(stream, authorized, ToolCallState.Authorized);
        ToolCallChain(stream, started, ToolCallState.Started);
        ToolCallChain(stream, succeeded, ToolCallState.Succeeded);

        Control(store).Interrupt(session, run.RunId);

        var journal = Journal(store, session);
        // Las llamadas sin efecto iniciado se cancelan; la iniciada falla con efecto desconocido
        // para que la reconciliación (ADR-0004) decida su destino.
        Assert.Equal(3, Count(journal, "toolcall.cancelled"));
        foreach (var call in new[] { requested, prepared, authorized })
        {
            Assert.Equal(1, journal.Count(e => e.Type.ToString() == "toolcall.cancelled"
                && e.ToolCallId is not null && e.ToolCallId.Equals(call)));
        }

        var failed = journal.Select(Codecs.Decode).OfType<ToolCallFailed>().Single();
        Assert.Equal(started, failed.ToolCallId);
        Assert.Equal(EffectOutcome.Unknown, failed.EffectOutcome);

        // La llamada ya Succeeded es terminal: no recibe ningún evento nuevo.
        Assert.Equal(6, journal.Count(e => e.ToolCallId is not null && e.ToolCallId.Equals(succeeded)));
        Assert.DoesNotContain(journal, e => e.ToolCallId is not null && e.ToolCallId.Equals(succeeded)
            && (e.Type.ToString() == "toolcall.cancelled" || e.Type.ToString() == "toolcall.failed"));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Interrupt_cuts_a_turn_in_model_completed()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var turn = TurnId.New();
        stream.Append(new TurnStarted(turn, run.RootLane));
        stream.Append(new ModelCompleted(turn, null));

        Control(store).Interrupt(session, run.RunId);

        var journal = Journal(store, session);
        // Replay del journal completo (ADR-0036): reconstruye el Turn cortado y el Run en pausa.
        var tracker = CanonicalStateTracker.Replay(Codecs, journal);
        Assert.Equal(TurnState.Interrupted, tracker.Turn(turn));
        Assert.Equal(1, Count(journal, "turn.interrupted"));
        Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(session, run.RunId, Codecs, journal).State);
    }

    [Fact]
    public void A_second_interrupt_cancels_the_run()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var control = Control(store);
        control.Interrupt(session, run.RunId);
        control.Interrupt(session, run.RunId);

        var journal = Journal(store, session);
        Assert.Equal(1, Count(journal, "run.cancelled"));
        Assert.Equal(RunState.Cancelled, RunProjection.Replay(session, run.RunId, Codecs, journal).State);
        Assert.Equal(LaneState.Cancelled, LaneProjection.Replay(Codecs, journal).StateOf(run.RootLane));
        Assert.Equal(TaskState.Cancelled, TaskGraphProjection.Replay(Codecs, journal).StateOf(run.RootTask));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Interrupt_on_a_terminal_run_fails_without_writing()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var control = Control(store);
        control.CancelRun(session, run.RunId);
        var written = store.CurrentSequence(session);

        var ex = Assert.Throws<RunNotActiveException>(() => control.Interrupt(session, run.RunId));
        Assert.Equal(run.RunId, ex.Run);
        Assert.Equal(written, store.CurrentSequence(session));
        AssertJournalIsValid(store, session);
    }

    // ── CancelRun: cancela todo el Run y solo ese Run (ADR-0036 §3) ───────────────────────────

    [Fact]
    public void CancelRun_cancels_all_non_terminal_lanes_and_tasks_of_the_run()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var budget = new TaskBudget(null, null, null, null);

        // Task hija completada antes de la cancelación: ya terminal, no debe tocarse.
        var doneTask = TaskId.New();
        var doneLane = LaneId.New();
        stream.AppendBatch(new DomainEventPayload[]
        {
            new TaskCreated(doneTask, run.RunId, "ya hecha", Array.Empty<TaskDependency>(), budget),
            new TaskReady(doneTask),
            new LaneCreated(doneLane, doneTask, ProfileId.New()),
            new LaneStarted(doneLane),
            new TaskStarted(doneTask, doneLane),
            new LaneCompleted(doneLane, null),
            new TaskCompleted(doneTask, null),
        }, DurabilityClass.Standard);

        // Task hija creada pero sin Lane: no terminal, debe cancelarse.
        var pendingTask = TaskId.New();
        stream.AppendBatch(new DomainEventPayload[]
        {
            new TaskCreated(pendingTask, run.RunId, "pendiente", Array.Empty<TaskDependency>(), budget),
        }, DurabilityClass.Standard);

        Control(store).CancelRun(session, run.RunId);

        var journal = Journal(store, session);
        var tasks = TaskGraphProjection.Replay(Codecs, journal);
        var lanes = LaneProjection.Replay(Codecs, journal);
        Assert.Equal(TaskState.Cancelled, tasks.StateOf(run.RootTask));
        Assert.Equal(TaskState.Completed, tasks.StateOf(doneTask));
        Assert.Equal(TaskState.Cancelled, tasks.StateOf(pendingTask));
        Assert.Equal(LaneState.Cancelled, lanes.StateOf(run.RootLane));
        Assert.Equal(LaneState.Completed, lanes.StateOf(doneLane));
        Assert.Equal(RunState.Cancelled, RunProjection.Replay(session, run.RunId, Codecs, journal).State);
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void CancelRun_does_not_touch_tasks_and_lanes_of_a_previous_run()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var control = Control(store);
        var first = control.StartRun(session, "primer objetivo", RunMode.Act);
        control.CancelRun(session, first);
        var second = control.StartRun(session, "segundo objetivo", RunMode.Act);
        control.CancelRun(session, second);

        var journal = Journal(store, session);
        var firstRun = RunProjection.Replay(session, first, Codecs, journal);
        var secondRun = RunProjection.Replay(session, second, Codecs, journal);
        Assert.Equal(RunState.Cancelled, firstRun.State);
        Assert.Equal(RunState.Cancelled, secondRun.State);

        // Cada Task y Lane del primer Run recibió exactamente una cancelación: el segundo
        // CancelRun no volvió a tocarlos.
        Assert.Equal(1, journal.Count(e => e.Type.ToString() == "task.cancelled"
            && e.TaskId is not null && e.TaskId.Equals(firstRun.RootTask!)));
        Assert.Equal(1, journal.Count(e => e.Type.ToString() == "lane.cancelled"
            && e.LaneId is not null && e.LaneId.Equals(RootLaneOf(journal, session, first))));
        Assert.Equal(1, journal.Count(e => e.Type.ToString() == "task.cancelled"
            && e.TaskId is not null && e.TaskId.Equals(secondRun.RootTask!)));
        Assert.Equal(2, Count(journal, "task.cancelled"));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void CancelRun_expires_pending_interactions_only()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var pending = PendingPermission(stream, run.RootLane);
        var resolved = PendingPermission(stream, run.RootLane);
        stream.Append(new InteractionResolved(resolved, "allow_once", InteractionCause.User));

        Control(store).CancelRun(session, run.RunId);

        var journal = Journal(store, session);
        var expired = journal.Select(Codecs.Decode).OfType<InteractionExpired>().Single();
        Assert.Equal(pending, expired.InteractionId);
        var stillResolved = journal.Select(Codecs.Decode).OfType<InteractionResolved>().Single();
        Assert.Equal(resolved, stillResolved.InteractionId);
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Cancelling_twice_fails_with_run_not_active()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var control = Control(store);
        control.CancelRun(session, run.RunId);

        var ex = Assert.Throws<RunNotActiveException>(() => control.CancelRun(session, run.RunId));
        Assert.Equal(run.RunId, ex.Run);
        Assert.Equal(1, Count(Journal(store, session), "run.cancelled"));
        AssertJournalIsValid(store, session);
    }

    // ── RespondToInteraction: solo el servidor decide las opciones (ADR-0034) ─────────────────

    [Fact]
    public void Respond_to_an_unknown_or_resolved_interaction_fails()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var resolved = PendingPermission(stream, run.RootLane);
        stream.Append(new InteractionResolved(resolved, "allow_once", InteractionCause.User));
        var expired = PendingPermission(stream, run.RootLane);
        stream.Append(new InteractionExpired(expired));
        var control = Control(store);
        var written = store.CurrentSequence(session);

        Assert.Throws<InteractionNotPendingException>(() => control.Respond(session, InteractionId.New(),
            "allow_once"));
        var ex = Assert.Throws<InteractionNotPendingException>(() => control.Respond(session, resolved,
            "allow_once"));
        Assert.Equal(resolved, ex.Interaction);
        Assert.Throws<InteractionNotPendingException>(() => control.Respond(session, expired, "allow_once"));
        Assert.Equal(written, store.CurrentSequence(session));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Respond_with_an_option_the_server_did_not_offer_fails_without_writing()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var approval = PendingPlanApproval(stream, run);
        var control = Control(store);
        var written = store.CurrentSequence(session);

        var ex = Assert.Throws<InvalidInteractionOptionException>(() => control.Respond(session, approval,
            "aprobado_total"));
        Assert.Equal(approval, ex.Interaction);
        Assert.Equal("aprobado_total", ex.OptionId);
        Assert.Equal(written, store.CurrentSequence(session));
        AssertJournalIsValid(store, session);
    }

    // ── PlanApproval: PLAN → ACT en el mismo Run (ADR-0035 §6) ─────────────────────────────────

    [Fact]
    public void Plan_approval_approve_execute_switches_the_run_to_act()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "planifica el refactor", RunMode.Plan);
        var stream = new EventStream(store, Codecs, session);
        var approval = PendingPlanApproval(stream, run);

        Control(store).Respond(session, approval, "approve_execute");

        var journal = Journal(store, session);
        var resolved = journal.Select(Codecs.Decode).OfType<InteractionResolved>().Single();
        Assert.Equal(approval, resolved.InteractionId);
        Assert.Equal("approve_execute", resolved.OptionId);
        Assert.Equal(InteractionCause.User, resolved.Cause);
        var modeChanged = journal.Select(Codecs.Decode).OfType<RunModeChanged>().Single();
        Assert.Equal(RunMode.Plan, modeChanged.From);
        Assert.Equal(RunMode.Act, modeChanged.To);
        Assert.Equal("PlanApproved", modeChanged.Cause);

        var projection = RunProjection.Replay(session, run.RunId, Codecs, journal);
        Assert.Equal(RunMode.Act, projection.Mode);
        Assert.Equal(RunState.Running, projection.State);
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Plan_approval_approve_only_completes_the_run_as_planned()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "planifica el refactor", RunMode.Plan);
        var stream = new EventStream(store, Codecs, session);
        var approval = PendingPlanApproval(stream, run);

        Control(store).Respond(session, approval, "approve_only");

        var journal = Journal(store, session);
        var completed = journal.Select(Codecs.Decode).OfType<RunCompleted>().Single();
        Assert.Equal(RunOutcome.Planned, completed.Outcome);
        Assert.Equal(RunState.Completed, RunProjection.Replay(session, run.RunId, Codecs, journal).State);
        Assert.Equal(LaneState.Completed, LaneProjection.Replay(Codecs, journal).StateOf(run.RootLane));
        Assert.Equal(TaskState.Completed, TaskGraphProjection.Replay(Codecs, journal).StateOf(run.RootTask));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Plan_approval_reject_leaves_the_run_awaiting_input_in_plan_mode()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "planifica el refactor", RunMode.Plan);
        var stream = new EventStream(store, Codecs, session);
        var approval = PendingPlanApproval(stream, run);

        Control(store).Respond(session, approval, "reject");

        var journal = Journal(store, session);
        var projection = RunProjection.Replay(session, run.RunId, Codecs, journal);
        Assert.Equal(RunState.AwaitingInput, projection.State);
        Assert.Equal(RunMode.Plan, projection.Mode);
        Assert.Equal(0, Count(journal, "run.mode_changed"));
        Assert.Equal(LaneActivity.WaitingForInput, LaneActivityProjection.Derive(Codecs, journal, run.RootLane));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Respond_rejects_question_interactions()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var question = RequestInteraction(stream, run.RootLane, InteractionKind.Question,
            "[{\"id\":\"responder\"}]", "responder");
        var control = Control(store);
        var written = store.CurrentSequence(session);

        // La opción sí está entre las ofrecidas: se rechaza porque los cuestionarios no se
        // responden por esta vía (ADR-0045).
        Assert.Throws<InvalidInteractionOptionException>(() => control.Respond(session, question, "responder"));
        Assert.Equal(written, store.CurrentSequence(session));
        AssertJournalIsValid(store, session);
    }

    // ── Protocolo: acks de error, causación por comando y replay del journal ───────────────────

    [Fact]
    public void A_failing_command_returns_an_error_ack_and_writes_no_events()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput("hola")).Status);
        var session = server.LastSessionId()!;
        var store = server.AcquireStore();
        var written = store.CurrentSequence(session);

        // interaction.respond sobre una interacción inexistente: el comando falla sin efecto.
        var ack = Send(server, Payload(
            JsonObj.Field("cmd", "interaction.respond"),
            JsonObj.Field("interactionId", InteractionId.New().ToString()),
            JsonObj.Field("optionId", "approve_execute")));

        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrEmpty(ack.Error), "el ack de error informa el motivo");
        Assert.Equal(written, store.CurrentSequence(session));
        AssertJournalIsValid(server);
    }

    [Fact]
    public void Every_event_written_by_a_command_carries_its_command_causation()
    {
        var server = OmniHost.CreateInMemoryServer();
        var inputId = Ids.NewV7();
        Assert.Equal("ok", server.Send(WireEnvelope.Command(inputId, SessionInput("hola")),
            TestContext.Current.CancellationToken).Status);
        var interruptId = Ids.NewV7();
        Assert.Equal("ok", server.Send(WireEnvelope.Command(interruptId,
            Payload(JsonObj.Field("cmd", "run.interrupt"))), TestContext.Current.CancellationToken).Status);

        var first = Guid.Parse(inputId);
        var second = Guid.Parse(interruptId);
        var journal = Journal(server);
        Assert.All(journal, evt =>
        {
            var causation = Assert.IsType<CommandCausation>(evt.Causation);
            Assert.True(causation.CommandId.Value == first || causation.CommandId.Value == second,
                "todo evento lleva la causación del comando que lo escribió");
        });

        // session.input escribe la sesión y todo el arranque del Run; run.interrupt solo la pausa.
        var paused = journal.Where(e => e.Causation is CommandCausation c && c.CommandId.Value == second)
            .ToArray();
        Assert.Single(paused);
        Assert.Equal("run.awaiting_input", paused[0].Type.ToString());
        Assert.Equal(journal.Count - 1,
            journal.Count(e => e.Causation is CommandCausation c && c.CommandId.Value == first));
        AssertJournalIsValid(server);
    }

    [Fact]
    public void The_whole_journal_of_a_full_conversation_replays_without_invalid_transitions()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput("hola")).Status);
        Assert.Equal("ok", Send(server, Payload(JsonObj.Field("cmd", "run.interrupt"))).Status);
        Assert.Equal("ok", Send(server, SessionInput("sigamos")).Status);
        Assert.Equal("ok", Send(server, Payload(JsonObj.Field("cmd", "run.cancel"))).Status);
        Assert.Equal("ok", Send(server, SessionInput("otro tema")).Status);

        var session = server.LastSessionId()!;
        var journal = Journal(server);
        // Replay completo: ninguna transición inválida aunque el Run se pausó, reactivó,
        // canceló y abrió uno nuevo en la misma sesión.
        var tracker = CanonicalStateTracker.Replay(Codecs, journal);

        Assert.Equal(3, Count(journal, "user_input.received"));
        Assert.Equal(2, Count(journal, "run.created"));
        Assert.Equal(1, Count(journal, "run.awaiting_input"));
        Assert.Equal(1, Count(journal, "run.cancelled"));
        var runs = journal.Select(Codecs.Decode).OfType<RunCreated>().ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Equal(RunState.Cancelled, tracker.Run(runs[0].RunId));
        Assert.Equal(RunState.Running, tracker.Run(runs[1].RunId));
        Assert.Equal(RunState.Cancelled, RunProjection.Replay(session, runs[0].RunId, Codecs, journal).State);
        Assert.Equal(RunState.Running, RunProjection.Replay(session, runs[1].RunId, Codecs, journal).State);
    }

    [Fact]
    public void Interaction_respond_with_a_malformed_id_returns_an_error_ack()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", Send(server, SessionInput("hola")).Status);
        var session = server.LastSessionId()!;
        var store = server.AcquireStore();
        var written = store.CurrentSequence(session);

        var ack = Send(server, Payload(
            JsonObj.Field("cmd", "interaction.respond"),
            JsonObj.Field("interactionId", "no-es-un-uuid"),
            JsonObj.Field("optionId", "approve_execute")));

        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrEmpty(ack.Error), "el ack de error informa el motivo");
        Assert.Equal(written, store.CurrentSequence(session));
        AssertJournalIsValid(server);
    }
}
