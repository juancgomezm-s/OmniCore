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

    /// <summary>
    /// Bug 6: uso de los pasos previos a una suspensión se contabiliza durable.
    /// Un Turn con un paso de modelo seguido por InputRequired persiste el uso
    /// antes de devolver (evento ModelStepCompleted) y sigue abierto con el mismo TurnId.
    /// Reanudar y completar conserva el uso anterior y añade el nuevo exactamente una vez.
    /// </summary>
    [Fact]
    public void ModelStepCompleted_persists_usage_before_suspension_and_resume_adds_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-bug6-" + Guid.NewGuid().ToString("N"));
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
            // Pricing: $10 per million input tokens, $20 per million output tokens
            var pricing = new ModelPricing(10m, 20m);

            // First model call: returns user.ask (suspends)
            // Second model call (after resume): returns final text
            var modelCalls = 0;
            ModelResponse AskThenEnd(ModelRequest _, CancellationToken __)
            {
                modelCalls++;
                if (modelCalls == 1)
                {
                    // Step 1: 1_000_000 input + 500_000 output tokens = cost 10*1 + 20*0.5 = 10 + 10 = 20
                    return new ModelResponse(new ContentBlock[] {
                        new ToolCallBlock(ToolCallId.New(), "provider-call-1", "user.ask",
                            QuestionnaireCodec.EncodeSchema(Schema))
                    }, StopReason.ToolUse, new TokenUsage(1_000_000, 500_000, 0, 0, 0), null,
                        new ProviderMetadata("scripted", "", null));
                }
                // Step 2 (after resume): 300_000 input + 200_000 output = 10*0.3 + 20*0.2 = 3 + 4 = 7
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") },
                    StopReason.EndTurn, new TokenUsage(300_000, 200_000, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "", null));
            }

            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete) =>
                new ExplorerTurn(complete, executor, catalog,
                    new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                    fingerprint, selection, store!, codecs, artifacts, new InMemoryAuditSink(),
                    new RedactionPolicy(), pricing: pricing, questionnaires: service);

            // First Ask: should suspend at user.ask
            var firstTurn = MakeTurn(AskThenEnd);
            var suspended = firstTurn.Ask("ask the user", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            // Verify suspension
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.NotNull(suspended.PendingInteractionId);
            Assert.Equal(1, modelCalls);
            Assert.Equal(new TokenUsage(1_000_000, 500_000, 0, 0, 0), suspended.Usage);

            // Verify ModelStepCompleted event was emitted for step 1
            var eventsAfterSuspend = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt)).ToArray();
            var modelStepEvents = eventsAfterSuspend.OfType<ModelStepCompleted>().ToArray();
            Assert.Single(modelStepEvents);
            var step1 = modelStepEvents[0];
            Assert.Equal(suspended.Usage, step1.Usage);
            Assert.Equal(1, step1.StepIndex);
            Assert.Equal(StopReason.ToolUse, step1.StopReason);
            Assert.NotNull(step1.CostUsd);
            Assert.Equal(20m, step1.CostUsd.Value);

            // Verify Turn is still open (no TurnCompleted, no TurnAbandoned)
            var hasTerminal = eventsAfterSuspend.OfType<TurnCompleted>().Any()
                || eventsAfterSuspend.OfType<TurnAbandoned>().Any()
                || eventsAfterSuspend.OfType<TurnInterrupted>().Any();
            Assert.False(hasTerminal);

            // Verify ReadJournalSpend sees the step 1 cost via EventStream replay
            var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var stream = new EventStream(store, codecs, session);
            var method = firstTurn.GetType().GetMethod("ReadJournalSpend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var spendObj = method.Invoke(firstTurn, new object[] { stream, run.RunId, today });
            var spendRunUsd = (decimal)spendObj!.GetType().GetProperty("RunUsd")!.GetValue(spendObj)!;
            var spendIncomplete = (bool)spendObj!.GetType().GetProperty("Incomplete")!.GetValue(spendObj)!;
            Assert.Equal(20m, spendRunUsd);
            Assert.False(spendIncomplete);

            // Resume the turn by answering the questionnaire
            var interaction = service.Pending(session)[0].InteractionId;
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

            // Second Ask: should complete
            var resumedTurn = MakeTurn(AskThenEnd);
            var completed = resumedTurn.Ask("resume", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            // Verify completion
            Assert.Equal(StopReason.EndTurn, completed.StopReason);
            Assert.Equal(2, modelCalls);
            // Total usage: step1 (1M+500k) + step2 (300k+200k) = 1.3M input, 700k output
            Assert.Equal(new TokenUsage(1_300_000, 700_000, 0, 0, 0), completed.Usage);

            // Verify ModelStepCompleted events: 2 total (step 1 and step 2)
            var allEvents = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt)).ToArray();
            var allModelSteps = allEvents.OfType<ModelStepCompleted>().OrderBy(e => e.StepIndex).ToArray();
            Assert.Equal(2, allModelSteps.Length);
            Assert.Equal(1, allModelSteps[0].StepIndex);
            Assert.Equal(2, allModelSteps[1].StepIndex);
            Assert.Equal(new TokenUsage(1_000_000, 500_000, 0, 0, 0), allModelSteps[0].Usage);
            Assert.Equal(new TokenUsage(300_000, 200_000, 0, 0, 0), allModelSteps[1].Usage);
            Assert.NotNull(allModelSteps[0].CostUsd);
            Assert.NotNull(allModelSteps[1].CostUsd);
            Assert.Equal(20m, allModelSteps[0].CostUsd!.Value);
            Assert.Equal(7m, allModelSteps[1].CostUsd!.Value);

            // Verify TurnCompleted was emitted
            var turnCompleted = allEvents.OfType<TurnCompleted>().First();
            var turnStarted = allEvents.OfType<TurnStarted>().First();
            Assert.Equal(turnStarted.TurnId, turnCompleted.TurnId);

            // Verify same TurnId throughout
            Assert.Equal(turnStarted.TurnId, step1.TurnId);
            Assert.Equal(turnStarted.TurnId, allModelSteps[1].TurnId);

            // Verify ReadJournalSpend after completion sees total cost (20 + 7 = 27)
            var stream2 = new EventStream(store, codecs, session);
            var method2 = resumedTurn.GetType().GetMethod("ReadJournalSpend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var finalSpendObj = method2.Invoke(resumedTurn, new object[] { stream2, run.RunId, today });
            var finalRunUsd = (decimal)finalSpendObj!.GetType().GetProperty("RunUsd")!.GetValue(finalSpendObj)!;
            var finalIncomplete = (bool)finalSpendObj!.GetType().GetProperty("Incomplete")!.GetValue(finalSpendObj)!;
            Assert.Equal(27m, finalRunUsd);
            Assert.False(finalIncomplete);

            // Verify ModelCompleted artifact exists with final response
            var modelCompleted = allEvents.OfType<ModelCompleted>().First();
            Assert.NotNull(modelCompleted.ResponseArtifact);

            // Reopen SQLite and verify spend is the same
            store.Close();
            var store2 = new SqliteEventStore(journal);
            var eventsReopened = store2.ReadFrom(session, 1).Select(evt => codecs.Decode(evt)).ToArray();
            var reopenedModelSteps = eventsReopened.OfType<ModelStepCompleted>().OrderBy(e => e.StepIndex).ToArray();
            Assert.Equal(2, reopenedModelSteps.Length);
            Assert.NotNull(reopenedModelSteps[0].CostUsd);
            Assert.NotNull(reopenedModelSteps[1].CostUsd);
            Assert.Equal(20m, reopenedModelSteps[0].CostUsd!.Value);
            Assert.Equal(7m, reopenedModelSteps[1].CostUsd!.Value);
            store2.Close();
        }
        finally
        {
            store?.Close();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
