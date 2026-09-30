namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Tools;

/// <summary>Fachada tipada del runtime usada por el CLI; oculta composición y tipos internos.</summary>
public sealed class OmniCliRuntime
{
    private readonly string _workspaceRoot;
    private OmniServer? _server;

    private OmniCliRuntime(string workspaceRoot) => _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public static OmniCliRuntime Create(string workspaceRoot) => new(workspaceRoot);

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
    public static int Doctor(string locale, Action<string> writeLine)
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        var paths = OmniHost.CreatePlatformPaths();
        var registry = OmniHost.LoadUserModelRegistry(paths);
        writeLine(locale == "en" ? "omni doctor — M2 diagnostics" : "omni doctor — diagnóstico de M2");
        writeLine((locale == "en" ? "Configuration: " : "Configuración: ") + paths.ConfigDirectory);
        WarnIgnoredRepoConfig(paths.ConfigDirectory, writeLine);
        writeLine(locale == "en" ? "Available models:" : "Modelos disponibles:");
        foreach (var model in registry.Models())
        {
            var provider = registry.Provider(model.ProviderId);
            writeLine("  " + model.Id + " → provider '" + model.ProviderId + "'"
                + (provider is null ? "" : " (" + provider.Family + ", " + provider.BaseUrl + ")"));
            if (provider is not null)
            {
                writeLine("    TLS: " + OmniHost.DescribeTls(provider.BaseUrl, provider.TrustedCertificatePath));
            }
        }

        var tokenizer = OmniHost.CreateTokenCounter();
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

    public static string RedactSensitive(string value) => new PiiRedactor().Redact(value);

    private async Task<int> RunTurnAsync(string prompt, bool act, Action<string> writeLine,
        CancellationToken cancellationToken)
    {
        var paths = OmniHost.CreatePlatformPaths();
        var workspaceData = OmniHost.WorkspaceDataDirectory(paths, _workspaceRoot);
        var registry = OmniHost.LoadUserModelRegistry(paths);
        WarnIgnoredRepoConfig(paths.ConfigDirectory, writeLine);
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

        var providerDescription = modelDefinition is null ? null : registry.Provider(modelDefinition.ProviderId);
        var secretRef = providerDescription?.Auth.SecretRef ?? "qwen";
        var model = Environment.GetEnvironmentVariable("OMNI_MODEL") ?? modelDefinition?.Id;
        var baseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL") ?? providerDescription?.BaseUrl
            ?? "http://127.0.0.1:8080/v1";
        string? key = null;
        if (providerDescription?.Auth.Kind == AuthKind.ApiKey)
        {
            var credentials = OmniHost.CreateUserCredentialStore(paths);
            key = OmniHost.ResolveApiKey(credentials, secretRef,
                Environment.GetEnvironmentVariable("OMNI_QWEN_KEY"), cancellationToken);
            if (key is null)
            {
                writeLine("omni " + (act ? "act" : "ask") + ": '" + (providerDescription.Id ?? "local")
                    + "' requiere API key '" + secretRef + "'. Define OMNI_QWEN_KEY una vez: se guarda cifrada"
                    + (act ? " en el almacén del usuario (ADR-0018)." : " en el almacén del usuario y no hará falta"
                    + " volver a definirla. No se guardan secretos en el repo (ADR-0018)."));
                return 1;
            }
        }

        try
        {
            if (model is null)
            {
                writeLine("omni " + (act ? "act" : "ask") + ": "
                    + (noModel?.UserMessage ?? LocalizedText.Of("models.noneConfigured")).Render());
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
                        writeLine("omni ask: " + (start.Error ?? "no se pudo iniciar el run"));
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
                    writeLine("omni act: " + (ack.Error ?? "no se pudo crear el Run Act"));
                    return 1;
                }
            }

            var sessionId = server.LastSessionId() ?? SessionId.New();
            var runId = server.LastRunId() ?? RunId.New();
            var laneId = server.LastLaneId() ?? LaneId.New();
            var provider = OmniHost.ConnectLocalChatCompletions(baseUrl, model, secretRef, key ?? "",
                providerDescription?.TrustedCertificatePath);
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
                harness.PlanControl, harness.StallThresholdTurns);
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
            var fingerprint = new ExecutionFingerprint(model, harnessHash, "core-tools-1", "heuristic:chars4/1",
                "none", act ? "M3" : "M2", effectivePolicy.Fingerprint(), "heuristic:chars4/1");
            var selection = new ModelSelection(new ModelIdValue(model), usableContext, ToolMode.Direct, null);
            var localHost = OmniHost.CreateLocalModelHost();
            if (!act && localHost.IsManagedRunning())
            {
                writeLine("omni ask: (servidor local managed activo)");
            }

            var hostTools = act ? OmniHost.CreateActTools() : OmniHost.CreateExplorerTools();
            var workspaceRoot = _workspaceRoot;
            var executor = act
                ? OmniHost.CreateActExecutor(hostTools.Catalog(), workspaceRoot, boundary)
                : OmniHost.CreateExplorerExecutor(hostTools.Catalog(), workspaceRoot, boundary);
            var contributors = act
                ? Array.Empty<IContextContributor>()
                : new IContextContributor[] { new WorkingStateContributor(workingState) };
            var materializer = new ContextMaterializer(new HeuristicTokenCounter(), contributors);
            var artifacts = OmniHost.CreateArtifactStore(workspaceData);
            var turn = new ExplorerTurn((request, token) => provider.Complete(request, token), executor,
                hostTools.Catalog(), materializer, fingerprint, selection, server.AcquireStore(),
                server.AcquireCodecs(), artifacts, new InMemoryAuditSink(), new RedactionPolicy(), harness, boundary);
            var instruction = act
                ? "Eres un asistente de ingeniería operando en el workspace actual. Tienes filesystem.read y filesystem.patch bajo la política efectiva del modelo. Contexto del run disponible ({context}). Responde la instrucción y usa las tools cuando aporten; no inventes lecturas ni tokens [version:…]: lee antes de parchear."
                : "Ayudas a un asistente de ingeniería. Work Thread del workspace:\nContexto del run disponible ({context}).\nResponde en español, sé conciso y usa las tools cuando aporten.";
            var result = turn.Ask(prompt, instruction, sessionId, runId, laneId, workingState, cancellationToken);
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

    private OmniServer Server() => Server(OmniHost.WorkspaceDataDirectory(
        OmniHost.CreatePlatformPaths(), _workspaceRoot));

    private OmniServer Server(string workspaceData)
    {
        _server ??= OmniHost.OpenPersistentServer(Path.Combine(workspaceData, "journal.db"));
        return _server;
    }

    private static string ReadWorkingState(IOmniClient client, CancellationToken cancellationToken)
    {
        var json = client.Query("workingState", cancellationToken)?.Json ?? "{}";
        return JsonObj.Parse(json).TryGetValue("workingState", out var text) ? text ?? "" : "";
    }

    private static void WarnIgnoredRepoConfig(string configDirectory, Action<string> writeLine)
    {
        foreach (var name in new[] { "providers.yaml", "models.yaml" })
        {
            if (File.Exists(Path.Combine(".", name))
                && !Path.GetFullPath(".").Equals(Path.GetFullPath(configDirectory), StringComparison.OrdinalIgnoreCase))
            {
                writeLine("aviso: se ignora ./" + name + " (un repo no configura providers, INV-029). "
                    + "La configuración va en " + Path.Combine(configDirectory, name));
            }
        }
    }
}
