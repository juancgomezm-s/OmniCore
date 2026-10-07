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
/// Offline proposal for the ADR-0046 §3 replay-policy boundary. The completion delegate is a
/// scripted fixture, not a remote provider; the filesystem read is local and read-only. No
/// continuation is fabricated: the first scripted response returns the state observed by the
/// second request.
/// </summary>
public sealed class M5ReasoningReplayPolicyTests
{
    [Theory]
    [InlineData(ReasoningReplayPolicy.None, false)]
    [InlineData(ReasoningReplayPolicy.PreserveAcrossSteps, true)]
    public void Tool_followup_respects_declared_replay_policy(
        ReasoningReplayPolicy replayPolicy, bool expectedContinuation)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-reasoning-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string fixtureFile = "read-only-fixture.txt";
            const string fixtureContent = "reasoning-policy-fixture";
            File.WriteAllText(Path.Combine(root, fixtureFile), fixtureContent);

            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                {
                    ["filesystem.read"] = PermissionDecision.Allow,
                }), root);

            var capability = new ReasoningCapability(true, ["high"], replayPolicy);
            var route = ModelRoute.DefaultForModel("scripted-model", "fixture-provider",
                "https://fixture.invalid/v1", ProviderFamily.OpenAIResponses,
                reasoningCapability: capability);
            var selection = new ModelSelection(new ModelIdValue("scripted-model"), 8192, ToolMode.Direct,
                new ReasoningRequest("high", null), route.Id, route);
            var providerState = new ProviderState("fixture-opaque-state", "{\"opaque\":\"fixture-only\"}");
            var opaque = artifacts.PutText("verified reasoning fixture", "text/plain",
                ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive);
            var call = new ToolCallBlock(ToolCallId.New(), "fixture-call", "filesystem.read",
                "{\"path\":\"" + fixtureFile + "\"}");
            var requests = new List<ModelRequest>();

            ModelResponse Complete(ModelRequest request, CancellationToken _)
            {
                requests.Add(request);
                return requests.Count == 1
                    ? new ModelResponse(new ContentBlock[] {
                        new ReasoningBlock("visible reasoning", ReasoningVisibility.Full, opaque), call }, StopReason.ToolUse,
                        new TokenUsage(2, 1, 0, 0, 0), providerState,
                        new ProviderMetadata("scripted-fixture", "scripted-model", null))
                    : new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("scripted-fixture", "scripted-model", null));
            }

            var turn = new ExplorerTurn(Complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M5.5"), selection,
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy());

            var result = turn.Ask("read the fixture", "system", session, run.RunId, run.RootLane, "",
                TestContext.Current.CancellationToken);

            // Establish that the second ModelStep really happened through the read-only tool path;
            // policy assertions must not pass because setup skipped the tool continuation.
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(2, requests.Count);
            Assert.Equal(3, result.Usage.Input);
            Assert.Equal(2, result.Usage.Output);
            var completions = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>().ToArray();
            Assert.Equal(2, completions.Length);
            Assert.Equal(completions[0].TurnId, completions[1].TurnId);
            Assert.All(completions, completion => Assert.NotNull(completion.ResponseArtifact));
            var toolResult = Assert.Single(requests[1].Messages.SelectMany(message => message.Content)
                .OfType<ToolResultBlock>(), block => block.Id == call.Id);
            Assert.False(toolResult.IsError);
            Assert.Contains(toolResult.Content.OfType<TextBlock>(), text =>
                text.Text.Contains(fixtureContent, StringComparison.Ordinal));

            if (expectedContinuation)
            {
                Assert.Equal(providerState, requests[1].Continuation);
                Assert.Equal(opaque, Assert.Single(requests[1].Messages.SelectMany(message => message.Content)
                    .OfType<ReasoningBlock>()).OpaquePayload);
            }
            else
            {
                Assert.Null(requests[1].Continuation);
                Assert.Null(Assert.Single(requests[1].Messages.SelectMany(message => message.Content)
                    .OfType<ReasoningBlock>()).OpaquePayload);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
