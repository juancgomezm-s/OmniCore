namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

/// <summary>
/// EPIC-007: Lane Completion Pipeline (ADR-0016 §10, ADR-0035 §5). Antes de marcar una Lane como
/// <c>Completed</c> pasan sus gates: ninguna ToolCall de la Lane sin resolver, ningún Turn abierto
/// y la Task de la Lane en vuelo. Si un gate falla la Lane NO se completa (se queda Running) y el
/// rechazo llega por la validación del Run (<c>RunValidationRejected</c>): ADR-0016/0036 no definen
/// ningún evento de rechazo a nivel de Lane (la actividad <c>Validating</c> es derivada, INV-027).
/// Todos los journals de estos tests replayan contra <see cref="CanonicalStateTracker"/> sin
/// transiciones inválidas (ADR-0036).
/// </summary>
public sealed class LaneCompletionGateTests
{
    private static readonly EventCodecs Codecs = EventCodecs.Create();

    private const string PlanApprovalOptions =
        "[{\"id\":\"approve_execute\"},{\"id\":\"approve_only\"},{\"id\":\"reject\"}]";

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<DomainEvent> Journal(IEventStore store, SessionId session) =>
        store.ReadFrom(session, 1);

    private static void AssertJournalIsValid(IEventStore store, SessionId session)
    {
        // ADR-0036: replayar el journal completo contra las máquinas de estado no puede lanzar.
        CanonicalStateTracker.Replay(Codecs, Journal(store, session));
    }

    private sealed record Work(TaskId Task, LaneId Lane);

    /// <summary>Añade una Task de trabajo en ejecución con su Lane (ADR-0036 §2).</summary>
    private static Work OpenWorkTask(EventStream stream, TestRun.Opened run)
    {
        var task = TaskId.New();
        var lane = LaneId.New();
        var budget = new TaskBudget(null, null, null, null);
        stream.AppendBatch(new DomainEventPayload[] {
            new TaskCreated(task, run.RunId, "trabajo", Array.Empty<TaskDependency>(), budget),
            new TaskReady(task),
            new LaneCreated(lane, task, ProfileId.New()),
            new LaneStarted(lane),
            new TaskStarted(task, lane),
        }, DurabilityClass.Standard);
        return new Work(task, lane);
    }

    /// <summary>La cadena válida de una ToolCall hasta el estado pedido (ADR-0036 §5).</summary>
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

    /// <summary>Un Turn de la Lane con una ToolCall dentro, hasta el estado pedido.</summary>
    private static TurnId TurnWithToolCall(EventStream stream, LaneId lane, ToolCallState target,
        bool closeTurn = true)
    {
        var turn = TurnId.New();
        stream.Append(new TurnStarted(turn, lane));
        ToolCallChain(stream, ToolCallId.New(), target);
        if (closeTurn)
        {
            if (target == ToolCallState.Succeeded)
            {
                stream.Append(new TurnCompleted(turn));
            }
            else
            {
                // Efecto en vuelo: el Turn cierra interrumpido, como el Interrupt del runtime.
                stream.Append(new TurnInterrupted(turn));
            }
        }

        return turn;
    }

    /// <summary>Ejecuta el completion del Run como la simulación (RunCoupon, ADR-0016 §10).</summary>
    private static bool CompleteRun(InMemoryEventStore store, SessionId session, RunId run)
    {
        var events = Journal(store, session);
        var stream = new EventStream(store, Codecs, session);
        var runProj = RunProjection.Replay(session, run, Codecs, events);
        var tasks = TaskGraphProjection.Replay(Codecs, events);
        var plan = PlanProjection.Replay(Codecs, events);
        return new RunCoupon(runProj, tasks, plan)
            .CheckCompletionAndGate(new PlanService(), new ProgressReconciler(), store, Codecs, session, stream);
    }

    private static IEnumerable<LaneCompleted> LaneCompletions(IEventStore store, SessionId session) =>
        Journal(store, session).Select(Codecs.Decode).OfType<LaneCompleted>();

    // ── Gates de Lane al completar el Run (RunCoupon) ────────────────────────────────────────────

    [Fact]
    public void Lane_with_all_toolcalls_terminal_and_turn_closed_completes()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var work = OpenWorkTask(stream, run);
        TurnWithToolCall(stream, work.Lane, ToolCallState.Succeeded);

        var completed = CompleteRun(store, session, run.RunId);

        var journal = Journal(store, session);
        Assert.True(completed);
        Assert.Contains(LaneCompletions(store, session), e => e.LaneId.Equals(work.Lane));
        Assert.Contains(journal.Select(Codecs.Decode).OfType<TaskCompleted>(), e => e.TaskId.Equals(work.Task));
        Assert.Contains(journal.Select(Codecs.Decode).OfType<RunCompleted>(), e => e.RunId.Equals(run.RunId));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Lane_with_a_started_toolcall_does_not_complete()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var work = OpenWorkTask(stream, run);
        TurnWithToolCall(stream, work.Lane, ToolCallState.Started); // Turn cerrado, efecto sin resolver

        var completed = CompleteRun(store, session, run.RunId);

        Assert.False(completed);
        Assert.Empty(LaneCompletions(store, session)); // ni la de trabajo ni la raíz se completan
        var rejected = Assert.Single(Journal(store, session).Select(Codecs.Decode).OfType<RunValidationRejected>());
        Assert.Contains("tasks", rejected.Gates); // la Task de la Lane sigue Running y lo ve el Run
        Assert.Contains(rejected.Missing, m => m.Contains("en ejecución"));
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void Lane_with_an_open_turn_does_not_complete()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var work = OpenWorkTask(stream, run);
        TurnWithToolCall(stream, work.Lane, ToolCallState.Succeeded, closeTurn: false); // Turn sin cerrar

        var completed = CompleteRun(store, session, run.RunId);

        Assert.False(completed);
        Assert.Empty(LaneCompletions(store, session));
        var rejected = Assert.Single(Journal(store, session).Select(Codecs.Decode).OfType<RunValidationRejected>());
        Assert.Contains("tasks", rejected.Gates);
        AssertJournalIsValid(store, session);
    }

    [Fact]
    public void A_started_toolcall_in_the_root_lane_rejects_the_run_completion()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        TurnWithToolCall(stream, run.RootLane, ToolCallState.Started); // la conversación, en vuelo

        var completed = CompleteRun(store, session, run.RunId);

        Assert.False(completed);
        Assert.Empty(LaneCompletions(store, session)); // la Lane raíz no se cierra con el Run
        var rejected = Assert.Single(Journal(store, session).Select(Codecs.Decode).OfType<RunValidationRejected>());
        Assert.Equal(new[] { "lane" }, rejected.Gates);
        Assert.Contains(rejected.Missing, m => m.Contains("Started")); // la ToolCall sin resolver
        Assert.Contains(Journal(store, session).Select(Codecs.Decode).OfType<RunAwaitingInput>(),
            e => e.RootLaneId.Equals(run.RootLane));
        AssertJournalIsValid(store, session);
    }

    // ── Gates de Lane en el cierre de approve_only (RunControlService) ─────────────────────────

    [Fact]
    public void Plan_approval_approve_only_with_inflight_work_rejects_instead_of_closing()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "planifica el refactor", RunMode.Plan);
        var stream = new EventStream(store, Codecs, session);
        TurnWithToolCall(stream, run.RootLane, ToolCallState.Started); // la conversación, en vuelo
        var approval = InteractionId.New();
        stream.Append(new InteractionRequested(approval, InteractionKind.PlanApproval, "{}",
            PlanApprovalOptions, "reject", null, run.RootLane, null, null, 0, 1));

        new RunControlService(store, Codecs).Respond(session, approval, "approve_only");

        var journal = Journal(store, session);
        Assert.Empty(journal.Select(Codecs.Decode).OfType<LaneCompleted>()); // no se cierra nada
        Assert.Empty(journal.Select(Codecs.Decode).OfType<RunCompleted>()); // el Run no termina
        var rejected = Assert.Single(journal.Select(Codecs.Decode).OfType<RunValidationRejected>());
        Assert.Equal(new[] { "lane" }, rejected.Gates);
        Assert.Contains(rejected.Missing, m => m.Contains("Started"));
        // El Run vuelve a Running (Running → Validating → Running): la conversación sigue abierta.
        Assert.Equal(RunState.Running, RunProjection.Replay(session, run.RunId, Codecs, journal).State);
        AssertJournalIsValid(store, session);
    }

    // ── El pipeline, unidad a unidad ───────────────────────────────────────────────────────────

    [Fact]
    public void The_pipeline_passes_a_clean_lane()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var work = OpenWorkTask(stream, run);
        TurnWithToolCall(stream, work.Lane, ToolCallState.Succeeded);

        var result = new LaneCompletionPipeline().Check(Codecs, Journal(store, session), work.Lane);

        Assert.True(result.Passed);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void The_pipeline_reports_a_non_terminal_toolcall_of_the_lane()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var work = OpenWorkTask(stream, run);
        var call = ToolCallId.New();
        var turn = TurnId.New();
        stream.Append(new TurnStarted(turn, work.Lane));
        ToolCallChain(stream, call, ToolCallState.Started);
        stream.Append(new TurnInterrupted(turn)); // Turn cerrado, ToolCall sin resolver

        var result = new LaneCompletionPipeline().Check(Codecs, Journal(store, session), work.Lane);

        Assert.False(result.Passed);
        Assert.Contains(result.Missing, m => m.Contains(call.ToString()) && m.Contains("Started"));
    }

    [Fact]
    public void The_pipeline_reports_an_open_turn_of_the_lane()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var work = OpenWorkTask(stream, run);
        var turn = TurnId.New();
        stream.Append(new TurnStarted(turn, work.Lane)); // abierto: sin ModelCompleted ni cierre

        var result = new LaneCompletionPipeline().Check(Codecs, Journal(store, session), work.Lane);

        Assert.False(result.Passed);
        Assert.Contains(result.Missing, m => m.Contains(turn.ToString()) && m.Contains("abierto"));
    }

    [Fact]
    public void The_pipeline_rejects_a_lane_whose_task_is_not_in_flight()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var work = OpenWorkTask(stream, run);
        stream.Append(new TaskCompleted(work.Task, null)); // la Task ya decidió con su Lane Running

        var result = new LaneCompletionPipeline().Check(Codecs, Journal(store, session), work.Lane);

        Assert.False(result.Passed);
        Assert.Contains(result.Missing, m => m.Contains(work.Task.ToString()) && m.Contains("Completed"));
    }

    [Fact]
    public void The_pipeline_attributes_toolcalls_written_before_their_turn()
    {
        // A legacy, single-Lane Run has an unambiguous owner for simulation events written first.
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var call = ToolCallId.New();
        ToolCallChain(stream, call, ToolCallState.Started); // antes del Turn, estilo sim
        var turn = TurnId.New();
        stream.Append(new TurnStarted(turn, run.RootLane));
        stream.Append(new TurnCompleted(turn));

        var result = new LaneCompletionPipeline().Check(Codecs, Journal(store, session), run.RootLane);

        Assert.False(result.Passed); // la ToolCall sin resolver es de esta Lane
        Assert.Contains(result.Missing, m => m.Contains(call.ToString()) && m.Contains("Started"));
        Assert.DoesNotContain(result.Missing, m => m.Contains("abierto")); // el Turn sí está cerrado
    }

    [Fact]
    public void Open_tool_call_in_a_sibling_lane_does_not_block_completion_or_activity_of_this_lane()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var laneA = OpenWorkTask(stream, run);
        var laneB = OpenWorkTask(stream, run);
        var turnA = TurnId.New();
        var turnB = TurnId.New();
        stream.Append(new TurnStarted(turnA, laneA.Lane));
        stream.Append(new TurnCompleted(turnA));
        stream.Append(new TurnStarted(turnB, laneB.Lane));
        var callB = ToolCallId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, laneB.Task, laneB.Lane, turnB)))
            ToolCallChain(stream, callB, ToolCallState.Started);

        var journal = Journal(store, session);
        var gateA = new LaneCompletionPipeline().Check(Codecs, journal, laneA.Lane);
        var activityA = LaneActivityProjection.Derive(Codecs, journal, laneA.Lane);

        Assert.True(gateA.Passed, string.Join(", ", gateA.Missing));
        Assert.Equal(LaneActivity.None, activityA);
        Assert.False(new LaneCompletionPipeline().Check(Codecs, journal, laneB.Lane).Passed);
    }

    [Fact]
    public void Unattributed_open_tool_call_in_a_multi_lane_run_blocks_completion_as_ambiguous()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var laneA = OpenWorkTask(stream, run);
        var laneB = OpenWorkTask(stream, run);
        var turnA = TurnId.New();
        var turnB = TurnId.New();
        stream.Append(new TurnStarted(turnA, laneA.Lane));
        stream.Append(new TurnStarted(turnB, laneB.Lane));
        var call = ToolCallId.New();
        ToolCallChain(stream, call, ToolCallState.Started); // legacy receipt while two Lanes are active
        stream.Append(new TurnCompleted(turnA));

        var journal = Journal(store, session);
        var gateA = new LaneCompletionPipeline().Check(Codecs, journal, laneA.Lane);
        var activityA = LaneActivityProjection.Derive(Codecs, journal, laneA.Lane);

        Assert.False(gateA.Passed);
        Assert.Contains(gateA.Missing, item => item.Contains(call.ToString()) && item.Contains("ambigua"));
        Assert.Equal(LaneActivity.Stalled, activityA);
    }

    [Fact]
    public void The_pipeline_rejects_a_lane_that_is_not_running()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, Codecs, session);
        var work = OpenWorkTask(stream, run);
        TurnWithToolCall(stream, work.Lane, ToolCallState.Succeeded);
        stream.Append(new LaneCompleted(work.Lane, null)); // ya completada: no vuelve a pasar gates

        var result = new LaneCompletionPipeline().Check(Codecs, Journal(store, session), work.Lane);

        Assert.False(result.Passed);
        Assert.Contains(result.Missing, m => m.Contains("solo una Lane Running"));
    }
}
