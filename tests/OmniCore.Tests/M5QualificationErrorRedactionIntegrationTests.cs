using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Qualification;
using OmniCore.Security;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class M5QualificationErrorRedactionIntegrationTests
{
    [Fact]
    public void Incomplete_exception_redacts_and_snapshots_caller_failures()
    {
        var sentinel = "qualification-exception-secret-" + Guid.NewGuid().ToString("N");
        _ = Secret.Of(sentinel);
        var mutableFailures = new List<string> { "provider: " + sentinel };

        var exception = new ModelQualificationSuiteIncompleteException(mutableFailures);
        mutableFailures[0] = "caller mutation after construction";

        Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, string.Join("; ", exception.Failures), StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Marker, exception.Message, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Marker, Assert.Single(exception.Failures), StringComparison.Ordinal);
        Assert.DoesNotContain("caller mutation", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_error_sentinel_is_redacted_at_host_and_cli_boundaries(bool throughCli)
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-error-redaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sentinel = "qualification-error-secret-" + Guid.NewGuid().ToString("N");
        _ = Secret.Of(sentinel); // synthetic canary registered with the process-wide redactor
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");

        try
        {
            await using var fixture = new JsonErrorFixture(sentinel);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", fixture.BaseUrl);
            WriteConfiguration(directory, fixture.BaseUrl);

            // Control: this exact process-wide registered value is known to the shared redactor.
            var redactedControl = OmniCliRuntime.RedactSensitive("fixture=" + sentinel);
            Assert.DoesNotContain(sentinel, redactedControl, StringComparison.Ordinal);
            Assert.Contains(SecretRedactor.Marker, redactedControl, StringComparison.Ordinal);

            if (!throughCli)
            {
                using var host = ModelQualificationHost.Create(directory);
                var exception = await Assert.ThrowsAsync<ModelQualificationSuiteIncompleteException>(() =>
                    host.QualifyAsync("qwen-test", new QualificationOptions
                    {
                        Suite = "quick",
                        ConsentGiven = true,
                        MaxTotalCostUsd = decimal.MaxValue,
                    }, timeout.Token));

                Assert.NotEmpty(exception.Failures);
                Assert.DoesNotContain(sentinel, string.Join("; ", exception.Failures), StringComparison.Ordinal);
                Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
                Assert.Contains(SecretRedactor.Marker, string.Join("; ", exception.Failures), StringComparison.Ordinal);
                Assert.Equal(QuickProbeSuite.Probes().Count, fixture.RequestCount);
            }
            else
            {
                var output = new StringWriter();
                var command = OmniCore.Cli.ModelPolicyCommands.Run(
                    new[] { "model", "qualify", "qwen-test", "--yes" },
                    new StringReader(""), output, interactive: false, dataDirectoryOverride: directory,
                    cancellationToken: timeout.Token);
                var exitCode = await command.WaitAsync(TimeSpan.FromSeconds(20), timeout.Token);

                Assert.Equal(2, exitCode);
                Assert.Equal(QuickProbeSuite.Probes().Count, fixture.RequestCount);
                Assert.DoesNotContain(sentinel, output.ToString(), StringComparison.Ordinal);
                Assert.Contains(SecretRedactor.Marker, output.ToString(), StringComparison.Ordinal);
            }
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Empty(store.List(CancellationToken.None));
            var blobs = Path.Combine(directory, "blobs");
            if (Directory.Exists(blobs))
                Assert.Empty(Directory.EnumerateFiles(blobs, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
            var databasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
            using (var connection = new SqliteConnection("DataSource=" + databasePath))
            {
                SqliteConnection.ClearPool(connection);
            }

            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteConfiguration(string directory, string baseUrl)
    {
        var paths = OmniHost.CreatePlatformPaths(directory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              local:
                family: OpenAiChatCompatible
                baseUrl: {{baseUrl}}
                auth: none
                billingMode: Local
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), """
            models:
              qwen-test:
                provider: local
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
    }

    private sealed class JsonErrorFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly byte[] _errorBody;
        private int _requestCount;

        public JsonErrorFixture(string sentinel)
        {
            _errorBody = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
            {
                error = new { message = "fixture failure: " + sentinel },
            }));
            _listener.Start();
            _stop.CancelAfter(TimeSpan.FromSeconds(30));
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _server = ServeAsync();
        }

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var requestBody = await ReadRequestBodyAsync(stream, _stop.Token);
                    var requestNumber = Interlocked.Increment(ref _requestCount) - 1;
                    var (status, contentType, responseBody) = ResponseFor(requestNumber, requestBody);
                    var responseBytes = Encoding.UTF8.GetBytes(responseBody);
                    var statusLine = status == 400 ? "HTTP/1.1 400 Bad Request" : "HTTP/1.1 200 OK";
                    var headers = Encoding.ASCII.GetBytes(string.Join("\r\n", statusLine,
                        "Content-Type: " + contentType,
                        "Connection: close",
                        "Content-Length: " + responseBytes.Length) + "\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(responseBytes, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private (int Status, string ContentType, string Body) ResponseFor(int requestNumber, byte[] requestBody)
        {
            // One real 400 error per Quick run, followed by nine valid responses. Successful
            // calls reset the real breaker so exact 10-request assertions prove the full suite
            // reached HTTP rather than short-circuiting on a circuit or preflight guard.
            if (requestNumber % QuickProbeSuite.Probes().Count == 0)
                return (400, "application/json; charset=utf-8", Encoding.UTF8.GetString(_errorBody));

            using var request = System.Text.Json.JsonDocument.Parse(requestBody);
            var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            var probe = Assert.Single(QuickProbeSuite.Probes(), candidate => candidate.Prompt == prompt);
            var eventBody = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = probe.Expected }, finish_reason = (string?)null } },
            });
            var body = string.Join("\n", "data: " + eventBody, "",
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}", "",
                "data: [DONE]", "");
            return (200, "text/event-stream", body);
        }

        private static async Task<byte[]> ReadRequestBodyAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            var oneByte = new byte[1];
            while (headerBytes.Count < 64 * 1024)
            {
                var read = await stream.ReadAsync(oneByte.AsMemory(), cancellationToken);
                if (read == 0) throw new IOException("Fixture client closed before the HTTP headers completed.");
                headerBytes.Add(oneByte[0]);
                var count = headerBytes.Count;
                if (count >= 4 && headerBytes[count - 4] == '\r' && headerBytes[count - 3] == '\n'
                    && headerBytes[count - 2] == '\r' && headerBytes[count - 1] == '\n') break;
            }

            if (headerBytes.Count >= 64 * 1024) throw new InvalidDataException("Fixture HTTP headers exceeded 64 KiB.");
            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var contentLength = 0;
            foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out contentLength) || contentLength < 0)
                    throw new InvalidDataException("Fixture received an invalid Content-Length.");
                break;
            }

            var body = new byte[contentLength];
            var readTotal = 0;
            while (readTotal < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(readTotal), cancellationToken);
                if (read == 0) throw new IOException("Fixture client closed before the request body completed.");
                readTotal += read;
            }

            return body;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _server.WaitAsync(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }
}
