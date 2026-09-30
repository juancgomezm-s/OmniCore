namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Domain;
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
    /// un run (crash → resume) entre procesos distintos apuntando al mismo archivo.
    /// </summary>
    public static OmniServer OpenPersistentServer(string journalFile)
    {
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journalFile);
        var audit = new InMemoryAuditSink();
        var workspaceData = Path.GetDirectoryName(journalFile)!;
        return new OmniServer(store, codecs, audit, Path.Combine(workspaceData, "lastsession.txt"),
            CreateArtifactStore(workspaceData));
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
            SecretRedactorRegistry.Register(environmentKey);
            credentials.Save(secretRef, environmentKey, cancellationToken);
            return environmentKey;
        }

        var stored = credentials.Load(secretRef, cancellationToken);
        if (stored is not null && stored.Length > 0) SecretRedactorRegistry.Register(stored);
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
        paths.WorkspaceDirectory(OmniCore.Domain.WorkspaceId.Of(
            ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot)).ToString());

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

    /// <summary>Token counter estimado por defecto, o exacto para providers llama.cpp declarados en config.</summary>
    public static ITokenCounter CreateTokenCounter() => new HeuristicTokenCounter();

    public static ITokenCounter CreateTokenCounter(ProviderDescriptor? provider, string? declaredKind,
        string? apiKey, Func<HttpClient>? httpFactory = null, string? baseUrlOverride = null)
    {
        var fallback = new HeuristicTokenCounter();
        if (provider is null || declaredKind is not ("llamaCpp" or "ikLlama")) return fallback;
        return new LlamaCppTokenCounter(GetLlamaCppTokenizeBaseUrl(baseUrlOverride ?? provider.BaseUrl),
            new OmniCore.Domain.TokenizerId("llama.cpp:" + provider.Id), fallback, httpFactory,
            () => apiKey);
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
        RunId? runId = null)
    {
        var policy = CreateGrantAwarePolicy(OmniCore.Domain.RunMode.Plan, projectRestrictions, workspaceRoot, runId);
        return ScriptedToolExecutor.WithWorkspace(catalog, policy, workspaceRoot, boundary);
    }

    public static ScriptedPermissionPolicy CreateProjectRestrictionPolicy(OmniCore.Domain.RunMode mode,
        IReadOnlyDictionary<string, string>? restrictions) =>
        new ScriptedPermissionPolicy(ParseProjectRestrictions(restrictions)).WithModeDefaults(mode);

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
    public static HostTools CreateActTools() =>
        new(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false, includeMutationTools: true);

    /// <summary>
    /// Executor de <c>omni act</c>: capa de modo ACT (escrituras dentro del workspace permitidas por
    /// el perfil autónomo, ADR-0037 §4) más la frontera de capacidad del modelo (ADR-0044 §5), que
    /// deja ObserveOnly a un modelo sin política efectiva. La frontera restringe; el Permission
    /// Engine sigue siendo la única autoridad (INV-018).
    /// </summary>
    public static IToolExecutor CreateActExecutor(FakeCatalog catalog, string workspaceRoot,
        ModelCapabilityBoundary boundary, IReadOnlyDictionary<string, string>? projectRestrictions = null,
        RunId? runId = null)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var policy = CreateGrantAwarePolicy(OmniCore.Domain.RunMode.Act, projectRestrictions, workspaceRoot, runId);
        return ScriptedToolExecutor.WithWorkspace(catalog, policy, workspaceRoot, boundary);
    }

    /// <summary>Política de permisos con grants aislados por WorkspaceId y auditados en user data.</summary>
    public static ScriptedPermissionPolicy CreateGrantAwarePolicy(
        OmniCore.Domain.RunMode mode, IReadOnlyDictionary<string, string>? restrictions, string workspaceRoot,
        RunId? runId, IAuditSink? audit = null)
    {
        var paths = CreatePlatformPaths();
        var workspace = OmniCore.Domain.WorkspaceId.Of(
            ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot));
        var store = new OmniCore.Security.FilePermissionGrantStore(
            WorkspaceDataDirectory(paths, workspaceRoot), audit ?? new FileAuditSink(paths.DataDirectory));
        return CreateProjectRestrictionPolicy(mode, restrictions).WithGrantStore(store, workspace, runId);
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

    public static LoadedUserConfiguration LoadUserConfiguration(string configDirectory)
    {
        var providersPath = Path.Combine(configDirectory, "providers.yaml");
        var modelsPath = Path.Combine(configDirectory, "models.yaml");
        return new ConfigLoader().Load(File.Exists(providersPath) ? File.ReadAllText(providersPath) : null,
            File.Exists(modelsPath) ? File.ReadAllText(modelsPath) : null);
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
        string secretRef, string apiKey, string? trustedCertificatePath = null)
    {
        var http = new HttpClient(CreateTlsHandler(baseUrl, trustedCertificatePath))
        {
            Timeout = System.TimeSpan.FromSeconds(300),
        };

        var secrets = new SimpleSecretProvider("OMNI_").With(secretRef, apiKey);
        var auth = (apiKey is not null && apiKey!.Length > 0)
            ? AuthConfig.ApiKey(secretRef)
            : AuthConfig.None();
        var descriptor = new ProviderDescriptor(
            "local", OmniCore.Domain.ProviderFamily.OpenAiChatCompatible, baseUrl,
            auth, false, false, false);
        return new OpenAiChatCompatibleProvider(descriptor, secrets, () => http);
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
