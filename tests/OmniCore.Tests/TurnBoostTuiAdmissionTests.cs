using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class TurnBoostTuiAdmissionTests
{
    private const string ProviderId = "turn-boost-loopback";
    private const string ModelId = "turn-boost-responses-fixture";

    [Fact]
    public async Task Pre_turn_capability_rejection_releases_boost_for_the_next_admission()
    {
        var root = TempDir();
        var endpoint = new ScriptedResponsesEndpoint();
        var previousData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var previousConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var previousBase = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        var previousModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        SqliteEventStore? journal = null;
        Exception? primaryFailure = null;
        var firstDiagnostics = new ConcurrentQueue<string>();
        using var operationStop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        operationStop.CancelAfter(TimeSpan.FromSeconds(20));
        Task<int>? firstOperation = null;
        Task<int>? retryOperation = null;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "data", "config"));
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", endpoint.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_MODEL", ModelId);
            Directory.CreateDirectory(Path.Combine(root, "workspace"));
            WriteConfiguration(Path.Combine(root, "data"), endpoint.BaseUrl, supported: false);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "data", "config")),
                Path.GetFullPath(OmniHost.CreatePlatformPaths().ConfigDirectory));

            var runtime = OmniCliRuntime.Create(Path.Combine(root, "workspace"));
            var host = new TuiTurnHost(runtime);
            Assert.True(host.SetNextTurnReasoningBoost(new ReasoningRequest("high", null)));

            firstOperation = Task.Run(async () => await host.ExecuteActAsync("first admission",
                firstDiagnostics.Enqueue, operationStop.Token));
            var firstExit = 0;
            var firstFailure = await Record.ExceptionAsync(async () =>
                firstExit = await firstOperation.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.True(firstFailure is not null || firstExit != 0,
                "The unsupported explicit boost should fail before a Turn/provider call. Diagnostics: "
                + string.Join(" | ", firstDiagnostics));
            Assert.Equal(0, endpoint.RequestCount);

            var server = Assert.IsType<OmniServer>(runtime.Connect(TestContext.Current.CancellationToken));
            journal = Assert.IsType<SqliteEventStore>(server.AcquireStore());
            if (server.LastSessionId() is { } failedAdmissionSession)
            {
                var eventsBeforeRetry = server.AcquireStore().ReadFrom(failedAdmissionSession, 1)
                    .Select(server.AcquireCodecs().Decode).ToArray();
                Assert.Empty(eventsBeforeRetry.OfType<TurnStarted>());
            }

            // The first admission failed before a durable TurnStarted. Make only the private
            // fixture route compatible; the same pending TUI boost must now be admitted.
            WriteConfiguration(Path.Combine(root, "data"), endpoint.BaseUrl, supported: true);
            retryOperation = Task.Run(async () => await host.ExecuteActAsync("retry after pre-turn rejection", _ => { },
                operationStop.Token));
            var retryExit = await retryOperation.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            Assert.Equal(0, retryExit);
            Assert.Equal(1, endpoint.RequestCount);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var events = server.AcquireStore().ReadFrom(session, 1).Select(server.AcquireCodecs().Decode).ToArray();
            var boostedTurn = Assert.Single(events.OfType<TurnStarted>(), turn =>
                turn.ReasoningResolution?.Source == ReasoningSelectionSource.TurnBoost);
            Assert.NotNull(boostedTurn.ReasoningResolution!.TurnBoostId);
            Assert.NotEqual(Guid.Empty, boostedTurn.ReasoningResolution.TurnBoostId!.Value);
            Assert.Equal(new ReasoningRequest("high", null), boostedTurn.ReasoningResolution.AppliedRequest);
            var boostedStep = Assert.Single(events.OfType<ModelStepStarted>(), step => step.TurnId == boostedTurn.TurnId);
            Assert.True(boostedTurn.ReasoningResolution.IsEquivalentTo(boostedStep.ReasoningResolution));
            Assert.Equal("high", endpoint.Requests.Single().ReasoningEffort);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            operationStop.Cancel();
            endpoint.ReleaseHeldRequests();
            Exception? cleanupFailure = null;
            void RecordCleanupFailure(Exception exception) => cleanupFailure = cleanupFailure is null
                ? exception : new AggregateException(cleanupFailure, exception);
            try { await endpoint.DisposeAsync(); } catch (Exception exception) { RecordCleanupFailure(exception); }
            try { await DrainOperationAsync(firstOperation, operationStop.Token); } catch (Exception exception) { RecordCleanupFailure(exception); }
            try { await DrainOperationAsync(retryOperation, operationStop.Token); } catch (Exception exception) { RecordCleanupFailure(exception); }
            try { CloseRuntimeJournal(journal); } catch (Exception exception) { RecordCleanupFailure(exception); }
            try { CloseExactRuntimePools(root); } catch (Exception exception) { RecordCleanupFailure(exception); }
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", previousConfig);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBase);
            Environment.SetEnvironmentVariable("OMNI_MODEL", previousModel);
            if (cleanupFailure is null) Directory.Delete(root, recursive: true);
            else if (primaryFailure is not null) primaryFailure.Data["TUI fixture cleanup failure"] = cleanupFailure;
            else throw cleanupFailure;
        }
    }

    [Fact]
    public async Task Concurrent_tui_calls_cannot_claim_the_same_boost_after_durable_turn_start()
    {
        var root = TempDir();
        var endpoint = new ScriptedResponsesEndpoint(holdFirstRequest: true);
        var previousData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var previousConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var previousBase = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        var previousModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        SqliteEventStore? journal = null;
        Exception? primaryFailure = null;
        var firstDiagnostics = new ConcurrentQueue<string>();
        var secondDiagnostics = new ConcurrentQueue<string>();
        using var operationStop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        operationStop.CancelAfter(TimeSpan.FromSeconds(20));
        Task<int>? firstOperation = null;
        Task<int>? secondOperation = null;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "data", "config"));
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", endpoint.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_MODEL", ModelId);
            Directory.CreateDirectory(Path.Combine(root, "workspace"));
            WriteConfiguration(Path.Combine(root, "data"), endpoint.BaseUrl, supported: true);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "data", "config")),
                Path.GetFullPath(OmniHost.CreatePlatformPaths().ConfigDirectory));

            var runtime = OmniCliRuntime.Create(Path.Combine(root, "workspace"));
            var server = Assert.IsType<OmniServer>(runtime.Connect(TestContext.Current.CancellationToken));
            journal = Assert.IsType<SqliteEventStore>(server.AcquireStore());
            var host = new TuiTurnHost(runtime);
            Assert.True(host.SetNextTurnReasoningBoost(new ReasoningRequest("high", null)));

            firstOperation = Task.Run(async () => await host.ExecuteActAsync("first concurrent action", firstDiagnostics.Enqueue,
                operationStop.Token));
            var firstProgress = await Task.WhenAny(endpoint.FirstRequest, firstOperation)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            if (firstProgress == firstOperation)
            {
                var firstExit = await firstOperation;
                Assert.True(firstExit == 0 && endpoint.RequestCount > 0,
                    $"The first TUI action ended before reaching its private provider (exit {firstExit}). "
                    + "Diagnostics: " + string.Join(" | ", firstDiagnostics));
            }
            else
            {
                await endpoint.FirstRequest.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }
            var firstSession = Assert.IsType<SessionId>(server.LastSessionId());
            var firstRun = Assert.IsType<RunId>(server.LastRunId());
            var firstEvents = server.AcquireStore().ReadFrom(firstSession, 1)
                .Where(evt => evt.RunId == firstRun).Select(server.AcquireCodecs().Decode).ToArray();
            var firstBoostedTurn = Assert.Single(firstEvents.OfType<TurnStarted>(), turn =>
                turn.ReasoningResolution?.Source == ReasoningSelectionSource.TurnBoost);
            var firstBoostedStep = Assert.Single(firstEvents.OfType<ModelStepStarted>(), step =>
                step.TurnId == firstBoostedTurn.TurnId);
            Assert.True(firstBoostedTurn.ReasoningResolution!.IsEquivalentTo(firstBoostedStep.ReasoningResolution));
            var sequenceBeforeSecond = server.AcquireStore().CurrentSequence(firstSession);
            // The endpoint holds the first response. The durable TurnStarted callback must
            // already have consumed the boost before this second call can claim it.
            secondOperation = Task.Run(async () => await host.ExecuteActAsync("second concurrent action", secondDiagnostics.Enqueue,
                operationStop.Token));
            _ = await secondOperation.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            endpoint.ReleaseHeldRequests();
            _ = await firstOperation.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var bodies = endpoint.Requests;
            Assert.NotEmpty(bodies);
            Assert.Equal(1, bodies.Count(request => request.ReasoningEffort == "high"));
            var secondSession = Assert.IsType<SessionId>(server.LastSessionId());
            var secondRun = Assert.IsType<RunId>(server.LastRunId());
            var secondStartSequence = secondSession == firstSession ? sequenceBeforeSecond + 1 : 1;
            var secondEvents = server.AcquireStore().ReadFrom(secondSession, secondStartSequence)
                .Where(evt => evt.RunId == secondRun).Select(server.AcquireCodecs().Decode).ToArray();
            Assert.DoesNotContain(secondEvents.OfType<TurnStarted>(), turn =>
                turn.ReasoningResolution?.Source == ReasoningSelectionSource.TurnBoost
                || turn.ReasoningResolution?.TurnBoostId is not null);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            operationStop.Cancel();
            endpoint.ReleaseHeldRequests();
            Exception? cleanupFailure = null;
            void RecordCleanupFailure(Exception exception) => cleanupFailure = cleanupFailure is null
                ? exception : new AggregateException(cleanupFailure, exception);
            try { await endpoint.DisposeAsync(); } catch (Exception exception) { RecordCleanupFailure(exception); }
            try { await DrainOperationAsync(firstOperation, operationStop.Token); } catch (Exception exception) { RecordCleanupFailure(exception); }
            try { await DrainOperationAsync(secondOperation, operationStop.Token); } catch (Exception exception) { RecordCleanupFailure(exception); }
            try { CloseRuntimeJournal(journal); } catch (Exception exception) { RecordCleanupFailure(exception); }
            try { CloseExactRuntimePools(root); } catch (Exception exception) { RecordCleanupFailure(exception); }
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", previousConfig);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBase);
            Environment.SetEnvironmentVariable("OMNI_MODEL", previousModel);
            if (cleanupFailure is null) Directory.Delete(root, recursive: true);
            else if (primaryFailure is not null) primaryFailure.Data["TUI fixture cleanup failure"] = cleanupFailure;
            else throw cleanupFailure;
        }
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-turn-boost-tui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteConfiguration(string dataDirectory, string endpoint, bool supported)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAIResponses
                baseUrl: {{endpoint}}
                auth: none
                billingMode: Local
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 8192
                recommendedUsableContext: 4096
                maxOutput: 2048
                reasoning:
                  supported: {{supported.ToString().ToLowerInvariant()}}
                  effortLevels: [high]
                  replayPolicy: PreserveAcrossSteps
            """);
    }

    private static void CloseExactRuntimePools(string root)
    {
        var data = Path.Combine(root, "data");
        var paths = OmniHost.CreatePlatformPaths(data);
        ClearPool(paths.UserDatabasePath);
    }

    private static void CloseRuntimeJournal(SqliteEventStore? store)
    {
        if (store is null) return;
        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        store.Connection.Dispose();
    }

    private static void ClearPool(string path)
    {
        if (!File.Exists(path)) return;
        // Match SqliteModelPolicyStore's exact DataSource-only connection string.
        using var connection = new SqliteConnection("DataSource=" + path);
        SqliteConnection.ClearPool(connection);
    }

    private static async Task DrainOperationAsync(Task<int>? operation, CancellationToken cancellationToken)
    {
        if (operation is null || operation.IsCompleted) return;
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed record CapturedRequest(int Number, string Body, string? ReasoningEffort);

    private sealed class ScriptedResponsesEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<string> _firstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly Task _serve;
        private readonly bool _holdFirstRequest;
        private int _nextRequest;
        private int _disposed;

        public ScriptedResponsesEndpoint(bool holdFirstRequest = false)
        {
            _holdFirstRequest = holdFirstRequest;
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }
        public IReadOnlyList<CapturedRequest> Requests => _requests.OrderBy(request => request.Number).ToArray();
        public int RequestCount => _requests.Count;
        public Task<string> FirstRequest => _firstRequest.Task;

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }
                var handler = HandleAsync(client, _stop.Token);
                _handlers.Add(handler);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                }

                var body = new char[contentLength];
                var read = 0;
                while (read < body.Length)
                {
                    var count = await reader.ReadAsync(body.AsMemory(read), cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    read += count;
                }

                var text = new string(body, 0, read);
                using var document = JsonDocument.Parse(text);
                var effort = document.RootElement.TryGetProperty("reasoning", out var reasoning)
                    && reasoning.TryGetProperty("effort", out var effortElement)
                    ? effortElement.GetString() : null;
                var number = Interlocked.Increment(ref _nextRequest);
                _requests.Enqueue(new CapturedRequest(number, text, effort));
                if (number == 1) _firstRequest.TrySetResult(text);
                if (_holdFirstRequest && number == 1)
                    await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                var bytes = Encoding.UTF8.GetBytes(MinimalStream("fixture completion"));
                var headers = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + bytes.Length) + "\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void ReleaseHeldRequests() => _release.TrySetResult(true);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            ReleaseHeldRequests();
            _stop.Cancel();
            _listener.Stop();
            try { await _serve.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            try { await Task.WhenAll(_handlers).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }

        private static string MinimalStream(string text) => $$$$"""
            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","output_index":0,"delta":{{{{JsonSerializer.Serialize(text)}}}}}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":{{{{JsonSerializer.Serialize(text)}}}}}]}}

            event: response.completed
            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":2,"output_tokens":1}}}

            """;
    }
}
