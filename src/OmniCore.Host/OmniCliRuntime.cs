namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;
using System.Text.Json;

/// <summary>Fachada tipada del runtime usada por el CLI; oculta composición y tipos internos.</summary>
public sealed class OmniCliRuntime
{
    private readonly string _workspaceRoot;
    private OmniServer? _server;
    private (IModelProvider Provider, ModelPricing? Pricing, string BaseUrl, IArtifactStore Artifacts)? _usageContext;
    private bool _workspaceWarningShown;
    private bool _providerDeprecationShown;

    private OmniCliRuntime(string workspaceRoot) => _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public static OmniCliRuntime Create(string workspaceRoot) => new(workspaceRoot);

    /// <summary>
    /// Resolución de texto localizado que aporta el cliente (clave + argumentos → frase, ADR-0040).
    /// El Host no traduce: sin resolver, escribe el formato estable <c>clave(arg=valor)</c>.
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>, string>? Localize { get; set; }

    /// <summary>Entrada plain provista por OmniCore.Cli; null cuando no existe un TTY interactivo.</summary>
    public Func<QuestionnairePromptDto, QuestionnaireResponseDto?>? QuestionnaireInput { get; set; }

    private string Text(LocalizedText text) => Resolve(text, Localize);

    private static LocalizedText Localized(string key, params (string Name, string Value)[] args) =>
        new(key, args.ToDictionary(argument => argument.Name, argument => argument.Value, StringComparer.Ordinal));

    private static string Resolve(LocalizedText text,
        Func<string, IReadOnlyDictionary<string, string>, string>? localize) =>
        localize is null ? text.Render() : localize(text.Key, text.Args);

    private string ResolveWire(string text)
    {
        var open = text.IndexOf('(');
        var key = open < 0 ? text : text[..open];
        if (Localize is null) return text;
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (open >= 0 && text.EndsWith(')'))
        {
            foreach (var part in text[(open + 1)..^1].Split(','))
            {
                var equals = part.IndexOf('=');
                if (equals > 0) args[part[..equals]] = part[(equals + 1)..];
            }
        }
        return Localize(key, args);
    }

    /// <summary>Abre el cliente in-process del workspace sin exponer OmniServer al consumidor.</summary>
    public IOmniClient Connect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Server();
    }

    /// <summary>Ejecuta un turno Explorer real, componiendo provider, políticas y persistencia.</summary>
    public Task<int> AskAsync(string question, Action<string> writeLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        return RunTurnAsync(question, act: false, writeLine, cancellationToken);
    }

    /// <summary>Ejecuta un Run Act real bajo las políticas del modelo y del Permission Engine.</summary>
    public Task<int> ActAsync(string objective, Action<string> writeLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        if (string.IsNullOrWhiteSpace(objective))
        {
            writeLine(Text(LocalizedText.Of("cli.runtime.act.usage")));
            return System.Threading.Tasks.Task.FromResult(2);
        }

        return RunTurnAsync(objective, act: true, writeLine, cancellationToken);
    }

    /// <summary>Diagnóstico de componentes de runtime en DTOs de texto para el CLI.</summary>
    public static int Doctor(string locale, Action<string> writeLine,
        Func<string, IReadOnlyDictionary<string, string>, string>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        var paths = OmniHost.CreatePlatformPaths();
        LoadedUserConfiguration loaded;
        try { loaded = OmniHost.LoadUserConfiguration(paths); }
        catch (ConfigValidationException ex)
        {
            ReportDiagnostics(ex.Diagnostics, locale, writeLine, localize);
            return 1;
        }
        var registry = loaded.Registry;
        ReportProviderNotices(loaded, locale, writeLine, localize);
        writeLine(Resolve(LocalizedText.Of("doctor.heading"), localize));
        writeLine(Resolve(Localized("doctor.config", ("path", paths.ConfigDirectory)), localize));
        var trust = new WorkspaceTrustStore(paths).IsTrusted(Directory.GetCurrentDirectory());
        WorkspaceConfigurationResult workspaceConfig;
        try
        {
            workspaceConfig = WorkspaceConfigurationLoader.Load(Directory.GetCurrentDirectory(), trust,
                alias => registry.Model(alias) is not null);
        }
        catch (ConfigValidationException ex)
        {
            ReportDiagnostics(ex.Diagnostics, locale, writeLine, localize);
            return 1;
        }
        WarnIgnoredWorkspaceConfig(workspaceConfig, writeLine, localize);
        ReportDiagnostics(workspaceConfig.Diagnostics, locale, writeLine, localize);
        writeLine(Resolve(LocalizedText.Of("doctor.models"), localize));
        foreach (var model in registry.Models())
        {
            var provider = registry.Provider(model.ProviderId);
            writeLine(Resolve(Localized("doctor.model", ("name", model.Id), ("provider", model.ProviderId)), localize)
                + (provider is null ? "" : Resolve(Localized("doctor.model.details", ("family", provider.Family.ToString()),
                    ("baseUrl", provider.BaseUrl)), localize)));
            if (provider is not null && !OmniHost.IsProviderFamilySupported(provider.Family))
            {
                var unsupported = new ProviderFamilyNotSupportedException(provider.Family);
                writeLine("    " + Resolve(unsupported.UserMessage, localize));
            }
            if (provider is not null && string.Equals(provider.Profile, "codex", StringComparison.Ordinal))
            {
                var session = OmniHost.CreateChatGptAuth(paths).Status(CancellationToken.None);
                writeLine(Resolve(!session.LoggedIn ? LocalizedText.Of("doctor.chatgpt.none")
                    : session.Expired ? Localized("doctor.chatgpt.expired", ("account", session.AccountIdMasked ?? ""))
                    : Localized("doctor.chatgpt.active", ("account", session.AccountIdMasked ?? ""),
                        ("expires", session.ExpiresAt?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "")), localize));
            }
            if (provider is not null)
            {
                writeLine(Resolve(Localized("doctor.tls",
                    ("description", OmniHost.DescribeTls(provider.BaseUrl, provider.TrustedCertificatePath))), localize));
            }
        }

        var defaultModel = registry.Models().FirstOrDefault();
        var defaultProvider = defaultModel is null ? null : registry.Provider(defaultModel.ProviderId);
        var tokenizer = OmniHost.CreateTokenCounter(defaultProvider,
            defaultProvider is null ? null : loaded.ProviderKind(defaultProvider.Id), null);
        var resolver = OmniHost.CreateScopeResolver();
        var credentials = OmniHost.CreateUserCredentialStore(paths);
        var localHost = OmniHost.CreateLocalModelHost();
        var artifacts = OmniHost.CreateArtifactStore(OmniHost.WorkspaceDataDirectory(paths, "."));
        writeLine(Resolve(LocalizedText.Of("doctor.runtime"), localize));
        writeLine(Resolve(Localized("doctor.runtime.tokenCounter", ("value", tokenizer.Id.ToString())), localize));
        writeLine(Resolve(Localized("doctor.runtime.scopeResolver",
            ("value", resolver is null ? "?" : resolver.GetType().Name)), localize));
        writeLine(Resolve(Localized("doctor.runtime.credentialStore", ("type", credentials.GetType().Name),
            ("value", credentials.ToString() ?? "")), localize));
        writeLine(Resolve(Localized("doctor.runtime.localModelHost", ("type", localHost.GetType().Name),
            ("managed", localHost.IsManagedRunning().ToString())), localize));
        writeLine(Resolve(Localized("doctor.runtime.artifactStore", ("type", artifacts.GetType().Name),
            ("value", artifacts.ToString() ?? "")), localize));
        var configured = registry.Models().Count > 0;
        writeLine(Resolve(LocalizedText.Of(configured ? "doctor.status.configured" : "doctor.status.unconfigured"),
            localize));
        return configured ? 0 : 1;
    }

    /// <summary>Boundary nuevo por Run; su registro de lecturas canoniza contra la raíz del workspace.</summary>
    internal static ModelCapabilityBoundary CreateBoundary(EffectiveModelPolicy policy, string workspaceRoot) =>
        new(policy, ModelCapabilityBoundary.CoreTools, new FileReadRegistry(workspaceRoot));

    public static string RedactSensitive(string value) =>
        SecretRedactor.Shared.Redact(new PiiRedactor().Redact(value));

    private async Task<int> RunTurnAsync(string prompt, bool act, Action<string> writeLine,
        CancellationToken cancellationToken)
    {
        var paths = OmniHost.CreatePlatformPaths();
        var workspaceData = OmniHost.WorkspaceDataDirectory(paths, _workspaceRoot);
        LoadedUserConfiguration loaded;
        try { loaded = OmniHost.LoadUserConfiguration(paths); }
        catch (ConfigValidationException ex)
        {
            WriteDiagnostics(ex.Diagnostics, writeLine);
            return 1;
        }
        var registry = loaded.Registry;
        var trust = new WorkspaceTrustStore(paths).IsTrusted(_workspaceRoot);
        WorkspaceConfigurationResult workspaceConfig;
        try
        {
            workspaceConfig = WorkspaceConfigurationLoader.Load(_workspaceRoot, trust,
                alias => registry.Model(alias) is not null);
        }
        catch (ConfigValidationException ex)
        {
            WriteDiagnostics(ex.Diagnostics, writeLine);
            return 1;
        }
        var locale = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? "en" : "es";
        if (!_providerDeprecationShown && loaded.DeprecationNotices is { Count: > 0 } notices)
        {
            foreach (var notice in notices)
                writeLine(Text(Localized("cli.runtime.warning", ("message", Text(notice)))));
            _providerDeprecationShown = true;
        }
        if (!_workspaceWarningShown)
        {
            WarnIgnoredWorkspaceConfig(workspaceConfig, writeLine, Localize);
            _workspaceWarningShown = workspaceConfig.Ignored;
        }
        WriteDiagnostics(workspaceConfig.Diagnostics, writeLine);
        ModelDefinition? modelDefinition = null;
        NoModelConfiguredException? noModel = null;
        try
        {
            modelDefinition = registry.ResolveDefault();
        }
        catch (NoModelConfiguredException ex)
        {
            noModel = ex;
        }

        if (workspaceConfig.Settings?.DefaultModel is not null)
            modelDefinition = registry.Model(workspaceConfig.Settings.DefaultModel);
        var providerDescription = modelDefinition is null ? null : registry.Provider(modelDefinition.ProviderId);
        var secretRef = providerDescription?.Auth.SecretRef ?? "qwen";
        var model = Environment.GetEnvironmentVariable("OMNI_MODEL") ?? workspaceConfig.Settings?.DefaultModel
            ?? modelDefinition?.Id;
        var baseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL") ?? providerDescription?.BaseUrl
            ?? "http://127.0.0.1:8080/v1";
        if (providerDescription is not null && !OmniHost.IsProviderFamilySupported(providerDescription.Family))
        {
            var unsupported = new ProviderFamilyNotSupportedException(providerDescription.Family);
            writeLine(Text(Localized("cli.runtime.command.error", ("command", act ? "act" : "ask"),
                ("message", Text(unsupported.UserMessage)))));
            return 1;
        }
        string? key = null;
        if (providerDescription?.Auth.Kind == AuthKind.ApiKey)
        {
            try
            {
                var credentials = OmniHost.CreateUserCredentialStore(paths);
                key = OmniHost.ResolveApiKey(credentials, secretRef,
                    Environment.GetEnvironmentVariable("OMNI_QWEN_KEY"), cancellationToken);
            }
            catch (SecretValueTooShortException ex)
            {
                writeLine(Text(Localized("cli.runtime.command.error", ("command", act ? "act" : "ask"),
                    ("message", Text(ex.UserMessage)))));
                return 1;
            }
            if (key is null)
            {
                writeLine(Text(Localized("cli.runtime.credential.missing",
                    ("command", act ? "act" : "ask"))));
                return 1;
            }
        }

        var tokenCounter = OmniHost.CreateTokenCounter(providerDescription,
            providerDescription is null ? null : loaded.ProviderKind(providerDescription.Id), key,
            baseUrlOverride: baseUrl);
        try
        {
            if (model is null)
            {
                writeLine(Text(Localized("cli.runtime.command.error", ("command", act ? "act" : "ask"),
                    ("message", Text(noModel?.UserMessage ?? LocalizedText.Of("models.noneConfigured"))))));
                return 1;
            }

            var server = Server(workspaceData);
            string workingState = "";
            if (!act)
            {
                workingState = ReadWorkingState(server, cancellationToken);
                if (workingState.Length == 0)
                {
                    var start = server.Send(WireEnvelope.Command(Ids.NewV7(), "{"
                        + JsonObj.Field("cmd", "explore.start") + ","
                        + JsonObj.Field("objective", prompt) + "}"), cancellationToken);
                    if (start.Status != "ok")
                    {
                        writeLine(Text(Localized("cli.runtime.run.startFailed", ("error",
                            start.Error is null ? Text(LocalizedText.Of("cli.runtime.run.startFailed.fallback"))
                                : ResolveWire(start.Error)))));
                        return 1;
                    }

                    workingState = ReadWorkingState(server, cancellationToken);
                }
            }
            else
            {
                var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
                    + JsonObj.Field("objective", prompt) + "}"), cancellationToken);
                if (ack.Status != "ok")
                {
                    writeLine(Text(Localized("cli.runtime.run.actCreateFailed", ("error",
                        ack.Error is null ? Text(LocalizedText.Of("cli.runtime.run.actCreateFailed.fallback"))
                            : ResolveWire(ack.Error)))));
                    return 1;
                }
            }

            var sessionId = server.LastSessionId() ?? SessionId.New();
            var runId = server.LastRunId() ?? RunId.New();
            var laneId = server.LastLaneId() ?? LaneId.New();
            IModelProvider provider = providerDescription is null
                ? OmniHost.ConnectLocalChatCompletions(baseUrl, model, secretRef, key ?? "")
                : OmniHost.ConnectProvider(providerDescription, baseUrl, secretRef, key ?? "",
                    string.Equals(providerDescription.Profile, "codex", StringComparison.Ordinal)
                        ? OmniHost.CreateChatGptAuth(paths) : null);
            var usableContext = modelDefinition is not null && modelDefinition.RecommendedUsableContext > 0
                ? modelDefinition.RecommendedUsableContext
                : modelDefinition is not null && modelDefinition.ContextWindow > 0
                    ? modelDefinition.ContextWindow : 8192;
            var effectiveProfile = new ModelProfileResolver().Resolve(
                modelDefinition ?? new ModelDefinition(model, "local", usableContext, usableContext, 2048),
                providerDescription);
            var harness = new HarnessPolicyResolver().Resolve(effectiveProfile);
            var harnessValue = string.Join("|", harness.ToolCallFormat, harness.ToolMode,
                harness.MaxVisibleTools, harness.GuidanceLevel, harness.RepairAttempts,
                harness.PlanControl, harness.StallThresholdTurns, harness.ContextManagement.ExternalizeAboveCharacters,
                harness.ContextManagement.CompressBodyCharacters, harness.ContextManagement.RecentTailItems,
                harness.ContextManagement.CompactAfterItems, harness.ContextManagement.MaxCheckpointCharacters);
            var harnessHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(harnessValue)));
            var policyService = OmniHost.CreateModelPolicyService(
                act ? null : Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR"));
            var modelKey = ModelPolicyKey.For(modelDefinition?.ProviderId ?? "local", model);
            EffectiveModelPolicy effectivePolicy;
            try
            {
                effectivePolicy = policyService.Effective(modelKey, harness, cancellationToken);
            }
            catch (Exception)
            {
                effectivePolicy = EffectiveModelPolicy.Resolve(modelKey, null, harness);
            }

            // Un boundary (y su registro de lecturas) por Run, canonizado contra la raíz del workspace.
            var boundary = CreateBoundary(effectivePolicy, _workspaceRoot);
            if (act && effectivePolicy.IsFallback)
                writeLine(Text(LocalizedText.Of("coder.policy.observeOnly")));
            if (!act && server.RequestPlanApprovalIfNeeded() is { } pendingApproval)
            {
                var selected = ReadPlanApprovalOption(writeLine, locale);
                if (selected is null)
                {
                    writeLine(InputRequiredJson(pendingApproval));
                    return 3;
                }
                var ack = server.RespondToInteraction(pendingApproval, selected);
                if (ack.Status != "ok")
                {
                    writeLine(Text(Localized("cli.runtime.interaction.error", ("kind", "PlanApproval"),
                        ("error", ack.Error ?? Text(LocalizedText.Of("cli.runtime.interaction.fallback"))))));
                    return 1;
                }
                if (selected is "approve_only" or "reject") return 0;
            }
            var fingerprint = new ExecutionFingerprint(model, harnessHash, "core-tools-1", tokenCounter.Id.Value,
                "none", act ? "M3" : "M2", effectivePolicy.Fingerprint(), tokenCounter.Id.Value);
            var selection = new ModelSelection(new ModelIdValue(model), usableContext, ToolMode.Direct, null);
            var localHost = OmniHost.CreateLocalModelHost();
            if (!act && localHost.IsManagedRunning())
            {
                writeLine(Text(LocalizedText.Of("cli.runtime.managedHost.active")));
            }

            var executingAct = act || server.CurrentRunMode() == RunMode.Act;
            var artifacts = OmniHost.CreateArtifactStore(workspaceData);
            _usageContext = (provider, loaded.Pricing(model), baseUrl, artifacts);
            var artifactReadTool = CreateArtifactReadTool(server, artifacts);
            var hostTools = executingAct
                ? OmniHost.CreateActTools(artifactReadTool: artifactReadTool)
                : OmniHost.CreateExplorerTools(artifactReadTool);
            server.ConfigureToolDiagnostics(hostTools.Catalog(), boundary,
                executingAct ? RunMode.Act : RunMode.Plan);
            var workspaceRoot = _workspaceRoot;
            var restrictions = workspaceConfig.Settings?.PermissionRestrictions;
            var audit = new FileAuditSink(paths.DataDirectory);
            var interactive = !Console.IsInputRedirected;
            var interactionResponder = CreateInteractionResponder(writeLine, locale);
            var executor = executingAct
                ? OmniHost.CreateActExecutor(hostTools.Catalog(), workspaceRoot, boundary, restrictions, runId,
                    audit, interactionResponder, interactive)
                : OmniHost.CreateExplorerExecutor(hostTools.Catalog(), workspaceRoot, boundary, restrictions, runId);
            var contributors = executingAct
                ? Array.Empty<IContextContributor>()
                : new IContextContributor[] { new WorkingStateContributor(workingState) };
            var materializer = new ContextMaterializer(tokenCounter, contributors);
            var questionnaireService = new QuestionnaireInteractionService(server.AcquireStore(),
                server.AcquireCodecs(), artifacts);
            QuestionnaireAskOutcome? QuestionnaireResponder(InteractionId interactionId, QuestionnaireSchema schema)
            {
                var proposed = QuestionnaireInput?.Invoke(ToQuestionnairePromptDto(schema));
                if (proposed is null) return null;
                var answers = proposed.Answers.Select(a => new QuestionAnswer(a.QuestionId,
                    a.SelectedOptionIds, a.Text, a.OtherText)).ToArray();
                var ack = server.RespondToQuestionnaire(interactionId, answers, proposed.Cancelled);
                if (ack.Status != "ok")
                {
                    writeLine(Text(Localized("cli.runtime.interaction.error", ("kind", "Question"),
                        ("error", ack.Error ?? Text(LocalizedText.Of("cli.runtime.interaction.fallback"))))));
                    return null;
                }
                return questionnaireService.ResolvedOutcome(sessionId, interactionId);
            }
            var turn = new ExplorerTurn((request, token) => provider.Complete(request, token), executor,
                hostTools.Catalog(), materializer, fingerprint, selection, server.AcquireStore(),
                server.AcquireCodecs(), artifacts, audit, new RedactionPolicy(), harness, boundary,
                loaded.Pricing(model), providerDescription?.Auth.Kind == AuthKind.ApiKey,
                questionnaires: questionnaireService, questionnaireResponder: QuestionnaireResponder,
                metaModelProvider: provider);
            var instruction = executingAct
                ? "You are executing the approved plan in the current workspace. Use the available tools under effective policy. Never invent reads or version tokens; read before patching."
                : "You are helping explain an engineering workspace. Use available read-only tools when helpful and distinguish observed facts from inference.";
            if (questionnaireService.Pending(sessionId).FirstOrDefault() is { } pendingQuestion)
            {
                var pendingSchema = questionnaireService.SchemaFor(
                    new EventStream(server.AcquireStore(), server.AcquireCodecs(), sessionId),
                    pendingQuestion.InteractionId);
                if (!interactive || pendingSchema is null
                    || QuestionnaireResponder(pendingQuestion.InteractionId, pendingSchema!) is null)
                {
                    writeLine(InputRequiredJson(pendingQuestion.InteractionId, "Question"));
                    return 3;
                }
            }
            if (act)
                return RunActLoop(turn, writeLine, prompt, instruction, sessionId, runId, laneId, workingState,
                    workspaceConfig.Settings?.Gates, restrictions, server, artifacts, audit,
                    interactionResponder, interactive, locale, cancellationToken, server.ConsumePromptOrigin());

            var result = turn.Ask(prompt, instruction, sessionId, runId, laneId, workingState, cancellationToken,
                server.ConsumePromptOrigin());
            if (result.StopReason == StopReason.InputRequired && result.PendingInteractionId is { } questionId)
            {
                writeLine(InputRequiredJson(questionId, "Question"));
                return 3;
            }
            foreach (ExplorerTurn.ToolUseTrace trace in result.ToolCalls)
            {
                writeLine(FormatToolTrace(trace));
            }

            if (!string.IsNullOrEmpty(result.FinalText))
            {
                writeLine(result.FinalText);
            }

            writeLine(Text(Localized("cli.runtime.turn.summary", ("reason", result.StopReason.ToString()),
                ("steps", result.Steps.ToString()),
                ("tokens", (result.Usage.Input + result.Usage.Output).ToString()))));

            if (!executingAct && server.RequestPlanApprovalIfNeeded() is { } approvalId)
            {
                if (Console.IsInputRedirected)
                {
                    writeLine(InputRequiredJson(approvalId));
                    return 3;
                }

                var optionId = ReadPlanApprovalOption(writeLine, locale);
                if (optionId is null)
                {
                    writeLine(InputRequiredJson(approvalId));
                    return 3;
                }
                var response = server.RespondToInteraction(approvalId, optionId);
                if (response.Status != "ok")
                {
                    writeLine(Text(Localized("cli.runtime.interaction.error", ("kind", "PlanApproval"),
                        ("error", response.Error ?? Text(LocalizedText.Of("cli.runtime.interaction.fallback"))))));
                    return 1;
                }
                if (optionId == "approve_execute")
                {
                    var actTools = OmniHost.CreateActTools(artifactReadTool: CreateArtifactReadTool(server, artifacts));
                    var actExecutor = OmniHost.CreateActExecutor(actTools.Catalog(), _workspaceRoot,
                        boundary, restrictions, runId, audit, interactionResponder, interactive);
                    var actTurn = new ExplorerTurn((request, token) => provider.Complete(request, token),
                        actExecutor, actTools.Catalog(), materializer, fingerprint, selection,
                        server.AcquireStore(), server.AcquireCodecs(), artifacts, audit,
                        new RedactionPolicy(), harness, boundary, loaded.Pricing(model),
                        providerDescription?.Auth.Kind == AuthKind.ApiKey,
                        questionnaires: questionnaireService, questionnaireResponder: QuestionnaireResponder,
                 metaModelProvider: provider);
                    var approvedState = ReadWorkingState(server, cancellationToken);
                    return RunActLoop(actTurn, writeLine, "Execute the approved plan for: " + prompt,
                        "You are executing the approved plan in the same Run. Use available tools safely and report verified results.",
                        sessionId, runId, laneId, approvedState, workspaceConfig.Settings?.Gates, restrictions,
                        server, artifacts, audit, interactionResponder, interactive, locale, cancellationToken);
                }
            }

            if (act && result.FinalText is null)
            {
                writeLine(Text(LocalizedText.Of("cli.runtime.act.noResponse")));
            }

            return act && (result.StopReason != StopReason.EndTurn || result.FinalText is null) ? 1 : 0;
        }
        catch (Exception ex)
        {
            writeLine(Text(Localized("cli.runtime.error", ("command", act ? "act" : "ask"),
                ("message", RedactSensitive(ex.Message ?? "?")), ("type", ex.GetType().Name))));
            if (!act && ex.StackTrace is not null)
            {
                writeLine(Text(Localized("cli.runtime.error.frames", ("count", ex.StackTrace.Length.ToString()),
                    ("frames", string.Join("|", ex.StackTrace.Take(6))))));
            }

            return 1;
        }
    }

    internal int RunActLoop(ExplorerTurn turn, Action<string> writeLine, string objective, string instruction,
        SessionId sessionId,
        RunId runId, LaneId laneId, string workingState, WorkspaceGatesYaml? gateConfiguration,
        IReadOnlyDictionary<string, string>? restrictions, OmniServer server, IArtifactStore artifacts,
        IAuditSink audit, Func<InteractionRequested, string?>? interactionResponder, bool interactive,
        string locale, CancellationToken cancellationToken, string? origin = null,
        Func<InteractionRequested, string?>? acceptanceResponder = null)
    {
        var hasExternalGates = gateConfiguration is { Build: not null } or { Test: not null };
        var hasAcceptance = gateConfiguration?.Acceptance == true;
        var hasAnyGate = hasExternalGates || hasAcceptance;
        var nextPrompt = objective;
        IReadOnlyList<ExternalCompletionGateResult>? acceptedResults = null;
        const int maxTurns = 16;

        for (var attempt = 0; attempt < maxTurns; attempt++)
        {
            var result = turn.Ask(nextPrompt, instruction, sessionId, runId, laneId, workingState,
                cancellationToken, origin);
            origin = null;
            if (result.StopReason == StopReason.InputRequired && result.PendingInteractionId is { } questionId)
            {
                writeLine(InputRequiredJson(questionId, "Question"));
                return 3;
            }
            foreach (ExplorerTurn.ToolUseTrace trace in result.ToolCalls)
                writeLine(FormatToolTrace(trace));
            if (!string.IsNullOrEmpty(result.FinalText)) writeLine(result.FinalText);
            writeLine(Text(Localized("cli.runtime.turn.summary", ("reason", result.StopReason.ToString()),
                ("steps", result.Steps.ToString()),
                ("tokens", (result.Usage.Input + result.Usage.Output).ToString()))));
            if (result.StopReason != StopReason.EndTurn || result.FinalText is null)
            {
                writeLine(Text(LocalizedText.Of("coder.completion.invalid")));
                return 1;
            }

            var events = server.AcquireStore().ReadFrom(sessionId, 1);
            var run = RunProjection.Replay(sessionId, runId, server.AcquireCodecs(), events);
            var tasks = TaskGraphProjection.Replay(server.AcquireCodecs(), events);
            var plan = PlanProjection.Replay(server.AcquireCodecs(), events);
            IReadOnlyList<ExternalCompletionGateResult> checkResults = Array.Empty<ExternalCompletionGateResult>();
            var stream = new EventStream(server.AcquireStore(), server.AcquireCodecs(), sessionId);
            var completed = new RunCoupon(run, tasks, plan).CheckCompletionAndGate(new PlanService(),
                new ProgressReconciler(), server.AcquireStore(), server.AcquireCodecs(), sessionId, stream,
                () =>
                {
                    if (acceptedResults is not null)
                    {
                        checkResults = acceptedResults;
                    }
                    else if (gateConfiguration is not null && hasAnyGate)
                    {
                        var runner = new ConfiguredCompletionGates(gateConfiguration, _workspaceRoot, runId,
                            laneId, run.RootTask, restrictions, audit, artifacts,
                            interactionResponder, interactive);
                        checkResults = runner.Run(stream, cancellationToken);
                    }
                    return checkResults;
                }, turn.MutationLedger);
            foreach (var gate in checkResults)
                writeLine(Text(Localized("cli.runtime.gate.result", ("name", gate.Key),
                    ("status", Text(LocalizedText.Of(gate.Passed ? "cli.runtime.gate.passed" : "cli.runtime.gate.failed"))),
                    ("summary", gate.Summary))));
            if (completed)
            {
                if (!hasExternalGates)
                    writeLine(Text(LocalizedText.Of("completion.gates.none")));
                return 0;
            }

            var pendingAcceptance = FindPendingInteraction(server.AcquireStore().ReadFrom(sessionId, 1),
                server.AcquireCodecs(), InteractionKind.AcceptanceConfirmation);
            if (pendingAcceptance is not null)
            {
                if (!interactive)
                {
                    writeLine(InputRequiredJson(pendingAcceptance.InteractionId, "AcceptanceConfirmation"));
                    return 3;
                }

                var selected = acceptanceResponder is null
                    ? ReadAcceptanceOption(writeLine, locale) : acceptanceResponder(pendingAcceptance);
                if (selected is null)
                {
                    writeLine(InputRequiredJson(pendingAcceptance.InteractionId, "AcceptanceConfirmation"));
                    return 3;
                }
                var response = server.RespondToInteraction(pendingAcceptance.InteractionId, selected);
                if (response.Status != "ok")
                {
                    writeLine(Text(Localized("cli.runtime.interaction.error", ("kind", "AcceptanceConfirmation"),
                        ("error", response.Error ?? Text(LocalizedText.Of("cli.runtime.interaction.fallback"))))));
                    return 1;
                }
                if (selected == "accept")
                {
                    acceptedResults = checkResults.Where(gate => gate.Key != "acceptance").ToList();
                    acceptedResults = acceptedResults.Append(new ExternalCompletionGateResult("acceptance", true,
                        "confirmado por el usuario")).ToArray();
                    var resumedEvents = server.AcquireStore().ReadFrom(sessionId, 1);
                    var resumedRun = RunProjection.Replay(sessionId, runId, server.AcquireCodecs(), resumedEvents);
                    var resumed = new RunCoupon(resumedRun, TaskGraphProjection.Replay(server.AcquireCodecs(), resumedEvents),
                        PlanProjection.Replay(server.AcquireCodecs(), resumedEvents)).CheckCompletionAndGate(
                            new PlanService(), new ProgressReconciler(), server.AcquireStore(), server.AcquireCodecs(),
                            sessionId, new EventStream(server.AcquireStore(), server.AcquireCodecs(), sessionId),
                            () => acceptedResults, turn.MutationLedger);
                    if (resumed) return 0;
                    acceptedResults = null; // cualquier trabajo posterior requiere volver a validar y aceptar
                }
                else
                {
                    acceptedResults = null;
                    nextPrompt = "The user rejected acceptance. Continue the work, address the feedback, and propose completion only when ready.";
                    continue;
                }
            }

            var rejection = server.AcquireStore().ReadFrom(sessionId, 1).Select(server.AcquireCodecs().Decode)
                .OfType<RunValidationRejected>().LastOrDefault(item => item.RunId.Equals(runId));
            var feedback = new List<string>();
            if (rejection is null)
                feedback.Add("Completion was rejected by a runtime gate.");
            else
            {
                feedback.AddRange(rejection.Missing.Select(ResolveWire));
                foreach (var artifact in rejection.OutputArtifacts ?? Array.Empty<ArtifactRef>())
                    feedback.Add("Gate output:\n" + RedactSensitive(artifacts.GetText(artifact.Hash) ?? ""));
            }
            nextPrompt = "Runtime completion validation failed. Fix the following feedback, then propose completion again:\n"
                + string.Join("\n", feedback);
        }

        writeLine(Text(LocalizedText.Of("cli.runtime.completion.turnLimit")));
        return 1;

    }

    private string FormatToolTrace(ExplorerTurn.ToolUseTrace trace) =>
        Text(LocalizedText.Of("cli.tool.activity", "tool", trace.ToolName)) + " → "
        + Text(LocalizedText.Of(trace.Succeeded ? "cli.tool.ok" : "cli.tool.failed")) + ": " + trace.Summary;

    private static InteractionRequested? FindPendingInteraction(IReadOnlyList<DomainEvent> events,
        IEventCodecRegistry codecs, InteractionKind kind)
    {
        var pending = new Dictionary<InteractionId, InteractionRequested>();
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case InteractionRequested requested when requested.Kind == kind:
                    pending[requested.InteractionId] = requested;
                    break;
                case InteractionResolved resolved:
                    pending.Remove(resolved.InteractionId);
                    break;
                case InteractionExpired expired:
                    pending.Remove(expired.InteractionId);
                    break;
            }
        }
        return pending.Values.LastOrDefault();
    }

    private string? ReadAcceptanceOption(Action<string> writeLine, string locale)
    {
        writeLine(Text(LocalizedText.Of("interaction.acceptance.title")));
        writeLine("[1] " + Text(LocalizedText.Of("interaction.acceptance.accept")));
        writeLine("[2] " + Text(LocalizedText.Of("interaction.acceptance.revise")));
        writeLine(Text(LocalizedText.Of("interaction.acceptance.choose")));
        return Console.ReadLine() switch
        {
            "1" or "accept" => "accept",
            "2" or "revise" => "revise",
            _ => null,
        };
    }

    private Func<InteractionRequested, string?> CreateInteractionResponder(Action<string> writeLine, string locale) => request =>
    {
        if (Console.IsInputRedirected) return null;
        var titleKey = request.Kind == InteractionKind.WeakSandboxConsent
            ? "interaction.weak_sandbox.title" : "interaction.permission.title";
        writeLine(Text(LocalizedText.Of(titleKey)));
        if (request.Kind == InteractionKind.WeakSandboxConsent)
            writeLine(Text(LocalizedText.Of("interaction.weak_sandbox.warning")));
        var choices = new List<(string Id, string Label)>();
        try
        {
            using var document = JsonDocument.Parse(request.OptionsJson);
            foreach (var option in document.RootElement.EnumerateArray())
            {
                var id = option.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                var key = id switch
                {
                    "consent_once" => "interaction.weak_sandbox.once",
                    "consent_run" => "interaction.weak_sandbox.run",
                    "allow_once" => "interaction.permission.allow_once",
                    "allow_run" => "interaction.permission.allow_run",
                    "allow_workspace" => "interaction.permission.allow_workspace",
                    _ => request.Kind == InteractionKind.WeakSandboxConsent
                        ? "interaction.weak_sandbox.deny" : "interaction.permission.deny",
                };
                choices.Add((id, Text(LocalizedText.Of(key))));
            }
        }
        catch (JsonException) { return request.DefaultOptionId; }
        for (var index = 0; index < choices.Count; index++)
            writeLine("[" + (index + 1) + "] " + choices[index].Label);
        writeLine(Text(LocalizedText.Of("cli.runtime.interaction.chooseDefaultDeny")));
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return request.DefaultOptionId;
        if (int.TryParse(input, out var choice) && choice >= 1 && choice <= choices.Count)
            return choices[choice - 1].Id;
        return choices.FirstOrDefault(choice => choice.Id == input).Id ?? request.DefaultOptionId;
    };

    private string? ReadPlanApprovalOption(Action<string> writeLine, string locale)
    {
        if (Console.IsInputRedirected) return null;
        writeLine(Text(LocalizedText.Of("interaction.plan_approval.title")));
        writeLine("[1] " + Text(LocalizedText.Of("interaction.plan_approval.approve_execute")));
        writeLine("[2] " + Text(LocalizedText.Of("interaction.plan_approval.approve_only")));
        writeLine("[3] " + Text(LocalizedText.Of("interaction.plan_approval.continue_planning")));
        writeLine("[4] " + Text(LocalizedText.Of("interaction.plan_approval.reject")));
        writeLine(Text(LocalizedText.Of("cli.runtime.interaction.choose")));
        return Console.ReadLine() switch
        {
            "1" or "approve_execute" => "approve_execute",
            "2" or "approve_only" => "approve_only",
            "3" or "continue_planning" => "continue_planning",
            "4" or "reject" => "reject",
            _ => null,
        };
    }

    private static QuestionnairePromptDto ToQuestionnairePromptDto(QuestionnaireSchema schema) =>
        new QuestionnairePromptDto(schema.Title, schema.Description, schema.Questions.Select(question =>
            new QuestionFieldDto(question.Id, question.Prompt, question.HelpText, question.Kind.ToString(),
                question.Options.Select(option => new QuestionOptionDto(option.Id, option.Label,
                    option.Description)).ToArray(),
                question.Other is null ? null : new OtherInputDto(question.Other.OptionId, question.Other.Label,
                    question.Other.Placeholder, question.Other.TextRequired, question.Other.MaxTextLength),
                question.Required, question.MinSelections, question.MaxSelections, question.MaxTextLength)).ToArray());

    private string InputRequiredJson(InteractionId interactionId, string kind = "PlanApproval")
    {
        var outcome = new InputRequiredOutcome(interactionId.ToString(), kind);
        var guidance = Text(LocalizedText.Of("interaction.input_required.guidance", "interactionId", outcome.InteractionId));
        return "{\"outcome\":\"InputRequired\","
            + JsonObj.Field("interactionId", outcome.InteractionId) + ","
            + JsonObj.Field("kind", outcome.Kind) + ","
            + JsonObj.Field("guidance", guidance) + "}";
    }

    private static ArtifactReadTool CreateArtifactReadTool(OmniServer server, IArtifactStore artifacts)
    {
        var authorization = new SessionArtifactReadAuthorization(server.AcquireStore(), server.LastSessionId, artifacts);
        return new ArtifactReadTool(artifacts, authorization.IsReferenced);
    }

    private OmniServer Server() => Server(OmniHost.WorkspaceDataDirectory(
        OmniHost.CreatePlatformPaths(), _workspaceRoot));

    private OmniServer Server(string workspaceData)
    {
        // Auditoría en scope User (<data>/audit/, ADR-0043 §1), separada del journal del workspace.
        _server ??= OmniHost.OpenPersistentServer(Path.Combine(workspaceData, "journal.db"),
            OmniHost.CreatePlatformPaths().DataDirectory);
        _server.ConfigureWorkspaceRoot(_workspaceRoot);
        return _server;
    }

    private static string ReadWorkingState(IOmniClient client, CancellationToken cancellationToken)
    {
        var json = client.Query("workingState", cancellationToken)?.Json ?? "{}";
        return JsonObj.Parse(json).TryGetValue("workingState", out var text) ? text ?? "" : "";
    }

    public bool IsWorkspaceTrusted() =>
        new WorkspaceTrustStore(OmniHost.CreatePlatformPaths()).IsTrusted(_workspaceRoot);

    /// <summary>Login con la cuenta de ChatGPT (navegador + loopback, o device code). Nunca imprime tokens.</summary>
    public async System.Threading.Tasks.Task<int> LoginChatGptAsync(bool deviceCode, Action<string> writeLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        var auth = OmniHost.CreateChatGptAuth(OmniHost.CreatePlatformPaths());
        try
        {
            var status = deviceCode
                ? await auth.LoginWithDeviceCodeAsync((url, code) => writeLine(Text(Localized("cli.login.device",
                    ("url", url), ("code", code)))), cancellationToken).ConfigureAwait(false)
                : await auth.LoginWithBrowserAsync(url => writeLine(Text(Localized("cli.login.browser", ("url", url)))),
                    cancellationToken).ConfigureAwait(false);
            writeLine(Text(Localized("cli.login.ok", ("account", status.AccountIdMasked ?? ""))));
            return 0;
        }
        catch (ChatGptAuthException ex)
        {
            writeLine(Text(ex.UserMessage));
            return 1;
        }
    }

    /// <summary>Borra la sesión de ChatGPT del credential store.</summary>
    public static void LogoutChatGpt() =>
        OmniHost.CreateChatGptAuth(OmniHost.CreatePlatformPaths()).Logout(CancellationToken.None);

    /// <summary>
    /// Uso de la sesión para la status line (ADR-0031 §3): tokens y costo desde el journal, cuota solo
    /// si el provider la informó. Null si todavía no hubo ningún Turn con modelo en este proceso.
    /// </summary>
    public UsageSnapshot? CurrentUsage()
    {
        if (_server is null || _usageContext is not { } context || _server.LastSessionId() is not { } session) return null;
        var (tokens, cost, complete) = SessionUsageReporter.ReadSessionTotals(_server.AcquireStore(), _server.AcquireCodecs(),
            context.Artifacts, session);
        var windows = context.Provider is IReportsRateLimits reporter ? reporter.LastRateLimits : [];
        return SessionUsageReporter.Build(tokens, cost, complete, context.Pricing?.IsComplete == true,
            OmniHost.IsPrivateHost(context.BaseUrl), windows, DateTimeOffset.UtcNow);
    }

    public void SetWorkspaceTrusted(bool trusted) =>
        new WorkspaceTrustStore(OmniHost.CreatePlatformPaths()).SetTrusted(_workspaceRoot, trusted);

    /// <summary>Configura /tools con el perfil, límite y frontera usados por el siguiente Turn.</summary>
    public void ConfigureToolDiagnostics(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = OmniHost.CreatePlatformPaths();
        var loaded = OmniHost.LoadUserConfiguration(paths);
        var registry = loaded.Registry;
        ModelDefinition? definition = null;
        try { definition = registry.ResolveDefault(); } catch (NoModelConfiguredException) { }
        var model = Environment.GetEnvironmentVariable("OMNI_MODEL") ?? definition?.Id ?? "local";
        definition ??= registry.Model(model) ?? new ModelDefinition(model, "local", 8192, 8192, 2048);
        var provider = registry.Provider(definition.ProviderId);
        var profile = new ModelProfileResolver().Resolve(definition, provider);
        var harness = new HarnessPolicyResolver().Resolve(profile);
        var key = ModelPolicyKey.For(definition.ProviderId, model);
        EffectiveModelPolicy effective;
        try
        {
            effective = OmniHost.CreateModelPolicyService(null).Effective(key, harness, cancellationToken);
        }
        catch (Exception)
        {
            effective = EffectiveModelPolicy.Resolve(key, null, harness);
        }
        var server = Server();
        var mode = server.CurrentRunMode();
        var artifacts = OmniHost.CreateArtifactStore(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), _workspaceRoot));
        var artifactReadTool = CreateArtifactReadTool(server, artifacts);
        var catalog = (mode == RunMode.Act
            ? OmniHost.CreateActTools(artifactReadTool: artifactReadTool)
            : OmniHost.CreateExplorerTools(artifactReadTool)).Catalog();
        server.ConfigureToolDiagnostics(catalog, CreateBoundary(effective, _workspaceRoot), mode);
    }

    /// <summary>Ejecuta un comando tipado de permisos para el WorkspaceId del cliente actual.</summary>
    public PermissionGrantCommandResult Permissions(PermissionGrantCommand command,
        CancellationToken cancellationToken)
    {
        var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
            ProjectIdentity.ResolvePhysicalWorkspaceRoot(_workspaceRoot)));
        var handler = new PermissionGrantCommandHandler(
            OmniHost.CreatePermissionGrantStore(_workspaceRoot), workspace, Server().LastRunId());
        return handler.Handle(command, cancellationToken);
    }

    private static void WarnIgnoredWorkspaceConfig(WorkspaceConfigurationResult result, Action<string> writeLine,
        Func<string, IReadOnlyDictionary<string, string>, string>? localize)
    {
        if (!result.Ignored) return;
        writeLine(Resolve(LocalizedText.Of("cli.runtime.workspace.untrusted"), localize));
    }

    private void WriteDiagnostics(IReadOnlyList<ConfigDiagnostic> diagnostics, Action<string> writeLine)
    {
        foreach (var diagnostic in diagnostics)
        {
            var message = Text(diagnostic.Message);
            var location = diagnostic.File + (diagnostic.Line is null ? "" : ":" + diagnostic.Line
                + ":" + diagnostic.Column);
            writeLine(Text(Localized("cli.runtime.config.error", ("location", location),
                ("key", diagnostic.KeyPath), ("message", message))));
        }
    }

    private static void ReportProviderNotices(LoadedUserConfiguration loaded, string locale,
        Action<string> writeLine, Func<string, IReadOnlyDictionary<string, string>, string>? localize)
    {
        if (loaded.DeprecationNotices is null) return;
        foreach (var notice in loaded.DeprecationNotices)
        {
            writeLine(Resolve(Localized("cli.runtime.warning", ("message", Resolve(notice, localize))), localize));
        }
    }

    private static void ReportDiagnostics(IReadOnlyList<ConfigDiagnostic> diagnostics, string locale,
        Action<string> writeLine, Func<string, IReadOnlyDictionary<string, string>, string>? localize)
    {
        foreach (var diagnostic in diagnostics)
        {
            var message = Resolve(diagnostic.Message, localize);
            var location = diagnostic.File + (diagnostic.Line is null ? "" : ":" + diagnostic.Line
                + ":" + diagnostic.Column);
            writeLine(Resolve(Localized("cli.runtime.config.error", ("location", location),
                ("key", diagnostic.KeyPath), ("message", message)), localize));
        }
    }
}
