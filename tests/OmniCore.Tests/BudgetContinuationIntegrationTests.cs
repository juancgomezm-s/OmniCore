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

/// <summary>SQLite/CAS/Host integration with scripted usage, no real provider consumption.</summary>
public sealed class BudgetContinuationIntegrationTests
{
    [Theory]
    [InlineData("session", false)]
    [InlineData("session", true)]
    [InlineData("daily", false)]
    [InlineData("daily", true)]
    public void Explicit_consent_survives_reopen_and_applies_only_to_its_scope(string scope, bool otherSession)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-budget-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(db);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var loaded = new ConfigLoader().Load(null, null, scope == "session"
                ? "budget: {session: 0, daily: 100}" : "budget: {session: 100, daily: 0}");
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            ExplorerTurn Turn() => new((_, _) =>
            {
                calls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
                sessionCapUsd: loaded.SessionCapUsd, dailyCapUsd: loaded.DailyCapUsd);
            Assert.Equal(StopReason.Cancelled, Turn().Ask("first", "sys", session, run.RunId,
                run.RootLane, "", CancellationToken.None).StopReason);
            Assert.Equal(0, calls);
            var request = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<InteractionRequested>()
                .Single(r => r.Kind == InteractionKind.BudgetExceeded);
            Assert.Equal(scope, BudgetContinuation.Offer(request)!.Scope);
            var stateFile = Path.Combine(root, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToInteraction(request.InteractionId, "allow_plus").Status);
            store.Close();
            store = new SqliteEventStore(db);
            artifacts = new FileArtifactStore(root);
            if (otherSession)
            {
                session = SessionId.New();
                run = TestRun.Open(store, session);
            }
            var result = Turn().Ask("after restart", "sys", session, run.RunId, run.RootLane, "", CancellationToken.None);
            var allowed = !otherSession || scope == "daily";
            Assert.Equal(allowed ? StopReason.EndTurn : StopReason.Cancelled, result.StopReason);
            Assert.Equal(allowed ? 1 : 0, calls);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + db);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
