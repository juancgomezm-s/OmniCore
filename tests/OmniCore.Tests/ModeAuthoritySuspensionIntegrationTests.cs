using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Mode-authority revocation across a real SQLite/CAS questionnaire suspension and resume.</summary>
public sealed class ModeAuthoritySuspensionIntegrationTests
{
    private static readonly QuestionnaireSchema Schema = new("Choose", null,
        new QuestionField[] { new("approach", "Which?", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null) });

    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";
    private static WireEnvelope Command(string payload) => WireEnvelope.Command(Ids.NewV7(), payload);

    [Fact]
    public void User_revocation_survives_questionnaire_suspend_reopen_and_same_turn_resume()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mode-authority-suspension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var stateFile = Path.Combine(root, "last-session.txt");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root, new SecretRedactor());
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            var started = server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
                JsonObj.Field("text", "resume the same bounded turn after a user question"))),
                TestContext.Current.CancellationToken);
            Assert.Equal("ok", started.Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var lane = Assert.IsType<LaneId>(server.LastLaneId());

            var selected = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
                JsonObj.Field("mode", "act"), JsonObj.Field("effort", "ultracode"),
                JsonObj.FieldBool("adaptive", true), JsonObj.Field("allowedModes", "plan,act,orq"),
                JsonObj.FieldRaw("maxAgents", "1"), JsonObj.FieldRaw("maxDepth", "1"),
                JsonObj.FieldRaw("maxTurns", "3"), JsonObj.FieldRaw("maxToolCalls", "8"),
                JsonObj.FieldRaw("maxElapsedSeconds", "90"), JsonObj.FieldRaw("maxSpendUsd", "2.50"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
            var granted = Assert.IsType<RunModeAuthority>(server.CurrentModeAuthority());
            Assert.Equal(ProductEffort.UltraCode, granted.ProductEffort);
            Assert.True(granted.AutoModeSwitch);
            Assert.False(granted.ModePinned);
            var reasoning = new ReasoningRequest("high", null);
            var resolution = new ReasoningResolution(reasoning, reasoning, ReasoningSelectionSource.UltraCode,
                modeAuthorityRevision: granted.Revision, outputReserveTokens: 1024);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 4096, ToolMode.Direct,
                reasoning, route.Id, route, maxOutputTokens: 1024, reasoningResolution: resolution);

            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var callId = ToolCallId.New();
            var calls = 0;
            var requests = new List<ModelRequest>();
            var materializer = new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>());
            var questionnaireService = new QuestionnaireInteractionService(store!, codecs, artifacts);

            ExplorerTurn MakeTurn() => new((request, _) =>
            {
                requests.Add(request);
                calls++;
                return calls == 1
                    ? new ModelResponse(new ContentBlock[] { new ToolCallBlock(callId,
                        "provider-question", "user.ask", QuestionnaireCodec.EncodeSchema(Schema)) },
                        StopReason.ToolUse, new TokenUsage(10, 2, 0, 0, 0), null,
                        new ProviderMetadata("scripted", string.Empty, null))
                    : new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                        new TokenUsage(4, 1, 0, 0, 0), null, new ProviderMetadata("scripted", string.Empty, null));
            }, executor, catalog, materializer,
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"), selection,
                store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: questionnaireService, maximumGenerationRequestAttempts: 1);

            var suspended = MakeTurn().Ask("ask", "system", session, run, lane, string.Empty,
                TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.Equal(1, calls);
            var interactionId = Assert.IsType<InteractionId>(suspended.PendingInteractionId);
            var firstEvents = store.ReadFrom(session, 1);
            var turnStartedEvent = Assert.Single(firstEvents, evt => codecs.Decode(evt) is TurnStarted);
            var turnStarted = Assert.IsType<TurnStarted>(codecs.Decode(turnStartedEvent));
            var interactionRequestEvent = Assert.Single(firstEvents, evt =>
                codecs.Decode(evt) is InteractionRequested requested && requested.Kind == InteractionKind.Question);
            var interactionRequest = Assert.IsType<InteractionRequested>(codecs.Decode(interactionRequestEvent));
            Assert.Equal(interactionId, interactionRequest.InteractionId);
            Assert.Equal(session, interactionRequestEvent.SessionId);
            Assert.Equal(run, interactionRequestEvent.RunId);
            Assert.Equal(turnStarted.TurnId, interactionRequestEvent.TurnId);
            Assert.Equal(lane, interactionRequestEvent.LaneId);
            Assert.Equal(run, turnStartedEvent.RunId);
            Assert.Equal(lane, turnStarted.LaneId);
            Assert.Single(firstEvents.Select(codecs.Decode).OfType<ModelStepStarted>());
            var firstStep = Assert.Single(firstEvents.Select(codecs.Decode).OfType<ModelStepStarted>());
            Assert.Equal(turnStarted.TurnId, firstStep.TurnId);
            Assert.Equal(4096, firstStep.ContextBudget);
            Assert.True(resolution.IsEquivalentTo(firstStep.ReasoningResolution));
            Assert.Equal(10, Assert.Single(firstEvents.Select(codecs.Decode).OfType<ModelStepCompleted>()).Usage.Input);

            var revoked = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.revoke"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, revoked.Outcome?.Kind);
            var afterRevoke = RunProjection.Replay(session, run, codecs, store.ReadFrom(session, 1));
            var revokedAuthority = Assert.IsType<RunModeAuthority>(afterRevoke.ModeAuthority);
            Assert.Equal(ProductEffort.Standard, revokedAuthority.ProductEffort);
            Assert.True(revokedAuthority.ModePinned);
            Assert.False(revokedAuthority.AutoModeSwitch);
            Assert.Null(revokedAuthority.Authorization);

            var closedConnection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(closedConnection);
            closedConnection.Dispose();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root, new SecretRedactor());
            questionnaireService = new QuestionnaireInteractionService(store, codecs, artifacts);
            File.WriteAllText(stateFile, session + "\n" + run);
            server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);

            var pending = questionnaireService.Pending(session);
            Assert.Collection(pending, item => Assert.Equal(interactionRequest.InteractionId, item.InteractionId));
            Assert.Equal("ok", server.RespondToQuestionnaire(interactionRequest.InteractionId,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);
            Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is InteractionResolved item
                && item.InteractionId == interactionRequest.InteractionId);

            var resumed = MakeTurn().Ask("resume", "system", session, run, lane, string.Empty,
                TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, resumed.StopReason);
            Assert.Equal("done", resumed.FinalText);
            Assert.Equal(2, calls);
            Assert.Equal(2, requests.Count);
            Assert.All(requests, request =>
            {
                Assert.Equal(reasoning, request.Reasoning);
                Assert.Equal(reasoning, request.Model.Reasoning);
                Assert.Equal(4096, request.Model.ContextBudget);
                Assert.Equal(1024, request.Model.MaxOutputTokens);
                Assert.True(resolution.IsEquivalentTo(request.Model.ReasoningResolution));
            });

            var finalEvents = store.ReadFrom(session, 1);
            Assert.Single(finalEvents, evt => codecs.Decode(evt) is TurnStarted);
            var stepStarts = finalEvents.Select(codecs.Decode).OfType<ModelStepStarted>().ToArray();
            Assert.Equal(2, stepStarts.Length);
            Assert.Equal(new[] { 0, 1 }, stepStarts.Select(item => item.StepIndex));
            Assert.All(stepStarts, item =>
            {
                Assert.Equal(turnStarted.TurnId, item.TurnId);
                Assert.Equal(4096, item.ContextBudget);
                Assert.True(resolution.IsEquivalentTo(item.ReasoningResolution));
            });
            Assert.Equal(2, finalEvents.Select(codecs.Decode).OfType<ModelStepCompleted>().Count());
            Assert.Single(finalEvents, evt => codecs.Decode(evt) is InteractionRequested item
                && item.Kind == InteractionKind.Question);
            Assert.Single(finalEvents, evt => codecs.Decode(evt) is InteractionResolved item
                && item.InteractionId == interactionRequest.InteractionId);
            Assert.DoesNotContain(finalEvents, evt => codecs.Decode(evt) is RunModeTransitionAuthorized
                { Origin: "UltraCodePolicy" });

            var replayed = RunProjection.Replay(session, run, codecs, finalEvents);
            Assert.Equal(RunState.Running, replayed.State);
            AssertAuthorityEqual(revokedAuthority, replayed.ModeAuthority);
            var beforeDeniedTransition = store.CurrentSequence(session);
            var deniedTransition = server.ApplyUltraCodePolicyTransition(session, run, RunMode.Plan,
                "questionnaire response is not adaptive authority", revokedAuthority.Revision,
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, deniedTransition.Outcome?.Kind);
            Assert.Equal(beforeDeniedTransition, store.CurrentSequence(session));
        }
        finally
        {
            if (store is not null)
            {
                var connection = (SqliteConnection)store.Connection;
                store.Close();
                SqliteConnection.ClearPool(connection);
                connection.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertAuthorityEqual(RunModeAuthority expected, RunModeAuthority? actual)
    {
        var value = Assert.IsType<RunModeAuthority>(actual);
        Assert.Equal(expected.RunId, value.RunId);
        Assert.Equal(expected.Revision, value.Revision);
        Assert.Equal(expected.Mode, value.Mode);
        Assert.Equal(expected.Strategy, value.Strategy);
        Assert.Equal(expected.ProductEffort, value.ProductEffort);
        Assert.Equal(expected.ModePinned, value.ModePinned);
        Assert.Equal(expected.AutoModeSwitch, value.AutoModeSwitch);
        Assert.Equal(expected.ObjectiveRevision, value.ObjectiveRevision);
        Assert.Equal(expected.ObjectiveDigest, value.ObjectiveDigest);
        Assert.Equal(expected.PolicyRevision, value.PolicyRevision);
        if (expected.Authorization is null)
        {
            Assert.Null(value.Authorization);
            return;
        }

        var authorization = Assert.IsType<ModeSwitchAuthorization>(value.Authorization);
        Assert.Equal(expected.Authorization.AuthorizationId, authorization.AuthorizationId);
        Assert.Equal(expected.Authorization.AuthorityRevision, authorization.AuthorityRevision);
        Assert.Equal(expected.Authorization.ObjectiveRevision, authorization.ObjectiveRevision);
        Assert.Equal(expected.Authorization.ObjectiveDigest, authorization.ObjectiveDigest);
        Assert.Equal(expected.Authorization.PolicyRevision, authorization.PolicyRevision);
        Assert.Equal(expected.Authorization.Limits, authorization.Limits);
        Assert.Equal(expected.Authorization.GrantedAtUtc, authorization.GrantedAtUtc);
        Assert.True(expected.Authorization.AllowedModes.SequenceEqual(authorization.AllowedModes));
    }
}
