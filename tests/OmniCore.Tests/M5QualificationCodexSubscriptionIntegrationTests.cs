using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Exercises the normal configured Codex provider path with only a synthetic OAuth session and
/// a private loopback Responses endpoint. This does not use an injected model provider or real login.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class M5QualificationCodexSubscriptionIntegrationTests
{
    private const string ModelId = "m5-codex-subscription-model";
    private const string ProviderId = "m5-codex-subscription-provider";
    private const string SyntheticAccessToken = "synthetic-codex-access-token-for-loopback-only";
    private const string SyntheticAccountId = "synthetic-account-loopback";

    [Fact]
    public async Task Normal_configured_codex_qualification_uses_private_subscription_session_and_persists_unknown_cost_evidence()
    {
        var directory = CreatePrivateDirectory();
        var previousBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");

        try
        {
            await using var server = new LoopbackResponsesServer();
            server.RespondWith(QuickResponsesFixture);
            var paths = Configure(directory, server.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", server.BaseUrl);
            SaveSyntheticSubscription(paths);

            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            Assert.Equal(AuthKind.None, provider.Auth.Kind);
            Assert.Null(provider.Auth.SecretRef);
            var key = ModelQualificationHost.QualificationKeyFor(model, provider, server.BaseUrl);

            QualificationRunResult result;
            using (var host = ModelQualificationHost.Create(directory))
            {
                result = await host.QualifyAsync(ModelId, new QualificationOptions
                {
                    Suite = "quick",
                    ConsentGiven = true,
                    MaxTotalCostUsd = 1m,
                    QueryQuota = (id, _) => Task.FromResult(new ProviderQuotaSnapshot(id, null,
                        "private-loopback-fixture:no-account-query", DateTimeOffset.UtcNow,
                        MetricAvailability.Unknown, [], [], "Offline fixture; no authenticated quota query.")),
                }, CancellationToken.None);

                Assert.Equal(ModelQualificationState.Qualified.ToString(), result.NewState);
                Assert.True(result.SuiteComplete);
                Assert.Equal(key.QualificationKeyHash(), result.KeyHash);
                Assert.NotNull(host.Get(model, provider, CancellationToken.None));
            }

            Assert.Equal(10, server.Requests.Count);
            Assert.All(server.Requests, request =>
            {
                Assert.Equal("/codex/responses", request.Path);
                Assert.Equal("Bearer " + SyntheticAccessToken, request.Headers["Authorization"]);
                Assert.Equal(SyntheticAccountId, request.Headers["ChatGPT-Account-Id"]);
                Assert.Equal("omnicore", request.Headers["originator"]);
                Assert.False(request.Headers.ContainsKey("api-key"));
                Assert.False(request.Headers.ContainsKey("x-api-key"));
                Assert.False(request.Headers.ContainsKey("openai-api-key"));
            });

            Assert.Equal(10, result.Probes.Count);
            Assert.All(result.Probes, probe =>
            {
                Assert.Equal("Passed", probe.Status);
                Assert.Null(probe.CostUsd);
                Assert.NotNull(probe.Usage);
                Assert.Equal(17, probe.Usage!.Input);
                Assert.Equal(4, probe.Usage.Output);
                Assert.Null(probe.Usage.CacheRead);
                Assert.Null(probe.Usage.CacheWrite);
                Assert.Null(probe.Usage.Reasoning);
            });

            Assert.StartsWith("sha256:", result.EvidenceHash!);
            using (var store = OmniHost.CreateModelQualificationStore(directory))
            {
                var profile = Assert.IsType<ModelQualificationProfile>(store.Get(key, CancellationToken.None));
                Assert.Equal(ModelQualificationState.Qualified, profile.State);
                var evidence = Assert.IsType<ModelQualificationEvidence>(
                    store.Evidence(key, profile.ProfileRevision, CancellationToken.None));
                Assert.Equal(result.EvidenceHash, evidence.Artifact.Hash.ToString());
            }

            var artifactHash = ContentHash.Sha256(result.EvidenceHash!["sha256:".Length..]);
            var evidenceJson = new FileArtifactStore(directory).GetText(artifactHash);
            Assert.NotNull(evidenceJson);
            Assert.DoesNotContain(SyntheticAccessToken, evidenceJson!, StringComparison.Ordinal);
            using var evidenceDocument = JsonDocument.Parse(evidenceJson!);
            Assert.Equal(server.BaseUrl,
                evidenceDocument.RootElement.GetProperty("key").GetProperty("endpoint").GetString());
            Assert.Equal("configured-provider", evidenceDocument.RootElement.GetProperty("source").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBaseUrl);
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Consent_and_declared_cost_cap_reject_before_any_codex_http_request_even_with_private_session()
    {
        var directory = CreatePrivateDirectory();
        var previousBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");

        try
        {
            await using var server = new LoopbackResponsesServer();
            server.RespondWith(QuickResponsesFixture);
            var paths = Configure(directory, server.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", server.BaseUrl);
            SaveSyntheticSubscription(paths);

            using var host = ModelQualificationHost.Create(directory);
            await Assert.ThrowsAsync<ModelQualificationConsentException>(() => host.QualifyAsync(ModelId,
                new QualificationOptions { Suite = "quick", ConsentGiven = false, MaxTotalCostUsd = 1m },
                CancellationToken.None));

            var cappedProbes = QuickProbeSuite.Probes().Select(probe => new Probe(probe.Id, probe.Kind,
                probe.Prompt, probe.Expected, 0.01m)).ToArray();
            await Assert.ThrowsAsync<ModelQualificationCostCapException>(() => host.QualifyAsync(ModelId,
                new QualificationOptions
                {
                    Suite = "quick",
                    Probes = cappedProbes,
                    ConsentGiven = true,
                    MaxTotalCostUsd = 0.05m,
                }, CancellationToken.None));

            Assert.Empty(server.Requests);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Null(store.Get(ModelQualificationHost.QualificationKeyFor(
                OmniHost.LoadUserConfiguration(paths).Registry.Model(ModelId)!,
                OmniHost.LoadUserConfiguration(paths).Registry.Provider(ProviderId)!, server.BaseUrl),
                CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBaseUrl);
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_private_subscription_session_is_incomplete_without_http_or_persisted_profile()
    {
        var directory = CreatePrivateDirectory();
        var previousBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");

        try
        {
            await using var server = new LoopbackResponsesServer();
            server.RespondWith(QuickResponsesFixture);
            var paths = Configure(directory, server.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", server.BaseUrl);

            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            var key = ModelQualificationHost.QualificationKeyFor(model, provider, server.BaseUrl);

            using (var host = ModelQualificationHost.Create(directory))
            {
                await Assert.ThrowsAsync<ModelQualificationSuiteIncompleteException>(() => host.QualifyAsync(ModelId,
                    new QualificationOptions { Suite = "quick", ConsentGiven = true, MaxTotalCostUsd = 1m },
                    CancellationToken.None));
            }

            Assert.Empty(server.Requests);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Null(store.Get(key, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBaseUrl);
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreatePrivateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-codex-subscription-",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IPlatformPaths Configure(string dataDirectory, string loopbackBaseUrl)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAIResponses
                profile: codex
                baseUrl: '{{loopbackBaseUrl}}'
                auth: none
                billingMode: IncludedQuota
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
        return paths;
    }

    private static void SaveSyntheticSubscription(IPlatformPaths paths)
    {
        var now = DateTimeOffset.UtcNow;
        var session = JsonSerializer.Serialize(new
        {
            access = SyntheticAccessToken,
            refresh = "synthetic-refresh-token-loopback-only",
            expires = now.AddHours(3).ToUnixTimeSeconds(),
            account_id = SyntheticAccountId,
            saved = now.ToUnixTimeSeconds(),
        });
        OmniHost.CreateUserCredentialStore(paths).Save(ChatGptSubscriptionAuthProvider.CredentialKey,
            session, CancellationToken.None);
    }

    private static string QuickResponsesFixture(string requestBody)
    {
        using var request = JsonDocument.Parse(requestBody);
        var requestStrings = JsonStrings(request.RootElement).ToHashSet(StringComparer.Ordinal);
        var probe = QuickProbeSuite.Probes().Single(candidate => requestStrings.Contains(candidate.Prompt));
        return string.Join("\n",
            "event: response.output_item.added",
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"role\":\"assistant\"}}",
            "",
            "event: response.output_text.delta",
            "data: " + JsonSerializer.Serialize(new
            {
                type = "response.output_text.delta", output_index = 0, delta = probe.Expected,
            }),
            "",
            "event: response.output_item.done",
            "data: " + JsonSerializer.Serialize(new
            {
                type = "response.output_item.done", output_index = 0,
                item = new { type = "message", content = new[] { new { type = "output_text", text = probe.Expected } } },
            }),
            "",
            "event: response.completed",
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":17,\"output_tokens\":4}}}",
            "");
    }

    private static IEnumerable<string> JsonStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return element.GetString()!;
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var value in JsonStrings(property.Value)) yield return value;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                foreach (var value in JsonStrings(child)) yield return value;
    }

    private static void ClearPrivateUserDatabasePool(string directory)
    {
        var databasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
        using var poolIdentity = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        SqliteConnection.ClearPool(poolIdentity);
    }

    private sealed record CapturedRequest(string Path, Dictionary<string, string> Headers, string Body);

    private sealed class LoopbackResponsesServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private Func<string, string>? _response;
        private readonly object _requestLock = new();
        private readonly List<CapturedRequest> _requests = [];

        public string BaseUrl { get; }
        public IReadOnlyList<CapturedRequest> Requests
        {
            get { lock (_requestLock) return _requests.ToArray(); }
        }

        public LoopbackResponsesServer()
        {
            _stop.CancelAfter(TimeSpan.FromSeconds(30));
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _serve = ServeAsync();
        }

        public void RespondWith(Func<string, string> response) => _response = response;

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
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

                var headersText = Encoding.ASCII.GetString(headerBuffer.GetBuffer(), 0,
                    checked((int)headerBuffer.Length));
                var lines = headersText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var contentLength = 0;
                foreach (var line in lines.Skip(1))
                {
                    var separator = line.IndexOf(':');
                    if (separator < 0) continue;
                    var name = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim();
                    headers[name] = value;
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(value, out contentLength);
                }

                var bodyBytes = new byte[contentLength];
                var read = 0;
                while (read < bodyBytes.Length)
                {
                    var count = await stream.ReadAsync(bodyBytes.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }

                var body = Encoding.UTF8.GetString(bodyBytes, 0, read);
                lock (_requestLock) _requests.Add(new CapturedRequest(requestLine[1], headers, body));
                var payload = _response?.Invoke(body)
                    ?? throw new InvalidOperationException("Loopback Responses fixture was not configured.");
                var payloadBytes = Encoding.UTF8.GetBytes(payload);
                var responseHeaders = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + payloadBytes.Length) + "\r\n\r\n");
                await stream.WriteAsync(responseHeaders, cancellationToken);
                await stream.WriteAsync(payloadBytes, cancellationToken);
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
}
