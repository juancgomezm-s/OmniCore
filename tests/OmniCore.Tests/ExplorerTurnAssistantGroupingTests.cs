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
/// M55 bug4 same-Turn assistant grouping. This Fact encodes the DESIRED shape of the second
/// ModelRequest of a two-call Ask: exactly ONE assistant message carrying the WHOLE first
/// ToolUse response (preamble text + both tool calls, original order, same ids/arguments),
/// followed by the matching tool results in call order. It is expected RED against the current
/// runtime, which appends one assistant message per tool call and drops the response text, and
/// must NOT be weakened to bless it. Opaque ProviderState continuity is a separate open item
/// (see ExplorerTurnContinuationRegressionTests) and is deliberately not asserted here.
/// No production code is touched by this file.
/// </summary>
public sealed class ExplorerTurnAssistantGroupingTests
{
    private static readonly ExecutionFingerprint Fingerprint = new("scripted", "h", "t", "c", "o", "M3");
    private static readonly ModelSelection Selection = new(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null);
    private const string FixtureFileName = "fixture.txt";
    private const string FixtureContent = "explorer-assistant-grouping-fixture-payload";

    private sealed record ScriptedOutcome(
        List<ModelRequest> Requests,
        StopReason StopReason,
        SqliteEventStore Store,
        string Root,
        string Journal);

    [Fact]
    public void Second_call_gets_one_grouped_assistant_message_before_matching_tool_results_in_order()
    {
        var callA = new ToolCallBlock(ToolCallId.New(), "call-group-a", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        var callB = new ToolCallBlock(ToolCallId.New(), "call-group-b", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        var outcome = RunTwoCallTurn(new ContentBlock[] { new TextBlock("preamble"), callA, callB });
        try
        {
            // Turn shape first: a failure here is a broken fixture, not the grouping regression.
            Assert.Equal(StopReason.EndTurn, outcome.StopReason);
            Assert.Equal(2, outcome.Requests.Count);
            var flat = outcome.Requests[1].Messages.SelectMany(message => message.Content).ToArray();
            var results = flat.OfType<ToolResultBlock>().ToArray();
            Assert.Equal(2, results.Length);
            Assert.False(results[0].IsError, "the real read-only catalog tool must have succeeded");
            Assert.False(results[1].IsError, "the real read-only catalog tool must have succeeded");
            Assert.Equal(callA.Id, results[0].Id);
            Assert.Equal(callB.Id, results[1].Id);
            Assert.Contains(results[0].Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));
            Assert.Contains(results[1].Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));

            // M55 bug4 grouping: exactly ONE assistant message for the whole ToolUse response,
            // blocks in original order text → callA → callB with the same ids/arguments, and it
            // precedes the matching tool results, which keep the call order.
            var assistant = Assert.Single(outcome.Requests[1].Messages,
                message => message.Role == MessageRole.Assistant);
            var blocks = assistant.Content.ToArray();
            Assert.Equal(3, blocks.Length);
            Assert.Equal("preamble", Assert.IsType<TextBlock>(blocks[0]).Text);
            Assert.Equal(callA, Assert.IsType<ToolCallBlock>(blocks[1]));
            Assert.Equal(callB, Assert.IsType<ToolCallBlock>(blocks[2]));
            Assert.True(Array.IndexOf(flat, blocks[0]) < Array.IndexOf(flat, results[0]),
                "the grouped assistant message must precede the matching tool results");
            Assert.True(Array.IndexOf(flat, results[0]) < Array.IndexOf(flat, results[1]),
                "tool results must keep the call order");
        }
        finally
        {
            Cleanup(outcome.Store, outcome.Root, outcome.Journal);
        }
    }

    /// <summary>One Ask, one shared scripted delegate, two model calls: a ToolUse response with
    /// a text preamble and two real filesystem.read calls first, then EndTurn. Captures both
    /// ModelRequests without logging any payload.</summary>
    private static ScriptedOutcome RunTwoCallTurn(ContentBlock[] firstResponseBlocks)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-explorer-group-" + Guid.NewGuid().ToString("N"));
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
                return requests.Count == 1
                    ? new ModelResponse(firstResponseBlocks, StopReason.ToolUse,
                        new TokenUsage(2, 1, 0, 0, 0), null,
                        new ProviderMetadata("scripted", "test", null))
                    : new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
            }
            var turn = new OmniCore.Host.ExplorerTurn(Ask, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                Fingerprint, Selection, store, codecs, artifacts, new InMemoryAuditSink(),
                new RedactionPolicy());
            var result = turn.Ask("group the whole tool use response", "system", session, run.RunId, run.RootLane, "",
                TestContext.Current.CancellationToken);
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
