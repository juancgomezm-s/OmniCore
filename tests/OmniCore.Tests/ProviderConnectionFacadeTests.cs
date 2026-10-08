namespace OmniCore.Tests;

using System.Net;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;

/// <summary>Host facade fixtures: no UI, real account, network or inference.</summary>
public sealed class ProviderConnectionFacadeTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("{\"data\":[],\"has_more\":false}")
            });
    }

    [Fact]
    public async Task Facade_UnverifiedSaveDoesNotInventMeasurement_AndDisconnectPreservesNoCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-facade-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var service = new ProviderConnectionService(store, paths);
            var facade = new TuiProviderConnectionHost(service, paths);
            var result = await facade.ConnectAsync("fixture-only-api-key-12345", false, TestContext.Current.CancellationToken);
            Assert.Equal(ProviderConnectionState.Unknown, result.State);
            Assert.Null(result.ValidatedAt);
            Assert.Null(result.DiscoveredAt);
            Assert.DoesNotContain("fixture-only-api-key-12345", result.Detail ?? "");
            var row = Assert.Single(facade.List(TestContext.Current.CancellationToken),
                item => item.ProviderId == ProviderConnectionService.DefaultAnthropicProviderId);
            Assert.True(row.CanTest);
            facade.Disconnect(row.ProviderId, TestContext.Current.CancellationToken);
            Assert.Equal(ProviderConnectionState.NotConfigured,
                Assert.Single(facade.List(TestContext.Current.CancellationToken),
                    item => item.ProviderId == row.ProviderId).State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Facade_FailedRetestDoesNotReuseSuccessfulMeasurement()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-facade-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DefaultPlatformPaths(root, Path.Combine(root, "config"));
            var store = new FileCredentialStore(Path.Combine(root, "credentials.ini"));
            var handler = new Handler();
            var measuredAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
            var service = new ProviderConnectionService(store, paths,
                httpFactory: () => new HttpClient(handler, false), now: () => measuredAt);
            var facade = new TuiProviderConnectionHost(service, paths);
            var connected = await facade.ConnectAsync("fixture-only-api-key-12345", true, TestContext.Current.CancellationToken);
            Assert.Equal(ProviderConnectionState.Connected, connected.State);
            Assert.Equal(measuredAt, connected.ValidatedAt);
            var row = Assert.Single(facade.List(TestContext.Current.CancellationToken),
                item => item.ProviderId == ProviderConnectionService.DefaultAnthropicProviderId);
            handler.Status = HttpStatusCode.Unauthorized;
            var failed = await facade.TestAsync(row.ProviderId, TestContext.Current.CancellationToken);
            Assert.Equal(ProviderConnectionState.Invalid, failed.State);
            Assert.Null(failed.ValidatedAt);
            Assert.Null(failed.DiscoveredAt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
