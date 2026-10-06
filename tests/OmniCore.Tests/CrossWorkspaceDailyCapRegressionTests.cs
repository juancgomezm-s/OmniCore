using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CrossWorkspaceDailyCapRegressionTests
{
    private const string ModelId = "cross-workspace-budget-model";
    private const string ProviderId = "cross-workspace-budget-provider";

    [Theory]
    [InlineData("daily")]
    [InlineData("session")]
    public async Task User_daily_continuation_crosses_workspaces_but_session_grant_does_not(string scope)
    {
        var root = FixtureRoot();
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        var workspaceA = Path.Combine(root, "workspace-a");
        var workspaceB = Path.Combine(root, "workspace-b");
        foreach (var directory in new[] { data, config, workspaceA, workspaceB }) Directory.CreateDirectory(directory);
        var environment = ProcessEnvironment.Capture();
        var provider = new ScriptedHttpProvider(inputTokens: 100);
        var runtimeA = OmniCliRuntime.Create(workspaceA);
        var runtimeB = OmniCliRuntime.Create(workspaceB);
        try
        {
            SetEnvironment(data, config);
            WriteConfiguration(config, provider.BaseUrl, scope == "daily" ? 0m : 100m,
                scope == "session" ? 0m : 5m);
            var firstOutput = new List<string>();
            await new TuiTurnHost(runtimeA).ExecuteAsync("ask for explicit continuation", firstOutput.Add,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, provider.RequestCount);
            var request = Assert.Single(ReadCurrentEvents(workspaceA).OfType<InteractionRequested>(),
                item => item.Kind == InteractionKind.BudgetExceeded);
            Assert.Equal(scope, BudgetContinuation.Offer(request)!.Scope);
            var server = Assert.IsType<OmniServer>(runtimeA.Connect(CancellationToken.None));
            Assert.Equal("ok", server.RespondToInteraction(request.InteractionId, "allow_plus").Status);
            CloseRuntime(runtimeA);
            var secondOutput = new List<string>();
            await new TuiTurnHost(runtimeB).ExecuteAsync("respect the durable grant", secondOutput.Add,
                TestContext.Current.CancellationToken);
            Assert.Equal(scope == "daily" ? 1 : 0, provider.RequestCount);
            var events = ReadCurrentEvents(workspaceB);
            if (scope == "daily") Assert.Single(events.OfType<ModelStepCompleted>());
            else Assert.Contains(events, evt => evt is InteractionRequested interaction
                && interaction.Kind == InteractionKind.BudgetExceeded);
        }
        finally
        {
            await provider.DisposeAsync();
            CloseRuntime(runtimeA);
            CloseRuntime(runtimeB);
            ProcessEnvironment.Restore(environment);
            ClearFixturePools(data, workspaceA, workspaceB);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Spend_over_daily_cap_in_workspace_a_blocks_workspace_b_before_provider()
    {
        var root = FixtureRoot();
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        var workspaceA = Path.Combine(root, "workspace-a");
        var workspaceB = Path.Combine(root, "workspace-b");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);

        var environment = ProcessEnvironment.Capture();
        var provider = new ScriptedHttpProvider(inputTokens: 600_000);
        var runtimeA = OmniCliRuntime.Create(workspaceA);
        var runtimeB = OmniCliRuntime.Create(workspaceB);
        try
        {
            SetEnvironment(data, config);
            WriteConfiguration(config, provider.BaseUrl, dailyCapUsd: 0.50m);

            var firstOutput = new List<string>();
            var firstExitCode = await new TuiTurnHost(runtimeA).ExecuteAsync(
                "record historical workspace spend", firstOutput.Add, TestContext.Current.CancellationToken);

            Assert.True(provider.RequestCount == 1,
                $"Workspace A did not invoke the configured scripted model (exit {firstExitCode}): {string.Join("\n", firstOutput)}");
            var firstSteps = ReadCurrentEvents(workspaceA).OfType<ModelStepCompleted>().ToArray();
            var historical = Assert.Single(firstSteps);
            Assert.Equal(0.60m, historical.CostUsd);
            Assert.Equal(new TokenUsage(600_000, 0, 0, 0, 0), historical.Usage);

            var secondOutput = new List<string>();
            var secondExitCode = await new TuiTurnHost(runtimeB).ExecuteAsync(
                "must be blocked by user daily spend", secondOutput.Add, TestContext.Current.CancellationToken);

            Assert.True(provider.RequestCount == 1,
                $"Workspace B should be blocked before provider invocation (exit {secondExitCode}): {string.Join("\n", secondOutput)}");
            var secondEvents = ReadCurrentEvents(workspaceB);
            Assert.Contains(secondEvents, evt => evt is InteractionRequested requested
                && requested.Kind == InteractionKind.BudgetExceeded);
            Assert.DoesNotContain(secondEvents, evt => evt is ModelStepStarted);
            Assert.DoesNotContain(secondEvents, evt => evt is ModelStepCompleted);

            var evidenceReader = new UserWorkspaceSpendReader(data,
                OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspaceB));
            var snapshot = Assert.Single(evidenceReader.ReadOtherWorkspaces());
            var repeated = Assert.Single(evidenceReader.ReadOtherWorkspaces());
            Assert.Equal(snapshot.Events.Select(evt => evt.EventId), repeated.Events.Select(evt => evt.EventId));
            Assert.Equal(1, provider.RequestCount);
        }
        finally
        {
            await provider.DisposeAsync();
            CloseRuntime(runtimeA);
            CloseRuntime(runtimeB);
            ProcessEnvironment.Restore(environment);
            ClearFixturePools(data, workspaceA, workspaceB);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_other_workspace_allows_call_but_corrupt_journal_blocks_it(bool corruptJournal)
    {
        var root = FixtureRoot();
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        var workspaceA = Path.Combine(root, "workspace-a");
        var workspaceB = Path.Combine(root, "workspace-b");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);

        var environment = ProcessEnvironment.Capture();
        var provider = new ScriptedHttpProvider(inputTokens: 100);
        var runtimeB = OmniCliRuntime.Create(workspaceB);
        try
        {
            SetEnvironment(data, config);
            WriteConfiguration(config, provider.BaseUrl, dailyCapUsd: 0.50m);

            Assert.Empty(ReadCurrentEvents(workspaceA));
            if (corruptJournal)
            {
                var otherData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspaceA);
                Directory.CreateDirectory(otherData);
                File.WriteAllText(Path.Combine(otherData, "journal.db"), "fixture: invalid SQLite journal");
            }
            var output = new List<string>();
            var exitCode = await new TuiTurnHost(runtimeB).ExecuteAsync(
                "small call with unused daily budget", output.Add, TestContext.Current.CancellationToken);

            if (corruptJournal)
            {
                Assert.Equal(0, provider.RequestCount);
                var events = ReadCurrentEvents(workspaceB);
                Assert.Contains(events, evt => evt is InteractionRequested requested
                    && requested.Kind == InteractionKind.BudgetExceeded);
                Assert.DoesNotContain(events, evt => evt is ModelStepStarted or ModelStepCompleted);
                return;
            }
            Assert.True(provider.RequestCount == 1,
                $"A small request with unused budget should invoke the configured model (exit {exitCode}): {string.Join("\n", output)}");
            var completion = Assert.Single(ReadCurrentEvents(workspaceB).OfType<ModelStepCompleted>());
            Assert.Equal(0.0001m, completion.CostUsd);
            Assert.Equal(new TokenUsage(100, 0, 0, 0, 0), completion.Usage);
        }
        finally
        {
            await provider.DisposeAsync();
            CloseRuntime(runtimeB);
            ProcessEnvironment.Restore(environment);
            ClearFixturePools(data, workspaceA, workspaceB);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FixtureRoot() => Path.Combine(Path.GetTempPath(),
        "omnicore-cross-workspace-daily-cap-" + Guid.NewGuid().ToString("N"));

    private static void SetEnvironment(string data, string config)
    {
        Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
        Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
        Environment.SetEnvironmentVariable("OMNI_MODEL", ModelId);
        Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
    }

    private static void WriteConfiguration(string config, string baseUrl, decimal dailyCapUsd, decimal sessionCapUsd = 5m)
    {
        File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAiChatCompatible
                baseUrl: {{baseUrl}}
                auth: none
                billingMode: CreditBalance
                inputPricePerMillionUsd: 1
                outputPricePerMillionUsd: 1
            """);
        File.WriteAllText(Path.Combine(config, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 1000000
                maxOutput: 2048
            """);
        File.WriteAllText(Path.Combine(config, "settings.yaml"), $$"""
            budget:
              session: {{sessionCapUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
              daily: {{dailyCapUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
            """);
    }

    private static DomainEventPayload[] ReadCurrentEvents(string workspaceRoot)
    {
        var paths = OmniHost.CreatePlatformPaths();
        var workspaceData = OmniHost.WorkspaceDataDirectory(paths, workspaceRoot);
        var journalPath = Path.Combine(workspaceData, "journal.db");
        if (!File.Exists(journalPath)) return [];

        var sessions = new List<SessionId>();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = journalPath,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT session_id FROM events";
            using var reader = command.ExecuteReader();
            while (reader.Read()) sessions.Add(SessionId.Parse(reader.GetString(0)));
        }

        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journalPath);
        try
        {
            return sessions.SelectMany(session => store.ReadFrom(session, 1))
                .Select(codecs.Decode).ToArray();
        }
        finally { store.Close(); }
    }

    private static void ClearFixturePools(string data, params string[] workspaces)
    {
        var userDb = Path.GetFullPath(Path.Combine(data, "user.db"));
        using (var connection = new SqliteConnection("DataSource=" + userDb))
            SqliteConnection.ClearPool(connection);

        foreach (var workspace in workspaces)
        {
            var paths = new DefaultPlatformPaths(data);
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(paths, workspace),
                "journal.db");
            using var connection = new SqliteConnection("DataSource=" + Path.GetFullPath(journal));
            SqliteConnection.ClearPool(connection);
        }
    }

    private static void CloseRuntime(OmniCliRuntime runtime)
    {
        if (runtime.Connect(CancellationToken.None) is OmniServer server
            && server.AcquireStore() is SqliteEventStore store)
            store.Close();
    }

    private sealed record ProcessEnvironment(string? Data, string? Config, string? Model, string? BaseUrl)
    {
        public static ProcessEnvironment Capture() => new(
            Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable),
            Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable),
            Environment.GetEnvironmentVariable("OMNI_MODEL"),
            Environment.GetEnvironmentVariable("OMNI_BASE_URL"));

        public static void Restore(ProcessEnvironment prior)
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, prior.Data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, prior.Config);
            Environment.SetEnvironmentVariable("OMNI_MODEL", prior.Model);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", prior.BaseUrl);
        }
    }

    private sealed class ScriptedHttpProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private readonly long _inputTokens;
        private int _requestCount;

        public ScriptedHttpProvider(long inputTokens)
        {
            _inputTokens = inputTokens;
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }
                _ = HandleAsync(client, _stop.Token);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                var body = new char[contentLength];
                var read = 0;
                while (read < body.Length)
                {
                    var count = await reader.ReadAsync(body.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }

                Interlocked.Increment(ref _requestCount);
                var eventBody = "data: " + JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content = "fixture response" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = _inputTokens, completion_tokens = 0 },
                }) + "\n\ndata: [DONE]\n\n";
                var bytes = Encoding.UTF8.GetBytes(eventBody);
                var responseHeaders = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + bytes.Length) + "\r\n\r\n");
                await stream.WriteAsync(responseHeaders, cancellationToken);
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
