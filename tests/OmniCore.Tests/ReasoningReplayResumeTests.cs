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
/// Proposed regression based on ExplorerTurnDurableProviderStateTests: reopen the real
/// SQLite journal/CAS between a user.ask suspension and its response, then check only the
/// declared reasoning replay policy. The scripted provider is offline and has no credentials.
/// A changed effective declaration is separately rejected by ExplorerTurn's real resume
/// fingerprint guard before a second model step is invoked.
/// </summary>
public sealed class ReasoningReplayResumeTests
{
    private static readonly QuestionnaireSchema Schema = new("Choose", null,
        new QuestionField[] { new("approach", "Which?", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null) });

    [Theory]
    [InlineData("none", false)]
    [InlineData("preserve", true)]
    [InlineData("changed-to-none-after-reopen", false)]
    public void Reopened_questionnaire_resume_obeys_explicit_reasoning_replay_policy(
        string scenario, bool shouldReplay)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-reasoning-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root, new SecretRedactor());
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);

            var policyBeforeReopen = scenario == "none"
                ? ReasoningReplayPolicy.None
                : ReasoningReplayPolicy.PreserveAcrossSteps;
            var physicalRoute = Route(policyBeforeReopen);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, physicalRoute.Id, physicalRoute);
            var continuationState = new ProviderState("fixture.kind", "{\"opaque\":\"offline-state\"}");
            var opaque = artifacts.PutText("verified resume reasoning", "text/plain",
                ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive);
            var totalCalls = 0;

            ExecutionFingerprint Fingerprint()
            {
                var model = new ModelDefinition("scripted", "fixture-provider", 8192, 7000, 2048, 1000,
                    reasoningCapability: selection.Route!.ReasoningCapability);
                var profile = new ModelProfileResolver().Resolve(model, null, route: selection.Route);
                var harness = new HarnessPolicyResolver().Resolve(profile);
                return RuntimeFingerprintFactory.Create(model, profile, harness, selection,
                    "harness", "context", "policy", "counter");
            }

            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(
                complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                Fingerprint(), selection,
                store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: service, recordEffectiveFingerprint: true);

            var first = MakeTurn((request, _) =>
            {
                totalCalls++;
                Assert.Null(request.Continuation);
                return new ModelResponse(new ContentBlock[]
                {
                    new ReasoningBlock("visible reasoning", ReasoningVisibility.Full, opaque),
                    new ToolCallBlock(ToolCallId.New(), "provider-question", "user.ask",
                        QuestionnaireCodec.EncodeSchema(Schema))
                }, StopReason.ToolUse, new TokenUsage(10, 2, 0, 0, 0), continuationState,
                    new ProviderMetadata("scripted", "", null));
            }).Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.InputRequired, first.StopReason);
            Assert.NotNull(first.PendingInteractionId);
            var firstStep = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>().Single();
            Assert.Equal(0, firstStep.StepIndex);

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root);
            service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(first.PendingInteractionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            // A policy change is not a physical-route change: route identity intentionally
            // excludes declared capabilities. Keep provider, endpoint, protocol, profile,
            // provider model name, ModelId, RouteId, Turn, and CAS root identical.
            if (scenario == "changed-to-none-after-reopen")
            {
                var samePhysicalRoute = Route(ReasoningReplayPolicy.None);
                Assert.Equal(physicalRoute.Id, samePhysicalRoute.Id);
                Assert.Equal(physicalRoute.CanonicalJson(), samePhysicalRoute.CanonicalJson());
                selection = new ModelSelection(selection.Model, 8192, ToolMode.Direct, null,
                    samePhysicalRoute.Id, samePhysicalRoute);
            }

            var changedPolicy = scenario == "changed-to-none-after-reopen";
            var secondCalls = 0;
            var second = MakeTurn((request, _) =>
            {
                secondCalls++;
                totalCalls++;
                if (shouldReplay) Assert.Equal(continuationState, request.Continuation);
                else Assert.Null(request.Continuation);
                // Durable history currently restores tool messages and ProviderState, not
                // ReasoningBlock payloads. Do not claim that missing round-trip is implemented.
                if (!shouldReplay)
                    Assert.All(request.Messages.SelectMany(message => message.Content).OfType<ReasoningBlock>(),
                        reasoning => Assert.Null(reasoning.OpaquePayload));
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(4, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }).Ask("continue", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(changedPolicy ? 0 : 1, secondCalls);
            Assert.Equal(changedPolicy ? 1 : 2, totalCalls);
            Assert.Equal(changedPolicy ? StopReason.Error : StopReason.EndTurn, second.StopReason);
            if (changedPolicy)
                Assert.Contains("effective fingerprint differs", second.FinalText, StringComparison.Ordinal);
            var decoded = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var turnStarts = decoded.OfType<TurnStarted>().ToArray();
            Assert.Single(turnStarts);
            var turnId = turnStarts[0].TurnId;
            var completions = decoded.OfType<ModelStepCompleted>().ToArray();
            Assert.Equal(changedPolicy ? 1 : 2, completions.Length);
            Assert.All(completions, completion => Assert.Equal(turnId, completion.TurnId));
            Assert.Equal(changedPolicy ? new[] { 0 } : new[] { 0, 1 }, completions.OrderBy(completion => completion.StepIndex)
                .Select(completion => completion.StepIndex).ToArray());
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = journal,
                Pooling = false,
            }.ToString());
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ModelRoute Route(ReasoningReplayPolicy replayPolicy) =>
        ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
            ProviderFamily.OpenAiChatCompatible, reasoningCapability: new ReasoningCapability(
                supported: true, effortLevels: null, replayPolicy: replayPolicy));
}
