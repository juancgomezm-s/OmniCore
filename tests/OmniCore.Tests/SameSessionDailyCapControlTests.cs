using System.Globalization;
using System.Text.Json;
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
public sealed class SameSessionDailyCapControlTests
{
    [Fact]
    public void Daily_cap_blocks_new_run_in_same_session_after_prior_run_exceeds_same_day_cap()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-same-session-spend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, bool enforceCaps) => new(
                complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: enforceCaps,
                sessionCapUsd: 100m, dailyCapUsd: 20m,
                maximumGenerationRequestAttempts: 1); // these fixtures never retry the callback
            static ModelResponse Response(long input) => new(new ContentBlock[] { new TextBlock("done") },
                StopReason.EndTurn, new TokenUsage(input, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            var sessionA = SessionId.New();
            var runA = TestRun.Open(store, sessionA);
            var callsA = 0;
            var fixtureDay = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var first = MakeTurn((_, _) => { callsA++; return Response(21_000_000); }, false)
                .Ask("first", "system", sessionA, runA.RunId, runA.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, first.StopReason);
            Assert.Equal(1, callsA);
            var completedA = Assert.Single(store.ReadFrom(sessionA, 1).Select(codecs.Decode).OfType<ModelCompleted>());
            Assert.NotNull(completedA.ResponseArtifact);
            var payload = artifacts.GetText(completedA.ResponseArtifact.Hash);
            Assert.NotNull(payload);
            using var record = JsonDocument.Parse(payload);
            Assert.Equal(21m, decimal.Parse(record.RootElement.GetProperty("costUsd").GetString()!, CultureInfo.InvariantCulture));
            var day = record.RootElement.GetProperty("day").GetString();
            Assert.True(day == fixtureDay,
                $"Artifact day '{day}' differs from captured fixture day '{fixtureDay}'; fixture boundary issue, not a product cap result");
            Assert.Equal(runA.RunId.ToString(), record.RootElement.GetProperty("runId").GetString());
            var sessionB = sessionA;
            var runB = TestRun.Open(store, sessionB);
            Assert.Equal(sessionA, sessionB);
            Assert.NotEqual(runA.RunId, runB.RunId);
            Assert.Single(store.ReadFrom(sessionB, 1).Select(codecs.Decode).OfType<ModelCompleted>());
            var callsB = 0;
            var second = MakeTurn((_, _) => { callsB++; return Response(1); }, true)
                .Ask("second", "system", sessionB, runB.RunId, runB.RootLane, "", CancellationToken.None);
            Assert.True(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == fixtureDay,
                "Fixture crossed UTC date boundary; not a product cap result");
            Assert.Equal(0, callsB);
            Assert.Equal(StopReason.Cancelled, second.StopReason);
            Assert.Contains(store.ReadFrom(sessionB, 1).Select(codecs.Decode).OfType<InteractionRequested>(),
                interaction => interaction.Kind == InteractionKind.BudgetExceeded);
            // The second ModelCompleted is Explorer's zero-usage closure event written after
            // BudgetExceeded (Cancelled + break), not a second provider request.
            var completions = store.ReadFrom(sessionB, 1).Select(codecs.Decode).OfType<ModelCompleted>().ToArray();
            Assert.Equal(2, completions.Length);
            var closureArtifact = completions[1].ResponseArtifact;
            Assert.NotNull(closureArtifact);
            var closurePayload = artifacts.GetText(closureArtifact.Hash);
            Assert.NotNull(closurePayload);
            using var closure = JsonDocument.Parse(closurePayload);
            Assert.Equal(runB.RunId.ToString(), closure.RootElement.GetProperty("runId").GetString());
            Assert.Equal(fixtureDay, closure.RootElement.GetProperty("day").GetString());
            Assert.Equal(0m, decimal.Parse(closure.RootElement.GetProperty("costUsd").GetString()!, CultureInfo.InvariantCulture));
            Assert.Equal(0L, closure.RootElement.GetProperty("input").GetInt64());
            Assert.Equal(0L, closure.RootElement.GetProperty("output").GetInt64());
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
