namespace OmniCore.Host;

using System.Globalization;
using System.Numerics;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using OmniCore.Protocol;
using YamlDotNet.RepresentationModel;

/// <summary>
/// User-scoped OpenRouter connection, catalog discovery and credential lifecycle. API keys
/// remain in the platform credential store; user YAML contains only the provider reference
/// and explicitly discovered model facts.
/// </summary>
public sealed class OpenRouterConnectionService
{
    public const string DefaultProviderId = "openrouter";
    public const string DefaultSecretRef = "openrouter";
    private const int DiscoveryPageSize = 100;
    private const int DiscoveryMaximumModels = 1000;
    private const int MaximumCachedCatalogBytes = 16 * 1024 * 1024;
    private readonly ICredentialStore _credentials;
    private readonly IPlatformPaths _paths;
    private readonly OpenRouterApi _api;
    private readonly Func<DateTimeOffset> _now;

    public OpenRouterConnectionService(ICredentialStore? credentials = null, IPlatformPaths? paths = null,
        Func<HttpClient>? httpFactory = null, Func<DateTimeOffset>? now = null)
    {
        _paths = paths ?? OmniHost.CreatePlatformPaths();
        _credentials = credentials ?? OmniHost.CreateUserCredentialStore(_paths);
        _api = new OpenRouterApi(httpFactory, now);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<ProviderConnectionStatus> List(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        var found = loaded.Registry.Providers().Where(IsOpenRouterProvider).ToArray();
        var rows = new List<ProviderConnectionStatus>();
        foreach (var provider in found)
        {
            var present = CredentialPresent(provider.Auth.SecretRef!, cancellationToken);
            var metadata = ProviderConnectionMetadataStore.Read(_paths.DataDirectory, provider.Id, cancellationToken);
            var state = !present ? ProviderConnectionState.NotConfigured : metadata.ValidationState switch
            {
                "valid" => ProviderConnectionState.Connected,
                "invalid" => ProviderConnectionState.Invalid,
                "unreachable" => ProviderConnectionState.Unreachable,
                _ => ProviderConnectionState.Unknown,
            };
            rows.Add(new(provider.Id, ProviderConnectionMethod.ApiKey, state, provider.BillingMode,
                metadata.MaskedKey, state switch
                {
                    ProviderConnectionState.NotConfigured => LocalizedText.Of("providers.notice.missingKey", "ref", provider.Auth.SecretRef!),
                    ProviderConnectionState.Invalid => LocalizedText.Of("providers.notice.storedKeyRejected"),
                    ProviderConnectionState.Unreachable => LocalizedText.Of("providers.notice.storedKeyUnreachable"),
                    ProviderConnectionState.Unknown when present => LocalizedText.Of("providers.notice.storedKeyUnverified"),
                    _ => null,
                }, true, present, present, present));
        }
        if (found.Length == 0)
        {
            var id = FreeProviderId(loaded.Registry.Providers().Select(provider => provider.Id));
            rows.Add(new(id, ProviderConnectionMethod.ApiKey, ProviderConnectionState.NotConfigured,
                BillingMode.MeteredCurrency, null, null, true, false, false, false));
        }
        return rows;
    }

    public bool Owns(string providerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OmniHost.LoadUserConfiguration(_paths).Registry.Provider(providerId) is { } provider
            && IsOpenRouterProvider(provider);
    }

    public async Task<ProviderConnectResult> ConnectAsync(string providerId, string apiKey, bool validate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (apiKey ?? string.Empty).Trim();
        if (key.Length < Secret.MinimumLength)
            throw new ProviderConnectionException("tooShort", "OpenRouter API key is too short.");
        var target = OpenRouterRegistration.ResolveProvider(_paths.ConfigDirectory, providerId);
        SecretRedactorRegistry.Register(key);
        ProviderTestResult? validation = null;
        if (validate)
        {
            validation = await _api.TestKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (validation.State == ProviderConnectionState.Invalid)
                throw new ProviderConnectionException("invalid", "OpenRouter rejected the API key; nothing was saved.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var originalSecret = _credentials.Load(target.SecretRef, cancellationToken);
        var priorMetadata = ProviderConnectionMetadataStore.ReadAll(_paths.DataDirectory, cancellationToken);
        var hadMetadata = priorMetadata.TryGetValue(providerId, out var previousMetadata);
        previousMetadata ??= new(null, null, null, null);
        var generation = Guid.NewGuid().ToString("N");
        try
        {
            _credentials.Save(target.SecretRef, key, cancellationToken);
            ProviderConnectionMetadataStore.Write(_paths.DataDirectory, providerId,
                new(ProviderConnectionService.Mask(key), State(validation?.State),
                    validation?.State == ProviderConnectionState.Connected ? _now() : previousMetadata.ValidatedAt,
                    previousMetadata.DiscoveredAt, generation), cancellationToken);
            // User configuration is the final commit point. All credential/metadata changes above
            // are rolled back if the exact provider configuration cannot be committed.
            target = OpenRouterRegistration.EnsureProvider(_paths.ConfigDirectory, providerId, target.SecretRef);
        }
        catch
        {
            try
            {
                if (originalSecret is null) _credentials.Delete(target.SecretRef, CancellationToken.None);
                else _credentials.Save(target.SecretRef, originalSecret, CancellationToken.None);
                if (hadMetadata)
                    ProviderConnectionMetadataStore.Write(_paths.DataDirectory, providerId, previousMetadata, CancellationToken.None);
                else ProviderConnectionMetadataStore.Clear(_paths.DataDirectory, providerId, CancellationToken.None);
            }
            catch (Exception rollbackFailure) when (rollbackFailure is IOException or UnauthorizedAccessException
                or InvalidOperationException)
            {
                throw new AggregateException("OpenRouter connection failed and rollback could not restore local state.", rollbackFailure);
            }
            throw;
        }
        if (!string.IsNullOrEmpty(previousMetadata.CredentialGeneration))
            InvalidateCaches(providerId, target.SecretRef, previousMetadata.CredentialGeneration, CancellationToken.None);
        return new(validation?.State ?? ProviderConnectionState.Unknown, ProviderConnectionService.Mask(key),
            validation?.Notice ?? LocalizedText.Of("providers.notice.savedUnverified"));
    }

    public async Task<ProviderTestResult> TestAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = Resolve(providerId, cancellationToken);
        var key = _credentials.Load(provider.Auth.SecretRef!, cancellationToken)
            ?? throw new ProviderConnectionException("notConfigured", "Connect an OpenRouter API key first.");
        var result = await _api.TestKeyAsync(key, cancellationToken).ConfigureAwait(false);
        var old = ProviderConnectionMetadataStore.Read(_paths.DataDirectory, providerId, cancellationToken);
        ProviderConnectionMetadataStore.Write(_paths.DataDirectory, providerId,
            new(ProviderConnectionService.Mask(key), State(result.State),
                result.State == ProviderConnectionState.Connected ? _now() : old.ValidatedAt,
                old.DiscoveredAt, old.CredentialGeneration), cancellationToken);
        return result;
    }

    public async Task<OpenRouterDiscoveryResult> DiscoverAsync(string providerId, CancellationToken cancellationToken)
    {
        var page = await RefreshCatalogAsync(providerId, string.Empty, 0, 100, cancellationToken).ConfigureAwait(false);
        if (page.Stale || page.Error is not null)
            throw new ProviderConnectionException("unreachable", "OpenRouter catalog refresh failed; the stale catalog was left unchanged.");
        var provider = Resolve(providerId, cancellationToken);
        var generation = CredentialGeneration(providerId, provider.Auth.SecretRef!, cancellationToken);
        var catalog = ReadCatalog(providerId, provider.Auth.SecretRef!, generation, cancellationToken)?.Models ?? [];
        var registered = OpenRouterRegistration.RegisterModels(_paths.ConfigDirectory, providerId, catalog);
        return new(providerId, registered, page.Truncated);
    }

    public OpenRouterCatalogPage BrowseCatalog(string providerId, string? search, int offset, int limit,
        CancellationToken cancellationToken)
    {
        _ = Resolve(providerId, cancellationToken);
        if (offset < 0 || limit is < 1 or > 100 || search?.Length > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "OpenRouter catalog view is outside its bounded range.");
        var provider = Resolve(providerId, cancellationToken);
        var generation = CredentialGeneration(providerId, provider.Auth.SecretRef!, cancellationToken);
        var cache = ReadCatalog(providerId, provider.Auth.SecretRef!, generation, cancellationToken);
        if (cache is null) return new([], 0, null, false, false, "not_loaded", ProviderId: providerId);
        var query = (search ?? string.Empty).Trim();
        var filtered = cache.Models.Where(model => query.Length == 0
                || model.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || model.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || model.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var page = filtered.Skip(offset).Take(limit).ToArray();
        var more = offset + page.Length < filtered.Length;
        var stale = cache.LastError is not null || cache.FetchedAt < _now().AddDays(-7);
        return new(page, filtered.Length, more ? offset + page.Length : null, cache.Truncated,
            stale, cache.LastError, cache.FetchedAt, providerId);
    }

    public async Task<OpenRouterCatalogPage> RefreshCatalogAsync(string providerId, string? search, int offset,
        int limit, CancellationToken cancellationToken)
    {
        var provider = Resolve(providerId, cancellationToken);
        var key = _credentials.Load(provider.Auth.SecretRef!, cancellationToken)
            ?? throw new ProviderConnectionException("notConfigured", "Connect an OpenRouter API key first.");
        var generation = CredentialGeneration(providerId, provider.Auth.SecretRef!, cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var all = new List<OpenRouterModel>(DiscoveryMaximumModels);
            var bytes = 0;
            var pageOffset = 0;
            var truncated = false;
            while (all.Count < DiscoveryMaximumModels)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var page = await _api.ListModelsAsync(key, pageOffset,
                    Math.Min(DiscoveryPageSize, DiscoveryMaximumModels - all.Count), deadline.Token).ConfigureAwait(false);
                foreach (var model in page.Models)
                {
                    bytes = checked(bytes + System.Text.Encoding.UTF8.GetByteCount(model.RawJson));
                    if (bytes > MaximumCachedCatalogBytes) { truncated = true; break; }
                    all.Add(model);
                }
                if (truncated || !page.HasMore || page.Models.Count == 0) break;
                pageOffset += page.Models.Count;
            }
            if (all.Count == DiscoveryMaximumModels)
            {
                var probe = await _api.ListModelsAsync(key, all.Count, 1, deadline.Token).ConfigureAwait(false);
                truncated |= probe.Models.Count > 0;
            }
            var current = Resolve(providerId, cancellationToken);
            var currentGeneration = CredentialGeneration(providerId, current.Auth.SecretRef!, cancellationToken);
            if (!string.Equals(current.Auth.SecretRef, provider.Auth.SecretRef, StringComparison.Ordinal)
                || !string.Equals(currentGeneration, generation, StringComparison.Ordinal))
                throw new ProviderConnectionException("configurationChanged", "OpenRouter credential changed during catalog refresh.");
            WriteCatalog(providerId, provider.Auth.SecretRef!, generation, new(all, _now(), truncated, _now(), null), cancellationToken);
            var metadata = ProviderConnectionMetadataStore.Read(_paths.DataDirectory, providerId, cancellationToken);
            ProviderConnectionMetadataStore.Write(_paths.DataDirectory, providerId, metadata with { DiscoveredAt = _now() }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return StaleCatalogPage(providerId, search, offset, limit, "refresh_timeout", cancellationToken);
        }
        catch (Exception ex) when (ex is ProviderConnectionException or IOException or UnauthorizedAccessException)
        {
            return StaleCatalogPage(providerId, search, offset, limit, "refresh_failed", cancellationToken);
        }
        return BrowseCatalog(providerId, search, offset, limit, cancellationToken);
    }

    public int RegisterCatalogModels(string providerId, IReadOnlyList<string> modelIds,
        CancellationToken cancellationToken)
    {
        _ = Resolve(providerId, cancellationToken);
        if (modelIds.Count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(modelIds));
        var provider = Resolve(providerId, cancellationToken);
        var generation = CredentialGeneration(providerId, provider.Auth.SecretRef!, cancellationToken);
        var cache = ReadCatalog(providerId, provider.Auth.SecretRef!, generation, cancellationToken)
            ?? throw new ProviderConnectionException("catalogInvalid", "Refresh the model catalog before registering models.");
        var requested = modelIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Count != modelIds.Count) throw new ProviderConnectionException("catalogInvalid", "Duplicate model id.");
        var models = cache.Models.Where(model => requested.Contains(model.Id)).ToArray();
        if (models.Length != requested.Count) throw new ProviderConnectionException("catalogInvalid", "A selected model is not in the current catalog.");
        return OpenRouterRegistration.RegisterModels(_paths.ConfigDirectory, providerId, models);
    }

    private OpenRouterCatalogPage StaleCatalogPage(string providerId, string? search, int offset, int limit,
        string error, CancellationToken cancellationToken)
    {
        var old = BrowseCatalog(providerId, search, offset, limit, cancellationToken);
        var provider = Resolve(providerId, cancellationToken);
        var generation = CredentialGeneration(providerId, provider.Auth.SecretRef!, cancellationToken);
        var cached = ReadCatalog(providerId, provider.Auth.SecretRef!, generation, cancellationToken);
        if (cached is not null)
            WriteCatalog(providerId, provider.Auth.SecretRef!, generation,
                cached with { LastAttemptAt = _now(), LastError = error }, cancellationToken);
        return old with { Stale = true, Error = error, ProviderId = providerId };
    }

    private sealed record CatalogCache(IReadOnlyList<OpenRouterModel> Models, DateTimeOffset FetchedAt,
        bool Truncated, DateTimeOffset LastAttemptAt, string? LastError);

    private void WriteCatalog(string providerId, string secretRef, string generation, CatalogCache cache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_paths.DataDirectory);
        var path = CachePath("model-catalog", providerId, secretRef, generation);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = File.Create(temp))
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteString("providerId", providerId);
                writer.WriteString("secretRef", secretRef); writer.WriteString("credentialGeneration", generation);
                writer.WriteString("fetchedAt", cache.FetchedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("lastAttemptAt", cache.LastAttemptAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("lastError", cache.LastError);
                writer.WriteBoolean("truncated", cache.Truncated); writer.WriteStartArray("models");
                foreach (var model in cache.Models) writer.WriteRawValue(model.RawJson, skipInputValidation: false);
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            if (new FileInfo(temp).Length > MaximumCachedCatalogBytes + 64 * 1024)
                throw new ProviderConnectionException("catalogTooLarge", "OpenRouter catalog cache exceeds its size limit.");
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private CatalogCache? ReadCatalog(string providerId, string secretRef, string generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(generation)) return null;
        var path = CachePath("model-catalog", providerId, secretRef, generation);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumCachedCatalogBytes + 64 * 1024) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path), new() { MaxDepth = 48 });
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || root.GetProperty("providerId").GetString() != providerId
                || root.GetProperty("secretRef").GetString() != secretRef
                || root.GetProperty("credentialGeneration").GetString() != generation
                || !DateTimeOffset.TryParse(root.GetProperty("fetchedAt").GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var fetched)) return null;
            DateTimeOffset.TryParse(root.GetProperty("lastAttemptAt").GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var attempted);
            var error = root.TryGetProperty("lastError", out var errorNode)
                && errorNode.ValueKind == System.Text.Json.JsonValueKind.String ? errorNode.GetString() : null;
            var truncated = root.TryGetProperty("truncated", out var tr) && tr.ValueKind == System.Text.Json.JsonValueKind.True;
            var models = root.GetProperty("models").EnumerateArray().Select(OpenRouterApi.ParseModel).ToArray();
            return models.Length <= DiscoveryMaximumModels ? new(models, fetched, truncated, attempted, error) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        { return null; }
    }

    public OpenRouterApi Api => _api;

    public async Task<OpenRouterCreditSnapshot> ReadCreditsAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = Resolve(providerId, cancellationToken);
        var key = _credentials.Load(provider.Auth.SecretRef!, cancellationToken)
            ?? throw new ProviderConnectionException("notConfigured", "Connect an OpenRouter API key first.");
        var generation = CredentialGeneration(providerId, provider.Auth.SecretRef!, cancellationToken);
        try
        {
            var current = await _api.ReadCreditsAsync(key, cancellationToken).ConfigureAwait(false);
            var currentProvider = Resolve(providerId, cancellationToken);
            var currentGeneration = CredentialGeneration(providerId, currentProvider.Auth.SecretRef!, cancellationToken);
            if (!string.Equals(currentProvider.Auth.SecretRef, provider.Auth.SecretRef, StringComparison.Ordinal)
                || !string.Equals(currentGeneration, generation, StringComparison.Ordinal))
                throw new ProviderConnectionException("configurationChanged", "OpenRouter credential changed during credits refresh.");
            current = current with { ProviderId = providerId, CredentialGeneration = generation };
            if (current.AccountBalanceAvailability != MetricAvailability.Reported)
            {
                var previous = ReadCreditSnapshot(providerId, provider.Auth.SecretRef!, generation, cancellationToken);
                var previousBalance = previous?.Measurements.FirstOrDefault(item => item.Scope == CreditScope.AccountBalance);
                if (previousBalance?.Amount.Value is not null)
                {
                    var carried = previousBalance with
                    {
                        Amount = previousBalance.Amount with { Availability = MetricAvailability.Stale },
                    };
                    var measurements = current.Measurements.Where(item => item.Scope != CreditScope.AccountBalance)
                        .Append(carried).ToArray();
                    current = current with
                    {
                        Measurements = measurements,
                        AccountBalanceAvailability = MetricAvailability.Stale,
                        Limitation = current.Limitation ?? "credits_unavailable",
                    };
                }
            }
            WriteCreditSnapshot(providerId, provider.Auth.SecretRef!, generation, current, cancellationToken);
            return current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ProviderConnectionException or IOException or UnauthorizedAccessException)
        {
            if (ex is ProviderConnectionException { Kind: "configurationChanged" }) throw;
            var previous = ReadCreditSnapshot(providerId, provider.Auth.SecretRef!, generation, cancellationToken);
            if (previous is null) throw;
            var now = _now();
            var measurements = previous.Measurements.Select(item => item with
            {
                Amount = item.Amount with { Availability = MetricAvailability.Stale },
            }).ToArray();
            var staleSnapshot = previous with { Measurements = measurements, AccountBalanceAvailability = MetricAvailability.Stale,
                Limitation = "refresh_failed", LastQueryAttemptAt = now, ProviderId = providerId,
                CredentialGeneration = generation, KeyAvailability = MetricAvailability.Stale };
            WriteCreditSnapshot(providerId, provider.Auth.SecretRef!, generation, staleSnapshot, cancellationToken);
            return staleSnapshot;
        }
    }

    public void Disconnect(string providerId, CancellationToken cancellationToken)
    {
        var provider = Resolve(providerId, cancellationToken);
        var metadata = ProviderConnectionMetadataStore.Read(_paths.DataDirectory, providerId, cancellationToken);
        _credentials.Delete(provider.Auth.SecretRef!, cancellationToken);
        ProviderConnectionMetadataStore.Clear(_paths.DataDirectory, providerId, CancellationToken.None);
        InvalidateCaches(providerId, provider.Auth.SecretRef!, metadata.CredentialGeneration, CancellationToken.None);
    }

    private ProviderDescriptor Resolve(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = OmniHost.LoadUserConfiguration(_paths).Registry.Provider(id);
        if (provider is null || !IsOpenRouterProvider(provider))
            throw new ProviderConnectionException("unknownProvider", "Unknown OpenRouter provider.");
        return provider;
    }

    internal static bool IsOpenRouterProvider(ProviderDescriptor provider) =>
        provider.Family == ProviderFamily.OpenAiChatCompatible
        && provider.Auth.Kind == AuthKind.ApiKey
        && string.Equals(provider.BaseUrl.TrimEnd('/'), "https://openrouter.ai/api/v1", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(provider.Auth.SecretRef);

    private bool CredentialPresent(string secretRef, CancellationToken cancellationToken)
    {
        try { return _credentials.Load(secretRef, cancellationToken) is not null; }
        catch (SecretValueTooShortException) { return true; }
    }

    private static string? State(ProviderConnectionState? state) => state switch
    {
        ProviderConnectionState.Connected => "valid",
        ProviderConnectionState.Invalid => "invalid",
        ProviderConnectionState.Unreachable => "unreachable",
        _ => null,
    };

    private void WriteCreditSnapshot(string providerId, string secretRef, string generation,
        OpenRouterCreditSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_paths.DataDirectory);
        var path = CachePath("credit-snapshot", providerId, secretRef, generation);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = File.Create(temp))
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new() { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("providerId", providerId); writer.WriteString("secretRef", secretRef);
                writer.WriteString("credentialGeneration", generation);
                writer.WriteNumber("keyLimit", snapshot.Key.Limit ?? 0m); writer.WriteBoolean("keyLimitKnown", snapshot.Key.Limit.HasValue);
                writer.WriteNumber("keyRemaining", snapshot.Key.LimitRemaining ?? 0m); writer.WriteBoolean("keyRemainingKnown", snapshot.Key.LimitRemaining.HasValue);
                writer.WriteNumber("keyUsage", snapshot.Key.Usage ?? 0m); writer.WriteBoolean("keyUsageKnown", snapshot.Key.Usage.HasValue);
                writer.WriteBoolean("management", snapshot.Key.IsManagementKey ?? false); writer.WriteBoolean("managementKnown", snapshot.Key.IsManagementKey.HasValue);
                writer.WriteString("limitResetInterval", snapshot.Key.LimitResetInterval);
                writer.WriteString("limitResetAt", snapshot.Key.LimitResetAt?.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("keyAvailability", snapshot.KeyAvailability.ToString());
                writer.WriteString("availability", snapshot.AccountBalanceAvailability.ToString());
                writer.WriteString("limitation", snapshot.Limitation);
                writer.WriteString("asOf", snapshot.AsOf.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("attemptedAt", snapshot.LastQueryAttemptAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteStartArray("measurements");
                foreach (var measurement in snapshot.Measurements)
                {
                    writer.WriteStartObject(); writer.WriteString("scope", measurement.Scope.ToString());
                    writer.WriteString("availability", measurement.Amount.Availability.ToString());
                    writer.WriteNumber("amount", measurement.Amount.Value ?? 0m); writer.WriteBoolean("known", measurement.Amount.Value.HasValue);
                    writer.WriteString("source", measurement.Amount.Source); writer.WriteString("currency", measurement.Currency);
                    writer.WriteString("asOf", measurement.Amount.AsOf?.ToString("O", CultureInfo.InvariantCulture)); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private OpenRouterCreditSnapshot? ReadCreditSnapshot(string providerId, string secretRef, string generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(generation)) return null;
        var path = CachePath("credit-snapshot", providerId, secretRef, generation);
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || root.GetProperty("providerId").GetString() != providerId
                || root.GetProperty("secretRef").GetString() != secretRef
                || root.GetProperty("credentialGeneration").GetString() != generation) return null;
            decimal? Dec(string key, string known) => root.TryGetProperty(known, out var present) && present.ValueKind == System.Text.Json.JsonValueKind.True
                && root.TryGetProperty(key, out var number) && number.TryGetDecimal(out var value) ? value : null;
            DateTimeOffset? Date(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
            var asOf = Date("asOf"); var attempted = Date("attemptedAt");
            if (asOf is null || attempted is null) return null;
            var availability = Enum.TryParse<MetricAvailability>(root.GetProperty("availability").GetString(), out var parsed)
                ? parsed : MetricAvailability.Unknown;
            var measurements = new List<CreditMeasurement>();
            if (root.TryGetProperty("measurements", out var array) && array.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var row in array.EnumerateArray())
                {
                    if (!Enum.TryParse<CreditScope>(row.GetProperty("scope").GetString(), out var scope)) continue;
                    var amount = row.TryGetProperty("known", out var known) && known.ValueKind == System.Text.Json.JsonValueKind.True
                        && row.TryGetProperty("amount", out var number) && number.TryGetDecimal(out var value) ? value : (decimal?)null;
                    DateTimeOffset? stamp = row.TryGetProperty("asOf", out var at) && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedAt) ? parsedAt : null;
                    var source = row.TryGetProperty("source", out var src) && src.ValueKind == System.Text.Json.JsonValueKind.String ? src.GetString() : null;
                    var currency = row.TryGetProperty("currency", out var cur) && cur.ValueKind == System.Text.Json.JsonValueKind.String ? cur.GetString() ?? "USD" : "USD";
                    var metricAvailability = row.TryGetProperty("availability", out var av) && av.ValueKind == System.Text.Json.JsonValueKind.String
                        && Enum.TryParse<MetricAvailability>(av.GetString(), out var parsedAvailability)
                        ? parsedAvailability : MetricAvailability.Reported;
                    measurements.Add(new(scope, new Metric<decimal?>(metricAvailability, amount, source, stamp), currency, currency, null));
                }
            return new OpenRouterCreditSnapshot(measurements, availability,
                root.TryGetProperty("limitation", out var limit) && limit.ValueKind == System.Text.Json.JsonValueKind.String ? limit.GetString() : null,
                asOf.Value, attempted.Value,
                new(Dec("keyLimit", "keyLimitKnown"), Dec("keyRemaining", "keyRemainingKnown"), Dec("keyUsage", "keyUsageKnown"),
                    root.TryGetProperty("limitResetInterval", out var resetInterval) && resetInterval.ValueKind == System.Text.Json.JsonValueKind.String
                        ? resetInterval.GetString() : null, Date("limitResetAt"),
                    root.TryGetProperty("managementKnown", out var managementKnown) && managementKnown.ValueKind == System.Text.Json.JsonValueKind.True
                        ? root.GetProperty("management").GetBoolean() : null, asOf.Value))
                { ProviderId = providerId, CredentialGeneration = generation,
                    KeyAvailability = root.TryGetProperty("keyAvailability", out var keyAv)
                        && keyAv.ValueKind == System.Text.Json.JsonValueKind.String
                        && Enum.TryParse<MetricAvailability>(keyAv.GetString(), out var parsedKeyAv)
                            ? parsedKeyAv : MetricAvailability.Reported };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or FormatException or KeyNotFoundException)
        { return null; }
    }

    private string CredentialGeneration(string providerId, string secretRef, CancellationToken cancellationToken)
    {
        var metadata = ProviderConnectionMetadataStore.Read(_paths.DataDirectory, providerId, cancellationToken);
        var provider = Resolve(providerId, cancellationToken);
        return string.Equals(provider.Auth.SecretRef, secretRef, StringComparison.Ordinal)
            ? metadata.CredentialGeneration ?? string.Empty : string.Empty;
    }

    private string CachePath(string kind, string providerId, string secretRef, string generation)
    {
        var identity = System.Text.Encoding.UTF8.GetBytes(providerId + "\0" + secretRef + "\0" + generation);
        var name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(identity));
        return Path.Combine(_paths.DataDirectory, "openrouter-" + kind + "-" + name + ".json");
    }

    private void InvalidateCaches(string providerId, string secretRef, string? generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(generation)) return;
        foreach (var kind in new[] { "model-catalog", "credit-snapshot" })
        {
            var path = CachePath(kind, providerId, secretRef, generation);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string FreeProviderId(IEnumerable<string> ids)
    {
        var existing = ids.ToHashSet(StringComparer.Ordinal);
        var candidate = DefaultProviderId;
        for (var n = 2; existing.Contains(candidate); n++) candidate = DefaultProviderId + "-" + n;
        return candidate;
    }
}

public sealed record OpenRouterDiscoveryResult(string ProviderId, int RegisteredModels, bool Truncated);
public sealed record OpenRouterCatalogPage(IReadOnlyList<OpenRouterModel> Models, int TotalCount,
    int? NextOffset, bool Truncated, bool Stale, string? Error, DateTimeOffset? FetchedAt = null,
    string? ProviderId = null);

/// <summary>Atomic, validated User-scope provider and model catalog registration.</summary>
internal static class OpenRouterRegistration
{
    internal sealed record Target(string ProviderId, string SecretRef);

    internal static Target ResolveProvider(string directory, string expectedId)
    {
        var providersPath = Path.Combine(directory, "providers.yaml");
        var modelsPath = Path.Combine(directory, "models.yaml");
        var loaded = new ConfigLoader().Load(Read(providersPath), Read(modelsPath));
        var existing = loaded.Registry.Provider(expectedId);
        if (existing is not null)
        {
            if (!OpenRouterConnectionService.IsOpenRouterProvider(existing))
                throw new ProviderConnectionException("unknownProvider", "Selected provider id is occupied.");
            return new(existing.Id, existing.Auth.SecretRef!);
        }
        var refs = loaded.Registry.Providers().Select(p => p.Auth.SecretRef).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        var secretRef = OpenRouterConnectionService.DefaultSecretRef;
        for (var n = 2; refs.Contains(secretRef); n++) secretRef = OpenRouterConnectionService.DefaultSecretRef + "-" + n;
        return new(expectedId, secretRef);
    }

    internal static Target EnsureProvider(string directory, string expectedId, string expectedSecretRef)
    {
        Directory.CreateDirectory(directory);
        using var lease = ProviderConfigurationLease.Acquire(directory);
        var providersPath = Path.Combine(directory, "providers.yaml");
        var modelsPath = Path.Combine(directory, "models.yaml");
        var oldProviders = Read(providersPath);
        var oldModels = Read(modelsPath);
        var loaded = new ConfigLoader().Load(oldProviders, oldModels);
        var existing = loaded.Registry.Provider(expectedId);
        if (existing is not null)
        {
            if (!OpenRouterConnectionService.IsOpenRouterProvider(existing))
                throw new ProviderConnectionException("unknownProvider", "Selected provider id is occupied.");
            if (!string.Equals(existing.Auth.SecretRef, expectedSecretRef, StringComparison.Ordinal))
                throw new ProviderConnectionException("configurationChanged", "OpenRouter credential reference changed before save.");
            return new(existing.Id, existing.Auth.SecretRef!);
        }
        var refs = loaded.Registry.Providers().Select(p => p.Auth.SecretRef).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        if (refs.Contains(expectedSecretRef)) throw new ProviderConnectionException("configurationChanged", "OpenRouter credential reference became occupied.");
        var providers = Parse(oldProviders ?? "providers:\n  local: { baseUrl: http://127.0.0.1:8080, auth: none }\n");
        var providerMap = (YamlMappingNode)providers.Children[new YamlScalarNode("providers")];
        providerMap.Add(expectedId, new YamlMappingNode {
            { "family", "OpenAiChatCompatible" }, { "baseUrl", OpenRouterApi.ApiBaseUrl.TrimEnd('/') },
            { "authRef", expectedSecretRef }, { "billingMode", "MeteredCurrency" },
        });
        var next = Save(providers);
        _ = new ConfigLoader().Load(next, oldModels);
        WriteAtomic(providersPath, oldProviders, next);
        return new(expectedId, expectedSecretRef);
    }

    internal static int RegisterModels(string directory, string providerId, IReadOnlyList<OpenRouterModel> catalog)
    {
        Directory.CreateDirectory(directory);
        using var lease = ProviderConfigurationLease.Acquire(directory);
        var providersPath = Path.Combine(directory, "providers.yaml");
        var modelsPath = Path.Combine(directory, "models.yaml");
        var oldProviders = Read(providersPath); var oldModels = Read(modelsPath);
        var loaded = new ConfigLoader().Load(oldProviders, oldModels);
        var provider = loaded.Registry.Provider(providerId);
        if (provider is null || !OpenRouterConnectionService.IsOpenRouterProvider(provider))
            throw new ProviderConnectionException("configurationChanged", "OpenRouter provider changed before catalog registration.");
        var root = Parse(oldModels ?? (oldProviders is null ? "models:\n  local-worker: { provider: local, context: 8192 }\n" : "models: {}\n"));
        var map = (YamlMappingNode)root.Children[new YamlScalarNode("models")];
        var added = 0;
        foreach (var model in catalog)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || model.Id.Any(char.IsControl)) continue;
            var prior = loaded.Registry.Model(model.Id);
            if (prior is not null)
            {
                if (prior.ProviderId != providerId)
                    throw new ProviderConnectionException("modelConflict", "The selected model is already registered with another provider.");
                continue; // Never overwrite user qualification, limits or aliases.
            }
            var entry = new YamlMappingNode { { "provider", providerId } };
            if (model.ContextLength is > 0) entry.Add("context", model.ContextLength.Value.ToString(CultureInfo.InvariantCulture));
            if (model.MaxCompletionTokens is > 0) entry.Add("maxOutput", model.MaxCompletionTokens.Value.ToString(CultureInfo.InvariantCulture));
            AddPrice(entry, "inputPricePerMillionUsd", model.PromptPricePerToken);
            AddPrice(entry, "outputPricePerMillionUsd", model.CompletionPricePerToken);
            map.Add(model.Id, entry); added++;
        }
        if (added == 0) return 0;
        var next = Save(root);
        _ = new ConfigLoader().Load(oldProviders, next);
        WriteAtomic(modelsPath, oldModels, next);
        return added;
    }

    private static void AddPrice(YamlMappingNode entry, string name, string? perToken)
    {
        if (!TryScalePrice(perToken, out var pricePerMillion)) return;
        entry.Add(name, pricePerMillion.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryScalePrice(string? text, out decimal scaled)
    {
        scaled = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 128) return false;
        var value = text.AsSpan();
        var position = 0;
        if (value[position] == '+') position++;
        else if (value[position] == '-') return false;
        var exponentAt = value.IndexOfAny('e', 'E');
        var exponent = 0;
        if (exponentAt >= 0)
        {
            if (!int.TryParse(value[(exponentAt + 1)..], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out exponent) || exponent is < -1000 or > 1000) return false;
            value = value[..exponentAt];
        }
        var dot = value.IndexOf('.');
        var fractional = dot < 0 ? 0 : value.Length - dot - 1;
        Span<char> digits = stackalloc char[value.Length];
        var count = 0;
        var dotSeen = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '.')
            {
                if (dotSeen) return false;
                dotSeen = true;
                continue;
            }
            if (!char.IsAsciiDigit(value[i])) return false;
            digits[count++] = value[i];
        }
        if (count == 0 || !BigInteger.TryParse(digits[..count], NumberStyles.None,
                CultureInfo.InvariantCulture, out var coefficient)) return false;
        var scale = fractional - exponent - 6; // API prices are USD/token; local config is USD/million tokens.
        if (scale < 0)
        {
            coefficient *= BigInteger.Pow(10, -scale);
            scale = 0;
        }
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        if (scale > 28 || coefficient > new BigInteger(decimal.MaxValue)) return false;
        var mask = new BigInteger(uint.MaxValue);
        var lo = (int)(uint)(coefficient & mask);
        var mid = (int)(uint)((coefficient >> 32) & mask);
        var hi = (int)(uint)((coefficient >> 64) & mask);
        scaled = new decimal(lo, mid, hi, false, (byte)scale);
        return true;
    }
    private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    private static YamlMappingNode Parse(string text) { var stream = new YamlStream(); stream.Load(new StringReader(text)); return (YamlMappingNode)stream.Documents[0].RootNode; }
    private static string Save(YamlMappingNode root) { using var writer = new StringWriter(CultureInfo.InvariantCulture); new YamlStream(new YamlDocument(root)).Save(writer, false); return writer.ToString(); }
    private static void WriteAtomic(string path, string? expected, string content)
    {
        if (Read(path) != expected) throw new IOException("User configuration changed concurrently.");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temp, content); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
