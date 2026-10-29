namespace OmniCoreLiveAcceptance;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const string LunaModel = "gpt-6-luna";
    private const string QwenModel = "qwen38-27b-abl-v9-best-q4kxl";
    private const string Marker = "OMNICORE-LIVE-ACCEPTANCE";
    private static readonly TimeSpan MinimumLunaCredentialTtl = TimeSpan.FromMinutes(15);
    private const int MaximumGenerationInvocations = 32;
    // Local prefill may legitimately take several minutes. These are acceptance-only
    // deadlines, not persisted server/model settings. Preserve the existing Luna limits.
    private static readonly TimeSpan QwenGenerationTimeout = TimeSpan.FromMinutes(60);
    private static TimeSpan GenerationTimeout(string model, TimeSpan other) =>
        model == "qwen" ? QwenGenerationTimeout : other;
    private static TimeSpan CaseTimeout(string model) => model == "qwen"
        ? QwenGenerationTimeout * 2 : TimeSpan.FromSeconds(90);
    private static TimeSpan RunTimeout(string model) => model == "qwen"
        ? QwenGenerationTimeout * MaximumGenerationInvocations + TimeSpan.FromMinutes(5)
        : TimeSpan.FromMinutes(25);
    public static IReadOnlyList<string> KnownCaseIds { get; } = new[]
    {
        "filesystem.read", "filesystem.list", "filesystem.search", "reference.resolve",
        "filesystem.write", "filesystem.patch", "process.exec", "shell.exec",
        "artifact.read", "plan.propose", "mode.propose", "user.ask", "model-command-boundary",
        "core.agents.mailbox.receive", "worktree.integrate",
    };

    public PreflightReport Preflight(string modelName)
    {
        var errors = new List<string>();
        var paths = OmniHost.CreatePlatformPaths();
        var registry = OmniHost.LoadUserModelRegistry(paths);
        var expectedModel = modelName == "luna" ? LunaModel : QwenModel;
        var model = registry.Model(expectedModel);
        var provider = model is null ? null : registry.Provider(model.ProviderId);
        if (model is null) errors.Add("configured_model_missing");
        if (provider is null) errors.Add("configured_provider_missing");
        if (model is not null && !string.Equals(model.Id, expectedModel, StringComparison.Ordinal))
            errors.Add("configured_model_mismatch");

        string authentication = "not_checked";
        DateTimeOffset? credentialExpiresAt = null;
        string? qwenCredentialStatus = null;
        if (modelName == "luna" && provider is not null)
        {
            if (provider.Family != ProviderFamily.OpenAIResponses || !provider.SupportsNativeToolCalls
                || !string.Equals(provider.Profile, "codex", StringComparison.Ordinal)
                || provider.BillingMode != BillingMode.IncludedQuota)
                errors.Add("luna_provider_not_configured_as_chatgpt_subscription");
            var status = OmniHost.CreateChatGptAuth(paths).Status(CancellationToken.None);
            credentialExpiresAt = status.ExpiresAt;
            authentication = status.LoggedIn && !status.Expired
                && status.ExpiresAt is { } expiry && expiry - DateTimeOffset.UtcNow >= MinimumLunaCredentialTtl
                    ? "ready_without_refresh"
                    : "missing_expired_or_near_expiry";
            if (authentication != "ready_without_refresh") errors.Add("subscription_not_fresh_enough_without_refresh");
        }
        else if (modelName == "qwen" && provider is not null)
        {
            if (provider.BillingMode != BillingMode.Local || provider.LocalHost?.Mode != LocalHostMode.Attach)
                errors.Add("qwen_provider_must_be_local_attach");
            if (provider.Family != ProviderFamily.OpenAiChatCompatible || !provider.SupportsNativeToolCalls)
                errors.Add("qwen_provider_must_support_native_tools");
            if (provider.Auth.Kind is not (AuthKind.None or AuthKind.ApiKey))
                errors.Add("qwen_auth_kind_not_supported_without_interactive_setup");
            if (!IsConfiguredQwenEndpoint(provider.BaseUrl)) errors.Add("qwen_endpoint_not_the_configured_private_lan_endpoint");
            if (string.IsNullOrWhiteSpace(provider.TrustedCertificatePath)
                || !File.Exists(provider.TrustedCertificatePath))
                errors.Add("qwen_configured_ca_certificate_missing");
            if (provider.Auth.Kind == AuthKind.ApiKey)
            {
                var key = ReadConfiguredApiKey(paths, provider);
                qwenCredentialStatus = key is { Length: > 0 } ? "configured_secret_available" : "configured_secret_missing";
                if (key is null) errors.Add("qwen_configured_secret_missing");
                authentication = qwenCredentialStatus;
            }
            else authentication = "not_required";
        }

        var preflightArtifactStore = new FileArtifactStore(Path.Combine(Path.GetTempPath(),
            "OmniCoreLiveAcceptance", "preflight-artifacts"));
        var catalog = ComposeCatalog(preflightArtifactStore, new HashSet<string>(StringComparer.Ordinal));
        var builtInIds = catalog.RegisteredTools().Where(tool => tool.Descriptor.Source.Kind == SourceKind.BuiltIn
                && !tool.Descriptor.Id.ToString().StartsWith("fake.", StringComparison.Ordinal))
            .Select(tool => tool.Descriptor.Id.ToString()).Order(StringComparer.Ordinal).ToArray();
        var notExercisedCases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["worktree.integrate"] = "not_exercised: no real ownership lease and authenticated proposal are injected",
        };
        return new PreflightReport(modelName, expectedModel, model is not null, provider is not null,
            model?.ContextWindow, model?.MaxOutputTokens, modelName == "qwen" ? model?.MaxOutputTokens : null,
            authentication, qwenCredentialStatus, credentialExpiresAt, builtInIds, notExercisedCases,
            errors.Count == 0, errors);
    }

    public string CreateEvidenceDirectory(string modelName)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var root = Path.Combine(Path.GetTempPath(), "OmniCoreLiveAcceptance", modelName + "-" + stamp + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public SelfTestSummary RunSelfTest(string? selectedCase = null)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "OmniCoreLiveAcceptance",
            "self-test-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(fixtureRoot, "workspace");
        var artifactsPath = Path.Combine(fixtureRoot, "artifacts");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(artifactsPath);
        var artifacts = new FileArtifactStore(artifactsPath);
        var artifact = artifacts.PutText("fixture artifact " + Marker, "text/plain",
            ArtifactKind.ToolOutput, Sensitivity.Normal);
        var referenced = new HashSet<string>(StringComparer.Ordinal) { artifact.Hash.Value };
        SeedWorkspace(workspace);
        var catalog = ComposeCatalog(artifacts, referenced);
        var cases = BuildCases(workspace, artifact.Hash.Value);
        var selected = selectedCase is null ? cases.ToArray()
            : cases.Where(item => item.Id == selectedCase).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Unknown self-test case.");

        var results = new List<SelfTestCaseResult>();
        foreach (var item in selected)
        {
            if (item.Id is "model-command-boundary" or "worktree.integrate")
            {
                results.Add(new SelfTestCaseResult(item.Id, "not_exercised", false, false,
                    item.Id == "model-command-boundary"
                        ? "requires_model_request; omitted from offline self-test"
                        : "requires_live_host_fixture_or_coordinator"));
                continue;
            }
            var tool = catalog.RegisteredTools().SingleOrDefault(candidate =>
                candidate.Descriptor.Id.ToString() == item.Id);
            if (tool is null)
            {
                results.Add(new SelfTestCaseResult(item.Id, "not_exercised", false, false,
                    "tool_not_composed_in_offline_fixture"));
                continue;
            }

            var json = SelfTestArguments(item, artifact.Hash.Value);
            if (item.Id == "user.ask")
            {
                var nullableQuestionnaire = JsonSerializer.Serialize(new
                {
                    title = "Fixture question", description = (string?)null,
                    questions = new[] { new { id = "fixture-answer", prompt = "Which marker is present?",
                        kind = "FreeText", helpText = (string?)null, options = Array.Empty<object>(), other = (object?)null,
                        required = false, minSelections = (int?)null, maxSelections = (int?)null, maxTextLength = 128 } },
                });
                if (!SafeArgumentGate(item, nullableQuestionnaire, workspace)
                    || SafeArgumentGate(item, nullableQuestionnaire.Replace("Which marker is present?", "Which secret marker is present?", StringComparison.Ordinal), workspace)
                    || SafeArgumentGate(item, nullableQuestionnaire.Replace("\"description\":null", "\"description\":\"api key\"", StringComparison.Ordinal), workspace))
                    throw new InvalidOperationException("questionnaire_nullable_safety_contract_failed");
                json = nullableQuestionnaire;
            }
            if (!SafeArgumentGate(item, json, workspace))
            {
                results.Add(new SelfTestCaseResult(item.Id, "failed", false, false,
                    "self_test_arguments_rejected_by_safety_gate"));
                continue;
            }

            var decisions = catalog.RegisteredTools().ToDictionary(
                candidate => candidate.Descriptor.Id.ToString(), _ => PermissionDecision.Deny,
                StringComparer.Ordinal);
            decisions[item.Id] = item.Id == "user.ask" ? PermissionDecision.Ask : PermissionDecision.Allow;
            var policy = new ScriptedPermissionPolicy(decisions);
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(catalog, policy,
                payload => { events.Add(payload); return VoidBox.Instance; }, boundary: null,
                executableResolver: new SystemExecutableResolver());
            var call = new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
                "offline-self-test", json);
            ToolRuntime.Outcome outcome;
            try
            {
                outcome = runtime.Run(call, new ToolPreparationContext(workspace, DateTimeOffset.UtcNow),
                    new ToolExecutionContext(workspace, readRegistry: null, artifacts: artifacts, isInteractive: false),
                    userApprovesAsk: false, cancellationToken: CancellationToken.None);
            }
            catch (Exception exception)
            {
                results.Add(new SelfTestCaseResult(item.Id, "failed",
                    events.Any(payload => payload is ToolCallPrepared), false,
                    "pipeline_exception_type:" + exception.GetType().Name));
                continue;
            }

            var prepared = events.Any(payload => payload is ToolCallPrepared);
            if (item.Id == "core.agents.mailbox.receive")
            {
                var refusedWithoutBinding = events.OfType<ToolCallFailed>().Any(failure =>
                    failure.ErrorCode == ToolErrorCode.CapabilityRefused);
                results.Add(new SelfTestCaseResult(item.Id,
                    prepared && refusedWithoutBinding ? "blocked_no_supervisor" : "failed",
                    prepared, refusedWithoutBinding,
                    prepared && refusedWithoutBinding
                        ? "real_tool_schema_prepare_and_production_capability_refusal_without_receive_binding"
                        : "mailbox_without_binding_did_not_refuse_as_expected"));
                continue;
            }
            if (item.Id == "user.ask")
            {
                results.Add(new SelfTestCaseResult(item.Id,
                    prepared ? "blocked_interaction_required" : "failed", prepared, false,
                    prepared ? "schema_preparation_checked; no interactive Host client" : "questionnaire_prepare_failed"));
                continue;
            }

            var verified = outcome.Succeeded && VerifyEffect(item, json, outcome, workspace,
                artifacts, referenced).Verified;
            var environmentalBlock = outcome.Summary?.Contains("WEAK_SANDBOX_CONSENT_REQUIRED", StringComparison.Ordinal) == true;
            var status = verified ? "success" : environmentalBlock || outcome.FinalState.ToString() == "Blocked" ? "blocked" : "failed";
            results.Add(new SelfTestCaseResult(item.Id, status, prepared, outcome.Succeeded,
                verified ? "schema_prepare_permission_execute_and_fixture_effect_verified"
                    : outcome.Summary ?? outcome.FinalState.ToString()));
        }

        return new SelfTestSummary("offline_pipeline_only_no_model_requests", fixtureRoot,
            results.Count, results.Count(item => item.Status == "success"),
            results.Count(item => item.Status == "failed"), results.Count(item => item.Status.StartsWith("blocked", StringComparison.Ordinal)),
            results);
    }

    private static string SelfTestArguments(AcceptanceCase item, string artifactHash)
    {
        object arguments = item.Id switch
        {
            "filesystem.read" => new { path = "read-me.txt" },
            "filesystem.list" => new { path = ".", recursive = false, maxEntries = 30 },
            "filesystem.search" => new { pattern = Marker, regex = false, path = ".", caseSensitive = true },
            "reference.resolve" => new { @ref = "@read-me.txt" },
            "filesystem.write" => new { path = "new-file.txt", content = Marker + "-WRITTEN", expectedVersion = (string?)null },
            "filesystem.patch" => new { path = "patch-target.txt", expectedVersion = FileVersion.VersionToken(Encoding.UTF8.GetBytes("before marker\nkeep-this-line\n")), oldText = "before", newText = "after" },
            "process.exec" => new { executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), argv = new[] { "/d", "/c", "echo " + Marker }, cwd = ".", timeoutSeconds = 5, networkRequired = false },
            "shell.exec" => new { command = "echo " + Marker, cwd = ".", timeoutSeconds = 5 },
            "artifact.read" => new { hash = artifactHash, offset = 0, limit = 256 },
            "plan.propose" => new { kind = "start", itemId = "fixture-item", reason = "acceptance fixture proposal only" },
            "mode.propose" => new { mode = "plan", reason = "acceptance fixture proposal only" },
            "user.ask" => new { title = "Fixture question", questions = new[] { new { id = "fixture-answer", prompt = "Which marker is present?", kind = "FreeText" } } },
            "core.agents.mailbox.receive" => new { },
            _ => throw new ArgumentOutOfRangeException(nameof(item)),
        };
        return JsonSerializer.Serialize(arguments);
    }

    public async Task<RunSummary> RunAsync(Options options, PreflightReport preflight, string evidenceDirectory)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "OmniCoreLiveAcceptance", "fixture-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(fixtureRoot, "workspace");
        var artifactsPath = Path.Combine(fixtureRoot, "artifacts");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(artifactsPath);
        var artifactStore = new FileArtifactStore(artifactsPath);
        var reference = artifactStore.PutText("fixture artifact " + Marker, "text/plain",
            ArtifactKind.ToolOutput, Sensitivity.Normal);
        var referenced = new HashSet<string>(StringComparer.Ordinal) { reference.Hash.Value };
        SeedWorkspace(workspace);

        var catalog = ComposeCatalog(artifactStore, referenced);
        var cases = BuildCases(workspace, reference.Hash.Value);
        var selected = options.CaseId == "core-tools"
            ? cases.Where(item => item.Id is not ("process.exec" or "shell.exec" or "user.ask"
                or "core.agents.mailbox.receive" or "worktree.integrate" or "model-command-boundary")).ToArray()
            : options.All
            ? cases.ToArray()
            : cases.Where(item => string.Equals(item.Id, options.CaseId, StringComparison.Ordinal)).ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("Unknown case id. Use --preflight to list tool ids.");

        var caseIds = selected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var results = new List<CaseEvidence>();
        foreach (var notExercised in preflight.NotExercisedCases.Where(pair => options.All || caseIds.Contains(pair.Key)))
        {
            var evidence = CaseEvidence.NotExercised(options.Model, preflight.ModelId, notExercised.Key,
                notExercised.Value);
            results.Add(evidence);
            WriteEvidence(evidenceDirectory, evidence);
        }

        var runnable = selected.Where(item => !preflight.NotExercisedCases.ContainsKey(item.Id)).ToArray();
        if (runnable.Length == 0)
            return new RunSummary(options.Model, preflight.ModelId, evidenceDirectory, fixtureRoot,
                results.Count, 0, 0, 0, results.Count, 0, null, MaximumGenerationInvocations,
                preflight.ContextWindow, preflight.ConfiguredMaxOutputTokens,
                preflight.RequestMaxOutputTokens, null, results);

        var provider = ConnectModelProvider(options.Model, preflight.ModelId);
        using var totalBudget = new CancellationTokenSource(RunTimeout(options.Model));
        var providerFailuresInRow = 0;
        var generationInvocations = 0;
        foreach (var acceptanceCase in runnable)
        {
            totalBudget.Token.ThrowIfCancellationRequested();
            var caseStartedAt = DateTimeOffset.UtcNow;
            var maximumForCase = acceptanceCase.Id == "model-command-boundary" ? 1 : 2;
            if (generationInvocations + maximumForCase > MaximumGenerationInvocations)
            {
                var bounded = CaseEvidence.Blocked(options.Model, preflight.ModelId, acceptanceCase.Id,
                    false, false, null, "generation_invocation_ceiling_reached");
                results.Add(bounded);
                WriteEvidence(evidenceDirectory, bounded);
                continue;
            }
            if (providerFailuresInRow >= 3)
            {
                var stopped = CaseEvidence.Blocked(options.Model, preflight.ModelId, acceptanceCase.Id,
                    false, false, null, "provider_failure_circuit_open_after_three_consecutive_failures");
                results.Add(stopped);
                WriteEvidence(evidenceDirectory, stopped);
                continue;
            }
            using var caseBudget = CancellationTokenSource.CreateLinkedTokenSource(totalBudget.Token);
            caseBudget.CancelAfter(CaseTimeout(options.Model));
            var evidence = await RunCaseAsync(options.Model, preflight, provider, catalog,
                acceptanceCase, workspace, artifactStore, referenced, caseBudget.Token,
                () => generationInvocations++, options.AllowFixtureProcess, options.AutoSelect,
                progress => File.WriteAllText(Path.Combine(evidenceDirectory, acceptanceCase.Id + ".progress.json"),
                    JsonSerializer.Serialize(progress, JsonOptions.Indented))).ConfigureAwait(false);
            evidence = evidence with
            {
                StartedAt = caseStartedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                OfferedToolNames = acceptanceCase.Id == "model-command-boundary"
                    ? evidence.OfferedToolNames
                    : options.AutoSelect ? catalog.RegisteredTools().Select(tool => tool.Descriptor.Id.ToString()).ToArray()
                        : [acceptanceCase.Id],
                InvocationMode = options.AutoSelect && acceptanceCase.Id != "model-command-boundary"
                    ? "auto_selection_real_catalog_fixture_execution" : evidence.InvocationMode,
                PermissionPolicyFixture = acceptanceCase.Id == "user.ask"
                    ? "deny_all_except_target_ask_without_host_interaction"
                    : evidence.InvocationMode == "boundary_auto_catalog"
                        ? "no_tool_runtime_permission_decision_no_execution"
                        : "deny_all_except_exact_case_tool_fixture_only",
            };
            results.Add(evidence);
            WriteEvidence(evidenceDirectory, evidence);
            providerFailuresInRow = evidence.ProviderRequestFailed ? providerFailuresInRow + 1 : 0;
        }

        var counts = results.GroupBy(item => item.Status).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var notExercisedCount = results.Count(item => item.Status == "not_exercised");
        return new RunSummary(options.Model, preflight.ModelId, evidenceDirectory, fixtureRoot,
            results.Count, counts.GetValueOrDefault("success"), counts.GetValueOrDefault("failed"),
            counts.GetValueOrDefault("blocked"), notExercisedCount, generationInvocations,
            (provider as IModelRequestAttemptBound)?.MaximumGenerationRequestAttempts,
            MaximumGenerationInvocations, preflight.ContextWindow, preflight.ConfiguredMaxOutputTokens, preflight.RequestMaxOutputTokens,
            (provider as IModelRequestAttemptBound)?.MaximumGenerationRequestAttempts is { } attempts
                ? attempts * generationInvocations : null,
            results);
    }

    private static FakeCatalog ComposeCatalog(IArtifactStore artifactStore,
        HashSet<string> referencedArtifacts)
    {
        var artifactTool = new ArtifactReadTool(artifactStore, hash => referencedArtifacts.Contains(hash));
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(),
            includeSimulationTools: false, includeMutationTools: true, includeProcessTools: true,
            processSandboxStrength: SandboxStrength.Strong, artifactReadTool: artifactTool,
            includeControlTools: true, includeMailboxReceive: true, includeShellTool: true);
        return hostTools.Catalog();
    }

    private static IReadOnlyList<AcceptanceCase> BuildCases(string workspace, string artifactHash)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var cmd = Path.Combine(systemDirectory, "cmd.exe");
        var patchPath = "patch-target.txt";
        var patchBytes = Encoding.UTF8.GetBytes("before marker\nkeep-this-line\n");
        var patchVersion = FileVersion.VersionToken(patchBytes);
        return
        [
            new("filesystem.read", "Read the fixture file read-me.txt. Make exactly this function call; do not read any other path.",
                "path", "read-me.txt", "fixture read content contains " + Marker),
            new("filesystem.list", "List the fixture workspace root only (path is dot) with recursive false and maxEntries 30.",
                "path", ".", "entries include read-me.txt and patch-target.txt"),
            new("filesystem.search", "Search the workspace root for the exact literal marker OMNICORE-LIVE-ACCEPTANCE, regex false, caseSensitive true.",
                "pattern", Marker, "results include read-me.txt and patch-target.txt"),
            new("reference.resolve", "Resolve only the workspace reference @read-me.txt.",
                "ref", "@read-me.txt", "file reference resolves inside the fixture"),
            new("filesystem.write", "Create only new-file.txt with content OMNICORE-LIVE-ACCEPTANCE-WRITTEN. Set expectedVersion to null if the schema includes it.",
                "path", "new-file.txt", "created fixture file matches exact marker"),
            new("filesystem.patch", "Patch patch-target.txt exactly once: expectedVersion " + patchVersion
                + ", replace oldText before with newText after. Preserve the remaining line.",
                "path", patchPath, "post-image begins after and preserves keep-this-line"),
            new("process.exec", "Run only the harmless local command that prints OMNICORE-LIVE-ACCEPTANCE. Use executable "
                + JsonSerializer.Serialize(cmd) + ", argv [\"/d\",\"/c\",\"echo " + Marker + "\"], cwd \".\", "
                + "timeoutSeconds 5 and networkRequired false.", "executable", cmd, "stdout contains exact marker"),
            new("shell.exec", "Run only the harmless shell command echo OMNICORE-LIVE-ACCEPTANCE in cwd \".\" with timeoutSeconds 5.",
                "command", "echo " + Marker, "stdout contains exact marker"),
            new("artifact.read", "Read hash " + artifactHash + " at offset 0 with limit 256 from the fixture artifact.",
                "hash", artifactHash, "preview matches real fixture CAS text"),
            new("plan.propose", "Propose a start mutation for symbolic item fixture-item with reason 'acceptance fixture proposal only'. This is a declaration, not an applied plan change.",
                "kind", "start", "canonical plan proposal is returned; no external plan is changed"),
            new("mode.propose", "Recommend mode plan with reason 'acceptance fixture proposal only'. Do not claim authority changed.",
                "mode", "plan", "recommendation is returned while mode authority remains unchanged"),
            new("user.ask", "Create a valid questionnaire with title 'Fixture question' and one free-text question id 'fixture-answer' asking 'Which marker is present?'. Do not request secrets or approval.",
                "title", "Fixture question", "schema and questionnaire preparation checked; no interactive host client is bound"),
            new("model-command-boundary", "I need a status check. Run /doctor for me. If slash commands are not callable tools, say so; do not pretend to execute it.",
                "", "", "slash commands remain outside the model tool catalog; no client command is executed"),
            new("core.agents.mailbox.receive", "Try to receive one message from the supervisor for this run. Do not invent a message or acknowledgement.",
                "", "", "pipeline must refuse without a bound Host receive operation; no message or acknowledgement is fabricated"),
            new("worktree.integrate", "Apply only the exact previously previewed integration proposal and its complete target list.",
                "", "", "not exercised: no real ownership lease and integration coordinator are attached"),
        ];
    }

    private async Task<CaseEvidence> RunCaseAsync(string modelName, PreflightReport preflight, IModelProvider provider,
        FakeCatalog catalog, AcceptanceCase acceptanceCase, string workspace, IArtifactStore artifactStore,
        HashSet<string> referencedArtifacts, CancellationToken cancellationToken, Action onGenerationInvocation,
        bool allowFixtureProcess, bool autoSelect, Action<object> recordProgress)
    {
        var modelId = preflight.ModelId;
        var stage = "before_generation";
        var modelRequested = false;
        var prepared = false;
        var executed = false;
        string? argumentsHash = null;
        void Progress(string next)
        {
            stage = next;
            recordProgress(new { acceptanceCase.Id, modelId, stage, utc = DateTimeOffset.UtcNow,
                modelRequested, prepared, executed, argumentsHash });
        }
        if (acceptanceCase.Id == "model-command-boundary")
        {
            try
            {
                var definitions = catalog.RegisteredTools().Where(item => item.Descriptor.Source.Kind == SourceKind.BuiltIn
                        && !item.Descriptor.Id.ToString().StartsWith("fake.", StringComparison.Ordinal))
                    .Select(item => new ToolDefinition(item.Descriptor.Id.ToString(), item.Descriptor.Description,
                        item.Descriptor.InputSchema.ToString())).ToArray();
                var commandName = "/doctor";
                var commandIsTool = definitions.Any(item => string.Equals(item.Name, commandName,
                    StringComparison.OrdinalIgnoreCase));
                var request = new ModelRequest(new ModelSelection(new ModelIdValue(modelId),
                        preflight.ContextWindow ?? throw new InvalidOperationException("configured_context_window_missing"),
                        ToolMode.Direct, reasoning: null, maxOutputTokens: preflight.RequestMaxOutputTokens),
                    [new ModelMessage(MessageRole.User, [new TextBlock(acceptanceCase.Prompt)])],
                    "Only use the provided built-in tools. Slash commands are client commands, not tools. Never claim a command ran.",
                    definitions, ToolChoice.Auto(), null, null, null, null);
                var response = await GetResponseAsync(provider, request, cancellationToken, onGenerationInvocation).ConfigureAwait(false);
                var noToolCall = response.Content.All(block => block is not ToolCallBlock);
                return CaseEvidence.Boundary(modelName, modelId, acceptanceCase.Id,
                    requestedTool: !noToolCall, noToolCall && !commandIsTool,
                    definitions.Select(item => item.Name).Order(StringComparer.Ordinal).ToArray());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return CaseEvidence.ProviderFailure(modelName, modelId, acceptanceCase.Id,
                    SafeProviderFailureCode(exception));
            }
        }
        var tool = catalog.RegisteredTools().SingleOrDefault(item => item.Descriptor.Id.ToString() == acceptanceCase.Id);
        if (tool is null) return CaseEvidence.NotExercised(modelName, modelId, acceptanceCase.Id, "tool_not_composed_without_real_host_coordinator");

        try
        {
            var targetDefinition = new ToolDefinition(tool.Descriptor.Id.ToString(), tool.Descriptor.Description,
                tool.Descriptor.InputSchema.ToString());
            var offered = autoSelect ? catalog.RegisteredTools().Select(candidate => new ToolDefinition(
                candidate.Descriptor.Id.ToString(), candidate.Descriptor.Description,
                candidate.Descriptor.InputSchema.ToString())).ToArray() : [targetDefinition];
            var request = new ModelRequest(new ModelSelection(new ModelIdValue(modelId),
                    preflight.ContextWindow ?? throw new InvalidOperationException("configured_context_window_missing"),
                    ToolMode.Direct, reasoning: null,
                    maxOutputTokens: preflight.RequestMaxOutputTokens),
                [new ModelMessage(MessageRole.User, [new TextBlock(acceptanceCase.Prompt)])],
                "Select the appropriate tool for the task and call it once with arguments restricted to the temporary fixture. "
                + "Do not access external resources or claim an action succeeded before receiving its result.",
                offered, autoSelect ? ToolChoice.Auto() : ToolChoice.Exact(acceptanceCase.Id), null, null, null, null);
            ModelResponse response;
            try
            {
                Progress("requesting_tool_call");
                response = await GetResponseAsync(provider, request, cancellationToken, onGenerationInvocation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return CaseEvidence.ProviderFailure(modelName, modelId, acceptanceCase.Id,
                    SafeProviderFailureCode(exception));
            }
            var calls = response.Content.OfType<ToolCallBlock>().ToArray();
            if (calls.Length != 1 || !string.Equals(calls[0].ToolName, acceptanceCase.Id, StringComparison.Ordinal))
                return CaseEvidence.Failed(modelName, modelId, acceptanceCase.Id, false, false, null,
                    "model_did_not_return_exactly_one_requested_tool_call", response.StopReason.ToString()) with
                {
                    RequestedToolCount = calls.Length,
                    RequestedToolNames = calls.Take(16).Select(item => KnownCaseIds.Contains(item.ToolName, StringComparer.Ordinal)
                        ? item.ToolName : "<unknown-tool>").ToArray(),
                };

            var call = calls[0];
            modelRequested = true;
            argumentsHash = Hash(call.ArgumentsJson);
            Progress("tool_call_received");
            if (call.ArgumentsJson.Length > 16_384 || !SafeArgumentGate(acceptanceCase, call.ArgumentsJson, workspace))
                return CaseEvidence.Blocked(modelName, modelId, acceptanceCase.Id, true, false,
                    Hash(call.ArgumentsJson), "arguments_outside_fixture_safety_envelope",
                    safeArgumentShape: DescribeArgumentShape(call.ArgumentsJson)) with
                {
                    CanonicalArgumentMatches = CompareCanonicalArguments(acceptanceCase, call.ArgumentsJson),
                };

            // ToolRuntime is the production schema -> Prepare -> Permission -> Execute pipeline.
            // It runs one exact tool with a one-operation fixture-only Allow and a real CAS store.
            var events = new List<DomainEventPayload>();
            var decision = acceptanceCase.Id == "user.ask" ? PermissionDecision.Ask : PermissionDecision.Allow;
            var decisions = catalog.RegisteredTools().ToDictionary(
                candidate => candidate.Descriptor.Id.ToString(), _ => PermissionDecision.Deny,
                StringComparer.Ordinal);
            decisions[acceptanceCase.Id] = decision;
            var policy = new ScriptedPermissionPolicy(decisions);
            var runtime = new ToolRuntime(catalog, policy,
                payload => { events.Add(payload); return VoidBox.Instance; }, boundary: null,
                executableResolver: new SystemExecutableResolver());
            // Explicit task authorization, limited to the exact harmless command accepted above.
            // Use production consent_once; never grant global/run consent or force Weak launch.
            var processConsent = allowFixtureProcess && acceptanceCase.Id is "process.exec" or "shell.exec";
            var audit = new InMemoryAuditSink();
            var context = new ToolExecutionContext(workspace,
                readRegistry: null, emitEvent: payload => events.Add(payload), artifacts: artifactStore,
                receiveMailbox: null, isInteractive: processConsent, audit: audit,
                resolveInteraction: interaction => processConsent && interaction.Kind == InteractionKind.WeakSandboxConsent
                    ? "consent_once" : null);
            var validated = new ValidatedToolCall(call.Id, new ToolId(acceptanceCase.Id),
                call.ProviderCallId ?? "live-acceptance", call.ArgumentsJson);
            var outcome = runtime.Run(validated, new ToolPreparationContext(workspace, DateTimeOffset.UtcNow),
                context, userApprovesAsk: false, cancellationToken);
            var schemaAndPrepareValid = events.Any(item => item is ToolCallPrepared);
            prepared = schemaAndPrepareValid;
            executed = events.Any(item => item is ToolCallStarted or ToolCallSucceeded);
            Progress("pipeline_finished");
            var argsHash = Hash(call.ArgumentsJson);
            var toolSchemaRejected = events.OfType<ToolCallRejected>().Any(rejection =>
                rejection.Reason.StartsWith(ToolErrorCode.InvalidArgumentsCode + ":", StringComparison.Ordinal));
            var toolSchemaValid = !toolSchemaRejected;
            var decodedQuestionnaire = acceptanceCase.Id == "user.ask"
                ? QuestionnaireCodec.DecodeSchema(call.ArgumentsJson) : null;
            var questionnaireValidation = decodedQuestionnaire is null ? null
                : QuestionnaireValidator.ValidateSchema(decodedQuestionnaire, QuestionnaireLimits.Default());
            var questionnaireErrorCodes = questionnaireValidation is { Valid: false }
                ? string.Join(',', questionnaireValidation.Errors.Take(4).Select(error => error.Code.ToString()))
                : decodedQuestionnaire is null && acceptanceCase.Id == "user.ask" ? "decode_failed" : null;
            if (acceptanceCase.Id == "user.ask")
            {
                if (!toolSchemaValid || !schemaAndPrepareValid)
                    return CaseEvidence.Failed(modelName, modelId, acceptanceCase.Id, true,
                        toolSchemaValid && schemaAndPrepareValid, argsHash,
                        toolSchemaRejected ? "tool_schema_rejected"
                            : questionnaireValidation is { Valid: false } ? "questionnaire_validation_rejected" : "questionnaire_decode_rejected",
                        outcome.FinalState.ToString(),
                        events.Any(payload => payload is ToolCallStarted or ToolCallSucceeded),
                        safeArgumentShape: DescribeArgumentShape(call.ArgumentsJson),
                        toolSchemaValid: toolSchemaValid,
                        questionnairePreparationValid: schemaAndPrepareValid,
                        questionnaireErrorCodes: questionnaireErrorCodes);
                // Direct tool execution would falsely claim the Host published an interaction.
                // The Ask policy therefore stops before ExecuteAsync; this records a checked
                // model proposal but correctly leaves the interactive Host action non-executable.
                return CaseEvidence.Blocked(modelName, modelId, acceptanceCase.Id, true,
                    schemaAndPrepareValid, argsHash, "interactive_host_client_required; pipeline_not_approved",
                    toolSchemaValid: toolSchemaValid,
                    questionnairePreparationValid: schemaAndPrepareValid,
                    questionnaireErrorCodes: questionnaireErrorCodes);
            }
            if (acceptanceCase.Id == "core.agents.mailbox.receive")
            {
                var refusedWithoutBinding = events.OfType<ToolCallFailed>().Any(failure =>
                    failure.ErrorCode == ToolErrorCode.CapabilityRefused);
                return refusedWithoutBinding && schemaAndPrepareValid
                    ? CaseEvidence.BlockedAfterExecution(modelName, modelId, acceptanceCase.Id,
                        argsHash, "host_receive_binding_missing; no_message_or_acknowledgement_created")
                    : CaseEvidence.Failed(modelName, modelId, acceptanceCase.Id, true,
                        schemaAndPrepareValid, argsHash, "expected_production_capability_refusal_not_observed",
                        outcome.FinalState.ToString(), events.Any(payload => payload is ToolCallStarted or ToolCallSucceeded));
            }
            if (!schemaAndPrepareValid)
                return CaseEvidence.Failed(modelName, modelId, acceptanceCase.Id, true, false,
                    argsHash, "tool_schema_or_prepare_rejected", outcome.FinalState.ToString(),
                    pipelineExecuted: events.Any(payload => payload is ToolCallStarted or ToolCallSucceeded));
            if (!outcome.Succeeded)
            {
                if (outcome.Summary?.Contains("WEAK_SANDBOX_CONSENT_REQUIRED", StringComparison.Ordinal) == true)
                    return CaseEvidence.Blocked(modelName, modelId, acceptanceCase.Id, true, true,
                        argsHash, "strong_sandbox_blocked_pending_host_consent");
                return CaseEvidence.Failed(modelName, modelId, acceptanceCase.Id, true, true,
                    argsHash, "tool_pipeline_failed", outcome.FinalState.ToString(),
                    pipelineExecuted: events.Any(payload => payload is ToolCallStarted or ToolCallSucceeded));
            }

            // Every successful call is returned to the same model with its own opaque receipt.
            // The receipt is created only after execution and never appears in the task prompt.
            {
                var receipt = "RESULT-" + Guid.NewGuid().ToString("N");
                var toolResult = new ToolResultBlock(validated.ToolCallId,
                    [new TextBlock(JsonSerializer.Serialize(new { tool = acceptanceCase.Id,
                        summary = outcome.Summary, output = outcome.Preview, verificationReceipt = receipt }))], IsError: false);
                var followUp = new ModelRequest(request.Model,
                    [new ModelMessage(MessageRole.User, [new TextBlock(acceptanceCase.Prompt)]),
                        new ModelMessage(MessageRole.Assistant, response.Content),
                        new ModelMessage(MessageRole.Tool, [toolResult])],
                    "Explain what the returned tool result actually says and include its exact verificationReceipt. "
                    + "Distinguish advisory proposals from applied actions. Do not invent other actions or call more tools.",
                    Array.Empty<ToolDefinition>(), ToolChoice.None(), null, null, null, response.State);
                ModelResponse roundTrip;
                try
                {
                    Progress("returning_tool_result");
                    roundTrip = await GetResponseAsync(provider, followUp, cancellationToken, onGenerationInvocation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    return CaseEvidence.ProviderFailure(modelName, modelId, acceptanceCase.Id,
                        SafeProviderFailureCode(exception));
                }
                var finalText = string.Concat(roundTrip.Content.OfType<TextBlock>().Select(block => block.Text));
                var effect = VerifyEffect(acceptanceCase, call.ArgumentsJson, outcome, workspace,
                    artifactStore, referencedArtifacts);
                var roundTripVerified = finalText.Contains(receipt, StringComparison.Ordinal)
                    && roundTrip.Content.All(block => block is not ToolCallBlock);
                var consentRecorded = events.OfType<InteractionResolved>().Any(item => item.OptionId == "consent_once");
                var audited = audit.Records().Any(item => item.EventName == "WeakSandboxConsentGranted");
                if (consentRecorded && !audited) throw new InvalidOperationException("fixture_consent_audit_missing");
                Progress("result_round_trip_completed");
                return CaseEvidence.Completed(modelName, modelId, acceptanceCase.Id, argsHash,
                    schemaAndPrepareValid, outcome.FinalState.ToString(), outcome.Effect.ToString(),
                    outcome.Preview, effect.Verified && roundTripVerified,
                    effect.Description + "; model_received_unique_result_receipt=" + roundTripVerified
                        + "; explicit_fixture_consent_once=" + consentRecorded + "; consent_audited=" + audited,
                    roundTripCompleted: roundTripVerified) with
                {
                    ToolEffectVerified = effect.Verified,
                    CanonicalArgumentMatches = CompareCanonicalArguments(acceptanceCase, call.ArgumentsJson),
                    ExpectedVerificationReceipt = receipt,
                    FollowUpStopReason = roundTrip.StopReason.ToString(),
                    FollowUpTextCharacters = finalText.Length,
                    FollowUpToolCalls = roundTrip.Content.OfType<ToolCallBlock>().Select(item => item.ToolName).ToArray(),
                    FollowUpOutputTokens = roundTrip.Usage.Output,
                    // This response only sees a synthetic fixture and its own ToolResult.
                    // Never capture reasoning blocks, account context or transport headers.
                    FollowUpTextPreview = finalText.Length > 1200 ? finalText[..1200] : finalText,
                };
            }
        }
        catch (OperationCanceledException)
        {
            return CaseEvidence.Blocked(modelName, modelId, acceptanceCase.Id, modelRequested, prepared, argumentsHash,
                "bounded_timeout_or_run_cancelled_at:" + stage, providerRequestFailed: true)
                with { PipelineExecuted = executed };
        }
        catch (Exception exception)
        {
            return CaseEvidence.Failed(modelName, modelId, acceptanceCase.Id, false, false,
                null, "runtime_exception_type:" + exception.GetType().Name, null);
        }
    }

    private static Verification VerifyEffect(AcceptanceCase acceptanceCase, string argumentsJson,
        ToolRuntime.Outcome outcome, string workspace, IArtifactStore artifactStore,
        HashSet<string> referencedArtifacts)
    {
        switch (acceptanceCase.Id)
        {
            case "filesystem.read": return Check(outcome.Preview?.Contains(Marker, StringComparison.Ordinal) == true,
                "read_preview_contains_fixture_marker");
            case "filesystem.list": return Check(outcome.Preview?.Contains("read-me.txt", StringComparison.Ordinal) == true
                && outcome.Preview.Contains("patch-target.txt", StringComparison.Ordinal), "listing_contains_seeded_files");
            case "filesystem.search": return Check(outcome.Preview?.Contains(Marker, StringComparison.Ordinal) == true,
                "search_matches_fixture_marker");
            case "reference.resolve": return Check(outcome.Preview?.Contains("fixture read content " + Marker,
                StringComparison.Ordinal) == true, "reference_resolves_fixture_file");
            case "filesystem.write":
                var written = File.ReadAllText(Path.Combine(workspace, "new-file.txt"));
                return Check(written == Marker + "-WRITTEN", "written_fixture_postimage_matches_exact_marker");
            case "filesystem.patch":
                var patched = File.ReadAllText(Path.Combine(workspace, "patch-target.txt"));
                return Check(patched == "after marker\nkeep-this-line\n", "patched_fixture_postimage_matches_and_preserves_unrelated_line");
            case "process.exec":
            case "shell.exec": return Check(outcome.Preview?.Contains(Marker, StringComparison.Ordinal) == true,
                "bounded_local_process_output_contains_marker");
            case "artifact.read": return Check(outcome.Preview == "fixture artifact " + Marker,
                "fixture_cas_artifact_read_matches_seeded_content");
            case "plan.propose":
                return Check(outcome.Preview is { } json && json.Contains("\"kind\":\"start\"", StringComparison.Ordinal)
                    && json.Contains("\"itemId\":\"fixture-item\"", StringComparison.Ordinal),
                    "proposal_declaration_returned; host_plan_application_not_part_of_tool_runtime");
            case "mode.propose": return Check(outcome.Summary?.Contains("mode remains unchanged", StringComparison.Ordinal) == true,
                "advisory_returned_without_mode_authority_change");
            default: return Check(false, "no_effect_verifier_for_tool");
        }
    }

    private static Verification Check(bool verified, string description) => new(verified, description);

    private static bool SafeArgumentGate(AcceptanceCase acceptanceCase, string json, string workspace)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            string? String(string name) => root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool Bool(string name, bool expected) => !root.TryGetProperty(name, out var value)
                || value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
            bool Int(string name, int expected) => !root.TryGetProperty(name, out var value)
                || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == expected;
            bool IsNullOrMissing(string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;
            bool Keys(params string[] allowed) => root.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal));
            switch (acceptanceCase.Id)
            {
                case "filesystem.read": return Keys("path") && String("path") == "read-me.txt";
                case "filesystem.list": return Keys("path", "recursive", "maxEntries") && String("path") == "."
                    && Bool("recursive", false) && Int("maxEntries", 30);
                case "filesystem.search": return Keys("pattern", "regex", "path", "glob", "maxResults", "caseSensitive")
                    && String("pattern") == Marker && (String("path") is null or ".")
                    && Bool("regex", false) && Bool("caseSensitive", true);
                case "reference.resolve": return Keys("ref") && String("ref") == "@read-me.txt";
                case "filesystem.write": return Keys("path", "content", "expectedVersion")
                    && String("path") == "new-file.txt" && String("content") == Marker + "-WRITTEN"
                    && IsNullOrMissing("expectedVersion");
                case "filesystem.patch":
                    var expectedVersion = FileVersion.VersionToken(Encoding.UTF8.GetBytes("before marker\nkeep-this-line\n"));
                    return Keys("path", "expectedVersion", "oldText", "newText")
                        && String("path") == "patch-target.txt" && String("expectedVersion") == expectedVersion
                        && String("oldText") == "before" && String("newText") == "after";
                case "process.exec":
                    var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    var executable = Path.Combine(system, "cmd.exe");
                    return OperatingSystem.IsWindows() && Keys("executable", "argv", "cwd", "timeoutSeconds", "networkRequired")
                        && String("executable") == executable && String("cwd") == "."
                        && Int("timeoutSeconds", 5) && Bool("networkRequired", false)
                        && root.TryGetProperty("argv", out var argv) && argv.ValueKind == JsonValueKind.Array
                        && argv.GetArrayLength() == 3 && argv[0].GetString() == "/d" && argv[1].GetString() == "/c"
                        && argv[2].GetString() == "echo " + Marker;
                case "shell.exec": return OperatingSystem.IsWindows() && Keys("command", "cwd", "timeoutSeconds")
                    && String("command") == "echo " + Marker && String("cwd") == "." && Int("timeoutSeconds", 5);
                case "artifact.read": return Keys("hash", "offset", "limit")
                    && String("hash") == acceptanceCase.ExpectedPrimaryValue
                    && String("hash") is { Length: 64 } hash && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                    && Int("offset", 0) && Int("limit", 256);
                case "plan.propose": return Keys("kind", "itemId", "reason") && String("kind") == "start"
                    && String("itemId") == "fixture-item"
                    && String("reason") == "acceptance fixture proposal only";
                case "mode.propose": return Keys("mode", "reason") && String("mode") == "plan"
                    && String("reason") == "acceptance fixture proposal only";
                case "user.ask": return Keys("title", "description", "questions")
                    && String("title") == "Fixture question"
                    && (!root.TryGetProperty("description", out var description)
                        || description.ValueKind == JsonValueKind.Null
                        || description.ValueKind == JsonValueKind.String
                            && SafeQuestionText(description.GetString(), 500))
                    && root.TryGetProperty("questions", out var questions)
                    && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() == 1
                    && questions[0].EnumerateObject().All(property => property.Name is "id" or "prompt" or "helpText"
                        or "kind" or "options" or "other" or "required" or "minSelections" or "maxSelections" or "maxTextLength")
                    && questions[0].GetProperty("id").GetString() == "fixture-answer"
                    && IsSafeQuestionPrompt(questions[0].GetProperty("prompt").GetString())
                    && questions[0].GetProperty("kind").GetString() == "FreeText"
                    && (!questions[0].TryGetProperty("helpText", out var helpText)
                        || helpText.ValueKind == JsonValueKind.Null
                        || helpText.ValueKind == JsonValueKind.String && SafeQuestionText(helpText.GetString(), 500))
                    && SafeOptionalQuestionText(questions[0]);
                case "core.agents.mailbox.receive": return Keys();
                default: return false;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    private static bool IsSafeQuestionPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 500
            || !prompt.Contains("marker", StringComparison.OrdinalIgnoreCase)) return false;
        return SafeQuestionText(prompt, 500);
    }

    private static bool SafeQuestionText(string? value, int maximumLength)
    {
        if (value is null || value.Length > maximumLength) return false;
        var sensitiveTerms = new[] { "secret", "password", "credential", "api key", "token", "approve", "approval", "permission", "authorize" };
        return !sensitiveTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SafeOptionalQuestionText(JsonElement question)
    {
        if (question.TryGetProperty("options", out var options))
        {
            if (options.ValueKind != JsonValueKind.Array || options.GetArrayLength() > 8) return false;
            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object
                    || option.EnumerateObject().Any(property => property.Name is not ("id" or "label" or "description"))) return false;
                foreach (var name in new[] { "label", "description" })
                    if (option.TryGetProperty(name, out var value)
                        && (value.ValueKind != JsonValueKind.String || !SafeQuestionText(value.GetString(), 500))) return false;
            }
        }
        if (question.TryGetProperty("other", out var other) && other.ValueKind != JsonValueKind.Null)
        {
            if (other.ValueKind != JsonValueKind.Object
                || other.EnumerateObject().Any(property => property.Name is not ("optionId" or "label" or "placeholder" or "textRequired" or "maxTextLength"))) return false;
            foreach (var name in new[] { "label", "placeholder" })
                if (other.TryGetProperty(name, out var value)
                    && (value.ValueKind != JsonValueKind.String || !SafeQuestionText(value.GetString(), 500))) return false;
        }
        return true;
    }

    private static string DescribeArgumentShape(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "root_type:" + root.ValueKind;
            var properties = root.EnumerateObject().Take(17).Select(property =>
            {
                var name = property.Name.Length <= 48 && property.Name.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
                    ? property.Name : "<redacted-name>";
                if (property.Name == "questions" && property.Value.ValueKind == JsonValueKind.Array
                    && property.Value.GetArrayLength() > 0 && property.Value[0].ValueKind == JsonValueKind.Object)
                {
                    var nested = string.Join(',', property.Value[0].EnumerateObject().Take(12)
                        .Select(child => (child.Name.Length <= 48 && child.Name.All(character =>
                            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
                            ? child.Name : "<redacted-name>") + ":" + child.Value.ValueKind));
                    return name + ":Array{" + nested + "}";
                }
                return name + ":" + property.Value.ValueKind;
            }).ToArray();
            return properties.Length > 16 ? string.Join(',', properties.Take(16)) + ",<more>"
                : properties.Length == 0 ? "object:empty" : string.Join(',', properties);
        }
        catch (JsonException)
        {
            return "invalid_json";
        }
    }

    // Diagnostic only, never a replacement for SafeArgumentGate or schema validation.
    // Only canonical fixture key names and equality booleans leave this method; no
    // arbitrary model-generated argument value is written into the evidence.
    private static IReadOnlyDictionary<string, bool> CompareCanonicalArguments(AcceptanceCase item, string json)
    {
        var matches = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            using var actual = JsonDocument.Parse(json);
            using var expected = JsonDocument.Parse(SelfTestArguments(item, item.ExpectedPrimaryValue));
            if (actual.RootElement.ValueKind != JsonValueKind.Object) return matches;
            foreach (var property in expected.RootElement.EnumerateObject())
                matches[property.Name] = actual.RootElement.TryGetProperty(property.Name, out var value)
                    && JsonElement.DeepEquals(property.Value, value);
        }
        catch (JsonException) { }
        return matches;
    }

    private static async Task<ModelResponse> GetResponseAsync(IModelProvider provider, ModelRequest request,
        CancellationToken cancellationToken, Action onGenerationInvocation)
    {
        ModelResponse? response = null;
        onGenerationInvocation();
        await foreach (var item in provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            if (item is ResponseCompleted completed) response = completed.Response;
        return response ?? throw new InvalidOperationException("provider_response_incomplete");
    }

    private static string SafeProviderFailureCode(Exception exception) => exception switch
    {
        ModelProviderException provider => "provider_kind:" + SafeCode(provider.Kind)
            + ";status_code:" + (provider.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"),
        ChatGptAuthException auth => "chatgpt_auth_kind:" + SafeCode(auth.Kind),
        HttpRequestException => "transport_failure",
        TimeoutException => "timeout",
        _ => "provider_exception_type:" + exception.GetType().Name,
    };

    private static string SafeCode(string value) => new(value.Where(char.IsAsciiLetterOrDigit)
        .Take(48).ToArray());

    private static IModelProvider ConnectModelProvider(string modelName, string modelId)
    {
        var paths = OmniHost.CreatePlatformPaths();
        var registry = OmniHost.LoadUserModelRegistry(paths);
        var model = registry.Model(modelId) ?? throw new InvalidOperationException("configured_model_missing");
        var descriptor = registry.Provider(model.ProviderId) ?? throw new InvalidOperationException("configured_provider_missing");
        if (modelName == "luna")
        {
            var auth = new NoRefreshSubscriptionSource(OmniHost.CreateChatGptAuth(paths));
            return OmniHost.ConnectProvider(descriptor, descriptor.BaseUrl, descriptor.Auth.SecretRef ?? "", "",
                subscription: auth);
        }
        if (descriptor.Auth.Kind is not (AuthKind.None or AuthKind.ApiKey)
            || descriptor.LocalHost?.Mode != LocalHostMode.Attach
            || descriptor.BillingMode != BillingMode.Local || !IsConfiguredQwenEndpoint(descriptor.BaseUrl))
            throw new InvalidOperationException("qwen_provider_configuration_not_safe_for_live_acceptance");
        // Intentionally no LocalModelHost/EnsureConfiguredLocalServer call: use only the
        // already-attached endpoint, with the configured provider and no credential/env overrides.
        var apiKey = descriptor.Auth.Kind == AuthKind.ApiKey ? ReadConfiguredApiKey(paths, descriptor) : null;
        if (descriptor.Auth.Kind == AuthKind.ApiKey && apiKey is null)
            throw new InvalidOperationException("qwen_configured_secret_missing");
        // Use the production adapter and Host TLS validation with a test-only client
        // deadline. The normal Host factory has a 300-second header timeout, which would
        // otherwise silently defeat the longer prefill allowance above. No TLS bypass,
        // endpoint replacement, server startup, or persisted configuration change.
        return OmniHost.ConnectProvider(descriptor, descriptor.BaseUrl, descriptor.Auth.SecretRef ?? "",
            apiKey ?? "", chatCompletionsRequestTimeout: QwenGenerationTimeout);
    }

    private static void SeedWorkspace(string workspace)
    {
        File.WriteAllText(Path.Combine(workspace, "read-me.txt"), "fixture read content " + Marker + "\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(workspace, "patch-target.txt"), "before marker\nkeep-this-line\n",
            new UTF8Encoding(false));
    }

    private static string? ReadConfiguredApiKey(OmniCore.Abstractions.IPlatformPaths paths,
        ProviderDescriptor descriptor)
    {
        if (descriptor.Auth.Kind != AuthKind.ApiKey || string.IsNullOrWhiteSpace(descriptor.Auth.SecretRef)) return null;
        var credentials = OmniHost.CreateUserCredentialStore(paths);
        return OmniHost.ResolveApiKey(credentials, descriptor.Auth.SecretRef!, environmentKey: null,
            CancellationToken.None);
    }

    private static bool IsConfiguredQwenEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "192.168.1.104"
        && uri.Port == 8080
        && uri.AbsolutePath.TrimEnd('/') == "/v1"
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void WriteEvidence(string directory, CaseEvidence evidence)
    {
        Directory.CreateDirectory(directory);
        var filename = SafeFileName(evidence.CaseId) + ".json";
        var path = Path.Combine(directory, filename);
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions.Indented), new UTF8Encoding(false));
    }

    private static string SafeFileName(string value) => new(value.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray());
}

internal sealed class NoRefreshSubscriptionSource(ChatGptSubscriptionAuthProvider auth) : ISubscriptionCredentialSource
{
    public ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = auth.Status(cancellationToken);
        if (!status.LoggedIn || status.Expired || status.ExpiresAt is not { } expiry
            || expiry - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(15))
            return ValueTask.FromException<SubscriptionCredential>(
                new InvalidOperationException("live_acceptance_credential_below_minimum_ttl"));
        // ChatGptSubscriptionAuthProvider.GetAsync can refresh implicitly near expiry; the
        // preflight and this immediate TTL guard keep it on the read-only path for this call.
        return auth.GetAsync(cancellationToken);
    }

    public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<SubscriptionCredential>(new InvalidOperationException("live_acceptance_refresh_disabled"));
}

internal sealed record AcceptanceCase(string Id, string Prompt, string PrimaryField,
    string ExpectedPrimaryValue, string Verification);

internal sealed record SelfTestCaseResult(string CaseId, string Status, bool ArgumentsAccepted,
    bool PipelineExecuted, string Detail);

internal sealed record SelfTestSummary(string Scope, string FixtureDirectory, int Cases,
    int Succeeded, int Failed, int Blocked, IReadOnlyList<SelfTestCaseResult> Results);

internal sealed record Verification(bool Verified, string Description);

internal sealed record PreflightReport(string ModelAlias, string ModelId, bool ModelConfigured,
    bool ProviderConfigured, long? ContextWindow, long? ConfiguredMaxOutputTokens,
    long? RequestMaxOutputTokens, string AuthenticationStatus, string? QwenCredentialStatus,
    DateTimeOffset? CredentialExpiresAt, IReadOnlyList<string> BuiltInToolIds,
    IReadOnlyDictionary<string, string> NotExercisedCases,
    bool Ready, IReadOnlyList<string> Blockers);

internal sealed record CaseEvidence(string ModelAlias, string ModelId, string CaseId, string Status,
    bool ModelRequestedTool, bool ArgumentsSchemaValid, string? ArgumentsSha256, bool PipelineExecuted,
    string? PipelineState, string? EffectOutcome, bool? EffectVerified, string? Verification,
    string? ResultSha256, int? ResultCharacters, string? FailureCode, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, string InvocationMode = "directed_tool_acceptance",
    bool ProviderRequestFailed = false, bool? ToolResultRoundTripVerified = null,
    IReadOnlyList<string>? OfferedToolNames = null,
    string PermissionPolicyFixture = "deny_all_except_exact_case_tool",
    string? SafeArgumentShape = null,
    bool? ToolSchemaValid = null,
    bool? QuestionnairePreparationValid = null,
    string? QuestionnaireErrorCodes = null)
{
    public bool? ToolEffectVerified { get; init; }
    public string? FollowUpStopReason { get; init; }
    public int? FollowUpTextCharacters { get; init; }
    public IReadOnlyList<string>? FollowUpToolCalls { get; init; }
    public long? FollowUpOutputTokens { get; init; }
    public string? FollowUpTextPreview { get; init; }
    public IReadOnlyDictionary<string, bool>? CanonicalArgumentMatches { get; init; }
    // Random fixture receipt, not credentials, model reasoning or user content.
    public string? ExpectedVerificationReceipt { get; init; }
    public int? RequestedToolCount { get; init; }
    public IReadOnlyList<string>? RequestedToolNames { get; init; }

    public static CaseEvidence NotExercised(string modelAlias, string modelId, string caseId, string reason) =>
        new(modelAlias, modelId, caseId, "not_exercised", false, false, null, false, null,
            null, null, reason, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "not_exercised_no_live_fixture", PermissionPolicyFixture: "none_no_generation");

    public static CaseEvidence Blocked(string modelAlias, string modelId, string caseId,
        bool modelRequested, bool schemaValid, string? argumentHash, string reason,
        string? safeArgumentShape = null, bool providerRequestFailed = false,
        bool? toolSchemaValid = null, bool? questionnairePreparationValid = null,
        string? questionnaireErrorCodes = null) =>
        new(modelAlias, modelId, caseId, "blocked", modelRequested, schemaValid, argumentHash, false,
            null, null, null, reason, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ProviderRequestFailed: providerRequestFailed, SafeArgumentShape: safeArgumentShape,
            ToolSchemaValid: toolSchemaValid, QuestionnairePreparationValid: questionnairePreparationValid,
            QuestionnaireErrorCodes: questionnaireErrorCodes);

    public static CaseEvidence BlockedAfterExecution(string modelAlias, string modelId, string caseId,
        string argumentHash, string reason) =>
        new(modelAlias, modelId, caseId, "blocked", true, true, argumentHash, true,
            "CapabilityRefused", "no_effect", false, reason, null, null, "host_receive_binding_missing",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public static CaseEvidence Failed(string modelAlias, string modelId, string caseId,
        bool modelRequested, bool schemaValid, string? argumentHash, string failureCode, string? pipelineState,
        bool pipelineExecuted = false, string? safeArgumentShape = null, bool? toolSchemaValid = null,
        bool? questionnairePreparationValid = null, string? questionnaireErrorCodes = null) =>
        new(modelAlias, modelId, caseId, "failed", modelRequested, schemaValid, argumentHash,
            pipelineExecuted, pipelineState, null, false, null, null, null, failureCode,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SafeArgumentShape: safeArgumentShape,
            ToolSchemaValid: toolSchemaValid, QuestionnairePreparationValid: questionnairePreparationValid,
            QuestionnaireErrorCodes: questionnaireErrorCodes);

    public static CaseEvidence Completed(string modelAlias, string modelId, string caseId,
        string argumentHash, bool schemaValid, string pipelineState, string effectOutcome,
        string? result, bool effectVerified, string verification, bool roundTripCompleted = false) =>
        new(modelAlias, modelId, caseId, effectVerified ? "success" : "failed", true, schemaValid,
            argumentHash, true, pipelineState, effectOutcome, effectVerified, verification,
            result is null ? null : HashResult(result), result?.Length, effectVerified ? null : "effect_verification_failed",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "directed_tool_acceptance",
            false, roundTripCompleted, [caseId]);

    public static CaseEvidence ProviderFailure(string modelAlias, string modelId, string caseId, string reason) =>
        new(modelAlias, modelId, caseId, "failed", false, false, null, false, null, null, false,
            null, null, null, reason, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "directed_tool_acceptance", ProviderRequestFailed: true);

    public static CaseEvidence Boundary(string modelAlias, string modelId, string caseId,
        bool requestedTool, bool passed, IReadOnlyList<string> offeredTools) =>
        new(modelAlias, modelId, caseId, passed ? "success" : "failed", requestedTool, false, null,
            false, null, "no_execution", passed, "auto_catalog_boundary; slash command not executed",
            null, null, passed ? null : "model_called_tool_or_command_was_offered", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "boundary_auto_catalog", false, null, offeredTools,
            "no_tool_runtime_permission_decision_no_execution");

    private static string HashResult(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed record RunSummary(string ModelAlias, string ModelId, string EvidenceDirectory,
    string FixtureDirectory, int Executed, int Succeeded, int Failed, int Blocked,
    int NotExercised, int GenerationInvocations, long? MaximumGenerationRequestAttemptsPerInvocation,
    int GenerationInvocationCeiling, long? ConfiguredContextWindow, long? ConfiguredMaxOutputTokens,
    long? RequestMaxOutputTokens, long? WorstCaseGenerationRequestAttempts,
    IReadOnlyList<CaseEvidence> Cases);
