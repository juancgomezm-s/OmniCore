using OmniCore.Abstractions;
using OmniCore.Cli;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Pruebas de integración del cierre de M2 (ADRs 0005/0011/0014/0037/0038/0042):
///  1. Turn end-to-end con el modelo vía StreamAsync + pipeline real de tools.
///  2. Tool-call roundtrip: el modelo recibe resultados de tool en el historial.
///  3. Ask aprobado: ejecuta EXACTAMENTE una vez (INV-002) y emite Granted.
///  4. Timeout de proceso: el árbol muere y Wait devuelve TimedOut sin colgar.
///  5. YAML/auth: `authRef` conserva solo referencias de secretos; models context/maxOutput.
///  6. Context overflow: la política de recorte suelta volátiles, no rinde el WorkingState.
///  7. Secretos: `Secret.ToString()` y los credenciales nunca salen del proceso.
/// </summary>
public sealed class M2IntegrationTests
{
    private static readonly string TestCwd = Path.GetFullPath(".");

    [Fact]
    public async System.Threading.Tasks.Task Explorer_explain_criterion_requires_model_turn_runtime_plan_and_persisted_fingerprint()
    {
        var server = OmniHost.CreateInMemoryServer();
        var objective = "explícame este repositorio";
        var command = WireEnvelope.Command(Ids.NewV7(), "{"
            + JsonObj.Field("cmd", "explore.start") + ","
            + JsonObj.Field("objective", objective) + "}");
        Assert.Equal("ok", server.Send(command, CancellationToken.None).Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var lane = Assert.IsType<LaneId>(server.LastLaneId());

        var tools = OmniHost.CreateExplorerTools();
        var executor = ScriptedToolExecutor.WithCoreTools(tools.Catalog(),
            ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Plan));
        var fingerprint = new ExecutionFingerprint("scripted-explain", "harness", "tools", "context", "overrides", "M2");
        var artifactsPath = Path.Combine(TestCwd, ".omnicore-explain-criterion-artifacts");
        var artifacts = new FileArtifactStore(artifactsPath);
        var turn = new ExplorerTurn((request, token) => FakeResponses.PlanThenEnd(request), executor,
            tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            fingerprint, new ModelSelection(new ModelIdValue("scripted-explain"), 4096, ToolMode.Direct, null),
            server.AcquireStore(), server.AcquireCodecs(), artifacts, new InMemoryAuditSink(), new RedactionPolicy());

        var result = turn.Ask(objective, "Explain the repository. {context}", session, run, lane,
            "runtime-owned-plan", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
        var events = server.AcquireStore().ReadFrom(session, 1);
        var types = events.Select(evt => evt.Type.ToString()).ToArray();
        Assert.Contains("plan_item.started", types);
        Assert.Contains("turn.completed", types);
        var turnStarted = Assert.IsType<TurnStarted>(server.AcquireCodecs().Decode(
            events.Last(evt => evt.Type.ToString() == "turn.started")));
        Assert.NotNull(turnStarted.Fingerprint);
        Assert.Equal(fingerprint.Hash(), turnStarted.Fingerprint!.Hash());
        Assert.NotNull(turnStarted.ContextSnapshotRef);
        Assert.Equal(ArtifactKind.ContextSnapshot, turnStarted.ContextSnapshotRef!.Kind);
        var snapshot = artifacts.GetText(turnStarted.ContextSnapshotRef.Hash);
        Assert.Contains(fingerprint.Hash(), snapshot!);
        Assert.Contains("working-state", snapshot!);

        var approvalId = server.RequestPlanApprovalIfNeeded();
        Assert.NotNull(approvalId);
        var requested = events = server.AcquireStore().ReadFrom(session, 1);
        Assert.Contains(requested, evt => evt.Type.ToString() == "interaction.requested"
            && server.AcquireCodecs().Decode(evt) is InteractionRequested interaction
            && interaction.Kind == InteractionKind.PlanApproval);
        TryDeleteFiles(TestCwd, Path.GetFileName(artifactsPath));
    }

    [Fact]
    public async System.Threading.Tasks.Task Explorer_never_exposes_simulation_write_tools()
    {
        var tools = OmniHost.CreateExplorerTools();
        var names = tools.Catalog().Definitions().Select(tool => tool.Name).ToArray();
        Assert.Contains("filesystem.read", names);
        Assert.Contains("reference.resolve", names);
        Assert.Contains("plan.propose", names);
        Assert.DoesNotContain("fake.write", names);
        Assert.DoesNotContain("fake.read", names);
    }

    [Fact]
    public async System.Threading.Tasks.Task Provisional_model_profile_controls_exposed_tools_and_rejects_hidden_calls()
    {
        var model = new ModelDefinition("small", "local", 8192, 4096, 1024, 4);
        var provider = new ProviderDescriptor("local", ProviderFamily.OpenAiChatCompatible,
            "http://localhost:8080", AuthConfig.None(), false, false, true);
        var profile = new ModelProfileResolver().Resolve(model, provider);
        var harness = new HarnessPolicyResolver().Resolve(profile);
        Assert.Equal(ToolMode.Direct, harness.ToolMode);
        Assert.Equal(6, harness.MaxVisibleTools);
        var overridden = new ModelProfileResolver().Resolve(model, provider,
            new Dictionary<string, double> { ["ToolCallReliability"] = 0.9 });
        Assert.Equal(0.9, overridden.Trait("ToolCallReliability", 0));

        var restricted = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 1,
            GuidanceLevel.DomainOnly, 1, PlanControl.RuntimeDriven, 4);
        var tools = OmniHost.CreateExplorerTools();
        var executor = OmniHost.CreateExplorerExecutor(tools.Catalog(), TestCwd);
        var store = new InMemoryEventStore();
        var sessionId = SessionId.New();
        ToolResultBlock? observed = null;
        var turn = new ExplorerTurn((request, token) => {
                Assert.Single(request.Tools);
                foreach (var message in request.Messages)
                    foreach (var block in message.Content)
                        if (block is ToolResultBlock result) observed = result;
                return FakeResponses.PlanThenEnd(request);
            }, executor, tools.Catalog(),
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("small", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("small"), 4096, ToolMode.Direct, null),
            store, EventCodecs.Create(), new FileArtifactStore(Path.Combine(TestCwd, ".omnicore-profile-art")),
            new InMemoryAuditSink(), new RedactionPolicy(), restricted);

        turn.Ask("pregunta", "sys", sessionId, TestRun.OpenRun(store, sessionId), "ws", CancellationToken.None);
        Assert.NotNull(observed);
        Assert.True(observed!.IsError);
        Assert.Contains("toolcall.rejected", store.ReadFrom(sessionId, 1).Select(e => e.Type.ToString()));
        TryDeleteFiles(TestCwd, ".omnicore-profile-art");
    }

    [Fact]
    public async System.Threading.Tasks.Task Restart_restores_only_the_last_runs_plan()
    {
        var journal = Path.Combine(TestCwd, ".omnicore-restore-" + Guid.NewGuid().ToString("N") + ".db");
        var stateFile = Path.Combine(TestCwd, ".omnicore-last-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var sessionId = SessionId.New();
            var stream = new EventStream(store, codecs, sessionId);
            var first = RunId.New();
            var second = RunId.New();
            foreach (var item in new[] { (first, "FIRST_RUN_ONLY"), (second, "SECOND_RUN_ONLY") })
            {
                var taskId = TaskId.New();
                var planId = PlanId.New();
                var root = PlanItemId.New();
                stream.Append(new RunCreated(item.Item1, sessionId, item.Item2, RunMode.Act,
                    ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
                    new TaskBudget(null, null, null, null), taskId, DateTimeOffset.UtcNow));
                stream.Append(new RunStarted(item.Item1));
                stream.Append(new PlanCreated(planId, item.Item1, root, item.Item2));
                stream.Append(new PlanItemAdded(PlanItemId.New(), planId, item.Item2 + " item", 2,
                    null, Array.Empty<PlanItemId>(), true, new Dictionary<string, string>()));
            }

            File.WriteAllText(stateFile, sessionId + "\n" + second);
            var restored = new OmniServer(new SqliteEventStore(journal), codecs,
                new InMemoryAuditSink(), stateFile);
            var workingState = restored.Query("workingState", CancellationToken.None)?.Json ?? "";
            Assert.Contains("SECOND_RUN_ONLY", workingState);
            Assert.DoesNotContain("FIRST_RUN_ONLY", workingState);
        }
        finally
        {
            TryDelete(journal);
            TryDelete(stateFile);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Journal_redacts_nested_tool_arguments_without_breaking_replay()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var sessionId = SessionId.New();
        var stream = new EventStream(store, codecs, sessionId);
        stream.Append(new ToolCallRequested(ToolCallId.New(), "provider-call", "filesystem.read",
            "{\"path\":\"README.md\",\"authorization\":\"Bearer VerySecretToken123\"}"));

        var evt = store.ReadFrom(sessionId, 1).Single();
        Assert.DoesNotContain("VerySecretToken123", evt.PayloadJson);
        var replayed = (ToolCallRequested)codecs.Decode(evt);
        using var args = System.Text.Json.JsonDocument.Parse(replayed.ArgumentsJson);
        Assert.Equal("README.md", args.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Explorer_applies_plan_propose_via_projection()
    {
        // Requisito 2: plan.propose se aplica DESDE ExplorerTurn con las proyecciones del Run.
        var journal = Path.Combine(TestCwd, ".omnicore-plan-journal-" + Guid.NewGuid().ToString().Substring(0, 8) + ".db");
        if (File.Exists(journal)) File.Delete(journal);
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journal);
        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var executor = ScriptedToolExecutor.WithCoreTools(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act));
        var fingerprint = new ExecutionFingerprint("m", "h", "t", "c", "o", "M2");
        var selection = new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var turn = new ExplorerTurn(
            (request, token) => FakeResponses.PlanThenEnd(request),
            executor, hostTools.Catalog(), materializer, fingerprint, selection,
            store, codecs, new FileArtifactStore(Path.Combine(TestCwd, ".omnicore-plan-artifacts")),
            new InMemoryAuditSink(), new RedactionPolicy());
        // El journal necesita un Run con Plan (P0 como item) para que plan.propose (start P1) aplique.
        var stream = new EventStream(store, codecs, sessionId);
        var rootItem = PlanItemId.New();
        stream.Append(new RunCreated(runId, sessionId, "objetivo", RunMode.Act, ExecutionStrategy.Direct,
            FailurePolicy.BlockDependents, new TaskBudget(null, 1000L, 10, 20), CreatenRootTask(), DateTimeOffset.Now));
        stream.Append(new RunStarted(runId));
        stream.Append(new PlanCreated(PlanId.New(), runId, rootItem, "objetivo"));
        var p1 = PlanItemId.New();
        stream.Append(new PlanItemAdded(p1, PlanId.New(), "P1 inspeccionar", 2, null, new PlanItemId[0], true, new Dictionary<string, string>()));
        stream.Append(new ToolCallRequested(ToolCallId.New(), "pc-1", "plan.propose", "{}"));

        var result = turn.Ask("propón iniciar", "sys {context}", sessionId, runId, "ws", CancellationToken.None);

        var tail = store.ReadFrom(sessionId, 1);
        var types = tail.Select(e => e.Type.ToString()).ToArray();
        Assert.True(types.Contains("plan_item.started"), "plan.propose desde el Explorer aplica PlanItemStarted");
        Assert.True(types.Contains("turn.completed"), "El Turn persiste su cierre");
        TryDelete(journal);
        TryDeleteFiles(TestCwd, ".omnicore-plan-artifacts");
    }

    [Fact]
    public async System.Threading.Tasks.Task Rejected_plan_proposal_is_a_failed_tool_result()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var root = PlanItemId.New();
        var stream = new EventStream(store, codecs, sessionId);
        stream.Append(new RunCreated(runId, sessionId, "objetivo", RunMode.Act,
            ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
            new TaskBudget(null, null, null, null), TaskId.New(), DateTimeOffset.UtcNow));
        stream.Append(new RunStarted(runId));
        stream.Append(new PlanCreated(PlanId.New(), runId, root, "objetivo"));
        stream.Append(new PlanItemStarted(root));

        ToolResultBlock? observed = null;
        var tools = new HostTools(new PathBoundaryValidator(), new PlanService());
        var executor = ScriptedToolExecutor.WithWorkspace(tools.Catalog(),
            ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow), TestCwd);
        var turn = new ExplorerTurn((request, token) => {
                foreach (var message in request.Messages)
                    foreach (var block in message.Content)
                        if (block is ToolResultBlock result) observed = result;
                return FakeResponses.PlanThenEnd(request);
            }, executor, tools.Catalog(),
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("m", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null),
            store, codecs, new FileArtifactStore(Path.Combine(TestCwd, ".omnicore-rejected-plan-art")),
            new InMemoryAuditSink(), new RedactionPolicy());

        turn.Ask("inicia el plan", "sys", sessionId, runId, "ws", CancellationToken.None);
        var types = store.ReadFrom(sessionId, 1).Select(e => e.Type.ToString()).ToArray();
        Assert.Contains("plan_mutation.rejected", types);
        Assert.Contains("toolcall.failed", types);
        Assert.DoesNotContain("toolcall.succeeded", types);
        Assert.NotNull(observed);
        Assert.True(observed!.IsError);
        Assert.Contains("No se puede iniciar", ((TextBlock)observed.Content[0]).Text);
        TryDeleteFiles(TestCwd, ".omnicore-rejected-plan-art");
    }

    private static TaskId CreatenRootTask() => TaskId.New();

    [Fact]
    public async System.Threading.Tasks.Task Filesystem_read_blocks_secret_paths_and_redacts_content()
    {
        // Requisito 3 + P0-2: rutas de secretos (.env, .pem, .key, .ssh/) se REJECT en Prepare
        // (nunca toolcall.succeeded); el archivo normal se lee DENTRO del workspace y su
        // contenido con Bearer llega redactado al tool result.
        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var wsDir = Path.Combine(TestCwd, ".omnicore-secrets-test");
        if (!Directory.Exists(wsDir)) Directory.CreateDirectory(wsDir);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow), wsDir);

        // 1. .env se rechaza en Prepare → Rejected, NUNCA toolcall.succeeded.
        File.WriteAllText(Path.Combine(wsDir, ".env"), "API_KEY=sk-test123secret\nPASSWORD=hunter2");
        var secretCall = new ValidatedToolCall(ToolCallId.New(), new OmniCore.Abstractions.ToolId("filesystem.read"),
            "pc-env", "{\"path\":\".env\"}");
        var outcomeSecret = executor.ExecuteToolWithoutJournal(secretCall, false, CancellationToken.None);
        Assert.True(outcomeSecret.FinalState == ToolCallState.Rejected,
            "Un .env se rechaza (Rejected). summary=" + outcomeSecret.Summary);
        Assert.False(outcomeSecret.Preview is not null && outcomeSecret.Preview!.Length > 0,
            "El .env bloqueado no expone ningún contenido");
        var secretTypes = outcomeSecret.Events.Select(e => e.Type().ToString()).ToArray();
        Assert.True(secretTypes.Contains("toolcall.rejected"),
            "la ruta secreta produce ToolCallRejected (no toolcall.succeeded)");
        Assert.False(secretTypes.Contains("toolcall.succeeded"), "nunca toolcall.succeeded para un secreto");
        File.Delete(Path.Combine(wsDir, ".env"));

        // 2. Archivo normal (dentro del workspace) con Bearer: se lee REAL y se redacta.
        File.WriteAllText(Path.Combine(wsDir, "normal.txt"), "Bearer VOtOkEn123secret contenido normal");
        var okCall = new ValidatedToolCall(ToolCallId.New(), new OmniCore.Abstractions.ToolId("filesystem.read"),
            "pc-ok", "{\"path\":\"normal.txt\"}");
        var outcomeOk = executor.ExecuteToolWithoutJournal(okCall, false, CancellationToken.None);
        Assert.True(outcomeOk.Succeeded, "Un archivo normal dentro del workspace se lee. summary=" + outcomeOk.Summary);
        Assert.True(outcomeOk.Preview is not null, "El content leído vuelve como Preview (no es un fallo silencioso)");
        Assert.True(outcomeOk.Preview!.Contains("contenido normal"), "El contenido del archivo se leyó de verdad");
        Assert.False(outcomeOk.Preview!.Contains("VOtOkEn123secret"), "El tool result va redactado (sin Bearer/keys)");
        File.Delete(Path.Combine(wsDir, "normal.txt"));

        // 3. Archivo inexistente no es un éxito.
        var missingCall = new ValidatedToolCall(ToolCallId.New(), new OmniCore.Abstractions.ToolId("filesystem.read"),
            "pc-missing", "{\"path\":\"no-existe.txt\"}");
        var outcomeMissing = executor.ExecuteToolWithoutJournal(missingCall, false, CancellationToken.None);
        Assert.False(outcomeMissing.Succeeded, "Un archivo inexistente NO se marca como éxito");
        Assert.False(outcomeMissing.Preview is not null && outcomeMissing.Preview!.Length > 0,
            "Un archivo inexistente no expone contenido");
        RemoveDir(wsDir);
    }

    private static bool HasSecretBlocked(OmniCore.Engine.ToolOutcome outcome)
    {
        return outcome.Summary is not null && outcome.Summary!.ToLowerInvariant().Contains("secret");
    }

    private static void RemoveDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Explaine_repo_criterion_renders_with_plain_renderer()
    {
        // Requisito 9 (P0-3): el criterio `omni "explícame este repositorio"` debe funcionar
        // LITERALMENTE — el default del CLI trata el primer argumento no-subcomando como pregunta.
        var captured = CaptureStdout(() =>
        {
            var result = CliApp.RunAsync(new string[] { "explícame este repositorio" }).GetAwaiter().GetResult();
            System.Console.WriteLine("[exit:" + result + "]");
        });

        // El CLI asume la intención como pregunta (no "comando desconocido") y renderiza la
        // simulación + el WorkingState del plan en el plain renderer.
        Assert.False(captured.Contains("comando desconocido"), "El CLI no rechaza la pregunta literal");
        Assert.True(captured.Contains("intención asumida"), "El default es ask con la pregunta");
        Assert.True(captured.Length > 0, "El plain renderer produce salida");
    }

    private static string CaptureStdout(System.Action action)
    {
        var captured = new System.IO.StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(captured);
        try
        {
            action();
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        return captured.ToString();
    }

    [Fact]
    public async System.Threading.Tasks.Task Host_wires_runtime_components()
    {
        // Requisito 8: LocalModelHost, ScopeResolver, FileCredentialStore, tokenizer y Artifact
        // Store están cableados con las factories del Host (no aislados).
        var localHost = OmniHost.CreateLocalModelHost();
        Assert.False(localHost.IsManagedRunning(), "sin servidor managed aún");

        var resolver = OmniHost.CreateScopeResolver();
        Assert.Equal("fallback", resolver.Resolve(OmniCore.Domain.ScopeLevel.User, "clave", "fallback"));

        var creds = OmniHost.CreateCredentialStore(".");
        Assert.True(creds.GetType().Name.Equals("FileCredentialStore", StringComparison.Ordinal),
            "ICredentialStore de M2 es FileCredentialStore");

        var tokenizer = OmniHost.CreateTokenCounter();
        Assert.True(tokenizer.Id.ToString() != "fake:words/1", "tokenizer real (no el Fake de tests)");

        var artifacts = OmniHost.CreateArtifactStore(Path.Combine(TestCwd, ".omnicore-host-artifacts"));
        var refArtifact = artifacts.PutText("Bearer VOtOkEnSecret123", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
        var stored = artifacts.GetText(refArtifact.Hash);
        Assert.True(stored is not null, "GetText devolvió contenido (hash=" + refArtifact.Hash + ")");
        Assert.False(stored!.Contains("VOtOkEnSecret123"), "redacta secretos al persistir");
        TryDeleteFiles(TestCwd, ".omnicore-host-artifacts");
    }

    [Fact]
    public async System.Threading.Tasks.Task Same_session_two_runs_do_not_cross_contaminate()
    {
        // P0-4: con DOS runs en la MISMA session, plan.propose del run2 debe operar sobre el
        // plan del run2 (no el run1), y el budget se lee del run2 (no del primero).
        var journal = Path.Combine(TestCwd, ".omnicore-2runs-" + Guid.NewGuid().ToString().Substring(0, 8) + ".db");
        if (File.Exists(journal)) File.Delete(journal);
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journal);
        var sessionId = SessionId.New();

        var stream = new EventStream(store, codecs, sessionId);
        // Run 1 con budget 1000 tokens.
        var run1 = RunId.New();
        var root1 = PlanItemId.New();
        stream.Append(new RunCreated(run1, sessionId, "run1", RunMode.Act, ExecutionStrategy.Direct,
            FailurePolicy.BlockDependents, new TaskBudget(null, 1000L, 20, 20), TaskId.New(), DateTimeOffset.Now));
        stream.Append(new RunStarted(run1));
        stream.Append(new PlanCreated(PlanId.New(), run1, root1, "objetivo run1"));
        var p1Run1 = PlanItemId.New();
        stream.Append(new PlanItemAdded(p1Run1, PlanId.New(), "R1 item", 2, null, new PlanItemId[0], true, new Dictionary<string, string>()));
        stream.Append(new PlanItemStarted(p1Run1));

        // Run 2 con budget MUY distinto (10 tokens) y su propio plan.
        var run2 = RunId.New();
        var root2 = PlanItemId.New();
        stream.Append(new RunCreated(run2, sessionId, "run2", RunMode.Act, ExecutionStrategy.Direct,
            FailurePolicy.BlockDependents, new TaskBudget(null, 10L, 1, 1), TaskId.New(), DateTimeOffset.Now));
        stream.Append(new RunStarted(run2));
        stream.Append(new PlanCreated(PlanId.New(), run2, root2, "objetivo run2"));
        var p1Run2 = PlanItemId.New();
        stream.Append(new PlanItemAdded(p1Run2, PlanId.New(), "R2 item", 2, null, new PlanItemId[0], true, new Dictionary<string, string>()));

        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow), TestCwd);
        var turn = new ExplorerTurn(
            (request, token) =>
            {
                // El turno de run2 recibe un budget distinto (vía RunCreated del run2).
                return new ModelResponse(new ContentBlock[] { new TextBlock("ok") },
                    StopReason.EndTurn, new TokenUsage(7, 7, 0, 0, 0), null, new ProviderMetadata("", "", null));
            },
            executor, hostTools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]),
            new ExecutionFingerprint("m", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null),
            store, codecs, new FileArtifactStore(Path.Combine(TestCwd, ".omnicore-2runs-art")),
            new InMemoryAuditSink(), new RedactionPolicy());

        var result = turn.Ask("pregunta run2", "sys", sessionId, run2, "ws|R2", CancellationToken.None);

        // El turno de run2 se corta por el budget del run2 (10 tokens, 1 turno).
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.True(result.FinalText is not null && result.FinalText!.Contains("Presupuesto"),
            "El budget del run2 (10 tokens) cortó el turno: " + result.FinalText);
        TryDelete(journal);
        TryDeleteFiles(TestCwd, ".omnicore-2runs-art");
    }

    [Fact]
    public async System.Threading.Tasks.Task Explorer_turn_persists_and_replays_after_restart()
    {
        // Requisito 1: el Turn del Explorer PERSISTE en el journal (turn.started, tool calls/
        // permisos/outcomes, model.completed, turn.completed) y se reelige tras reiniciar.
        var journal = Path.Combine(TestCwd, ".omnicore-turn-journal-" + Guid.NewGuid().ToString().Substring(0, 8) + ".db");
        if (File.Exists(journal)) File.Delete(journal);
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journal);
        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act), TestCwd);
        var fingerprint = new ExecutionFingerprint("test-model", "h", "t", "c", "o", "M2");
        var selection = new ModelSelection(new ModelIdValue("test-model"), 8192, ToolMode.Direct, null);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var artifactPath = Path.Combine(TestCwd, ".omnicore-turn-artifacts");
        var artifacts = new FileArtifactStore(artifactPath);
        var turn = new ExplorerTurn(
            (request, token) => FakeResponses.ToolThenText(request),
            executor, hostTools.Catalog(), materializer, fingerprint, selection,
            store, codecs, artifacts,
            new InMemoryAuditSink(), new RedactionPolicy());
        var sessionId = SessionId.New();
        var runId = TestRun.OpenRun(store, sessionId);
        var fixture = Path.Combine(TestCwd, "fixture.txt");
        File.WriteAllText(fixture, "contenido fixture para el turno");

        var result = turn.Ask("usa la tool", "sys {context}", sessionId, runId, "ws-state", CancellationToken.None);
        File.Delete(fixture);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.True(result.ToolCalls.Count >= 1, "Hubo tool-call en el turno");
        Assert.True(result.ToolCalls[0].Succeeded,
            "La tool del turno ejecutó con éxito. summary=" + result.ToolCalls[0].Summary);

        // Replay tras "reinicio": abrir el mismo journal y REPRODUCIR con la state machine de
        // ToolCall (P0-1): cada evento aplica una transición válida (Requested → Prepared →
        // PermissionEvaluated → Authorized → Started → Succeeded), sin lanzar InvalidTransition.
        var store2 = new SqliteEventStore(journal);
        var tail = store2.ReadFrom(sessionId, 1);
        var types = tail.Select(e => e.Type.ToString()).ToArray();
        Assert.True(types.Contains("turn.started"), "turn.started? types=" + string.Join(",", types));
        var startedEvent = tail.First(evt => evt.Type.ToString() == "turn.started");
        var startedPayload = Assert.IsType<TurnStarted>(codecs.Decode(startedEvent));
        Assert.NotNull(startedPayload.Fingerprint);
        Assert.Equal(fingerprint.Hash(), startedPayload.Fingerprint!.Hash());
        Assert.Equal(startedPayload.Fingerprint.Hash(), Assert.IsType<TurnStarted>(codecs.Decode(startedEvent)).Fingerprint!.Hash());
        Assert.NotNull(startedPayload.ContextSnapshotRef);
        Assert.Equal(ArtifactKind.ContextSnapshot, startedPayload.ContextSnapshotRef!.Kind);
        var snapshotJson = artifacts.GetText(startedPayload.ContextSnapshotRef.Hash);
        Assert.NotNull(snapshotJson);
        Assert.Contains("working-state", snapshotJson!);
        Assert.Contains("session-conversation", snapshotJson!);
        Assert.True(types.Contains("turn.completed"),  "turn.completed? types=" + string.Join(",", types));
        Assert.True(types.Contains("toolcall.requested"), "requested? types=" + string.Join(",", types));
        Assert.True(types.Contains("toolcall.prepared"), "prepared? types=" + string.Join(",", types));
        Assert.True(types.Contains("toolcall.permission_evaluated"), "perm_eval? types=" + string.Join(",", types));
        Assert.True(types.Contains("toolcall.succeeded"), "succeeded? types=" + string.Join(",", types));
        Assert.True(types.Contains("model.completed"), "model? types=" + string.Join(",", types));
        Assert.True(result.ResponseArtifactId is not null, "La respuesta se guardó como artifact");

        // Validar la transición de CADA toolcall del journal reabierto contra la state machine.
        var toolcalls = new List<OmniCore.Domain.ToolCallId>();
        foreach (var evt in tail)
        {
            if (evt.Type.ToString() == "toolcall.requested") toolcalls.Add(evt.ToolCallId!);
        }

        Assert.True(toolcalls.Count >= 1, "Hay al menos una toolcall en el journal");
        foreach (var tcId in toolcalls)
        {
            Assert.True(ReplayToolCallValid(codecs, tail, tcId),
                "La toolcall " + tcId + " se reproduce válidamente por la state machine");
        }

        TryDelete(journal);
        TryDeleteFiles(TestCwd, Path.GetFileName(artifactPath));
    }

    [Fact]
    public void Explorer_reconstructs_previous_user_and_assistant_messages_from_journal()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var artifactPath = Path.Combine(TestCwd, ".omnicore-conversation-" + Guid.NewGuid().ToString("N"));
        var artifacts = new FileArtifactStore(artifactPath);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act), TestCwd);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var fingerprint = new ExecutionFingerprint("test-model", "h", "t", "c", "o", "M2");
        var selection = new ModelSelection(new ModelIdValue("test-model"), 8192, ToolMode.Direct, null);
        var session = SessionId.New();
        var opened = TestRun.Open(store, session);
        var run = opened.RunId;
        var lane = opened.RootLane;

        var first = new ExplorerTurn((request, token) =>
            new ModelResponse(new ContentBlock[] { new TextBlock("respuesta anterior") },
                StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("", "", null)),
            executor, hostTools.Catalog(), materializer, fingerprint, selection,
            store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy());
        Assert.Equal(StopReason.EndTurn,
            first.Ask("pregunta anterior", "sys", session, run, lane, "", CancellationToken.None).StopReason);

        var seen = new List<string>();
        var second = new ExplorerTurn((request, token) =>
        {
            foreach (var message in request.Messages)
                foreach (var part in message.Content)
                    if (part is TextBlock text) seen.Add(text.Text);
            return new ModelResponse(new ContentBlock[] { new TextBlock("respuesta nueva") },
                StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("", "", null));
        }, executor, hostTools.Catalog(), materializer, fingerprint, selection,
            store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy());
        Assert.Equal(StopReason.EndTurn,
            second.Ask("pregunta nueva", "sys", session, run, lane, "", CancellationToken.None).StopReason);
        Assert.Equal(new[] { "pregunta anterior", "respuesta anterior", "pregunta nueva" }, seen);

        var eventTypes = store.ReadFrom(session, 1).Select(evt => evt.Type.ToString()).ToArray();
        Assert.Equal(2, eventTypes.Count(type => type == "user_input.received"));
        Assert.Equal(2, eventTypes.Count(type => type == "model.completed"));
        TryDeleteFiles(TestCwd, System.IO.Path.GetFileName(artifactPath));
    }

    [Fact]
    public void Explorer_exhausted_steps_abandons_turn_without_completed_event()
    {
        var store = new InMemoryEventStore();
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act), TestCwd);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var session = SessionId.New();
        var turn = new ExplorerTurn((request, token) =>
            new ModelResponse(new ContentBlock[] {
                new ToolCallBlock(ToolCallId.New(), "repeated", "unknown.tool", "{}")
            }, StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("", "", null)),
            executor, hostTools.Catalog(), materializer,
            new ExecutionFingerprint("test-model", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("test-model"), 8192, ToolMode.Direct, null),
            store, EventCodecs.Create(), new FileArtifactStore(Path.Combine(TestCwd, ".omnicore-step-artifacts")),
            new InMemoryAuditSink(), new RedactionPolicy());

        var result = turn.Ask("pregunta", "sys", session, TestRun.OpenRun(store, session), LaneId.New(), "",
            CancellationToken.None);
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Equal(ExplorerTurn.MaxSteps, result.Steps);
        var types = store.ReadFrom(session, 1).Select(evt => evt.Type.ToString()).ToArray();
        Assert.Contains("turn.abandoned", types);
        Assert.DoesNotContain("turn.completed", types);
        Assert.DoesNotContain("model.completed", types);
        TryDeleteFiles(TestCwd, ".omnicore-step-artifacts");
    }

    [Fact]
    public void Explorer_user_input_replays_as_valid_run_transitions()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var lane = LaneId.New();
        var stream = new EventStream(store, codecs, session);
        stream.Append(new RunCreated(run, session, "objetivo", RunMode.Act,
            ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
            new TaskBudget(null, null, null, null), TaskId.New(), DateTimeOffset.UtcNow));
        stream.Append(new RunStarted(run));

        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow), TestCwd);
        var turn = new ExplorerTurn((request, token) =>
            new ModelResponse(new ContentBlock[] { new TextBlock("ok") },
                StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("", "", null)),
            executor, hostTools.Catalog(),
            new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]),
            new ExecutionFingerprint("m", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null),
            store, codecs, new FileArtifactStore(Path.Combine(TestCwd, ".omnicore-run-artifacts")),
            new InMemoryAuditSink(), new RedactionPolicy());

        Assert.Equal(StopReason.EndTurn,
            turn.Ask("primera", "sys", session, run, lane, "", CancellationToken.None).StopReason);
        Assert.Equal(StopReason.EndTurn,
            turn.Ask("segunda", "sys", session, run, lane, "", CancellationToken.None).StopReason);

        var state = RunState.Created;
        var lifecycle = new List<string>();
        foreach (var evt in store.ReadFrom(session, 1))
        {
            var type = evt.Type.ToString();
            if (type != "run.created" && type != "run.started" && type != "run.awaiting_input"
                && type != "user_input.received") continue;
            lifecycle.Add(type);
            state = StateMachines.ApplyRun(state, codecs.Decode(evt));
        }
        Assert.Equal(RunState.Running, state);
        Assert.Equal(new[] { "run.created", "run.started", "run.awaiting_input", "user_input.received",
            "run.awaiting_input", "user_input.received" }, lifecycle);
        TryDeleteFiles(TestCwd, ".omnicore-run-artifacts");
    }

    /// <summary>
    /// Reproduce una ToolCall desde su Requested aplicando la state machine (StateMachines.
    /// ApplyToolCall) sobre los eventos del journal; devuelve true si nunca lanza transición
    /// inválida y termina en Succeeded/Failed/Rejected.
    /// </summary>
    private static bool ReplayToolCallValid(OmniCore.Abstractions.IEventCodecRegistry codecs,
        IReadOnlyList<OmniCore.Domain.DomainEvent> tail, OmniCore.Domain.ToolCallId id)
    {
        var state = OmniCore.Domain.ToolCallState.Requested;
        var started = false;
        foreach (var evt in tail)
        {
            if (evt.ToolCallId is null || evt.ToolCallId!.ToString() != id.ToString())
            {
                continue;
            }

            if (evt.Type.ToString() == "toolcall.requested") started = true;
            var payload = codecs.Decode(evt);
            if (!started) continue;
            try
            {
                state = OmniCore.Domain.StateMachines.ApplyToolCall(state, payload);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return state == OmniCore.Domain.ToolCallState.Succeeded
            || state == OmniCore.Domain.ToolCallState.Failed
            || state == OmniCore.Domain.ToolCallState.Rejected
            || state == OmniCore.Domain.ToolCallState.Reconciled;
    }

    private static void TryDeleteFiles(string dir, string prefix)
    {
        try
        {
            var full = Path.Combine(dir, prefix);
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Turn_end_to_end_executes_tool_and_returns_final_text()
    {
        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act), TestCwd);
        var fingerprint = new ExecutionFingerprint("test-model", "h", "t", "c", "o", "M2");
        var selection = new ModelSelection(new ModelIdValue("test-model"), 8192, ToolMode.Direct, null);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var recorded = new List<ModelRequest>();
        var turn = new ExplorerTurn(
            (request, token) =>
            {
                recorded.Add(request);
                return FakeResponses.ToolThenText(request);
            },
            executor, hostTools.Catalog(), materializer, fingerprint, selection);
        var sessionId = SessionId.New();
        var runId = TestRun.OpenRun(turn.JournalStore, sessionId);
        var fixture = Path.Combine(TestCwd, "fixture.txt");
        File.WriteAllText(fixture, "contenido fixture del turno end-to-end");

        var result = turn.Ask("usa la tool y explica", "instruccion {context}", sessionId, runId, "ws-state",
            CancellationToken.None);
        File.Delete(fixture);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.True(result.Steps >= 2, "Hubo paso de tool antes del final");
        Assert.True(result.ToolCalls.Count >= 1, "El modelo llamó una tool");
        Assert.True(result.ToolCalls[0].Succeeded, "La tool se ejecutó con éxito");
        Assert.True(result.ToolCalls[0].ToolName == "filesystem.read", "Tool del catálogo real");
        Assert.True(result.FinalText is not null && result.FinalText!.Length > 0, "Hay texto final");
        Assert.True(recorded.Count >= 2, "Al menos 2 llamadas (tool → resultado → final)");
    }

    [Fact]
    public async System.Threading.Tasks.Task Turn_injects_tool_result_in_history_between_calls()
    {
        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act), TestCwd);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var recorded = new List<ModelRequest>();
        var turn = new ExplorerTurn((request, token) =>
        {
            recorded.Add(request);
            return FakeResponses.ToolThenText(request);
        }, executor, hostTools.Catalog(), materializer,
            new ExecutionFingerprint("m", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null));
        var fixture = Path.Combine(TestCwd, "fixture.txt");
        File.WriteAllText(fixture, "fixture turno historial");

        var askSession = SessionId.New();
        turn.Ask("pregunta", "sys", askSession, TestRun.OpenRun(turn.JournalStore, askSession), "", CancellationToken.None);
        File.Delete(fixture);

        var second = recorded[1];
        var roles = second.Messages.Select(m => m.Role).ToArray();
        Assert.Contains(MessageRole.Tool, roles);
        var hasAssistantCall = false;
        foreach (ModelMessage m in second.Messages)
        {
            foreach (ContentBlock b in m.Content)
            {
                if (m.Role == MessageRole.Assistant && b is ToolCallBlock) hasAssistantCall = true;
            }
        }

        Assert.True(hasAssistantCall, "Historial lleva el assistant tool_calls");
    }

    [Fact]
    public async System.Threading.Tasks.Task Ask_approved_executes_exactly_once()
    {
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Ask);
        var catalog = FakeCatalog.Default();
        var events = new List<DomainEventPayload>();
        var runtime = ToolRuntime.For(catalog, policy, payload =>
        {
            events.Add(payload);
            return VoidBox.Instance;
        });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("fake.write"), "pc-1", "{}");

        var outcome = runtime.Run(call, new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), true, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El Ask aprobado ejecuta la tool");
        var types = events.Select(e => e.Type().ToString()).ToArray();
        Assert.Equal(1, Count(types, "toolcall.succeeded"));
        Assert.Equal(0, Count(types, "toolcall.permission_denied"));
        Assert.True(Count(types, "toolcall.permission_granted") >= 1);
    }

    [Fact]
    public async System.Threading.Tasks.Task Ask_denied_when_no_interactive_client()
    {
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Ask);
        var events = new List<DomainEventPayload>();
        var runtime = ToolRuntime.For(FakeCatalog.Default(), policy, payload =>
        {
            events.Add(payload);
            return VoidBox.Instance;
        });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("fake.write"), "pc-2", "{}");

        var outcome = runtime.Run(call, new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        var types = events.Select(e => e.Type().ToString()).ToArray();
        Assert.Contains("toolcall.permission_denied", types);
        Assert.Equal(0, Count(types, "toolcall.succeeded"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Process_timeout_return_fast_and_kill_tree()
    {
        var runtime = SystemProcessRuntime.Instance();
        // Proceso que vive +30s y genera salida abundante en stdout (para probar el drenaje
        // concurrente: en la versión anterior Wait() bloqueaba el ReadToEnd hasta la muerte).
        var launch = new ProcessLaunch("cmd.exe", ["/c", "for /l %i in (1,1,200) do @echo fila-%i-de-salida-larga", "&",
            "ping", "-n", "40", "127.0.0.1", ">", "NUL"], "",
            new Dictionary<string, string>(), true);
        var handle = runtime.Launch(launch, CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = runtime.Wait(handle, System.TimeSpan.FromSeconds(1), CancellationToken.None);
        sw.Stop();

        Assert.True(result.TimedOut, "El timeout se respeta");
        Assert.True(sw.ElapsedMilliseconds < 3000, "Timeout de 1s responde en <3s (real: " + sw.ElapsedMilliseconds + "ms)");
        Assert.False(runtime.IsAlive(handle), "El árbol quedó matado después del timeout");
    }

    [Fact]
    public async System.Threading.Tasks.Task Process_wait_captures_abundant_output()
    {
        var runtime = SystemProcessRuntime.Instance();
        // Salida abundante con proceso rápido: el drenaje concurrente la captura entera.
        var launch = new ProcessLaunch("cmd.exe", ["/c", "for /l %i in (1,1,500) do @echo linea-%i"], "",
            new Dictionary<string, string>(), true);
        var handle = runtime.Launch(launch, CancellationToken.None);

        var result = runtime.Wait(handle, System.TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.False(result.TimedOut, "Proceso rápido termina sin timeout");
        Assert.True(result.Stdout is not null, "stdout capturado");
        Assert.True(result.Stdout!.Length > 2000, "Salida abundante capturada entera (" + result.Stdout!.Length + " chars)");
    }

    [Fact]
    public async System.Threading.Tasks.Task ConfigLoader_respects_auth_and_model_facts()
    {
        var providers = "providers:\n"
            + "  local: { baseUrl: http://127.0.0.1:8080 }\n"
            + "  openrouter: { baseUrl: https://openrouter.ai/api/v1, authRef: openrouter-key }\n";
        var models = "models:\n"
            + "  qwen-27b: { provider: local, context: 32768, maxOutput: 8192 }\n";
        var loader = new ConfigLoader();
        var registry = loader.BuildRegistry(providers, models);

        var local = registry.Provider("local");
        var openrouter = registry.Provider("openrouter");
        Assert.True(local!.Auth.Kind == AuthKind.None, "provider sin authRef no requiere API key");
        Assert.True(openrouter!.Auth.Kind == AuthKind.ApiKey, "authRef es una referencia a API key");
        Assert.Equal("openrouter-key", openrouter!.Auth.SecretRef);

        var qwen = registry.Model("qwen-27b");
        Assert.True(qwen!.ContextWindow == 32768, "context se respeta (no 8192 fijo)");
        Assert.True(qwen!.MaxOutputTokens == 8192, "maxOutput se respeta (no 2048 fijo)");
    }

    [Fact]
    public async System.Threading.Tasks.Task Context_overlow_pinned_greater_than_budget_flows()
    {
        // WorkingState protegido permanece íntegro incluso cuando su coste excede el límite.
        var counter = new FakeTokenCounter();
        var big = new string[1] { "" };
        var hugeWorkingState = new string[1000];
        for (var i = 0; i < hugeWorkingState.Length; i++)
        {
            hugeWorkingState[i] = "working state largo con contenido del plan ";
        }

        big[0] = string.Join("", hugeWorkingState);
        var contributors = new IContextContributor[] {
            new WorkingStateContributor(big[0]),
        };
        var materializer = new ContextMaterializer(counter, contributors);
        var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 1,
            new ExecutionFingerprint("k", "h", "t", "c", "o", "M2"));

        // Presupuesto menor que el WorkingState protegido, que permanece íntegro.
        var snapshot = materializer.MaterializeWithinBudget(request, CancellationToken.None, 10);

        Assert.True(snapshot.Overflowed, "WorkingState protegido mayor al presupuesto → ContextOverflow");
        Assert.True(snapshot.TokenCount > 10, "El contenido protegido no se trunca para fingir que cabe");
        Assert.Equal(big[0], snapshot.Items.Single().Content);
    }

    [Fact]
    public void Context_budget_trims_oldest_conversation_first_and_keeps_working_state_last()
    {
        var entries = new[]
        {
            new ConversationContextEntry("conversation-0", ContextItemKind.UserMessage, "first run input", true),
            new ConversationContextEntry("conversation-1", ContextItemKind.UserMessage, "oldest old input"),
            new ConversationContextEntry("conversation-2", ContextItemKind.AssistantMessage, "old assistant answer"),
            new ConversationContextEntry("conversation-3", ContextItemKind.ToolResult, "recent tool result"),
            new ConversationContextEntry("conversation-4", ContextItemKind.UserMessage, "current input"),
        };
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[] {
            new SystemPromptContributor("system"),
            new SessionConversationContributor(entries),
            new WorkingStateContributor("working state"),
        });
        var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 1,
            new ExecutionFingerprint("model", "harness", "tools", "context", "overrides", "build"));

        // Coste total 17; al límite 11 se omiten los dos items conversacionales más antiguos.
        var snapshot = materializer.MaterializeWithinBudget(request, CancellationToken.None, 11);

        Assert.False(snapshot.Overflowed);
        Assert.True(snapshot.TokenCount <= 11);
        Assert.Equal("working-state", snapshot.Items[^1].Id);
        Assert.Contains(snapshot.Items, item => item.Id == "system-prompt");
        Assert.Contains(snapshot.Items, item => item.Id == "conversation-0");
        Assert.DoesNotContain(snapshot.Items, item => item.Id == "conversation-1");
        Assert.DoesNotContain(snapshot.Items, item => item.Id == "conversation-2");
        Assert.Contains(snapshot.Items, item => item.Id == "conversation-3");
        var omissions = snapshot.Diagnostics.Where(d => d.Decision == ContextDecision.OmittedByBudget).ToArray();
        Assert.Equal(new[] { "conversation-1", "conversation-2" }, omissions.Select(d => d.ItemId));
        Assert.All(omissions, diagnostic =>
        {
            Assert.Equal(ContributionCategory.Conversation, diagnostic.Provenance.Category);
            Assert.Equal("session-conversation", diagnostic.Provenance.ContributorId);
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task Context_overflow_policy_keeps_working_state_pinned()
    {
        var counter = new FakeTokenCounter();
        var contributors = new IContextContributor[] { new BigVolatileContributor(),
            new WorkingStateContributor("WorkingState del plan actual — contenido crítico del turno") };
        var materializer = new ContextMaterializer(counter, contributors);
        var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 1,
            new ExecutionFingerprint("k", "h", "t", "c", "o", "M2"));

        var snapshot = materializer.MaterializeWithinBudget(request, CancellationToken.None, 200);

        Assert.True(snapshot.TokenCount <= 200, "El presupuesto se respeta (TokenCount " + snapshot.TokenCount + ")");
        var workingCount = 0;
        foreach (ContextItem item in snapshot.Items)
        {
            if (item.Kind == ContextItemKind.WorkingState) workingCount += 1;
        }

        Assert.True(workingCount == 1, "WorkingState presente exactamente una vez (sin duplicados)");
        Assert.True(snapshot.Items.Count >= 1, "El WorkingState permanece (es pinned) bajo overflow");
    }

    [Fact]
    public async System.Threading.Tasks.Task Secrets_never_serialize_and_store_is_isolated()
    {
        var secret = Secret.Of("supersecreto-abc");
        Assert.Equal("***", secret.ToString());

        var path = Path.Combine(TestCwd, ".omnicore-test-creds.ini");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var store = new FileCredentialStore(path);
        store.Save("local-qwen-key", secret.Value(), CancellationToken.None);
        var loaded = store.Load("local-qwen-key", CancellationToken.None);
        Assert.Equal("supersecreto-abc", loaded);

        // P0-7: el archivo en disco NO contiene el secreto en texto plano (está ofuscado).
        var raw = File.ReadAllText(path);
        Assert.False(raw.Contains("supersecreto-abc"),
            "El secreto nunca se escribe en texto plano en el archivo de credenciales");
        Assert.True(raw.StartsWith("local-qwen-key=enc:"), "El valor se almacena ofuscado (enc:...)");

        store.Delete("local-qwen-key", CancellationToken.None);
        Assert.True(store.Load("local-qwen-key", CancellationToken.None) is null, "revocación: se elimina");
        File.Delete(path);
    }

    [Fact]
    public async System.Threading.Tasks.Task Server_restores_context_between_processes()
    {
        // P0-3: una nueva invocación recupera el snapshot del journal sin re-ejecutar sim.
        var stamp = OmniserverNewStamp();
        var journal = Path.Combine(TestCwd, ".omnicore-journal-restore-" + stamp + ".db");
        var stateFile = Path.Combine(TestCwd, "lastsession-restore-" + stamp + ".txt");
        if (File.Exists(journal)) File.Delete(journal);
        if (File.Exists(stateFile)) File.Delete(stateFile);

        // Proceso 1: ejecuta el sim y persiste journal + lastsession.
        var server1 = OmniserverPersistent(journal, stateFile);
        server1.Send(WireEnvelope.Command(Ids.NewV7(), "{" + OmniCore.Protocol.JsonObj.Field("cmd", "sim")
            + "," + OmniCore.Protocol.JsonObj.Field("scenario", "multi-item-plan") + "}"), CancellationToken.None);
        var ctx1 = server1.Query("workingState", CancellationToken.None);
        Assert.True(ctx1 is not null && ctx1!.Json.Contains("workingState"), "El primer proceso materializó el contexto");

        // Proceso 2: abre el mismo journal — LoadLastSession debe reconstruir el snapshot.
        var server2 = OmniserverPersistent(journal, stateFile);
        var ctx2 = server2.Query("workingState", CancellationToken.None);
        Assert.True(ctx2 is not null && ctx2!.Json.Contains("workingState") && !ctx2!.Json.Contains("{}"),
            "El segundo proceso restauró el snapshot. ctx2=" + (ctx2 is null ? "null" : ctx2!.Json));

        TryDelete(journal);
        TryDelete(stateFile);
    }

    private static string OmniserverNewStamp() => Guid.NewGuid().ToString().Substring(0, 8);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // el store mantiene el handle; la limpieza es best-effort
        }
    }

    private static OmniServer OmniserverPersistent(string journal, string stateFile)
    {
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journal);
        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile);
        return server;
    }

    [Fact]
    public async System.Threading.Tasks.Task Chained_scope_resolver_most_specific_wins()
    {
        // ADR-0022 §25: la capa más específica que define la clave gana; fallback si ninguna.
        var lookup = new Dictionary<ScopeLevel, Dictionary<string, string>>();
        lookup[ScopeLevel.BuiltIn] = new Dictionary<string, string> { ["model"] = "builtin-model" };
        lookup[ScopeLevel.User] = new Dictionary<string, string> { ["model"] = "user-model" };
        var resolver = new ScopeResolver<string>((scope, key) =>
        {
            var layer = lookup.TryGetValue(scope, out var v) ? v : null;
            return layer is null ? null : layer!.TryGetValue(key, out var val) ? val : null;
        });

        Assert.Equal("user-model", resolver.Resolve(ScopeLevel.User, "model", "fallback"));
        Assert.Equal("user-model", resolver.Resolve(ScopeLevel.Run, "model", "fallback"));
        Assert.Equal("fallback", resolver.Resolve(ScopeLevel.Run, "missing", "fallback"));
        Assert.Equal(ScopeLevel.User, resolver.SourceOf("model"));
    }

    [Fact]
    public async System.Threading.Tasks.Task PathBoundary_rejects_symlink_escape_intermediate()
    {
        // P1-12 + Req7: un symlink de DIRECTORIO intermediario que apunta fuera del workspace
        // se rechaza. La prueba es OBLIGATORIA: si el entorno no puede crear el enlace, falla.
        var baseDir = Path.Combine(TestCwd, ".omnicore-boundary-" + Guid.NewGuid().ToString().Substring(0, 8));
        var ws = Path.Combine(baseDir, "ws");
        var outside = Path.Combine(baseDir, "outside");
        if (!Directory.Exists(ws)) Directory.CreateDirectory(ws);
        if (!Directory.Exists(outside)) Directory.CreateDirectory(outside);
        var link = Path.Combine(ws, "escape");

        // Creación OBLIGATORIA del enlace (requisito 7: no hay skip). Primero symlink; si el
        // entorno lo rechaza (sin Developer Mode), se crea un JUNCTION de directorio vía
        // mklink /J (no requiere privilegios en Windows). Si ambos fallan, la prueba falla.
        var created = TryCreateDirectoryLink(link, outside);
        Assert.True(created, "No se pudo crear symlink/junction para verificar la frontera (requisito 7). link="
            + link + " exists=" + Directory.Exists(link));

        try
        {
            var v = new PathBoundaryValidator();
            var insideFile = Path.Combine(link, "secret.txt");
            Assert.False(v.IsWithin(insideFile, ws),
                "Un symlink de directorio intermedio que escapa NO debe considerarse dentro. insideFile="
                + insideFile + " ws=" + ws + " result=" + v.IsWithin(insideFile, ws));
        }
        finally
        {
            RemoveDirWithLinks(baseDir);
        }
    }

    private static void RemoveDirWithLinks(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var d in Directory.GetDirectories(dir))
            {
                RemoveDirWithLinks(d);
            }

            foreach (var f in Directory.GetFiles(dir))
            {
                try
                {
                    File.Delete(f);
                }
                catch (Exception)
                {
                }
            }

            Directory.Delete(dir);
        }
        catch (Exception)
        {
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception)
        {
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            var p = System.Diagnostics.Process.Start(psi);
            p!.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task PathBoundary_posix_and_unc_cases()
    {
        // Requisito 7: cobertura POSIX y UNC (comportamiento sintético sin disco).
        var posix = PathBoundaryValidator.Posix();
        Assert.True(posix.IsWithin("/ws/a.cs", "/ws"), "posix dentro");
        Assert.False(posix.IsWithin("/etc/passwd", "/ws"), "posix fuera");
        Assert.False(posix.IsWithin("/ws/../x", "/ws"), "posix traversal");
        Assert.False(posix.IsSafeRelative("/abs/rel"), "posix absoluto nunca es relative");
        Assert.True(posix.IsSafeRelative("src/a.cs"), "posix relativa válida");

        // UNC: \\server\share — una ruta fuera del propio workspace no debe pasar.
        var v = new PathBoundaryValidator();
        Assert.False(v.IsSafeRelative("\\\\server\\share\\x"), "UNC no es una relative válida");
        Assert.True(v.IsSafeRelative("src/x.cs"), "relative Windows válida");
        Assert.False(v.IsSafeRelative("C:\\Windows\\x"), "absoluta Windows nunca es relative");
    }

    [Fact]
    public async System.Threading.Tasks.Task Turn_exposes_real_file_content_to_model()
    {
        // P0-5: el contenido que filesystem.read lee (Preview) vuelve al modelo, no solo el summary.
        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act), TestCwd);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var captured = new List<string>();
        var turn = new ExplorerTurn((request, token) =>
        {
            // La segunda llamada (tras la tool) debe llevar en el historial el contenido real.
            foreach (ModelMessage m in request.Messages)
            {
                foreach (ContentBlock b in m.Content)
                {
                    if (b is ToolResultBlock tr)
                    {
                        foreach (ContentBlock inner in tr.Content)
                        {
                            if (inner is TextBlock t) captured.Add(t.Text);
                        }
                    }
                }
            }

            return FakeResponses.ToolThenText(request);
        }, executor, hostTools.Catalog(), materializer,
            new ExecutionFingerprint("m", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null));
        File.WriteAllText(Path.Combine(TestCwd, "fixture.txt"), "contenido real del archivo del turno");

        var askSession = SessionId.New();
        turn.Ask("usa filesystem.read", "sys", askSession, TestRun.OpenRun(turn.JournalStore, askSession), "",
            CancellationToken.None);

        var forwarded = string.Join(" ", captured.ToArray());
        Assert.False(forwarded.Contains("Ruta fuera del workspace"), "El archivo se lee dentro del workspace");
        Assert.True(forwarded.Length > 5, "El contenido REAL del archivo se propagó al modelo: {" + forwarded + "}");
        File.Delete(Path.Combine(TestCwd, "fixture.txt"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Spend_guard_enforces_turn_and_tool_limits()
    {
        var budget = new TaskBudget(null, 1000L, 3, 2);
        var guard = new SpendGuard(budget);

        guard.AdvanceTurn(100);
        guard.RecordToolCall();
        guard.AdvanceTurn(100);
        guard.RecordToolCall();

        var raised = false;
        try
        {
            guard.RecordToolCall();
        }
        catch (BudgetExceededException ex)
        {
            raised = true;
            Assert.True(ex.Detail.Contains("tool calls"), "tope de tool calls: " + ex.Detail);
        }

        Assert.True(raised, "El tercer tool call excede el presupuesto");
        Assert.True(guard.Tokens() == 200, "Tokens acumulados correctos");
    }

    private static int Count(IReadOnlyList<string> items, string value)
    {
        var n = 0;
        foreach (var item in items)
        {
            if (item.Equals(value, StringComparison.Ordinal))
            {
                n += 1;
            }
        }

        return n;
    }
}

/// <summary>Respuestas fake: primero una tool call; después el texto final.</summary>
public sealed class FakeResponses
{
    public static ModelResponse PlanThenEnd(ModelRequest request)
    {
        if (!HasToolResult(request))
        {
            var callId = ToolCallId.New();
            return new ModelResponse(new ContentBlock[] {
                new ToolCallBlock(callId, "call_plan", "plan.propose", "{\"kind\":\"start\",\"itemId\":\"P1\"}"),
            }, StopReason.ToolUse, new TokenUsage(10, 5, 0, 0, 0), null,
                new ProviderMetadata("", "", null));
        }

        return new ModelResponse(new ContentBlock[] { new TextBlock("Plan iniciado.") },
            StopReason.EndTurn, new TokenUsage(20, 10, 0, 0, 0), null, new ProviderMetadata("", "", null));
    }

    public static ModelResponse ToolThenText(ModelRequest request)
    {
        if (!HasToolResult(request))
        {
            var callId = ToolCallId.New();
            return new ModelResponse(new ContentBlock[] {
                new ToolCallBlock(callId, "call_test", "filesystem.read", "{\"path\":\"fixture.txt\"}"),
            }, StopReason.ToolUse, new TokenUsage(10, 5, 0, 0, 0), null,
                new ProviderMetadata("", "", null));
        }

        return new ModelResponse(new ContentBlock[] { new TextBlock("Terminado: tool ok") },
            StopReason.EndTurn, new TokenUsage(20, 10, 0, 0, 0), null, new ProviderMetadata("", "", null));
    }

    private static bool HasToolResult(ModelRequest request)
    {
        foreach (ModelMessage msg in request.Messages)
        {
            foreach (ContentBlock b in msg.Content)
            {
                if (msg.Role == MessageRole.Tool && b is ToolResultBlock) return true;
            }
        }

        return false;
    }
}

/// <summary>Aporta un item volátil grande para forzar overflow.</summary>
public sealed class BigVolatileContributor : IContextContributor
{
    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        var prov = new ContextProvenance("big", ContributionCategory.ToolObservations, "test", ScopeLevel.Run, false);
        var item = new ContextItem("volatile", ContextItemKind.ToolResult, BigText(), 5000, ContextPriority.Low,
            RetentionPolicy.RegenerateEachTurn, prov);
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([item]);
    }

    private static string BigText()
    {
        var chars = new string[1200];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = "dato volátil grande ";
        }

        return string.Join("", chars);
    }
}
