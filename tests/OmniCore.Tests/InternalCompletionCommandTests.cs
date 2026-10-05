using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class InternalCompletionCommandTests
{
    [Fact]
    public void Completion_true_and_validation_false_are_both_accepted_evaluations_with_exact_ranges()
    {
        using var setup = new Setup();
        var before = setup.Store.CurrentSequence(setup.Session);
        var complete = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run,
            _ => Array.Empty<ExternalCompletionGateResult>(), null);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, complete.Ack.Outcome?.Kind);
        Assert.True(complete.Completed);
        var completedEvents = setup.Store.ReadFrom(setup.Session, before + 1)
            .Where(evt => evt.Causation is CommandCausation cause
                && cause.CommandId.Value.ToString() == complete.Ack.CommandId).ToArray();
        Assert.NotEmpty(completedEvents);
        Assert.Equal(completedEvents.Min(evt => evt.Sequence), complete.Ack.FirstSeq);
        Assert.Equal(completedEvents.Max(evt => evt.Sequence), complete.Ack.LastSeq);
        var runCompleted = Assert.Single(completedEvents, evt => evt.Type.ToString() == "run.completed");
        Assert.Equal(setup.Run, runCompleted.RunId);
        Assert.Equal(setup.Task, runCompleted.TaskId);
        Assert.Equal(setup.Lane, runCompleted.LaneId);

        using var rejectedSetup = new Setup();
        var rejectedEvaluation = rejectedSetup.Server.CheckRunCompletionAndGate(rejectedSetup.Session,
            rejectedSetup.Run, _ => new[] { new ExternalCompletionGateResult("build", false, "build failed") }, null);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, rejectedEvaluation.Ack.Outcome?.Kind);
        Assert.False(rejectedEvaluation.Completed);
        var rejectedEvents = rejectedSetup.Store.ReadFrom(rejectedSetup.Session, 1).Where(evt =>
            evt.Causation is CommandCausation cause
            && cause.CommandId.Value.ToString() == rejectedEvaluation.Ack.CommandId).ToArray();
        Assert.NotEmpty(rejectedEvents);
        Assert.Equal(rejectedEvents.Min(evt => evt.Sequence), rejectedEvaluation.Ack.FirstSeq);
        Assert.Equal(rejectedEvents.Max(evt => evt.Sequence), rejectedEvaluation.Ack.LastSeq);
        Assert.Contains(rejectedEvents, evt => evt.Type.ToString() == "run.validation_rejected");
    }

    [Fact]
    public void Missing_session_or_run_is_rejected_without_invoking_gate_callback()
    {
        using var setup = new Setup();
        var calls = 0;
        var wrongSession = setup.Server.CheckRunCompletionAndGate(SessionId.New(), setup.Run, _ =>
        {
            calls++;
            return Array.Empty<ExternalCompletionGateResult>();
        }, null);
        Assert.Null(wrongSession.Completed);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongSession.Ack.Outcome?.Kind);
        Assert.Null(wrongSession.Ack.FirstSeq);
        Assert.Null(wrongSession.Ack.LastSeq);

        var wrongRun = setup.Server.CheckRunCompletionAndGate(setup.Session, RunId.New(), _ =>
        {
            calls++;
            return Array.Empty<ExternalCompletionGateResult>();
        }, null);
        Assert.Null(wrongRun.Completed);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongRun.Ack.Outcome?.Kind);
        Assert.Null(wrongRun.Ack.FirstSeq);
        Assert.Null(wrongRun.Ack.LastSeq);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Ambient_command_and_execution_scopes_are_preserved_and_not_inherited_by_events()
    {
        using var setup = new Setup();
        var command = new CommandCausation(new CommandId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
        using (CausationScope.Begin(command))
        using (ExecutionScope.Begin(parentExecution))
        {
            var result = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run,
                _ => Array.Empty<ExternalCompletionGateResult>(), null);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
            Assert.Equal(command.CommandId.Value.ToString(), result.Ack.CommandId);
            Assert.Equal(command, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);

            var caused = setup.Store.ReadFrom(setup.Session, 1).Where(evt =>
                evt.Causation is CommandCausation cause && cause.CommandId == command.CommandId).ToArray();
            Assert.NotEmpty(caused);
            Assert.All(caused, evt =>
            {
                Assert.Equal(setup.Run, evt.RunId);
                Assert.NotEqual(parentExecution.RunId, evt.RunId);
            });
            var validationStarted = Assert.Single(caused, evt => evt.Type.ToString() == "run.validation_started");
            Assert.Equal(setup.Task, validationStarted.TaskId);
            Assert.Equal(setup.Lane, validationStarted.LaneId);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Gate_exception_propagates_after_durable_prefix_and_restores_parent_scopes()
    {
        using var setup = new Setup();
        var before = setup.Store.CurrentSequence(setup.Session);
        var cause = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New());
        using (CausationScope.Begin(cause))
        using (ExecutionScope.Begin(parentExecution))
        {
            Assert.Throws<InvalidOperationException>(() => setup.Server.CheckRunCompletionAndGate(
                setup.Session, setup.Run, _ => throw new InvalidOperationException("controlled gate failure"), null));
            Assert.Equal(cause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
            var prefix = setup.Store.ReadFrom(setup.Session, before + 1);
            Assert.Contains(prefix, evt => evt.Type.ToString() == "run.validation_started"
                && evt.Causation is CommandCausation);
            Assert.All(prefix.Where(evt => evt.Causation is CommandCausation), evt =>
            {
                Assert.Equal(setup.Run, evt.RunId);
                Assert.NotEqual(parentExecution.RunId, evt.RunId);
            });
            Assert.DoesNotContain(prefix, evt => evt.Type.ToString() == "run.completed");
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Act_loop_acceptance_response_reenters_completion_boundary()
    {
        using var setup = new Setup();
        var catalog = new FakeCatalog();
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), setup.Root);
        var turn = new ExplorerTurn((_, _) => new ModelResponse(new ContentBlock[] { new TextBlock("done") },
                StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("scripted", "test", null)), executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("scripted", "h", "t", "c", "o", "test"),
            new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
            setup.Store, setup.Codecs, setup.Artifacts, new InMemoryAuditSink(), new RedactionPolicy());
        var runtime = OmniCliRuntime.Create(setup.Root);
        var exit = runtime.RunActLoop(turn, _ => { }, "completion objective", "instructions",
            setup.Session, setup.Run, setup.Lane, "", new WorkspaceGatesYaml { Acceptance = true }, null,
            setup.Server, setup.Artifacts, new InMemoryAuditSink(), null, true, "en",
            TestContext.Current.CancellationToken, acceptanceResponder: _ => "accept");

        Assert.Equal(0, exit);
        var events = setup.Store.ReadFrom(setup.Session, 1);
        Assert.Contains(events, evt => evt.Type.ToString() == "run.completed");
        var validations = events.Where(evt => evt.Type.ToString() == "run.validation_started").ToArray();
        Assert.Equal(2, validations.Length);
        var causes = validations.Select(evt => Assert.IsType<CommandCausation>(evt.Causation).CommandId.Value)
            .ToArray();
        Assert.NotEqual(causes[0], causes[1]);
    }

    private sealed class Setup : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-completion-command-"
            + Guid.NewGuid().ToString("N"));
        public InMemoryEventStore Store { get; } = new();
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public FileArtifactStore Artifacts { get; }
        public OmniServer Server { get; }
        public SessionId Session { get; }
        public RunId Run { get; }
        public LaneId Lane { get; }
        public TaskId Task { get; }

        public Setup()
        {
            Directory.CreateDirectory(Root);
            Artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
            Server = new OmniServer(Store, Codecs, new InMemoryAuditSink(), Artifacts);
            var ack = Server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
                + JsonObj.Field("objective", "completion test") + ","
                + JsonObj.Field("workspace", Root) + "}"), CancellationToken.None);
            Assert.Equal("ok", ack.Status);
            Session = Assert.IsType<SessionId>(Server.LastSessionId());
            Run = Assert.IsType<RunId>(Server.LastRunId());
            Lane = Assert.IsType<LaneId>(Server.LastLaneId());
            Task = Assert.Single(Store.ReadFrom(Session, 1).Select(Codecs.Decode).OfType<RunCreated>()).RootTask;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
