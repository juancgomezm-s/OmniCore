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

public sealed class ExplorerTurnOpaqueReasoningCasTests
{
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
            const string rawState = "opaque-provider-secret-marker";
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
            var reasonings = blocks.OfType<ReasoningBlock>().ToArray();
            Assert.Equal(8, reasonings.Length);
            Assert.Equal(new[] { "valid", "missing", "corrupt", "kind", "sensitivity", "algorithm", "size", "redacted" },
                reasonings.Select(item => item.VisibleText!.Split(' ')[0]));
            Assert.Equal(valid, reasonings[0].OpaquePayload);
            Assert.DoesNotContain("sk-super-secret-value", reasonings[0].VisibleText);
            foreach (var invalid in reasonings.Skip(1))
            {
                Assert.Null(invalid.OpaquePayload);
                Assert.DoesNotContain("sk-super-secret-value", invalid.VisibleText);
            }
            Assert.Equal("[estado opaco del provider omitido]", Assert.IsType<TextBlock>(blocks[9]).Text);
            var retainedCall = Assert.IsType<ToolCallBlock>(blocks[10]);
            var trailing = Assert.IsType<TextBlock>(blocks[11]);
            Assert.StartsWith("after", trailing.Text, StringComparison.Ordinal);
            foreach (var visible in blocks.OfType<TextBlock>())
                Assert.DoesNotContain("sk-super-secret-value", visible.Text);
            Assert.DoesNotContain(blocks, block => block is ProviderOpaqueBlock);
            Assert.Equal("fake.read", retainedCall.ToolName);

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
}
