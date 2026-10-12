using System.Diagnostics;
using System.Text.Json;
using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCoreCommandAcceptance;

internal static class Program
{
    private const int TimeoutSeconds = 12;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private sealed record Scenario(string Id, string Command, string Scope, string CallableBy,
        string ProposedPrompt, string Preconditions, string Expected, string Observed,
        string Evidence, string Status);

    private sealed record ProcessResult(int ExitCode, string Output, bool TimedOut, string Invocation);

    private static async Task<int> Main(string[] args)
    {
        var repositoryRoot = GetOption(args, "--repo-root") ?? FindRepositoryRoot(Environment.CurrentDirectory);
        var cliDll = GetOption(args, "--cli-dll") ?? Path.Combine(repositoryRoot,
            "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
        if (!File.Exists(cliDll))
        {
            Console.Error.WriteLine("CLI assembly not found: " + cliDll + ". Build this harness project first; it references OmniCore.Cli for build ordering.");
            return 2;
        }

        var runTag = DateTimeOffset.UtcNow.ToString("HHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        var resultsDirectory = Path.Combine(repositoryRoot, "TestResults");
        Directory.CreateDirectory(resultsDirectory);
        var reportPath = Path.Combine(resultsDirectory, "command-acceptance-20261012-" + runTag + ".jsonl");
        var logPath = Path.Combine(resultsDirectory, "command-acceptance-20261012-" + runTag + ".log");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "OmniCoreCommandAcceptance-" + runTag);
        var workspace = Path.Combine(fixtureRoot, "workspace");
        var data = Path.Combine(fixtureRoot, "data");
        var config = Path.Combine(fixtureRoot, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "providers.yaml"), "providers: {}\n");
        File.WriteAllText(Path.Combine(config, "models.yaml"), "models: {}\n");
        File.WriteAllText(Path.Combine(config, "settings.yaml"), "ui: { locale: es }\n");

        var catalogWorkspace = Path.Combine(fixtureRoot, "catalog-workspace");
        var catalogData = Path.Combine(fixtureRoot, "catalog-data");
        var catalogConfig = Path.Combine(fixtureRoot, "catalog-config");
        Directory.CreateDirectory(catalogWorkspace);
        Directory.CreateDirectory(catalogData);
        Directory.CreateDirectory(catalogConfig);
        File.WriteAllText(Path.Combine(catalogConfig, "providers.yaml"), """
            providers:
              acceptance-offline:
                family: OpenAiChatCompatible
                baseUrl: https://provider.invalid/v1
                auth: none
                billingMode: Local
            """);
        File.WriteAllText(Path.Combine(catalogConfig, "models.yaml"), """
            models:
              acceptance-offline-model:
                provider: acceptance-offline
                context: 8192
                maxOutput: 1024
                aliases: [acceptance-offline-alias]
            """);
        File.WriteAllText(Path.Combine(catalogConfig, "settings.yaml"), "ui: { locale: es }\n");

        var scenarios = new List<Scenario>();
        var transcript = new List<string>
        {
            "OmniCore command acceptance harness",
            "UTC: " + DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "Repository: " + repositoryRoot,
            "CLI: " + cliDll,
            "Fixture preserved: " + fixtureRoot,
            "No account credentials, real provider endpoints, login, refresh, model inference, or server probes are configured.",
        };

        AddParserCoverage(scenarios);
        AddSlashCallabilityRows(scenarios);

        // Every public ClientAction command is executed through the built CLI, against the empty fixture.
        var clientCommands = ClientCommandRegistry.Default.Commands;
        foreach (var (command, index) in clientCommands.Select((value, index) => (value, index)))
        {
            var locale = UiLocaleCodes.Supported[index % UiLocaleCodes.Supported.Count];
            var displayedCommand = HumanCommandAliases.DisplayName(command.Name, locale);
            var actionArgs = ClientActionArgs(command.Name);
            var result = await InvokeCli(cliDll, workspace, data, config,
                ["--locale", locale, "/" + displayedCommand, .. actionArgs], CancellationToken.None);
            var isRunControl = command.Name is "cancel" or "interrupt";
            var status = isRunControl ? (LooksLikeRunControlBlocked(result.Output) ? "Blocked" : "Failed")
                : result.TimedOut ? "Failed"
                : ValidateClientAction(command.Name, result.Output, result.ExitCode);
            var expected = isRunControl
                ? "No active Run exists; only a visible no-active/rejected result qualifies as Blocked, never success inferred from exit code."
                : "Client action must return the expected structured/text result; exit code alone is not considered success.";
            var observation = result.TimedOut ? "Process timed out and was killed."
                : "exit=" + result.ExitCode + "; " + FirstLine(result.Output);
            scenarios.Add(New("client-slash-" + command.Name, "/" + displayedCommand + (actionArgs.Length == 0 ? "" : " " + string.Join(' ', actionArgs)), "ClientAction",
                "User slash input only; not callable as a model tool", "Invoke /" + command.Name,
                "Fresh isolated config has providers: {} and models: {}; fixture workspace only.", expected,
                observation, result.Invocation + "\n" + Clip(result.Output, 1800), status));
            if (command.Name == "cancel" && locale == "en"
                && result.Output.Contains("no hay una sesión activa", StringComparison.OrdinalIgnoreCase))
            {
                scenarios.Add(New("client-locale-cancel-en", "/cancel --locale en", "ClientAction localization defect",
                    "Human slash action; not callable by model", "Check that the no-active-session result follows the explicit English process locale.",
                    "Same isolated no-session guard as the preceding cancel test; English is explicitly selected.",
                    "Expected a localized English response; non-English text is a localization defect, separate from operation dispatch.",
                    "Observed Spanish guard text despite `--locale en`: " + FirstLine(result.Output),
                    result.Invocation + "\n" + Clip(result.Output, 1200), "Failed"));
            }
            transcript.Add("CLI /" + command.Name + " [" + locale + "] => " + status + " (" + result.ExitCode + ")");
        }

        // Explicitly test locale argument resolution and user-facing usage, all in the fixture.
        foreach (var locale in UiLocaleCodes.Supported)
        {
            var result = await InvokeCli(cliDll, workspace, data, config,
                ["--locale", locale, "--help"], CancellationToken.None);
            var localizedUsage = locale switch
            {
                "es" => result.Output.Contains("Uso:", StringComparison.Ordinal),
                "en" => result.Output.Contains("Usage:", StringComparison.Ordinal),
                "fr" => result.Output.Contains("Utilisation", StringComparison.Ordinal),
                "it" => result.Output.Contains("Uso:", StringComparison.Ordinal),
                _ => false,
            };
            var status = !result.TimedOut && result.ExitCode == 0 && localizedUsage ? "Passed" : "Failed";
            scenarios.Add(New("locale-help-" + locale, "--locale " + locale + " --help", "CLI locale/parser",
                "Human CLI user", "Show localized CLI help", "Only isolated fixture paths are available.",
                "Exit 0 and localized usage text.", result.TimedOut ? "timed out" : "exit=" + result.ExitCode + "; " + FirstLine(result.Output),
                result.Invocation + "\n" + Clip(result.Output, 1200), status));
        }

        var directCases = new (string Id, string[] Args, string Expected, string StatusRule)[]
        {
            ("sim-complete", ["sim", "--json"], "Deterministic built-in simulation completes in local in-memory host and emits JSON outcome.", "sim-complete"),
            ("sim-interrupted", ["sim", "--crash", "--json"], "Deterministic simulation reports Interrupted with non-zero exit; no model provider is used.", "sim-interrupted"),
            ("sim-missing-scenario", ["sim", "fixture-scenario-that-does-not-exist.yaml"], "Invalid fixture path is rejected with exit 2; no file is created.", "missing-scenario"),
            ("model-list", ["model", "list"], "Offline model-policy list returns an empty/fixture-local catalog.", "model-list"),
            ("model-policy-list-usage-only", ["model", "policy", "list"], "Documents that policy list requires a model ID; usage output is not counted as command success.", "usage-only"),
            ("model-select-missing", ["model", "select", "acceptance-missing-model"], "Unknown fixture model rejected without network or mutation.", "model-unknown"),
            ("worktree-help", ["worktree", "help"], "Usage only; no checkout changes.", "usage"),
            ("worktree-inspect-fixture", ["worktree", "inspect"], "Safely reports that the isolated workspace is not a repository.", "not-repository"),
            ("trust-fixture", ["trust"], "Writes workspace trust only under the temporary fixture data directory.", "trust"),
            ("permissions-list", ["permissions"], "Reads grants for the fixture workspace; empty is valid.", "permissions-list"),
            ("permissions-revoke-absent", ["permissions", "revoke", "00000000-0000-4000-8000-000000000001"], "Unknown grant is reported; fixture-only no-op.", "grant-absent"),
            ("resolve-pending-readonly", ["resolve"], "Renders the pre-existing fixture interaction and available choices without submitting a resolution.", "interaction-listed"),
            ("verify-journal-fixture", ["verify-journal", "--json"], "Verifies only the fixture journal, or explicitly reports that it is absent.", "journal"),
            ("session-purge-invalid-id", ["session", "purge", "not-a-session-id"], "Rejects invalid ID before opening or changing a journal.", "invalid-id"),
            ("gc-dry-run", ["gc", "--dry-run", "--scope", "workspace"], "Dry-run inspects fixture workspace data and performs no deletion.", "dry-run"),
            ("audit-purge-dry-run", ["audit", "purge", "--dry-run", "--data-dir", data], "Dry-run inspects fixture audit data and performs no deletion.", "dry-run"),
            ("extensions-list", ["extensions"], "Reads only extension manifests under the fixture workspace; empty is valid.", "extensions-list"),
            ("doctor-fixture", ["doctor"], "Read-only diagnostics against isolated data/config/workspace paths; no account or provider probes.", "doctor"),
            ("unknown-command-fallback", ["acceptance-unknown-command"], "Unknown command follows documented ask fallback and fails closed before model inference.", "blocked-no-model"),
            ("tui-sim-headless", ["tui", "--sim"], "Headless simulated TUI rendering; does not initialize desktop/terminal UI or call a model.", "tui-sim"),
            ("ask-no-provider", ["ask", "acceptance fixture prompt"], "Must fail with an explicit no-model/configured-provider result before any network access.", "blocked-no-model"),
            ("act-no-provider", ["act", "acceptance fixture prompt"], "Must fail closed before any network access because fixture has no model.", "blocked-no-model"),
            ("explain-fixture", ["explain", "README.md"], "Reads and renders local runtime context only; it does not invoke model inference.", "explain-report"),
            ("explore-fixture", ["explore", "README.md"], "Legacy alias follows the same local context report path; no model inference is invoked.", "explain-report"),
        };

        foreach (var item in directCases)
        {
            var result = await InvokeCli(cliDll, workspace, data, config, item.Args, CancellationToken.None);
            var status = Classify(result, item.StatusRule);
            var preconditions = item.Id == "resolve-pending-readonly"
                ? "The preceding fixture-only sim --crash created this pending interaction; no choice is submitted. Providers/models remain empty."
                : "providers.yaml and models.yaml are empty; no account, network, or model endpoint is configured.";
            scenarios.Add(New(item.Id, string.Join(" ", item.Args), "Direct CLI",
                "Human CLI user; not a model tool", "Exercise the documented command with isolated fixture arguments.",
                preconditions,
                item.Expected, result.TimedOut ? "timeout; process tree killed" : "exit=" + result.ExitCode + "; " + FirstLine(result.Output),
                result.Invocation + "\n" + Clip(result.Output, 2000), status));
            transcript.Add("CLI " + string.Join(" ", item.Args) + " => " + status + " (" + result.ExitCode + ")");
        }

        var catalogCases = new (string Id, string[] Args, string Expected)[]
        {
            ("model-catalog-list", ["model", "list"], "List a synthetic offline model without contacting its example.invalid endpoint."),
            ("model-catalog-select", ["model", "select", "acceptance-offline-model"], "Select only the synthetic fixture model; noninteractive onboarding must remain ObserveOnly."),
            ("model-policy-set", ["model", "policy", "set", "acceptance-offline-model", "--category", "ObserveOnly", "--note", "acceptance-fixture"], "Persist explicit ObserveOnly policy in fixture-local database."),
            ("model-policy-show", ["model", "policy", "show", "acceptance-offline-model"], "Read the policy just written in the fixture."),
            ("model-policy-history", ["model", "policy", "history", "acceptance-offline-model"], "Read fixture-local policy history."),
            ("model-policy-delete", ["model", "policy", "delete", "acceptance-offline-model"], "Delete only the fixture-local policy; catalog model remains unchanged."),
        };
        foreach (var item in catalogCases)
        {
            var result = await InvokeCli(cliDll, catalogWorkspace, catalogData, catalogConfig, item.Args, CancellationToken.None);
            var status = ClassifyCatalogCase(result, item.Id);
            scenarios.Add(New(item.Id, string.Join(" ", item.Args), "Direct CLI · offline synthetic model",
                "Human CLI only; not a model tool", item.Expected,
                "Synthetic provider uses auth:none and host *.invalid; no DNS/network/probe/inference command is invoked.",
                item.Expected + " The output must contain operation-specific evidence; exit code alone is insufficient.",
                result.TimedOut ? "timeout; process tree killed" : "exit=" + result.ExitCode + "; " + FirstLine(result.Output),
                result.Invocation + "\n" + Clip(result.Output, 2000), status));
        }

        var policyAfterDelete = await InvokeCli(cliDll, catalogWorkspace, catalogData, catalogConfig,
            ["model", "policy", "show", "acceptance-offline-model"], CancellationToken.None);
        var policyIsAbsent = policyAfterDelete.ExitCode == 1
            && policyAfterDelete.Output.Contains("acceptance-offline-model", StringComparison.Ordinal)
            && ContainsAny(policyAfterDelete.Output, "no tiene política guardada", "has no saved policy", "aucune politique enregistrée", "non ha una policy salvata");
        scenarios.Add(New("model-policy-delete-readback", "model policy show acceptance-offline-model", "Direct CLI · offline synthetic model",
            "Human CLI only; not a model tool", "Verify the preceding fixture-only policy deletion by reading the same exact key.",
            "Only the harness-created ObserveOnly policy was targeted; synthetic model catalog remains unchanged.",
            "The policy lookup reports no saved policy after delete.",
            "exit=" + policyAfterDelete.ExitCode + "; absent=" + policyIsAbsent,
            policyAfterDelete.Invocation + "\n" + Clip(policyAfterDelete.Output, 1600),
            policyAfterDelete.TimedOut ? "Failed" : policyIsAbsent ? "Passed" : "Failed"));

        var memoryAdd = await InvokeCli(cliDll, workspace, data, config,
            ["/memory", "session", "add", "acceptance-only-memory-record"], CancellationToken.None);
        var records = ExtractObjects(memoryAdd.Output, "records");
        var addedRecord = records.FirstOrDefault(item =>
            GetString(item, "content") == "acceptance-only-memory-record"
            || GetString(item, "text") == "acceptance-only-memory-record"
            || GetString(item, "value") == "acceptance-only-memory-record");
        var memoryId = addedRecord.ValueKind == JsonValueKind.Object ? GetString(addedRecord, "id") : null;
        var memoryStatus = memoryAdd.TimedOut ? "Failed" : memoryAdd.ExitCode != 0 ? "Failed"
            : memoryId is null ? "Blocked" : "Passed";
        scenarios.Add(New("memory-session-add-fixture", "/memory session add acceptance-only-memory-record", "ClientAction · fixture mutation",
            "Human trusted action only; not a model tool", "Add a harmless test fact after the deterministic sim has created a session.",
            "All session journal/memory paths are under the temporary workspace/data fixture; no personal data or credentials.",
            "Host acknowledges the action and returns the persisted record with an exact record ID.",
            memoryAdd.TimedOut ? "timeout" : "exit=" + memoryAdd.ExitCode + "; parsedRecords=" + records.Count + "; id=" + (memoryId ?? "none"),
            memoryAdd.Invocation + "\n" + Clip(memoryAdd.Output, 2400), memoryStatus));
        if (memoryId is not null)
        {
            var memoryForget = await InvokeCli(cliDll, workspace, data, config,
                ["/memory", "session", "forget", memoryId], CancellationToken.None);
            var afterForget = await InvokeCli(cliDll, workspace, data, config, ["/memory", "session"], CancellationToken.None);
            var remaining = ExtractObjects(afterForget.Output, "records");
            var readback = ExtractRootJson(afterForget.Output);
            var forgotten = readback.HasValue && readback.Value.TryGetProperty("records", out var remainingArray)
                && remainingArray.ValueKind == JsonValueKind.Array
                && remaining.All(record => GetString(record, "id") != memoryId);
            scenarios.Add(New("memory-session-forget-fixture", "/memory session forget " + memoryId, "ClientAction · fixture mutation",
                "Human trusted action only; not a model tool", "Forget exactly the acceptance record added by this harness.",
                "Record ID was captured from the fixture's preceding memory-session-add result.",
                "Ack succeeds and a subsequent memory query no longer contains that exact ID.",
                "forgetExit=" + memoryForget.ExitCode + "; remainingRecords=" + remaining.Count + "; absent=" + forgotten,
                memoryForget.Invocation + "\n" + Clip(memoryForget.Output, 1600) + "\n" + Clip(afterForget.Output, 1600),
                !memoryForget.TimedOut && memoryForget.ExitCode == 0 && !LooksLikeRejected(memoryForget.Output) && forgotten ? "Passed" : "Failed"));
        }
        else
        {
            scenarios.Add(New("memory-session-forget-fixture", "/memory session forget <captured-id>", "ClientAction · fixture mutation",
                "Human trusted action only; not a model tool", "Delete only the temporary record added by this harness.",
                "No fixture record ID was returned by the add path.",
                "Not attempted because forgetting an uncaptured ID would test the wrong target.",
                "Blocked by preceding add result.", "No forget command issued.", "NotExercised"));
        }

        // Entry points deliberately not exercised: they can change real account state, open OAuth,
        // discover/refresh a connected account, perform inference, or mutate non-fixture data.
        AddNotExercised(scenarios, "account-login-claude", "login claude [provider] [--manual]", "OAuth browser/manual exchange is prohibited for acceptance fixtures.");
        AddNotExercised(scenarios, "account-login-chatgpt", "login chatgpt [--device]", "Device authorization is prohibited; no account authority was granted.");
        AddNotExercised(scenarios, "account-logout-claude", "logout claude [provider]", "Could disconnect a configured Claude account; no account fixture or authority was provided.");
        AddNotExercised(scenarios, "account-logout-chatgpt", "logout chatgpt", "Could revoke a configured ChatGPT account; no account fixture or authority was provided.");
        AddNotExercised(scenarios, "model-refresh", "model refresh", "Could refresh a connected account catalog; never invoked.");
        AddNotExercised(scenarios, "model-qualify", "model qualify <model>", "Can perform paid/remote probes; never invoked.");
        AddNotExercised(scenarios, "tui-interactive", "tui", "Interactive UI requires a TTY; parser/renderer are exercised only through --sim and the separate TUI suite.");
        AddNotExercised(scenarios, "session-purge-valid", "session purge <fixture-session-id>", "Destructive path is designed for disposable fixture journal but not executed in this acceptance pass.");
        AddNotExercised(scenarios, "gc-apply", "gc", "Only --dry-run is exercised; apply mode can delete data and is intentionally not run.");
        AddNotExercised(scenarios, "audit-purge-apply", "audit purge", "Only --dry-run is exercised; apply mode is intentionally not run.");
        AddNotExercised(scenarios, "worktree-cleanup", "worktree cleanup <proposal> [--yes]", "Mutates a checkout; no disposable git repository fixture was created for this operation.");
        AddNotExercised(scenarios, "worktree-discard", "worktree discard <proposal> [--yes]", "Discards proposed worktree changes; no disposable git repository fixture was created for this operation.");
        AddNotExercised(scenarios, "worktree-undo", "worktree undo [effect-id]", "Undo can revert repository changes; no disposable git repository fixture was created for this operation.");
        AddNotExercised(scenarios, "model-tool-inference", "model-invocable tools from /tools", "No real model or provider is configured; model-to-tool invocation belongs to the coordinated live acceptance pass.");

        // Capture the real Host command and tool catalogs via their read-only ClientActions.
        var hostCatalogResult = await InvokeCli(cliDll, catalogWorkspace, catalogData, catalogConfig, ["/commands"], CancellationToken.None);
        var hostCommands = ExtractObjects(hostCatalogResult.Output, "commands");
        var hostCatalogTruncated = hostCatalogResult.Output.Contains("…", StringComparison.Ordinal);
        scenarios.Add(New("host-command-catalog", "/commands", "Host catalog inventory",
            "Human client action only; slash action itself is not model-callable", "Capture actual Host command descriptors.",
            "Empty user/project extension configuration; local fixture only.",
            "Valid JSON catalog, distinct canonical IDs and names recorded below.",
            hostCatalogResult.TimedOut ? "timeout" : "exit=" + hostCatalogResult.ExitCode + "; entries=" + hostCommands.Count,
            hostCatalogResult.Invocation + "\n" + Clip(hostCatalogResult.Output, 4000),
            hostCatalogTruncated ? "Failed"
                : !hostCatalogResult.TimedOut && hostCatalogResult.ExitCode == 0 && hostCommands.Count > 0 ? "Passed" : "Failed"));
        if (hostCatalogTruncated)
            transcript.Add("BUG EVIDENCE: /commands ClientAction output contains truncation marker (18xx-character client result cap); full JSON cannot be accepted as inventory.");
        foreach (var command in hostCommands)
        {
            var id = GetString(command, "id") ?? "unknown";
            var name = GetString(command, "name") ?? id;
            scenarios.Add(New("host-descriptor-" + SafeId(id), "/" + name, "Host command descriptor",
                "Human Host command surface; model-callable status depends on descriptor/capability and is not inferred",
                "No execution suggested; inventory only.", "Read-only catalog query completed against fixture.",
                "Descriptor captured without treating it as an executable model tool.", "catalog id=" + id + "; name=" + name
                    + "; kind=" + (GetString(command, "kind") ?? "unknown") + "; enabled=" + (GetBool(command, "enabled")?.ToString() ?? "unknown"),
                "Source: /commands Host catalog.", "NotExercised"));
        }

        var hostHelpResult = await InvokeCli(cliDll, catalogWorkspace, catalogData, catalogConfig, ["/help"], CancellationToken.None);
        var helpCommands = ExtractHelpCommands(hostHelpResult.Output);
        var hostHelpOnly = helpCommands.Where(name => !clientCommands.Any(command =>
                string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        scenarios.Add(New("host-help-command-inventory", "/help", "Host command inventory fallback",
            "Human-only help listing; not model invocation", "Use non-JSON help to enumerate Host slash names without treating it as an executable tool list.",
            "Read-only ClientAction on synthetic offline fixture.", "Displays user-facing command names; this does not replace the truncated descriptor catalog.",
            "exit=" + hostHelpResult.ExitCode + "; help names=" + helpCommands.Count + "; host-only names=" + hostHelpOnly.Length,
            hostHelpResult.Invocation + "\nHost names (best-effort display extraction): " + string.Join(", ", hostHelpOnly)
                + "\n" + Clip(hostHelpResult.Output, 5000),
            !hostHelpResult.TimedOut && hostHelpResult.ExitCode == 0 && hostHelpOnly.Length > 0 ? "Passed" : "Blocked"));
        foreach (var name in hostHelpOnly)
            scenarios.Add(New("host-help-only-" + SafeId(name), "/" + name, "Host slash name from help",
                "Human-only command name; model callability not inferred", "No execution; the command's active-session/authority requirements are unknown without its descriptor.",
                "Help listing came from the actual CLI, but JSON descriptor was truncated or not available.",
                "Inventory only; not exercised until exact descriptor/capability and preconditions are known.",
                "Help display name=" + name, "Source: /help output.", "NotExercised"));

        var toolCatalogResult = await InvokeCli(cliDll, catalogWorkspace, catalogData, catalogConfig, ["/tools"], CancellationToken.None);
        var tools = ExtractObjects(toolCatalogResult.Output, "tools");
        var toolIds = ExtractToolIds(toolCatalogResult.Output);
        scenarios.Add(New("tool-catalog-fixture", "/tools", "Tool inventory",
            "Human read-only ClientAction; model tool execution not attempted", "Inventory actual tool IDs/capabilities from the fixture Host.",
            "No connected providers or model; tool diagnostics remain local.", "A human-readable effective-tool inventory is returned and IDs are recorded; no tool is invoked.",
            toolCatalogResult.TimedOut ? "timeout" : "exit=" + toolCatalogResult.ExitCode + "; tool IDs=" + toolIds.Length,
            toolCatalogResult.Invocation + "\nIDs: " + string.Join(", ", toolIds) + "\n" + Clip(toolCatalogResult.Output, 4000),
            !toolCatalogResult.TimedOut && toolCatalogResult.ExitCode == 0 && toolIds.Length > 0 ? "Passed" : "Blocked"));
        foreach (var toolId in toolIds)
            scenarios.Add(New("tool-not-live-" + SafeId(toolId!), toolId!, "Host tool inventory",
                "Model may invoke only if its actual tool schema/capability says so; this harness does not call it",
                "No model prompt issued.", "No model/provider configured; no tool effects possible.",
                "Inventory only; live model invocation is delegated to the separate authorized acceptance harness.",
                "Tool ID from /tools: " + toolId, "Source: captured fixture tool catalog.", "NotExercised"));

        // Compare alias catalog, ClientActions and CLI route coverage against explicit implementation inventories.
        var knownDirectRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "sim", "explain", "explore", "ask", "act", "model", "worktree", "trust", "extensions",
            "login", "logout", "permissions", "resolve", "verify-journal", "session", "gc", "audit",
            "doctor", "tui", "help", "--help", "-h",
        };
        var clientNames = clientCommands.Select(command => command.Name).ToHashSet(StringComparer.Ordinal);
        var aliasNames = HumanCommandAliases.Entries.Select(entry => entry.Canonical).ToHashSet(StringComparer.Ordinal);
        var clientAliasGaps = clientNames.Except(aliasNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var directWithoutAlias = knownDirectRoutes.Except(aliasNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var coverageStatus = clientAliasGaps.Length == 0 ? "Passed" : "Failed";
        scenarios.Add(New("inventory-cross-check", "HumanCommandAliases + ClientCommandRegistry + CliApp dispatch", "Coverage audit",
            "N/A", "Cross-check canonical command alias registry against ClientAction names and direct CLI dispatcher inventory.",
            "Implementation inventories are source-derived; Host command catalog is captured above.",
            "Every client slash action has a canonical human command alias; direct CLI routes without aliases are explicitly listed.",
            "aliases=" + HumanCommandAliases.Entries.Count + "; clientActions=" + clientCommands.Count
                + "; clientAliasGaps=" + (clientAliasGaps.Length == 0 ? "none" : string.Join(",", clientAliasGaps))
                + "; directRoutesWithoutAlias=" + string.Join(",", directWithoutAlias)
                + "; hostCatalog=" + hostCommands.Count + "; fixtureTools=" + toolIds.Length,
            "Alias parser matrix + actual CLI scenarios; route list is encoded in harness and must be reviewed when CliApp dispatch changes.", coverageStatus));

        await using (var report = new StreamWriter(reportPath, append: false, new System.Text.UTF8Encoding(false)))
        {
            foreach (var scenario in scenarios)
                await report.WriteLineAsync(JsonSerializer.Serialize(scenario, JsonOptions));
        }
        await File.WriteAllLinesAsync(logPath, transcript, new System.Text.UTF8Encoding(false));

        var counts = scenarios.GroupBy(scenario => scenario.Status, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + "=" + group.Count());
        Console.WriteLine("Scenarios=" + scenarios.Count + " " + string.Join(" ", counts));
        var parserScopes = new[] { "Alias/parser", "Alias/parser grammar", "Slash parser" };
        var parserOnly = scenarios.Count(scenario => parserScopes.Contains(scenario.Scope, StringComparer.Ordinal));
        var runtimeRows = scenarios.Count(scenario => !parserScopes.Contains(scenario.Scope, StringComparer.Ordinal)
            && scenario.Scope is not ("ClientAction boundary" or "Direct CLI planned/not exercised"));
        Console.WriteLine("Parser-only=" + parserOnly + " (not end-to-end); CLI/client/Host rows=" + runtimeRows
            + "; excluded/planned rows=" + scenarios.Count(scenario => scenario.Status is "NotExercised" or "NotModelCallable"));
        Console.WriteLine("Aliases=" + HumanCommandAliases.Entries.Count + "; ClientActions=" + clientCommands.Count
            + "; HostDescriptors=" + hostCommands.Count + "; fixture tools=" + toolIds.Length);
        Console.WriteLine("JSONL=" + reportPath);
        Console.WriteLine("Log=" + logPath);
        Console.WriteLine("Fixture preserved=" + fixtureRoot);
        return scenarios.Any(scenario => scenario.Status == "Failed") ? 1 : 0;
    }

    private static void AddParserCoverage(List<Scenario> scenarios)
    {
        foreach (var entry in HumanCommandAliases.Entries)
        {
            foreach (var (locale, name) in new[] { ("en", entry.Canonical), ("es", entry.Spanish),
                         ("fr", entry.French), ("it", entry.Italian) })
            {
                var typed = CommandLineParser.TryParseArguments(["/" + name], out var invocation);
                var normalized = typed ? invocation!.Name : "<parse-failed>";
                var status = typed && normalized == entry.Canonical ? "Passed" : "Failed";
                scenarios.Add(New("alias-" + entry.Canonical + "-" + locale, "/" + name, "Alias/parser",
                    "Human CLI/TUI text only; not a model tool", "Parse a localized slash head without changing its canonical identity.",
                    "Pure parser test; no CLI process, network, account, or filesystem access.",
                    "Canonical command equals " + entry.Canonical + ".", "parsed=" + typed + "; canonical=" + normalized,
                    "HumanCommandAliases.Normalize + CommandLineParser.TryParseArguments", status));
            }
        }

        var subcommands = new (string Command, string Prefix, string[] Variants, string Expected)[]
        {
            ("skills", "", ["load", "cargar", "charger", "carica"], "load"),
            ("skills", "", ["release", "liberar", "liberer", "rilascia"], "release"),
            ("memory", "", ["session", "sesion", "sessione"], "session"),
            ("memory", "session", ["add", "agregar", "ajouter", "aggiungi"], "session add"),
            ("memory", "session", ["forget", "olvidar", "oublier", "dimentica"], "session forget"),
            ("permissions", "", ["list", "listar", "lister", "elenca"], "list"),
            ("permissions", "", ["revoke", "revocar", "revoquer", "revoca"], "revoke"),
            ("extensions", "", ["trust", "confiar", "approuver", "autorizza"], "trust"),
            ("extensions", "", ["revoke", "revocar", "revoquer", "revoca"], "revoke"),
            ("model", "", ["list", "listar", "lister", "elenca"], "list"),
            ("model", "", ["select", "seleccionar", "selectionner", "seleziona"], "select"),
            ("model", "", ["policy", "politica", "politique"], "policy"),
            ("model", "policy", ["delete", "eliminar", "supprimer", "elimina"], "policy delete"),
            ("model", "", ["qualify", "cualificar", "qualifier", "qualifica"], "qualify"),
            ("model", "", ["refresh", "actualizar", "actualiser", "aggiorna"], "refresh"),
            ("worktree", "", ["inspect", "inspeccionar", "inspecter", "ispeziona"], "inspect"),
            ("worktree", "", ["preview", "previsualizar", "apercu", "anteprima"], "preview"),
            ("worktree", "", ["cleanup", "limpiar", "nettoyer", "pulisci"], "cleanup"),
            ("worktree", "", ["discard", "descartar", "abandonner", "scarta"], "discard"),
            ("worktree", "", ["undo", "deshacer", "retablir", "ripristina"], "undo"),
            ("mode", "", ["orq", "orquestar", "orchestrer", "orchestra"], "orq"),
            ("reasoning", "", ["default", "predeterminado", "defaut", "predefinito"], "default"),
            ("reasoning", "", ["boost", "refuerzo", "renfort", "potenziamento"], "boost"),
            ("reasoning", "", ["on", "activar", "activer", "attiva"], "on"),
            ("animation", "", ["off", "desactivar", "desactiver", "disattiva"], "off"),
            ("sidebar", "", ["workspace", "espacio", "espace", "spazio"], "workspace"),
            ("session", "", ["purge", "purgar", "purger", "svuota"], "purge"),
            ("audit", "", ["purge", "purgar", "purger", "svuota"], "purge"),
        };
        foreach (var item in subcommands)
        {
            foreach (var variant in item.Variants)
            {
                var input = new[] { item.Command }
                    .Concat(string.IsNullOrEmpty(item.Prefix) ? [] : item.Prefix.Split(' '))
                    .Append(variant).ToArray();
                var argv = HumanCommandAliases.NormalizeCli(input);
                var actual = string.Join(' ', argv.Skip(1));
                var status = actual == item.Expected ? "Passed" : "Failed";
                scenarios.Add(New("subcommand-" + item.Command + "-" + SafeId(variant), item.Command + " " + variant,
                    "Alias/parser grammar", "Human direct CLI grammar only", "Normalize only a finite grammar word, preserve all remaining argv as data.",
                    "Pure parser test; no command side effects.", "Expected normalized word " + item.Expected + ".",
                    "normalized argv=" + string.Join(" | ", argv), "HumanCommandAliases.NormalizeCli", status));
            }
        }

        var parserCases = new[]
        {
            (Text: "/memory session add \"literal prompt text\"", Expected: "memory", ArgCount: 3),
            (Text: "/model policy history fixture-model", Expected: "model", ArgCount: 3),
            (Text: "/mode orquestar", Expected: "mode", ArgCount: 1),
        };
        foreach (var item in parserCases)
        {
            var parsed = CommandLineParser.TryParse(item.Text, out var invocation);
            var status = parsed && invocation!.Name == item.Expected && invocation.Arguments.Count == item.ArgCount
                ? "Passed" : "Failed";
            scenarios.Add(New("parser-argv-preservation-" + SafeId(item.Text), item.Text, "Slash parser",
                "Human client input only", "Ensure quoted free text remains data after localized slash/subcommand parsing.",
                "Pure parser test.", "Parsed command and argv count match the input grammar.",
                "parsed=" + parsed + "; name=" + (invocation?.Name ?? "-") + "; argv=" + (invocation is null ? "-" : string.Join(" | ", invocation.Arguments)),
                "CommandLineParser.TryParse", status));
        }
    }

    private static void AddSlashCallabilityRows(List<Scenario> scenarios)
    {
        foreach (var command in ClientCommandRegistry.Default.Commands)
            scenarios.Add(New("slash-not-model-tool-" + command.Name, "/" + command.Name, "ClientAction boundary",
                "Invocable by human CLI/TUI text, not a model tool", "No model prompt; this row documents callability boundary.",
                "ClientAction registry is not the model tool catalog.", "Slash syntax resolves to ClientAction only; it is never emitted as a model tool call.",
                "ClientCommandRegistry.Default action=" + command.ActionId, "Source: actual ClientCommandRegistry.Default.", "NotModelCallable"));
    }

    private static string[] ClientActionArgs(string command) => command switch
    {
        "memory" => ["session"],
        _ => [],
    };

    private static bool LooksLikeRunControlBlocked(string output) =>
        output.Contains("no active", StringComparison.OrdinalIgnoreCase)
        || output.Contains("no hay una sesión activa", StringComparison.OrdinalIgnoreCase)
        || output.Contains("no active session", StringComparison.OrdinalIgnoreCase)
        || output.Contains("aucune session active", StringComparison.OrdinalIgnoreCase)
        || output.Contains("nessuna sessione attiva", StringComparison.OrdinalIgnoreCase)
        || output.Contains("no run", StringComparison.OrdinalIgnoreCase)
        || output.Contains("ningún run", StringComparison.OrdinalIgnoreCase)
        || output.Contains("no hay un run", StringComparison.OrdinalIgnoreCase)
        || output.Contains("rejected", StringComparison.OrdinalIgnoreCase)
        || output.Contains("rechaz", StringComparison.OrdinalIgnoreCase)
        || output.Contains("aucun run", StringComparison.OrdinalIgnoreCase)
        || output.Contains("nessun run", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRejected(string output) =>
        ContainsAny(output, "rejected", "rechaz", "refus", "rifiut", "erreur", "errore", "not authorized",
            "no autorizado", "denied", "denegado", "impossible", "no se pudo");

    private static List<string> ExtractHelpCommands(string output)
    {
        var names = new List<string>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("/", StringComparison.Ordinal)) continue;
            var localizedAt = trimmed.IndexOf(" (/", StringComparison.Ordinal);
            if (localizedAt >= 0)
            {
                var end = trimmed.IndexOf(')', localizedAt + 3);
                if (end > localizedAt) names.Add(trimmed[(localizedAt + 3)..end]);
                continue;
            }
            var endToken = trimmed.IndexOfAny([' ', '\t', '—']);
            var token = endToken < 0 ? trimmed : trimmed[..endToken];
            var name = token[1..].TrimEnd(':');
            if (name.Length > 0 && name.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':'))
                names.Add(name);
        }
        return names;
    }

    private static string ValidateClientAction(string command, string output, int exitCode)
    {
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return "Failed";
        var json = ExtractRootJson(output);
        return command switch
        {
            "commands" => output.Contains("…", StringComparison.Ordinal) ? "Failed"
                : json.HasValue && json.Value.TryGetProperty("commands", out var commands)
                    && commands.ValueKind == JsonValueKind.Array ? "Passed" : "Failed",
            "tools" => json.HasValue && json.Value.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0
                    || ExtractToolIds(output).Length > 0 ? "Passed" : "Blocked",
            "context" => json.HasValue && json.Value.TryGetProperty("snapshot", out _) ? "Passed" : "Blocked",
            "extensions" => json.HasValue && json.Value.TryGetProperty("extensions", out _) ? "Passed"
                : ContainsAny(output, "extensions detected", "estensioni rilevate", "extensiones detectadas", "extensions détectées") ? "Passed" : "Blocked",
            "skills" => json.HasValue && json.Value.ValueKind == JsonValueKind.Object ? "Passed" : "Blocked",
            "memory" => json.HasValue && json.Value.ValueKind == JsonValueKind.Object
                && json.Value.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array ? "Passed" : "Blocked",
            "plan" => json.HasValue && json.Value.ValueKind == JsonValueKind.Object ? "Passed" : "Blocked",
            "tasks" => json.HasValue && json.Value.ValueKind == JsonValueKind.Object
                && json.Value.TryGetProperty("tasks", out _) ? "Passed" : "Blocked",
            "events" => json.HasValue && json.Value.ValueKind == JsonValueKind.Object
                && json.Value.TryGetProperty("events", out _) ? "Passed" : "Blocked",
            "permissions" => ContainsAny(output, "permission", "permiso", "autoriz", "permess", "grant")
                ? "Passed" : "Blocked",
            "help" => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => line.TrimStart().StartsWith('/'))
                ? "Passed" : "Blocked",
            // A successful ClientActionResult only proves that the action was dispatched. ExitRequested is
            // not observable in this child-process invocation, so do not report the exit action as E2E passed.
            "exit" => "Blocked",
            _ => "Blocked",
        };
    }

    private static string ClassifyCatalogCase(ProcessResult result, string id)
    {
        if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return "Failed";
        var output = result.Output;
        return id switch
        {
            "model-catalog-list" => output.Contains("acceptance-offline-model", StringComparison.Ordinal) ? "Passed" : "Failed",
            "model-catalog-select" => output.Contains("acceptance-offline-model", StringComparison.Ordinal)
                && ContainsAny(output, "selected", "seleccion", "sélection", "selezion") ? "Passed" : "Failed",
            "model-policy-set" => ContainsAny(output, "saved", "guardad", "enregistr", "salvat")
                && output.Contains("acceptance-offline-model", StringComparison.Ordinal) ? "Passed" : "Failed",
            "model-policy-show" => output.Contains("acceptance-offline-model", StringComparison.Ordinal)
                && ContainsAny(output, "ObserveOnly", "observe only", "observación", "observation", "osservazione") ? "Passed" : "Failed",
            "model-policy-history" => ContainsAny(output, "created", "cread", "créé", "creat") ? "Passed" : "Failed",
            "model-policy-delete" => ContainsAny(output, "deleted", "eliminad", "supprim", "eliminat")
                && output.Contains("acceptance-offline-model", StringComparison.Ordinal) ? "Passed" : "Failed",
            _ => "Failed",
        };
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static JsonElement? ExtractRootJson(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static void AddNotExercised(List<Scenario> scenarios, string id, string command, string reason,
        string status = "NotExercised") => scenarios.Add(New(id, command, "Direct CLI planned/not exercised",
        "Human CLI only; never sent as a model tool", "No command issued.", reason,
        "A safe isolated scenario is documented; actual execution requires the excluded authority/fixture or risk boundary.",
        reason, "No process launched for this row.", status));

    private static Scenario New(string id, string command, string scope, string callableBy,
        string proposedPrompt, string preconditions, string expected, string observed, string evidence, string status) =>
        new(id, command, scope, callableBy, proposedPrompt, preconditions, expected, observed, evidence, status);

    private static async Task<ProcessResult> InvokeCli(string cliDll, string workspace, string data, string config,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(cliDll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["OMNICORE_DATA_DIR"] = data;
        start.Environment["OMNICORE_CONFIG_DIR"] = config;
        start.Environment["OMNI_LOCALE"] = "es";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var invocation = "dotnet " + Quote(cliDll) + " " + string.Join(' ', arguments.Select(Quote));
        try
        {
            if (!process.Start()) return new(-1, "Could not start CLI process.", false, invocation);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                return new(-1, "Process exceeded " + TimeoutSeconds + " seconds and was killed.\n"
                    + await stdout + "\n" + await stderr, true, invocation);
            }
            return new(process.ExitCode, (await stdout) + (await stderr), false, invocation);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(-1, "Process launch/read failed: " + exception.GetType().Name + ": " + exception.Message, false, invocation);
        }
    }

    private static string Classify(ProcessResult result, string rule)
    {
        if (result.TimedOut) return "Failed";
        var output = result.Output;
        return rule switch
        {
            "sim-complete" => result.ExitCode == 0 && ExtractRootJson(output) is { } sim
                && sim.TryGetProperty("outcome", out var outcome)
                && outcome.GetString() is "Completed" or "Succeeded" ? "Passed" : "Failed",
            "model-list" => result.ExitCode == 0 && ContainsAny(output, "model", "modelo", "modèle", "modello")
                ? "Passed" : "Failed",
            "usage" => result.ExitCode == 0 && ContainsAny(output, "usage", "uso", "utilisation", "utilizzo", "worktree")
                ? "Passed" : "Failed",
            "usage-only" => result.ExitCode != 0 && ContainsAny(output, "usage", "uso:", "utilisation", "utilizzo", "uso: omni model policy list")
                ? "Blocked" : "Failed",
            "trust" => result.ExitCode == 0 && ContainsAny(output, "trusted", "trust", "confi", "autoris", "fidat")
                ? "Passed" : "Failed",
            "permissions-list" => result.ExitCode == 0 && ContainsAny(output, "permission", "permiso", "autoriz", "permess", "grant")
                ? "Passed" : "Failed",
            "journal" => result.ExitCode == 0 && ContainsAny(output, "journal", "verified", "verificado", "vérifié", "verificat", "absent", "not found", "no encontrado")
                ? "Passed" : "Blocked",
            "dry-run" => result.ExitCode == 0 && ContainsAny(output, "dry-run", "dry run", "simulación", "simulation", "anteprima", "dry run")
                ? "Passed" : "Blocked",
            "extensions-list" => result.ExitCode == 0 && ContainsAny(output, "extension", "extensión", "extension", "estensione")
                ? "Passed" : "Blocked",
            "doctor" => result.ExitCode == 0 && ContainsAny(output, "doctor", "diagnóstico", "diagnostic", "diagnostica")
                ? "Passed"
                : result.ExitCode == 1 && ContainsAny(output, "sin modelo configurado", "no model configured", "no model is configured")
                    ? "Blocked" : "Failed",
            "explain-report" => result.ExitCode == 0 && output.Contains("README.md", StringComparison.Ordinal)
                && ContainsAny(output, "Pregunta", "Question", "Question", "Domanda") ? "Passed" : "Failed",
            "tui-sim" => result.ExitCode == 0 && ContainsAny(output, "sim", "frame", "buffer", "tui")
                ? "Passed" : "Blocked",
            "nonzero" => result.ExitCode != 0 && !string.IsNullOrWhiteSpace(output) ? "Blocked" : "Failed",
            "model-unknown" => result.ExitCode != 0 && output.Contains("acceptance-missing-model", StringComparison.Ordinal)
                ? "Blocked" : "Failed",
            "not-repository" => result.ExitCode != 0 && ContainsAny(output, "repository", "repositorio", "répertoire")
                ? "Blocked" : "Failed",
            "grant-absent" => result.ExitCode == 1 && (output.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || output.Contains("no encontrado", StringComparison.OrdinalIgnoreCase)
                || output.Contains("no existe ese grant", StringComparison.OrdinalIgnoreCase)
                || output.Contains("not_found", StringComparison.OrdinalIgnoreCase)) ? "Blocked" : "Failed",
            "interaction-listed" => result.ExitCode == 0
                && ContainsAny(output, "interacción pendiente", "pending interaction", "interaction en attente", "interazione in attesa")
                && output.Contains("[1]", StringComparison.Ordinal) && output.Contains("[2]", StringComparison.Ordinal) ? "Passed" : "Failed",
            "sim-interrupted" => result.ExitCode == 1 && output.Contains("Interrupted", StringComparison.OrdinalIgnoreCase)
                && output.Contains("\"outcome\"", StringComparison.Ordinal) ? "Passed" : "Failed",
            "missing-scenario" => result.ExitCode == 2 && output.Contains("fixture-scenario-that-does-not-exist", StringComparison.Ordinal) ? "Passed" : "Failed",
            "invalid-id" => result.ExitCode == 2 && output.Contains("not-a-session-id", StringComparison.Ordinal) ? "Passed" : "Failed",
            "blocked-no-model" => result.ExitCode != 0 && LooksLikeMissingModel(output) ? "Blocked" : "Failed",
            _ => "Failed",
        };
    }

    private static bool LooksLikeMissingModel(string output) =>
        output.Contains("No model", StringComparison.OrdinalIgnoreCase)
        || output.Contains("model is not configured", StringComparison.OrdinalIgnoreCase)
        || output.Contains("no model is configured", StringComparison.OrdinalIgnoreCase)
        || output.Contains("modelo", StringComparison.OrdinalIgnoreCase)
            && (output.Contains("configur", StringComparison.OrdinalIgnoreCase) || output.Contains("ningún", StringComparison.OrdinalIgnoreCase));

    private static List<JsonElement> ExtractObjects(string output, string property)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(property, out var array)
                    && array.ValueKind == JsonValueKind.Array)
                    return array.EnumerateArray().Select(element => element.Clone()).ToList();
            }
            catch (JsonException) { }
        }
        return [];
    }

    private static string[] ExtractToolIds(string output)
    {
        var ids = ExtractObjects(output, "tools")
            .Select(item => GetString(item, "toolId") ?? GetString(item, "id"))
            .OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('→');
            if (separator <= 0) continue;
            var candidate = line[..separator].Trim();
            if (candidate.Length > 0 && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
                ids.Add(candidate);
        }
        return ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(empty output)";
    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "… [truncated in report]";
    private static string SafeId(string value) => string.Concat(value.Select(character => char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-'));
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OmniCore.sln"))
                || (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || File.Exists(Path.Combine(directory.FullName, ".git")))
                    && Directory.Exists(Path.Combine(directory.FullName, "src", "OmniCore.Cli")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Pass --repo-root <OmniCore worktree>.");
    }
}
