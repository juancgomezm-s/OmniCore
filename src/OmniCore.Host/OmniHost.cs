namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;

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

    /// <summary>
    /// Catálogo de tools Core completo (fake + filesystem.read/reference.resolve/plan.propose)
    /// con la frontera de paths y el PlanService. Es la composición real del runtime (no test).
    /// </summary>
    public static HostTools CreateHostTools() => HostTools.Default();

    /// <summary>
    /// Executor del pipeline real de tools + permisos para el Turn de Explorer: usa el catálogo
    /// completo y la política con defaults por modo (Act). Ask sin cliente → Deny (los turnos
    /// no-interactivos del CLI no pueden aprobar; solo el modo char abre InteractionRequest).
    /// </summary>
    /// <summary>
    /// Executor del pipeline real de tools + permisos para el Turn de Explorer: usa el catálogo
    /// completo y la política con defaults por modo (Act). Ask sin cliente → Deny (los turnos
    /// no-interactivos del CLI no pueden aprobar; solo el modo char abre InteractionRequest).
    /// El workspaceRoot fija la frontera de paths del turno (el cwd del proceso).
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
    /// Carga el ModelRegistry desde providers.yaml/models.yaml de un directorio de configuración.
    /// Si no hay archivos, devuelve el registro mínimo (provider local + local-worker).
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
    /// Conecta un provider OpenAI Chat Completions local (M2). Acepta hosts con TLS self-signed
    /// SIEMPRE QUE sean loopback o IP privada (Ver0+): nunca se relaja la validación para hosts
    /// públicos (ADR-0011 §4, ADR-0038 §4). La key se provee por entorno (p. ej. OMNI_QWEN_KEY).
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
    /// True para loopback (127.0.0.0/8, ::1, localhost, *.local) y rangos privados RFC1918
    /// (10/8, 172.16/12, 192.168/16) + CGN/ULA (100.64/10, fd00::/8). Los hosts públicos
    /// nunca se marcan privados → validación TLS estricta (ADR-0038 §4).
    /// </summary>
    internal static bool IsPrivateHost(string baseUrl)
    {
        var lower = baseUrl.ToLowerInvariant();
        var host = lower;
        var scheme = lower.IndexOf("://");
        if (scheme >= 0)
        {
            host = lower.Substring(scheme + 3);
        }

        var slash = host.IndexOf('/');
        if (slash >= 0)
        {
            host = host.Substring(0, slash);
        }

        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon)
        {
            // host:port (IPv4); para IPv6 la última ':' separa puerto igualmente
            if (host.Count(':') == 1)
            {
                host = host.Substring(0, colon);
            }
        }

        host = host.Trim('[', ']');
        return host == "localhost"
            || host == "::1"
            || host.StartsWith("127.")
            || host.StartsWith("10.")
            || host.StartsWith("192.168.")
            || host.StartsWith("172.16.") || host.StartsWith("172.17.") || host.StartsWith("172.18.")
            || host.StartsWith("172.19.") || host.StartsWith("172.2.")
            || (host.StartsWith("172.3") && (host.Length > 5) && host[5] < '2')
            || host.StartsWith("100.64.") || host.StartsWith("100.65.")
            || host.StartsWith("fd00:") || host.StartsWith("fe80:")
            || host.EndsWith(".local");
    }
}