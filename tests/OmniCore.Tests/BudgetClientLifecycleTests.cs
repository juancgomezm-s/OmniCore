using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Security;
using OmniCore.Tools;
using OmniCore.Client;

namespace OmniCore.Tests;

/// <summary>In-process production Host/runtime boundaries; no provider requests or real consumption.</summary>
public sealed class BudgetClientLifecycleTests
{
    [Theory]
    [InlineData("es", "Detener", "Continuar con 10 USD adicionales")]
    [InlineData("en", "Stop", "Continue with an additional 10 USD")]
    public void Production_protocol_and_client_show_budget_detail_and_localized_actions(
        string locale, string stop, string allow)
    {
        var (server, session, run) = Open();
        var id = Request(server, session, run);
        var mapper = new ProtocolMapper(server.AcquireCodecs());
        var projection = new ClientProjection(locale == "es" ? Localization.Spanish() : Localization.English());
        var state = ClientState.Empty();
        foreach (var wire in mapper.Map(server.AcquireStore().ReadFrom(session, 1)))
            state = projection.Apply(state, wire);
        var overlay = Assert.Single(state.Overlays, overlay => overlay.Id == id.ToString());
        Assert.Equal("limit", overlay.Subject);
        Assert.Equal(new[] { stop, allow }, overlay.Options);
        Assert.Equal(new[] { "deny", "allow_plus" }, overlay.OptionIds);
        Assert.Equal("deny", overlay.DefaultOptionId);
        Assert.Equal("ok", server.ResolveBudgetWithoutClient(session, run, id).Status);
        state = ClientState.Empty();
        foreach (var wire in mapper.Map(server.AcquireStore().ReadFrom(session, 1)))
            state = projection.Apply(state, wire);
        Assert.Empty(state.Overlays);
    }

    [Fact]
    public void Actual_explorer_pre_call_cap_is_denied_without_client_before_any_provider_call()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-budget-no-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (server, session, run) = Open();
            var calls = 0;
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Provider must not be invoked at the exhausted cap");
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M5.5"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                server.AcquireStore(), server.AcquireCodecs(), new FileArtifactStore(root),
                new InMemoryAuditSink(), new RedactionPolicy(), pricing: new ModelPricing(1m, 1m),
                enforceDefaultSpendCaps: true, sessionCapUsd: 0m, dailyCapUsd: 100m);
            var execution = server.ExecuteExplorerTurn(session, run,
                token => turn.Ask("test", "system", session, run, server.LastLaneId()!, "", token),
                CancellationToken.None);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, execution.Ack.Outcome?.Kind);
            Assert.Equal(StopReason.Cancelled, execution.Result!.StopReason);
            Assert.Equal(1, OmniCliRuntime.Create(root).HandlePendingBudget(server, session, run, false, _ => { }));
            Assert.Equal(0, calls);
            Assert.Equal(RunState.Failed, RunProjection.Replay(session, run, server.AcquireCodecs(),
                server.AcquireStore().ReadFrom(session, 1)).State);
        }
        finally { Directory.Delete(root, true); }
    }

    private static (OmniServer Server, SessionId Session, RunId Run) Open()
    {
        var server = OmniHost.CreateInMemoryServer();
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"session.input\",\"text\":\"test budget\"}"), CancellationToken.None);
        Assert.Equal("ok", ack.Status);
        return (server, server.LastSessionId()!, server.LastRunId()!);
    }

    private static InteractionId Request(OmniServer server, SessionId session, RunId run,
        InteractionKind kind = InteractionKind.BudgetExceeded)
    {
        var id = InteractionId.New();
        using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: run));
        new EventStream(server.AcquireStore(), server.AcquireCodecs(), session).Append(new InteractionRequested(
            id, kind, BudgetContinuation.Context("limit", new("session", 5m, 5m, null, null)),
            "[{\"id\":\"deny\"},{\"id\":\"allow_plus\",\"value\":10}]", "deny",
            null, null, null, null, 0, 1));
        return id;
    }

    [Fact]
    public void No_client_denial_has_command_causation_exact_range_and_terminal_run()
    {
        var (server, session, run) = Open();
        var interaction = Request(server, session, run);
        var before = server.AcquireStore().CurrentSequence(session);
        var ack = server.ResolveBudgetWithoutClient(session, run, interaction);
        Assert.Equal("ok", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var written = server.AcquireStore().ReadFrom(session, before + 1);
        Assert.NotEmpty(written);
        Assert.Equal(written.First().Sequence, ack.FirstSeq);
        Assert.Equal(written.Last().Sequence, ack.LastSeq);
        var causation = Assert.IsType<CommandCausation>(written.First().Causation);
        Assert.All(written, evt => Assert.Equal(causation, evt.Causation));
        var payloads = written.Select(server.AcquireCodecs().Decode).ToArray();
        var resolution = Assert.Single(payloads.OfType<InteractionResolved>());
        Assert.Equal(interaction, resolution.InteractionId);
        Assert.Equal("deny", resolution.OptionId);
        Assert.Equal(InteractionCause.NoClient, resolution.Cause);
        Assert.Equal("BudgetExceeded", Assert.Single(payloads.OfType<RunFailed>()).Cause);
        Assert.Equal(RunState.Failed, RunProjection.Replay(session, run, server.AcquireCodecs(),
            server.AcquireStore().ReadFrom(session, 1)).State);
        Assert.Equal("error", server.ResolveBudgetWithoutClient(session, run, interaction).Status);
        Assert.Equal(written.Last().Sequence, server.AcquireStore().CurrentSequence(session));
    }

    [Theory]
    [InlineData(false, false, false, 1)]
    [InlineData(true, false, false, 3)]
    [InlineData(false, true, false, 3)]
    [InlineData(false, false, true, 3)]
    public void Runtime_distinguishes_no_client_from_console_and_connected_tui(
        bool connected, bool console, bool tui, int expected)
    {
        var (server, session, run) = Open();
        var interaction = Request(server, session, run);
        var runtime = OmniCliRuntime.Create(Path.GetTempPath());
        runtime.UseConsoleInput = false;
        runtime.HasInteractionClient = connected;
        if (tui) _ = new TuiTurnHost(runtime);
        var diagnostics = new List<string>();
        Assert.Equal(expected, runtime.HandlePendingBudget(server, session, run, console, diagnostics.Add));
        var payloads = server.AcquireStore().ReadFrom(session, 1).Select(server.AcquireCodecs().Decode).ToArray();
        if (expected == 3)
        {
            Assert.Empty(payloads.OfType<InteractionResolved>());
            Assert.Empty(payloads.OfType<RunFailed>());
            Assert.Contains(diagnostics, line => line.Contains("BudgetExceeded") && line.Contains(interaction.ToString()));
        }
        else
        {
            Assert.Equal(InteractionCause.NoClient, Assert.Single(payloads.OfType<InteractionResolved>()).Cause);
            Assert.Single(payloads.OfType<RunFailed>());
            Assert.Null(runtime.HandlePendingBudget(server, session, run, false, diagnostics.Add));
        }
    }

    [Fact]
    public void No_client_command_never_resolves_question_or_foreign_run_interactions()
    {
        var (server, session, run) = Open();
        var question = Request(server, session, run, InteractionKind.Question);
        var foreign = Request(server, session, RunId.New());
        var before = server.AcquireStore().CurrentSequence(session);
        Assert.Equal("error", server.ResolveBudgetWithoutClient(session, run, question).Status);
        Assert.Equal("error", server.ResolveBudgetWithoutClient(session, run, foreign).Status);
        Assert.Equal(before, server.AcquireStore().CurrentSequence(session));
        Assert.Null(OmniCliRuntime.Create(Path.GetTempPath()).HandlePendingBudget(server, session, run, false, _ => { }));
    }
}
