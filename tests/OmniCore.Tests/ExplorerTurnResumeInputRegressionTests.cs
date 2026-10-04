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
/// Regression: a user input supplied to a NEW ExplorerTurn.Ask that resumes a turn suspended on a
/// questionnaire must reach the provider as an ordinary USER message and be journaled as an ordinary
/// UserInputReceived (not an InteractionResponse transition). Expected RED today: on resume the turn
/// builds safeQuestion = "" so the resumed input never reaches the provider request.
/// </summary>
public sealed class ExplorerTurnResumeInputRegressionTests
{
    private static readonly QuestionnaireSchema Schema = new QuestionnaireSchema("Choose an approach", null,
        new QuestionField[] { new QuestionField("approach", "Which approach?", null,
            QuestionKind.SingleChoice, new[] { new QuestionOption("safe", "Safe", null),
                new QuestionOption("fast", "Fast", null) }, null, true, null, null, null) });

    [Fact]
    public void Resume_input_after_questionnaire_reaches_provider_as_user_message()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-explorer-resume-input-" + Guid.NewGuid().ToString("N"));
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

            // First Ask emits user.ask and suspends (proven questionnaire path).
            var first = MakeTurn(AskResponse);
            var suspended = first.Ask("ask the user", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.NotNull(suspended.PendingInteractionId);
            Assert.Equal(1, scriptedCalls);
            var pending = service.Pending(session);
            Assert.Single(pending);
            var originalTurn = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
                .OfType<TurnStarted>().Single().TurnId;
            var interaction = pending[0].InteractionId;

            // Reopen the store in the same process and resolve via the public interaction server.
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
            Assert.Empty(service.Pending(session));

            // Resume with a brand-new ordinary user input (not an interaction response); capture the
            // request handed to the scripted provider.
            ModelRequest? captured = null;
            var resumed = new ExplorerTurn((request, token) =>
            {
                captured = request;
                return new ModelResponse(new ContentBlock[] { new TextBlock("recorded") }, StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()), fingerprint,
                selection, store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: service);

            const string marker = "resume-user-marker";
            var completed = resumed.Ask(marker, "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, completed.StopReason);
            Assert.NotNull(captured);

            var userTexts = captured!.Messages
                .Where(message => message.Role == MessageRole.User)
                .SelectMany(message => message.Content)
                .OfType<TextBlock>()
                .Select(text => text.Text)
                .ToArray();
            Assert.Equal(1, userTexts.Count(text => text.Contains(marker, StringComparison.Ordinal)));
            Assert.Contains(userTexts, text => text.Contains("ask the user", StringComparison.Ordinal));

            var turnIds = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
                .OfType<TurnStarted>().Select(evt => evt.TurnId).ToArray();
            Assert.Single(turnIds);
            Assert.Equal(originalTurn, turnIds[0]);

            // Only reachable once the resumed input reaches the provider request: the journal must hold
            // exactly one ordinary UserInputReceived carrying the marker (origin must NOT be the
            // InteractionResponse(Questionnaire) transition, which is not a conversation user message).
            var markerInputs = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
                .OfType<UserInputReceived>()
                .Where(evt => evt.InputPartsJson.Contains(marker, StringComparison.Ordinal))
                .Where(evt => !string.Equals(evt.Origin, "InteractionResponse(Questionnaire)",
                    StringComparison.Ordinal))
                .ToArray();
            Assert.Single(markerInputs);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
