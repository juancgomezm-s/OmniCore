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
using System.Globalization;
using System.Text.Json;

/// <summary>Fachada tipada del runtime usada por el CLI; oculta composición y tipos internos.</summary>
public sealed class OmniCliRuntime
{
    private readonly string _workspaceRoot;
    private readonly ProviderResilienceCatalog _providerCircuits = new();
    private OmniServer? _server;
    private string? _escalatedModel;
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
    /// <summary>False for GUI/TUI hosts: pending interactions are rendered by the client, never Console.ReadLine.</summary>
    public bool UseConsoleInput { get; set; } = true;

    /// <summary>A connected GUI/TUI client can receive and resolve interaction requests.
    /// Disabling console input alone does not establish this capability.</summary>
    public bool HasInteractionClient { get; set; }

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

    /// <summary>Output guidance, not a substitute for tool permissions or completion gates.</summary>
    internal static string TurnInstruction(bool executingAct) =>
        (executingAct
            ? "You are executing the approved plan in the current workspace. Use the available tools under effective policy. Never invent reads or version tokens; read before patching."
            : "You are helping explain an engineering workspace. Use available read-only tools when helpful and distinguish observed facts from inference.")
        + " Answer in the conversation. For examples, demonstrations, or rendering tests, include the requested code, Markdown tables, and explanation directly in your final response."
        + " Do not create or modify workspace files unless the user explicitly requests file changes. Do not replace a requested inline answer with links to generated files."
        + " A conversational example does not require filesystem tools or a new plan. Preserve explicit user requests to edit files and all approval and completion requirements.";

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

    internal static bool QueuePromptForOpenTurn(IEventStore store, IEventCodecRegistry codecs,
        SessionId session, RunId run, LaneId lane, string prompt, string? origin) =>
        FollowUpQueue.TryQueue(store, codecs, session, run, lane, prompt, origin);

    internal Task<int> ConversationAsync(string prompt, Action<string> writeLine, CancellationToken cancellationToken) =>
        RunTurnAsync(prompt, false, writeLine, cancellationToken, conversationOnly: true);

    private sealed record RoutingResume(SessionId Session, RunId Run, InteractionId? Interaction, string Model, ModelRoute? Route = null);

    internal bool HasEscalationForInteraction(InteractionId interaction)
    {
        var server = Server();
        if (server.LastSessionId() is not { } session || server.LastRunId() is not { } run) return false;
        var events = server.AcquireStore().ReadFrom(session, 1);
        var request = events.LastOrDefault(evt => evt.RunId == run
            && server.AcquireCodecs().Decode(evt) is InteractionRequested item
            && item.Kind == InteractionKind.ModelRouteConsent && item.InteractionId == interaction);
        if (request is null) return false;
        var escalation = events.LastOrDefault(evt => evt.RunId == run && evt.Sequence < request.Sequence
            && server.AcquireCodecs().Decode(evt) is ModelEscalationRequested item && item.RunId == run
            && item.Cause == EscalationCause.ContextLimit);
        if (escalation is null) return false;
        return !events.Any(evt => evt.RunId == run && evt.Sequence > escalation.Sequence && evt.Sequence < request.Sequence
            && server.AcquireCodecs().Decode(evt) is InteractionRequested { Kind: InteractionKind.ModelRouteConsent }
                or ModelEscalationApproved or ModelEscalationCompleted);
    }

    /// <summary>Resume only the exact durable, user-consented escalation. Never submits a new intent.</summary>
    internal async Task<int> ResumeEscalationAsync(InteractionId interaction, Action<string> writeLine,
        CancellationToken cancellationToken)
    {
        var server = Server();
        if (server.LastSessionId() is not { } session || server.LastRunId() is not { } run
            || new RunControlService(server.AcquireStore(), server.AcquireCodecs()).ActiveRun(session) != run)
            return 1;
        var loaded = OmniHost.LoadUserConfiguration(OmniHost.CreatePlatformPaths());
        var events = server.AcquireStore().ReadFrom(session, 1);
        foreach (var model in loaded.Registry.Models())
        {
            var provider = loaded.Registry.Provider(model.ProviderId);
            var grantedRoute = ModelRoutingHost.RouteFor(model, provider);
            var pending = ModelEscalationConsentReplay.FindGrantedPending(events, server.AcquireCodecs(),
                session, run, model.Id, grantedRoute, provider?.BillingMode ?? BillingMode.Unknown);
            if (pending?.InteractionId != interaction) continue;
            // Legacy/unattributed attempts cannot borrow some earlier intention from the Run.
            var originStart = events.LastOrDefault(evt => evt.RunId == run && evt.Sequence < pending.RequestSequence
                && server.AcquireCodecs().Decode(evt) is TurnStarted start
                && start.TurnId == pending.Request.TurnId && start.LaneId == pending.Request.LaneId);
            var previousStart = originStart is null ? 0 : events.Where(evt => evt.RunId == run && evt.Sequence < originStart.Sequence
                && server.AcquireCodecs().Decode(evt) is TurnStarted start && start.LaneId == pending.Request.LaneId)
                .Select(evt => evt.Sequence).DefaultIfEmpty(0).Max();
            if (originStart is null || pending.Request.LaneId != server.LastLaneId()
                || !events.Any(evt => evt.RunId == run && evt.Sequence > previousStart && evt.Sequence <= originStart.Sequence
                    && server.AcquireCodecs().Decode(evt) is UserInputReceived input
                    && input.Origin?.StartsWith("InteractionResponse(", StringComparison.Ordinal) != true))
            { writeLine("Escalation has no durable original input; resume rejected"); return 1; }
            var before = server.AcquireStore().CurrentSequence(session);
            var code = await RunTurnAsync("", server.CurrentRunMode() == RunMode.Act, writeLine,
                cancellationToken, conversationOnly: server.CurrentRunMode() == RunMode.Plan,
                routingResume: new(session, run, interaction, model.Id, grantedRoute)).ConfigureAwait(false);
            // Completed means the target was actually invoked, not merely approved or deferred.
            if (server.LastSessionId() == session && server.LastRunId() == run
                && server.AcquireStore().ReadFrom(session, before + 1).Any(evt => evt.RunId == run
                    && server.AcquireCodecs().Decode(evt) is ModelStepCompleted))
                EnsureEscalationRecorded(server.RecordModelEscalationCompleted(session,
                    new ModelEscalationCompleted(run, model.Id, pending.Request.TurnId, pending.Request.LaneId)));
            return code;
        }
        // A stale, denied or already consumed escalation is not executable.
        writeLine("No pending user-consented escalation for this interaction");
        return 1;
    }

    private async Task<int> RunTurnAsync(string prompt, bool act, Action<string> writeLine,
        CancellationToken cancellationToken, bool conversationOnly = false, RoutingResume? routingResume = null)
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
        using var policyService = OmniHost.CreateModelPolicyService(null);
        var workspaceSelectionId = ModelPolicyHost.WorkspaceSelectionId(_workspaceRoot);
        var storedSelection = policyService.CurrentSelection(workspaceSelectionId, cancellationToken);
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
        ModelRoute? selectedRoute = routingResume?.Route;
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

        // Elección explícita (escalación en curso, OMNI_MODEL o defaultModel del workspace) siempre gana;
        // si no la hay y el usuario configuró routing:, elige el router por tipo de tarea (M5).
        var explicitModel = routingResume?.Model ?? _escalatedModel ?? Environment.GetEnvironmentVariable("OMNI_MODEL")
            ?? workspaceConfig.Settings?.DefaultModel ?? storedSelection?.ModelId;
        if (explicitModel is not null)
        {
            modelDefinition = ResolveExplicitModel(loaded, explicitModel);
            if (modelDefinition is null)
            {
                writeLine("Selected model is not registered. Open Models and select a configured model.");
                return 1;
            }
        }
        else if (explicitModel is null)
        {
            try
            {
                var decision = ModelRoutingHost.Route(loaded, act ? RoutingTaskKind.Implementation : RoutingTaskKind.Exploration,
                    act, 0, candidate => HasWritePolicy(candidate, loaded, cancellationToken), _providerCircuits);
                if (decision is not null)
                {
                    modelDefinition = registry.Model(decision.Chosen.ModelId);
                    selectedRoute = decision.Chosen.Route;
                    writeLine(Text(Localized("cli.routing.chosen", ("model", decision.Chosen.ModelId))));
                }
            }
            catch (NoRouteAvailableException ex)
            {
                writeLine(Text(Localized("cli.routing.none", ("reasons",
                    string.Join(", ", ex.Rejected.Select(r => r.ModelId + " [" + r.RouteId.Value + "]: " + r.Reason))))));
                return 1;
            }
        }
        var providerDescription = modelDefinition is null ? null : registry.Provider(modelDefinition.ProviderId);
        if (providerDescription?.Profile == "codex")
        {
            try
            {
                using var catalogPolicies = ModelPolicyHost.Create();
                var catalog = await new TuiAccountHost().ListModelsAsync(cancellationToken).ConfigureAwait(false);
                var changed = catalogPolicies.ApplyChatGptCatalog(catalog, workspaceSelectionId, cancellationToken);
                if (changed is not null) writeLine(changed);
                storedSelection = policyService.CurrentSelection(workspaceSelectionId, cancellationToken);
                if (_escalatedModel is null && Environment.GetEnvironmentVariable("OMNI_MODEL") is null
                    && workspaceConfig.Settings?.DefaultModel is null && storedSelection is not null)
                    explicitModel = storedSelection.ModelId;
                if (!catalog.Any(m => m.Id == (explicitModel ?? modelDefinition?.Id)))
                {
                    writeLine("No available ChatGPT model. Refresh Models or sign in again."); return 1;
                }
                loaded = OmniHost.LoadUserConfiguration(paths); registry = loaded.Registry;
                modelDefinition = registry.Model(explicitModel ?? modelDefinition!.Id);
                providerDescription = modelDefinition is null ? null : registry.Provider(modelDefinition.ProviderId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { writeLine("ChatGPT catalog unavailable; selection preserved. Retry before sending."); return 1; }
        }
        var secretRef = providerDescription?.Auth.SecretRef ?? "qwen";
        var model = modelDefinition?.Id ?? explicitModel;
        var baseUrl = selectedRoute?.Endpoint ?? Environment.GetEnvironmentVariable("OMNI_BASE_URL") ?? providerDescription?.BaseUrl
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
            server.ConfigureNewSessionRoutingPolicy(ModelRoutingHost.InitialSessionPolicy(loaded,
                modelDefinition?.ProviderId ?? "local"));
            string workingState = "";
            if (routingResume is not null)
            {
                if (server.LastSessionId() != routingResume.Session || server.LastRunId() != routingResume.Run
                    || new RunControlService(server.AcquireStore(), server.AcquireCodecs()).ActiveRun(routingResume.Session) != routingResume.Run)
                { writeLine("Selected Session/Run changed; routing resume rejected"); return 1; }
                workingState = ReadWorkingState(server, cancellationToken);
            }
            else if (conversationOnly)
            {
                var input = server.Send(WireEnvelope.Command(Ids.NewV7(), "{"
                    + JsonObj.Field("cmd", "session.input") + "," + JsonObj.Field("text", prompt)
                    + "," + JsonObj.Field("mode", "plan") + "}"), cancellationToken);
                if (input.Status != "ok") throw new InvalidOperationException(input.Error ?? "Conversation input rejected");
                // session.input already persisted/queued the intent. Explorer reads that history;
                // submitting the same text again would duplicate input and provider context.
                prompt = "";
                workingState = ReadWorkingState(server, cancellationToken);
            }
            else if (!act)
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
            var promptOrigin = routingResume is not null ? "InteractionResponse(ModelRouteConsent)"
                : conversationOnly ? "AlreadyPersisted(ConversationInput)" : server.ConsumePromptOrigin();
            var followUp = server.QueueFollowUpPromptCommand(sessionId, runId, laneId, prompt, promptOrigin);
            if (followUp.Ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Rejected)
            {
                writeLine(followUp.Ack.Error ?? "follow-up queue rejected");
                return 1;
            }

            if (followUp.Queued)
                prompt = ""; // queued once before any CLI early-return; Ask must not enqueue it again.
            var usableContext = modelDefinition is not null && modelDefinition.RecommendedUsableContext > 0
                ? modelDefinition.RecommendedUsableContext
                : modelDefinition is not null && modelDefinition.ContextWindow > 0
                    ? modelDefinition.ContextWindow : 8192;
            var runtimeModel = modelDefinition ?? new ModelDefinition(model, "local", usableContext, usableContext, 2048);
            var route = selectedRoute ?? ModelRoutingHost.RouteFor(runtimeModel, providerDescription, baseUrl);
            if (route.ProviderId != runtimeModel.ProviderId || route.ProviderModelName != model
                || route.Protocol != (providerDescription?.Family ?? ProviderFamily.OpenAiChatCompatible)
                || route.Profile != providerDescription?.Profile)
            {
                writeLine("Selected route no longer matches configured model/provider. Select a current route before sending.");
                return 1;
            }
            if (routingResume?.Interaction is not null)
            {
                var pending = ModelEscalationConsentReplay.FindGrantedPending(server.AcquireStore().ReadFrom(sessionId, 1),
                    server.AcquireCodecs(), sessionId, runId, model, route, providerDescription?.BillingMode ?? BillingMode.Unknown);
                if (pending?.InteractionId != routingResume.Interaction)
                { writeLine("Routing consent is stale or already consumed; resume rejected"); return 1; }
                EnsureEscalationRecorded(server.RecordModelEscalationApproved(sessionId,
                    new ModelEscalationApproved(runId, model, "interaction:" + pending.InteractionId,
                        pending.Request.TurnId, pending.Request.LaneId)));
            }
            if (AuthorizeRouteForInvocation(server, sessionId, runId, route,
                    providerDescription?.BillingMode ?? BillingMode.Unknown, writeLine, locale) is { } routeCode)
                return routeCode;
            var routingPolicy = SessionRoutingAuthorization.Read(server.AcquireStore().ReadFrom(sessionId, 1),
                server.AcquireCodecs(), sessionId)!;
            var sessionCap = Math.Min(loaded.SessionCapUsd, routingPolicy.SessionSpendLimit ?? loaded.SessionCapUsd);
            IModelProvider provider = providerDescription is null
                ? OmniHost.ConnectLocalChatCompletions(baseUrl, model, secretRef, key ?? "", circuits: _providerCircuits)
                : OmniHost.ConnectProvider(providerDescription, baseUrl, secretRef, key ?? "",
                    string.Equals(providerDescription.Profile, "codex", StringComparison.Ordinal)
                        ? OmniHost.CreateChatGptAuth(paths) : null, _providerCircuits);
            var qualification = EmpiricalQualification(modelDefinition, providerDescription,
                act ? null : Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR"), cancellationToken,
                route.Endpoint);
            var effectiveProfile = new ModelProfileResolver().Resolve(
                runtimeModel,
                providerDescription,
                overrides: null,
                empiricalTraits: qualification?.Traits, route: route);
            var harness = new HarnessPolicyResolver().Resolve(effectiveProfile);
            var harnessValue = string.Join("|", harness.ToolCallFormat, harness.ToolMode,
                harness.MaxVisibleTools, harness.GuidanceLevel, harness.RepairAttempts,
                harness.PlanControl, harness.StallThresholdTurns, harness.ContextManagement.ExternalizeAboveCharacters,
                harness.ContextManagement.CompressBodyCharacters, harness.ContextManagement.RecentTailItems,
                harness.ContextManagement.CompactAfterItems, harness.ContextManagement.MaxCheckpointCharacters);
            var harnessHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(harnessValue)));
            var contextPolicyHash = ComputeContextPolicyHash(harness.ContextManagement, usableContext);
            var modelKey = ModelPolicyKey.For(modelDefinition?.ProviderId ?? "local", model);
            EffectiveModelPolicy effectivePolicy;
            try
            {
                effectivePolicy = storedSelection is not null && storedSelection.ModelId == modelDefinition?.Id
                    && storedSelection.Key.ProviderId == modelDefinition.ProviderId
                    ? policyService.EffectiveForSelection(workspaceSelectionId, harness, cancellationToken)
                    : policyService.Effective(modelKey, harness, cancellationToken);
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
            var selection = new ModelSelection(new ModelIdValue(model), usableContext, ToolMode.Direct, null, route.Id, route,
                ModelRoutingHost.OutputTokenLimit(runtimeModel, providerDescription));
            var artifacts = OmniHost.CreateArtifactStore(workspaceData);
            var fingerprint = RuntimeFingerprintFactory.Create(runtimeModel, effectiveProfile, harness,
                selection, harnessHash, contextPolicyHash, effectivePolicy.Fingerprint(), tokenCounter.Id.Value,
                provider, qualification, artifacts);
            var localHost = OmniHost.CreateLocalModelHost();
            if (!act && localHost.IsManagedRunning())
            {
                writeLine(Text(LocalizedText.Of("cli.runtime.managedHost.active")));
            }

            var executingAct = !conversationOnly && (act || server.CurrentRunMode() == RunMode.Act);
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
            var interactive = UseConsoleInput && !Console.IsInputRedirected;
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
            var turn = new ExplorerTurn((request, token) => server.Observability.Complete(sessionId, provider, request,
                modelDefinition?.ContextWindow, token), executor,
                hostTools.Catalog(), materializer, fingerprint, selection, server.AcquireStore(),
                server.AcquireCodecs(), artifacts, audit, new RedactionPolicy(), harness, boundary,
                loaded.Pricing(model), providerDescription?.BillingMode is BillingMode.MeteredCurrency or BillingMode.Unknown or BillingMode.CreditBalance,
                sessionCapUsd: sessionCap, dailyCapUsd: loaded.DailyCapUsd,
                questionnaires: questionnaireService, questionnaireResponder: QuestionnaireResponder,
                metaModelProvider: provider, modelContextCapacity: modelDefinition?.ContextWindow,
                recordEffectiveFingerprint: true);
            var instruction = TurnInstruction(executingAct);
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
                    interactionResponder, interactive, locale, cancellationToken, promptOrigin);

            var askExecution = server.ExecuteExplorerTurn(sessionId, runId,
                token => turn.Ask(prompt, instruction, sessionId, runId, laneId, workingState, token,
                    promptOrigin), cancellationToken);
            if (askExecution.Result is null
                || askExecution.Ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted)
            {
                throw new InvalidOperationException(askExecution.Ack.Error
                    ?? "internal Explorer Ask command was rejected");
            }

            var result = askExecution.Result;
            if (HandlePendingBudget(server, sessionId, runId, interactive, writeLine) is { } budgetCode)
                return budgetCode;
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
            if (result.StopReason == StopReason.ContextOverflow &&
                await TryEscalateAsync(loaded, model!, route.Id, usableContext, prompt, act, writeLine, cancellationToken) is { } escalatedCode)
                return escalatedCode;

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
                    var actTurn = new ExplorerTurn((request, token) => server.Observability.Complete(sessionId, provider, request,
                        modelDefinition?.ContextWindow, token),
                        actExecutor, actTools.Catalog(), materializer, fingerprint, selection,
                        server.AcquireStore(), server.AcquireCodecs(), artifacts, audit,
                        new RedactionPolicy(), harness, boundary, loaded.Pricing(model),
                        providerDescription?.BillingMode is BillingMode.MeteredCurrency or BillingMode.Unknown or BillingMode.CreditBalance,
                        sessionCapUsd: sessionCap, dailyCapUsd: loaded.DailyCapUsd,
                        questionnaires: questionnaireService, questionnaireResponder: QuestionnaireResponder,
                        metaModelProvider: provider, modelContextCapacity: modelDefinition?.ContextWindow,
                        recordEffectiveFingerprint: true);
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
            var askExecution = server.ExecuteExplorerTurn(sessionId, runId,
                token => turn.Ask(nextPrompt, instruction, sessionId, runId, laneId, workingState,
                    token, origin), cancellationToken);
            if (askExecution.Result is null
                || askExecution.Ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted)
            {
                writeLine(Text(Localized("cli.runtime.error", ("command", "act"),
                    ("message", askExecution.Ack.Error ?? "internal Explorer turn command was rejected"),
                    ("type", nameof(InvalidOperationException)))));
                return 1;
            }

            var result = askExecution.Result;
            origin = null;
            if (HandlePendingBudget(server, sessionId, runId, interactive, writeLine) is { } budgetCode)
                return budgetCode;
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
                if (result.StopReason == StopReason.Error)
                {
                    var failure = server.AcquireStore().ReadFrom(sessionId, 1)
                        .Select(server.AcquireCodecs().Decode).OfType<TurnAbandoned>().LastOrDefault();
                    if (failure is not null) writeLine(RedactSensitive(failure.Reason));
                }
                writeLine(Text(LocalizedText.Of("coder.completion.invalid")));
                return 1;
            }

            IReadOnlyList<ExternalCompletionGateResult> checkResults = Array.Empty<ExternalCompletionGateResult>();
            var completion = server.CheckRunCompletionAndGate(sessionId, runId, stream =>
            {
                if (acceptedResults is not null)
                {
                    checkResults = acceptedResults;
                }
                else if (gateConfiguration is not null && hasAnyGate)
                {
                    var runEvents = server.AcquireStore().ReadFrom(sessionId, 1);
                    var run = RunProjection.Replay(sessionId, runId, server.AcquireCodecs(), runEvents);
                    var runner = new ConfiguredCompletionGates(gateConfiguration, _workspaceRoot, runId,
                        laneId, run.RootTask, restrictions, audit, artifacts,
                        interactionResponder, interactive);
                    checkResults = runner.Run(stream, cancellationToken);
                }
                return checkResults;
            }, turn.MutationLedger);
            if (completion.Ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted
                || completion.Completed is not { } completed)
            {
                writeLine(Text(Localized("cli.runtime.error", ("command", "act"),
                    ("message", completion.Ack.Error ?? "completion evaluation was rejected"),
                    ("type", nameof(InvalidOperationException)))));
                return 1;
            }
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
                    var resumedEvaluation = server.CheckRunCompletionAndGate(sessionId, runId,
                        _ => acceptedResults, turn.MutationLedger);
                    if (resumedEvaluation.Ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted
                        || resumedEvaluation.Completed is not { } resumed)
                    {
                        writeLine(Text(Localized("cli.runtime.error", ("command", "act"),
                            ("message", resumedEvaluation.Ack.Error ?? "completion evaluation was rejected"),
                            ("type", nameof(InvalidOperationException)))));
                        return 1;
                    }
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

    internal int? AuthorizeRouteForInvocation(OmniServer server, SessionId session, RunId run,
        ModelRoute route, BillingMode mode, Action<string> writeLine, string locale, bool requireConsent = false)
    {
        var initialized = server.EnsureSessionRoutingPolicy(session);
        if (initialized.Status != "ok") { writeLine(initialized.Error ?? "Routing policy initialization rejected"); return 1; }
        var authorization = server.AuthorizeModelRoute(session, run, route, mode, requireConsent);
        if (authorization.Authorized) return null;
        if (authorization.Interaction is not { } interaction)
        { writeLine(authorization.Ack.Error ?? "Model route authorization rejected"); return 1; }
        if (HasInteractionClient)
        { writeLine(InputRequiredJson(interaction, "ModelRouteConsent")); return 3; }
        if (UseConsoleInput && !Console.IsInputRedirected)
        {
            var request = server.AcquireStore().ReadFrom(session, 1).Select(server.AcquireCodecs().Decode)
                .OfType<InteractionRequested>().Last(request => request.InteractionId == interaction);
            var selected = CreateInteractionResponder(writeLine, locale)(request) ?? "deny";
            var ack = server.RespondToInteraction(interaction, selected);
            if (ack.Status != "ok") { writeLine(ack.Error ?? "Model route response rejected"); return 1; }
            if (selected == "allow_route" && server.AuthorizeModelRoute(session, run, route, mode).Authorized) return null;
            return 1;
        }
        var denial = server.ResolveModelRouteWithoutClient(session, run, interaction);
        if (denial.Status != "ok") writeLine(denial.Error ?? "NoClient route denial rejected");
        return 1;
    }

    internal int? HandlePendingBudget(OmniServer server, SessionId session, RunId run,
        bool interactiveConsole, Action<string> writeLine)
    {
        if (server.LastSessionId() != session || server.LastRunId() != run) return 1;
        var pending = new Dictionary<InteractionId, DomainEvent>();
        foreach (var evt in server.AcquireStore().ReadFrom(session, 1))
        {
            switch (server.AcquireCodecs().Decode(evt))
            {
                case InteractionRequested request: pending[request.InteractionId] = evt; break;
                case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
            }
        }
        var budget = pending.Values.OrderBy(evt => evt.Sequence)
            .FirstOrDefault(evt => evt.RunId == run && server.AcquireCodecs().Decode(evt)
                is InteractionRequested { Kind: InteractionKind.BudgetExceeded });
        if (budget is null)
        {
            if (pending.Values.Any(evt => evt.RunId is null && server.AcquireCodecs().Decode(evt)
                    is InteractionRequested { Kind: InteractionKind.BudgetExceeded }))
            {
                writeLine("Pending budget interaction has no Run attribution; execution is blocked");
                return 1;
            }
            return null;
        }
        var requestPayload = (InteractionRequested)server.AcquireCodecs().Decode(budget);
        if (HasInteractionClient || interactiveConsole)
        {
            // A live client owns the answer. Do not mistake console suppression for NoClient,
            // and never auto-approve an increase of the spending authorization.
            writeLine(InputRequiredJson(requestPayload.InteractionId, "BudgetExceeded"));
            return 3;
        }
        var ack = server.ResolveBudgetWithoutClient(session, run, requestPayload.InteractionId);
        if (ack.Status != "ok") writeLine(ack.Error ?? "Budget denial was rejected");
        return 1;
    }

    private Func<InteractionRequested, string?> CreateInteractionResponder(Action<string> writeLine, string locale) => request =>
    {
        if (!UseConsoleInput || Console.IsInputRedirected) return null;
        var titleKey = request.Kind switch
        {
            InteractionKind.WeakSandboxConsent => "interaction.weak_sandbox.title",
            InteractionKind.BudgetExceeded => "interaction.budget_exceeded.title",
            InteractionKind.ModelRouteConsent => "interaction.model_route_consent.title",
            _ => "interaction.permission.title",
        };
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
                    "allow_plus" when request.Kind == InteractionKind.BudgetExceeded => "interaction.budget_exceeded.continue",
                    "allow_route" when request.Kind == InteractionKind.ModelRouteConsent => "interaction.model_route_consent.allow_route",
                    "deny" when request.Kind == InteractionKind.ModelRouteConsent => "interaction.model_route_consent.deny",
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
        if (!UseConsoleInput || Console.IsInputRedirected) return null;
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

    internal static ModelDefinition? ResolveExplicitModel(LoadedUserConfiguration loaded, string modelId)
    {
        try { return loaded.Registry.Resolve(modelId); }
        catch (UnknownModelException) { return null; }
    }

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

    /// <summary>Read-only account queries through official CLI login; no model prompt and no renderer credentials.</summary>
    public async Task<ProviderQuotaSnapshot?> RefreshProviderQuotaAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (_server?.LastSessionId() is not { } session) return null;
        var quota = await new SubscriptionQuotaService().QueryAsync(providerId, cancellationToken).ConfigureAwait(false);
        // Do not apply an asynchronous account result to another session after navigation.
        if (_server.LastSessionId() == session) _server.Observability.SetQuota(session, quota);
        return quota;
    }

    /// <summary>ADR-0044: el router no manda una tarea escritora a un modelo sin política de mutación suficiente.</summary>
    private static bool HasWritePolicy(ModelDefinition candidate, LoadedUserConfiguration loaded, CancellationToken cancellationToken)
    {
        var provider = loaded.Registry.Provider(candidate.ProviderId);
        var harness = new HarnessPolicyResolver().Resolve(new ModelProfileResolver().Resolve(candidate, provider,
            route: ModelRoutingHost.RouteFor(candidate, provider)));
        try
        {
            var effective = OmniHost.CreateModelPolicyService(null)
                .Effective(ModelPolicyKey.For(candidate.ProviderId, candidate.Id), harness, cancellationToken);
            return !effective.IsFallback && effective.MutationPolicy.Mode != FileMutationMode.None;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Escalación explícita ante un límite de contexto (spec §73): se registra la causa; en modo
    /// <c>auto</c> se aprueba por política y el mismo pedido se repite con el siguiente modelo de la
    /// cadena; en modo <c>ask</c> queda solicitada y se informa, sin inventar una aprobación.
    /// </summary>
    private async Task<int?> TryEscalateAsync(LoadedUserConfiguration loaded, string currentModel, RouteId currentRouteId, long currentContext,
        string prompt, bool act, Action<string> writeLine, CancellationToken cancellationToken)
    {
        if (_escalatedModel is not null || Environment.GetEnvironmentVariable("OMNI_MODEL") is not null) return null;
        var next = ModelRoutingHost.NextEscalation(loaded, currentRouteId, act, currentContext + 1,
            candidate => HasWritePolicy(candidate, loaded, cancellationToken), _providerCircuits);
        var server = Server();
        if (next is null || server.LastSessionId() is not { } session || server.LastRunId() is not { } run) return null;
        var originatingTurn = server.AcquireStore().ReadFrom(session, 1)
            .Where(evt => evt.RunId == run).Select(server.AcquireCodecs().Decode).OfType<TurnStarted>().LastOrDefault();
        EnsureEscalationRecorded(server.RecordModelEscalationRequested(session,
            new ModelEscalationRequested(run, currentModel, next.ModelId, EscalationCause.ContextLimit,
                originatingTurn?.TurnId, originatingTurn?.LaneId)));
        // phaseA7 (M55): en modo auto no se aprueba la escalación si el proveedor del modelo
        // destino requiere API key y esta no está resuelta; misma semántica que RunTurnAsync,
        // sin aprobar ni completar el intento (el Requested ya registra la causa).
        var escalatedDefinition = loaded.Registry.Model(next.ModelId);
        var escalatedProvider = escalatedDefinition is null ? null
            : loaded.Registry.Provider(escalatedDefinition.ProviderId);
        if (escalatedDefinition is null) return 1;
        var escalatedRoute = next.Route;
        var escalationMode = ModelRoutingHost.EscalationMode(loaded);
        var locale = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en" ? "en" : "es";
        if (AuthorizeRouteForInvocation(server, session, run, escalatedRoute,
                escalatedProvider?.BillingMode ?? BillingMode.Unknown, writeLine, locale,
                requireConsent: escalationMode == "ask") is { } authorizationCode)
            return authorizationCode;
        if (escalatedProvider?.Auth.Kind == AuthKind.ApiKey)
        {
            string? escalatedKey;
            try
            {
                var credentials = OmniHost.CreateUserCredentialStore(OmniHost.CreatePlatformPaths());
                escalatedKey = OmniHost.ResolveApiKey(credentials, escalatedProvider.Auth.SecretRef ?? "qwen",
                    Environment.GetEnvironmentVariable("OMNI_QWEN_KEY"), cancellationToken);
            }
            catch (SecretValueTooShortException ex)
            {
                writeLine(Text(Localized("cli.runtime.command.error", ("command", act ? "act" : "ask"),
                    ("message", Text(ex.UserMessage)))));
                return 1;
            }
            if (escalatedKey is null)
            {
                writeLine(Text(Localized("cli.runtime.credential.missing",
                    ("command", act ? "act" : "ask"))));
                return 1;
            }
        }
        EnsureEscalationRecorded(server.RecordModelEscalationApproved(session,
            new ModelEscalationApproved(run, next.ModelId, escalationMode == "auto" ? "policy:auto-authorized" : "interaction:user",
                originatingTurn?.TurnId, originatingTurn?.LaneId)));
        writeLine(Text(Localized("cli.escalation.auto", ("from", currentModel), ("model", next.ModelId))));
        var beforeTarget = server.AcquireStore().CurrentSequence(session);
        _escalatedModel = next.ModelId;
        try
        {
            var code = await RunTurnAsync("", act, writeLine, cancellationToken,
                conversationOnly: !act && server.CurrentRunMode() == RunMode.Plan,
                routingResume: new(session, run, null, next.ModelId, next.Route)).ConfigureAwait(false);
            if (server.LastSessionId() == session && server.LastRunId() == run
                && server.AcquireStore().ReadFrom(session, beforeTarget + 1).Any(evt => evt.RunId == run
                    && server.AcquireCodecs().Decode(evt) is ModelStepCompleted))
                EnsureEscalationRecorded(server.RecordModelEscalationCompleted(session,
                    new ModelEscalationCompleted(run, next.ModelId, originatingTurn?.TurnId, originatingTurn?.LaneId)));
            return code;
        }
        finally { _escalatedModel = null; }
    }

    private static void EnsureEscalationRecorded(CommandAck ack)
    {
        if (ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted)
        {
            throw new InvalidOperationException(ack.Error ?? "the model escalation event was rejected");
        }
    }

    public void SetWorkspaceTrusted(bool trusted) =>
        new WorkspaceTrustStore(OmniHost.CreatePlatformPaths()).SetTrusted(_workspaceRoot, trusted);

    /// <summary>
    /// Traits empíricos de la capa Empirical (ADR-0007 §1, M5) para la configuración exacta del
    /// modelo: solo si existe un perfil Qualified/Calibrated/Stale en el almacén de scope User.
    /// Nunca resuelve por nombre de modelo; sin perfil utilizable devuelve null y el resolver
    /// queda en HeuristicDefaults (M2).
    /// </summary>
    private static IReadOnlyDictionary<string, double>? EmpiricalTraits(ModelDefinition? model,
        ProviderDescriptor? provider, string? dataDirectoryOverride, CancellationToken cancellationToken) =>
        EmpiricalQualification(model, provider, dataDirectoryOverride, cancellationToken)?.Traits;

    private static ModelQualificationSnapshot? EmpiricalQualification(ModelDefinition? model,
        ProviderDescriptor? provider, string? dataDirectoryOverride, CancellationToken cancellationToken,
        string? endpointOverride = null)
    {
        if (model is null)
        {
            return null;
        }

        try
        {
            using var store = OmniHost.CreateModelQualificationStore(dataDirectoryOverride);
            return ModelQualificationHost.UsableSnapshot(store, model, provider, cancellationToken, endpointOverride);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Un almacén dañado no degrada la ejecución: el perfil cae a HeuristicDefaults.
            return null;
        }
    }

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
        var profile = new ModelProfileResolver().Resolve(definition, provider,
            empiricalTraits: EmpiricalTraits(definition, provider,
                Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR"), cancellationToken),
            route: ModelRoutingHost.RouteFor(definition, provider));
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

    /// <summary>
    /// Computa un hash determinista de la política de contexto efectiva (ContextManagementPolicy + budget).
    /// Versión 1: campos estables de ContextManagementPolicy + budget de tokens utilizables.
    /// </summary>
    private static string ComputeContextPolicyHash(ContextManagementPolicy policy, long usableContext)
    {
        var canonical = string.Join("|",
            "ctx-policy-v1",
            policy.ExternalizeAboveCharacters.ToString(CultureInfo.InvariantCulture),
            policy.CompressBodyCharacters.ToString(CultureInfo.InvariantCulture),
            policy.RecentTailItems.ToString(CultureInfo.InvariantCulture),
            policy.CompactAfterItems.ToString(CultureInfo.InvariantCulture),
            policy.MaxCheckpointCharacters.ToString(CultureInfo.InvariantCulture),
            usableContext.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
