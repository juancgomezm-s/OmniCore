namespace OmniCore.Host;

using System.Net.Http.Headers;
using System.Net.Sockets;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

/// <summary>Endpoint con el que hay que hablar con el servidor local de un provider.</summary>
/// <param name="BaseUrl">URL base real; en modo managed incluye el puerto efímero.</param>
/// <param name="ApiKey">API key generada por el runtime (solo managed); nunca va a config ni al journal.</param>
/// <param name="Managed">True si OmniCore lanzó y supervisa el servidor.</param>
public sealed record LocalServerEndpoint(string BaseUrl, string? ApiKey, bool Managed);

/// <summary>Error tipado: el servidor local de un provider no está disponible (spec §71).</summary>
public sealed class LocalServerUnavailableException : InvalidOperationException
{
    public string ProviderId { get; }

    /// <summary><c>unreachable</c>, <c>startFailed</c>, <c>crashed</c> o <c>restartLimit</c>.</summary>
    public string Reason { get; }

    public string Endpoint { get; }

    public LocalizedText UserMessage => LocalizedText.Of("localServer." + Reason,
        ("provider", ProviderId), ("endpoint", Endpoint),
        ("count", LocalServerSupervisor.MaxManagedStarts.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public LocalServerUnavailableException(string providerId, string reason, string endpoint)
        : base("Local server unavailable (" + reason + "): " + providerId) =>
        (ProviderId, Reason, Endpoint) = (providerId, reason, endpoint);
}

/// <summary>
/// Servidores locales de los providers (ADR-0011 §4). <c>attach</c> comprueba que el servidor ya
/// levantado responde antes de gastar un Turn contra él; <c>managed</c> lo lanza con
/// <see cref="LocalModelHost"/> (puerto efímero y API key generados en runtime), lo reutiliza entre
/// Turns, lo relanza con límite y backoff si cae, y lo detiene al terminar el proceso.
/// </summary>
public sealed class LocalServerSupervisor : IDisposable
{
    /// <summary>Arranques de un servidor managed durante la vida del proceso (el inicial más sus reinicios).</summary>
    public const int MaxManagedStarts = 3;

    private sealed class ManagedEntry
    {
        internal required LocalModelHost Host { get; init; }
        internal int Starts { get; set; }
    }

    private readonly Func<IProcessRuntime> _processes;
    private readonly Func<string, bool> _reachable;
    private readonly Func<string, string?, bool> _ready;
    private readonly TimeSpan _restartBackoff;
    private readonly Dictionary<string, ManagedEntry> _managed = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _exitHooked;

    public LocalServerSupervisor(Func<IProcessRuntime>? processes = null, Func<string, bool>? reachable = null,
        Func<string, string?, bool>? ready = null, TimeSpan? restartBackoff = null)
    {
        _processes = processes ?? (() => OmniCore.Execution.SystemProcessRuntime.Instance());
        _reachable = reachable ?? (url => TcpReachable(url, TimeSpan.FromSeconds(2)));
        _ready = ready ?? ((url, key) => HttpReady(url, key, TimeSpan.FromSeconds(2)));
        _restartBackoff = restartBackoff ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Deja listo el servidor local de <paramref name="provider"/> y devuelve el endpoint real.
    /// Los providers sin servidor local (remotos) devuelven <paramref name="effectiveBaseUrl"/> intacta.
    /// Con un endpoint distinto del configurado (override explícito) se trata como attach.
    /// </summary>
    public LocalServerEndpoint Ensure(ProviderDescriptor provider, string effectiveBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.LocalHost is not { } config) return new LocalServerEndpoint(effectiveBaseUrl, null, false);
        if (config is { Mode: LocalHostMode.Attach, Declared: false })
            return new LocalServerEndpoint(effectiveBaseUrl, null, false);
        if (config.Mode == LocalHostMode.Attach
            || !string.Equals(effectiveBaseUrl, provider.BaseUrl, StringComparison.Ordinal))
        {
            if (!_reachable(effectiveBaseUrl))
                throw new LocalServerUnavailableException(provider.Id, "unreachable", effectiveBaseUrl);
            return new LocalServerEndpoint(effectiveBaseUrl, null, false);
        }

        lock (_gate)
        {
            if (!_managed.TryGetValue(provider.Id, out var entry))
                _managed[provider.Id] = entry = new ManagedEntry { Host = new LocalModelHost(_processes()) };
            if (!entry.Host.IsManagedRunning()) Start(provider, config, entry);
            return new LocalServerEndpoint(EndpointOf(entry.Host), entry.Host.GeneratedApiKeyValue(), true);
        }
    }

    private void Start(ProviderDescriptor provider, LocalHostConfig config, ManagedEntry entry)
    {
        if (entry.Starts >= MaxManagedStarts)
            throw new LocalServerUnavailableException(provider.Id, "restartLimit", provider.BaseUrl);
        // Reinicio tras una caída: espera creciente para no martillear un servidor que no arranca.
        if (entry.Starts > 0 && _restartBackoff > TimeSpan.Zero) Thread.Sleep(_restartBackoff * entry.Starts);
        entry.Starts++;
        if (!_exitHooked)
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            _exitHooked = true;
        }

        var spec = new ManagedServerSpec(config.Executable!, config.Args ?? [], config.WorkingDirectory ?? "",
            config.FixedPort);
        var host = entry.Host;
        var status = host.StartManaged(spec,
            () => _ready(EndpointOf(host), host.GeneratedApiKeyValue()), config.EffectiveReadinessTimeout);
        if (status == LocalServerStatus.Ready)
        {
            SecretRedactorRegistry.Register(host.GeneratedApiKeyValue());
            return;
        }

        throw new LocalServerUnavailableException(provider.Id,
            status == LocalServerStatus.Crashed ? "crashed" : "startFailed", provider.BaseUrl);
    }

    private static string EndpointOf(LocalModelHost host) =>
        "http://127.0.0.1:" + host.ChosenPort().ToString(System.Globalization.CultureInfo.InvariantCulture) + "/v1";

    public bool IsManagedRunning(string? providerId)
    {
        if (providerId is null) return false;
        lock (_gate) return _managed.TryGetValue(providerId, out var entry) && entry.Host.IsManagedRunning();
    }

    /// <summary>Detiene el servidor managed de un provider (kill-tree) y borra su secreto.</summary>
    public void Stop(string providerId)
    {
        lock (_gate)
        {
            if (_managed.TryGetValue(providerId, out var entry)) entry.Host.StopManaged();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _managed.Values) entry.Host.StopManaged();
            if (_exitHooked) AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            _exitHooked = false;
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try { Dispose(); } catch (Exception) { /* el proceso ya termina */ }
    }

    /// <summary>Attach: ¿acepta conexiones TCP el endpoint? No envía ninguna petición HTTP.</summary>
    public static bool TcpReachable(string baseUrl, TimeSpan timeout)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(uri.Host, uri.Port).Wait(timeout) && client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Managed: readiness real, <c>GET /models</c> con la API key del proceso (ADR-0011 §4).</summary>
    public static bool HttpReady(string baseUrl, string? apiKey, TimeSpan timeout)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = client.Send(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }
}
