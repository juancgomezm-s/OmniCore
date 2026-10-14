namespace OmniCore.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Engine;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;

public sealed class OpenRouterConnectionServiceTests
{
    [Fact]
    public async System.Threading.Tasks.Task UserConnection_Discovery_RegistersCatalog_AndModelAdapterUsesExactIdAndStoredKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new FixtureHandler();
            var service = new OpenRouterConnectionService(store, paths,
                httpFactory: () => new HttpClient(handler, disposeHandler: false),
                now: () => new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
            var facade = new TuiProviderConnectionHost(new ProviderConnectionService(store, paths), paths, service);
            var row = Assert.Single(facade.List(TestContext.Current.CancellationToken), item => item.ProviderId == "openrouter");
            Assert.True(row.CanConnect);
            var connected = await facade.ConnectAsync(row.ProviderId, "fixture-openrouter-key-12345", true,
                TestContext.Current.CancellationToken);
            Assert.Equal(ProviderConnectionState.Connected, connected.State);
            Assert.DoesNotContain("fixture-openrouter-key-12345", connected.Detail ?? string.Empty);

            var page = await service.RefreshCatalogAsync(row.ProviderId, "", 0, 30, TestContext.Current.CancellationToken);
            Assert.Equal(2, page.TotalCount);
            Assert.False(page.Stale);
            var selectedPage = service.BrowseCatalog(row.ProviderId, "gpt-fixture", 0, 30,
                TestContext.Current.CancellationToken);
            Assert.Equal("openai/gpt-fixture", Assert.Single(selectedPage.Models).Id);
            Assert.Equal(1, service.RegisterCatalogModels(row.ProviderId, ["openai/gpt-fixture"],
                TestContext.Current.CancellationToken));
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider("openrouter"));
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model("openai/gpt-fixture"));
            Assert.Equal(ProviderFamily.OpenAiChatCompatible, provider.Family);
            Assert.Equal("https://openrouter.ai/api/v1", provider.BaseUrl);
            Assert.Equal("openrouter", model.ProviderId);
            Assert.Equal(128000, model.ContextWindow);
            Assert.Equal(4096, model.MaxOutputTokens);
            var pricing = loaded.Pricing(model.Id)!;
            Assert.Equal(0.15m, pricing.InputPricePerMillionUsd);
            Assert.Equal(0.6m, pricing.OutputPricePerMillionUsd);
            Assert.Null(loaded.Registry.Model("anthropic/sonnet-fixture"));
            Assert.DoesNotContain("fixture-openrouter-key-12345", File.ReadAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml")));

            var requestHandler = new CaptureCompletionHandler();
            var route = new ModelRoute(provider.Id, provider.BaseUrl, provider.Family, null, model.Id);
            var selection = new ModelSelection(new ModelIdValue(model.Id), 4096, ToolMode.Direct, null, route: route);
            var request = new ModelRequest(selection,
                [new ModelMessage(MessageRole.User, [new TextBlock("offline fixture")])], null, [],
                ToolChoice.None(), null, null, null, null);
            var adapter = new OpenAiChatCompatibleProvider(provider, new StoreSecrets(store),
                () => new HttpClient(requestHandler, disposeHandler: false),
                new OpenAiProviderOptions { MaxRetries = 0 });
            _ = await Collect(adapter.StreamAsync(request, TestContext.Current.CancellationToken));
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", requestHandler.Url);
            Assert.Equal("Bearer fixture-openrouter-key-12345", requestHandler.Authorization);
            using var body = JsonDocument.Parse(requestHandler.Body);
            Assert.Equal("openai/gpt-fixture", body.RootElement.GetProperty("model").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task CliOpenRouterRoutesConnectTestCatalogRefreshSelectAndDisconnectWithoutKeyArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new FixtureHandler();
            Func<HttpClient> clients = () => new HttpClient(handler, false);
            var service = new OpenRouterConnectionService(store, paths, clients);
            var host = new TuiProviderConnectionHost(new ProviderConnectionService(store, paths), paths, service);
            var provider = Assert.Single(host.List(TestContext.Current.CancellationToken), row => row.Backend == "openrouter");
            var connect = await OmniCore.Cli.CliApp.RunOpenRouterCommandAsync(["connect", provider.ProviderId], host,
                _ => System.Threading.Tasks.Task.FromResult<string?>("fixture-openrouter-key-12345"),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, connect);
            var tested = await OmniCore.Cli.CliApp.RunOpenRouterCommandAsync(["test", provider.ProviderId], host,
                _ => throw new InvalidOperationException("test must not prompt for a key"), TestContext.Current.CancellationToken);
            Assert.Equal(0, tested);
            var refresh = await OmniCore.Cli.CliApp.RunOpenRouterCommandAsync(["refresh", provider.ProviderId], host,
                _ => throw new InvalidOperationException("catalog refresh must not prompt for a key"), TestContext.Current.CancellationToken);
            Assert.Equal(0, refresh);
            var catalog = await OmniCore.Cli.CliApp.RunOpenRouterCommandAsync(["catalog", provider.ProviderId], host,
                _ => throw new InvalidOperationException("catalog must not prompt for a key"), TestContext.Current.CancellationToken);
            Assert.Equal(0, catalog);
            var selected = await OmniCore.Cli.CliApp.RunOpenRouterCommandAsync(["select", provider.ProviderId, "openai/gpt-fixture"], host,
                _ => throw new InvalidOperationException("model selection must not prompt for a key"), TestContext.Current.CancellationToken);
            Assert.Equal(0, selected);
            Assert.NotNull(OmniHost.LoadUserConfiguration(paths).Registry.Model("openai/gpt-fixture"));
            var disconnected = await OmniCore.Cli.CliApp.RunOpenRouterCommandAsync(["disconnect", provider.ProviderId], host,
                _ => throw new InvalidOperationException("disconnect must not prompt for a key"), TestContext.Current.CancellationToken);
            Assert.Equal(0, disconnected);
            Assert.Null(store.Load("openrouter", TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task NormalKeyReportsKeyLimitButNeverInventsAccountBalance()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-credit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new FixtureHandler { ManagementKey = false };
            var service = new OpenRouterConnectionService(store, paths, () => new HttpClient(handler, false));
            var row = Assert.Single(service.List(TestContext.Current.CancellationToken));
            await service.ConnectAsync(row.ProviderId, "fixture-openrouter-key-12345", false, TestContext.Current.CancellationToken);
            var snapshot = await service.ReadCreditsAsync(row.ProviderId, TestContext.Current.CancellationToken);
            Assert.Equal(MetricAvailability.NotSupported, snapshot.AccountBalanceAvailability);
            Assert.Equal("management_key_required", snapshot.Limitation);
            Assert.Contains(snapshot.Measurements, measurement => measurement.Scope == CreditScope.KeyLimit);
            Assert.DoesNotContain(snapshot.Measurements, measurement => measurement.Scope == CreditScope.AccountBalance);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task FailedRefreshKeepsLastCreditReadingStale_AndRejectedNewKeyLeavesNoPartialProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new FixtureHandler();
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            var service = new OpenRouterConnectionService(store, paths, () => new HttpClient(handler, false),
                () => now);
            var row = Assert.Single(service.List(TestContext.Current.CancellationToken));
            await service.ConnectAsync(row.ProviderId, "fixture-openrouter-key-12345", true, TestContext.Current.CancellationToken);
            var first = await service.ReadCreditsAsync(row.ProviderId, TestContext.Current.CancellationToken);
            handler.FailKey = true;
            now = now.AddMinutes(1);
            var stale = await service.ReadCreditsAsync(row.ProviderId, TestContext.Current.CancellationToken);
            Assert.Equal(MetricAvailability.Stale, stale.AccountBalanceAvailability);
            Assert.True(stale.LastQueryAttemptAt > first.AsOf);
            Assert.Equal(first.Measurements.Single(item => item.Scope == CreditScope.AccountBalance).Amount.Value,
                stale.Measurements.Single(item => item.Scope == CreditScope.AccountBalance).Amount.Value);
            Assert.Equal(MetricAvailability.Stale,
                stale.Measurements.Single(item => item.Scope == CreditScope.AccountBalance).Amount.Availability);

            var rejectedRoot = Path.Combine(root, "rejected");
            var rejectedPaths = new DefaultPlatformPaths(rejectedRoot, Path.Combine(rejectedRoot, "config"));
            var rejectedHandler = new FixtureHandler { KeyStatus = HttpStatusCode.Unauthorized };
            var rejected = new OpenRouterConnectionService(new FileCredentialStore(Path.Combine(rejectedRoot, "credentials.ini")),
                rejectedPaths, () => new HttpClient(rejectedHandler, false));
            var synthetic = Assert.Single(rejected.List(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ProviderConnectionException>(() => rejected.ConnectAsync(synthetic.ProviderId,
                "fixture-openrouter-key-12345", true, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(rejectedPaths.ConfigDirectory, "providers.yaml")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task KeySuccessCreditsFailureKeepsFreshKeyAndSameIdentityStaleBalanceAcrossReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-partial-credit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new FixtureHandler();
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            Func<HttpClient> clients = () => new HttpClient(handler, false);
            var service = new OpenRouterConnectionService(store, paths, clients, () => now);
            var provider = Assert.Single(service.List(TestContext.Current.CancellationToken));
            await service.ConnectAsync(provider.ProviderId, "fixture-openrouter-key-12345", true,
                TestContext.Current.CancellationToken);
            var fresh = await service.ReadCreditsAsync(provider.ProviderId, TestContext.Current.CancellationToken);
            var oldBalance = Assert.Single(fresh.Measurements, item => item.Scope == CreditScope.AccountBalance);
            Assert.Equal(MetricAvailability.Reported, fresh.KeyAvailability);
            Assert.Equal(MetricAvailability.Reported, oldBalance.Amount.Availability);

            now = now.AddMinutes(3);
            handler.FailCredits = true;
            var refreshed = await service.ReadCreditsAsync(provider.ProviderId, TestContext.Current.CancellationToken);
            Assert.Equal(MetricAvailability.Reported, refreshed.KeyAvailability);
            Assert.Equal(20m, refreshed.Measurements.Single(item => item.Scope == CreditScope.KeyLimit).Amount.Value);
            Assert.Equal(MetricAvailability.Stale, refreshed.AccountBalanceAvailability);
            var staleBalance = Assert.Single(refreshed.Measurements, item => item.Scope == CreditScope.AccountBalance);
            Assert.Equal(oldBalance.Amount.Value, staleBalance.Amount.Value);
            Assert.Equal(oldBalance.Amount.AsOf, staleBalance.Amount.AsOf);
            Assert.Equal(MetricAvailability.Stale, staleBalance.Amount.Availability);

            var reopened = new OpenRouterConnectionService(store, paths, clients, () => now);
            var afterReopen = await reopened.ReadCreditsAsync(provider.ProviderId, TestContext.Current.CancellationToken);
            Assert.Equal(MetricAvailability.Reported, afterReopen.KeyAvailability);
            Assert.Equal(MetricAvailability.Stale, afterReopen.AccountBalanceAvailability);
            var reopenedBalance = Assert.Single(afterReopen.Measurements, item => item.Scope == CreditScope.AccountBalance);
            Assert.Equal(oldBalance.Amount.Value, reopenedBalance.Amount.Value);
            Assert.Equal(oldBalance.Amount.AsOf, reopenedBalance.Amount.AsOf);
            Assert.Equal(MetricAvailability.Stale, reopenedBalance.Amount.Availability);
            Assert.All(Directory.GetFiles(paths.DataDirectory, "openrouter-credit-snapshot-*.json"), file =>
                Assert.DoesNotContain("fixture-openrouter-key-12345", File.ReadAllText(file), StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task CreditCachesRemainIsolatedBetweenOpenRouterProviderIdentities()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-cache-isolation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new FixtureHandler();
            var service = new OpenRouterConnectionService(store, paths, () => new HttpClient(handler, false));
            var firstRow = Assert.Single(service.List(TestContext.Current.CancellationToken));
            await service.ConnectAsync(firstRow.ProviderId, "fixture-openrouter-key-12345", false,
                TestContext.Current.CancellationToken);
            var first = await service.ReadCreditsAsync(firstRow.ProviderId, TestContext.Current.CancellationToken);
            Assert.Equal(25m, first.Measurements.Single(item => item.Scope == CreditScope.AccountBalance).Amount.Value);

            await service.ConnectAsync("openrouter-secondary", "fixture-openrouter-key-secondary-12345", false,
                TestContext.Current.CancellationToken);
            handler.TotalCredits = 80m;
            var second = await service.ReadCreditsAsync("openrouter-secondary", TestContext.Current.CancellationToken);
            Assert.Equal(75m, second.Measurements.Single(item => item.Scope == CreditScope.AccountBalance).Amount.Value);
            Assert.NotEqual(first.CredentialGeneration, second.CredentialGeneration);
            Assert.Equal(2, Directory.GetFiles(paths.DataDirectory, "openrouter-credit-snapshot-*.json").Length);

            handler.FailCredits = true;
            var firstStale = await service.ReadCreditsAsync(firstRow.ProviderId, TestContext.Current.CancellationToken);
            var secondStale = await service.ReadCreditsAsync("openrouter-secondary", TestContext.Current.CancellationToken);
            Assert.Equal(25m, firstStale.Measurements.Single(item => item.Scope == CreditScope.AccountBalance).Amount.Value);
            Assert.Equal(75m, secondStale.Measurements.Single(item => item.Scope == CreditScope.AccountBalance).Amount.Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task ProtocolQueryAndSlashActionExposeOnlyLocalStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-protocol-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(),
                platformPathsFactory: () => paths);
            var query = server.Query(OpenRouterProtocol.StatusQuery, TestContext.Current.CancellationToken);
            Assert.NotNull(query);
            using var payload = JsonDocument.Parse(query.Json);
            Assert.Equal("openrouter", payload.RootElement[0].GetProperty("providerId").GetString());
            Assert.Equal("NotConfigured", payload.RootElement[0].GetProperty("state").GetString());
            Assert.DoesNotContain("apiKey", query.Json, StringComparison.OrdinalIgnoreCase);

            var text = new OmniCore.Client.Localization("en");
            var invocation = new OmniCore.Client.ClientActionInvocation(
                OmniCore.Client.ClientActionIds.OpenRouterInspect, [], "fixture");
            var result = await new OmniCore.Client.ClientActionHandler().ExecuteAsync(invocation,
                new OmniCore.Client.ClientContext(server, text), TestContext.Current.CancellationToken);
            Assert.Contains("OpenRouter connection", result.Text, StringComparison.Ordinal);
            Assert.Contains("Not configured", result.Text, StringComparison.Ordinal);
            Assert.True(OmniCore.Client.ClientCommandRegistry.Default.TryResolve(
                new CommandInvocation("openrouter", [], "cli"), out var action));
            Assert.Equal(OmniCore.Client.ClientActionIds.OpenRouterInspect, action!.ActionId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task ExplicitProtocolCreditsActionUsesFixturesAndKeepsKeyLimitSeparateFromAccountBalance()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-protocol-credits-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new FixtureHandler { ManagementKey = false };
            var apiFactory = () => new HttpClient(handler, false);
            var setup = new OpenRouterConnectionService(store, paths, apiFactory);
            var synthetic = Assert.Single(setup.List(TestContext.Current.CancellationToken));
            await setup.ConnectAsync(synthetic.ProviderId, "fixture-openrouter-key-12345", false,
                TestContext.Current.CancellationToken);
            var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(),
                platformPathsFactory: () => paths,
                openRouterServiceFactory: configuredPaths => new OpenRouterConnectionService(store, configuredPaths, apiFactory));
            var sim = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sim\"}"),
                TestContext.Current.CancellationToken);
            Assert.Equal("ok", sim.Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var action = new OmniCore.Client.ClientActionInvocation(OmniCore.Client.ClientActionIds.OpenRouterInspect,
                ["credits"], "cli");
            var result = await new OmniCore.Client.ClientActionHandler().ExecuteAsync(action,
                new OmniCore.Client.ClientContext(server, new OmniCore.Client.Localization("en")),
                TestContext.Current.CancellationToken);
            Assert.Contains("Key limit: 25", result.Text, StringComparison.Ordinal);
            Assert.Contains("Account balance: unknown", result.Text, StringComparison.Ordinal);
            Assert.Contains("Account balance is unavailable", result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-openrouter-key-12345", result.Text, StringComparison.Ordinal);
            var creditCache = Assert.Single(Directory.GetFiles(paths.DataDirectory, "openrouter-credit-snapshot-*.json"));
            var creditCacheJson = File.ReadAllText(creditCache);
            Assert.DoesNotContain("fixture-openrouter-key-12345", creditCacheJson, StringComparison.Ordinal);
            Assert.DoesNotContain("keyRaw", creditCacheJson, StringComparison.Ordinal);
            var quota = Assert.Single(server.Observability.Snapshot(session).Quotas);
            Assert.Equal("openrouter", quota.ProviderId);
            Assert.Equal(MetricAvailability.Reported, quota.Availability);
            Assert.Equal(CreditScope.KeyLimit, Assert.Single(quota.Credits).Scope);
            Assert.Contains("openrouter key limit 20 USD", OmniCore.Client.UsagePresentation.AccountQuota(quota, "en"),
                StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CachedOpenRouterQuotaBecomesStaleWhenCredentialGenerationChangesOrIsCleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-quota-generation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            Directory.CreateDirectory(paths.DataDirectory);
            var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(),
                platformPathsFactory: () => paths);
            var session = SessionId.New();
            var oldGeneration = Guid.NewGuid().ToString("N");
            var newGeneration = Guid.NewGuid().ToString("N");
            ProviderConnectionMetadataStore.Write(paths.DataDirectory, "openrouter",
                new(null, "valid", DateTimeOffset.UtcNow, null, newGeneration), TestContext.Current.CancellationToken);
            var reading = new ProviderQuotaSnapshot("openrouter", null, "openrouter.credits", DateTimeOffset.UtcNow,
                MetricAvailability.Reported, [],
                [new(CreditScope.AccountBalance, new(MetricAvailability.Reported, 25m, "fixture", DateTimeOffset.UtcNow), "USD", "USD", null)],
                null, DateTimeOffset.UtcNow, oldGeneration);
            server.Observability.SetQuota(session, reading);

            var rotated = Assert.IsType<ProviderQuotaSnapshot>(server.CurrentCredentialQuota(session, "openrouter"));
            Assert.Equal(MetricAvailability.Stale, rotated.Availability);
            Assert.Equal(MetricAvailability.Stale, Assert.Single(rotated.Credits).Amount.Availability);
            Assert.Equal(oldGeneration, rotated.CredentialGeneration);

            ProviderConnectionMetadataStore.Clear(paths.DataDirectory, "openrouter", TestContext.Current.CancellationToken);
            var disconnected = Assert.IsType<ProviderQuotaSnapshot>(server.CurrentCredentialQuota(session, "openrouter"));
            Assert.Equal(MetricAvailability.Stale, disconnected.Availability);
            Assert.Equal(MetricAvailability.Stale, Assert.Single(disconnected.Credits).Amount.Availability);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task StaleOpenRouterSelectionCannotRouteItsKeyToAnAnthropicProviderWithSameId()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-openrouter-id-collision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            Directory.CreateDirectory(paths.ConfigDirectory);
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"),
                "providers:\n  openrouter: { family: AnthropicMessages, baseUrl: https://api.anthropic.com, authRef: anthropic-key, billingMode: MeteredCurrency }\n");
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new FixtureHandler();
            var service = new ProviderConnectionService(store, paths, () => new HttpClient(handler, false));
            var openRouter = new OpenRouterConnectionService(store, paths, () => new HttpClient(handler, false));
            var facade = new TuiProviderConnectionHost(service, paths, openRouter);
            var staleOpenRouterRow = new ProviderConnectionMenuRow("openrouter", ProviderConnectionMethod.ApiKey,
                ProviderConnectionState.NotConfigured, BillingMode.MeteredCurrency, null, null, true, false, false,
                false, null, null) { Backend = "openrouter" };
            await Assert.ThrowsAsync<ProviderConnectionException>(() => facade.ConnectAsync(staleOpenRouterRow,
                "fixture-openrouter-key-12345", true, TestContext.Current.CancellationToken));
            Assert.Equal(0, handler.RequestCount);
            Assert.Null(store.Load("anthropic-key", TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> source)
    {
        var list = new List<ModelStreamEvent>();
        await foreach (var value in source) list.Add(value);
        return list;
    }

    private sealed class StoreSecrets(ICredentialStore store) : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            Secret.Of(store.Load(secretRef, cancellationToken) ?? throw new InvalidOperationException("credential missing"));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public bool ManagementKey { get; set; } = true;
        public bool FailKey { get; set; }
        public bool FailCredits { get; set; }
        public HttpStatusCode KeyStatus { get; set; } = HttpStatusCode.OK;
        public decimal TotalCredits { get; set; } = 30m;
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/api/v1/key") return System.Threading.Tasks.Task.FromResult(Json(FailKey ? HttpStatusCode.InternalServerError : KeyStatus,
                """{"data":{"limit":25.0,"limit_remaining":20.0,"usage":5.0,"is_management_key":true}}"""
                    .Replace("true}}", ManagementKey.ToString().ToLowerInvariant() + "}}", StringComparison.Ordinal)));
            if (path.StartsWith("/api/v1/models?", StringComparison.Ordinal)) return System.Threading.Tasks.Task.FromResult(Json(HttpStatusCode.OK,
                """{"data":[{"id":"openai/gpt-fixture","name":"Fixture","context_length":128000,"top_provider":{"max_completion_tokens":4096},"pricing":{"prompt":"0.00000015","completion":"0.0000006"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"]},{"id":"anthropic/sonnet-fixture","context_length":200000,"pricing":{"prompt":"0.000003"}}]}"""));
            if (path == "/api/v1/credits") return System.Threading.Tasks.Task.FromResult(Json(FailCredits ? HttpStatusCode.ServiceUnavailable
                : ManagementKey ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
                """{"data":{"total_credits":""" + TotalCredits.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + """, "total_usage":5.0}}"""));
            return System.Threading.Tasks.Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class CaptureCompletionHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
