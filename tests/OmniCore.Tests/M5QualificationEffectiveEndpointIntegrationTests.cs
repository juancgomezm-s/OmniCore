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

/// <summary>
/// End-to-end identity check for a configured provider endpoint overridden by OMNI_BASE_URL.
/// Both endpoints are private loopback fixtures, and the Host constructs the configured adapter;
/// no provider is injected and no external authentication or billing is involved.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class M5QualificationEffectiveEndpointIntegrationTests
{
    private const string ModelId = "m5-effective-endpoint-model";
    private const string ProviderId = "m5-effective-endpoint-provider";

    [Fact]
    public async Task Normal_qualification_connects_to_and_persists_identity_for_effective_override_endpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-effective-endpoint-",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");

        try
        {
            await using var configuredEndpoint = new LoopbackChatServer();
            await using var effectiveEndpoint = new LoopbackChatServer();
            configuredEndpoint.RespondWith(QuickFixtureResponse);
            effectiveEndpoint.RespondWith(QuickFixtureResponse);

            var paths = OmniHost.CreatePlatformPaths(directory);
            Directory.CreateDirectory(paths.ConfigDirectory);
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
                providers:
                  {{ProviderId}}:
                    family: OpenAiChatCompatible
                    baseUrl: '{{configuredEndpoint.BaseUrl}}'
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
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", effectiveEndpoint.BaseUrl);

            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;
            var effectiveKey = ModelQualificationHost.QualificationKeyFor(model, descriptor,
                effectiveEndpoint.BaseUrl);
            var configuredKey = ModelQualificationHost.QualificationKeyFor(model, descriptor,
                configuredEndpoint.BaseUrl);

            QualificationRunResult result;
            using (var host = ModelQualificationHost.Create(directory))
            {
                result = await host.QualifyAsync(ModelId, new QualificationOptions
                {
                    Suite = "quick",
                    ConsentGiven = true,
                    MaxTotalCostUsd = 1m,
                }, CancellationToken.None);

                Assert.Equal(ModelQualificationState.Qualified.ToString(), result.NewState);
                Assert.True(result.SuiteComplete);
                Assert.Equal(10, effectiveEndpoint.RequestCount);
                Assert.Equal(0, configuredEndpoint.RequestCount);
                Assert.Equal(effectiveKey.QualificationKeyHash(), result.KeyHash);
                Assert.NotNull(host.Get(model, descriptor, CancellationToken.None));

                // The same configured model under endpoint A is a different qualification identity.
                Environment.SetEnvironmentVariable("OMNI_BASE_URL", configuredEndpoint.BaseUrl);
                Assert.Null(host.Get(model, descriptor, CancellationToken.None));
                Environment.SetEnvironmentVariable("OMNI_BASE_URL", effectiveEndpoint.BaseUrl);
            }

            Assert.StartsWith("sha256:", result.EvidenceHash!);
            using (var store = OmniHost.CreateModelQualificationStore(directory))
            {
                var profile = Assert.IsType<ModelQualificationProfile>(
                    store.Get(effectiveKey, CancellationToken.None));
                Assert.Equal(ModelQualificationState.Qualified, profile.State);
                Assert.Null(store.Get(configuredKey, CancellationToken.None));
                var evidence = Assert.IsType<ModelQualificationEvidence>(
                    store.Evidence(effectiveKey, profile.ProfileRevision, CancellationToken.None));
                Assert.Equal(result.EvidenceHash, evidence.Artifact.Hash.ToString());

                var artifactHash = ContentHash.Sha256(result.EvidenceHash!["sha256:".Length..]);
                using var document = JsonDocument.Parse(new FileArtifactStore(directory).GetText(artifactHash)!);
                Assert.Equal(effectiveEndpoint.BaseUrl,
                    document.RootElement.GetProperty("key").GetProperty("endpoint").GetString());
                Assert.Equal("configured-provider", document.RootElement.GetProperty("source").GetString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBaseUrl);
            using var poolIdentity = new SqliteConnection("DataSource=" + Path.GetFullPath(Path.Combine(directory, "user.db")));
            SqliteConnection.ClearPool(poolIdentity);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string QuickFixtureResponse(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var prompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        var probe = Assert.Single(QuickProbeSuite.Probes(), candidate => candidate.Prompt == prompt);
        var eventBody = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = probe.Expected }, finish_reason = (string?)null } },
        });
        return string.Join("\n", "data: " + eventBody, "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}", "",
            "data: [DONE]", "");
    }

    private sealed class LoopbackChatServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private Func<string, string>? _response;
        private int _requestCount;

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        public LoopbackChatServer()
        {
            _stop.CancelAfter(TimeSpan.FromSeconds(30));
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
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
                    if (count == 0) throw new IOException("Loopback request ended before its headers completed.");
                    headerBuffer.WriteByte(oneByte[0]);
                    var length = checked((int)headerBuffer.Length);
                    if (length >= 4)
                    {
                        var headerBytes = headerBuffer.GetBuffer();
                        if (headerBytes[length - 4] == (byte)'\r' && headerBytes[length - 3] == (byte)'\n'
                            && headerBytes[length - 2] == (byte)'\r' && headerBytes[length - 1] == (byte)'\n')
                            break;
                    }
                }

                var headersText = Encoding.ASCII.GetString(headerBuffer.GetBuffer(), 0,
                    checked((int)headerBuffer.Length));
                var contentLength = 0;
                foreach (var header in headersText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                }

                var body = new byte[contentLength];
                var read = 0;
                while (read < body.Length)
                {
                    var count = await stream.ReadAsync(body.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }

                Interlocked.Increment(ref _requestCount);
                var payload = _response?.Invoke(Encoding.UTF8.GetString(body, 0, read))
                    ?? throw new InvalidOperationException("No loopback fixture response was configured.");
                var bytes = Encoding.UTF8.GetBytes(payload);
                var headers = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + bytes.Length) + "\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
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
