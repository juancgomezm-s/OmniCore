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

/// <summary>
/// ADR-0005 ProviderState isolation. Within ONE Ask the opaque ProviderState of a tool-use
/// response must be forwarded intact as the Continuation of the next ModelRequest of that same
/// Ask, but it is strictly LOCAL to the Ask: a subsequent Ask on the SAME ExplorerTurn instance
/// (same session/run/lane) must start from Continuation=null even though the previous Ask's
/// final response carried a non-null state. Expected RED against the current runtime (which
/// always sends Continuation=null); must NOT be weakened to bless it. No production code is
/// touched by this file. The opaque state is never logged or rendered by this test.
/// </summary>
public sealed class ExplorerTurnProviderStateIsolationTests
{
    private static readonly ExecutionFingerprint Fingerprint = new("scripted", "h", "t", "c", "o", "M3");
    private static readonly ModelSelection Selection = new(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null);
    private const string FixtureFileName = "fixture.txt";
    private const string FixtureContent = "explorer-ps-isolation-fixture-payload";

    private sealed record ScriptedOutcome(
        List<ModelRequest> Requests,
        StopReason FirstStopReason,
        StopReason SecondStopReason,
        SqliteEventStore Store,
        string Root,
        string Journal);

    [Fact]
    public void Provider_state_is_forwarded_within_one_ask_but_not_reused_by_the_next_ask()
    {
        var toolCallState = new ProviderState("test", "{\"v\":\"tool-use\"}");
        var endTurnState = new ProviderState("test", "{\"v\":\"end-of-first-ask\"}");
        var toolCall = new ToolCallBlock(ToolCallId.New(), "call-psiso", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        var outcome = RunTwoAskScript(requests => requests.Count switch
        {
            1 => new ModelResponse(new ContentBlock[] { toolCall }, StopReason.ToolUse,
                new TokenUsage(2, 1, 0, 0, 0), toolCallState, new ProviderMetadata("scripted", "test", null)),
            2 => new ModelResponse(new ContentBlock[] { new TextBlock("first ask done") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), endTurnState, new ProviderMetadata("scripted", "test", null)),
            _ => new ModelResponse(new ContentBlock[] { new TextBlock("second ask done") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null)),
        });
        try
        {
            // Verify turn/tool setup before the continuity assertions so a failure here is
            // setup, not the regression.
            Assert.Equal(StopReason.EndTurn, outcome.FirstStopReason);
            Assert.Equal(StopReason.EndTurn, outcome.SecondStopReason);
            Assert.Equal(3, outcome.Requests.Count);
            var toolResult = Assert.Single(outcome.Requests[1].Messages.SelectMany(message => message.Content)
                .OfType<ToolResultBlock>(), block => block.Id == toolCall.Id);
            Assert.False(toolResult.IsError, "the real read-only catalog tool must have succeeded");
            Assert.Contains(toolResult.Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));

            // Same-Ask continuity (ADR-0005): the tool-use response's opaque ProviderState is
            // re-sent intact as the Continuation of the second call of the SAME Ask.
            Assert.NotNull(outcome.Requests[1].Continuation);
            Assert.Equal(toolCallState, outcome.Requests[1].Continuation);

            // Cross-Ask isolation: the first request of the NEXT Ask — same ExplorerTurn
            // instance, same session/run/lane — must NOT inherit any state, even though the
            // previous Ask's final response carried a non-null ProviderState.
            Assert.Null(outcome.Requests[2].Continuation);
        }
        finally
        {
            Cleanup(outcome.Store, outcome.Root, outcome.Journal);
        }
    }

    /// <summary>Two Asks on ONE ExplorerTurn instance via a shared scripted delegate (no real
    /// upstream): tool-use + EndTurn in the first Ask, then a single EndTurn call in the second.
    /// Captures every ModelRequest without logging any payload.</summary>
    private static ScriptedOutcome RunTwoAskScript(Func<List<ModelRequest>, ModelResponse> script)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-explorer-psiso-" + Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(root, "journal.db");
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            File.WriteAllText(Path.Combine(root, FixtureFileName), FixtureContent);
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var tools = OmniHost.CreateExplorerTools();
            var catalog = tools.Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                {
                    ["filesystem.read"] = PermissionDecision.Allow,
                }), root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var requests = new List<ModelRequest>();
            ModelResponse Ask(ModelRequest request, CancellationToken _)
            {
                requests.Add(request);
                return script(requests);
            }
            var turn = new OmniCore.Host.ExplorerTurn(Ask, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                Fingerprint, Selection, store, codecs, artifacts, new InMemoryAuditSink(),
                new RedactionPolicy());
            var first = turn.Ask("first ask with a tool call", "system", session, run.RunId, run.RootLane, "",
                TestContext.Current.CancellationToken);
            var second = turn.Ask("second ask on the same turn instance", "system", session, run.RunId,
                run.RootLane, "", TestContext.Current.CancellationToken);
            return new ScriptedOutcome(requests, first.StopReason, second.StopReason, store, root, journal);
        }
        catch
        {
            Cleanup(store, root, journal);
            throw;
        }
    }

    private static void Cleanup(SqliteEventStore? store, string root, string journal)
    {
        store?.Close();
        // Release only THIS journal's pooled connection (ClearPool, never ClearAllPools) so the
        // isolated GUID directory can be deleted; includes SQLite sidecars if any remain.
        using var connection = new SqliteConnection("DataSource=" + journal);
        SqliteConnection.ClearPool(connection);
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
