using OmniCore.Abstractions;
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
        // P1-12: un symlink INTERMEDIO (no el final) que apunta fuera del workspace se rechaza
        // comparando la raíz resuelta, no el path léxico.
        var baseDir = TestCwd + "\\.omnicore-boundary-" + Guid.NewGuid().ToString().Substring(0, 8);
        var ws = baseDir + "\\ws";
        var outside = baseDir + "\\outside";
        if (!Directory.Exists(ws)) Directory.CreateDirectory(ws);
        if (!Directory.Exists(outside)) Directory.CreateDirectory(outside);
        var link = ws + "\\escape";
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception)
        {
            // sin privilegios de symlink: la verificación se salta (el validador canónic aún
            // rechaza traversals léxicos en otros tests).
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
            return;
        }

        try
        {
            var v = new PathBoundaryValidator();
            var insideFile = link + "\\secret.txt";
            Assert.False(v.IsWithin(insideFile, ws),
                "Un junction intermedio que escapa NO debe considerarse dentro del workspace");
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        }
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