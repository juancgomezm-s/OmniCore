using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Watchdog en Runs reales y respuesta al estancamiento (ADR-0048): detección por episodio,
/// cadena determinista, directiva en el contexto, AskUser durable y su resolución.
/// </summary>
public sealed class StallPolicyTests
{
    private const string RuntimeOrigin = ProgressWatchdog.RuntimeOriginPrefix + "ActLoop)";

    // ── Política (pura) ──

    [Fact]
    public void Evaluator_follows_the_default_chain_and_repeats_ask_user_when_exhausted()
    {
        var all = new StallAvailability(null, null);
        Assert.Equal(StallPolicy.Replan, StallPolicyEvaluator.Select(null, all).Policy);
        Assert.Equal(StallPolicy.Diagnose, StallPolicyEvaluator.Select(StallPolicy.Replan, all).Policy);
        Assert.Equal(StallPolicy.EscalateModel, StallPolicyEvaluator.Select(StallPolicy.Diagnose, all).Policy);
        Assert.Equal(StallPolicy.SplitTask, StallPolicyEvaluator.Select(StallPolicy.EscalateModel, all).Policy);
        Assert.Equal(StallPolicy.AskUser, StallPolicyEvaluator.Select(StallPolicy.SplitTask, all).Policy);
        var again = StallPolicyEvaluator.Select(StallPolicy.AskUser, all);
        Assert.Equal(StallPolicy.AskUser, again.Policy);
        Assert.Empty(again.Skipped);
    }

    [Fact]
    public void Evaluator_skips_unavailable_responses_with_stable_codes_and_never_invents_one()
    {
        var noEscalation = StallPolicyEvaluator.Select(StallPolicy.Diagnose, new StallAvailability("NoEscalationRoute", null));
        Assert.Equal(StallPolicy.SplitTask, noEscalation.Policy);
        Assert.Equal(new[] { "EscalateModel:NoEscalationRoute" }, noEscalation.Skipped);

        var delegated = StallAvailability.DirectivesOnly("DelegatedLane");
        Assert.Equal(StallPolicy.SplitTask, StallPolicyEvaluator.Select(StallPolicy.Diagnose, delegated).Policy);
        var exhausted = StallPolicyEvaluator.Select(StallPolicy.SplitTask, delegated);
        Assert.Null(exhausted.Policy);
        Assert.Equal(new[] { "AskUser:DelegatedLane" }, exhausted.Skipped);
        var afterExhausted = StallPolicyEvaluator.Select(StallPolicy.AskUser, delegated);
        Assert.Null(afterExhausted.Policy);
    }

    // ── Detección (pura, sobre el journal) ──

    [Fact]
    public void Observe_emits_one_episode_per_threshold_and_progress_restarts_the_chain()
    {
        var fx = WatchedRun.Create();
        fx.AppendTurn();
        Assert.Null(fx.Observe(2));
        fx.AppendTurn();
        var first = Assert.IsType<StallObservation>(fx.Observe(2));
        Assert.Equal(fx.Item, first.PlanItemId);
        Assert.Equal(2, first.TurnsWithoutProgress);
        Assert.Equal(0, first.Step);
        Assert.Null(first.LastPolicy);
        Assert.Equal(fx.LastSignalTimestamp(), first.LastProgressAt);

        fx.RecordStall(first, StallPolicy.Replan);
        Assert.Null(fx.Observe(2)); // el episodio registrado no se repite
        fx.AppendTurn();
        Assert.Null(fx.Observe(2));
        fx.AppendTurn();
        var second = Assert.IsType<StallObservation>(fx.Observe(2));
        Assert.Equal(1, second.Step);
        Assert.Equal(StallPolicy.Replan, second.LastPolicy);
        Assert.Equal(4, second.TurnsWithoutProgress);

        fx.Stream.Append(new PlanItemStarted(fx.Item)); // señal de progreso
        fx.AppendTurn();
        fx.AppendTurn();
        var restarted = Assert.IsType<StallObservation>(fx.Observe(2));
        Assert.Equal(0, restarted.Step);
        Assert.Null(restarted.LastPolicy);
    }

    [Fact]
    public void Human_input_restarts_the_count_but_runtime_prompts_do_not()
    {
        var human = WatchedRun.Create();
        human.AppendTurn();
        human.Stream.Append(new UserInputReceived(human.Run.RunId, "\"next question\"", null, null));
        human.AppendTurn();
        Assert.Null(human.Observe(2));

        var runtime = WatchedRun.Create();
        runtime.AppendTurn();
        runtime.Stream.Append(new UserInputReceived(runtime.Run.RunId, "\"feedback\"", null, RuntimeOrigin));
        runtime.AppendTurn();
        Assert.NotNull(runtime.Observe(2));
    }

    [Fact]
    public void Repeated_identical_validation_rejection_is_not_progress_but_a_new_result_is()
    {
        var repeated = WatchedRun.Create();
        repeated.Reject("plan", "P1 pending");
        repeated.AppendTurn();
        repeated.Reject("plan", "P1 pending");
        repeated.AppendTurn();
        Assert.NotNull(repeated.Observe(2));

        var changed = WatchedRun.Create();
        changed.Reject("plan", "P1 pending");
        changed.AppendTurn();
        changed.Reject("test", "test: 1 failed");
        changed.AppendTurn();
        Assert.Null(changed.Observe(2));
    }

    [Fact]
    public void Decomposed_plan_is_watched_only_while_an_item_is_in_progress()
    {
        var fx = WatchedRun.Create();
        var child = PlanItemId.New();
        fx.Stream.Append(new PlanItemAdded(child, fx.Plan, "subtask", 2, fx.Item, Array.Empty<PlanItemId>(), true,
            new Dictionary<string, string>()));
        fx.AppendTurn();
        fx.AppendTurn();
        Assert.Null(fx.Observe(2)); // ya no es el Plan sin descomponer y nada está InProgress
    }

    [Fact]
    public void Directive_targets_watched_lanes_until_the_next_progress_signal()
    {
        var fx = WatchedRun.Create();
        fx.AppendTurn();
        fx.AppendTurn();
        fx.RecordStall(fx.Observe(2)!, StallPolicy.Diagnose);

        var directive = Assert.IsType<StallDirective>(
            StallWatch.ActiveDirective(fx.Codecs, fx.Events, fx.Run.RunId, fx.Run.RootLane));
        Assert.Equal(StallPolicy.Diagnose, directive.Policy);
        Assert.Contains("plan.propose", directive.Render(canProposePlan: true));
        Assert.DoesNotContain("plan.propose", directive.Render(canProposePlan: false));
        Assert.Null(StallWatch.ActiveDirective(fx.Codecs, fx.Events, fx.Run.RunId, LaneId.New()));

        fx.Stream.Append(new PlanItemStarted(fx.Item));
        Assert.Null(StallWatch.ActiveDirective(fx.Codecs, fx.Events, fx.Run.RunId, fx.Run.RootLane));
    }

    [Theory]
    [InlineData(StallPolicy.EscalateModel)]
    [InlineData(StallPolicy.AskUser)]
    public void Escalation_and_ask_user_do_not_produce_a_context_directive(StallPolicy policy)
    {
        var fx = WatchedRun.Create();
        fx.AppendTurn();
        fx.AppendTurn();
        fx.RecordStall(fx.Observe(2)!, policy);
        Assert.Null(StallWatch.ActiveDirective(fx.Codecs, fx.Events, fx.Run.RunId, fx.Run.RootLane));
    }

    // ── Runs reales: ExplorerTurn ──

    [Fact]
    public void Real_turns_record_the_stall_and_the_next_turn_receives_the_directive()
    {
        using var fx = TurnFixture.Create();
        var turn = fx.Turn(threshold: 2);
        Assert.Null(fx.Ask(turn, "fix the bug", origin: null).Stall); // responde al humano: no cuenta
        Assert.Null(fx.Ask(turn, "continue").Stall);
        var stalled = fx.Ask(turn, "continue").Stall;

        Assert.NotNull(stalled);
        Assert.Equal(StallPolicy.Replan, stalled!.Policy);
        Assert.Equal(2, stalled.TurnsWithoutProgress);
        var events = fx.Decoded();
        var progress = Assert.Single(events.OfType<ProgressStalled>());
        Assert.Equal(fx.Item, progress.PlanItemId);
        var response = Assert.Single(events.OfType<StallResponseSelected>());
        Assert.Equal(StallPolicy.Replan, response.Policy);
        Assert.Equal(fx.Run.RunId, response.RunId);

        fx.Ask(turn, "continue");
        var instructions = fx.Requests[^1].Instructions!;
        Assert.Contains("stall policy Replan", instructions, StringComparison.Ordinal);
        Assert.Contains("plan.propose", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("stall policy", fx.Requests[0].Instructions ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Stall_threshold_from_the_harness_policy_changes_when_the_watchdog_fires()
    {
        static int Stalls(int threshold)
        {
            using var fx = TurnFixture.Create();
            var turn = fx.Turn(threshold);
            fx.Ask(turn, "fix the bug", origin: null);
            fx.Ask(turn, "continue");
            fx.Ask(turn, "continue");
            fx.Ask(turn, "continue");
            return fx.Decoded().OfType<ProgressStalled>().Count();
        }

        Assert.Equal(1, Stalls(2));
        Assert.Equal(0, Stalls(4));
    }

    [Fact]
    public void User_driven_conversation_never_stalls()
    {
        using var fx = TurnFixture.Create();
        var turn = fx.Turn(threshold: 1);
        for (var i = 0; i < 4; i++)
            Assert.Null(fx.Ask(turn, "question " + i, origin: null).Stall);
        Assert.Empty(fx.Decoded().OfType<ProgressStalled>());
    }

    [Fact]
    public void Chain_skips_escalation_without_host_and_ends_in_a_durable_ask_user()
    {
        using var fx = TurnFixture.Create();
        var turn = fx.Turn(threshold: 1);
        var policies = new List<StallPolicy?>();
        policies.Add(fx.Ask(turn, "fix the bug", origin: null).Stall?.Policy);
        for (var i = 0; i < 4; i++) policies.Add(fx.Ask(turn, "continue").Stall?.Policy);

        Assert.Equal(new StallPolicy?[]
            { null, StallPolicy.Replan, StallPolicy.Diagnose, StallPolicy.SplitTask, StallPolicy.AskUser }, policies);
        var events = fx.Decoded();
        var split = events.OfType<StallResponseSelected>().Single(item => item.Policy == StallPolicy.SplitTask);
        Assert.Equal(new[] { "EscalateModel:NoEscalationHost" }, split.Skipped);
        var request = Assert.Single(events.OfType<InteractionRequested>(),
            item => item.Kind == InteractionKind.StallResolution);
        Assert.Contains("\"continue\"", request.OptionsJson);
        Assert.Contains("\"stop\"", request.OptionsJson);
        Assert.Equal("stop", request.DefaultOptionId);
        Assert.Equal(fx.Item, request.PlanItem);
        Assert.Equal(RunState.AwaitingInput, fx.RunState());
    }

    [Fact]
    public void Escalation_is_chosen_only_when_the_host_offers_it()
    {
        static StallResponseSelected Third(Func<string?> escalation)
        {
            using var fx = TurnFixture.Create();
            var turn = fx.Turn(threshold: 1, escalation);
            fx.Ask(turn, "fix the bug", origin: null);
            for (var i = 0; i < 3; i++) fx.Ask(turn, "continue");
            return fx.Decoded().OfType<StallResponseSelected>().Last();
        }

        Assert.Equal(StallPolicy.EscalateModel, Third(() => null).Policy);
        var conversation = Third(() => "UserDrivenTurn");
        Assert.Equal(StallPolicy.SplitTask, conversation.Policy);
        Assert.Equal(new[] { "EscalateModel:UserDrivenTurn" }, conversation.Skipped);
    }

    // ── Resolución de AskUser (RunControlService) ──

    [Fact]
    public void Continue_resumes_the_run_and_the_next_stall_asks_the_user_again()
    {
        using var fx = TurnFixture.Create();
        var turn = fx.Turn(threshold: 1);
        var interaction = fx.ReachAskUser(turn);

        new RunControlService(fx.Store, fx.Codecs).Respond(fx.Session, interaction, "continue");
        Assert.Equal(RunState.Running, fx.RunState());
        var resumed = fx.Decoded().OfType<UserInputReceived>().Last();
        Assert.Equal("InteractionResponse(StallResolution)", resumed.Origin);

        Assert.Null(fx.Ask(turn, "continue").Stall); // responde a la decisión humana: no cuenta
        Assert.Equal(StallPolicy.AskUser, fx.Ask(turn, "continue").Stall?.Policy);
        Assert.Equal(2, fx.Decoded().OfType<InteractionRequested>().Count(item => item.Kind == InteractionKind.StallResolution));
    }

    [Fact]
    public void Stop_cancels_the_run_in_the_same_commit_without_expiring_the_answered_interaction()
    {
        using var fx = TurnFixture.Create();
        var turn = fx.Turn(threshold: 1);
        var interaction = fx.ReachAskUser(turn);
        var before = fx.Store.CurrentSequence(fx.Session);

        new RunControlService(fx.Store, fx.Codecs).Respond(fx.Session, interaction, "stop");
        var appended = fx.Store.ReadFrom(fx.Session, before + 1).Select(fx.Codecs.Decode).ToArray();
        Assert.Contains(appended, payload => payload is InteractionResolved { OptionId: "stop" } resolved
            && resolved.InteractionId == interaction);
        Assert.Contains(appended, payload => payload is RunCancelled cancelled && cancelled.RunId == fx.Run.RunId);
        Assert.DoesNotContain(appended, payload => payload is InteractionExpired expired && expired.InteractionId == interaction);
        Assert.Equal(RunState.Cancelled, fx.RunState());
    }

    // ── Bucle ACT real (OmniServer + journal SQLite + RunActLoop) ──

    [Fact]
    public void Act_loop_without_tty_walks_the_chain_and_leaves_ask_user_pending()
    {
        using var context = ActContext.Start();
        var output = new List<string>();
        var runtime = context.Runtime();
        var status = runtime.RunActLoop(context.Turn(escalation: null), output.Add, "corrige este test", "Coder",
            context.Session, context.Run, context.Lane, "", context.FailingGate, null, context.Server,
            context.Artifacts, new InMemoryAuditSink(), null, interactive: false, "es", CancellationToken.None);

        Assert.Equal(3, status);
        var events = context.Decoded();
        Assert.Equal(new[] { StallPolicy.Replan, StallPolicy.Diagnose, StallPolicy.SplitTask, StallPolicy.AskUser },
            events.OfType<StallResponseSelected>().Select(item => item.Policy));
        Assert.Equal(4, events.OfType<ProgressStalled>().Count());
        Assert.Contains(events.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.StallResolution);
        Assert.DoesNotContain(events.OfType<InteractionResolved>(), item => item.OptionId is "continue" or "stop");
        Assert.Equal(RunState.AwaitingInput, context.RunState());
        Assert.Contains(output, line => line.Contains("Sin progreso en «corrige este test»", StringComparison.Ordinal)
            && line.Contains("replanificar", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("InputRequired", StringComparison.Ordinal)
            && line.Contains("StallResolution", StringComparison.Ordinal));
        // Los prompts del bucle los genera el runtime y se marcan así; el objetivo es humano.
        var inputs = events.OfType<UserInputReceived>().ToArray();
        Assert.Null(inputs[0].Origin);
        Assert.Contains(inputs, input => input.Origin == RuntimeOrigin);
    }

    [Fact]
    public void Act_loop_console_answer_stop_cancels_the_run()
    {
        using var context = ActContext.Start();
        var output = new List<string>();
        var status = context.Runtime().RunActLoop(context.Turn(escalation: null), output.Add, "corrige este test",
            "Coder", context.Session, context.Run, context.Lane, "", context.FailingGate, null, context.Server,
            context.Artifacts, new InMemoryAuditSink(),
            request => request.Kind == InteractionKind.StallResolution ? "stop" : "deny",
            interactive: true, "es", CancellationToken.None);

        Assert.Equal(1, status);
        Assert.Equal(RunState.Cancelled, context.RunState());
        Assert.Contains(output, line => line.Contains("Run detenido por el usuario", StringComparison.Ordinal));
    }

    [Fact]
    public void Act_loop_hands_escalate_model_to_the_runtime_escalation_hook()
    {
        using var context = ActContext.Start();
        var escalations = 0;
        var status = context.Runtime().RunActLoop(context.Turn(escalation: () => null), _ => { }, "corrige este test",
            "Coder", context.Session, context.Run, context.Lane, "", context.FailingGate, null, context.Server,
            context.Artifacts, new InMemoryAuditSink(), null, interactive: false, "es", CancellationToken.None,
            escalateOnStall: () => { escalations++; return 7; });

        Assert.Equal(7, status);
        Assert.Equal(1, escalations);
        Assert.Equal(StallPolicy.EscalateModel, context.Decoded().OfType<StallResponseSelected>().Last().Policy);
    }

    // ── Visibilidad (protocolo y cliente) ──

    [Fact]
    public void Client_shows_stall_and_response_as_notices()
    {
        using var fx = TurnFixture.Create();
        var turn = fx.Turn(threshold: 2);
        fx.Ask(turn, "fix the bug", origin: null);
        fx.Ask(turn, "continue");
        fx.Ask(turn, "continue");

        var wire = new ProtocolMapper(fx.Codecs).Map(fx.Store.ReadFrom(fx.Session, 1));
        var projection = new ClientProjection(Localization.Spanish());
        var state = wire.Aggregate(ClientState.Empty(), projection.Apply);
        var system = state.Conversation.Blocks.Where(block => block.Role == ConversationRole.System)
            .Select(block => block.Text).ToArray();
        Assert.Contains(system, text => text.Contains("Sin progreso", StringComparison.Ordinal)
            && text.Contains("2 turns", StringComparison.Ordinal));
        Assert.Contains(system, text => text.Contains("replanificar", StringComparison.Ordinal));
    }

    // ── Fixtures ──

    /// <summary>
    /// Run creado por el comando <c>act</c> del servidor real: su Plan es el item raíz sin
    /// descomponer (ADR-0048 §1). El gate de test apunta a un ejecutable inexistente, así que
    /// rechaza siempre igual sin lanzar procesos.
    /// </summary>
    private sealed class ActContext : IDisposable
    {
        private readonly string _root;

        private ActContext(string root, OmniServer server, SqliteEventStore store, string workspace)
        {
            _root = root;
            Server = server;
            Store = store;
            Workspace = workspace;
            Session = server.LastSessionId()!;
            Run = server.LastRunId()!;
            Lane = server.LastLaneId()!;
            Artifacts = new FileArtifactStore(Path.Combine(root, "artifacts"));
        }

        public OmniServer Server { get; }
        public SqliteEventStore Store { get; }
        public string Workspace { get; }
        public SessionId Session { get; }
        public RunId Run { get; }
        public LaneId Lane { get; }
        public FileArtifactStore Artifacts { get; }
        public WorkspaceGatesYaml FailingGate { get; } = new() { Test = ["omnicore-stall-missing-gate-tool"] };

        public static ActContext Start()
        {
            var root = Path.Combine(Path.GetTempPath(), "omnicore-stall-act", Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var store = new SqliteEventStore(Path.Combine(root, "journal.db"));
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(),
                Path.Combine(root, "lastsession.txt"));
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
                + JsonObj.Field("objective", "corrige este test") + "," + JsonObj.Field("workspace", workspace) + "}"),
                CancellationToken.None).Status);
            return new ActContext(root, server, store, workspace);
        }

        public OmniCliRuntime Runtime()
        {
            var runtime = OmniCliRuntime.Create(Workspace);
            runtime.Localize = new Localization("es").Resolve;
            runtime.UseConsoleInput = false;
            return runtime;
        }

        public ExplorerTurn Turn(Func<string?>? escalation)
        {
            var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                PlanControl.Assisted, 1);
            var boundary = new ModelCapabilityBoundary(
                EffectiveModelPolicy.Resolve(ModelPolicyKey.For("stall", "stall-act"), null, harness));
            var catalog = OmniHost.CreateActTools().Catalog();
            var executor = OmniHost.CreateActExecutor(catalog, Workspace, boundary);
            return new ExplorerTurn((_, _) => new ModelResponse(new ContentBlock[] { new TextBlock("listo") },
                    StopReason.EndTurn, new TokenUsage(2, 2, 0, 0, 0), null, new ProviderMetadata("", "", null)),
                executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("stall-act", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("stall-act"), 8192, ToolMode.Direct, null),
                Server.AcquireStore(), Server.AcquireCodecs(), Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                harness: harness, boundary: boundary, stallEscalationUnavailable: escalation);
        }

        public IReadOnlyList<DomainEventPayload> Decoded() =>
            Store.ReadFrom(Session, 1).Select(Server.AcquireCodecs().Decode).ToArray();

        public RunState RunState() =>
            RunProjection.Replay(Session, Run, Server.AcquireCodecs(), Store.ReadFrom(Session, 1)).State;

        public void Dispose()
        {
            Store.Close();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class WatchedRun
    {
        public required InMemoryEventStore Store { get; init; }
        public required EventCodecs Codecs { get; init; }
        public required EventStream Stream { get; init; }
        public required TestRun.Opened Run { get; init; }
        public required PlanItemId Item { get; init; }
        public required PlanId Plan { get; init; }
        public IReadOnlyList<DomainEvent> Events => Store.ReadFrom(Run.SessionId, 1);

        public static WatchedRun Create()
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session);
            var item = PlanItemId.New();
            var plan = PlanId.New();
            stream.Append(new PlanCreated(plan, run.RunId, item, "objective"));
            return new WatchedRun { Store = store, Codecs = codecs, Stream = stream, Run = run, Item = item, Plan = plan };
        }

        public void AppendTurn()
        {
            var turn = TurnId.New();
            Stream.Append(new TurnStarted(turn, Run.RootLane));
            Stream.Append(new TurnCompleted(turn));
        }

        /// <summary>Ciclo real de un rechazo de gate: Validating → AwaitingInput → feedback del runtime.</summary>
        public void Reject(string gate, string missing)
        {
            Stream.Append(new RunValidationStarted(Run.RunId));
            Stream.Append(new RunValidationRejected(Run.RunId, [gate], [missing]));
            Stream.Append(new RunAwaitingInput(Run.RunId, Run.RootLane));
            Stream.Append(new UserInputReceived(Run.RunId, "\"fix it\"", null, RuntimeOrigin));
        }

        public StallObservation? Observe(int threshold) => StallWatch.Observe(Codecs, Events, Run.RunId, threshold);

        public void RecordStall(StallObservation observed, StallPolicy policy) => Stream.AppendBatch(
            new DomainEventPayload[]
            {
                new ProgressStalled(observed.PlanItemId, observed.TurnsWithoutProgress, observed.LastProgressAt),
                new StallResponseSelected(Run.RunId, observed.PlanItemId, policy, observed.Step, Array.Empty<string>()),
            }, DurabilityClass.Standard);

        public DateTimeOffset LastSignalTimestamp() =>
            Events.Last(evt => Codecs.Decode(evt) is TaskStarted or LaneStarted).Timestamp;
    }

    private sealed class TurnFixture : IDisposable
    {
        private readonly string _artifacts;
        private readonly HostTools _tools = OmniHost.CreateExplorerTools();

        private TurnFixture(string artifacts, InMemoryEventStore store, EventCodecs codecs, SessionId session,
            TestRun.Opened run, PlanItemId item)
        {
            _artifacts = artifacts;
            Store = store;
            Codecs = codecs;
            Session = session;
            Run = run;
            Item = item;
        }

        public InMemoryEventStore Store { get; }
        public EventCodecs Codecs { get; }
        public SessionId Session { get; }
        public TestRun.Opened Run { get; }
        public PlanItemId Item { get; }
        public List<ModelRequest> Requests { get; } = new();

        public static TurnFixture Create()
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session);
            var item = PlanItemId.New();
            stream.Append(new PlanCreated(PlanId.New(), run.RunId, item, "fix the bug"));
            var artifacts = Path.Combine(Path.GetTempPath(), "omnicore-stall-" + Guid.NewGuid().ToString("N"));
            return new TurnFixture(artifacts, store, codecs, session, run, item);
        }

        public ExplorerTurn Turn(int threshold, Func<string?>? escalation = null)
        {
            var executor = ScriptedToolExecutor.WithCoreTools(_tools.Catalog(),
                ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow));
            return new ExplorerTurn((request, token) =>
                {
                    Requests.Add(request);
                    return new ModelResponse(new ContentBlock[] { new TextBlock("still thinking") }, StopReason.EndTurn,
                        new TokenUsage(5, 7, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
                },
                executor, _tools.Catalog(),
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("stall", "h", "t", "c", "o", "M2"),
                new ModelSelection(new ModelIdValue("stall"), 32_000, ToolMode.Direct, null),
                Store, Codecs, new FileArtifactStore(_artifacts), new InMemoryAuditSink(), new RedactionPolicy(),
                harness: new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 16, GuidanceLevel.Full, 3,
                    PlanControl.Assisted, threshold),
                stallEscalationUnavailable: escalation);
        }

        public ExplorerTurn.TurnResult Ask(ExplorerTurn turn, string question, string? origin = RuntimeOrigin) =>
            turn.Ask(question, "ACT", Session, Run.RunId, Run.RootLane, "", TestContext.Current.CancellationToken, origin);

        public InteractionId ReachAskUser(ExplorerTurn turn)
        {
            fixedAsk(turn, "fix the bug", null);
            for (var i = 0; i < 4; i++) fixedAsk(turn, "continue", RuntimeOrigin);
            return Decoded().OfType<InteractionRequested>().Single(item => item.Kind == InteractionKind.StallResolution)
                .InteractionId;

            void fixedAsk(ExplorerTurn current, string question, string? origin) => Ask(current, question, origin);
        }

        public IReadOnlyList<DomainEventPayload> Decoded() =>
            Store.ReadFrom(Session, 1).Select(Codecs.Decode).ToArray();

        public RunState RunState() =>
            RunProjection.Replay(Session, Run.RunId, Codecs, Store.ReadFrom(Session, 1)).State;

        public void Dispose()
        {
            try { if (Directory.Exists(_artifacts)) Directory.Delete(_artifacts, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
