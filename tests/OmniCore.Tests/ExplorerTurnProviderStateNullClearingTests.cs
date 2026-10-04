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
/// ADR-0005 ProviderState null clearing WITHIN one Ask. The opaque Continuation is threaded
/// forward per response: a response carrying a non-null stateA is forwarded as the Continuation
/// of the next ModelRequest of the SAME Ask, but when a subsequent response carries a NULL
/// state, the NEXT request of that same Ask must go back to Continuation=null — the stale
/// stateA must NOT leak past the null response. Three scripted calls in ONE Ask: two real
/// filesystem.read ToolUse responses (first with stateA, second with null state) and a final
/// EndTurn. Same provider/model throughout; no real upstream; the opaque state is never logged
/// or rendered by this test. No production code is touched by this file.
/// </summary>
public sealed class ExplorerTurnProviderStateNullClearingTests
{
    private static readonly ExecutionFingerprint Fingerprint = new("scripted", "h", "t", "c", "o", "M3");
    private static readonly ModelSelection Selection = new(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null);
    private const string FixtureFileName = "fixture.txt";
    private const string FixtureContent = "explorer-ps-nullclearing-fixture-payload";

    private sealed record ScriptedOutcome(
        List<ModelRequest> Requests,
        StopReason StopReason,
        SqliteEventStore Store,
        string Root,
        string Journal);

    [Fact]
    public void null_provider_state_of_a_mid_ask_response_clears_the_continuation_of_the_next_request()
    {
        var stateA = new ProviderState("test", "{\"v\":\"tool-use-with-state\"}");
        var firstCall = new ToolCallBlock(ToolCallId.New(), "call-psnc-1", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        var secondCall = new ToolCallBlock(ToolCallId.New(), "call-psnc-2", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        Assert.NotEqual(firstCall.Id, secondCall.Id);
        var outcome = RunSingleAskScript(requests => requests.Count switch
        {
            // Response 1: real filesystem.read ToolUse with a NON-NULL opaque stateA.
            1 => new ModelResponse(new ContentBlock[] { firstCall }, StopReason.ToolUse,
                new TokenUsage(2, 1, 0, 0, 0), stateA, new ProviderMetadata("scripted", "test", null)),
            // Response 2: ANOTHER real filesystem.read ToolUse, but this time with a NULL state.
            2 => new ModelResponse(new ContentBlock[] { secondCall }, StopReason.ToolUse,
                new TokenUsage(2, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null)),
            // Response 3: EndTurn, no tool, no state.
            _ => new ModelResponse(new ContentBlock[] { new TextBlock("ask done after null state") },
                StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("scripted", "test", null)),
        });
        try
        {
            // Turn/tool setup before the continuity assertions so a failure here is setup, not
            // the regression.
            Assert.Equal(StopReason.EndTurn, outcome.StopReason);
            Assert.Equal(3, outcome.Requests.Count);
            // The final request contains both results once; earlier requests replay prefixes.
            var toolResults = outcome.Requests[2].Messages
                .SelectMany(message => message.Content)
                .OfType<ToolResultBlock>()
                .ToList();
            var firstResult = Assert.Single(toolResults, block => block.Id == firstCall.Id);
            var secondResult = Assert.Single(toolResults, block => block.Id == secondCall.Id);
            Assert.False(firstResult.IsError, "the first real read-only catalog tool must have succeeded");
            Assert.False(secondResult.IsError, "the second real read-only catalog tool must have succeeded");
            Assert.Contains(firstResult.Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));
            Assert.Contains(secondResult.Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));

            // Ask-local threading (ADR-0005): the first request of the Ask starts clean.
            Assert.Null(outcome.Requests[0].Continuation);
            // stateA of response 1 is forwarded intact as the Continuation of request 2.
            Assert.Equal(stateA, outcome.Requests[1].Continuation);
            // The NULL state of response 2 clears the threading: request 3 must go back to
            // Continuation=null, NOT re-send the stale stateA.
            Assert.Null(outcome.Requests[2].Continuation);
        }
        finally
        {
            Cleanup(outcome.Store, outcome.Root, outcome.Journal);
        }
    }

    /// <summary>ONE Ask on ONE ExplorerTurn instance via a scripted delegate (no real upstream):
    /// tool-use + stateA, tool-use + null state, EndTurn. Captures every ModelRequest in memory
    /// without logging any payload.</summary>
    private static ScriptedOutcome RunSingleAskScript(Func<List<ModelRequest>, ModelResponse> script)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-explorer-psnc-" + Guid.NewGuid().ToString("N"));
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
            var result = turn.Ask("one ask with two tool calls and a null state between them", "system",
                session, run.RunId, run.RootLane, "", TestContext.Current.CancellationToken);
            return new ScriptedOutcome(requests, result.StopReason, store, root, journal);
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
