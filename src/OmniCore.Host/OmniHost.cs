namespace OmniCore.Host;

using System.Collections.Concurrent;
using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Sandbox;
using OmniCore.Tools;

/// <summary>
/// Composition root de OmniCore (ADR-0019 §3). La superficie pública es mínima: la fábrica del
/// host y del cliente in-process. El CLI se conecta vía IOmniClient con DTOs wire; incluso
/// in-process pasa por el protocolo para no acoplar la UI al Engine.
/// </summary>
public sealed class OmniHost
{
    private static readonly ConcurrentDictionary<RunId, WeakReference<WeakSandboxConsentState>> RunSandboxConsents = new();

    static OmniHost() => _ = SecretRedactor.Shared;

    private readonly string[] _args;

    private OmniHost(string[] args) => _args = args;

    public static OmniHost Create(string[] args) => new(args);

    public string[] Args() => _args;

    /// <summary>Crea un servidor in-process conectando el Engine con sus stores (ADR-0002, ADR-0041).</summary>
    public static OmniServer CreateInProcessServer(string dataDirectory)
    {
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(Path.Combine(dataDirectory, "journal.db"));
        var audit = new FileAuditSink(dataDirectory);
        return new OmniServer(store, codecs, audit);
    }

    /// <summary>Crea un servidor in-process con un store en memoria (tests deterministas).</summary>
    public static OmniServer CreateInMemoryServer()
    {
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var audit = new InMemoryAuditSink();
        return new OmniServer(store, codecs, audit);
    }

    /// <summary>
    /// Crea un servidor sobre un journal SQLite persistente (ADR-0041 §2): permite reanudar
    /// un run (crash → resume) entre procesos distintos apuntando al mismo archivo. La
    /// auditoría es un FileAuditSink persistente fuera del journal, así que sobrevive a la purga
    /// de sesiones. El runtime real pasa el directorio de datos del usuario: scope User,
    /// <c>&lt;data&gt;/audit/</c> (ADR-0043 §1, ADR-0039 §2); sin él se usa el del workspace.
    /// </summary>
    public static OmniServer OpenPersistentServer(string journalFile, string? auditDataDirectory = null)
    {
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journalFile);
        var workspaceData = Path.GetDirectoryName(Path.GetFullPath(journalFile))!;
        var audit = new FileAuditSink(auditDataDirectory ?? workspaceData);
        return new OmniServer(store, codecs, audit, Path.Combine(workspaceData, "lastsession.txt"),
            CreateArtifactStore(workspaceData), auditDataDirectory is null ? null
                : new UserWorkspaceSpendReader(auditDataDirectory, workspaceData));
    }

    /// <summary>
    /// Fábrica del Artifact Store content-addressed en disco (ADR-0001 §5, ADR-0041 §3).
    /// En M1 el pipeline no externaliza outputs grandes (los FakeTools son cortos); el CAS
    /// completo con GC y verify llega en M2. Se expone aquí para no quedar huérfano.
    /// </summary>
    public static IArtifactStore CreateArtifactStore(string dataDirectory) => new FileArtifactStore(dataDirectory);

    /// <summary>LocalModelHost cableado con el SystemProcessRuntime real (attach + managed, ADR-0011 §4).</summary>
    public static LocalModelHost CreateLocalModelHost() => new LocalModelHost(SystemProcessRuntime.Instance());

    /// <summary>
    /// Crea la selección de lanzador de procesos por nivel: AppContainer cuando se solicita Strong
    /// y la sonda lo permite; Weak/None usan el runtime normal. Un Strong no disponible falla
    /// explícitamente para que el llamador aplique WeakSandboxConsent antes de reintentar.
    /// </summary>
    public static ISandboxProcessLauncher CreateProcessSandboxLauncher(IProcessRuntime runtime,
        ISandboxCapabilitiesProbe? capabilities = null) =>
        new PlatformSandboxProcessLauncher(runtime, capabilities);

    /// <summary>CredentialStore de plataforma del milestone M2 (FileCredentialStore; ver ADR-0011 AC-2026-09-27).</summary>
    public static ICredentialStore CreateCredentialStore(string dataDirectory) =>
        new FileCredentialStore(Path.Combine(dataDirectory, "credentials.ini"));

    /// <summary>
    /// API key de un provider: la del entorno si viene (y se guarda cifrada para las siguientes
    /// ejecuciones) o, si no, la del credential store del usuario. Null si no hay ninguna.
    /// </summary>
    public static string? ResolveApiKey(ICredentialStore credentials, string secretRef, string? environmentKey,
        CancellationToken cancellationToken)
    {
        if (environmentKey is not null && environmentKey.Length > 0)
        {
            if (environmentKey.Length < Secret.MinimumLength)
                throw new SecretValueTooShortException(Secret.MinimumLength);
            SecretRedactorRegistry.Register(environmentKey);
            credentials.Save(secretRef, environmentKey, cancellationToken);
            return environmentKey;
        }

        var stored = credentials.Load(secretRef, cancellationToken);
        if (stored is not null && stored.Length > 0)
        {
            if (stored.Length < Secret.MinimumLength)
                throw new SecretValueTooShortException(Secret.MinimumLength);
            SecretRedactorRegistry.Register(stored);
        }
        return stored is not null && stored.Length > 0 ? stored : null;
    }

    /// <summary>Rutas de plataforma del usuario (datos y configuración fuera del repo, ADR-0038 §2).</summary>
    public static IPlatformPaths CreatePlatformPaths(string? dataDirectoryOverride = null) =>
        new DefaultPlatformPaths(dataDirectoryOverride);

    /// <summary>CredentialStore del usuario, en el directorio de datos de la plataforma (nunca en el repo).</summary>
    /// <summary>Sesión de la suscripción ChatGPT del usuario (scope User, tokens cifrados en el credential store).</summary>
    public static ChatGptSubscriptionAuthProvider CreateChatGptAuth(IPlatformPaths paths) =>
        new(CreateUserCredentialStore(paths), static () => new HttpClient { Timeout = System.TimeSpan.FromSeconds(60) });

    /// <summary>Estado y apertura explícita del CLI oficial de Claude Code.
    /// No acredita login ni conecta un provider OAuth nativo al chat.</summary>
    public static ClaudeAccountService CreateClaudeAccountService() =>
        new(SystemProcessRuntime.Instance());

    public static ICredentialStore CreateUserCredentialStore(IPlatformPaths paths) =>
        CreateCredentialStore(paths.DataDirectory);

    /// <summary>
    /// Directorio de runtime del workspace: <c>(data)/workspaces/&lt;WorkspaceId&gt;/</c> (journal, blobs;
    /// ADR-0039 §2). El id se deriva de la ruta canónica de la raíz.
    /// </summary>
    public static string WorkspaceDataDirectory(IPlatformPaths paths, string workspaceRoot) =>
        paths.WorkspaceDirectory(OmniCore.Domain.WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
            ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot))).ToString());

    /// <summary>ModelRegistry desde la configuración del USUARIO, nunca desde el cwd (INV-029, ADR-0039).</summary>
    public static ModelRegistry LoadUserModelRegistry(IPlatformPaths paths) =>
        LoadModelRegistry(paths.ConfigDirectory);

    /// <summary>Store relacional de políticas de modelo en el user.db de plataforma (ADR-0044 §8).</summary>
    public static SqliteModelPolicyStore CreateModelPolicyStore() =>
        new(new DefaultPlatformPaths().UserDatabasePath);

    /// <summary>
    /// Servicio de política operativa de modelos (ADR-0044): onboarding, política efectiva y
    /// selección por workspace. Con el dataDirectory explícito, para tests y para el CLI
    /// cuando el usuario fija otra ubicación.
    /// </summary>
    public static ModelPolicyService CreateModelPolicyService(string? dataDirectoryOverride)
    {
        var paths = new DefaultPlatformPaths(dataDirectoryOverride);
        return new ModelPolicyService(new SqliteModelPolicyStore(paths.UserDatabasePath))
        {
            EvidenceLookup = (key, token) => QualificationEvidenceFor(paths, dataDirectoryOverride, key, token),
        };
    }

    /// <summary>
    /// Evidencia de cualificación utilizable para el onboarding de una <see cref="ModelPolicyKey"/>
    /// (ADR-0044 §6): el perfil Qualified/Calibrated/Stale del modelo en el registro del usuario.
    /// Un almacén o registro ilegible no rompe el onboarding: vuelve a la recomendación conservadora.
    /// </summary>
    internal static QualificationEvidence? QualificationEvidenceFor(IPlatformPaths paths,
        string? dataDirectoryOverride, ModelPolicyKey key, CancellationToken cancellationToken)
    {
        try
        {
            var registry = LoadUserModelRegistry(paths);
            var model = registry.Models().FirstOrDefault(candidate =>
                candidate.ProviderId == key.ProviderId && candidate.Id == key.ModelId);
            if (model is null) return null;
            using var store = CreateModelQualificationStore(dataDirectoryOverride);
            var snapshot = ModelQualificationHost.UsableSnapshot(store, model, registry.Provider(model.ProviderId),
                cancellationToken);
            return snapshot is null ? null
                : new QualificationEvidence(snapshot.Traits, snapshot.FileMutationSamples, snapshot.State);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Store relacional de perfiles de cualificación empírica (M5, ADR-0007 §6) sobre el user.db
    /// de plataforma (tablas model_profiles/model_traits, scope User). El llamador es dueño del
    /// ciclo de vida (IDisposable).
    /// </summary>
    public static SqliteModelQualificationStore CreateModelQualificationStore(string? dataDirectoryOverride) =>
        new(new DefaultPlatformPaths(dataDirectoryOverride).UserDatabasePath);

    /// <summary>Token counter estimado por defecto, o exacto para providers llama.cpp declarados en config.</summary>
    public static ITokenCounter CreateTokenCounter() => new HeuristicTokenCounter();

    /// <summary>Calibración de estimaciones de tokens compartida por el proceso.</summary>
    internal static TokenEstimateCalibrator TokenCalibration { get; } = new();

    public static ITokenCounter CreateTokenCounter(ProviderDescriptor? provider, string? declaredKind,
        string? apiKey, Func<HttpClient>? httpFactory = null, string? baseUrlOverride = null)
    {
        if (provider is null || declaredKind is not ("llamaCpp" or "ikLlama")) return new HeuristicTokenCounter();
        // El calibrador vive con el proceso: los conteos exactos de /tokenize ajustan la estimación
        // de respaldo del mismo tokenizer (ADR-0042; M5 calibración de estimaciones).
        var tokenizer = new OmniCore.Domain.TokenizerId("llama.cpp:" + provider.Id);
        var fallback = new HeuristicTokenCounter(HeuristicTokenCounter.DefaultSafetyMargin, TokenCalibration, tokenizer.Value);
        return new LlamaCppTokenCounter(GetLlamaCppTokenizeBaseUrl(baseUrlOverride ?? provider.BaseUrl),
            tokenizer, fallback, httpFactory, () => apiKey,
            onExactCount: (chars, tokens) => TokenCalibration.AddSample(tokenizer.Value, chars, tokens));
    }

    /// <summary>El endpoint llama.cpp vive en la raíz del servidor, no bajo el prefijo OpenAI /v1.</summary>
    public static string GetLlamaCppTokenizeBaseUrl(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
        var builder = new UriBuilder(uri) { Path = path, Query = "", Fragment = "" };
        return builder.Uri.ToString().TrimEnd('/');
    }

    /// <summary>
    /// Catálogo de tools Core completo (fake + filesystem.read/reference.resolve/plan.propose)
    /// con la frontera de paths y el PlanService. Es la composición real del runtime (no test).
    /// </summary>
    public static HostTools CreateHostTools() => HostTools.Default();

    public static HostTools CreateExplorerTools(ArtifactReadTool? artifactReadTool = null) =>
        HostTools.Explorer(artifactReadTool);

    /// <summary>
    /// Executor del pipeline real de tools + permisos para la simulación: catálogo completo y
    /// política con defaults por modo (Act). Ask sin cliente → Deny (los turnos no-interactivos
    /// del CLI no pueden aprobar; solo el modo chat abre InteractionRequest). El workspaceRoot
    /// fija la frontera de paths del turno (el cwd del proceso).
    /// </summary>
    public static IToolExecutor CreateSimExecutor(string workspaceRoot)
    {
        var hostTools = HostTools.Default();
        var policy = ScriptedPermissionPolicy
            .WithTool("fake.write", OmniCore.Domain.PermissionDecision.Allow)
            .WithModeDefaults(OmniCore.Domain.RunMode.Act);
        return ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, workspaceRoot);
    }

    /// <summary>
    /// Executor del Turn de Explorer. Explorer es de solo lectura, así que la capa de modo es la
    /// de PLAN: escrituras, procesos y red quedan en Deny aunque alguien meta una tool mutadora
    /// en el catálogo (techo de seguridad, además de no exponerla).
    /// </summary>
    public static IToolExecutor CreateExplorerExecutor(FakeCatalog catalog, string workspaceRoot)
    {
        return CreateExplorerExecutor(catalog, workspaceRoot, null);
    }

    /// <summary>
    /// Executor del pipeline real para el Turn de Explorer con la frontera de capacidad del
    /// modelo (ADR-0044 §5). El modelo sin <c>UserModelPolicy</c> efectiva queda ObserveOnly: la
    /// frontera lo rechaza aunque invoque una tool de escritura directamente. La frontera
    /// restringe; el Permission Engine sigue siendo la única autoridad (INV-018). null = sin
    /// frontera (semántica M2).
    /// </summary>
    public static IToolExecutor CreateExplorerExecutor(FakeCatalog catalog, string workspaceRoot,
        ModelCapabilityBoundary? boundary) => CreateExplorerExecutor(catalog, workspaceRoot, boundary, null);

    public static IToolExecutor CreateExplorerExecutor(FakeCatalog catalog, string workspaceRoot,
        ModelCapabilityBoundary? boundary, IReadOnlyDictionary<string, string>? projectRestrictions,
        RunId? runId = null, AgentProfile? agentProfile = null,
        Func<ToolCallId, CancellationToken, Task<string?>>? receiveMailbox = null)
    {
        var policy = CreateGrantAwarePolicy(OmniCore.Domain.RunMode.Plan, projectRestrictions, workspaceRoot, runId);
        IPermissionPolicy effectivePolicy = agentProfile is null ? policy
            : new AgentProfilePermissionPolicy(policy, agentProfile, new PathBoundaryValidator(), workspaceRoot);
        return ScriptedToolExecutor.WithWorkspace(catalog, effectivePolicy, workspaceRoot, boundary, receiveMailbox);
    }

    public static ScriptedPermissionPolicy CreateProjectRestrictionPolicy(OmniCore.Domain.RunMode mode,
        IReadOnlyDictionary<string, string>? restrictions, UserPermissions? user = null)
    {
        var policy = new ScriptedPermissionPolicy(ParseProjectRestrictions(restrictions)).WithModeDefaults(mode);
        if (user is null) return policy;
        // ADR-0037 §4–§5: el perfil y las reglas del usuario (permissions.yaml, scope User) son la capa UserPolicy.
        policy.WithProfile(user.Profile);
        foreach (var (tool, decision) in user.Rules) policy.WithUserPolicyTool(tool, decision);
        return policy;
    }

    private static Dictionary<string, OmniCore.Domain.PermissionDecision> ParseProjectRestrictions(
        IReadOnlyDictionary<string, string>? restrictions)
    {
        var result = new Dictionary<string, OmniCore.Domain.PermissionDecision>(StringComparer.Ordinal);
        if (restrictions is null) return result;
        foreach (var pair in restrictions)
            result[pair.Key] = pair.Value == "deny"
                ? OmniCore.Domain.PermissionDecision.Deny : OmniCore.Domain.PermissionDecision.Ask;
        return result;
    }

    /// <summary>
    /// Catálogo de <c>omni act</c> (M3): tools Core de lectura + <c>filesystem.patch</c>, sin
    /// FakeTools de simulación. Es la única composición real que expone mutaciones.
    /// </summary>
    public static HostTools CreateActTools(SandboxStrength processSandboxStrength = SandboxStrength.Strong,
        ArtifactReadTool? artifactReadTool = null) =>
        new(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            includeMutationTools: true, includeProcessTools: true, processSandboxStrength: processSandboxStrength,
            artifactReadTool: artifactReadTool);

    /// <summary>
    /// Executor de <c>omni act</c>: capa de modo ACT (escrituras dentro del workspace permitidas por
    /// el perfil autónomo, ADR-0037 §4) más la frontera de capacidad del modelo (ADR-0044 §5), que
    /// deja ObserveOnly a un modelo sin política efectiva. La frontera restringe; el Permission
    /// Engine sigue siendo la única autoridad (INV-018).
    /// </summary>
    public static IToolExecutor CreateActExecutor(FakeCatalog catalog, string workspaceRoot,
        ModelCapabilityBoundary boundary, IReadOnlyDictionary<string, string>? projectRestrictions = null,
        RunId? runId = null, IAuditSink? audit = null,
        Func<InteractionRequested, string?>? interactionResponder = null, bool isInteractive = false,
        IArtifactStore? artifacts = null, AgentProfile? agentProfile = null,
        Func<ToolCallId, CancellationToken, Task<string?>>? receiveMailbox = null)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var policy = CreateGrantAwarePolicy(OmniCore.Domain.RunMode.Act, projectRestrictions, workspaceRoot, runId, audit);
        IPermissionPolicy effectivePolicy = agentProfile is null ? policy
            : new AgentProfilePermissionPolicy(policy, agentProfile, new PathBoundaryValidator(), workspaceRoot);
        return new ScriptedToolExecutor(catalog, effectivePolicy, workspaceRoot, boundary, audit,
            interactionResponder, isInteractive, GetWeakSandboxConsentState(runId),
            artifacts ?? CreateArtifactStore(WorkspaceDataDirectory(CreatePlatformPaths(), workspaceRoot)),
            receiveMailbox: receiveMailbox);
    }

    /// <summary>Política de permisos con grants aislados por WorkspaceId y auditados en user data.</summary>
    public static ScriptedPermissionPolicy CreateGrantAwarePolicy(
        OmniCore.Domain.RunMode mode, IReadOnlyDictionary<string, string>? restrictions, string workspaceRoot,
        RunId? runId, IAuditSink? audit = null)
    {
        var paths = CreatePlatformPaths();
        var workspace = OmniCore.Domain.WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
            ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot)));
        var store = new OmniCore.Security.FilePermissionGrantStore(
            WorkspaceDataDirectory(paths, workspaceRoot), audit ?? new FileAuditSink(paths.DataDirectory));
        return CreateProjectRestrictionPolicy(mode, restrictions, UserPermissionsLoader.Load(paths))
            .WithGrantStore(store, workspace, runId);
    }

    internal static WeakSandboxConsentState GetWeakSandboxConsentState(RunId? runId)
    {
        if (runId is null) return new WeakSandboxConsentState();
        foreach (var entry in RunSandboxConsents)
            if (!entry.Value.TryGetTarget(out _) && RunSandboxConsents.TryRemove(entry.Key, out _)) { }
        while (true)
        {
            if (RunSandboxConsents.TryGetValue(runId, out var existing))
            {
                if (existing.TryGetTarget(out var state)) return state;
                var replacementState = new WeakSandboxConsentState();
                if (RunSandboxConsents.TryUpdate(runId,
                    new WeakReference<WeakSandboxConsentState>(replacementState), existing)) return replacementState;
                continue;
            }

            var created = new WeakSandboxConsentState();
            if (RunSandboxConsents.TryAdd(runId, new WeakReference<WeakSandboxConsentState>(created))) return created;
        }
    }

    public static OmniCore.Security.FilePermissionGrantStore CreatePermissionGrantStore(
        string workspaceRoot, IAuditSink? audit = null)
    {
        var paths = CreatePlatformPaths();
        return new OmniCore.Security.FilePermissionGrantStore(WorkspaceDataDirectory(paths, workspaceRoot),
            audit ?? new FileAuditSink(paths.DataDirectory));
    }

    /// <summary>
    /// Carga el ModelRegistry desde providers.yaml/models.yaml de un directorio de configuración.
    /// Si no hay archivos, devuelve el registro mínimo (provider local + local-worker). El runtime
    /// usa <see cref="LoadUserModelRegistry"/>; pasar aquí un directorio del repo permitiría que
    /// el repo redirija la API key a otro host.
    /// </summary>
    public static ModelRegistry LoadModelRegistry(string configDirectory) => LoadUserConfiguration(configDirectory).Registry;

    public static LoadedUserConfiguration LoadUserConfiguration(IPlatformPaths paths) =>
        LoadUserConfiguration(paths.ConfigDirectory);

    /// <summary>Only trusted User config is read; workspace files and model output are never merged.</summary>
    internal static AgentProfileConfiguration.Loaded LoadAgentProfiles(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var file = Path.Combine(paths.ConfigDirectory, "agent-profiles.yaml");
        return File.Exists(file)
            ? AgentProfileConfiguration.LoadSelection(File.ReadAllText(file), ScopeLevel.User)
            : new AgentProfileConfiguration.Loaded(new AgentProfileRegistry([]), null);
    }

    public static LoadedUserConfiguration LoadUserConfiguration(string configDirectory)
    {
        var providersPath = Path.Combine(configDirectory, "providers.yaml");
        var modelsPath = Path.Combine(configDirectory, "models.yaml");
        var settingsPath = Path.Combine(configDirectory, "settings.yaml");
        return new ConfigLoader().Load(File.Exists(providersPath) ? File.ReadAllText(providersPath) : null,
            File.Exists(modelsPath) ? File.ReadAllText(modelsPath) : null,
            File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null);
    }

    /// <summary>
    /// Conecta un provider OpenAI Chat Completions local (M2). Validación TLS (ADR-0011 §4,
    /// ADR-0038 §4), en este orden:
    /// <list type="number">
    /// <item>Con <paramref name="trustedCertificatePath"/> (<c>caCertificate</c> del provider): la
    /// cadena del servidor debe terminar en ese certificado y el nombre del host debe coincidir.</item>
    /// <item>Sin él, para localhost o un literal IP privado (<see cref="IsPrivateHost"/>): se acepta
    /// cualquier certificado. Es un modo heredado y débil; <c>omni doctor</c> avisa.</item>
    /// <item>Para cualquier otro host: validación estándar del sistema.</item>
    /// </list>
    /// </summary>
    public static OpenAiChatCompatibleProvider ConnectLocalChatCompletions(string baseUrl, string modelId,
        string secretRef, string apiKey, string? trustedCertificatePath = null, ProviderResilienceCatalog? circuits = null)
    {
        var descriptor = new ProviderDescriptor("local", ProviderFamily.OpenAiChatCompatible, baseUrl,
            apiKey.Length > 0 ? AuthConfig.ApiKey(secretRef) : AuthConfig.None(), false, false, false)
        { TrustedCertificatePath = trustedCertificatePath };
        return (OpenAiChatCompatibleProvider)ConnectProvider(descriptor, baseUrl, secretRef, apiKey, circuits: circuits);
    }

    /// <summary>Familias con adapter nativo implementado (ADR-0005 §1); el resto falla tipado.</summary>
    public static bool IsProviderFamilySupported(ProviderFamily family) =>
        family is ProviderFamily.OpenAiChatCompatible or ProviderFamily.AnthropicMessages or ProviderFamily.OpenAIResponses;

    /// <summary>Conecta la implementación que corresponde a la familia declarada (ADR-0005, M5).</summary>
    public static IModelProvider ConnectProvider(ProviderDescriptor descriptor, string baseUrl,
        string secretRef, string apiKey, ISubscriptionCredentialSource? subscription = null,
        ProviderResilienceCatalog? circuits = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Family switch
        {
            ProviderFamily.OpenAiChatCompatible => ConnectOpenAiChatCompatible(descriptor, baseUrl, secretRef, apiKey, circuits),
            ProviderFamily.AnthropicMessages => ConnectAnthropicMessages(descriptor, baseUrl, secretRef, apiKey, circuits),
            ProviderFamily.OpenAIResponses => ConnectOpenAIResponses(descriptor, baseUrl, secretRef, apiKey, subscription, circuits),
            _ => throw new ProviderFamilyNotSupportedException(descriptor.Family),
        };
    }

    /// <summary>
    /// Perfil <c>api</c> de la Responses API. El perfil <c>codex</c> (suscripción ChatGPT) necesita la
    /// sesión OAuth de ChatGptSubscriptionAuthProvider y se conecta aparte (ADR-0011 §3.4).
    /// </summary>
    private static OpenAIResponsesProvider ConnectOpenAIResponses(ProviderDescriptor descriptor,
        string baseUrl, string secretRef, string apiKey, ISubscriptionCredentialSource? subscription,
        ProviderResilienceCatalog? circuits)
    {
        var codex = string.Equals(descriptor.Profile, "codex", StringComparison.Ordinal);
        if (codex && subscription is null)
            throw new ChatGptAuthException("notLoggedIn", "El perfil codex necesita la sesión de ChatGPT: ejecuta omni login chatgpt.");
        var configured = new ProviderDescriptor(descriptor.Id, descriptor.Family, baseUrl, descriptor.Auth,
            descriptor.SupportsJsonSchemaPerRequest, descriptor.SupportsGrammarPerRequest,
            descriptor.SupportsNativeToolCalls)
        { TrustedCertificatePath = descriptor.TrustedCertificatePath, Profile = descriptor.Profile, BillingMode = descriptor.BillingMode };
        HttpClient CreateClient() => new(CreateTlsHandler(baseUrl, descriptor.TrustedCertificatePath))
        {
            Timeout = System.TimeSpan.FromSeconds(600),
        };
        var secrets = new SimpleSecretProvider("OMNI_").With(secretRef, apiKey);
        return new OpenAIResponsesProvider(configured, secrets, CreateClient,
            new OpenAIResponsesOptions { Profile = codex ? ResponsesProfile.Codex : ResponsesProfile.Api,
                Resilience = new OpenAiProviderOptions { CircuitCatalog = circuits } },
            codex ? subscription : null);
    }

    private static AnthropicMessagesProvider ConnectAnthropicMessages(ProviderDescriptor descriptor,
        string baseUrl, string secretRef, string apiKey, ProviderResilienceCatalog? circuits)
    {
        var configured = new ProviderDescriptor(descriptor.Id, descriptor.Family, baseUrl, descriptor.Auth,
            descriptor.SupportsJsonSchemaPerRequest, descriptor.SupportsGrammarPerRequest,
            descriptor.SupportsNativeToolCalls)
        { TrustedCertificatePath = descriptor.TrustedCertificatePath, Profile = descriptor.Profile, BillingMode = descriptor.BillingMode };
        // Misma política TLS que el resto: validación estándar para hosts públicos como api.anthropic.com.
        HttpClient CreateClient() => new(CreateTlsHandler(baseUrl, descriptor.TrustedCertificatePath))
        {
            Timeout = System.TimeSpan.FromSeconds(600),
        };
        var secrets = new SimpleSecretProvider("OMNI_").With(secretRef, apiKey);
        return new AnthropicMessagesProvider(configured, secrets, CreateClient,
            new AnthropicProviderOptions { Resilience = new OpenAiProviderOptions { CircuitCatalog = circuits } });
    }

    private static OpenAiChatCompatibleProvider ConnectOpenAiChatCompatible(ProviderDescriptor descriptor,
        string baseUrl, string secretRef, string apiKey, ProviderResilienceCatalog? circuits)
    {
        var configured = new ProviderDescriptor(descriptor.Id, descriptor.Family, baseUrl, descriptor.Auth,
            descriptor.SupportsJsonSchemaPerRequest, descriptor.SupportsGrammarPerRequest,
            descriptor.SupportsNativeToolCalls)
        { TrustedCertificatePath = descriptor.TrustedCertificatePath, Profile = descriptor.Profile, BillingMode = descriptor.BillingMode };
        HttpClient CreateClient() => new(CreateTlsHandler(baseUrl, descriptor.TrustedCertificatePath))
        {
            Timeout = System.TimeSpan.FromSeconds(300),
        };
        var secrets = new SimpleSecretProvider("OMNI_").With(secretRef, apiKey);
        // StreamAsync owns and disposes each HttpClient returned by the factory; a shared instance
        // works for the first completion only, then every follow-up Turn fails as disposed.
        return new OpenAiChatCompatibleProvider(configured, secrets, CreateClient,
            new OpenAiProviderOptions { CircuitCatalog = circuits });
    }

    /// <summary>Handler HTTP con la política TLS de <see cref="ConnectLocalChatCompletions"/>.</summary>
    internal static HttpClientHandler CreateTlsHandler(string baseUrl, string? trustedCertificatePath)
    {
        var handler = new HttpClientHandler();
        if (trustedCertificatePath is not null && trustedCertificatePath.Length > 0)
        {
            var trustedRoot = LoadTrustedCertificate(trustedCertificatePath);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                ValidateAgainstTrustedRoot(certificate, errors, trustedRoot);
        }
        else if (IsPrivateHost(baseUrl))
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

    /// <summary>
    /// Describe cómo se validará el TLS de un provider, para <c>omni doctor</c>.
    /// </summary>
    public static string DescribeTls(string baseUrl, string? trustedCertificatePath)
    {
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "sin TLS (http)";
        }

        if (trustedCertificatePath is not null && trustedCertificatePath.Length > 0)
        {
            return File.Exists(trustedCertificatePath)
                ? "fijado a " + trustedCertificatePath
                : "ERROR: no existe caCertificate " + trustedCertificatePath;
        }

        return IsPrivateHost(baseUrl)
            ? "AVISO: IP privada sin validar el certificado; añade caCertificate al provider"
            : "validación estándar del sistema";
    }

    /// <summary>Carga un certificado de confianza en PEM o DER.</summary>
    internal static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadTrustedCertificate(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(text)
            : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(bytes);
    }

    /// <summary>
    /// Acepta el certificado del servidor solo si su cadena termina en <paramref name="trustedRoot"/>
    /// y no hay otros errores: un nombre que no coincide o un certificado ausente siguen fallando.
    /// </summary>
    internal static bool ValidateAgainstTrustedRoot(
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate,
        System.Net.Security.SslPolicyErrors errors,
        System.Security.Cryptography.X509Certificates.X509Certificate2 trustedRoot)
    {
        if (certificate is null)
        {
            return false;
        }

        if ((errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            != System.Net.Security.SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
        chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    /// <summary>
    /// True solo si el host de la URL es "localhost" o un LITERAL IP de loopback o de red privada:
    /// 127.0.0.0/8, ::1, 10/8, 172.16/12, 192.168/16, 100.64/10 (CGN) y fc00::/7 (ULA). Se parsea
    /// la dirección (no se compara por prefijo de texto), así que "10.evil.com" o
    /// "192.168.1.1.nip.io" no cuentan como privados. Los nombres DNS, incluido "*.local", nunca se
    /// consideran privados: pueden resolver a cualquier sitio. Con host público o nombre, la
    /// validación TLS es estricta (ADR-0038 §4).
    /// </summary>
    internal static bool IsPrivateHost(string baseUrl)
    {
        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (System.Net.IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        }

        // IPv6: solo ULA (fc00::/7).
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && (bytes[0] & 0xFE) == 0xFC;
    }
}
