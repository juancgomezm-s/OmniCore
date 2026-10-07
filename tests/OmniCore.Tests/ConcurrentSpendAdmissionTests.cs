namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

public sealed class ConcurrentSpendAdmissionTests
{
    [Fact]
    public async Task Two_workspace_admissions_must_not_exceed_shared_daily_cap()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-concurrent-spend-" + Guid.NewGuid().ToString("N"));
        var paths = new[] { Path.Combine(root, "workspaces", "a"), Path.Combine(root, "workspaces", "b") };
        foreach (var path in paths) Directory.CreateDirectory(path);
        var stores = paths.Select(path => new SqliteEventStore(Path.Combine(path, "journal.db"))).ToArray();
        using var arrivals = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var codecs = EventCodecs.Create();
        var calls = 0;
        try
        {
            var jobs = paths.Select((path, index) => Task.Run(() =>
            {
                var store = stores[index];
                var session = SessionId.New();
                var run = TestRun.Open(new EventStream(store, codecs, session), session);
                var catalog = new FakeCatalog();
                var turn = new ExplorerTurn((_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return new ModelResponse([new TextBlock("offline fixture response")], StopReason.EndTurn,
                        new TokenUsage(300_000, 0, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null), TokenUsageFields.All);
                }, ScriptedToolExecutor.WithWorkspace(catalog,
                    new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), path), catalog,
                    new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                    new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                    new ModelSelection(new ModelIdValue("fixture"), 400_000, ToolMode.Direct, null,
                        maxOutputTokens: 1000), store, codecs,
                    new AdmissionBarrierArtifacts(new FileArtifactStore(path), arrivals, release),
                    new InMemoryAuditSink(), new RedactionPolicy(), pricing: new ModelPricing(1m, 1m),
                    enforceDefaultSpendCaps: true, sessionCapUsd: 5m, dailyCapUsd: 0.50m,
                    userSpendReader: new UserWorkspaceSpendReader(root, path), modelContextCapacity: 400_000,
                    maximumGenerationRequestAttempts: 1);
                var result = turn.Ask("fixture", "system", run.SessionId, run.RunId,
                    run.RootLane, "", CancellationToken.None);
                return store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>()
                    .Sum(step => step.CostUsd ?? 0m);
            })).ToArray();
            // Bounded release also supports a future admission implementation that blocks one
            // contender before it reaches this artifact boundary. No unbounded fixture deadlock.
            await Task.Run(() => arrivals.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            var costs = await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.True(costs.Sum() <= 0.50m,
                $"Shared cap 0.50 USD was exceeded: {costs.Sum()} USD, {calls} offline invocations.");
            Assert.Equal(1, calls);
            Assert.Equal(0.30m, costs.Sum());
        }
        finally
        {
            release.Set();
            foreach (var store in stores) store.Close();
            foreach (var path in paths)
            {
                using var connection = new SqliteConnection("DataSource=" + Path.Combine(path, "journal.db"));
                SqliteConnection.ClearPool(connection);
            }
            Directory.Delete(root, true);
        }
    }

    private sealed class AdmissionBarrierArtifacts(IArtifactStore inner, CountdownEvent arrivals,
        ManualResetEventSlim release) : IArtifactStore
    {
        private int _snapshots;
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            if (kind == ArtifactKind.ContextSnapshot && Interlocked.Increment(ref _snapshots) == 2)
            {
                arrivals.Signal();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture admission barrier expired.");
            }
            return inner.PutText(content, mediaType, kind, sensitivity);
        }
        public string? GetText(ContentHash hash) => inner.GetText(hash);
        public bool Verify(ContentHash hash, long expectedSize) => inner.Verify(hash, expectedSize);
    }
}
