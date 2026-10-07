using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Cli;

/// <summary>Despacha argumentos, procesa el protocolo y delega la composición al Host.</summary>
public sealed class CliApp
{
    private static OmniCliRuntime Runtime = CreateRuntime();

    // Internal injection point for end-to-end tests. Production always uses CreateRuntime().
    internal static OmniCliRuntime UseRuntimeForTests(OmniCliRuntime runtime)
    {
        var previous = Runtime;
        Runtime = runtime;
        return previous;
    }

    private static OmniCliRuntime CreateRuntime()
    {
        var runtime = OmniCliRuntime.Create(".");
        runtime.Localize = (key, args) => Loc().Resolve(key, args);
        runtime.QuestionnaireInput = Console.IsInputRedirected ? null : PlainQuestionnaireForm.Read;
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
        if (command == "login" && args.Length >= 2 && args[1] == "chatgpt")
            return Runtime.LoginChatGptAsync(args.Any(a => a == "--device"), Console.WriteLine, CancellationToken.None);
        if (command == "logout" && args.Length >= 2 && args[1] == "chatgpt")
        {
            OmniCliRuntime.LogoutChatGpt();
            Console.WriteLine(Loc().Resolve("cli.logout.ok"));
            return Task.FromResult(0);
        }
        if (command == "permissions") return RunPermissions(args);
        if (command == "resolve") return RunResolve(args);
        if (command == "verify-journal") return JournalCommands.VerifyJournal(args);
        if (command == "session" && args.Length >= 2 && args[1] == "purge")
            return MaintenanceCommands.SessionPurge(args);
        if (command == "gc") return MaintenanceCommands.Gc(args);
        if (command == "audit" && args.Length >= 2 && args[1] == "purge")
            return MaintenanceCommands.AuditPurge(args);
        if (command is "session" or "audit")
        {
            Console.WriteLine(Loc().Resolve("cli.unknown_subcommand", "command", command));
            return Task.FromResult(2);
        }
        if (command == "doctor") return RunDoctor(args);
        if (command is "--tui" or "tui") return RunTui(args);
        if (command is "--help" or "-h" or "help")
        {
            PrintUsage();
            return Task.FromResult(0);
        }

        Console.WriteLine(Loc().Resolve("cli.ask_assumed", "command", command));
        return RunAsk(new[] { "ask", string.Join(" ", args) });
    }

    private static Task<int> RunResolve(string[] args)
    {
        if (args.Length > 3)
        {
            Console.WriteLine(Loc().Resolve("interaction.resolve.usage"));
            return Task.FromResult(2);
        }

        var client = Runtime.Connect(CancellationToken.None);
        if (args.Length < 3)
        {
            var projection = new ClientProjection(Loc());
            var state = ClientState.Empty();
            foreach (var envelope in client.SubscribeSince(0)) state = projection.Apply(state, envelope);
            if (args.Length == 2)
            {
                var matches = state.Overlays.Where(item => item.Id == args[1]).ToArray();
                state = new ClientState(state.Header, state.Conversation, state.Sidebar, state.Composer,
                    state.StatusLine, matches, state.Connection);
            }
            if (state.Overlays.Count == 0)
            {
                Console.WriteLine(Loc().Resolve("interaction.resolve.none"));
                return Task.FromResult(1);
            }
            new PlainRenderer(Loc().Locale).Render(state);
            return Task.FromResult(0);
        }

        var payload = "{" + JsonObj.Field("cmd", "interaction.respond") + ","
            + JsonObj.Field("interactionId", args[1]) + "," + JsonObj.Field("optionId", args[2]) + "}";
        var ack = client.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        if (ack.Status != "ok")
        {
            Console.WriteLine(Loc().ResolveWire(ack.Error ?? "interaction resolution failed"));
            return Task.FromResult(1);
        }

        Console.WriteLine(Loc().Resolve("interaction.resolve.done"));
        return Task.FromResult(0);
    }

    private static Task<int> RunTypedCommand(string[] args)
    {
        if (!CommandLineParser.TryParse(string.Join(" ", args), out var parsed) || parsed is null)
        {
            Console.WriteLine(Loc().Resolve("cli.invalid_command"));
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
                    Console.WriteLine(Loc().Resolve("cli.tools_error", "error", ex.Message));
                    return Task.FromResult(1);
                }
            }
            var result = client.Query(invocation.Name, CancellationToken.None);
            if (result is null)
            {
                Console.WriteLine(Loc().Resolve("cli.no_data", "command", invocation.Name));
                return Task.FromResult(1);
            }
            RenderDiagnostic(invocation.Name, result.Json);
            return Task.FromResult(0);
        }

        var ack = client.Send(WireEnvelope.Command(Ids.NewV7(), CommandInvocationJson.Encode(invocation)),
            CancellationToken.None);
        if (ack.Status != "ok")
        {
            Console.WriteLine(Loc().Resolve("cli.command_ack_error",
                new Dictionary<string, string> { ["command"] = invocation.Name, ["error"] = ack.Error ?? Loc().Resolve("cli.command_failed") }));
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
            Console.WriteLine(Loc().Resolve("cli.command_invalid_result", "command", invocation.Name));
            return Task.FromResult(1);
        }
    }

    private static void RenderDiagnostic(string name, string json)
    {
        var locale = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? "en" : "es";
        Console.WriteLine(name == "context"
            ? Loc().Resolve("cli.diagnostic.context")
            : Loc().Resolve("cli.diagnostic.tools"));
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
            Console.WriteLine(Loc().Resolve("cli.diagnostic.no_snapshot"));
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
            Console.WriteLine(Loc().Resolve("cli.sim.resume_failed", "error",
                ack.Error is null ? Loc().Resolve("cli.fallback.failure") : Loc().ResolveWire(ack.Error)));
            return Task.FromResult(1);
        }

        var scenarioPath = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1] : null;
        var crash = args.Any(a => a == "--crash");
        if (scenarioPath is not null && !File.Exists(scenarioPath))
        {
            Console.WriteLine(Loc().Resolve("cli.sim.no_scenario", "path", scenarioPath));
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
        if (jsonOutput)
        {
            // A simulated crash is a command failure, but its committed events are still useful
            // output: preserve the JSON-lines contract and report an interrupted outcome.
            return RenderEvents(client, jsonOutput: true, exitCode: 1, scenarioName: scenarioName);
        }
        Console.WriteLine(Loc().Resolve("cli.sim.failed", "error",
            result.Error is null ? Loc().Resolve("cli.fallback.failure") : Loc().ResolveWire(result.Error)));
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
        if (events.Count == 0) Console.WriteLine(Loc().Resolve("cli.sim.ok_no_events"));
        return Task.FromResult(exitCode);
    }

    private static Task<int> RunTui(string[] args)
    {
        var client = Runtime.Connect(CancellationToken.None);
        if (args.Any(a => a == "--sim"))
        {
            var payload = "{" + JsonObj.Field("cmd", "sim") + ","
                + JsonObj.Field("scenario", "multi-item-plan") + "}";
            var result = client.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
            if (result.Status != "ok")
            {
                Console.WriteLine(Loc().Resolve("cli.tui.sim_failed", "error",
                    result.Error is null ? Loc().Resolve("cli.fallback.failure") : Loc().ResolveWire(result.Error)));
                return Task.FromResult(1);
            }

            // --sim is explicitly headless: never initialize Terminal.Gui or wait for a TTY.
            Console.WriteLine(TuiApp.RenderNonInteractive(client, Loc().Locale));
            return Task.FromResult(0);
        }

        return Task.FromResult(TuiApp.Run(client, Loc().Locale, new TuiTurnHost(Runtime)));
    }

    private static Task<int> RunExplain(string[] args)
    {
        var question = args.Length >= 2 ? args[1] : Loc().Resolve("cli.explain.default_question");
        var client = Runtime.Connect(CancellationToken.None);
        var state = client.Query("state", CancellationToken.None);
        var workingState = client.Query("workingState", CancellationToken.None);
        Console.WriteLine(Loc().Resolve("cli.explain.intro"));
        Console.WriteLine(Loc().Resolve("cli.explain.question", "question", question));
        if (state is not null)
        {
            var milestone = JsonObj.Parse(state.Json).TryGetValue("runState", out var value) ? value! : "M2";
            Console.WriteLine(Loc().Resolve("cli.explain.run", "run", milestone));
        }

        if (workingState is not null)
        {
            var text = JsonObj.Parse(workingState.Json).TryGetValue("workingState", out var value) ? value! : "";
            Console.WriteLine(Loc().Resolve("cli.explain.working_state"));
            Console.WriteLine(RedactJson(text, 900));
        }
        else Console.WriteLine(Loc().Resolve("cli.explain.no_context"));
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
            Console.WriteLine(Loc().Resolve("cli.trust.usage"));
            return Task.FromResult(2);
        }
        Runtime.SetWorkspaceTrusted(!revoke);
        Console.WriteLine(Loc().Resolve(revoke ? "cli.trust.revoked" : "cli.trust.saved"));
        return Task.FromResult(0);
    }

    private static Task<int> RunDoctor(string[] args)
    {
        if (args.Any(argument => argument == "--verify-journal"))
            return JournalCommands.VerifyJournal(new[] { "verify-journal" }
                .Concat(args.Where(argument => argument == "--json")).ToArray());

        var localization = new Localization(DoctorLocale(args));
        return Task.FromResult(OmniCliRuntime.Doctor(localization.Locale, Console.WriteLine,
            localization.Resolve));
    }

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

    private static async Task<int> RunAsk(string[] args)
    {
        var code = await Runtime.AskAsync(args.Length >= 2 ? args[1] : Loc().Resolve("cli.ask.default_question"),
            Console.WriteLine, CancellationToken.None);
        PrintStatusLine();
        return code;
    }

    private static async Task<int> RunAct(string[] args)
    {
        var code = await Runtime.ActAsync(args.Length >= 2 ? args[1] : "", Console.WriteLine, CancellationToken.None);
        PrintStatusLine();
        return code;
    }

    /// <summary>Status line resumida del plain renderer (ADR-0031 §3): tokens · costo · cuota, sin inventar datos.</summary>
    private static void PrintStatusLine()
    {
        if (Runtime.CurrentUsage() is not { } usage) return;
        Console.WriteLine(StatusLineText(usage));
    }

    internal static string StatusLineText(UsageSnapshot usage) =>
        string.Join(" · ", new[]
        {
            usage.SessionTokenMeasurement is { } measurement
                ? UsagePresentation.Tokens(measurement) : UsagePresentation.Tokens(usage.SessionTokens),
            UsagePresentation.Cost(usage.SessionCost),
            UsagePresentation.Remaining(usage.Remaining),
            UsagePresentation.AccountQuota(usage.AccountQuota),
        }.Where(part => part is not null));

    private static void PrintUsage()
    {
        Console.WriteLine(Loc().Resolve("cli.usage.title"));
        Console.WriteLine();
        Console.WriteLine(Loc().Resolve("cli.usage.heading"));
        Console.WriteLine(Loc().Resolve("cli.usage.sim"));
        Console.WriteLine(Loc().Resolve("cli.usage.act"));
        Console.WriteLine(Loc().Resolve("cli.usage.ask"));
        Console.WriteLine(Loc().Resolve("commands.context.help"));
        Console.WriteLine(Loc().Resolve("commands.tools.help"));
        Console.WriteLine(Loc().Resolve("commands.explain.help"));
        Console.WriteLine(Loc().Resolve("cli.usage.model"));
        Console.WriteLine(Loc().Resolve("cli.usage.verify_journal"));
        Console.WriteLine(Loc().Resolve("cli.usage.session_purge"));
        Console.WriteLine(Loc().Resolve("cli.usage.gc"));
        Console.WriteLine(Loc().Resolve("cli.usage.audit_purge"));
        Console.WriteLine(Loc().Resolve("permissions.help"));
        Console.WriteLine(Loc().Resolve("interaction.resolve.usage"));
        Console.WriteLine(Loc().Resolve("cli.usage.help"));
    }
}
