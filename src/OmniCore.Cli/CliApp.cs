using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;

namespace OmniCore.Cli;

/// <summary>
/// Punto de composición del CLI (ADR-0019 §3): solo conoce tipos de Protocol + la fábrica del
/// Host. `Program.cs` es el único lugar que compone.
/// </summary>
public sealed class CliApp
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return Task.FromResult(1);
        }

        var command = args[0];
        if (command == "sim")
        {
            return RunSim(args);
        }

        if (command == "explain" || command == "explore")
        {
            return RunExplain(args);
        }

        if (command == "ask")
        {
            return RunAsk(args);
        }

        if (command == "doctor")
        {
            return RunDoctor(args);
        }

        if (command == "--tui" || command == "tui")
        {
            return RunTui(args);
        }

        if (command == "--help" || command == "-h" || command == "help")
        {
            PrintUsage();
            return Task.FromResult(0);
        }

        Console.WriteLine("omni: comando desconocido '" + command + "'");
        PrintUsage();
        return Task.FromResult(1);
    }

    private static Task<int> RunSim(string[] args)
    {
        var jsonOutput = args != null && args.Any(a => a == "--json");
        var resume = args != null && args.Any(a => a == "--resume");
        var server = ResumeAwareServer();
        var cancellationToken = CancellationToken.None;

        if (resume)
        {
            var resumePayload = "{" + JsonObj.Field("cmd", "sim.resume") + "}";
            var r = server.Send(WireEnvelope.Command(Ids.NewV7(), resumePayload), cancellationToken);
            if (r.Status == "ok")
            {
                return RenderEvents(server, jsonOutput, 0, "resume");
            }

            Console.WriteLine("omni sim --resume: " + (r.Error ?? "fallo"));
            return Task.FromResult(1);
        }

        var scenarioName = args != null && args.Length >= 2 ? args[1] : null;
        var crash = args != null && args.Any(a => a == "--crash");
        var scenario = (scenarioName is not null && File.Exists(scenarioName!))
            ? ScenarioYaml.Parse(File.ReadAllText(scenarioName!))
            : (crash ? Scenarios.WithToolCrash() : Scenarios.MultiItemPlan());

        // El CLI no conoce el Engine directamente: pasa por IOmniClient con payload wire (ADR-0019).
        var payload = "{" + JsonObj.Field("cmd", "sim") + ","
            + JsonObj.Field("scenario", scenario.Name) + "}";
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), payload), cancellationToken);
        if (ack.Status == "ok")
        {
            return RenderEvents(server, jsonOutput, crash ? 1 : 0, scenario.Name);
        }

        Console.WriteLine("omni sim: " + (ack.Error ?? "fallo"));
        return Task.FromResult(1);
    }

    /// <summary>
    /// Consume los eventos del server (implicación de IOmniClient, ADR-0019), los reduce con la
    /// ClientProjection (ADR-0030) y los renderiza con el renderer seleccionado (flujo de M1:
    /// --json → JsonRenderer con NDJSON + RunOutcome; si no, PlainRenderer).
    /// </summary>
    private static Task<int> RenderEvents(OmniCore.Host.OmniServer server, bool jsonOutput, int exitCode,
        string scenarioName)
    {
        var events = server.SubscribeSince(0);
        if (jsonOutput)
        {
            var json = new JsonRenderer();
            foreach (var envelope in events)
            {
                json.Emit(envelope);
            }

            json.EmitOutcome(exitCode, exitCode == 0 ? "Completed" : "Interrupted", "sim");
            return Task.FromResult(exitCode);
        }

        var projection = new OmniCore.Client.ClientProjection();
        var state = OmniCore.Client.ClientState.Empty();
        foreach (var envelope in events)
        {
            state = projection.Apply(state, envelope);
        }

        var renderer = new PlainRenderer("es");
        renderer.Render(state);
        if (events.Count == 0)
        {
            Console.WriteLine("omni sim: ok (sin eventos nuevos; escenario determinista)");
        }

        return Task.FromResult(exitCode);
    }

    /// <summary>
    /// Modo TUI: renderiza el estado de la ClientProjection como un frame de 4 zonas
    /// (header, conversación, sidebar, status line; ADR-0031). En M1 y sin Terminal.Gui
    /// enlazable en este toolchain, la TUI usa el render ANSI propio de TuiApp.
    /// </summary>
    private static Task<int> RunTui(string[] args)
    {
        var server = ResumeAwareServer();
        var simulation = args != null && args.Any(a => a == "--sim");
        if (simulation)
        {
            var payload = "{" + JsonObj.Field("cmd", "sim") + ","
                + JsonObj.Field("scenario", "multi-item-plan") + "}";
            server.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        }

        var events = server.SubscribeSince(0);
        var state = OmniCore.Client.ClientState.Empty();
        var projection = new OmniCore.Client.ClientProjection();
        foreach (var envelope in events)
        {
            state = projection.Apply(state, envelope);
        }

        Console.WriteLine(TuiApp.Render(state, "es"));
        return Task.FromResult(0);
    }

    /// <summary>
    /// `omni explain "pregunta"`: el criterio de M2 — el runtime mantiene el Plan y el Turn es
    /// explicable por fingerprint (ADR-0017). Materializa el contexto real del último run
    /// (WorkingState + fingerprint) y lo muestra con la pregunta del usuario.
    /// </summary>
    private static Task<int> RunExplain(string[] args)
    {
        var question = args.Length >= 2 ? args[1] : "explícame el estado del plan";
        var server = ResumeAwareServer();

        var cancellationToken = CancellationToken.None;
        var state = server.Query("state", cancellationToken);
        var ws = server.Query("workingState", cancellationToken);

        Console.WriteLine("omni explain: contexto del run y plan mantenido por el runtime.");
        Console.WriteLine("Pregunta: " + question);
        if (state is not null)
        {
            var milestone = OmniCore.Protocol.JsonObj.Parse(state!.Json)
                .TryGetValue("runState", out var m) ? m! : "M2";
            Console.WriteLine("Run: " + milestone);
        }

        if (ws is not null)
        {
            var text = OmniCore.Protocol.JsonObj.Parse(ws!.Json)
                .TryGetValue("workingState", out var v) ? v! : "";
            Console.WriteLine("WorkingState (redactado):");
            Console.WriteLine(RedactJson(text, 900));
        }
        else
        {
            Console.WriteLine("Contexto: ejecuta primero `omni sim` para materializar el run.");
        }

        return Task.FromResult(0);
    }

    /// <summary>Trunca JSON de diagnóstico sin romper paréntesis (parser perezoso para shell).</summary>
    private static string RedactJson(string json, int max)
    {
        var redacted = new OmniCore.Infrastructure.PiiRedactor().Redact(json);
        if (redacted.Length <= max)
        {
            return redacted;
        }

        return redacted.Substring(0, max) + "…";
    }

    /// <summary>
    /// `omni ask "texto"`: conecta al modelo local (M2) y muestra la respuesta. La conexión JSON
    /// del endpoint se configura aquí (base URL, modelo y key en variables): el CLI no guarda
    /// secretos. En M2 esto demuestra el runtime real sobre ik_llama/llama.cpp.
    /// </summary>
    private static Task<int> RunDoctor(string[] args)
    {
        Console.WriteLine("omni doctor — diagnóstico de M2");
        var registry = OmniHost.LoadModelRegistry(".");
        Console.WriteLine("Modelos disponibles:");
        foreach (var model in registry.Models())
        {
            var provider = registry.Provider(model.ProviderId);
            Console.WriteLine("  " + model.Id + " → provider '" + model.ProviderId + "'"
                + (provider is null ? "" : " (" + provider.Family + ", " + provider.BaseUrl + ")"));
        }

        var configured = registry.Models().Count > 0;
        Console.WriteLine(configured ? "Estado: modelo configurado ✓" : "Estado: sin modelo configurado (ejecuta omni ask para ver la guía)");
        return Task.FromResult(configured ? 0 : 1);
    }

    private static Task<int> RunAsk(string[] args)
    {
        var question = args.Length >= 2 ? args[1] : "Responde solo: hola";

        // Resolución desde el registro (providers.yaml/models.yaml) o variables de entorno.
        var registry = OmniHost.LoadModelRegistry(".");
        var modelDef = registry.Models().Count > 0 ? registry.Models()[0] : null;
        var providerDesc = modelDef is null ? null : registry.Provider(modelDef!.ProviderId);
        var authKind = providerDesc is null ? OmniCore.Abstractions.AuthKind.None : providerDesc!.Auth.Kind;

        var model = System.Environment.GetEnvironmentVariable("OMNI_MODEL") ?? modelDef?.Id;
        var baseUrl = System.Environment.GetEnvironmentVariable("OMNI_BASE_URL") ?? providerDesc?.BaseUrl
            ?? "http://127.0.0.1:8080/v1";

        // Solo se exige la key si el provider configurado la requiere (None no la pide).
        var secretRef = providerDesc?.Auth.SecretRef ?? "qwen";
        var key = System.Environment.GetEnvironmentVariable("OMNI_QWEN_KEY");
        if (authKind == OmniCore.Abstractions.AuthKind.ApiKey && (key is null || key!.Length == 0))
        {
            Console.WriteLine("omni ask: '" + (providerDesc?.Id ?? "local") + "' requiere API key; define OMNI_QWEN_KEY. No se guardan secretos en el repo (ADR-0018).");
            return Task.FromResult(1);
        }

        try
        {
            if (model is null)
            {
                Console.WriteLine("omni ask: no hay modelo configurado. Crea models.yaml o define OMNI_MODEL.");
                return Task.FromResult(1);
            }

            // 1. Runtime real: materializa el contexto con el sim (WorkingState + plan + tokens).
            var hostTools = OmniHost.CreateHostTools();
            var server = ResumeAwareServer();
            var wsQuery = server.Query("workingState", CancellationToken.None);
            var wsJson = wsQuery is null ? "{}" : wsQuery!.Json;
            var workingStateText = OmniCore.Protocol.JsonObj.Parse(wsJson)
                .TryGetValue("workingState", out var wsVal) ? wsVal! : "";
            if (workingStateText.Length == 0)
            {
                Console.WriteLine("omni ask: el run no tiene WorkingState; ejecuta primero `omni sim`.");
                return Task.FromResult(1);
            }

            var stateQuery = server.Query("state", CancellationToken.None);
            var stateJson = stateQuery?.Json ?? "{}";
            var runState = OmniCore.Protocol.JsonObj.Parse(stateJson).TryGetValue("runState", out var rs) ? rs! : "M2";
            var fingerprint = new OmniCore.Domain.ExecutionFingerprint(
                model!, "harness-v1", "core-tools-1", "ctx-v1", "none", "M2");
            var sessionId = server.LastSessionId() ?? OmniCore.Domain.SessionId.New();
            var runId = server.LastRunId() ?? OmniCore.Domain.RunId.New();

            // 2. Provider conectado al modelo local (TLS relajado solo para loopback/privado).
            var provider = OmniHost.ConnectLocalChatCompletions(baseUrl!, model!, secretRef, key ?? "");
            var selection = new OmniCore.Domain.ModelSelection(new OmniCore.Domain.ModelIdValue(model!), 8192,
                OmniCore.Domain.ToolMode.Direct, null);

            // 3. Turn end-to-end: contexto REAL del run + fingerprint + tools reales + permisos.
            var executor = OmniHost.CreateSimExecutor(Path.GetFullPath("."));
            var materializer = new OmniCore.Context.ContextMaterializer(
                new OmniCore.Infrastructure.FakeTokenCounter(),
                new OmniCore.Context.IContextContributor[] {
                    new OmniCore.Context.WorkingStateContributor(workingStateText),
                });
            var turn = new OmniCore.Host.ExplorerTurn(
                (req, token) => provider.Complete(req, token),
                executor, hostTools.Catalog(), materializer, fingerprint, selection);
            var instruction = "Ayudas a un asistente de ingeniería. Work Thread del workspace:\n"
                + "Contexto del run disponible ({context}).\n"
                + "Responde en español, sé conciso y usa las tools cuando aporten.";
            var result = turn.Ask(question, instruction, sessionId, runId, workingStateText, CancellationToken.None);

            foreach (OmniCore.Host.ExplorerTurn.ToolUseTrace trace in result.ToolCalls)
            {
                Console.WriteLine("[tool] " + trace.ToolName + " → "
                    + (trace.Succeeded ? "ok" : "FALLO") + ": " + trace.Summary);
            }

            if (result.FinalText is not null && result.FinalText!.Length > 0)
            {
                Console.WriteLine(result.FinalText);
            }

            Console.WriteLine("── " + result.StopReason + " · steps " + result.Steps
                + " · tokens " + (result.Usage.Input + result.Usage.Output));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine("omni ask: error: " + (ex.Message ?? "?") + " [" + ex.GetType().Name + "]");
            if (ex.StackTrace is not null)
            {
                Console.WriteLine("  frames: " + ex.StackTrace.Length + " " + string.Join("|", ex.StackTrace.Take(6)));
            }

            return Task.FromResult(1);
        }
    }
    private static OmniCore.Host.OmniServer ResumeAwareServer()
    {
        if (_server is null)
        {
            // Journal persistente del CLI para permitir --resume entre procesos (ADR-0041 §2).
            var journal = ".omnicore-sim-journal.db";
            _server = OmniHost.OpenPersistentServer(journal);
        }

        return _server!;
    }

    private static OmniCore.Host.OmniServer? _server;

    private static void PrintUsage()
    {
        Console.WriteLine("omni: runtime de agentes local-first para .NET 10 (M1)");
        Console.WriteLine();
        Console.WriteLine("Uso:");
        Console.WriteLine("  omni sim [escenario.yaml] [--json]   Ejecuta la simulación de M1");
        Console.WriteLine("  omni --help                          Esta ayuda");
    }
}