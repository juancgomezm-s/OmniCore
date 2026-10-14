using OmniCore.Abstractions;
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
        if ((args.Length == 2 || args.Length == 4 && args[2] == "--locale")
            && args[0] == "host" && args[1] == "--stdio")
        {
            var protocolOutput = Console.OpenStandardOutput();
            Console.SetOut(TextWriter.Null);
            var hostLocale = UiLocaleCodes.NormalizeOrDefault(args.Length == 4
                ? args[3] : Environment.GetEnvironmentVariable("OMNI_LOCALE"));
            var hostText = new Localization(hostLocale);
            try
            {
                var client = Runtime.Connect(CancellationToken.None);
                return Task.FromResult(StdioProtocolHost.Run(client, Console.OpenStandardInput(),
                    protocolOutput, Console.Error, localizeDiagnostic: key => hostText.Resolve(key)));
            }
            catch (Exception)
            {
                Console.Error.WriteLine(hostText.Resolve("stdio.host.start_failed"));
                return Task.FromResult(1);
            }
        }

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
            return RunDefaultInvocationAsync(
                isInteractiveTerminal: !Console.IsInputRedirected && !Console.IsOutputRedirected,
                launchTui: () => RunTui(args));
        }

        var aliases = PolicyPreferencesHost.Create(Environment.CurrentDirectory).Read().CommandAliases;
        var userAliases = new UserCommandAliases(aliases);
        var withUserAlias = userAliases.NormalizeCli(args);
        var userAliasApplied = !ReferenceEquals(withUserAlias, args);
        args = HumanCommandAliases.NormalizeCli(withUserAlias);
        var command = args[0];
        if (command.StartsWith("/", StringComparison.Ordinal)) return RunTypedCommand(args);
        if (command == "sim") return RunSim(args);
        if (command == "stdio") return RunStdioClientAsync(args);
        if (command is "explain" or "explore") return RunExplain(args);
        if (command == "ask") return RunAsk(args);
        if (command == "act") return RunAct(args);
        if (command == "model") return ModelPolicyCommands.Run(args, selectedLocalization: Loc());
        if (command == "worktree") return WorktreeCommands.RunAsync(args, CancellationToken.None, Loc());
        if (command == "trust") return RunTrust(args);
        if (command == "mcp") return RunMcp(args);
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

        // An explicit user alias is a command route, never free-form model input. A bare
        // command target is resolved through the same typed command path as /target; this
        // also lets extension-qualified ids reach the Host command catalog and rejects
        // unknown targets without invoking the model.
        if (userAliasApplied)
            return RunTypedCommand(new[] { "/" + command }.Concat(args.Skip(1)).ToArray());

        Console.WriteLine(Loc().Resolve("cli.ask_assumed", "command", command));
        return RunAsk(new[] { "ask", string.Join(" ", args) });
    }

    private static Task<int> RunStdioClientAsync(string[] args)
    {
        if (args.Length < 2 || args[1] is not ("sim" or "query" or "input"))
        {
            Console.WriteLine(Loc().Resolve("cli.stdio.usage"));
            return Task.FromResult(2);
        }

        if (args[1] == "query" && (args.Length != 3 || !StdioProtocol.IsSupportedQuery(args[2])))
        {
            Console.WriteLine(Loc().Resolve("cli.stdio.query_help"));
            return Task.FromResult(2);
        }
        if (args[1] == "input" && args.Length < 3)
        {
            Console.WriteLine(Loc().Resolve("cli.stdio.input_usage"));
            return Task.FromResult(2);
        }
        if (args[1] == "sim" && args.Skip(2).Any(value => value is not ("--resume" or "--json")))
        {
            Console.WriteLine(Loc().Resolve("cli.stdio.sim_usage"));
            return Task.FromResult(2);
        }

        StdioOmniClient client;
        try
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Current executable path is unavailable.");
            var isDotnetHost = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            var executable = processPath;
            var childArgs = new List<string>();
            if (isDotnetHost)
            {
                var entryAssembly = Path.Combine(AppContext.BaseDirectory, "omni.dll");
                if (!File.Exists(entryAssembly))
                    throw new InvalidOperationException("Current CLI assembly path is unavailable.");
                childArgs.Add(entryAssembly);
            }
            childArgs.Add("host");
            childArgs.Add("--stdio");
            childArgs.Add("--locale");
            childArgs.Add(Loc().Locale);
            client = StdioOmniClient.Start(executable, childArgs, Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException
            or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            Console.Error.WriteLine(Loc().Resolve("cli.stdio.connect_failed"));
            return Task.FromResult(1);
        }

        using (client)
        {
            try
            {
            switch (args[1])
            {
                case "query":
                    if (args.Length != 3 || !StdioProtocol.IsSupportedQuery(args[2]))
                    {
                        Console.WriteLine(Loc().Resolve("cli.stdio.query_help"));
                        return Task.FromResult(2);
                    }
                    var query = client.Query(args[2], CancellationToken.None);
                    if (query is null) return Task.FromResult(1);
                    Console.WriteLine(query.Json);
                    return Task.FromResult(0);
                case "input":
                    if (args.Length < 3)
                    {
                        Console.WriteLine(Loc().Resolve("cli.stdio.input_usage"));
                        return Task.FromResult(2);
                    }
                    var input = string.Join(" ", args.Skip(2));
                    var inputPayload = "{" + JsonObj.Field("cmd", "session.input") + "," + JsonObj.Field("text", input) + "}";
                    var inputAck = client.Send(WireEnvelope.Command(Ids.NewV7(), inputPayload), CancellationToken.None);
                    Console.WriteLine("{" + JsonObj.Field("status", inputAck.Status) + "," + JsonObj.Field("outcome",
                        inputAck.Outcome?.Kind.ToString() ?? "Unknown") + "," + JsonObj.FieldRaw("firstSeq",
                        inputAck.FirstSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
                        + "," + JsonObj.FieldRaw("lastSeq",
                        inputAck.LastSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + "}");
                    if (!IsStdioAckSuccessful(inputAck))
                    {
                        Console.Error.WriteLine(inputAck.Error ?? Loc().Resolve("cli.stdio.command_rejected"));
                        return Task.FromResult(1);
                    }
                    foreach (var envelope in client.SubscribeSince(inputAck.FirstSeq ?? 1))
                        new JsonRenderer().Emit(envelope);
                    var stateAfterInput = client.Query("state", CancellationToken.None);
                    if (stateAfterInput is not null) Console.WriteLine(stateAfterInput.Json);
                    return Task.FromResult(0);
                default:
                    var resume = args.Skip(2).Contains("--resume", StringComparer.Ordinal);
                    var jsonOutput = args.Skip(2).Contains("--json", StringComparer.Ordinal);
                    if (args.Skip(2).Any(value => value is not ("--resume" or "--json")))
                    {
                        Console.WriteLine(Loc().Resolve("cli.stdio.sim_usage"));
                        return Task.FromResult(2);
                    }
                    var simCommand = resume
                        ? "{" + JsonObj.Field("cmd", "sim.resume") + "}"
                        : "{" + JsonObj.Field("cmd", "sim") + "," + JsonObj.Field("scenario", "multi-item-plan") + "}";
                    var ack = client.Send(WireEnvelope.Command(Ids.NewV7(), simCommand), CancellationToken.None);
                    if (!IsStdioAckSuccessful(ack))
                    {
                        Console.Error.WriteLine(ack.Error ?? Loc().Resolve("cli.stdio.command_rejected"));
                        return Task.FromResult(1);
                    }
                    return RenderEvents(client, jsonOutput, 0, "stdio-sim");
            }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException
                or ObjectDisposedException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                Console.Error.WriteLine(Loc().Resolve("cli.stdio.connect_failed"));
                return Task.FromResult(1);
            }
        }
    }

    internal static bool IsStdioAckSuccessful(CommandAck ack) =>
        ack.Status == "ok" && ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Rejected;

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

    private static Task<int> RunMcp(string[] args)
    {
        if (args.Length == 1 || args is ["mcp", "list"])
        {
            var query = Runtime.Connect(CancellationToken.None).Query("mcp", CancellationToken.None);
            if (query is null)
            {
                Console.WriteLine(Loc().Resolve("cli.no_data", "command", "mcp"));
                return Task.FromResult(1);
            }
            RenderDiagnostic("mcp", query.Json);
            return Task.FromResult(0);
        }
        if (args.Length >= 4 && args[1] == "add-stdio")
        {
            try
            {
                Runtime.AddMcpStdio(args[2], args[3], args.Skip(4).ToArray());
                Console.WriteLine(Loc().Resolve("mcp.add.done", "id", args[2]));
                return Task.FromResult(0);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException
                or ArgumentException or InvalidDataException or UnauthorizedAccessException)
            { Console.WriteLine(Loc().Resolve("mcp.trust.failed", "diagnostic", exception.Message)); return Task.FromResult(1); }
        }
        if (args.Length == 4 && args[1] == "add-http")
        {
            try
            {
                Runtime.AddMcpHttp(args[2], args[3]);
                Console.WriteLine(Loc().Resolve("mcp.add.done", "id", args[2]));
                return Task.FromResult(0);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException
                or ArgumentException or InvalidDataException or UnauthorizedAccessException)
            { Console.WriteLine(Loc().Resolve("mcp.trust.failed", "diagnostic", exception.Message)); return Task.FromResult(1); }
        }
        if (args.Length == 3 && args[1] == "credential")
        {
            var secret = ReadMcpSecret();
            if (secret is null) return Task.FromResult(2);
            try
            {
                Runtime.SetMcpCredential(args[2], secret, CancellationToken.None);
                Console.WriteLine(Loc().Resolve("mcp.credential.done", "id", args[2]));
                return Task.FromResult(0);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException
                or ArgumentException or InvalidDataException or UnauthorizedAccessException)
            { Console.WriteLine(Loc().Resolve("mcp.trust.failed", "diagnostic", exception.Message)); return Task.FromResult(1); }
        }
        if (args.Length != 3 || args[1] is not ("trust" or "revoke"))
        {
            Console.WriteLine(Loc().Resolve("mcp.usage"));
            return Task.FromResult(2);
        }
        try
        {
            Runtime.SetMcpTrust(args[2], trusted: args[1] == "trust");
            Console.WriteLine(Loc().Resolve(args[1] == "trust" ? "mcp.trust.done" : "mcp.revoke.done",
                "id", args[2]));
            return Task.FromResult(0);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException
            or InvalidDataException or UnauthorizedAccessException)
        {
            Console.WriteLine(Loc().Resolve("mcp.trust.failed", "diagnostic", exception.Message));
            return Task.FromResult(1);
        }
    }

    private static string? ReadMcpSecret()
    {
        Console.Write(Loc().Resolve("mcp.credential.prompt"));
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return null; }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        return value.ToString();
    }

    private static Task<int> RunTypedCommand(string[] args)
    {
        if (!CommandLineParser.TryParseArguments(args, out var parsed) || parsed is null)
        {
            Console.WriteLine(Loc().Resolve("cli.invalid_command"));
            return Task.FromResult(2);
        }
        var invocation = parsed!;
        if (invocation.Name == "openrouter")
            return RunOpenRouterCommandAsync(invocation.Arguments.ToArray(), CreateOpenRouterHost(),
                ReadOpenRouterKeyAsync, CancellationToken.None);
        var client = Runtime.Connect(CancellationToken.None);
        // ADR-0024: el resto de ClientCommands (/help, /exit, /plan, /tasks, /events, /permissions, /cancel,
        // /interrupt) se resuelven a su ClientAction; /context y /tools conservan su render propio de abajo.
        if (invocation.Name is not ("context" or "tools" or "extensions" or "hooks")
            && ClientCommandRegistry.Default.TryResolve(invocation, out var clientAction) && clientAction is not null)
        {
            var context = new ClientContext(client, Loc(), PermissionsListing);
            var result = new ClientActionHandler().ExecuteAsync(clientAction, context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Console.WriteLine(result.Text);
            return Task.FromResult(0);
        }
        if (invocation.Name is "context" or "tools" or "extensions" or "hooks")
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

    private static ITuiProviderConnectionHost CreateOpenRouterHost()
        => OmniHost.CreateTuiProviderConnectionHost();

    private static async Task<string?> ReadOpenRouterKeyAsync(CancellationToken cancellationToken)
    {
        Console.Write(Loc().Resolve("openrouter.cli.keyPrompt"));
        if (Console.IsInputRedirected) return await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var key = new System.Text.StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyInfo = Console.ReadKey(intercept: true);
            if (keyInfo.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (keyInfo.Key == ConsoleKey.Backspace) { if (key.Length > 0) key.Length--; continue; }
            if (!char.IsControl(keyInfo.KeyChar)) key.Append(keyInfo.KeyChar);
        }
        return key.ToString();
    }

    internal static async Task<int> RunOpenRouterCommandAsync(string[] args, ITuiProviderConnectionHost host,
        Func<CancellationToken, Task<string?>> readKey, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] == "status")
            return await RunClientAction([], status: true).ConfigureAwait(false);
        var operation = args[0].ToLowerInvariant();
        if (operation == "credits" && args.Length <= 2)
            return await RunClientAction(args.Skip(1).ToArray(), status: false).ConfigureAwait(false);
        try
        {
            switch (operation)
            {
                case "connect" when args.Length is 1 or 2:
                {
                    var providerId = args.Length == 2 ? args[1] : SelectSingleProvider(host, cancellationToken);
                    if (providerId is null) return Usage();
                    var key = await readKey(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(key))
                    { Console.WriteLine(Loc().Resolve("openrouter.cli.keyMissing")); return 2; }
                    var result = await host.ConnectAsync(providerId, key, validate: true, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(Loc().Resolve(result.State == ProviderConnectionState.Connected
                        ? "openrouter.cli.connected" : "openrouter.cli.connectionFailed"));
                    return result.State == ProviderConnectionState.Connected ? 0 : 1;
                }
                case "test" when args.Length == 2:
                {
                    var result = await host.TestAsync(args[1], cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(Loc().Resolve(result.State == ProviderConnectionState.Connected
                        ? "openrouter.cli.testPassed" : "openrouter.cli.testFailed"));
                    return result.State == ProviderConnectionState.Connected ? 0 : 1;
                }
                case "disconnect" when args.Length == 2:
                    host.Disconnect(args[1], cancellationToken);
                    Console.WriteLine(Loc().Resolve("openrouter.cli.disconnected"));
                    return 0;
                case "catalog" when args.Length is >= 2 and <= 3:
                    return RenderOpenRouterCatalog(host, args[1], args.ElementAtOrDefault(2), cancellationToken);
                case "refresh" when args.Length is >= 2 and <= 3:
                {
                    if (host is not ITuiOpenRouterCatalogHost catalog)
                        throw new ProviderConnectionException("unsupported", "OpenRouter catalog is unavailable.");
                    var page = await catalog.RefreshOpenRouterCatalogAsync(args[1], args.ElementAtOrDefault(2), 0, 30,
                        cancellationToken).ConfigureAwait(false);
                    PrintOpenRouterCatalog(page);
                    return page.Error is null ? 0 : 1;
                }
                case "select" or "register" when args.Length == 3:
                {
                    if (host is not ITuiOpenRouterCatalogHost catalog)
                        throw new ProviderConnectionException("unsupported", "OpenRouter catalog is unavailable.");
                    var added = catalog.RegisterOpenRouterModels(args[1], [args[2]], cancellationToken);
                    Console.WriteLine(Loc().Resolve("openrouter.cli.registered", new Dictionary<string, string>
                    {
                        ["model"] = args[2],
                        ["count"] = added.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }));
                    return 0;
                }
                default: return Usage();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or ProviderConnectionException or KeyNotFoundException)
        {
            Console.WriteLine(Loc().Resolve("openrouter.cli.operationFailed", "diagnostic", ex.Message));
            return 1;
        }

        int Usage()
        {
            Console.WriteLine(Loc().Resolve("openrouter.command.usage"));
            return 2;
        }

        async Task<int> RunClientAction(string[] actionArgs, bool status)
        {
            var client = Runtime.Connect(cancellationToken);
            var result = new ClientActionHandler().ExecuteAsync(
                new ClientActionInvocation(ClientActionIds.OpenRouterInspect,
                    status ? [] : new[] { "credits" }.Concat(actionArgs).ToArray(), "cli"),
                new ClientContext(client, Loc()), cancellationToken).AsTask().GetAwaiter().GetResult();
            Console.WriteLine(result.Text);
            return 0;
        }
    }

    private static string? SelectSingleProvider(ITuiProviderConnectionHost host, CancellationToken cancellationToken)
    {
        var rows = host.List(cancellationToken).Where(row => row.Backend == "openrouter" && row.CanConnect).ToArray();
        return rows.Length == 1 ? rows[0].ProviderId : null;
    }

    private static int RenderOpenRouterCatalog(ITuiProviderConnectionHost host, string providerId, string? search,
        CancellationToken cancellationToken)
    {
        if (host is not ITuiOpenRouterCatalogHost catalog)
            throw new ProviderConnectionException("unsupported", "OpenRouter catalog is unavailable.");
        PrintOpenRouterCatalog(catalog.BrowseOpenRouterCatalog(providerId, search, 0, 30, cancellationToken));
        return 0;
    }

    private static void PrintOpenRouterCatalog(OpenRouterCatalogPage page)
    {
        Console.WriteLine(Loc().Resolve("openrouter.status.title") + " · " + page.ProviderId);
        foreach (var model in page.Models)
            Console.WriteLine(model.Id + (model.Name is null ? "" : " · " + model.Name));
        if (page.Stale) Console.WriteLine(Loc().Resolve("openrouter.catalog.stale"));
        if (page.Error is not null) Console.WriteLine(Loc().Resolve("openrouter.catalog.failed"));
    }

    internal static void RenderDiagnostic(string name, string json)
    {
        Console.WriteLine(name switch
        {
            "context" => Loc().Resolve("cli.diagnostic.context"),
            "tools" => Loc().Resolve("cli.diagnostic.tools"),
            "hooks" => Loc().Resolve("cli.diagnostic.hooks"),
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
            else if (name == "hooks")
            {
                if (root.TryGetProperty("workspaceTrusted", out var trusted) && !trusted.GetBoolean())
                    Console.WriteLine("  " + Loc().Resolve("cli.diagnostic.hooks.untrusted"));
                var hooks = root.GetProperty("hooks").EnumerateArray().ToArray();
                if (hooks.Length == 0) Console.WriteLine("  " + Loc().Resolve("cli.diagnostic.hook.no_entries"));
                foreach (var hook in hooks)
                {
                    var state = KnownDiagnosticValue("cli.diagnostic.hook.state.", hook.GetProperty("state").GetString(),
                        "enabled", "consentrequired", "workspacetrustrequired", "invalidmanifest", "incompatibleapi", "untrusted");
                    Console.WriteLine("  " + hook.GetProperty("id").GetString() + " · "
                        + Loc().Resolve("cli.diagnostic.hook.point") + " " + hook.GetProperty("point").GetString()
                        + " · " + state);
                    if (hook.TryGetProperty("diagnostic", out var diagnostic)
                        && diagnostic.ValueKind == System.Text.Json.JsonValueKind.String)
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
        if (value is null) return Loc().Resolve("cli.runtime.status.unknown", "value", Loc().Resolve("cli.diagnostic.value.unavailable"));
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
        if (value is null) return Loc().Resolve("cli.runtime.status.unknown", "value", Loc().Resolve("cli.diagnostic.value.unavailable"));
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

    internal static Task<int> RunDefaultInvocationAsync(bool isInteractiveTerminal, Func<Task<int>> launchTui)
    {
        ArgumentNullException.ThrowIfNull(launchTui);
        if (isInteractiveTerminal) return launchTui();

        Console.Error.WriteLine(Loc().Resolve("cli.tui.interactive_required"));
        return Task.FromResult(2);
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
        Console.WriteLine(Loc().Resolve("cli.usage.tui"));
        Console.WriteLine(Loc().Resolve("cli.usage.sim"));
        Console.WriteLine(Loc().Resolve("cli.usage.act"));
        Console.WriteLine(Loc().Resolve("cli.usage.ask"));
        Console.WriteLine(Loc().Resolve("cli.usage.explain"));
        Console.WriteLine(Loc().Resolve("commands.context.help"));
        Console.WriteLine(Loc().Resolve("commands.tools.help"));
        Console.WriteLine(Loc().Resolve("commands.extensions.help"));
        Console.WriteLine(Loc().Resolve("extensions.usage"));
        Console.WriteLine(Loc().Resolve("commands.hooks.help"));
        Console.WriteLine(Loc().Resolve("hooks.usage"));
        Console.WriteLine(Loc().Resolve("commands.mcp.help"));
        Console.WriteLine(Loc().Resolve("mcp.usage"));
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
