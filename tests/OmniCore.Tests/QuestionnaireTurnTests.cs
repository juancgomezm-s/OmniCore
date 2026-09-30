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

public sealed class QuestionnaireTurnTests
{
    [Fact]
    public void User_ask_is_available_in_explorer_and_act_catalogs_as_no_effect_core_tool()
    {
        foreach (var catalog in new[] { HostTools.Explorer().Catalog(), OmniHost.CreateActTools().Catalog() })
        {
            var tool = catalog.Find(new ToolId("user.ask"));
            Assert.NotNull(tool);
            Assert.Equal(EffectClass.None, tool!.Descriptor.EffectClass);
            Assert.Equal(TrustLevel.Core, tool.Descriptor.Source.Trust);
            Assert.Equal("core", tool.Descriptor.Source.Owner);
        }
    }

    private static readonly QuestionnaireSchema Schema = new QuestionnaireSchema("Choose an approach", null,
        new QuestionField[] { new QuestionField("approach", "Which approach?", null,
            QuestionKind.SingleChoice, new[] { new QuestionOption("safe", "Safe", null),
                new QuestionOption("fast", "Fast", null) }, null, true, null, null, null) });

    [Fact]
    public void Questionnaire_suspends_then_restarts_and_returns_answer_as_original_tool_result()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-question-turn-" + Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(root, "journal.db");
        var blobs = Path.Combine(root, "blobs");
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(blobs);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null);
            var fingerprint = new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3");
            var scriptedCalls = 0;
            ModelResponse AskResponse(ModelRequest _, CancellationToken __)
            {
                scriptedCalls++;
                return new ModelResponse(new ContentBlock[] {
                    new ToolCallBlock(ToolCallId.New(), "provider-call-1", "user.ask",
                        QuestionnaireCodec.EncodeSchema(Schema))
                }, StopReason.ToolUse, new TokenUsage(2, 1, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "", null));
            }
            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete) =>
                new ExplorerTurn(complete, executor, catalog,
                    new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                    fingerprint, selection, store!, codecs, artifacts, new InMemoryAuditSink(),
                    new RedactionPolicy(), questionnaires: service);

            var first = MakeTurn(AskResponse);
            var suspended = first.Ask("ask the user", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.NotNull(suspended.PendingInteractionId);
            Assert.Equal(1, scriptedCalls);
            var pending = service.Pending(session);
            Assert.Single(pending);
            Assert.NotNull(service.SchemaFor(new EventStream(store, codecs, session), pending[0].InteractionId));
            foreach (var evt in store.ReadFrom(session, 1))
            {
                Assert.DoesNotContain("Choose an approach", evt.PayloadJson, StringComparison.Ordinal);
                Assert.DoesNotContain("Which approach?", evt.PayloadJson, StringComparison.Ordinal);
            }
            Assert.Equal(RunState.AwaitingInput,
                RunProjection.Replay(session, run.RunId, codecs, store.ReadFrom(session, 1)).State);

            var originalTurn = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
                .OfType<TurnStarted>().Single().TurnId;
            var interaction = pending[0].InteractionId;
            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(blobs);
            service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "lastsession.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            var accepted = server.RespondToQuestionnaire(interaction,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false);
            Assert.Equal("ok", accepted.Status);
            Assert.Equal(RunState.Running,
                RunProjection.Replay(session, run.RunId, codecs, store.ReadFrom(session, 1)).State);

            string? receivedAnswer = null;
            var resumed = new ExplorerTurn((request, token) =>
            {
                var resultBlocks = request.Messages.SelectMany(message => message.Content)
                    .OfType<ToolResultBlock>().ToArray();
                Assert.Contains(resultBlocks, result => result.Content.OfType<TextBlock>()
                    .Any(text => text.Text.Contains("safe", StringComparison.Ordinal)));
                receivedAnswer = resultBlocks.LastOrDefault()?.Content.OfType<TextBlock>().FirstOrDefault()?.Text;
                return new ModelResponse(new ContentBlock[] { new TextBlock("recorded") }, StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()), fingerprint,
                selection, store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: service);

            var completed = resumed.Ask("resume", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, completed.StopReason);
            Assert.Contains("safe", receivedAnswer ?? "", StringComparison.Ordinal);
            var turnIds = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
                .OfType<TurnStarted>().Select(evt => evt.TurnId).ToArray();
            Assert.Single(turnIds);
            Assert.Equal(originalTurn, turnIds[0]);
            Assert.Empty(service.Pending(session));
            Assert.NotEqual("ok", server.RespondToQuestionnaire(interaction,
                new[] { new QuestionAnswer("approach", new[] { "fast" }, null, null) }, false).Status);
        }
        finally
        {
            store?.Close();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Cancelled_questionnaire_is_returned_to_the_model_as_structured_tool_result()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var artifacts = new FileArtifactStore(Path.Combine(Path.GetTempPath(), "omnicore-q-cancel-"
            + Guid.NewGuid().ToString("N")));
        var session = SessionId.New();
        var run = TestRun.Open(store, session, mode: RunMode.Plan);
        var service = new QuestionnaireInteractionService(store, codecs, artifacts);
        var catalog = new FakeCatalog().Add(new UserAskTool());
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), Path.GetTempPath());
        var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null);
        var fingerprint = new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3");
        var modelCalls = 0;
        var turn = new ExplorerTurn((request, token) =>
        {
            modelCalls++;
            if (modelCalls == 1)
                return new ModelResponse(new ContentBlock[] {
                    new ToolCallBlock(ToolCallId.New(), "provider-call", "user.ask",
                        QuestionnaireCodec.EncodeSchema(Schema))
                }, StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "", null));
            var result = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()
                .Single(block => block.Content.OfType<TextBlock>()
                    .Any(text => text.Text.Contains("cancelled", StringComparison.Ordinal)));
            Assert.Contains("\"cancelled\":true", result.Content.OfType<TextBlock>().Single().Text);
            return new ModelResponse(new ContentBlock[] { new TextBlock("cancel understood") },
                StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("scripted", "", null));
        }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            fingerprint, selection, store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            questionnaires: service, questionnaireResponder: (id, schema) =>
                QuestionnaireAskOutcome.CancelledOutcome());

        var result = turn.Ask("question", "system", session, run.RunId, run.RootLane, "",
            CancellationToken.None);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal("cancel understood", result.FinalText);
        Assert.Equal(2, modelCalls);
        Assert.Empty(service.Pending(session));
    }
}
