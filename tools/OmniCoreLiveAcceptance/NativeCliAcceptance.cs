namespace OmniCoreLiveAcceptance;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;

internal sealed partial class AcceptanceRunner
{
    private const string NativeCliFixtureModel = "acceptance-fixture-model";
    private const int NativeCliMaxProcesses = 16;
    private static readonly TimeSpan NativeCliProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NativeCliGenerationTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Lets the selected configured model choose process.exec argv for a finite set of real CLI
    /// invocations. Every proposed process is matched to a closed exact-argv allowlist before the
    /// production ToolRuntime is entered. Both CLI data and configuration paths are fixture-only.
    /// </summary>
    public async Task<NativeCliRunSummary> RunNativeCliCommandsAsync(Options options,
        PreflightReport preflight, string evidenceDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceDirectory);
        if (!preflight.Ready)
            throw new InvalidOperationException("native_cli_preflight_not_ready");
        if (!options.AllowFixtureProcess)
            throw new InvalidOperationException("native_cli_requires_explicit_fixture_process_authorization");
        if (!OperatingSystem.IsWindows())
            return WriteNativeCliSummary(options.Model, preflight.ModelId, evidenceDirectory,
                "not_applicable", "native_cli_fixture_requires_windows_powershell", 0, []);

        // Resolve the real provider while the caller's original profile is still in force;
        // fixture environment values are set only in the child PowerShell script below.
        var provider = ConnectModelProvider(options.Model, preflight.ModelId);
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "OmniCoreLiveAcceptance",
            "native-cli-" + options.Model + "-" + DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-"
            + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(fixtureRoot, "workspace");
        var dataDirectory = Path.Combine(fixtureRoot, "user-data");
        var configDirectory = Path.Combine(fixtureRoot, "user-config");
        var artifactsDirectory = Path.Combine(fixtureRoot, "tool-artifacts");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(artifactsDirectory);
        Directory.CreateDirectory(evidenceDirectory);

        var cliAssembly = FindBuiltCliAssembly();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return WriteNativeCliSummary(options.Model, preflight.ModelId, evidenceDirectory,
                "not_applicable", "windows_powershell_not_found", 0, []);

        var dataPath = Path.GetFullPath(dataDirectory);
        var configPath = Path.GetFullPath(configDirectory);
        var workspacePath = Path.GetFullPath(workspace);
        var cliPath = Path.GetFullPath(cliAssembly);
        const string scriptArgument = "run-fixture-cli.ps1";
        var scriptFilePath = Path.Combine(workspacePath, scriptArgument);
        WriteFixtureConfiguration(configPath);
        File.WriteAllText(Path.Combine(workspacePath, "README.md"),
            "# Native CLI fixture\n\n" + Marker + " is a local acceptance marker.\n",
            new UTF8Encoding(false));
        File.WriteAllText(scriptFilePath, BuildPowerShellScript(dataPath, configPath, cliPath),
            new UTF8Encoding(false));

        var workspacePaths = new DefaultPlatformPaths(dataPath, configPath);
        var workspaceData = OmniHost.WorkspaceDataDirectory(workspacePaths, workspacePath);
        var cliArtifactStore = new FileArtifactStore(workspaceData);
        var gcOrphan = cliArtifactStore.PutText("unreferenced native CLI GC fixture " + Marker,
            "text/plain", ArtifactKind.ToolOutput, Sensitivity.Normal);
        var gcOrphanPath = Path.Combine(workspaceData, "blobs", "sha256",
            gcOrphan.Hash.Value[..2], gcOrphan.Hash.Value.Substring(2, 2), gcOrphan.Hash.Value);
        File.SetLastWriteTimeUtc(gcOrphanPath, DateTime.UtcNow.AddDays(-3));
        var auditFixturePath = FileAuditSink.AuditFilePath(dataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(auditFixturePath)!);
        const string auditFixtureRecord = "{\"at\":\"2000-01-01T00:00:00.0000000+00:00\",\"event\":\"fixture\"}\n";
        File.WriteAllText(auditFixturePath, auditFixtureRecord, new UTF8Encoding(false));

        var artifactStore = new FileArtifactStore(artifactsDirectory);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var catalog = ComposeCatalog(artifactStore, referenced);
        var processTool = catalog.RegisteredTools().SingleOrDefault(item =>
            item.Descriptor.Id.ToString() == "process.exec");
        if (processTool is null)
            throw new InvalidOperationException("native_cli_process_exec_tool_missing");

        var commands = BuildNativeCliCommands();
        if (options.CaseId == "native-cli-maintenance")
        {
            commands = commands.Where(item => item.Id is "gc_dry_run" or "audit_purge_dry_run").ToArray();
            var journal = new SqliteEventStore(Path.Combine(workspaceData, "journal.db"));
            journal.Close();
        }
        if (commands.Count > NativeCliMaxProcesses)
            throw new InvalidOperationException("native_cli_command_allowlist_exceeds_ceiling");
        var helpProbe = await RunNativeCliHelpProbeAsync(powershell, scriptArgument,
            workspacePath, cancellationToken).ConfigureAwait(false);
        if (!helpProbe.Verified)
            return WriteNativeCliSummary(options.Model, preflight.ModelId, evidenceDirectory,
                "failed", "fixture_cli_help_probe_failed", 0, [], fixtureRoot,
                cliHelpPreflightVerified: false);

        var toolDefinition = new ToolDefinition(processTool.Descriptor.Id.ToString(),
            processTool.Descriptor.Description, processTool.Descriptor.InputSchema.ToString());
        var selection = new ModelSelection(new ModelIdValue(preflight.ModelId),
            preflight.ContextWindow ?? throw new InvalidOperationException("configured_context_window_missing"),
            ToolMode.Direct, reasoning: null, maxOutputTokens: preflight.RequestMaxOutputTokens);
        var requestPrompt = BuildNativeCliPrompt(commands, powershell, scriptArgument, helpProbe.Text);
        return await RunNativeCliConversationAsync(options.Model, preflight, evidenceDirectory,
            fixtureRoot, workspacePath, dataPath, workspaceData, gcOrphanPath,
            powershell, scriptArgument, commands, toolDefinition,
            selection, requestPrompt, provider, catalog, artifactStore, processTool,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<NativeCliRunSummary> RunNativeCliConversationAsync(string modelKey,
        PreflightReport preflight, string evidenceDirectory, string fixtureRoot, string workspace,
        string dataDirectory, string workspaceData, string gcOrphanPath, string powershell,
        string scriptArgument, IReadOnlyList<NativeCliCommandSpec> commands, ToolDefinition toolDefinition,
        ModelSelection selection, string requestPrompt, IModelProvider provider, FakeCatalog catalog,
        IArtifactStore artifactStore, ITool processTool, CancellationToken cancellationToken)
    {
        using var wholeRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wholeRun.CancelAfter(RunTimeout(modelKey));
        var messages = new List<ModelMessage> { new(MessageRole.User, [new TextBlock(requestPrompt)]) };
        var results = new List<NativeCliCaseEvidence>(commands.Count);
        var receipts = new HashSet<string>(StringComparer.Ordinal);
        var attemptedCommands = new HashSet<string>(StringComparer.Ordinal);
        var resultAttemptCount = 0;
        var eventCount = 0;
        var generationCount = 0;
        var rejectedArgumentHashes = new Dictionary<string, int>(StringComparer.Ordinal);
        var rejectionBudget = new NativeCliRejectionBudget();
        var stopAfterRepeatedRejectedArguments = false;
        var audit = new InMemoryAuditSink();
        var decisions = catalog.RegisteredTools().ToDictionary(
            item => item.Descriptor.Id.ToString(), _ => PermissionDecision.Deny,
            StringComparer.Ordinal);
        decisions["process.exec"] = PermissionDecision.Allow;
        var policy = new ScriptedPermissionPolicy(decisions);
        ProviderState? continuation = null;
        string? providerFailure = null;
        string? finalText = null;
        var finalTurnObserved = false;

        while (generationCount < MaximumGenerationInvocations)
        {
            wholeRun.Token.ThrowIfCancellationRequested();
            var remaining = commands.Where(item => !attemptedCommands.Contains(item.Id))
                .OrderBy(item => item.Order).ToArray();
            var isFinalTurn = remaining.Length == 0;
            if (messages.Count > 1)
            {
                var nextPrompt = isFinalTurn
                    ? "All checklist entries now have a ToolResult. Read the full transcript and return each exact CLI-RESULT receipt whose result states effectVerified=true. Do not call tools."
                    : BuildNativeCliContinuation(remaining, attemptedCommands);
                messages.Add(new ModelMessage(MessageRole.User, [new TextBlock(nextPrompt)]));
            }

            var request = new ModelRequest(selection, messages.ToArray(),
                "Use only process.exec from the offered catalog. Every successful result is produced by the real local CLI through production ToolRuntime. "
                + "Never claim an effect unless its ToolResult says effectVerified=true. A harness rejection is not a CLI execution; use its bounded diagnostic to correct a later call. "
                + "The wrapper is fixed fixture plumbing. Do not change executable, script, cwd, path, timeout, or network flag. This directed test is not general process authorization.",
                isFinalTurn ? Array.Empty<ToolDefinition>() : [toolDefinition],
                isFinalTurn ? ToolChoice.None() : ToolChoice.Auto(), null, null, null, continuation);

            ModelResponse response;
            using var generationBudget = CancellationTokenSource.CreateLinkedTokenSource(wholeRun.Token);
            generationBudget.CancelAfter(GenerationTimeout(modelKey, NativeCliGenerationTimeout));
            try
            {
                response = await GetResponseAsync(provider, request, generationBudget.Token,
                    () => generationCount++).ConfigureAwait(false);
                continuation = response.State;
                WriteNativeCliProgress(evidenceDirectory, "model_response_received", generationCount,
                    resultAttemptCount, attemptedCommands, results);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                providerFailure = "cancelled_by_caller";
                break;
            }
            catch (OperationCanceledException)
            {
                providerFailure = wholeRun.IsCancellationRequested
                    ? "native_cli_run_deadline_exceeded"
                    : $"model_generation_timeout_{GenerationTimeout(modelKey, NativeCliGenerationTimeout).TotalSeconds:0}s";
                break;
            }
            catch (Exception exception)
            {
                providerFailure = SafeProviderFailureCode(exception);
                break;
            }

            messages.Add(new ModelMessage(MessageRole.Assistant, response.Content));
            var calls = response.Content.OfType<ToolCallBlock>().ToArray();
            if (calls.Length == 0)
            {
                finalText = string.Concat(response.Content.OfType<TextBlock>().Select(block => block.Text));
                finalTurnObserved = isFinalTurn;
                break;
            }

            var toolResults = new List<ContentBlock>(calls.Length);
            var attemptsBeforeTurn = resultAttemptCount;
            var resultsBeforeTurn = results.Count;
            foreach (var call in calls)
            {
                wholeRun.Token.ThrowIfCancellationRequested();
                if (!string.Equals(call.ToolName, "process.exec", StringComparison.Ordinal))
                {
                    var rejectionHash = AddRejectedNativeCall(results, toolResults, call, "unexpected_tool_name",
                        JsonSerializer.Serialize(new { providedToolNameHash = Hash(call.ToolName) }));
                    stopAfterRepeatedRejectedArguments |= IsRepeatedRejectedHash(rejectedArgumentHashes, rejectionHash);
                    continue;
                }

                NativeCliCommandSpec? spec = null;
                var gatePassed = call.ArgumentsJson.Length <= 8192
                    && TryMatchNativeCliArguments(call.ArgumentsJson, powershell, scriptArgument,
                        commands, out spec!);
                if (!gatePassed || spec is null)
                {
                    var rejectionHash = AddRejectedNativeCall(results, toolResults, call,
                        call.ArgumentsJson.Length > 8192 ? "arguments_over_length_limit" : "arguments_rejected_by_exact_fixture_gate",
                        BuildNativeCliArgumentDiagnostic(call.ArgumentsJson, powershell, scriptArgument, commands));
                    stopAfterRepeatedRejectedArguments |= IsRepeatedRejectedHash(rejectedArgumentHashes, rejectionHash);
                    continue;
                }
                if (attemptedCommands.Contains(spec.Id))
                {
                    var rejectionHash = AddRejectedNativeCall(results, toolResults, call, "duplicate_command_call_rejected",
                        JsonSerializer.Serialize(new { matchedCommandId = spec.Id, commandAlreadyAttempted = true }));
                    stopAfterRepeatedRejectedArguments |= IsRepeatedRejectedHash(rejectedArgumentHashes, rejectionHash);
                    continue;
                }
                if (resultAttemptCount >= NativeCliMaxProcesses)
                {
                    var rejectionHash = AddRejectedNativeCall(results, toolResults, call, "process_invocation_ceiling_reached",
                        JsonSerializer.Serialize(new { matchedCommandId = spec.Id, maximumProcesses = NativeCliMaxProcesses }));
                    stopAfterRepeatedRejectedArguments |= IsRepeatedRejectedHash(rejectedArgumentHashes, rejectionHash);
                    continue;
                }
                attemptedCommands.Add(spec.Id);
                resultAttemptCount++;

                var rawArguments = call.ArgumentsJson;
                var auditBefore = spec.Id == "audit_purge_dry_run"
                    ? File.ReadAllText(FileAuditSink.AuditFilePath(dataDirectory)) : null;
                var argumentHash = Hash(rawArguments);
                var events = new List<DomainEventPayload>();
                var priorAuditCount = audit.Records().Count;
                var runtime = new ToolRuntime(catalog, policy, payload =>
                {
                    eventCount++;
                    events.Add(payload);
                    return VoidBox.Instance;
                }, boundary: null, executableResolver: new SystemExecutableResolver());
                var context = new ToolExecutionContext(fixtureRoot, readRegistry: null,
                    emitEvent: payload => events.Add(payload), artifacts: artifactStore,
                    receiveMailbox: null, isInteractive: true, audit: audit,
                    resolveInteraction: interaction => interaction.Kind == InteractionKind.WeakSandboxConsent
                        ? "consent_once" : null);
                var validated = new ValidatedToolCall(call.Id, processTool.Descriptor.Id,
                    call.ProviderCallId ?? "native-cli-acceptance", rawArguments);
                ToolRuntime.Outcome outcome;
                try
                {
                    outcome = runtime.Run(validated,
                        new ToolPreparationContext(fixtureRoot, DateTimeOffset.UtcNow), context,
                        userApprovesAsk: false, wholeRun.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    var failed = new NativeCliCaseEvidence(spec.Id, "failed", true,
                        events.Any(value => value is ToolCallPrepared),
                        events.Any(value => value is ToolCallStarted or ToolCallSucceeded),
                        null, null, false, "tool_runtime_exception_type:" + exception.GetType().Name,
                        argumentHash, null);
                    results.Add(failed);
                    toolResults.Add(new ToolResultBlock(call.Id,
                        [new TextBlock("Fixture process failed in the production pipeline. " + failed.SafeDiagnostic)], true));
                    continue;
                }

                var prepared = events.Any(value => value is ToolCallPrepared);
                var started = events.Any(value => value is ToolCallStarted or ToolCallSucceeded);
                var exitCode = ReadProcessExitCode(outcome.Summary);
                var output = outcome.Preview ?? string.Empty;
                var verification = VerifyNativeCliCommand(spec, outcome, exitCode, output,
                    dataDirectory, workspace, workspaceData, gcOrphanPath, auditBefore);
                var consentOnce = events.OfType<InteractionResolved>().Any(value => value.OptionId == "consent_once");
                var consentAudited = audit.Records().Skip(priorAuditCount)
                    .Any(value => value.EventName == "WeakSandboxConsentGranted");
                var success = verification.Verified && (!consentOnce || consentAudited);
                var receipt = success ? "CLI-RESULT-" + Guid.NewGuid().ToString("N") : null;
                if (receipt is not null) receipts.Add(receipt);
                var evidence = new NativeCliCaseEvidence(spec.Id, success ? "success" : "failed",
                    true, prepared, started, exitCode, outcome.Effect.ToString(), success,
                    success ? verification.Description
                        : consentOnce && !consentAudited ? "fixture_consent_missing_audit" : verification.Description,
                    argumentHash, receipt);
                results.Add(evidence);
                var safeResult = JsonSerializer.Serialize(new
                {
                    command = spec.Id,
                    cliExitCode = exitCode,
                    effectVerified = evidence.EffectVerified,
                    outputSha256 = Hash(output),
                    outputPreview = BoundNativeCliOutput(output),
                    verificationReceipt = receipt,
                });
                toolResults.Add(new ToolResultBlock(call.Id, [new TextBlock(safeResult)], !success));
                WriteNativeCliProgress(evidenceDirectory, "tool_result_recorded", generationCount,
                    resultAttemptCount, attemptedCommands, results);
            }
            messages.Add(new ModelMessage(MessageRole.Tool, toolResults));
            WriteNativeCliProgress(evidenceDirectory, "tool_results_returned_to_model", generationCount,
                resultAttemptCount, attemptedCommands, results);
            // Different malformed strings can evade the identical-hash limit. Stop
            // after three rejected-only turns rather than burning the whole campaign.
            if (rejectionBudget.Observe(results.Skip(resultsBeforeTurn).Any(item => item.Status == "rejected"),
                resultAttemptCount > attemptsBeforeTurn))
            {
                providerFailure = "three_consecutive_gate_rejected_turns_without_execution";
                WriteNativeCliProgress(evidenceDirectory, "stopped_at_rejection_budget", generationCount,
                    resultAttemptCount, attemptedCommands, results);
                break;
            }
            if (stopAfterRepeatedRejectedArguments)
            {
                // Retain the failed command but continue the independent checklist. Repeating
                // an invalid call must not hide every later command from the report.
                var skipped = commands.OrderBy(item => item.Order)
                    .FirstOrDefault(item => !attemptedCommands.Contains(item.Id));
                if (skipped is null) break;
                attemptedCommands.Add(skipped.Id);
                results.Add(new NativeCliCaseEvidence(skipped.Id, "failed", true, false, false,
                    null, null, false, "same_argument_hash_rejected_twice; command_not_executed", null, null));
                messages.Add(new ModelMessage(MessageRole.User, [new TextBlock(
                    "The repeated rejected call was not executed. Mark " + skipped.Id
                    + " failed and do not repeat it; continue the next independent item.")]));
                stopAfterRepeatedRejectedArguments = false;
                rejectedArgumentHashes.Clear();
            }
        }

        var missing = commands.Where(item => !attemptedCommands.Contains(item.Id)).ToArray();
        foreach (var command in missing)
            results.Add(new NativeCliCaseEvidence(command.Id, "not_returned_by_model",
                resultAttemptCount > 0, false, false, null, null, false,
                generationCount >= MaximumGenerationInvocations ? "generation_invocation_ceiling_reached"
                    : "model_returned_no_gate_approved_call_for_command", null, null));

        var roundTripVerified = receipts.Count > 0 && finalTurnObserved && finalText is not null
            && receipts.All(receipt => finalText.Contains(receipt, StringComparison.Ordinal));
        var roundTripFailure = roundTripVerified ? null
            : receipts.Count == 0 ? "no_successful_result_receipts"
            : !finalTurnObserved ? "model_final_result_turn_not_observed"
            : "model_did_not_echo_every_success_receipt";
        var finalResults = results.Select(result => result.Status == "success"
            ? result with { ResultReturnedToModel = roundTripVerified,
                ResultReturnFailure = roundTripVerified ? null : roundTripFailure }
            : result).ToArray();
        var failedCount = finalResults.Count(item => item.Status == "failed");
        var rejectedCount = finalResults.Count(item => item.Status == "rejected");
        var unreturnedCount = finalResults.Count(item => item.Status == "not_returned_by_model");
        var summaryStatus = failedCount > 0 ? "failed"
            : rejectedCount > 0 || unreturnedCount > 0 || receipts.Count > 0 && !roundTripVerified
                ? "incomplete" : "success";
        return WriteNativeCliSummary(modelKey, preflight.ModelId, evidenceDirectory, summaryStatus,
            providerFailure ?? (roundTripVerified ? null : roundTripFailure), generationCount,
            finalResults, fixtureRoot, eventCount, roundTripVerified, cliHelpPreflightVerified: true);
    }

    private static string AddRejectedNativeCall(ICollection<NativeCliCaseEvidence> results,
        ICollection<ContentBlock> toolResults, ToolCallBlock call, string code, string diagnostic)
    {
        var argumentHash = Hash(call.ArgumentsJson);
        results.Add(new NativeCliCaseEvidence("gate_rejection", "rejected",
            string.Equals(call.ToolName, "process.exec", StringComparison.Ordinal), false, false,
            null, null, false, code + ":" + diagnostic, argumentHash, null));
        toolResults.Add(new ToolResultBlock(call.Id,
            [new TextBlock("Harness rejected this call before ToolRuntime. Code=" + code + "; safe diagnostic=" + diagnostic)], true));
        return argumentHash;
    }

    private static bool IsRepeatedRejectedHash(IDictionary<string, int> counts, string argumentHash)
    {
        counts.TryGetValue(argumentHash, out var count);
        counts[argumentHash] = ++count;
        return count >= 2;
    }

    private static void WriteNativeCliProgress(string evidenceDirectory, string stage,
        int generationCount, int processAttemptCount, IEnumerable<string> attemptedCommands,
        IEnumerable<NativeCliCaseEvidence> cases)
    {
        var progress = new
        {
            scenario = "native-cli-process-exec",
            stage,
            modelGenerationInvocations = generationCount,
            processAttempts = processAttemptCount,
            attemptedCommandIds = attemptedCommands.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            cases = cases.ToArray(),
            updatedAt = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(evidenceDirectory, "native-cli-commands.progress.json"),
            JsonSerializer.Serialize(progress, JsonOptions.Indented), new UTF8Encoding(false));
    }

    private static string BuildNativeCliContinuation(IReadOnlyList<NativeCliCommandSpec> remaining,
        IReadOnlySet<string> attempted)
    {
        var next = remaining[0];
        var completed = string.Join(", ", attempted.OrderBy(item => item, StringComparer.Ordinal));
        var pending = string.Join("\n", remaining.Select(item => item.Id + ": " + item.NaturalGoal));
        return "Continue after reading the previous ToolResult. Already attempted (never repeat): "
            + (completed.Length == 0 ? "none" : completed) + ".\n"
            + "Execute exactly one next pending item now, beginning with " + next.Id + ". Do not call any other process in this response.\n"
            + pending;
    }

    private static Task<NativeCliHelpProbe> RunNativeCliHelpProbeAsync(string powershell,
        string scriptArgument, string workspace, CancellationToken cancellationToken)
    {
        try
        {
            // Exercise the same fixed wrapper under the production environment allowlist before
            // spending a model call. A direct dotnet launch with inherited PATHEXT hid wrapper errors.
            var runtime = new SystemProcessRuntime();
            var helpText = new StringBuilder();
            ProcessResult? result = null;
            foreach (var helpArguments in new string[][] { ["help"], ["/help"], ["model", "policy"] })
            {
                var handle = runtime.Launch(new ProcessLaunch(powershell,
                    NativeCliArgv(scriptArgument, helpArguments), workspace,
                    new Dictionary<string, string>(), captureOutput: true), cancellationToken);
                var helpResult = runtime.Wait(handle, TimeSpan.FromSeconds(15), cancellationToken);
                result ??= helpResult;
                if (helpResult.TimedOut) return System.Threading.Tasks.Task.FromResult(new NativeCliHelpProbe(false, "help_probe_timeout"));
                helpText.AppendLine(helpResult.Stdout ?? "").AppendLine(helpResult.Stderr ?? "");
            }
            var output = helpText.ToString();
            return System.Threading.Tasks.Task.FromResult(new NativeCliHelpProbe(result!.ExitCode == 0
                && output.Contains("usage", StringComparison.OrdinalIgnoreCase)
                && output.Contains("omni", StringComparison.OrdinalIgnoreCase), output));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return System.Threading.Tasks.Task.FromResult(new NativeCliHelpProbe(false, string.Empty));
        }
    }

    private static string BuildNativeCliArgumentDiagnostic(string argumentsJson, string executable,
        string scriptArgument, IReadOnlyList<NativeCliCommandSpec> commands)
    {
        var knownTokens = commands.SelectMany(item => item.CliArguments)
            .Concat([executable, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                scriptArgument, "workspace", "30", "false", "true"])
            .ToHashSet(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return JsonSerializer.Serialize(new { jsonRoot = root.ValueKind.ToString(), argumentsLength = argumentsJson.Length });
            var properties = root.EnumerateObject().ToArray();
            var keys = properties.Select(property => property.Name is "executable" or "argv" or "cwd"
                or "timeoutSeconds" or "networkRequired" ? property.Name : "unknown-key:" + Hash(property.Name)[..12]).ToArray();
            object? argvShape = null;
            if (root.TryGetProperty("argv", out var argv) && argv.ValueKind == JsonValueKind.Array)
            {
                argvShape = argv.EnumerateArray().Take(40).Select(value =>
                {
                    if (value.ValueKind != JsonValueKind.String)
                        return "non-string:" + value.ValueKind;
                    var token = value.GetString() ?? string.Empty;
                    return knownTokens.Contains(token) ? token
                        : "other:length=" + token.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + ":sha256=" + Hash(token);
                }).ToArray();
            }
            return JsonSerializer.Serialize(new
            {
                argumentsLength = argumentsJson.Length,
                keys,
                duplicateKeys = keys.Length != properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count(),
                propertyKinds = properties.Select(property => new
                {
                    key = property.Name is "executable" or "argv" or "cwd" or "timeoutSeconds" or "networkRequired"
                        ? property.Name : "unknown-key:" + Hash(property.Name)[..12],
                    kind = property.Value.ValueKind.ToString(),
                }),
                argvCount = argvShape is string[] tokens ? tokens.Length : (int?)null,
                argvShape,
                executableMatches = root.TryGetProperty("executable", out var executableValue)
                    && executableValue.ValueKind == JsonValueKind.String && executableValue.GetString() == executable,
                cwdMatches = root.TryGetProperty("cwd", out var cwdValue)
                    && cwdValue.ValueKind == JsonValueKind.String && cwdValue.GetString() == "workspace",
                timeoutMatches = root.TryGetProperty("timeoutSeconds", out var timeoutValue)
                    && timeoutValue.ValueKind == JsonValueKind.Number && timeoutValue.TryGetInt32(out var seconds) && seconds == 30,
                networkMatches = !root.TryGetProperty("networkRequired", out var networkValue)
                    || networkValue.ValueKind == JsonValueKind.False,
            });
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { argumentsLength = argumentsJson.Length, json = "malformed" });
        }
    }

    /// <summary>Offline exact-argv gate plus fixed read-only CLI help probes; no model request.</summary>
    public static NativeCliSafetyCheck RunNativeCliSafetyCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCoreLiveAcceptance",
            "native-cli-gate-self-test-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        const string script = "fixture.ps1";
        var commands = BuildNativeCliCommands();
        var allAccepted = commands.All(item => TryMatchNativeCliArguments(
            EncodeNativeCliArguments(item, powershell, script), powershell, script, commands, out var matched)
            && matched.Id == item.Id);
        var injectionRejected = !TryMatchNativeCliArguments(
            EncodeNativeCliArguments(commands[0] with { CliArguments = ["help", "; whoami"] }, powershell, script),
            powershell, script, commands, out _);
        var budget = new NativeCliRejectionBudget();
        var stopsAfterThree = !budget.Observe(true, false) && !budget.Observe(true, false)
            && budget.Observe(true, false);
        var resetBudget = new NativeCliRejectionBudget();
        var resetsAfterExecution = !resetBudget.Observe(true, false) && !resetBudget.Observe(true, false)
            && !resetBudget.Observe(true, true) && !resetBudget.Observe(true, false)
            && !resetBudget.Observe(true, false) && resetBudget.Observe(true, false);
        if (!stopsAfterThree || !resetsAfterExecution)
            throw new InvalidOperationException("native_cli_rejection_budget_contract_failed");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        WriteFixtureConfiguration(config);
        File.WriteAllText(Path.Combine(workspace, script), BuildPowerShellScript(data, config, FindBuiltCliAssembly()));
        var probe = RunNativeCliHelpProbeAsync(powershell, script, workspace, CancellationToken.None).GetAwaiter().GetResult();
        if (!probe.Verified) throw new InvalidOperationException("offline_fixture_wrapper_help_failed");
        return new NativeCliSafetyCheck(commands.Count, allAccepted, injectionRejected,
            "offline_exact_gate_and_three_readonly_wrapper_help_processes; no_model_request")
        { RejectionBudgetVerified = stopsAfterThree && resetsAfterExecution };
    }

    private static IReadOnlyList<NativeCliCommandSpec> BuildNativeCliCommands()
    {
        var fixtureModel = NativeCliFixtureModel;
        return
        [
            new(0, "help", "Show the command-line help without starting a model turn.", ["help"],
                "help", ["Usage", "omni"]),
            new(1, "client_tools", "Inspect available tools through the CLI text-command surface.", ["/tools"],
                "client_command", ["filesystem.read"]),
            new(2, "client_commands", "Inspect available commands through the CLI text-command surface.", ["/commands"],
                "client_command", ["commands"]),
            new(3, "client_skills", "Inspect the fixture workspace skills through the CLI text-command surface.", ["/skills"],
                "client_command", ["skills"]),
            new(4, "client_memory_session", "Inspect session memory through the CLI text-command surface; do not add or forget entries.", ["/memory", "session"],
                "client_command", ["session"]),
            new(5, "permissions_list", "List configured workspace permissions using the native CLI command.", ["permissions", "list"],
                "read_only_cli", ["permission"]),
            new(6, "trust_workspace", "Mark only this disposable fixture workspace trusted using the native CLI command.", ["trust"],
                "fixture_user_data_write", ["trusted"]),
            new(7, "extensions_list", "Inspect project extensions from this fixture workspace; do not grant or revoke consent.", ["extensions"],
                "read_only_cli", ["extensions"]),
            new(8, "model_list", "List models from the synthetic fixture registry, not the user's model registry.", ["model", "list"],
                "read_only_cli", [fixtureModel]),
            new(9, "model_policy_set", "Set PatchOnly policy for the synthetic fixture model only, with revision 0 and a fixture note.",
                ["model", "policy", "set", fixtureModel, "--category", "PatchOnly", "--revision", "0", "--note", "fixture acceptance only"],
                "fixture_user_database_write", ["patch", "revision"]),
            new(10, "model_policy_show", "Show the PatchOnly policy just saved for the synthetic fixture model.",
                ["model", "policy", "show", fixtureModel], "read_only_cli", [fixtureModel, "patch"]),
            new(11, "sim", "Run the deterministic built-in CLI simulation in the disposable fixture workspace.", ["sim"],
                "fixture_journal_write", ["sim"]),
            new(12, "verify_journal", "Verify the journal created by the fixture simulation using the native read-only verifier.",
                ["verify-journal"], "read_only_cli", ["journal"]),
            new(13, "explain_fixture_readme", "Ask the native explain command to explain the fixture README filename and current runtime context; do not call a model.",
                ["explain", "Explain the fixture README.md and current runtime context"], "cli_context_report", ["fixture README.md"]),
            new(14, "gc_dry_run", "Preview workspace artifact garbage collection only; preserve every fixture file.",
                ["gc", "--scope", "workspace", "--dry-run"], "fixture_dry_run", ["dry-run"]),
            new(15, "audit_purge_dry_run", "Preview audit retention only in the disposable fixture data directory; preserve every record.",
                ["audit", "purge", "--dry-run"], "fixture_dry_run", ["dry-run"]),
        ];
    }

    private static string BuildNativeCliPrompt(IReadOnlyList<NativeCliCommandSpec> commands,
        string powershell, string scriptPath, string actualHelpOutput)
    {
        var tasks = string.Join("\n", commands.Select((item, index) =>
            (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + item.NaturalGoal));
        var english = new OmniCore.Client.Localization("en");
        var detailedUsage = english.Resolve("client.memory.usage") + "\n" + english.Resolve("cli.model.policy.set.usage");
        return "Complete this directed, local-only CLI acceptance checklist. Make one process.exec call per response, read its ToolResult, then continue with exactly the next pending item. "
            + "The following help text was produced by running the actual fixture CLI with exit code 0; use it to choose valid CLI argument syntax:\n--- CLI HELP BEGIN ---\n"
            + actualHelpOutput + "\n" + detailedUsage + "\n--- CLI HELP END ---\n"
            + "Fixture task values: model id acceptance-fixture-model; policy note text must be exactly \"fixture acceptance only\"; explanation question must be exactly \"Explain the fixture README.md and current runtime context\".\n"
            + "Use executable " + JsonSerializer.Serialize(powershell)
            + " and argv prefix [\"-NoLogo\",\"-NoProfile\",\"-NonInteractive\",\"-File\","
            + JsonSerializer.Serialize(scriptPath) + "]; append only the native CLI arguments that satisfy that task. "
            + "Use cwd \"workspace\", timeoutSeconds 30, networkRequired false. The process is confined to the temporary fixture root, and the fixed script sets child-only data/config environment to fixture directories and invokes the already-built local CLI. "
            + "Do not use a different executable, alter the script, run arbitrary shell text, use login/refresh/qualify/ask/act, or contact any provider. "
            + "The CLI slash commands here are headless ClientCommand actions dispatched by the CLI process; do not start or claim to control the TUI. "
            + "The explain/explore CLI handlers report current context and echo the requested fixture filename; they do not read README.md or call a model. "
            + "Run these commands in listed order so each synthetic policy operation observes the previous one. Do not batch calls; wait for each real ToolResult:\n" + tasks;
    }

    private static void WriteFixtureConfiguration(string configDirectory)
    {
        File.WriteAllText(Path.Combine(configDirectory, "providers.yaml"), """
            providers:
              acceptance-fixture:
                family: OpenAiChatCompatible
                baseUrl: http://127.0.0.1:1/v1
                auth: none
                billingMode: Local
            """ + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(configDirectory, "models.yaml"), """
            models:
              acceptance-fixture-model:
                provider: acceptance-fixture
                context: 4096
                maxOutput: 128
            """ + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string BuildPowerShellScript(string dataDirectory, string configDirectory,
        string cliAssembly)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var dotnet = new SystemExecutableResolver().Resolve("dotnet.exe", Path.GetDirectoryName(dataDirectory)!).ResolvedPath;
        return "$ErrorActionPreference = 'Stop'\n"
            + "$env:OMNICORE_DATA_DIR = " + Quote(dataDirectory) + "\n"
            + "$env:OMNICORE_CONFIG_DIR = " + Quote(configDirectory) + "\n"
            + "$env:OMNI_LOCALE = 'en'\n$env:NO_COLOR = '1'\n"
            // Windows PowerShell also uses PATHEXT to route even an absolute .exe through
            // its native-output pipeline. Keep this child-only value limited to .EXE.
            + "$env:PATHEXT = '.EXE'\n"
            + "$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'\n$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'\n"
            + "& " + Quote(dotnet) + " " + Quote(cliAssembly) + " --locale en @args\n"
            + "exit $LASTEXITCODE\n";
    }

    private static string FindBuiltCliAssembly()
    {
        var candidates = new List<string>();
        foreach (var startingPoint in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(startingPoint);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "OmniCore.slnx")))
                {
                    candidates.Add(Path.Combine(current.FullName, "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll"));
                    candidates.Add(Path.Combine(current.FullName, "src", "OmniCore.Cli", "bin", "Release", "net10.0", "omni.dll"));
                    break;
                }
                current = current.Parent;
            }
        }
        var existing = candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
        return existing is null ? throw new InvalidOperationException("built_cli_assembly_not_found")
            : Path.GetFullPath(existing);
    }

    private static string EncodeNativeCliArguments(NativeCliCommandSpec item, string executable,
        string scriptPath) => JsonSerializer.Serialize(new
    {
        executable,
        argv = NativeCliArgv(scriptPath, item.CliArguments),
        cwd = "workspace",
        timeoutSeconds = (int)NativeCliProcessTimeout.TotalSeconds,
        networkRequired = false,
    });

    private static bool TryMatchNativeCliArguments(string json, string executable, string scriptPath,
        IReadOnlyList<NativeCliCommandSpec> commands, out NativeCliCommandSpec matched)
    {
        matched = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length
                || properties.Any(item => item.Name is not
                    ("executable" or "argv" or "cwd" or "timeoutSeconds" or "networkRequired"))
                || properties.Length is < 4 or > 5
                || !root.TryGetProperty("executable", out var executableValue)
                || executableValue.ValueKind != JsonValueKind.String
                || !string.Equals(executableValue.GetString(), executable, StringComparison.Ordinal)
                || !root.TryGetProperty("cwd", out var cwdValue) || cwdValue.ValueKind != JsonValueKind.String
                || cwdValue.GetString() != "workspace"
                || !root.TryGetProperty("timeoutSeconds", out var timeoutValue)
                || timeoutValue.ValueKind != JsonValueKind.Number || timeoutValue.GetInt32() != 30
                || root.TryGetProperty("networkRequired", out var networkValue)
                    && networkValue.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("argv", out var argvValue) || argvValue.ValueKind != JsonValueKind.Array
                || argvValue.GetArrayLength() < 6 || argvValue.GetArrayLength() > 32)
                return false;
            var argv = argvValue.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()! : "\0").ToArray();
            var prefix = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath };
            if (argv.Length < prefix.Length + 1
                || !argv.Take(prefix.Length).SequenceEqual(prefix, StringComparer.Ordinal)) return false;
            var cliArguments = argv.Skip(prefix.Length).ToArray();
            if (cliArguments.Length == 1 && cliArguments[0] is "--help" or "-h")
            {
                matched = commands.SingleOrDefault(command => command.Id == "help")!;
                return matched is not null;
            }
            foreach (var command in commands)
            {
                var exactMatch = command.CliArguments.SequenceEqual(cliArguments, StringComparer.Ordinal);
                // CliApp.RunTypedCommand joins slash arguments before parsing. Both forms
                // are genuine equivalent read-only invocations, not broader process authority.
                var equivalentSlash = command.Id == "client_memory_session"
                    && cliArguments.SequenceEqual(new[] { "/memory session" }, StringComparer.Ordinal)
                    || command.Id == "extensions_list"
                    && cliArguments.SequenceEqual(new[] { "/extensions" }, StringComparer.Ordinal)
                    || command.Id == "gc_dry_run"
                    && (cliArguments.SequenceEqual(new[] { "gc", "--dry-run" }, StringComparer.Ordinal)
                        || cliArguments.SequenceEqual(new[] { "gc", "--dry-run", "--scope", "workspace" }, StringComparer.Ordinal));
                if (exactMatch || equivalentSlash)
                {
                    matched = command;
                    return true;
                }
            }
            return false;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        { return false; }
    }

    private static IReadOnlyList<string> NativeCliArgv(string scriptPath,
        IReadOnlyList<string> cliArguments) => new[] { "-NoLogo", "-NoProfile", "-NonInteractive",
        "-File", scriptPath }.Concat(cliArguments).ToArray();

    private static NativeCliVerification VerifyNativeCliCommand(NativeCliCommandSpec spec,
        ToolRuntime.Outcome outcome, int? exitCode, string output, string dataDirectory,
        string workspace, string workspaceData, string gcOrphanPath, string? auditBefore)
    {
        if (!outcome.Succeeded || exitCode != 0)
            return new(false, "production_process_tool_or_cli_exit_failed");
        bool Contains(string value) => output.Contains(value, StringComparison.OrdinalIgnoreCase);
        switch (spec.Id)
        {
            case "help": return new(Contains("usage") && Contains("omni"), "native_help_output_and_exit_zero");
            case "client_tools": return new(Contains("filesystem.read"), "cli_client_tools_query_returned_builtin_tool");
            case "client_commands": return new(Contains("/help") || Contains("commands"), "cli_client_commands_query_returned_command_catalog");
            case "client_skills": return new(Contains("skills"), "cli_client_skills_query_returned_fixture_snapshot");
            case "client_memory_session": return new(Contains("session"), "cli_memory_session_query_returned_snapshot");
            case "permissions_list": return new(Contains("permission"), "cli_permissions_listing_returned");
            case "trust_workspace":
                var trust = File.ReadAllText(Path.Combine(dataDirectory, "trust.yaml"));
                const string expectedTrust = "trusted: true";
                return new(trust.Contains(expectedTrust, StringComparison.OrdinalIgnoreCase), "fixture_trust_file_matches_requested_state");
            case "extensions_list": return new(Contains("extensions"), "native_extensions_query_returned");
            case "model_list": return new(Contains(NativeCliFixtureModel), "synthetic_registry_model_listed");
            case "model_policy_set":
                return new(ModelPolicyIsPatchOnly(dataDirectory)
                    && Contains("patch"), "synthetic_policy_write_created_patch_only_fixture_policy_in_user_database");
            case "model_policy_show": return ModelPolicyIsPatchOnly(dataDirectory)
                && Contains(NativeCliFixtureModel) && Contains("patch")
                    ? new(true, "saved_fixture_policy_read_back_from_native_cli_and_sqlite_store")
                    : new(false, "saved_fixture_policy_not_observed");
            case "sim": return new(File.Exists(Path.Combine(workspaceData, "journal.db")), "real_cli_simulation_created_fixture_journal");
            case "verify_journal": return new(File.Exists(Path.Combine(workspaceData, "journal.db"))
                && output.Length > 0, "real_cli_journal_verifier_completed_on_simulation_journal");
            case "explain_fixture_readme": return new(Contains("fixture README.md")
                && File.Exists(Path.Combine(workspace, "README.md")), "cli_context_report_echoed_fixture_filename; handler_does_not_read_readme_or_call_a_model");
            case "gc_dry_run": return new(File.Exists(gcOrphanPath)
                && Contains("dry-run"), "gc_dry_run_reported_without_deleting_old_unreferenced_fixture_blob");
            case "audit_purge_dry_run": return Contains("dry-run")
                && auditBefore is not null && auditBefore.Contains("\"event\":\"fixture\"", StringComparison.Ordinal)
                && File.ReadAllText(FileAuditSink.AuditFilePath(dataDirectory)) == auditBefore
                    ? new(true, "audit_retention_preview_preserved_eligible_fixture_record")
                    : new(false, "audit_retention_preview_output_or_record_preservation_not_verified");
            default: return new(false, "missing_native_cli_effect_verifier");
        }
    }

    private static bool ModelPolicyIsPatchOnly(string dataDirectory)
    {
        using var store = new SqliteModelPolicyStore(Path.Combine(dataDirectory, "user.db"));
        var policy = store.Get(ModelPolicyKey.For("acceptance-fixture", NativeCliFixtureModel),
            CancellationToken.None);
        return policy is { Revision: 1, Policy.Category: ModelPolicyCategory.PatchOnly };
    }

    private static int? ReadProcessExitCode(string? summary)
    {
        if (summary is null) return null;
        var match = Regex.Match(summary, "(?:código|code)\\s+(-?[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value,
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var code)
            ? code : null;
    }

    private static string BoundNativeCliOutput(string value)
    {
        const int maximum = 2048;
        if (value.Length <= maximum) return value;
        return value[..maximum] + "…[truncated]";
    }

    private static NativeCliRunSummary WriteNativeCliSummary(string modelKey, string modelId,
        string evidenceDirectory, string status, string? diagnostic, int generationCount,
        IReadOnlyList<NativeCliCaseEvidence> cases, string? fixtureRoot = null,
        int processEventsObserved = 0, bool resultRoundTripVerified = false,
        bool cliHelpPreflightVerified = false)
    {
        var summary = new NativeCliRunSummary("native-cli-process-exec", modelKey, modelId,
            status, diagnostic, fixtureRoot, generationCount, processEventsObserved,
            resultRoundTripVerified, cases, DateTimeOffset.UtcNow)
        { CliHelpPreflightVerified = cliHelpPreflightVerified };
        File.WriteAllText(Path.Combine(evidenceDirectory, "native-cli-commands.json"),
            JsonSerializer.Serialize(summary, JsonOptions.Indented), new UTF8Encoding(false));
        return summary;
    }

    private sealed record NativeCliCommandSpec(int Order, string Id, string NaturalGoal,
        IReadOnlyList<string> CliArguments, string EffectKind, IReadOnlyList<string> ExpectedOutputFragments);

    private sealed record NativeCliVerification(bool Verified, string Description);

    private sealed class NativeCliRejectionBudget
    {
        private int _consecutiveRejectedTurns;
        public bool Observe(bool rejected, bool executed)
        {
            _consecutiveRejectedTurns = rejected && !executed ? _consecutiveRejectedTurns + 1 : 0;
            return _consecutiveRejectedTurns >= 3;
        }
    }
}

internal sealed record NativeCliRunSummary(string Scenario, string ModelKey, string ModelId,
    string Status, string? Diagnostic, string? FixtureRoot, int ModelGenerationInvocations,
    int ToolPipelineEventsObserved, bool ResultRoundTripVerified,
    IReadOnlyList<NativeCliCaseEvidence> Cases, DateTimeOffset CompletedAt)
{
    public bool CliHelpPreflightVerified { get; init; }
}

internal sealed record NativeCliCaseEvidence(string CommandId, string Status,
    bool ModelRequestedProcessExec, bool ToolRuntimePrepared, bool ProcessStarted,
    int? CliExitCode, string? ToolEffect, bool EffectVerified, string? SafeDiagnostic,
    string? ArgumentSha256, string? Receipt)
{
    public bool ResultReturnedToModel { get; init; }
    public string? ResultReturnFailure { get; init; }
}

internal sealed record NativeCliSafetyCheck(int AllowedCommandCount, bool CanonicalArgumentsAccepted,
    bool InjectedArgumentsRejected, string Scope)
{
    public bool RejectionBudgetVerified { get; init; }
}

internal sealed record NativeCliHelpProbe(bool Verified, string Text);
