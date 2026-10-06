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
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Durable SQLite/CAS steering across a questionnaire suspension with scripted provider responses.</summary>
public sealed class ExplorerTurnSteeringDurableResumeTests
{
    private static readonly QuestionnaireSchema Schema = new("Choose", null, new QuestionField[]
    {
        new("approach", "Which?", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null),
    });

    [Fact]
    public void Pending_steering_survives_questionnaire_reopen_and_applies_once_at_next_model_step()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-steering-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var questionnaire = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool()).Add(FakeTool.Read("fake.read"));
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                    { ["fake.read"] = PermissionDecision.Allow }), root);
            var physicalRoute = ModelRoute.DefaultForModel("scripted", "fixture-provider",
                "http://127.0.0.1:9901", ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, physicalRoute.Id, physicalRoute);
            var fingerprint = new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3");
            var firstState = new ProviderState("fixture.kind", "{\"checkpoint\":\"first\"}");
            var secondState = new ProviderState("fixture.kind", "{\"checkpoint\":\"second\"}");
            var askCall = new ToolCallBlock(ToolCallId.New(), "provider-question", "user.ask",
                QuestionnaireCodec.EncodeSchema(Schema));
            var readCall = new ToolCallBlock(ToolCallId.New(), "read-step", "fake.read", "{}");
            var requests = new List<ModelRequest>();

            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(
                complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                fingerprint, selection, store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 2m), questionnaires: questionnaire);

            var initial = MakeTurn((request, _) =>
            {
                requests.Add(request);
                Assert.Null(request.Continuation);
                Assert.DoesNotContain(request.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
                    block => block.Text == "remember this steering");
                return new ModelResponse(new ContentBlock[] { askCall },
                    StopReason.ToolUse, new TokenUsage(10, 2, 0, 0, 0), firstState,
                    new ProviderMetadata("scripted", "fixture", null));
            }).Ask("ask the user", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, initial.StopReason);
            Assert.NotNull(initial.PendingInteractionId);
            var turnId = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<TurnStarted>()).TurnId;

            // Explicit public command while the questionnaire is pending; this must remain pending
            // and must not become ordinary user input or a FollowUp before the suspension resolves.
            var steeringId = SteeringId.New();
            var commandId = CommandId.New();
            var stateFile = Path.Combine(root, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var inputJson = "{" + string.Join(",", new[]
            {
                JsonObj.Field("cmd", "session.input"), JsonObj.Field("text", "remember this steering"),
                JsonObj.Field("kind", "steering"), JsonObj.Field("steeringId", steeringId.ToString()),
                JsonObj.Field("sessionId", session.ToString()), JsonObj.Field("runId", run.RunId.ToString()),
                JsonObj.Field("laneId", run.RootLane.ToString()), JsonObj.Field("turnId", turnId.ToString()),
            }) + "}";
            var steeringAck = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts)
                .Send(WireEnvelope.Command(commandId.ToString(), inputJson), CancellationToken.None);
            Assert.Equal("ok", steeringAck.Status);
            var receivedEvent = Assert.Single(store.ReadFrom(session, 1),
                evt => codecs.Decode(evt) is TurnSteeringReceived);
            var received = Assert.IsType<TurnSteeringReceived>(codecs.Decode(receivedEvent));
            Assert.Equal(steeringId, received.SteeringId);
            Assert.Equal(run.RunId, received.RunId);
            Assert.Equal(run.RootLane, received.LaneId);
            Assert.Equal(turnId, received.TurnId);
            Assert.Equal(commandId.Value, Assert.IsType<CommandCausation>(receivedEvent.Causation).CommandId.Value);
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnSteeringApplied>());
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnSteeringDropped>());
            Assert.Single(SteeringQueue.Pending(store, codecs, session, run.RunId, run.RootLane, turnId));

            // Reopen both durable stores before answering the interaction through the public API.
            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root);
            questionnaire = new QuestionnaireInteractionService(store, codecs, artifacts);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(initial.PendingInteractionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            var resumed = MakeTurn((request, _) =>
            {
                requests.Add(request);
                if (requests.Count == 2)
                {
                    Assert.Equal(firstState, request.Continuation);
                    var steering = Assert.Single(request.Messages.SelectMany(message => message.Content)
                        .OfType<TextBlock>(), block => block.Text == "remember this steering");
                    Assert.NotNull(steering);
                    var answer = Assert.Single(request.Messages.SelectMany(message => message.Content)
                        .OfType<ToolResultBlock>(), block => block.Id == askCall.Id);
                    Assert.False(answer.IsError);
                    Assert.Contains(answer.Content.OfType<TextBlock>(), block =>
                        block.Text.Contains("safe", StringComparison.Ordinal));
                    return new ModelResponse(new ContentBlock[] { readCall }, StopReason.ToolUse,
                        new TokenUsage(5, 1, 0, 0, 0), secondState,
                        new ProviderMetadata("scripted", "fixture", null));
                }

                Assert.Equal(secondState, request.Continuation);
                var replayedSteering = Assert.Single(request.Messages.SelectMany(message => message.Content)
                    .OfType<TextBlock>(), block => block.Text == "remember this steering");
                Assert.NotNull(replayedSteering);
                var toolResult = Assert.Single(request.Messages.SelectMany(message => message.Content)
                    .OfType<ToolResultBlock>(), block => block.Id == readCall.Id);
                Assert.False(toolResult.IsError);
                Assert.Single(request.Messages.SelectMany(message => message.Content)
                    .OfType<ToolResultBlock>(), block => block.Id == askCall.Id);
                return new ModelResponse(new ContentBlock[] { new TextBlock("completed after steering") },
                    StopReason.EndTurn, new TokenUsage(6, 2, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "fixture", null));
            }).Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, resumed.StopReason);
            Assert.Equal(3, requests.ToArray().Length);
            var events = store.ReadFrom(session, 1);
            var decoded = events.Select(codecs.Decode).ToArray();
            Assert.Single(decoded.OfType<TurnStarted>());
            Assert.Empty(decoded.OfType<FollowUpQueued>());
            Assert.DoesNotContain(decoded.OfType<UserInputReceived>(), input =>
                input.InputPartsJson.Contains("remember this steering", StringComparison.Ordinal));
            var applied = Assert.Single(decoded.OfType<TurnSteeringApplied>());
            Assert.Equal(steeringId, applied.SteeringId);
            Assert.Equal(run.RunId, applied.RunId);
            Assert.Equal(run.RootLane, applied.LaneId);
            Assert.Equal(turnId, applied.TurnId);
            Assert.Equal(1, applied.StepIndex);
            var appliedEnvelope = Assert.Single(events, evt => codecs.Decode(evt) is TurnSteeringApplied item
                && item.SteeringId == steeringId);
            Assert.Equal(run.RunId, appliedEnvelope.RunId);
            Assert.Equal(run.RootTask, appliedEnvelope.TaskId);
            Assert.Equal(run.RootLane, appliedEnvelope.LaneId);
            Assert.Equal(turnId, appliedEnvelope.TurnId);
            Assert.Empty(decoded.OfType<TurnSteeringDropped>());
            Assert.Empty(SteeringQueue.Pending(store, codecs, session, run.RunId, run.RootLane, turnId));
            Assert.Equal(steeringId, Assert.Single(SteeringQueue.Applied(store, codecs, session,
                run.RunId, run.RootLane, turnId)).Id);

            var starts = decoded.OfType<ModelStepStarted>().Where(step => step.TurnId == turnId)
                .OrderBy(step => step.StepIndex).ToArray();
            Assert.Equal(new[] { 0, 1, 2 }, starts.Select(step => step.StepIndex));
            Assert.All(starts, step => Assert.Equal(physicalRoute.Id, step.RouteId));
            var completions = decoded.OfType<ModelStepCompleted>().Where(step => step.TurnId == turnId)
                .OrderBy(step => step.StepIndex).ToArray();
            Assert.Equal(3, completions.Length);
            Assert.Equal(new long[] { 10, 5, 6 }, completions.Select(step => step.Usage.Input));
            Assert.Equal(new long[] { 2, 1, 2 }, completions.Select(step => step.Usage.Output));
            Assert.Equal(new decimal?[] { 0.000014m, 0.000007m, 0.000010m },
                completions.Select(step => step.CostUsd));
            var turnArtifact = Assert.Single(decoded.OfType<ModelCompleted>(), item => item.TurnId == turnId);
            using (var usage = System.Text.Json.JsonDocument.Parse(artifacts.GetText(turnArtifact.ResponseArtifact!.Hash)!))
            {
                Assert.Equal(21L, usage.RootElement.GetProperty("input").GetInt64());
                Assert.Equal(5L, usage.RootElement.GetProperty("output").GetInt64());
                Assert.Equal("0.000031", usage.RootElement.GetProperty("costUsd").GetString());
            }
            Assert.Single(decoded.OfType<ToolCallSucceeded>(), item => item.ToolCallId == readCall.Id);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Applied_steering_is_not_replayed_after_second_suspension_and_new_pending_is_consumed()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-steering-second-suspend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var questionnaire = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var physicalRoute = ModelRoute.DefaultForModel("scripted", "fixture-provider",
                "http://127.0.0.1:9901", ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, physicalRoute.Id, physicalRoute);
            var fingerprint = new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3");
            var state0 = new ProviderState("fixture.kind", "{\"checkpoint\":0}");
            var state1 = new ProviderState("fixture.kind", "{\"checkpoint\":1}");
            var ask0 = new ToolCallBlock(ToolCallId.New(), "ask-0", "user.ask", QuestionnaireCodec.EncodeSchema(Schema));
            var ask1 = new ToolCallBlock(ToolCallId.New(), "ask-1", "user.ask", QuestionnaireCodec.EncodeSchema(Schema));
            var requests = new List<ModelRequest>();

            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(
                complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                fingerprint, selection, store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: questionnaire);

            var first = MakeTurn((request, _) =>
            {
                requests.Add(request);
                return new ModelResponse(new ContentBlock[] { ask0 }, StopReason.ToolUse,
                    new TokenUsage(3, 1, 0, 0, 0), state0, new ProviderMetadata("scripted", "fixture", null));
            }).Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, first.StopReason);
            var turn = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>()).TurnId;
            var steering1 = SteeringId.New();
            var stateFile = Path.Combine(root, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            SendSteering(server, session, run.RunId, run.RootLane, turn, steering1, "first durable direction");
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnSteeringApplied>());

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root);
            questionnaire = new QuestionnaireInteractionService(store, codecs, artifacts);
            server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(first.PendingInteractionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            var second = MakeTurn((request, _) =>
            {
                requests.Add(request);
                Assert.Equal(state0, request.Continuation);
                Assert.Single(request.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
                    block => block.Text == "first durable direction");
                var steering2 = SteeringId.New();
                SendSteering(server, session, run.RunId, run.RootLane, turn,
                    steering2, "second durable direction");
                return new ModelResponse(new ContentBlock[] { ask1 }, StopReason.ToolUse,
                    new TokenUsage(4, 1, 0, 0, 0), state1, new ProviderMetadata("scripted", "fixture", null));
            }).Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, second.StopReason);
            Assert.NotNull(second.PendingInteractionId);
            var receivedAtSecondBoundary = store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<TurnSteeringReceived>().OrderBy(item => item.SteeringId.ToString(), StringComparer.Ordinal).ToArray();
            Assert.Equal(2, receivedAtSecondBoundary.Length);
            var received1 = Assert.Single(receivedAtSecondBoundary, item => item.SteeringId == steering1);
            var steering2Id = Assert.Single(receivedAtSecondBoundary, item => item.SteeringId != steering1).SteeringId;
            Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnSteeringApplied>(),
                item => item.SteeringId == steering1 && item.StepIndex == 1);
            Assert.Single(SteeringQueue.Pending(store, codecs, session, run.RunId, run.RootLane, turn),
                item => item.Id == steering2Id);

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root);
            questionnaire = new QuestionnaireInteractionService(store, codecs, artifacts);
            server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(second.PendingInteractionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            var final = MakeTurn((request, _) =>
            {
                requests.Add(request);
                Assert.Equal(state1, request.Continuation);
                Assert.Single(request.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
                    block => block.Text == "first durable direction");
                Assert.Single(request.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
                    block => block.Text == "second durable direction");
                return new ModelResponse(new ContentBlock[] { new TextBlock("finished") }, StopReason.EndTurn,
                    new TokenUsage(2, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "fixture", null));
            }).Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, final.StopReason);
            Assert.Equal(3, requests.ToArray().Length);

            var events = store.ReadFrom(session, 1);
            var decoded = events.Select(codecs.Decode).ToArray();
            var applies = decoded.OfType<TurnSteeringApplied>().OrderBy(item => item.StepIndex).ToArray();
            Assert.Equal(new[] { (steering1, 1), (steering2Id, 2) },
                applies.Select(item => (item.SteeringId, item.StepIndex)));
            Assert.Equal(receivedAtSecondBoundary.Length, decoded.OfType<TurnSteeringReceived>().ToArray().Length);
            Assert.Empty(decoded.OfType<TurnSteeringDropped>());
            Assert.Single(decoded.OfType<TurnStarted>());
            Assert.Empty(decoded.OfType<FollowUpQueued>());
            Assert.DoesNotContain(decoded.OfType<UserInputReceived>(), input =>
                input.InputPartsJson.Contains("durable direction", StringComparison.Ordinal));
            Assert.Empty(SteeringQueue.Pending(store, codecs, session, run.RunId, run.RootLane, turn));
            Assert.Equal(new[] { steering1, steering2Id }, SteeringQueue.Applied(store, codecs, session,
                run.RunId, run.RootLane, turn).Select(item => item.Id));
            Assert.Equal(run.RunId, received1.RunId);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void SendSteering(OmniServer server, SessionId session, RunId run, LaneId lane,
        TurnId turn, SteeringId steeringId, string text)
    {
        var commandId = CommandId.New();
        var json = "{" + string.Join(",", new[]
        {
            JsonObj.Field("cmd", "session.input"), JsonObj.Field("text", text), JsonObj.Field("kind", "steering"),
            JsonObj.Field("steeringId", steeringId.ToString()), JsonObj.Field("sessionId", session.ToString()),
            JsonObj.Field("runId", run.ToString()), JsonObj.Field("laneId", lane.ToString()),
            JsonObj.Field("turnId", turn.ToString()),
        }) + "}";
        var ack = server.Send(WireEnvelope.Command(commandId.ToString(), json), CancellationToken.None);
        Assert.Equal("ok", ack.Status);
    }
}
