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

    private string Text(LocalizedText text) => Localize is null ? text.Render() : Localize(text.Key, text.Args);

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
            writeLine("omni act: falta la instrucción. Uso: omni act \"instrucción\"");
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
        writeLine(locale == "en" ? "omni doctor — M2 diagnostics" : "omni doctor — diagnóstico de M2");
        writeLine((locale == "en" ? "Configuration: " : "Configuración: ") + paths.ConfigDirectory);
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
        WarnIgnoredWorkspaceConfig(workspaceConfig, locale, writeLine);
        ReportDiagnostics(workspaceConfig.Diagnostics, locale, writeLine, localize);
        writeLine(locale == "en" ? "Available models:" : "Modelos disponibles:");
        foreach (var model in registry.Models())
        {
            var provider = registry.Provider(model.ProviderId);
            writeLine("  " + model.Id + " → provider '" + model.ProviderId + "'"
                + (provider is null ? "" : " (" + provider.Family + ", " + provider.BaseUrl + ")"));
            if (provider is not null && provider.Family != ProviderFamily.OpenAiChatCompatible)
            {
                var unsupported = new ProviderFamilyNotSupportedException(provider.Family);
                var message = localize is null ? unsupported.UserMessage.Render()
                    : localize(unsupported.UserMessage.Key, unsupported.UserMessage.Args);
                writeLine("    " + message);
            }
            if (provider is not null)
            {
                writeLine("    TLS: " + OmniHost.DescribeTls(provider.BaseUrl, provider.TrustedCertificatePath));
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
        writeLine(locale == "en" ? "Wired runtime:" : "Runtime cableado:");
        writeLine("  tokenCounter=" + tokenizer.Id);
        writeLine("  scopeResolver=" + (resolver is null ? "?" : resolver.GetType().Name));
        writeLine("  credentialStore=" + credentials.GetType().Name + " (" + credentials + ")");
        writeLine("  localModelHost=" + localHost.GetType().Name + " managed=" + localHost.IsManagedRunning());
        writeLine("  artifactStore=" + artifacts.GetType().Name + " (" + artifacts + ")");
        var configured = registry.Models().Count > 0;
        writeLine(configured
            ? (locale == "en" ? "Status: model configured ✓" : "Estado: modelo configurado ✓")
            : (locale == "en" ? "Status: no model configured (run omni ask for guidance)"
                : "Estado: sin modelo configurado (ejecuta omni ask para ver la guía)"));
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
            WriteDiagnostics(ex.Diagnostics, Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? "en" : "es",
                writeLine);
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
            WriteDiagnostics(ex.Diagnostics, Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? "en" : "es",
                writeLine);
            return 1;
        }
        var locale = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? "en" : "es";
        if (!_providerDeprecationShown && loaded.DeprecationNotices is { Count: > 0 } notices)
        {
            foreach (var notice in notices) writeLine("warning: " + Text(notice));
            _providerDeprecationShown = true;
        }
        if (!_workspaceWarningShown)
        {
            WarnIgnoredWorkspaceConfig(workspaceConfig, locale, writeLine);
            _workspaceWarningShown = workspaceConfig.Ignored;
        }
        WriteDiagnostics(workspaceConfig.Diagnostics, locale, writeLine);
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
        if (providerDescription is not null && providerDescription.Family != ProviderFamily.OpenAiChatCompatible)
        {
            var unsupported = new ProviderFamilyNotSupportedException(providerDescription.Family);
            writeLine("omni " + (act ? "act" : "ask") + ": " + Text(unsupported.UserMessage));
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
                writeLine("omni " + (act ? "act" : "ask") + ": " + Text(ex.UserMessage));
                return 1;
            }
            if (key is null)
            {
                writeLine("omni " + (act ? "act" : "ask") + ": falta la credencial del provider. "
                    + "Define OMNI_QWEN_KEY una vez; se guarda cifrada en el almacén del usuario. "
                    + "No se guardan secretos en el repo (ADR-0018).");
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
                writeLine("omni " + (act ? "act" : "ask") + ": "
                    + Text(noModel?.UserMessage ?? LocalizedText.Of("models.noneConfigured")));
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
                        writeLine("omni ask: " + (start.Error is null ? "no se pudo iniciar el run" : ResolveWire(start.Error)));
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
                    writeLine("omni act: " + (ack.Error is null ? "no se pudo crear el Run Act" : ResolveWire(ack.Error)));
                    return 1;
                }
            }

            var sessionId = server.LastSessionId() ?? SessionId.New();
            var runId = server.LastRunId() ?? RunId.New();
            var laneId = server.LastLaneId() ?? LaneId.New();
            var provider = providerDescription is null
                ? OmniHost.ConnectLocalChatCompletions(baseUrl, model, secretRef, key ?? "")
                : OmniHost.ConnectProvider(providerDescription, baseUrl, secretRef, key ?? "");
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
                    writeLine("PlanApproval: " + (ack.Error ?? "no se pudo registrar la respuesta"));
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
                writeLine("omni ask: (servidor local managed activo)");
            }

            var executingAct = act || server.CurrentRunMode() == RunMode.Act;
            var artifacts = OmniHost.CreateArtifactStore(workspaceData);
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
                    writeLine("Question: " + (ack.Error ?? "no se pudo registrar la respuesta"));
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
                writeLine("[tool] " + trace.ToolName + " → " + (trace.Succeeded ? "ok" : "FALLO") + ": " + trace.Summary);
            }

            if (!string.IsNullOrEmpty(result.FinalText))
            {
                writeLine(result.FinalText);
            }

            writeLine("── " + result.StopReason + " · steps " + result.Steps
                + " · tokens " + (result.Usage.Input + result.Usage.Output));

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
                    writeLine("PlanApproval: " + (response.Error ?? "no se pudo registrar la respuesta"));
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
                writeLine("omni act: el turno no produjo respuesta.");
            }

            return act && (result.StopReason != StopReason.EndTurn || result.FinalText is null) ? 1 : 0;
        }
        catch (Exception ex)
        {
            writeLine("omni " + (act ? "act" : "ask") + ": error: " + RedactSensitive(ex.Message ?? "?")
                + " [" + ex.GetType().Name + "]");
            if (!act && ex.StackTrace is not null)
            {
                writeLine("  frames: " + ex.StackTrace.Length + " " + string.Join("|", ex.StackTrace.Take(6)));
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
                WriteToolTrace(writeLine, trace);
            if (!string.IsNullOrEmpty(result.FinalText)) writeLine(result.FinalText);
            writeLine("── " + result.StopReason + " · steps " + result.Steps
                + " · tokens " + (result.Usage.Input + result.Usage.Output));
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
                writeLine("[gate] " + gate.Key + " → " + (gate.Passed ? "ok" : "FALLO") + ": " + gate.Summary);
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
                    writeLine("AcceptanceConfirmation: " + (response.Error ?? "no se pudo registrar la respuesta"));
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

        writeLine(locale == "en"
            ? "omni act: completion gates were not satisfied within the turn limit; Run remains resumable."
            : "omni act: no se satisficieron los gates dentro del límite; el Run puede reanudarse.");
        return 1;

        static void WriteToolTrace(Action<string> output, ExplorerTurn.ToolUseTrace trace) => output(
            "[tool] " + trace.ToolName + " → " + (trace.Succeeded ? "ok" : "FALLO") + ": " + trace.Summary);
    }

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
        writeLine(locale == "en" ? "Choose an option (default: deny): " : "Elige una opción (por defecto: denegar): ");
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
        writeLine("[4] " + (locale == "en" ? "Reject" : "Rechazar"));
        writeLine(locale == "en" ? "Choose an option: " : "Elige una opción: ");
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

    private static string InputRequiredJson(InteractionId interactionId, string kind = "PlanApproval")
    {
        var outcome = new InputRequiredOutcome(interactionId.ToString(), kind);
        return "{\"outcome\":\"InputRequired\","
            + JsonObj.Field("interactionId", outcome.InteractionId) + ","
            + JsonObj.Field("kind", outcome.Kind) + "}";
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

    private static void WarnIgnoredWorkspaceConfig(WorkspaceConfigurationResult result, string locale,
        Action<string> writeLine)
    {
        if (!result.Ignored) return;
        writeLine(locale == "en"
            ? "warning: workspace is untrusted; .omnicore/ is ignored. Run 'omni trust' to trust this workspace."
            : "aviso: el workspace no es confiable; se ignora .omnicore/. Ejecuta 'omni trust' para confiar en él.");
    }

    private void WriteDiagnostics(IReadOnlyList<ConfigDiagnostic> diagnostics, string locale, Action<string> writeLine)
    {
        foreach (var diagnostic in diagnostics)
        {
            var message = Localize is null ? diagnostic.Message.Render() : Text(diagnostic.Message);
            var location = diagnostic.File + (diagnostic.Line is null ? "" : ":" + diagnostic.Line
                + ":" + diagnostic.Column);
            writeLine((locale == "en" ? "Configuration error " : "Error de configuración ")
                + location + " (" + diagnostic.KeyPath + "): " + message);
        }
    }

    private static void ReportProviderNotices(LoadedUserConfiguration loaded, string locale,
        Action<string> writeLine, Func<string, IReadOnlyDictionary<string, string>, string>? localize)
    {
        if (loaded.DeprecationNotices is null) return;
        foreach (var notice in loaded.DeprecationNotices)
        {
            var message = localize is null ? notice.Render() : localize(notice.Key, notice.Args);
            writeLine((locale == "en" ? "warning: " : "aviso: ") + message);
        }
    }

    private static void ReportDiagnostics(IReadOnlyList<ConfigDiagnostic> diagnostics, string locale,
        Action<string> writeLine, Func<string, IReadOnlyDictionary<string, string>, string>? localize)
    {
        foreach (var diagnostic in diagnostics)
        {
            var message = localize is null ? diagnostic.Message.Render()
                : localize(diagnostic.Message.Key, diagnostic.Message.Args);
            var location = diagnostic.File + (diagnostic.Line is null ? "" : ":" + diagnostic.Line
                + ":" + diagnostic.Column);
            writeLine((locale == "en" ? "Configuration error " : "Error de configuración ")
                + location + " (" + diagnostic.KeyPath + "): " + message);
        }
    }
}
