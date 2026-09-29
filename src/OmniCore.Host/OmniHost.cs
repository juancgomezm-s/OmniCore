namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>
/// Composition root de OmniCore (ADR-0019 §3). La superficie pública es mínima: la fábrica del
/// host y del cliente in-process. El CLI se conecta vía IOmniClient con DTOs wire; incluso
/// in-process pasa por el protocolo para no acoplar la UI al Engine.
/// </summary>
public sealed class OmniHost
{
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
    /// un run (crash → resume) entre procesos distintos apuntando al mismo archivo.
    /// </summary>
    public static OmniServer OpenPersistentServer(string journalFile)
    {
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journalFile);
        var audit = new InMemoryAuditSink();
        return new OmniServer(store, codecs, audit, Path.Combine(Path.GetDirectoryName(journalFile)!,
            "lastsession.txt"));
    }

    /// <summary>
    /// Fábrica del Artifact Store content-addressed en disco (ADR-0001 §5, ADR-0041 §3).
    /// En M1 el pipeline no externaliza outputs grandes (los FakeTools son cortos); el CAS
    /// completo con GC y verify llega en M2. Se expone aquí para no quedar huérfano.
    /// </summary>
    public static IArtifactStore CreateArtifactStore(string dataDirectory) => new FileArtifactStore(dataDirectory);

    /// <summary>LocalModelHost cableado con el SystemProcessRuntime real (attach + managed, ADR-0011 §4).</summary>
    public static LocalModelHost CreateLocalModelHost() => new LocalModelHost(SystemProcessRuntime.Instance());

    /// <summary>Resolver de configuración por scope (ADR-0022 §25) con lookup vacío por defecto.</summary>
    public static OmniCore.Domain.ScopeResolver<string> CreateScopeResolver()
    {
        return new OmniCore.Domain.ScopeResolver<string>((scope, key) =>
        {
            // Lookup por defecto: sin capas configuradas, nada se resuelve → fallback.
            return null;
        });
    }

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
            credentials.Save(secretRef, environmentKey, cancellationToken);
            return environmentKey;
        }

        var stored = credentials.Load(secretRef, cancellationToken);
        return stored is not null && stored.Length > 0 ? stored : null;
    }

    /// <summary>Rutas de plataforma del usuario (datos y configuración fuera del repo, ADR-0038 §2).</summary>
    public static IPlatformPaths CreatePlatformPaths(string? dataDirectoryOverride = null) =>
        new DefaultPlatformPaths(dataDirectoryOverride);

    /// <summary>CredentialStore del usuario, en el directorio de datos de la plataforma (nunca en el repo).</summary>
    public static ICredentialStore CreateUserCredentialStore(IPlatformPaths paths) =>
        CreateCredentialStore(paths.DataDirectory);

    /// <summary>
    /// Directorio de runtime del workspace: <c>(data)/workspaces/&lt;WorkspaceId&gt;/</c> (journal, blobs;
    /// ADR-0039 §2). El id se deriva de la ruta canónica de la raíz.
    /// </summary>
    public static string WorkspaceDataDirectory(IPlatformPaths paths, string workspaceRoot) =>
        paths.WorkspaceDirectory(OmniCore.Domain.WorkspaceId.Of(Path.GetFullPath(workspaceRoot)).ToString());

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
        return new ModelPolicyService(new SqliteModelPolicyStore(paths.UserDatabasePath));
    }

    /// <summary>Token counter real (heurístico chars/4) para el runtime (no el Fake de tests).</summary>
    public static ITokenCounter CreateTokenCounter() => new HeuristicTokenCounter();

    /// <summary>
    /// Catálogo de tools Core completo (fake + filesystem.read/reference.resolve/plan.propose)
    /// con la frontera de paths y el PlanService. Es la composición real del runtime (no test).
    /// </summary>
    public static HostTools CreateHostTools() => HostTools.Default();

    public static HostTools CreateExplorerTools() => HostTools.Explorer();

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
    /// en el catálogo (techo de seguridad, además de no exponerla). Pendiente: cablear
    /// ModelCapabilityBoundary (ADR-0044) como capa adicional.
    /// </summary>
    public static IToolExecutor CreateExplorerExecutor(FakeCatalog catalog, string workspaceRoot)
    {
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, OmniCore.Domain.PermissionDecision>())
            .WithModeDefaults(OmniCore.Domain.RunMode.Plan);
        return ScriptedToolExecutor.WithWorkspace(catalog, policy, workspaceRoot);
    }

    /// <summary>
    /// Carga el ModelRegistry desde providers.yaml/models.yaml de un directorio de configuración.
    /// Si no hay archivos, devuelve el registro mínimo (provider local + local-worker). El runtime
    /// usa <see cref="LoadUserModelRegistry"/>; pasar aquí un directorio del repo permitiría que
    /// el repo redirija la API key a otro host.
    /// </summary>
    public static ModelRegistry LoadModelRegistry(string configDirectory)
    {
        var loader = new ConfigLoader();
        var providers = File.Exists(Path.Combine(configDirectory, "providers.yaml"))
            ? File.ReadAllText(Path.Combine(configDirectory, "providers.yaml"))
            : null;
        var models = File.Exists(Path.Combine(configDirectory, "models.yaml"))
            ? File.ReadAllText(Path.Combine(configDirectory, "models.yaml"))
            : null;
        return loader.BuildRegistry(providers, models);
    }

    /// <summary>
    /// Conecta un provider OpenAI Chat Completions local (M2). Acepta TLS self-signed SOLO si el
    /// host es localhost o un literal IP de loopback/red privada (<see cref="IsPrivateHost"/>):
    /// nunca para nombres DNS ni hosts públicos (ADR-0011 §4, ADR-0038 §4). La key se provee por entorno (p. ej. OMNI_QWEN_KEY).
    /// </summary>
    public static OpenAiChatCompatibleProvider ConnectLocalChatCompletions(string baseUrl, string modelId,
        string secretRef, string apiKey)
    {
        var handler = new HttpClientHandler();
        if (IsPrivateHost(baseUrl))
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        var http = new HttpClient(handler) { Timeout = System.TimeSpan.FromSeconds(300) };

        var secrets = new SimpleSecretProvider("OMNI_").With(secretRef, apiKey);
        var auth = (apiKey is not null && apiKey!.Length > 0)
            ? AuthConfig.ApiKey(secretRef)
            : AuthConfig.None();
        var descriptor = new ProviderDescriptor(
            "local", OmniCore.Domain.ProviderFamily.OpenAiChatCompatible, baseUrl,
            auth, false, false, false);
        return new OpenAiChatCompatibleProvider(descriptor, secrets, () => http);
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
