// Offline integration coverage with the normal configured provider path.
// Uses the configured OpenAI Responses adapter against a private loopback SSE endpoint; no API key,
// subscription session, injected provider, or external network is involved.
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
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class M5QualificationIncompleteResponseIntegrationTests
{
    private const string ModelId = "m5-incomplete-response-model";
    private const string ProviderId = "m5-incomplete-response-provider";

    [Fact]
    public async Task Unknown_incomplete_reason_with_exact_probe_text_fails_suite_without_profile_or_evidence()
    {
        await AssertIncompleteSuiteAsync("unrecognized_fixture_reason");
    }

    [Fact]
    public async Task Max_output_tokens_with_exact_probe_text_is_not_rejected_by_a_blanket_non_end_turn_rule()
    {
        var directory = CreatePrivateDirectory();
        var previousBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        try
        {
            await using var server = new LoopbackResponsesServer();
            server.RespondWith(body => QuickResponsesFixture(body, "max_output_tokens"));
            var paths = Configure(directory, server.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", server.BaseUrl);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            var key = ModelQualificationHost.QualificationKeyFor(model, provider, server.BaseUrl);

            QualificationRunResult result;
            using (var host = ModelQualificationHost.Create(directory))
            {
                result = await host.QualifyAsync(ModelId, new QualificationOptions
                {
                    Suite = "quick",
                    ConsentGiven = true,
                    MaxTotalCostUsd = 1m,
                }, CancellationToken.None);
            }

            Assert.Equal(ModelQualificationState.Qualified.ToString(), result.NewState);
            Assert.True(result.SuiteComplete);
            Assert.Equal(10, result.Probes.Count);
            Assert.Equal(10, server.Requests.Count);
            AssertWireIsPrivateAndUnauthenticated(server.Requests);
            Assert.All(result.Probes, probe =>
            {
                Assert.Equal("Passed", probe.Status);
                Assert.Null(probe.CostUsd);
                Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), probe.Usage);
            });

            using var store = OmniHost.CreateModelQualificationStore(directory);
            var profile = Assert.IsType<ModelQualificationProfile>(store.Get(key, CancellationToken.None));
            Assert.Equal(ModelQualificationState.Qualified, profile.State);
            var evidence = Assert.IsType<ModelQualificationEvidence>(
                store.Evidence(key, profile.ProfileRevision, CancellationToken.None));
            Assert.Equal(result.EvidenceHash, evidence.Artifact.Hash.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBaseUrl);
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertIncompleteSuiteAsync(string incompleteReason)
    {
        var directory = CreatePrivateDirectory();
        var previousBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        try
        {
            await using var server = new LoopbackResponsesServer();
            server.RespondWith(body => QuickResponsesFixture(body, incompleteReason));
            var paths = Configure(directory, server.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", server.BaseUrl);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            var key = ModelQualificationHost.QualificationKeyFor(model, provider, server.BaseUrl);

            using (var host = ModelQualificationHost.Create(directory))
            {
                await Assert.ThrowsAsync<ModelQualificationSuiteIncompleteException>(() => host.QualifyAsync(ModelId,
                    new QualificationOptions
                    {
                        Suite = "quick",
                        ConsentGiven = true,
                        MaxTotalCostUsd = 1m,
                    }, CancellationToken.None));
            }

            Assert.Equal(10, server.Requests.Count);
            AssertWireIsPrivateAndUnauthenticated(server.Requests);
            Assert.All(server.Requests, request => Assert.Equal("POST", request.Method));

            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Empty(store.List(CancellationToken.None));
            Assert.Null(store.Get(key, CancellationToken.None));
            Assert.Null(store.Evidence(key, 1, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBaseUrl);
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertWireIsPrivateAndUnauthenticated(IReadOnlyList<CapturedRequest> requests)
    {
        Assert.Equal(10, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("/responses", request.Path);
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.False(request.Headers.ContainsKey("api-key"));
            Assert.False(request.Headers.ContainsKey("x-api-key"));
            using var body = JsonDocument.Parse(request.Body);
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal(ModelId, body.RootElement.GetProperty("model").GetString());
            Assert.Equal(2048, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        });
    }

    private static string CreatePrivateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-incomplete-response-",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IPlatformPaths Configure(string directory, string loopbackBaseUrl)
    {
        var paths = OmniHost.CreatePlatformPaths(directory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAIResponses
                profile: api
                baseUrl: '{{loopbackBaseUrl}}'
                auth: none
                billingMode: Local
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

    private static string QuickResponsesFixture(string requestBody, string incompleteReason)
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
            "event: response.incomplete",
            "data: " + JsonSerializer.Serialize(new
            {
                type = "response.incomplete",
                response = new
                {
                    status = "incomplete",
                    incomplete_details = new { reason = incompleteReason },
                    usage = new { input_tokens = 17, output_tokens = 4 },
                },
            }),
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
                foreach (var value in JsonStrings(property.Value)) yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                foreach (var value in JsonStrings(child)) yield return value;
        }
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

    private sealed record CapturedRequest(string Method, string Path,
        Dictionary<string, string> Headers, string Body);

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
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _serve = ServeAsync();
        }

        public void RespondWith(Func<string, string> response) => _response = response;

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

                var headerLines = Encoding.ASCII.GetString(headerBuffer.GetBuffer(), 0,
                    checked((int)headerBuffer.Length)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var requestLine = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var contentLength = 0;
                foreach (var line in headerLines.Skip(1))
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
                lock (_requestLock) _requests.Add(new CapturedRequest(requestLine[0], requestLine[1], headers, body));
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
