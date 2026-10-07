using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Exercises account-quota status presentation through the normal configured Responses runtime.
/// All quota readings and HTTP responses are private fixtures; this test uses no provider account.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class SubscriptionStatusLineIntegrationTests
{
    private const string ProviderId = "m5-statusline-fixture-provider";
    private const string ModelId = "m5-statusline-fixture-model";

    [Fact]
    public async Task Account_quota_is_session_and_provider_scoped_and_stays_distinct_from_response_rate_limits()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-statusline-quota-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(workspace);
        var prior = ProcessEnvironment.Capture();
        var requestedProviders = new List<string>();
        var reportUnknownForFixture = 0;
        var reportedAt = DateTimeOffset.UtcNow;
        var reportedQuota = ReportedQuota(ProviderId, reportedAt, 80d);
        var attemptedUnknownAt = reportedAt.AddMinutes(2);
        OmniServer? server = null;

        try
        {
            await using var endpoint = new LoopbackResponsesServer();
            WriteConfiguration(config, endpoint.BaseUrl);
            ProcessEnvironment.Set(data, config, ModelId);

            Task<ProviderQuotaSnapshot> QueryQuota(string providerId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (requestedProviders) requestedProviders.Add(providerId);
                if (providerId == ProviderId && Volatile.Read(ref reportUnknownForFixture) != 0)
                    return Task.FromResult(new ProviderQuotaSnapshot(providerId, "private-fixture-account",
                        "synthetic-statusline-query", attemptedUnknownAt, MetricAvailability.Unknown, [], [],
                        "synthetic query intentionally unavailable", attemptedUnknownAt));

                return Task.FromResult(providerId == ProviderId
                    ? reportedQuota
                    : ReportedQuota(providerId, reportedAt, 25d));
            }

            var runtime = OmniCliRuntime.Create(workspace, QueryQuota);
            runtime.UseConsoleInput = false;
            server = Assert.IsType<OmniServer>(runtime.Connect(TestContext.Current.CancellationToken));
            Assert.Null(runtime.CurrentUsage());
            var output = new List<string>();
            var exitCode = await runtime.AskAsync("Answer briefly without using tools.", output.Add,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);

            var activeSession = Assert.IsType<SessionId>(server.LastSessionId());
            Assert.Single(endpoint.Requests);
            Assert.Equal("/responses", Assert.Single(endpoint.Requests).Path);
            lock (requestedProviders) Assert.Contains(ProviderId, requestedProviders);

            var firstUsage = Assert.IsType<UsageSnapshot>(runtime.CurrentUsage());
            Assert.Equal(MetricAvailability.Reported, firstUsage.AccountQuota!.Availability);
            Assert.Equal(ProviderId, firstUsage.AccountQuota.ProviderId);
            Assert.Equal("private-fixture-account", firstUsage.AccountQuota.AccountId);
            Assert.Equal(reportedAt, firstUsage.AccountQuota.AsOf);
            var accountWindow = Assert.Single(firstUsage.AccountQuota.Windows);
            Assert.Equal((int?)10_080, accountWindow.DurationMinutes);
            Assert.Equal(MetricAvailability.Reported, accountWindow.RemainingPercent.Availability);
            Assert.Equal((double?)80d, accountWindow.RemainingPercent.Value);

            Assert.Equal(MetricAvailability.Reported, firstUsage.Remaining.Availability);
            Assert.Equal(QuotaKind.RateLimitWindow, firstUsage.Remaining.Value!.Kind);
            Assert.Equal(1_000m, firstUsage.Remaining.Value.Limit);
            Assert.Equal(600m, firstUsage.Remaining.Value.Remaining);
            Assert.Equal("tokens 60%", OmniCore.Client.UsagePresentation.Remaining(firstUsage.Remaining));
            var firstStatus = OmniCore.Cli.CliApp.StatusLineText(firstUsage);
            Assert.Contains("tokens 60%", firstStatus);
            Assert.Contains(ProviderId + " 7d remaining 80%", firstStatus);

            // An unknown refresh preserves the last reading as stale and timestamps the failed attempt.
            Volatile.Write(ref reportUnknownForFixture, 1);
            var attempted = await runtime.RefreshProviderQuotaAsync(ProviderId, TestContext.Current.CancellationToken);
            Assert.Equal(MetricAvailability.Unknown, attempted!.Availability);
            Assert.Equal(attemptedUnknownAt, attempted.LastQueryAttemptAt);
            var staleUsage = Assert.IsType<UsageSnapshot>(runtime.CurrentUsage());
            Assert.Equal(MetricAvailability.Stale, staleUsage.AccountQuota!.Availability);
            Assert.Equal(reportedAt, staleUsage.AccountQuota.AsOf);
            Assert.Equal(attemptedUnknownAt, staleUsage.AccountQuota.LastQueryAttemptAt);
            Assert.Equal("synthetic query intentionally unavailable", staleUsage.AccountQuota.Limitation);
            Assert.Equal("tokens 60%", OmniCore.Client.UsagePresentation.Remaining(staleUsage.Remaining));
            Assert.Contains(ProviderId + " 7d remaining 80% stale", OmniCore.Cli.CliApp.StatusLineText(staleUsage));

            // A query for another provider is stored independently and cannot replace the active provider's quota.
            var otherProvider = "m5-statusline-other-fixture-provider";
            var other = await runtime.RefreshProviderQuotaAsync(otherProvider, TestContext.Current.CancellationToken);
            Assert.Equal(otherProvider, other!.ProviderId);
            Assert.Equal(MetricAvailability.Reported, other.Availability);
            var afterOtherProvider = Assert.IsType<UsageSnapshot>(runtime.CurrentUsage());
            Assert.Equal(ProviderId, afterOtherProvider.AccountQuota!.ProviderId);
            Assert.Equal(MetricAvailability.Stale, afterOtherProvider.AccountQuota.Availability);
            Assert.Equal("tokens 60%", OmniCore.Client.UsagePresentation.Remaining(afterOtherProvider.Remaining));
            var afterOtherStatus = OmniCore.Cli.CliApp.StatusLineText(afterOtherProvider);
            Assert.Contains(ProviderId + " 7d remaining 80% stale", afterOtherStatus);
            Assert.DoesNotContain(otherProvider, afterOtherStatus, StringComparison.Ordinal);

            lock (requestedProviders)
            {
                Assert.Contains(ProviderId, requestedProviders);
                Assert.Contains(otherProvider, requestedProviders);
            }

            // Observability is keyed by SessionId: a fresh session bucket has neither this quota nor context.
            var freshSession = SessionId.New();
            var freshSnapshot = server.Observability.Snapshot(freshSession);
            Assert.Empty(freshSnapshot.Quotas);
            Assert.Null(freshSnapshot.Context);
            Assert.Null(freshSnapshot.PendingContext);
            var activeSnapshot = server.Observability.Snapshot(activeSession);
            Assert.Equal(activeSession.ToString(), activeSnapshot.SessionId);
            Assert.NotNull(activeSnapshot.Context);
            Assert.Contains(activeSnapshot.Quotas, quota => quota.ProviderId == ProviderId);
            Assert.Contains(activeSnapshot.Quotas, quota => quota.ProviderId == otherProvider);
        }
        finally
        {
            if (server?.AcquireStore() is SqliteEventStore store) store.Close();
            ProcessEnvironment.Restore(prior);
            ClearPrivatePools(data, workspace);
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProviderQuotaSnapshot ReportedQuota(string providerId, DateTimeOffset asOf, double remainingPercent) =>
        new(providerId, "private-fixture-account", "synthetic-statusline-query", asOf, MetricAvailability.Reported,
            [new ProviderUsageWindow("seven-day", "fixture-window", 10_080,
                new Metric<double?>(MetricAvailability.Reported, 100d - remainingPercent, "fixture", asOf),
                new Metric<double?>(MetricAvailability.Reported, remainingPercent, "fixture", asOf),
                asOf.AddDays(7), "synthetic seven-day window")], [], null, asOf);

    private static void WriteConfiguration(string configDirectory, string baseUrl)
    {
        File.WriteAllText(Path.Combine(configDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAIResponses
                profile: api
                baseUrl: '{{baseUrl}}'
                auth: none
                billingMode: IncludedQuota
            """);
        File.WriteAllText(Path.Combine(configDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
    }

    private static void ClearPrivatePools(string dataDirectory, string workspace)
    {
        var paths = new DefaultPlatformPaths(dataDirectory);
        var userDatabase = Path.GetFullPath(Path.Combine(dataDirectory, "user.db"));
        using (var userPoolKey = new SqliteConnection("DataSource=" + userDatabase))
            SqliteConnection.ClearPool(userPoolKey);

        var journal = Path.GetFullPath(Path.Combine(OmniHost.WorkspaceDataDirectory(paths, workspace), "journal.db"));
        using var journalPoolKey = new SqliteConnection("DataSource=" + journal);
        SqliteConnection.ClearPool(journalPoolKey);
    }

    private sealed record CapturedRequest(string Path, string Body);

    private sealed class LoopbackResponsesServer : IAsyncDisposable
    {
        private const string ResponseBody =
            "event: response.output_item.added\n" +
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"role\":\"assistant\"}}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"Fixture answer\"}\n\n" +
            "event: response.output_item.done\n" +
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Fixture answer\"}]}}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":17,\"output_tokens\":4}}}\n\n";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private readonly object _gate = new();
        private readonly List<CapturedRequest> _requests = [];

        public string BaseUrl { get; }
        public IReadOnlyList<CapturedRequest> Requests
        {
            get { lock (_gate) return _requests.ToArray(); }
        }

        public LoopbackResponsesServer()
        {
            _stop.CancelAfter(TimeSpan.FromSeconds(30));
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }
                await HandleAsync(client, _stop.Token);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var headerBuffer = new MemoryStream();
                var oneByte = new byte[1];
                while (true)
                {
                    var count = await stream.ReadAsync(oneByte.AsMemory(), cancellationToken);
                    if (count == 0) throw new IOException("Loopback request ended before headers completed.");
                    headerBuffer.WriteByte(oneByte[0]);
                    var length = checked((int)headerBuffer.Length);
                    if (length >= 4)
                    {
                        var bytes = headerBuffer.GetBuffer();
                        if (bytes[length - 4] == (byte)'\r' && bytes[length - 3] == (byte)'\n'
                            && bytes[length - 2] == (byte)'\r' && bytes[length - 1] == (byte)'\n') break;
                    }
                }

                var headerText = Encoding.ASCII.GetString(headerBuffer.GetBuffer(), 0, checked((int)headerBuffer.Length));
                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var contentLength = 0;
                foreach (var line in lines.Skip(1))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0 && line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line[(separator + 1)..].Trim(), out contentLength);
                }

                var bodyBytes = new byte[contentLength];
                var read = 0;
                while (read < bodyBytes.Length)
                {
                    var count = await stream.ReadAsync(bodyBytes.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }
                lock (_gate) _requests.Add(new CapturedRequest(requestLine[1], Encoding.UTF8.GetString(bodyBytes, 0, read)));

                var responseBytes = Encoding.UTF8.GetBytes(ResponseBody);
                var responseHeaders = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + responseBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "x-ratelimit-limit-tokens: 1000", "x-ratelimit-remaining-tokens: 600") + "\r\n\r\n");
                await stream.WriteAsync(responseHeaders, cancellationToken);
                await stream.WriteAsync(responseBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _serve; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }
    }

    private sealed record ProcessEnvironment(string? Data, string? Config, string? Model, string? BaseUrl)
    {
        public static ProcessEnvironment Capture() => new(
            Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable),
            Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable),
            Environment.GetEnvironmentVariable("OMNI_MODEL"),
            Environment.GetEnvironmentVariable("OMNI_BASE_URL"));

        public static void Set(string data, string config, string model)
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("OMNI_MODEL", model);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
        }

        public static void Restore(ProcessEnvironment prior)
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, prior.Data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, prior.Config);
            Environment.SetEnvironmentVariable("OMNI_MODEL", prior.Model);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", prior.BaseUrl);
        }
    }
}
