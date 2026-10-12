using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Cli;

/// <summary>Despacha argumentos, procesa el protocolo y delega la composición al Host.</summary>
public sealed class CliApp
{
    private static OmniCliRuntime Runtime = CreateRuntime();
    private static LocaleSelection _localeSelection = new("es", LocaleSelectionSource.Default);
    private static Localization _localization = Localization.Spanish();
    internal static LocaleSelection ActiveLocaleSelection => _localeSelection;
    internal static string ActiveEffectiveLocale => _localization.Locale;

    // Internal injection point for end-to-end tests. Production always uses CreateRuntime().
    internal static OmniCliRuntime UseRuntimeForTests(OmniCliRuntime runtime)
    {
        var previous = Runtime;
        Runtime = runtime;
        // This seam is also used to restore the prior runtime. Do not leave the
        // previous fixture's language preview in the shared CLI presentation state.
        _localeSelection = new(UiLocaleCodes.NormalizeOrDefault(runtime.UiLocale), LocaleSelectionSource.Default);
        _localization = new Localization(_localeSelection.Locale);
        return previous;
    }

    private static OmniCliRuntime CreateRuntime()
    {
        var runtime = OmniCliRuntime.Create(".");
        runtime.Localize = (key, args) => Loc().Resolve(key, args);
        runtime.QuestionnaireInput = Console.IsInputRedirected ? null
            : prompt => PlainQuestionnaireForm.Read(prompt, Loc());
        return runtime;
    }

    private static Localization Loc() => _localization;

    private static void UpdateLocaleConsumers()
    {
        Runtime.UiLocale = _localization.Locale;
        Runtime.Localize = (key, values) => Loc().Resolve(key, values);
        Runtime.QuestionnaireInput = Console.IsInputRedirected ? null
            : prompt => PlainQuestionnaireForm.Read(prompt, Loc());
    }

    internal static void UpdateLocaleFromPreferences(string effectiveLocale)
    {
        var locale = _localeSelection.HasProcessOverride
            ? _localeSelection.Locale : UiLocaleCodes.NormalizeOrDefault(effectiveLocale);
        _localization = new Localization(locale);
        UpdateLocaleConsumers();
    }

    internal static void SetLocaleForTests(string locale)
    {
        _localeSelection = new(UiLocaleCodes.NormalizeOrDefault(locale), LocaleSelectionSource.UserSetting);
        _localization = new Localization(_localeSelection.Locale);
        UpdateLocaleConsumers();
    }

    public static Task<int> RunAsync(string[] args)
    {
        var parsedLocale = LocaleArgumentParser.Parse(args);
        args = parsedLocale.Arguments;
        var savedLocale = ReadUserLocale();
        _localeSelection = LocaleSelectionResolver.Resolve(parsedLocale.ExplicitLocale,
            Environment.GetEnvironmentVariable("OMNI_LOCALE"),
            savedLocale?.IsConfigured == true ? savedLocale.Locale : null);
        _localization = new Localization(_localeSelection.Locale);
        UpdateLocaleConsumers();
        ReportLocaleDiagnostics(parsedLocale.Diagnostics, savedLocale);
        if (parsedLocale.HasMissingLocaleValue)
            return Task.FromResult(2);

        if (args.Length == 0)
        {
            PrintUsage();
            return Task.FromResult(1);
        }

        args = HumanCommandAliases.NormalizeCli(args);
        var command = args[0];
        if (command.StartsWith("/", StringComparison.Ordinal)) return RunTypedCommand(args);
        if (command == "sim") return RunSim(args);
        if (command is "explain" or "explore") return RunExplain(args);
        if (command == "ask") return RunAsk(args);
        if (command == "act") return RunAct(args);
        if (command == "model") return ModelPolicyCommands.Run(args, selectedLocalization: Loc());
        if (command == "worktree") return WorktreeCommands.RunAsync(args, CancellationToken.None, Loc());
        if (command == "trust") return RunTrust(args);
        if (command == "extensions") return RunExtensions(args);
        if (command is "login" or "logout" && args.Length >= 2 && args[1] == "claude")
        {
            var providerArgs = args.Skip(2).Where(a => a != "--manual").ToArray();
            if (providerArgs.Length > 1 || providerArgs.Any(a => a.StartsWith("-", StringComparison.Ordinal))
                || (command == "logout" && args.Contains("--manual")))
            { Console.WriteLine(Loc().Resolve("claude.oauth.cli.usage")); return Task.FromResult(2); }
            var provider = providerArgs.FirstOrDefault() ?? "anthropic";
            return command == "login"
                ? Runtime.LoginClaudeAsync(provider, args.Contains("--manual"),
                    token => Console.In.ReadLineAsync(token).AsTask(), Console.WriteLine, CancellationToken.None)
                : Task.FromResult(Runtime.LogoutClaude(provider, Console.WriteLine, CancellationToken.None));
        }
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
        if (command == "verify-journal") return JournalCommands.VerifyJournal(args, selectedLocalization: Loc());
        if (command == "session" && args.Length >= 2 && args[1] == "purge")
            return MaintenanceCommands.SessionPurge(args, selectedLocalization: Loc());
        if (command == "gc") return MaintenanceCommands.Gc(args, selectedLocalization: Loc());
        if (command == "audit" && args.Length >= 2 && args[1] == "purge")
            return MaintenanceCommands.AuditPurge(args, selectedLocalization: Loc());
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

    private static Task<int> RunExtensions(string[] args)
    {
        if (args.Length == 1)
        {
            var query = Runtime.Connect(CancellationToken.None).Query("extensions", CancellationToken.None);
            if (query is null)
            {
                Console.WriteLine(Loc().Resolve("cli.no_data", "command", "extensions"));
                return Task.FromResult(1);
            }
            RenderDiagnostic("extensions", query.Json);
            return Task.FromResult(0);
        }
        if (args.Length != 3 || args[1] is not ("trust" or "revoke"))
        {
            Console.WriteLine(Loc().Resolve("extensions.usage"));
            return Task.FromResult(2);
        }
        try
        {
            var result = Runtime.SetProjectExtensionConsent(args[2], grant: args[1] == "trust");
            Console.WriteLine(result.Granted
                ? Loc().Resolve("cli.extension.consent.granted", new Dictionary<string, string>
                {
                    ["id"] = result.ExtensionId,
                    ["capabilities"] = result.Capabilities.Count == 0 ? "—" : string.Join(", ", result.Capabilities),
                })
                : Loc().Resolve("cli.extension.consent.revoked", "id", result.ExtensionId));
            return Task.FromResult(0);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            Console.WriteLine(Loc().Resolve("cli.extension.consent.failed", "diagnostic", exception.Message));
            return Task.FromResult(1);
        }
    }

    private static Task<int> RunTypedCommand(string[] args)
    {
        if (!CommandLineParser.TryParseArguments(args, out var parsed) || parsed is null)
        {
            Console.WriteLine(Loc().Resolve("cli.invalid_command"));
            return Task.FromResult(2);
        }
        var invocation = parsed!;
        var client = Runtime.Connect(CancellationToken.None);
        // ADR-0024: el resto de ClientCommands (/help, /exit, /plan, /tasks, /events, /permissions, /cancel,
        // /interrupt) se resuelven a su ClientAction; /context y /tools conservan su render propio de abajo.
        if (invocation.Name is not ("context" or "tools" or "extensions")
            && ClientCommandRegistry.Default.TryResolve(invocation, out var clientAction) && clientAction is not null)
        {
            var context = new ClientContext(client, Loc(), PermissionsListing);
            var result = new ClientActionHandler().ExecuteAsync(clientAction, context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Console.WriteLine(result.Text);
            return Task.FromResult(0);
        }
        if (invocation.Name is "context" or "tools" or "extensions")
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
            Console.WriteLine(FormatCommandAckError(invocation.Name, ack.Error));
            return Task.FromResult(2);
        }
        var expanded = client.Query("commandOutcome", CancellationToken.None)?.Json ?? "{}";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(expanded);
            var outcome = document.RootElement.GetProperty("outcome");
            if (outcome.TryGetProperty("kind", out var kind) && kind.GetString() == "workflowRequested")
            {
                var workflow = new WorkflowRequestDto(outcome.GetProperty("workflowId").GetString() ?? "",
                    outcome.GetProperty("version").GetString() ?? "",
                    outcome.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString() ?? "").ToArray());
                var commandId = outcome.GetProperty("commandId").GetString() ?? "";
                return Runtime.ExecuteWorkflowAsync(workflow, commandId, Console.WriteLine, CancellationToken.None);
            }
            var text = outcome.GetProperty("text").GetString() ?? "";
            var origin = outcome.GetProperty("origin").GetString() ?? "";
            if (outcome.TryGetProperty("toolName", out var toolName) && toolName.GetString() is { Length: > 0 } forcedTool)
                return Runtime.ExecuteExtensionCommandAsync(text, forcedTool, Console.WriteLine, CancellationToken.None);
            Console.WriteLine("[" + FormatCommandOrigin(origin) + "]");
            return Runtime.AskAsync(text, Console.WriteLine, CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or KeyNotFoundException)
        {
            Console.WriteLine(Loc().Resolve("cli.command_invalid_result", "command", invocation.Name));
            return Task.FromResult(1);
        }
    }

    internal static void RenderDiagnostic(string name, string json)
    {
        Console.WriteLine(name switch
        {
            "context" => Loc().Resolve("cli.diagnostic.context"),
            "tools" => Loc().Resolve("cli.diagnostic.tools"),
            _ => Loc().Resolve("cli.diagnostic.extensions"),
        });
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (name == "context")
            {
                var snapshot = root.GetProperty("snapshot");
                Console.WriteLine(Loc().Resolve("cli.diagnostic.fingerprint") + ": " + snapshot.GetProperty("fingerprint").GetString());
                var accuracy = KnownDiagnosticValue("cli.diagnostic.context.accuracy.", snapshot.GetProperty("tokenAccuracy").GetString(),
                    "exact", "estimated", "unknown");
                Console.WriteLine(Loc().Resolve("cli.diagnostic.tokens") + ": " + snapshot.GetProperty("tokenCount").GetInt32() + "/"
                    + snapshot.GetProperty("tokenBudget").GetInt64() + " ("
                    + accuracy + ")");
                foreach (var item in snapshot.GetProperty("items").EnumerateArray())
                    Console.WriteLine("  " + KnownDiagnosticValue("cli.diagnostic.context.kind.", item.GetProperty("kind").GetString(),
                        "system", "task", "decision", "constraint", "usermessage", "assistantmessage", "toolresult",
                        "file", "skill", "knowledge", "subagentresult", "summary", "checkpoint", "workingstate", "memory") + " · "
                        + item.GetProperty("contributor").GetString() + " · "
                        + item.GetProperty("tokens").GetInt32() + " " + Loc().Resolve("cli.diagnostic.estimated_tokens"));
                if (snapshot.TryGetProperty("diagnostics", out var diagnostics))
                    foreach (var item in diagnostics.EnumerateArray())
                        Console.WriteLine("  " + item.GetProperty("itemId").GetString() + " · "
                            + DiagnosticDecision(item.GetProperty("decision").GetString()) + " · "
                            + Loc().Resolve("cli.diagnostic.reason", "value", item.GetProperty("reason").GetString()));
            }
            else if (name == "extensions")
            {
                foreach (var extension in root.GetProperty("extensions").EnumerateArray())
                {
                    var id = extension.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                    var version = extension.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
                    var status = KnownDiagnosticValue("cli.diagnostic.extension.status.", extension.GetProperty("status").GetString(),
                        "ready", "workspacetrustrequired", "consentrequired", "invalidmanifest", "incompatibleapi", "untrusted");
                    Console.WriteLine("  " + (id ?? "<" + Loc().Resolve("cli.diagnostic.extension.invalid_id") + ">")
                        + (version is null ? "" : "@" + version) + " · " + status);
                    if (extension.TryGetProperty("diagnostic", out var diagnostic) && diagnostic.ValueKind == System.Text.Json.JsonValueKind.String)
                        Console.WriteLine("    " + FormatExternalDiagnostic(diagnostic.GetString()));
                }
            }
            else
            {
                Console.WriteLine(Loc().Resolve("cli.diagnostic.mode") + ": " + KnownDiagnosticValue("cli.diagnostic.mode.",
                    root.GetProperty("mode").GetString(), "plan", "act", "orchestrate"));
                foreach (var tool in root.GetProperty("tools").EnumerateArray())
                    Console.WriteLine("  " + tool.GetProperty("visibleName").GetString() + " → "
                        + tool.GetProperty("toolId").GetString() + " · "
                        + DiagnosticToolSource(tool.GetProperty("source").GetString()) + " · "
                        + DiagnosticToolEffect(tool.GetProperty("effectClass").GetString()) + " · "
                        + DiagnosticDecision(tool.GetProperty("decision").GetString()));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or KeyNotFoundException)
        {
            Console.WriteLine(Loc().Resolve("cli.diagnostic.no_snapshot"));
        }
    }

    private static string KnownDiagnosticValue(string keyPrefix, string? value, params string[] known)
    {
        if (value is null) return Loc().Resolve("cli.runtime.status.unknown", "value", "Unavailable");
        var code = value.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (known.Contains(code, StringComparer.Ordinal))
        {
            var key = keyPrefix + code;
            var localized = Loc().ResolveDetailed(key);
            if (!localized.UsedFallback && localized.Text != key) return localized.Text;
        }
        return Loc().Resolve("cli.runtime.status.unknown", "value", value);
    }

    private static string DiagnosticDecision(string? value)
    {
        if (value is null) return Loc().Resolve("cli.runtime.status.unknown", "value", "Unavailable");
        var code = value.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        if (code is "allow" or "allowed") return Loc().Resolve("cli.diagnostic.decision.allowed");
        if (code is "deny" or "denied") return Loc().Resolve("cli.diagnostic.decision.denied");
        if (code is "pending") return Loc().Resolve("cli.diagnostic.decision.pending");
        if (code == "ask") return Loc().Resolve("cli.diagnostic.decision.ask");
        if (code == "reject:plan mode") return Loc().Resolve("cli.diagnostic.decision.plan_rejected");
        var known = new[] { "included", "omitted", "truncated", "omittedbybudget", "truncatedbybudget",
            "pruned", "externalized", "compressed", "compacted" };
        if (known.Contains(code, StringComparer.Ordinal))
            return KnownDiagnosticValue("cli.diagnostic.decision.", code, known);
        return Loc().Resolve("cli.runtime.status.unknown", "value", value);
    }

    private static string DiagnosticToolSource(string? value) => KnownDiagnosticValue(
        "cli.diagnostic.tool.source.", value, "builtin", "extension");

    private static string DiagnosticToolEffect(string? value) => KnownDiagnosticValue(
        "cli.diagnostic.tool.effect.", value, "none", "rerunnable", "reconcilable", "nonidempotent", "read", "write", "execute");

    internal static string FormatCommandOrigin(string origin) => Loc().Resolve("cli.command.origin", "value", origin);

    internal static string FormatExternalDiagnostic(string? diagnostic) =>
        Loc().Resolve("cli.runtime.diagnostic.external", "value", diagnostic ?? "—");

    internal static string FormatCommandAckError(string command, string? error)
    {
        var diagnostic = error is null ? Loc().Resolve("cli.command_failed")
            : FormatExternalDiagnostic(Loc().ResolveWire(error));
        return Loc().Resolve("cli.command_ack_error",
            new Dictionary<string, string> { ["command"] = command, ["error"] = diagnostic });
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

        var projection = new ClientProjection(Loc());
        var state = ClientState.Empty();
        foreach (var envelope in events) state = projection.Apply(state, envelope);
        new PlainRenderer(Loc().Locale).Render(state);
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

        return Task.FromResult(TuiApp.Run(client, Loc().Locale, new TuiTurnHost(Runtime),
            localeSelection: _localeSelection, localeChanged: UpdateLocaleFromPreferences,
            localEngineSupervisor: Runtime.CreateTuiLocalEngineSupervisor()));
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
        ExplainContext(client);
        ExplainFingerprint(client);
        return Task.FromResult(0);
    }

    /// <summary>Resumen del contexto del último Turn: presupuesto y de dónde viene cada pieza (ADR-0029).</summary>
    private static void ExplainContext(IOmniClient client)
    {
        Console.WriteLine(Loc().Resolve("cli.explain.context"));
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                client.Query("context", CancellationToken.None)?.Json ?? "{\"snapshot\":null}");
            var snapshot = document.RootElement.GetProperty("snapshot");
            if (snapshot.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                Console.WriteLine(Loc().Resolve("cli.explain.no_snapshot"));
                return;
            }

            var items = snapshot.GetProperty("items").EnumerateArray().ToArray();
            Console.WriteLine(Loc().Resolve("cli.explain.tokens", new Dictionary<string, string>
            {
                ["tokens"] = snapshot.GetProperty("tokenCount").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["budget"] = snapshot.TryGetProperty("tokenBudget", out var budget) ? budget.ToString() : "?",
                ["accuracy"] = snapshot.TryGetProperty("tokenAccuracy", out var accuracy)
                    ? KnownDiagnosticValue("cli.diagnostic.context.accuracy.", accuracy.GetString(), "exact", "estimated", "unknown") : "?",
            }));
            foreach (var group in items.GroupBy(item => (Kind: item.GetProperty("kind").GetString() ?? "",
                Contributor: item.GetProperty("contributor").GetString() ?? "")))
            {
                var kind = KnownDiagnosticValue("cli.diagnostic.context.kind.", group.Key.Kind, "system", "task", "decision",
                    "constraint", "usermessage", "assistantmessage", "toolresult", "file", "skill", "knowledge",
                    "subagentresult", "summary", "checkpoint", "workingstate", "memory");
                Console.WriteLine("  " + kind + " · " + group.Key.Contributor + " ×" + group.Count());
            }
            var dropped = snapshot.TryGetProperty("diagnostics", out var diagnostics) ? diagnostics.GetArrayLength() : 0;
            if (dropped > 0)
                Console.WriteLine(Loc().Resolve("cli.explain.omitted", "count",
                    dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or KeyNotFoundException)
        {
            Console.WriteLine(Loc().Resolve("cli.diagnostic.no_snapshot"));
        }
    }

    /// <summary>La configuración efectiva que recibió el último Turn: componentes con su versión y hash (ADR-0017).</summary>
    private static void ExplainFingerprint(IOmniClient client)
    {
        Console.WriteLine(Loc().Resolve("cli.explain.fingerprint"));
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                client.Query("turnFingerprint", CancellationToken.None)?.Json ?? "{\"fingerprint\":null}");
            var fingerprint = document.RootElement.GetProperty("fingerprint");
            if (fingerprint.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                Console.WriteLine(Loc().Resolve("cli.explain.no_fingerprint"));
                return;
            }

            Console.WriteLine("  " + Loc().Resolve("cli.explain.model", "model", fingerprint.GetProperty("modelKey").GetString() ?? ""));
            foreach (var component in fingerprint.GetProperty("components").EnumerateArray())
            {
                var hash = component.GetProperty("hash").GetString() ?? "";
                Console.WriteLine("  " + component.GetProperty("name").GetString() + "@"
                    + component.GetProperty("version").GetString() + "  " + (hash.Length > 12 ? hash[..12] : hash));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or KeyNotFoundException)
        {
            Console.WriteLine(Loc().Resolve("cli.explain.no_fingerprint"));
        }
    }

    private static string RedactJson(string json, int max)
    {
        var redacted = OmniCliRuntime.RedactSensitive(json);
        return redacted.Length <= max ? redacted : redacted.Substring(0, max) + "…";
    }

    /// <summary>Listado de los grants del workspace (mismo texto que <c>omni permissions list</c>).</summary>
    internal static string PermissionsListing()
    {
        var result = Runtime.Permissions(new ListPermissionGrantsCommand(), CancellationToken.None);
        var lines = new List<string> { Loc().Resolve("permissions.heading") };
        if (result.Grants.Count == 0) lines.Add(Loc().Resolve("permissions.empty"));
        foreach (var grant in result.Grants)
        {
            var lifetimeKey = grant.Lifetime switch
            {
                "Run" => "interaction.permission.allow_run",
                "Workspace" => "interaction.permission.allow_workspace",
                _ => "interaction.permission.allow_once",
            };
            lines.Add(grant.Id + "  " + grant.ToolId + "  " + Loc().Resolve(lifetimeKey)
                + "  " + grant.ClaimsKey + (grant.Run is null ? "" : "  Run " + grant.Run));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static Task<int> RunPermissions(string[] args)
    {
        if (args.Length == 1 || args.Length == 2 && args[1] == "list")
        {
            Console.WriteLine(PermissionsListing());
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
                .Concat(args.Where(argument => argument == "--json")).ToArray(), selectedLocalization: Loc());

        var localization = Loc();
        return Task.FromResult(OmniCliRuntime.Doctor(localization.Locale, Console.WriteLine,
            localization.Resolve));
    }

    private static UiLocalePreferencesSnapshot? ReadUserLocale()
    {
        try
        {
            return UserLocaleConfiguration.ReadCurrentUser();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            return new UiLocalePreferencesSnapshot("unavailable", "es", ["unavailable"]);
        }
    }

    private static void ReportLocaleDiagnostics(IReadOnlyList<string> parserDiagnostics,
        UiLocalePreferencesSnapshot? savedLocale)
    {
        foreach (var diagnostic in parserDiagnostics)
            Console.Error.WriteLine(Loc().Resolve("cli.locale.missing"));
        if (_localeSelection.InvalidValue is { } invalid)
            Console.Error.WriteLine(Loc().Resolve("cli.locale.invalid", "value", invalid));
        if (savedLocale is null || savedLocale.Diagnostics.Count > 0)
        {
            Console.Error.WriteLine(Loc().Resolve("cli.locale.settings_unavailable"));
        }
        else if (_localeSelection.HasProcessOverride && savedLocale.IsConfigured)
        {
            Console.Error.WriteLine(Loc().Resolve("cli.locale.override", new Dictionary<string, string>
            {
                ["active"] = _localeSelection.Locale,
                ["saved"] = savedLocale.Locale,
            }));
        }
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
                ? UsagePresentation.Tokens(measurement, Loc().Locale) : UsagePresentation.Tokens(usage.SessionTokens, Loc().Locale),
            UsagePresentation.Cost(usage.SessionCost),
            UsagePresentation.Remaining(usage.Remaining, Loc().Locale),
            UsagePresentation.AccountQuota(usage.AccountQuota, Loc().Locale),
            UsagePresentation.Account(usage.Account, Loc()),
        }.Where(part => part is not null));

    private static void PrintUsage()
    {
        Console.WriteLine(Loc().Resolve("cli.usage.title"));
        Console.WriteLine();
        Console.WriteLine(Loc().Resolve("cli.usage.heading"));
        Console.WriteLine(Loc().Resolve("cli.usage.sim"));
        Console.WriteLine(Loc().Resolve("cli.usage.act"));
        Console.WriteLine(Loc().Resolve("cli.usage.ask"));
        Console.WriteLine(Loc().Resolve("cli.usage.explain"));
        Console.WriteLine(Loc().Resolve("commands.context.help"));
        Console.WriteLine(Loc().Resolve("commands.tools.help"));
        Console.WriteLine(Loc().Resolve("commands.extensions.help"));
        Console.WriteLine(Loc().Resolve("extensions.usage"));
        Console.WriteLine(Loc().Resolve("commands.explain.help"));
        Console.WriteLine(Loc().Resolve("commands.workflow.help"));
        Console.WriteLine(Loc().Resolve("cli.usage.model"));
        Console.WriteLine(Loc().Resolve("worktree.inspect.usage"));
        Console.WriteLine(Loc().Resolve("worktree.preview.usage"));
        Console.WriteLine(Loc().Resolve("worktree.undo.usage"));
        Console.WriteLine(Loc().Resolve("claude.oauth.cli.usage"));
        Console.WriteLine(Loc().Resolve("cli.usage.verify_journal"));
        Console.WriteLine(Loc().Resolve("cli.usage.session_purge"));
        Console.WriteLine(Loc().Resolve("cli.usage.gc"));
        Console.WriteLine(Loc().Resolve("cli.usage.audit_purge"));
        Console.WriteLine(Loc().Resolve("permissions.help"));
        Console.WriteLine(Loc().Resolve("interaction.resolve.usage"));
        Console.WriteLine(Loc().Resolve("cli.usage.help"));
    }
}
