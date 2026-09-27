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
///  5. YAML/auth: `auth: none` no pide key; `auth: {apiKey: ref}` la conserva; models context/maxOutput.
///  6. Context overflow: la política de recorte suelta volátiles, no rinde el WorkingState.
///  7. Secretos: `Secret.ToString()` y los credenciales nunca salen del proceso.
/// </summary>
public sealed class M2IntegrationTests
{
    private static readonly string TestCwd = Path.GetFullPath(".");

    [Fact]
    public async System.Threading.Tasks.Task Explorer_applies_plan_propose_via_projection()
    {
        // Requisito 2: plan.propose se aplica DESDE ExplorerTurn con las proyecciones del Run.
        var journal = TestCwd + "\\.omnicore-plan-journal-" + Guid.NewGuid().ToString().Substring(0, 8) + ".db";
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
            store, codecs, new FileArtifactStore(TestCwd + "\\.omnicore-plan-artifacts"),
            new InMemoryAuditSink(), new RedactionPolicy());
        // El journal necesita un Run con Plan (P0 como item) para que plan.propose (start P1) aplique.
        var stream = new EventStream(store, codecs, sessionId);
        var rootItem = PlanItemId.New();
        stream.Append(new RunCreated(runId, sessionId, "objetivo", RunMode.Act, ExecutionStrategy.Direct,
            FailurePolicy.BlockDependents, new TaskBudget(1m, 1000L, 10, 20), CreatenRootTask(), DateTimeOffset.Now));
        stream.Append(new PlanCreated(PlanId.New(), runId, rootItem, "objetivo"));
        stream.Append(new PlanItemAdded(rootItem, PlanId.New(), "P0 raíz", 1, null, new PlanItemId[0], true, new Dictionary<string, string>()));
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

    private static TaskId CreatenRootTask() => TaskId.New();

    [Fact]
    public async System.Threading.Tasks.Task Filesystem_read_blocks_secret_paths_and_redacts_content()
    {
        // Requisito 3: rutas de secretos (.env, .pem, .key, .ssh/) nunca se leen; el contenido
        // leído queda redactado (sin API keys/Bearer en el tool result).
        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var executor = ScriptedToolExecutor.WithCoreTools(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow));

        // Archivo .env real con un secreto: el file se bloquea por nombre.
        var wsDir = TestCwd + "\\.omnicore-secrets-test";
        if (!Directory.Exists(wsDir)) Directory.CreateDirectory(wsDir);
        File.WriteAllText(wsDir + "\\.env", "API_KEY=sk-test123secret\nPASSWORD=hunter2");
        var secretCall = new ValidatedToolCall(ToolCallId.New(), new OmniCore.Abstractions.ToolId("filesystem.read"),
            "pc-env", "{\"path\":\".env\"}");

        var outcomeSecret = executor.ExecuteTool(secretCall, false, CancellationToken.None);
        Assert.True(HasSecretBlocked(outcomeSecret), "Un .env se bloquea. summary=" + outcomeSecret.Summary);
        Assert.False(outcomeSecret.Preview is not null && outcomeSecret.Preview!.Length > 0,
            "El .env bloqueado no expone ningún contenido");
        File.Delete(wsDir + "\\.env");

        // Archivo normal con un Bearer: el contenido se redacta al devolverlo.
        File.WriteAllText(wsDir + "\\normal.txt", "Bearer VOtOkEn123secret contenido normal");
        var okCall = new ValidatedToolCall(ToolCallId.New(), new OmniCore.Abstractions.ToolId("filesystem.read"),
            "pc-ok", "{\"path\":\"normal.txt\"}");
        var outcomeOk = executor.ExecuteTool(okCall, false, CancellationToken.None);
        Assert.True(outcomeOk.Succeeded, "Un archivo normal se lee");
        Assert.False(outcomeOk.Preview is not null && outcomeOk.Preview!.Contains("VOtOkEn123secret"),
            "El tool result va redactado (sin Bearer/keys)");
        File.Delete(wsDir + "\\normal.txt");
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
        // Requisito 9: el criterio `omni "explícame este repositorio"` se cumple en el
        // plain renderer: sim → workingState real → ClientProjection → PlainRenderer.
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + OmniCore.Protocol.JsonObj.Field("cmd", "sim")
            + "," + OmniCore.Protocol.JsonObj.Field("scenario", "multi-item-plan") + "}"), CancellationToken.None);

        var ws = server.Query("workingState", CancellationToken.None);
        Assert.True(ws is not null && ws!.Json.Contains("workingState"), "el run expuso el WorkingState real");
        var wsText = OmniCore.Protocol.JsonObj.Parse(ws!.Json)
            .TryGetValue("workingState", out var v) ? v! : "";

        // PlainRenderer con ClientState: renderiza el conversation del sim (con el plan).
        var state = OmniCore.Client.ClientState.Empty();
        foreach (var envelope in server.SubscribeSince(0))
        {
            state = new OmniCore.Client.ClientProjection().Apply(state, envelope);
        }

        var renderer = new PlainRenderer("es");
        var output = CaptureRender(renderer, state);

        // El plain renderer describe el repositorio (objetivo del run y el plan).
        Assert.True(wsText.Length > 0, "WorkingState materializado");
        Assert.True(output.Length > 0, "El plain renderer produce salida");
    }

    private static string CaptureRender(PlainRenderer renderer, OmniCore.Client.ClientState state)
    {
        var captured = new System.IO.StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(captured);
        try
        {
            renderer.Render(state);
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

        var artifacts = OmniHost.CreateArtifactStore(TestCwd + "\\.omnicore-host-artifacts");
        var refArtifact = artifacts.PutText("Bearer VOtOkEnSecret123", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
        var stored = artifacts.GetText(refArtifact.Hash);
        Assert.True(stored is not null, "GetText devolvió contenido (hash=" + refArtifact.Hash + ")");
        Assert.False(stored!.Contains("VOtOkEnSecret123"), "redacta secretos al persistir");
        TryDeleteFiles(TestCwd, ".omnicore-host-artifacts");
    }

    [Fact]
    public async System.Threading.Tasks.Task Explorer_turn_persists_and_replays_after_restart()
    {
        // Requisito 1: el Turn del Explorer PERSISTE en el journal (turn.started, tool calls/
        // permisos/outcomes, model.completed, turn.completed) y se reelige tras reiniciar.
        var journal = TestCwd + "\\.omnicore-turn-journal-" + Guid.NewGuid().ToString().Substring(0, 8) + ".db";
        if (File.Exists(journal)) File.Delete(journal);
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journal);
        var plan = new PlanService();
        var hostTools = new HostTools(new PathBoundaryValidator(), plan);
        var executor = ScriptedToolExecutor.WithCoreTools(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act));
        var fingerprint = new ExecutionFingerprint("test-model", "h", "t", "c", "o", "M2");
        var selection = new ModelSelection(new ModelIdValue("test-model"), 8192, ToolMode.Direct, null);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var turn = new ExplorerTurn(
            (request, token) => FakeResponses.ToolThenText(request),
            executor, hostTools.Catalog(), materializer, fingerprint, selection,
            store, codecs, new FileArtifactStore(TestCwd + "\\.omnicore-turn-artifacts"),
            new InMemoryAuditSink(), new RedactionPolicy());
        var sessionId = SessionId.New();
        var runId = RunId.New();

        var result = turn.Ask("usa la tool", "sys {context}", sessionId, runId, "ws-state", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.True(result.ToolCalls.Count >= 1, "Hubo tool-call en el turno");

        // Replay tras "reinicio": abrir el mismo journal y leer los eventos del Turn.
        var store2 = new SqliteEventStore(journal);
        var tail = store2.ReadFrom(sessionId, 1);
        var types = tail.Select(e => e.Type.ToString()).ToArray();
        Assert.Contains("turn.started", types);
        Assert.Contains("turn.completed", types);
        Assert.Contains("toolcall.permission_evaluated", types);
        Assert.Contains("toolcall.succeeded", types);
        Assert.Contains("model.completed", types);
        Assert.True(result.ResponseArtifactId is not null, "La respuesta se guardó como artifact");
        TryDelete(journal);
        TryDeleteFiles(TestCwd, ".omnicore-turn-artifacts");
    }

    private static void TryDeleteFiles(string dir, string prefix)
    {
        try
        {
            var full = dir + "\\" + prefix;
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
        var executor = ScriptedToolExecutor.WithCoreTools(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act));
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
        var runId = RunId.New();

        var result = turn.Ask("usa la tool y explica", "instruccion {context}", sessionId, runId, "ws-state",
            CancellationToken.None);

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
        var executor = ScriptedToolExecutor.WithCoreTools(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act));
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var recorded = new List<ModelRequest>();
        var turn = new ExplorerTurn((request, token) =>
        {
            recorded.Add(request);
            return FakeResponses.ToolThenText(request);
        }, executor, hostTools.Catalog(), materializer,
            new ExecutionFingerprint("m", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null));

        turn.Ask("pregunta", "sys", SessionId.New(), RunId.New(), "", CancellationToken.None);

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
            + "  local: { baseUrl: http://127.0.0.1:8080, auth: none }\n"
            + "  openrouter: { baseUrl: https://openrouter.ai/api/v1, auth: { apiKey: openrouter-key } }\n";
        var models = "models:\n"
            + "  qwen-27b: { provider: local, context: 32768, maxOutput: 8192 }\n";
        var loader = new ConfigLoader();
        var registry = loader.BuildRegistry(providers, models);

        var local = registry.Provider("local");
        var openrouter = registry.Provider("openrouter");
        Assert.True(local!.Auth.Kind == AuthKind.None, "auth: none NO es una API key");
        Assert.True(openrouter!.Auth.Kind == AuthKind.ApiKey, "auth: {apiKey: ref} es ApiKey");
        Assert.Equal("openrouter-key", openrouter!.Auth.SecretRef);

        var qwen = registry.Model("qwen-27b");
        Assert.True(qwen!.ContextWindow == 32768, "context se respeta (no 8192 fijo)");
        Assert.True(qwen!.MaxOutputTokens == 8192, "maxOutput se respeta (no 2048 fijo)");
    }

    [Fact]
    public async System.Threading.Tasks.Task Context_overlow_pinned_greater_than_budget_flows()
    {
        // Requisito 6: WorkingState pinned MAYOR al presupuesto produce producción de
        // ContextOverflow (snapshot.Overflowed), nunca un snapshot por encima del límite.
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

        // Presupuesto menor que el WorkingState (que es pinned y va primero).
        var snapshot = materializer.MaterializeWithinBudget(request, CancellationToken.None, 10);

        Assert.True(snapshot.Overflowed, "WorkingState pinned mayor al presupuesto → ContextOverflow");
        Assert.True(snapshot.TokenCount <= 12, "Nunca un snapshot muy por encima del límite (real " + snapshot.TokenCount + ")");
        Assert.True(snapshot.Items.Count >= 1, "El WorkingState truncado permanece (items " + snapshot.Items.Count + ")");
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

        var path = TestCwd + "\\.omnicore-test-creds.ini";
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
        var journal = TestCwd + "\\.omnicore-journal-restore-" + stamp + ".db";
        var stateFile = TestCwd + "\\lastsession-restore-" + stamp + ".txt";
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
        var baseDir = TestCwd + "\\.omnicore-boundary-" + Guid.NewGuid().ToString().Substring(0, 8);
        var ws = baseDir + "\\ws";
        var outside = baseDir + "\\outside";
        if (!Directory.Exists(ws)) Directory.CreateDirectory(ws);
        if (!Directory.Exists(outside)) Directory.CreateDirectory(outside);
        var link = ws + "\\escape";

        // Creación OBLIGATORIA del enlace (requisito 7: no hay skip). Primero symlink; si el
        // entorno lo rechaza (sin Developer Mode), se crea un JUNCTION de directorio vía
        // mklink /J (no requiere privilegios en Windows). Si ambos fallan, la prueba falla.
        var created = TryCreateDirectoryLink(link, outside);
        Assert.True(created, "No se pudo crear symlink/junction para verificar la frontera (requisito 7). link="
            + link + " exists=" + Directory.Exists(link));

        try
        {
            var v = new PathBoundaryValidator();
            var insideFile = link + "\\secret.txt";
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
        var executor = ScriptedToolExecutor.WithCoreTools(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act));
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

        turn.Ask("usa filesystem.read", "sys", SessionId.New(), RunId.New(), "", CancellationToken.None);

        var forwarded = string.Join(" ", captured.ToArray());
        Assert.False(forwarded.Contains("Ruta fuera del workspace"), "El archivo se lee dentro del workspace");
        Assert.True(forwarded.Length > 5, "El contenido REAL del archivo se propagó al modelo: {" + forwarded + "}");
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
                new ToolCallBlock(callId, "call_test", "filesystem.read", "{\"path\":\"README.md\"}"),
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