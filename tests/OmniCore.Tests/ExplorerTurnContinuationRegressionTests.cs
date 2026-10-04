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
/// ADR-0005 same-Turn continuity regressions. These two Facts encode the DESIRED behavior of the
/// ExplorerTurn loop within a single Ask (two model calls): the opaque ProviderState and every
/// block of the first model response must survive into the second ModelRequest intact and in
/// original order. They are expected RED against the current runtime (which builds
/// Continuation=null and replays only the assistant ToolCallBlock) and must NOT be weakened to
/// bless it. No production code is touched by this file.
/// </summary>
public sealed class ExplorerTurnContinuationRegressionTests
{
    private static readonly ExecutionFingerprint Fingerprint = new("scripted", "h", "t", "c", "o", "M3");
    private static readonly ModelSelection Selection = new(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null);
    private const string FixtureFileName = "fixture.txt";
    private const string FixtureContent = "explorer-continuity-fixture-payload";

    private sealed record ScriptedOutcome(
        List<ModelRequest> Requests,
        StopReason StopReason,
        SqliteEventStore Store,
        string Root,
        string Journal);

    [Fact]
    public void Second_call_of_same_turn_receives_first_response_provider_state_as_intact_continuation()
    {
        var state = new ProviderState("test", "{\"v\":1}");
        var toolCall = new ToolCallBlock(ToolCallId.New(), "call-cont-state", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        var outcome = RunTwoCallTurn(_ => new ContentBlock[] { toolCall }, state);
        try
        {
            // Verify turn shape before continuity so a failure here is setup, not the regression.
            Assert.Equal(StopReason.EndTurn, outcome.StopReason);
            Assert.Equal(2, outcome.Requests.Count);
            var toolResult = Assert.Single(outcome.Requests[1].Messages.SelectMany(message => message.Content)
                .OfType<ToolResultBlock>(), block => block.Id == toolCall.Id);
            Assert.False(toolResult.IsError, "the real read-only catalog tool must have succeeded");
            Assert.Contains(toolResult.Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));

            // Desired ADR-0005 same-Turn continuity: the opaque ProviderState returned by the
            // first model call of this Ask is re-sent intact as the Continuation of the second
            // call, with no interpretation, no second Ask, and no cross-model/provider reuse.
            Assert.NotNull(outcome.Requests[1].Continuation);
            Assert.Equal(state, outcome.Requests[1].Continuation);
        }
        finally
        {
            Cleanup(outcome.Store, outcome.Root, outcome.Journal);
        }
    }

    [Fact]
    public void Second_call_of_same_turn_retains_first_response_blocks_in_order_before_matching_tool_result()
    {
        var preamble = new TextBlock("preamble");
        var toolCall = new ToolCallBlock(ToolCallId.New(), "call-cont-blocks", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        ArtifactRef? opaqueRef = null;
        ReasoningBlock? reasoning = null;
        var outcome = RunTwoCallTurn(artifacts =>
        {
            opaqueRef = artifacts.PutText("""{"chain":"opaque-continuity"}""", "application/json",
                ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive);
            reasoning = new ReasoningBlock("visible", ReasoningVisibility.Full, opaqueRef);
            return new ContentBlock[] { preamble, reasoning!, toolCall };
        }, null);
        try
        {
            // Verify turn shape before continuity so a failure here is setup, not the regression.
            Assert.Equal(StopReason.EndTurn, outcome.StopReason);
            Assert.Equal(2, outcome.Requests.Count);
            var flat = outcome.Requests[1].Messages.SelectMany(message => message.Content).ToArray();
            var toolResult = Assert.Single(flat.OfType<ToolResultBlock>(), block => block.Id == toolCall.Id);
            Assert.False(toolResult.IsError, "the real read-only catalog tool must have succeeded");
            Assert.Contains(toolResult.Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));

            // Desired ADR-0005 same-Turn continuity: the second request's assistant message keeps
            // the text, the reasoning (with its Sensitive opaque artifact ref) and the tool call
            // of the first response, in original order, before the matching ToolResultBlock.
            var assistantBlocks = outcome.Requests[1].Messages
                .Where(message => message.Role == MessageRole.Assistant)
                .SelectMany(message => message.Content).ToArray();
            var retainedText = Assert.Single(assistantBlocks.OfType<TextBlock>());
            Assert.Equal(preamble, retainedText);
            var retainedReasoning = Assert.Single(assistantBlocks.OfType<ReasoningBlock>());
            Assert.Equal("visible", retainedReasoning.VisibleText);
            Assert.Equal(ReasoningVisibility.Full, retainedReasoning.Visibility);
            Assert.Equal(opaqueRef, retainedReasoning.OpaquePayload);
            var retainedCall = Assert.Single(assistantBlocks.OfType<ToolCallBlock>(), block => block.Id == toolCall.Id);
            Assert.Equal(toolCall, retainedCall);
            Assert.True(Array.IndexOf(assistantBlocks, retainedText) < Array.IndexOf(assistantBlocks, retainedReasoning),
                "TextBlock must precede ReasoningBlock in the replayed assistant message");
            Assert.True(Array.IndexOf(assistantBlocks, retainedReasoning) < Array.IndexOf(assistantBlocks, retainedCall),
                "ReasoningBlock must precede ToolCallBlock in the replayed assistant message");
            Assert.True(Array.IndexOf(flat, retainedCall) < Array.IndexOf(flat, toolResult),
                "the replayed assistant blocks must precede the matching ToolResultBlock");
        }
        finally
        {
            Cleanup(outcome.Store, outcome.Root, outcome.Journal);
        }
    }

    /// <summary>One Ask, one shared scripted delegate, two model calls: a real filesystem.read
    /// tool call first, then EndTurn. Captures both ModelRequests without logging any payload.</summary>
    private static ScriptedOutcome RunTwoCallTurn(Func<FileArtifactStore, ContentBlock[]> firstResponseBlocks,
        ProviderState? firstResponseState)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-explorer-cont-" + Guid.NewGuid().ToString("N"));
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
                    ? new ModelResponse(firstResponseBlocks(artifacts), StopReason.ToolUse,
                        new TokenUsage(2, 1, 0, 0, 0), firstResponseState,
                        new ProviderMetadata("scripted", "test", null))
                    : new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
            }
            var turn = new OmniCore.Host.ExplorerTurn(Ask, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                Fingerprint, Selection, store, codecs, artifacts, new InMemoryAuditSink(),
                new RedactionPolicy());
            var result = turn.Ask("continue within this same turn", "system", session, run.RunId, run.RootLane, "",
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
