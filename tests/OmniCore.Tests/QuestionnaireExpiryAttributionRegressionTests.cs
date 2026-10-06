using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>SQLite/CAS regressions for questionnaire expiry and post-reopen envelope ownership.</summary>
public sealed class QuestionnaireExpiryAttributionRegressionTests
{
    private static readonly QuestionnaireSchema Schema = new("Pick one", null,
        new[] { new QuestionField("choice", "Choice", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("yes", "Yes", null) }, null, true, null, null, null) });

    [Fact]
    public void Cancelled_run_question_is_not_pending_after_reopen()
    {
        var root = NewPrivateRoot();
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var data = Path.Combine(root, "data");
        SqliteEventStore? store = null;
        try
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(data);
            var session = SessionId.New();
            var firstRun = TestRun.Open(store, session, "question run", RunMode.Plan);
            var interaction = PublishPendingQuestion(store, codecs, artifacts, session, firstRun);

            var firstConnection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(firstConnection);
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(data);

            new RunControlService(store, codecs).CancelRun(session, firstRun.RunId);
            Assert.Contains(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is InteractionExpired expired
                && expired.InteractionId == interaction);
            Assert.Equal(RunState.Cancelled,
                RunProjection.Replay(session, firstRun.RunId, codecs, store.ReadFrom(session, 1)).State);

            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            Assert.Empty(service.Pending(session));
        }
        finally
        {
            CloseAndClearPool(store);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Expired_question_cannot_be_answered_after_a_new_run_starts_in_same_session()
    {
        var root = NewPrivateRoot();
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var data = Path.Combine(root, "data");
        var stateFile = Path.Combine(root, "last-session.txt");
        SqliteEventStore? store = null;
        try
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(data);
            var session = SessionId.New();
            var firstRun = TestRun.Open(store, session, "question run", RunMode.Plan);
            var interaction = PublishPendingQuestion(store, codecs, artifacts, session, firstRun);

            var firstConnection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(firstConnection);
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(data);

            var control = new RunControlService(store, codecs);
            control.CancelRun(session, firstRun.RunId);
            var secondRun = control.StartRun(session, "next run", RunMode.Plan);
            File.WriteAllText(stateFile, session + "\n" + secondRun);
            var beforeResponse = store.CurrentSequence(session);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);

            var response = server.RespondToQuestionnaire(interaction,
                new[] { new QuestionAnswer("choice", new[] { "yes" }, null, null) }, cancelled: false);

            Assert.Equal("error", response.Status);
            Assert.Equal(beforeResponse, store.CurrentSequence(session));
            Assert.DoesNotContain(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is InteractionResolved resolved
                && resolved.InteractionId == interaction);
            Assert.Empty(new QuestionnaireInteractionService(store, codecs, artifacts).Pending(session));
        }
        finally
        {
            CloseAndClearPool(store);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Valid_reopened_question_response_uses_request_scope_not_foreign_ambient_scope()
    {
        var root = NewPrivateRoot();
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var data = Path.Combine(root, "data");
        var stateFile = Path.Combine(root, "last-session.txt");
        SqliteEventStore? store = null;
        try
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(data);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, "question run", RunMode.Plan);
            var interaction = PublishPendingQuestion(store, codecs, artifacts, session, run);
            var requestEvent = Assert.Single(store.ReadFrom(session, 1), evt =>
                codecs.Decode(evt) is InteractionRequested request && request.InteractionId == interaction);
            Assert.Equal(run.RunId, requestEvent.RunId);
            Assert.Equal(run.RootTask, requestEvent.TaskId);
            Assert.Equal(run.RootLane, requestEvent.LaneId);
            var requestPayload = Assert.IsType<InteractionRequested>(codecs.Decode(requestEvent));
            Assert.Equal(run.RootLane, requestPayload.Lane);
            Assert.Single(requestEvent.ArtifactRefs, artifact => artifact == requestPayload.QuestionnaireSchemaRef);

            var connection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(connection);
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(data);
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            var foreign = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
            CommandAck response;
            using (ExecutionScope.Begin(foreign))
            {
                response = server.RespondToQuestionnaire(interaction,
                    new[] { new QuestionAnswer("choice", new[] { "yes" }, null, null) }, cancelled: false);
                Assert.Same(foreign, ExecutionScope.Current);
            }
            Assert.Equal("ok", response.Status);
            Assert.Null(ExecutionScope.Current);

            var events = store.ReadFrom(session, 1).ToArray();
            var resolvedEvent = Assert.Single(events, evt => codecs.Decode(evt) is InteractionResolved resolved
                && resolved.InteractionId == interaction);
            var resolved = Assert.IsType<InteractionResolved>(codecs.Decode(resolvedEvent));
            Assert.Equal(run.RunId, resolvedEvent.RunId);
            Assert.Equal(run.RunId, resolvedEvent.CorrelationId);
            Assert.Equal(run.RootTask, resolvedEvent.TaskId);
            Assert.Equal(run.RootLane, resolvedEvent.LaneId);
            Assert.Equal(requestEvent.TurnId, resolvedEvent.TurnId);
            Assert.Equal(requestEvent.ExecutionId, resolvedEvent.ExecutionId);
            Assert.Equal(requestEvent.ToolCallId, resolvedEvent.ToolCallId);
            Assert.NotNull(resolved.AnswerRef);
            Assert.Equal(new[] { resolved.AnswerRef }, resolvedEvent.ArtifactRefs);
            Assert.True(artifacts.Verify(resolved.AnswerRef!.Hash, resolved.AnswerRef.Size));
            Assert.Equal(TimeSpan.Zero, resolvedEvent.Timestamp.Offset);
            Assert.Equal("OmniCore.Engine.EventStream", resolvedEvent.Source);

            var inputEvent = Assert.Single(events, evt => codecs.Decode(evt) is UserInputReceived input
                && input.RunId == run.RunId && input.Origin == "InteractionResponse(Questionnaire)");
            var input = Assert.IsType<UserInputReceived>(codecs.Decode(inputEvent));
            Assert.Equal(run.RunId, inputEvent.RunId);
            Assert.Equal(run.RunId, inputEvent.CorrelationId);
            Assert.Equal(run.RootTask, inputEvent.TaskId);
            Assert.Equal(run.RootLane, inputEvent.LaneId);
            Assert.Equal(requestEvent.TurnId, inputEvent.TurnId);
            Assert.Equal(requestEvent.ExecutionId, inputEvent.ExecutionId);
            Assert.Equal(requestEvent.ToolCallId, inputEvent.ToolCallId);
            Assert.Equal(TimeSpan.Zero, inputEvent.Timestamp.Offset);
            Assert.Equal("OmniCore.Engine.EventStream", inputEvent.Source);

            var commandId = new CommandId(Guid.Parse(response.CommandId));
            Assert.Equal(new CommandCausation(commandId), resolvedEvent.Causation);
            Assert.Equal(new CommandCausation(commandId), inputEvent.Causation);
        }
        finally
        {
            CloseAndClearPool(store);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static InteractionId PublishPendingQuestion(SqliteEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts, SessionId session, TestRun.Opened run)
    {
        var stream = new EventStream(store, codecs, session);
        var turn = TurnId.New();
        var interaction = InteractionId.New();
        var toolCall = ToolCallId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn,
            toolCall, ExecutionId.New())))
        {
            stream.Append(new TurnStarted(turn, run.RootLane));
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var result = service.Publish(stream, Schema, interaction, run.RootLane,
                "{\"toolCallId\":\"" + toolCall + "\"}");
            Assert.True(result.Published);
            Assert.NotNull(result.SchemaArtifact);
            Assert.True(artifacts.Verify(result.SchemaArtifact!.Hash, result.SchemaArtifact.Size));
            stream.Append(new RunAwaitingInput(run.RunId, run.RootLane));
        }

        return interaction;
    }

    private static string NewPrivateRoot() => Path.Combine(Path.GetTempPath(),
        "omni-m55-question-expiry-" + Guid.NewGuid().ToString("N"));

    private static void CloseAndClearPool(SqliteEventStore? store)
    {
        if (store is null) return;
        var connection = (SqliteConnection)store.Connection;
        store.Close();
        SqliteConnection.ClearPool(connection);
    }
}
