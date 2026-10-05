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
            Assert.Equal(StopReason.EndTurn, outcome.StopReason);
            Assert.Equal(2, outcome.Requests.Count);
            var toolResult = Assert.Single(outcome.Requests[1].Messages.SelectMany(message => message.Content)
                .OfType<ToolResultBlock>(), block => block.Id == toolCall.Id);
            Assert.False(toolResult.IsError, "the real read-only catalog tool must have succeeded");
            Assert.Contains(toolResult.Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));
            Assert.NotNull(outcome.Requests[1].Continuation);
            Assert.Equal(state, outcome.Requests[1].Continuation);
        }
        finally { Cleanup(outcome.Store, outcome.Root, outcome.Journal); }
    }

    [Fact]
    public void Second_call_of_same_turn_retains_first_response_blocks_in_order_before_matching_tool_result()
    {
        var preamble = new TextBlock("preamble");
        var toolCall = new ToolCallBlock(ToolCallId.New(), "call-cont-blocks", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        ArtifactRef? opaqueRef = null;
        var outcome = RunTwoCallTurn(artifacts =>
        {
            opaqueRef = artifacts.PutText("{\"chain\":\"opaque-continuity\"}", "application/json",
                ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive);
            var reasoning = new ReasoningBlock("visible", ReasoningVisibility.Full, opaqueRef);
            return new ContentBlock[] { preamble, reasoning, toolCall };
        }, null);
        try
        {
            Assert.Equal(StopReason.EndTurn, outcome.StopReason);
            Assert.Equal(2, outcome.Requests.Count);
            var flat = outcome.Requests[1].Messages.SelectMany(message => message.Content).ToArray();
            var toolResult = Assert.Single(flat.OfType<ToolResultBlock>(), block => block.Id == toolCall.Id);
            Assert.False(toolResult.IsError, "the real read-only catalog tool must have succeeded");
            Assert.Contains(toolResult.Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));
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
            Assert.True(Array.IndexOf(assistantBlocks, retainedText) < Array.IndexOf(assistantBlocks, retainedReasoning));
            Assert.True(Array.IndexOf(assistantBlocks, retainedReasoning) < Array.IndexOf(assistantBlocks, retainedCall));
            Assert.True(Array.IndexOf(flat, retainedCall) < Array.IndexOf(flat, toolResult));
        }
        finally { Cleanup(outcome.Store, outcome.Root, outcome.Journal); }
    }

    [Fact]
    public void Assistant_tool_continuation_preserves_only_verified_opaque_reasoning_refs()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-continuation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "artifact-store"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var valid = artifacts.PutText("verified provider bytes", "application/json",
                ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive);
            var missing = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('a', 64)), 8,
                "application/json", ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive);
            var corrupt = artifacts.PutText("original provider bytes", "application/json",
                ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive);
            var corruptPath = Path.Combine(Path.Combine(root, "artifact-store"), "blobs", "sha256",
                corrupt.Hash.Value[..2], corrupt.Hash.Value[2..4], corrupt.Hash.Value);
            File.WriteAllText(corruptPath, "tampered but same ref");
            var wrongKind = valid with { Kind = ArtifactKind.ModelResponse };
            var wrongSensitivity = valid with { Sensitivity = Sensitivity.Normal };
            var wrongAlgorithm = valid with { Hash = new ContentHash("sha512", valid.Hash.Value) };
            var wrongSize = valid with { Size = valid.Size + 1 };
            var markedRedacted = valid with { Redacted = true };

            var catalog = FakeCatalog.Default();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            ModelMessage? continuedAssistant = null;
            ProviderState? continuedState = null;
            var rawState = "opaque-provider-secret-marker";
            var turn = new ExplorerTurn((request, _) =>
            {
                calls++;
                if (calls == 2)
                {
                    continuedAssistant = request.Messages.Last(message => message.Role == MessageRole.Assistant
                        && message.Content.OfType<ToolCallBlock>().Any());
                    continuedState = request.Continuation;
                    return Final();
                }

                return new ModelResponse(new ContentBlock[]
                {
                    new TextBlock("before sk-super-secret-value"),
                    new ReasoningBlock("valid sk-super-secret-value", ReasoningVisibility.Full, valid),
                    new ReasoningBlock("missing sk-super-secret-value", ReasoningVisibility.Full, missing),
                    new ReasoningBlock("corrupt sk-super-secret-value", ReasoningVisibility.Full, corrupt),
                    new ReasoningBlock("kind sk-super-secret-value", ReasoningVisibility.Full, wrongKind),
                    new ReasoningBlock("sensitivity sk-super-secret-value", ReasoningVisibility.Full, wrongSensitivity),
                    new ReasoningBlock("algorithm sk-super-secret-value", ReasoningVisibility.Full, wrongAlgorithm),
                    new ReasoningBlock("size sk-super-secret-value", ReasoningVisibility.Full, wrongSize),
                    new ReasoningBlock("redacted sk-super-secret-value", ReasoningVisibility.Full, markedRedacted),
                    new ProviderOpaqueBlock(new ProviderOpaque(ProviderFamily.OpenAIResponses, "test", "state",
                        valid, ReplayPolicy.SameModel)),
                    new ToolCallBlock(ToolCallId.New(), "provider-call", "fake.read", "{}"),
                    new TextBlock("after sk-super-secret-value"),
                }, StopReason.ToolUse, new TokenUsage(1, 0, 0, 0, 0), new ProviderState("opaque", rawState),
                    new ProviderMetadata("scripted", "test", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("test", "h", "t", "c", "o", "M5.5"),
                new ModelSelection(new ModelIdValue("test"), 8192, ToolMode.Direct, null), store, codecs, artifacts,
                new InMemoryAuditSink(), new RedactionPolicy(), pricing: new ModelPricing(1m, 1m));

            var result = turn.Ask("request", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(2, calls);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(rawState, continuedState?.PayloadJson);
            Assert.NotNull(continuedAssistant);
            var blocks = continuedAssistant!.Content;
            Assert.Equal(12, blocks.Count);
            Assert.IsType<TextBlock>(blocks[0]);
            Assert.StartsWith("before", Assert.IsType<TextBlock>(blocks[0]).Text, StringComparison.Ordinal);
            Assert.IsType<ReasoningBlock>(blocks[1]);
            var preserved = Assert.IsType<ReasoningBlock>(blocks[1]);
            Assert.Equal(valid, preserved.OpaquePayload);
            Assert.DoesNotContain("sk-super-secret-value", preserved.VisibleText);
            var reasonings = blocks.OfType<ReasoningBlock>().ToArray();
            Assert.Equal(new[] { "valid", "missing", "corrupt", "kind", "sensitivity", "algorithm", "size", "redacted" },
                reasonings.Select(item => item.VisibleText!.Split(' ')[0]));
            foreach (var invalid in reasonings.Skip(1))
            {
                Assert.Null(invalid.OpaquePayload);
                Assert.DoesNotContain("sk-super-secret-value", invalid.VisibleText);
            }
            Assert.Equal("[estado opaco del provider omitido]", Assert.IsType<TextBlock>(blocks[9]).Text);
            Assert.IsType<ToolCallBlock>(blocks[10]);
            Assert.IsType<TextBlock>(blocks[11]);
            Assert.StartsWith("after", Assert.IsType<TextBlock>(blocks[11]).Text, StringComparison.Ordinal);
            foreach (var visible in blocks.OfType<TextBlock>())
                Assert.DoesNotContain("sk-super-secret-value", visible.Text);
            Assert.DoesNotContain(blocks, block => block is ProviderOpaqueBlock);

            var journalJson = string.Join("\n", store.ReadFrom(session, 1).Select(evt => evt.PayloadJson));
            Assert.DoesNotContain(rawState, journalJson, StringComparison.Ordinal);
            foreach (var evt in store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>())
            {
                var response = artifacts.GetText(evt.ResponseArtifact!.Hash) ?? "";
                Assert.DoesNotContain(rawState, response, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ModelResponse Final() => new(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
        new TokenUsage(1, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));

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
            ModelResponse Complete(ModelRequest request, CancellationToken _)
            {
                requests.Add(request);
                return requests.Count == 1
                    ? new ModelResponse(firstResponseBlocks(artifacts), StopReason.ToolUse,
                        new TokenUsage(2, 1, 0, 0, 0), firstResponseState,
                        new ProviderMetadata("scripted", "test", null))
                    : new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
            }
            var turn = new ExplorerTurn(Complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                Fingerprint, Selection, store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy());
            var result = turn.Ask("continue within this same turn", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
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
        using var connection = new SqliteConnection("DataSource=" + journal);
        SqliteConnection.ClearPool(connection);
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
