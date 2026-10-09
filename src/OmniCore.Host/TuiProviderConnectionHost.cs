namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Contrato del Host para el menú de conexiones de providers (ADR-0030 §3): el renderer solo
/// presenta; toda operación y todo acceso a credenciales vive aquí, sobre
/// <see cref="ProviderConnectionService"/> (y, de forma aditiva, sobre el backend de cuentas).
/// Las filas y resultados nunca contienen secretos (ADR-0018): solo presencia, método, estado
/// medido y pista enmascarada, con las marcas de tiempo de la última medición.
/// </summary>
public interface ITuiProviderConnectionHost
{
    /// <summary>Estado de todas las conexiones declaradas en la configuración User.</summary>
    IReadOnlyList<ProviderConnectionMenuRow> List(CancellationToken cancellationToken);

    /// <summary>Guarda (y opcionalmente valida contra la Models API) la API key de Anthropic.</summary>
    Task<ProviderConnectionMenuResult> ConnectAsync(string apiKey, bool validate, CancellationToken cancellationToken);

    /// <summary>Guarda la API key en el provider exacto seleccionado por el usuario.</summary>
    Task<ProviderConnectionMenuResult> ConnectAsync(string providerId, string apiKey, bool validate,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This provider connection host does not support exact provider selection.");

    /// <summary>Mide la conexión guardada del provider contra su API pública (sin inferencia).</summary>
    Task<ProviderConnectionMenuResult> TestAsync(string providerId, CancellationToken cancellationToken);

    /// <summary>Descubre los modelos actuales del provider y los registra en la configuración User.</summary>
    Task<ProviderConnectionMenuResult> DiscoverAsync(string providerId, CancellationToken cancellationToken);

    /// <summary>Borra la credencial guardada del provider (operación local).</summary>
    void Disconnect(string providerId, CancellationToken cancellationToken);
}

/// <summary>
/// Fila del menú de conexiones: el estado del backend de conexiones más las marcas de tiempo
/// de la última medición (verificación de la credencial y descubrimiento de modelos). Sin
/// secretos: la pista de credencial ya viene enmascarada por el backend (ADR-0018).
/// </summary>
public sealed record ProviderConnectionMenuRow(
    string ProviderId,
    ProviderConnectionMethod Method,
    ProviderConnectionState State,
    BillingMode BillingMode,
    string? Detail,
    LocalizedText? Notice,
    bool CanConnect,
    bool CanTest,
    bool CanDisconnect,
    bool CanDiscoverModels,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? DiscoveredAt);

/// <summary>Resultado de una acción del menú de conexiones (solo estado y detalle; nunca la credencial).</summary>
public sealed record ProviderConnectionMenuResult(
    ProviderConnectionState State,
    string? Detail,
    LocalizedText? Notice,
    DateTimeOffset? ValidatedAt = null,
    DateTimeOffset? DiscoveredAt = null,
    int? RegisteredModels = null,
    bool Truncated = false,
    int QuotaWindows = 0);

/// <summary>
/// Fachada del Host que usa el renderer del menú: envuelve el contrato público de conexiones
/// y añade las marcas de tiempo de medición. No implementa autenticación: la credencial entra
/// por el backend cifrado del Host (<c>ProviderConnections.cs</c>) y jamás se expone al
/// renderer, ni a logs ni a eventos.
/// </summary>
public sealed class TuiProviderConnectionHost : ITuiProviderConnectionHost
{
    private readonly ProviderConnectionService _connections;
    private readonly IPlatformPaths _paths;

    public TuiProviderConnectionHost(ProviderConnectionService? connections = null, IPlatformPaths? paths = null)
    {
        _paths = paths ?? OmniHost.CreatePlatformPaths();
        _connections = connections ?? new ProviderConnectionService(paths: _paths);
    }

    /// <summary>Instancia de producción: rutas y almacén de credenciales reales del usuario.</summary>
    public static ITuiProviderConnectionHost Create() => new TuiProviderConnectionHost();

    public IReadOnlyList<ProviderConnectionMenuRow> List(CancellationToken cancellationToken)
    {
        var metadata = ProviderConnectionMetadataStore.ReadAll(_paths.DataDirectory, cancellationToken);
        return _connections.List(cancellationToken).Select(row =>
        {
            var entry = metadata.TryGetValue(row.ProviderId, out var found)
                ? found : new ProviderConnectionMetadata(null, null, null, null);
            return new ProviderConnectionMenuRow(row.ProviderId, row.Method, row.State, row.BillingMode,
                row.Detail, row.Notice, row.CanConnect, row.CanTest, row.CanDisconnect, row.CanDiscoverModels,
                entry.ValidatedAt, entry.DiscoveredAt);
        }).ToArray();
    }

    public async Task<ProviderConnectionMenuResult> ConnectAsync(string apiKey, bool validate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = _connections.List(cancellationToken).FirstOrDefault(row => row.CanConnect)?.ProviderId
            ?? ProviderConnectionService.DefaultAnthropicProviderId;
        var result = await _connections.ConnectAnthropicAsync(providerId, apiKey, validate, cancellationToken)
            .ConfigureAwait(false);
        return WithTimestamps(result.State, result.Detail, result.Notice, providerId, cancellationToken);
    }

    public async Task<ProviderConnectionMenuResult> ConnectAsync(string providerId, string apiKey, bool validate,
        CancellationToken cancellationToken)
    {
        var result = await _connections.ConnectAnthropicAsync(providerId, apiKey, validate, cancellationToken)
            .ConfigureAwait(false);
        return WithTimestamps(result.State, result.Detail, result.Notice, providerId, cancellationToken);
    }

    public async Task<ProviderConnectionMenuResult> TestAsync(string providerId, CancellationToken cancellationToken)
    {
        var result = await _connections.TestAsync(providerId, cancellationToken).ConfigureAwait(false);
        return WithTimestamps(result.State, result.Detail, result.Notice, providerId, cancellationToken);
    }

    public async Task<ProviderConnectionMenuResult> DiscoverAsync(string providerId, CancellationToken cancellationToken)
    {
        var result = await _connections.DiscoverAnthropicModelsAsync(providerId, cancellationToken)
            .ConfigureAwait(false);
        var metadata = ProviderConnectionMetadataStore.Read(_paths.DataDirectory, result.ProviderId, cancellationToken);
        return new(ProviderConnectionState.Connected, null,
            result.Truncated ? LocalizedText.Of("providers.notice.discoveryTruncated") : null,
            metadata.ValidatedAt, metadata.DiscoveredAt,
            result.RegisteredModels, result.Truncated, result.Quota.Count);
    }

    public void Disconnect(string providerId, CancellationToken cancellationToken) =>
        _connections.Disconnect(providerId, cancellationToken);

    /// <summary>
    /// La marca de tiempo solo se muestra con el estado que la originó: <see cref="ProviderConnectionState.Connected"/>
    /// muestra la verificación exitosa; un estado no concluyente o inválido nunca la reutiliza
    /// como si midiera ahora.
    /// </summary>
    private ProviderConnectionMenuResult WithTimestamps(ProviderConnectionState state, string? detail,
        LocalizedText? notice, string? providerId, CancellationToken cancellationToken)
    {
        var id = providerId
            ?? _connections.List(cancellationToken).FirstOrDefault(row => row.CanConnect)?.ProviderId
            ?? ProviderConnectionService.DefaultAnthropicProviderId;
        var metadata = ProviderConnectionMetadataStore.Read(_paths.DataDirectory, id, cancellationToken);
        return state == ProviderConnectionState.Connected
            ? new(state, detail, notice, metadata.ValidatedAt, metadata.DiscoveredAt)
            : new(state, detail, notice);
    }
}
