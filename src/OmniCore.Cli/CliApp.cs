using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Cli;

/// <summary>Despacha argumentos, procesa el protocolo y delega la composición al Host.</summary>
public sealed class CliApp
{
    private static readonly OmniCliRuntime Runtime = CreateRuntime();

    private static OmniCliRuntime CreateRuntime()
    {
        var runtime = OmniCliRuntime.Create(".");
        runtime.Localize = (key, args) => Loc().Resolve(key, args);
        return runtime;
    }

    // Idioma de la interfaz: OMNI_LOCALE=en o español por defecto (ADR-0040).
    private static Localization Loc() =>
        Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? Localization.English() : Localization.Spanish();

    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return Task.FromResult(1);
        }

        var command = args[0];
        if (command.StartsWith("/", StringComparison.Ordinal)) return RunTypedCommand(args);
        if (command == "sim") return RunSim(args);
        if (command is "explain" or "explore") return RunExplain(args);
        if (command == "ask") return RunAsk(args);
        if (command == "act") return RunAct(args);
        if (command == "model") return ModelPolicyCommands.Run(args);
        if (command == "trust") return RunTrust(args);
        if (command == "permissions") return RunPermissions(args);
        if (command == "doctor") return RunDoctor(args);
        if (command is "--tui" or "tui") return RunTui(args);
        if (command is "--help" or "-h" or "help")
        {
            PrintUsage();
            return Task.FromResult(0);
        }

        Console.WriteLine("omni: intención asumida como pregunta → ask '" + command + "'");
        return RunAsk(new[] { "ask", string.Join(" ", args) });
    }

    private static Task<int> RunTypedCommand(string[] args)
    {
        if (!CommandLineParser.TryParse(string.Join(" ", args), out var parsed) || parsed is null)
        {
            Console.WriteLine("omni: comando / inválido");
            return Task.FromResult(2);
        }
        var invocation = parsed!;
        var client = Runtime.Connect(CancellationToken.None);
        if (invocation.Name is "context" or "tools")
        {
            if (invocation.Name == "tools")
            {
                try { Runtime.ConfigureToolDiagnostics(CancellationToken.None); }
                catch (Exception ex)
                {
                    Console.WriteLine("/tools: " + ex.Message);
                    return Task.FromResult(1);
                }
            }
            var result = client.Query(invocation.Name, CancellationToken.None);
            if (result is null)
            {
                Console.WriteLine(invocation.Name + ": " + (Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en"
                    ? "no data available" : "sin datos disponibles"));
                return Task.FromResult(1);
            }
            RenderDiagnostic(invocation.Name, result.Json);
            return Task.FromResult(0);
        }

        var ack = client.Send(WireEnvelope.Command(Ids.NewV7(), CommandInvocationJson.Encode(invocation)),
            CancellationToken.None);
        if (ack.Status != "ok")
        {
            Console.WriteLine("/" + invocation.Name + ": " + (ack.Error ?? "command failed"));
            return Task.FromResult(2);
        }
        var expanded = client.Query("commandOutcome", CancellationToken.None)?.Json ?? "{}";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(expanded);
            var outcome = document.RootElement.GetProperty("outcome");
            var text = outcome.GetProperty("text").GetString() ?? "";
            var origin = outcome.GetProperty("origin").GetString() ?? "";
            Console.WriteLine("[" + origin + "]");
            return Runtime.AskAsync(text, Console.WriteLine, CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or KeyNotFoundException)
        {
            Console.WriteLine("/" + invocation.Name + ": resultado de comando inválido");
            return Task.FromResult(1);
        }
    }

    private static void RenderDiagnostic(string name, string json)
    {
        var locale = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? "en" : "es";
        Console.WriteLine(name == "context"
            ? (locale == "en" ? "Context snapshot (persisted):" : "Snapshot de contexto (persistido):")
            : (locale == "en" ? "Effective tools:" : "Tools efectivas:"));
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (name == "context")
            {
                var snapshot = root.GetProperty("snapshot");
                Console.WriteLine("Fingerprint: " + snapshot.GetProperty("fingerprint").GetString());
                Console.WriteLine("Tokens: " + snapshot.GetProperty("tokenCount").GetInt32() + "/"
                    + snapshot.GetProperty("tokenBudget").GetInt64() + " ("
                    + snapshot.GetProperty("tokenAccuracy").GetString() + ")");
                foreach (var item in snapshot.GetProperty("items").EnumerateArray())
                    Console.WriteLine("  " + item.GetProperty("kind").GetString() + " · "
                        + item.GetProperty("contributor").GetString() + " · "
                        + item.GetProperty("tokens").GetInt32() + " estimated tokens");
                if (snapshot.TryGetProperty("diagnostics", out var diagnostics))
                    foreach (var item in diagnostics.EnumerateArray())
                        Console.WriteLine("  " + item.GetProperty("itemId").GetString() + " · "
                            + item.GetProperty("decision").GetString() + " · "
                            + item.GetProperty("reason").GetString());
            }
            else
            {
                Console.WriteLine("Mode: " + root.GetProperty("mode").GetString());
                foreach (var tool in root.GetProperty("tools").EnumerateArray())
                    Console.WriteLine("  " + tool.GetProperty("visibleName").GetString() + " → "
                        + tool.GetProperty("toolId").GetString() + " · "
                        + tool.GetProperty("source").GetString() + " · "
                        + tool.GetProperty("effectClass").GetString() + " · "
                        + tool.GetProperty("decision").GetString());
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or KeyNotFoundException)
        {
            Console.WriteLine(locale == "en" ? "No snapshot is available." : "No hay snapshot disponible.");
        }
    }

    private static Task<int> RunSim(string[] args)
    {
        var jsonOutput = args.Any(a => a == "--json");
        var resume = args.Any(a => a == "--resume");
        var client = Runtime.Connect(CancellationToken.None);
        if (resume)
        {
            var ack = client.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "sim.resume") + "}"),
                CancellationToken.None);
            if (ack.Status == "ok") return RenderEvents(client, jsonOutput, 0, "resume");
            Console.WriteLine("omni sim --resume: " + (ack.Error is null ? "fallo" : Loc().ResolveWire(ack.Error)));
            return Task.FromResult(1);
        }

        var scenarioPath = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1] : null;
        var crash = args.Any(a => a == "--crash");
        if (scenarioPath is not null && !File.Exists(scenarioPath))
        {
            Console.WriteLine("omni sim: no existe el escenario " + scenarioPath);
            return Task.FromResult(2);
        }

        var scenarioName = scenarioPath is not null ? Path.GetFileNameWithoutExtension(scenarioPath)
            : crash ? "with-tool-crash" : "multi-item-plan";
        var payload = "{" + JsonObj.Field("cmd", "sim") + ","
            + (scenarioPath is not null
                ? JsonObj.Field("scenarioYaml", File.ReadAllText(scenarioPath))
                : JsonObj.Field("scenario", scenarioName)) + "}";
        var result = client.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        if (result.Status == "ok") return RenderEvents(client, jsonOutput, crash ? 1 : 0, scenarioName);
        Console.WriteLine("omni sim: " + (result.Error is null ? "fallo" : Loc().ResolveWire(result.Error)));
        return Task.FromResult(1);
    }

    private static Task<int> RenderEvents(IOmniClient client, bool jsonOutput, int exitCode, string scenarioName)
    {
        var events = client.SubscribeSince(0);
        if (jsonOutput)
        {
            var json = new JsonRenderer();
            foreach (var envelope in events) json.Emit(envelope);
            json.EmitOutcome(exitCode, exitCode == 0 ? "Completed" : "Interrupted", "sim");
            return Task.FromResult(exitCode);
        }

        var projection = new ClientProjection();
        var state = ClientState.Empty();
        foreach (var envelope in events) state = projection.Apply(state, envelope);
        new PlainRenderer("es").Render(state);
        if (events.Count == 0) Console.WriteLine("omni sim: ok (sin eventos nuevos; escenario determinista)");
        return Task.FromResult(exitCode);
    }

    private static Task<int> RunTui(string[] args)
    {
        var client = Runtime.Connect(CancellationToken.None);
        if (args.Any(a => a == "--sim"))
        {
            var payload = "{" + JsonObj.Field("cmd", "sim") + ","
                + JsonObj.Field("scenario", "multi-item-plan") + "}";
            client.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        }

        var state = ClientState.Empty();
        var projection = new ClientProjection();
        foreach (var envelope in client.SubscribeSince(0)) state = projection.Apply(state, envelope);
        Console.WriteLine(TuiApp.Render(state, "es"));
        return Task.FromResult(0);
    }

    private static Task<int> RunExplain(string[] args)
    {
        var question = args.Length >= 2 ? args[1] : "explícame el estado del plan";
        var client = Runtime.Connect(CancellationToken.None);
        var state = client.Query("state", CancellationToken.None);
        var workingState = client.Query("workingState", CancellationToken.None);
        Console.WriteLine("omni explain: contexto del run y plan mantenido por el runtime.");
        Console.WriteLine("Pregunta: " + question);
        if (state is not null)
        {
            var milestone = JsonObj.Parse(state.Json).TryGetValue("runState", out var value) ? value! : "M2";
            Console.WriteLine("Run: " + milestone);
        }

        if (workingState is not null)
        {
            var text = JsonObj.Parse(workingState.Json).TryGetValue("workingState", out var value) ? value! : "";
            Console.WriteLine("WorkingState (redactado):");
            Console.WriteLine(RedactJson(text, 900));
        }
        else Console.WriteLine("Contexto: ejecuta primero `omni sim` para materializar el run.");
        return Task.FromResult(0);
    }

    private static string RedactJson(string json, int max)
    {
        var redacted = OmniCliRuntime.RedactSensitive(json);
        return redacted.Length <= max ? redacted : redacted.Substring(0, max) + "…";
    }

    private static Task<int> RunPermissions(string[] args)
    {
        if (args.Length == 1 || args.Length == 2 && args[1] == "list")
        {
            var result = Runtime.Permissions(new ListPermissionGrantsCommand(), CancellationToken.None);
            Console.WriteLine(Loc().Resolve("permissions.heading"));
            if (result.Grants.Count == 0) Console.WriteLine(Loc().Resolve("permissions.empty"));
            foreach (var grant in result.Grants)
            {
                var lifetimeKey = grant.Lifetime switch
                {
                    "Run" => "interaction.permission.allow_run",
                    "Workspace" => "interaction.permission.allow_workspace",
                    _ => "interaction.permission.allow_once",
                };
                Console.WriteLine(grant.Id + "  " + grant.ToolId + "  " + Loc().Resolve(lifetimeKey)
                    + "  " + grant.ClaimsKey + (grant.Run is null ? "" : "  Run " + grant.Run));
            }
            return Task.FromResult(0);
        }

        if (args.Length == 3 && args[1] == "revoke" && Guid.TryParse(args[2], out var id))
        {
            var result = Runtime.Permissions(new RevokePermissionGrantCommand(id.ToString()),
                CancellationToken.None);
            Console.WriteLine(Loc().Resolve(result.Revoked ? "permissions.revoked" : "permissions.not_found"));
            return Task.FromResult(result.Revoked ? 0 : 1);
        }

        Console.WriteLine(Loc().Resolve("permissions.usage"));
        return Task.FromResult(2);
    }

    private static Task<int> RunTrust(string[] args)
    {
        var revoke = args.Length > 1 && args[1] is "revoke" or "--revoke";
        if (args.Length > 2 || args.Length == 2 && !revoke)
        {
            Console.WriteLine("Uso: omni trust [revoke]");
            return Task.FromResult(2);
        }
        Runtime.SetWorkspaceTrusted(!revoke);
        Console.WriteLine(revoke ? "Workspace marcado como no confiable." : "Workspace confiable guardado fuera del repo.");
        return Task.FromResult(0);
    }

    private static Task<int> RunDoctor(string[] args) => Task.FromResult(
        OmniCliRuntime.Doctor(DoctorLocale(args), Console.WriteLine, (key, values) => Loc().Resolve(key, values)));

    private static string DoctorLocale(string[] args)
    {
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--locale" && index + 1 < args.Length)
                return args[index + 1] == "en" ? "en" : "es";
            if (args[index].StartsWith("--locale=", StringComparison.Ordinal))
                return args[index][9..] == "en" ? "en" : "es";
        }
        return Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? "en" : "es";
    }

    private static Task<int> RunAsk(string[] args) => Runtime.AskAsync(
        args.Length >= 2 ? args[1] : "Responde solo: hola", Console.WriteLine, CancellationToken.None);

    private static Task<int> RunAct(string[] args) => Runtime.ActAsync(
        args.Length >= 2 ? args[1] : "", Console.WriteLine, CancellationToken.None);

    private static void PrintUsage()
    {
        Console.WriteLine("omni: runtime de agentes local-first para .NET 10 (M1)");
        Console.WriteLine();
        Console.WriteLine("Uso:");
        Console.WriteLine("  omni sim [escenario.yaml] [--json]   Ejecuta la simulación de M1");
        Console.WriteLine("  omni act \"instrucción\"              Run Act con filesystem.read/patch bajo política efectiva (M3)");
        Console.WriteLine("  omni ask \"texto\"                    Turn end-to-end contra el modelo local (M2)");
        Console.WriteLine(Loc().Resolve("commands.context.help"));
        Console.WriteLine(Loc().Resolve("commands.tools.help"));
        Console.WriteLine(Loc().Resolve("commands.explain.help"));
        Console.WriteLine("  omni model ...                       Políticas de modelo y onboarding (M3)");
        Console.WriteLine(Loc().Resolve("permissions.help"));
        Console.WriteLine("  omni --help                          Esta ayuda");
    }
}
