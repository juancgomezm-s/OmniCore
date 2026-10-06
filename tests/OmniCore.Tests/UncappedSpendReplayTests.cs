namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class UncappedSpendReplayTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Historical_monetary_receipts_are_revalidated_only_when_a_monetary_cap_requires_them(bool capped, bool runCap)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-uncapped-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(new EventStream(store, codecs, session), session,
                taskBudget: runCap ? new TaskBudget(100m, null, null, null) : null);
            var artifacts = new CountedArtifacts(new FileArtifactStore(root));
            var catalog = new FakeCatalog();
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("fixture response") },
                    StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "fixture", null), TokenUsageFields.All);
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: capped,
                sessionCapUsd: 100m, dailyCapUsd: 100m);
            Assert.Equal(StopReason.EndTurn, turn.Ask("first", "system", run.SessionId, run.RunId,
                run.RootLane, "", CancellationToken.None).StopReason);
            var first = Assert.Single(store.ReadFrom(run.SessionId, 1).Select(codecs.Decode).OfType<ModelStepCompleted>());
            artifacts.TrackedHash = first.ResponseArtifact!.Hash;
            Assert.Equal(StopReason.EndTurn, turn.Ask("second", "system", run.SessionId, run.RunId,
                run.RootLane, "", CancellationToken.None).StopReason);
            Assert.Equal(2, calls);
            if (capped || runCap) Assert.True(artifacts.HistoricalVerifications > 0);
            else Assert.Equal(0, artifacts.HistoricalVerifications);
            var completions = store.ReadFrom(run.SessionId, 1).Select(codecs.Decode).OfType<ModelStepCompleted>().ToArray();
            Assert.Equal(2, completions.Length);
            Assert.All(completions, completion => Assert.Equal(0.000002m, completion.CostUsd));
            Assert.Equal(2, completions.Sum(completion => completion.Usage!.Input));
            Assert.Equal(2, completions.Sum(completion => completion.Usage!.Output));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CountedArtifacts(IArtifactStore inner) : IArtifactStore
    {
        public ContentHash? TrackedHash { get; set; }
        public int HistoricalVerifications { get; private set; }
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
            => inner.PutText(content, mediaType, kind, sensitivity);
        public string? GetText(ContentHash hash) => inner.GetText(hash);
        public bool Verify(ContentHash hash, long expectedSize)
        {
            if (hash == TrackedHash) HistoricalVerifications++;
            return inner.Verify(hash, expectedSize);
        }
    }
}
