namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Tools;

public sealed class TurnFingerprintContentIntegrationTests
{
    [Fact]
    public void Turn_records_exact_explainable_components_in_CAS_and_reopens_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-turn-fingerprint-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            var database = Path.Combine(root, "journal.db");
            var session = SessionId.New();
            var codecs = EventCodecs.Create();
            var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.inspect"));
            var artifacts = new FileArtifactStore(Path.Combine(root, "artifacts"));
            ExecutionFingerprint recorded;
            store = new SqliteEventStore(database);
            {
                var run = TestRun.Open(new EventStream(store, codecs, session), session, "CAS fingerprint fixture");
                var turn = new ExplorerTurn((_, _) => new ModelResponse([new TextBlock("done")],
                        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("scripted-fixture", "fixture-model", null)),
                    new NoTools(), catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                    new ExecutionFingerprint("fixture-model", "harness", "tools", "context", "none", "build"),
                    new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null),
                    store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                    recordEffectiveFingerprint: true);
                var result = turn.Ask("hello", "exact system instruction", session, run.RunId, run.RootLane, "",
                    CancellationToken.None);
                Assert.Equal(StopReason.EndTurn, result.StopReason);
                recorded = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>()).Fingerprint!;
                Assert.NotNull(recorded);
                Assert.Equal(4, recorded.Components.Count);
                foreach (var component in recorded.Components)
                {
                    Assert.NotNull(component.Content);
                    Assert.Equal(component.Hash, component.Content.Hash);
                    Assert.False(component.Content.Redacted);
                    Assert.True(artifacts.Verify(component.Hash, component.Content.Size));
                }
            }
            store.Close();
            store = new SqliteEventStore(database);
            {
                var envelope = Assert.Single(store.ReadFrom(session, 1), item => codecs.Decode(item) is TurnStarted);
                var fingerprint = ((TurnStarted)codecs.Decode(envelope)).Fingerprint!;
                Assert.Equal(recorded.Hash(), fingerprint.Hash());
                Assert.Equal(recorded.Components, fingerprint.Components);
                foreach (var component in fingerprint.Components)
                    Assert.Contains(envelope.ArtifactRefs, reference => reference.Hash == component.Hash);
                using var tools = JsonDocument.Parse(artifacts.GetText(Assert.Single(fingerprint.Components,
                    c => c.Name == "tools.plan").Hash)!);
                Assert.Equal(Assert.Single(catalog.Definitions()).InputSchemaJson,
                    tools.RootElement.GetProperty("tools")[0].GetProperty("inputSchemaJson").GetString());
                using var prompt = JsonDocument.Parse(artifacts.GetText(Assert.Single(fingerprint.Components,
                    c => c.Name == "prompt.template").Hash)!);
                Assert.Contains("exact system instruction", prompt.RootElement.GetProperty("renderedText").GetString()!);
            }
        }
        finally
        {
            store?.Close();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + Path.Combine(root, "journal.db"));
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Directory.Delete(root, true);
        }
    }

    private sealed class NoTools : IToolExecutor
    {
        public ToolOutcome ExecuteTool(ValidatedToolCall call, bool approve, CancellationToken token, EventStream stream) =>
            throw new InvalidOperationException("EndTurn fixture must not execute tools.");
    }
}
