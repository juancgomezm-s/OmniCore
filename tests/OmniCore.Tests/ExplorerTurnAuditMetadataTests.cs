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

namespace OmniCore.Tests;

public sealed class ExplorerTurnAuditMetadataTests
{
    [Theory]
    [InlineData("")]
    [InlineData("ordinary-output-fixture-marker")]
    [InlineData("ordinary-output-fixture-marker" + " and a deliberately long tail of filler text that pushes this combined constant literal comfortably past the two hundred character threshold so the metadata scan also covers a large ordinary model output string in full.")]
    public void Spend_audit_records_metadata_without_ordinary_model_output_content(string output)
    {
        const string marker = "ordinary-output-fixture-marker";
        var root = Path.Combine(Path.GetTempPath(), "omni-audit-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var audit = new InMemoryAuditSink();
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            ModelResponse Complete(ModelRequest request, CancellationToken token) => new(
                new ContentBlock[] { new TextBlock(output) }, StopReason.EndTurn,
                new TokenUsage(2, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            var turn = new ExplorerTurn(Complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(Path.Combine(root, "blobs")), audit,
                new RedactionPolicy());
            var result = turn.Ask("respond", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(output, result.FinalText);
            var spend = Assert.Single(audit.Records(), record => record.EventName == "turn.spend");
            Assert.Equal(run.RunId, spend.Run);
            Assert.Equal(session, spend.Session);
            Assert.Equal("EndTurn", spend.Details["stop"]);
            Assert.Equal("2", spend.Details["inputTokens"]);
            Assert.Equal("1", spend.Details["outputTokens"]);
            Assert.All(audit.Records(), record => Assert.All(record.Details, pair =>
            {
                Assert.DoesNotContain(marker, pair.Key, StringComparison.Ordinal);
                Assert.DoesNotContain(marker, pair.Value, StringComparison.Ordinal);
            }));
            Assert.False(spend.Details.ContainsKey("final"));
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
