using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class InputInteractionCommandOutcomeTests
{
    private static readonly EventCodecs Codecs = EventCodecs.Create();

    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";

    private static CommandAck Send(IOmniClient client, string commandId, string payload) =>
        client.Send(WireEnvelope.Command(commandId, payload), TestContext.Current.CancellationToken);

    private static string SessionInput(string text) => Payload(
        JsonObj.Field("cmd", "session.input"), JsonObj.Field("text", text));

    private static IReadOnlyList<DomainEvent> CausedEvents(OmniServer server, SessionId session, string commandId) =>
        server.AcquireStore().ReadFrom(session, 1)
            .Where(evt => evt.Causation is CommandCausation cause
                && cause.CommandId.Value == Guid.Parse(commandId))
            .ToArray();

    private static void AssertRangeMatches(CommandAck ack, IReadOnlyList<DomainEvent> events)
    {
        if (events.Count == 0)
        {
            Assert.Null(ack.FirstSeq);
            Assert.Null(ack.LastSeq);
            return;
        }

        Assert.Equal(events.Min(evt => evt.Sequence), ack.FirstSeq);
        Assert.Equal(events.Max(evt => evt.Sequence), ack.LastSeq);
    }

    private static RunId StartRun(OmniServer server)
    {
        var ack = Send(server, Ids.NewV7(), SessionInput("initial objective"));
        Assert.Equal("ok", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        return Assert.IsType<RunId>(server.LastRunId());
    }

    [Fact]
    public void New_session_input_is_accepted_and_range_is_local_to_its_session()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var commandId = Ids.NewV7();
        var commandCausation = new CommandCausation(new CommandId(Guid.Parse(commandId)));
        var otherSession = SessionId.New();

        // Simulate an older journal whose per-session sequence is greater than the new session's.
        for (var i = 0; i < 20; i++)
        {
            var priorEvent = DomainEvent.Create(otherSession, EventType.Of("test.foreign"), 1,
                commandCausation, null, null, null, null, null, null, null,
                Array.Empty<ArtifactRef>(), "{}");
            store.Append(otherSession, priorEvent, DurabilityClass.Standard, CancellationToken.None);
        }

        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        var ack = Send(server, commandId, SessionInput("new session objective"));

        if (ack.Error is { } error) Assert.Fail(error);
        Assert.Null(ack.Error);
        Assert.Equal("ok", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        Assert.NotEqual(otherSession, session);
        var ownEvents = CausedEvents(server, session, commandId);
        Assert.Contains(ownEvents, evt => evt.Type.ToString() == "session.created");
        Assert.Contains(ownEvents, evt => evt.Type.ToString() == "run.created");
        AssertRangeMatches(ack, ownEvents);
        Assert.True(ack.LastSeq < store.CurrentSequence(otherSession));
        Assert.Equal(1, ownEvents.Min(evt => evt.Sequence));
    }

    [Fact]
    public void Existing_session_input_reports_only_new_command_events()
    {
        var server = OmniHost.CreateInMemoryServer();
        var run = StartRun(server);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var before = server.AcquireStore().CurrentSequence(session);
        var commandId = Ids.NewV7();

        var ack = Send(server, commandId, SessionInput("follow-on user message"));

        Assert.Equal("ok", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        Assert.Equal(run, server.LastRunId());
        var caused = CausedEvents(server, session, commandId);
        Assert.Equal(new[] { "user_input.received" }, caused.Select(evt => evt.Type.ToString()));
        AssertRangeMatches(ack, caused);
        Assert.Equal(before + 1, ack.FirstSeq);
        Assert.Equal(before + 1, ack.LastSeq);
    }

    [Fact]
    public void Input_during_open_turn_is_accepted_as_followup_not_as_immediate_user_turn()
    {
        var server = OmniHost.CreateInMemoryServer();
        var run = StartRun(server);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var store = server.AcquireStore();
        var journal = store.ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, Codecs, journal);
        var lane = Assert.Single(LaneProjection.Replay(Codecs, journal).ForTask(projection.RootTask!));
        var turn = TurnId.New();
        new EventStream(store, Codecs, session).Append(new TurnStarted(turn, lane.Id));
        var commandId = Ids.NewV7();

        var ack = Send(server, commandId, SessionInput("queue for next turn"));

        Assert.Equal("ok", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var caused = CausedEvents(server, session, commandId);
        var queued = Assert.Single(caused);
        Assert.Equal("followup.queued", queued.Type.ToString());
        var payload = Assert.IsType<FollowUpQueued>(Codecs.Decode(queued));
        Assert.Equal(run, payload.RunId);
        Assert.Equal(lane.Id, payload.LaneId);
        Assert.Equal(turn, payload.TurnId);
        AssertRangeMatches(ack, caused);
        Assert.Equal(turn, server.AcquireStore().ReadFrom(session, 1)
            .Select(Codecs.Decode).OfType<TurnStarted>().Single().TurnId);
    }

    [Fact]
    public void Missing_input_is_rejected_without_effects_or_range()
    {
        var server = OmniHost.CreateInMemoryServer();
        var commandId = Ids.NewV7();
        var ack = Send(server, commandId, Payload(JsonObj.Field("cmd", "session.input")));

        Assert.Equal("error", ack.Status);
        Assert.Equal("falta 'text'", ack.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
        Assert.Null(server.LastSessionId());
    }

    [Fact]
    public void Interaction_response_accepts_valid_option_and_rejects_invalid_duplicate_and_unknown()
    {
        var server = OmniHost.CreateInMemoryServer();
        _ = StartRun(server);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var interaction = InteractionId.New();
        new EventStream(server.AcquireStore(), Codecs, session).Append(new InteractionRequested(
            interaction, InteractionKind.Permission, "{}", "[{\"id\":\"allow_once\"},{\"id\":\"deny\"}]",
            "deny", null, null, null, null, 0, 1));

        var badOptionId = Ids.NewV7();
        var badOption = Send(server, badOptionId, InteractionResponse(interaction, "allow_anyway"));
        AssertRejectedWithoutWrites(server, session, badOptionId, badOption, "allow_anyway");

        var acceptedId = Ids.NewV7();
        var accepted = Send(server, acceptedId, InteractionResponse(interaction, "allow_once"));
        Assert.Equal("ok", accepted.Status);
        Assert.Null(accepted.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, accepted.Outcome?.Kind);
        var resolved = CausedEvents(server, session, acceptedId);
        Assert.Equal(new[] { "interaction.resolved" }, resolved.Select(evt => evt.Type.ToString()));
        AssertRangeMatches(accepted, resolved);

        var duplicateId = Ids.NewV7();
        var duplicate = Send(server, duplicateId, InteractionResponse(interaction, "allow_once"));
        Assert.Equal("error", duplicate.Status);
        Assert.False(string.IsNullOrWhiteSpace(duplicate.Error));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, duplicate.Outcome?.Kind);
        var duplicateEvents = CausedEvents(server, session, duplicateId);
        Assert.Empty(duplicateEvents);
        AssertRangeMatches(duplicate, duplicateEvents);

        var unknown = InteractionId.New();
        var unknownId = Ids.NewV7();
        var unknownAck = Send(server, unknownId, InteractionResponse(unknown, "deny"));
        Assert.Equal("error", unknownAck.Status);
        Assert.False(string.IsNullOrWhiteSpace(unknownAck.Error));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, unknownAck.Outcome?.Kind);
        var unknownEvents = CausedEvents(server, session, unknownId);
        Assert.Empty(unknownEvents);
        AssertRangeMatches(unknownAck, unknownEvents);
    }

    [Fact]
    public void Questionnaire_response_outcomes_follow_validation_and_durable_resolution()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "omnicore-outcome-questionnaire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(tempDir);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), artifacts);
            _ = StartRun(server);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var interaction = InteractionId.New();
            var schema = new QuestionnaireSchema("Pick one", null, new[]
            {
                new QuestionField("choice", "Choice", null, QuestionKind.SingleChoice,
                    new[] { new QuestionOption("valid", "Valid", null) }, null, true, null, null, null),
            });
            var published = new QuestionnaireInteractionService(store, codecs, artifacts).Publish(
                new EventStream(store, codecs, session), schema, interaction, null, null);
            Assert.True(published.Published);

            var invalidId = Ids.NewV7();
            var invalid = Send(server, invalidId, QuestionnaireResponse(interaction, "wrong", false));
            Assert.Equal("error", invalid.Status);
            Assert.False(string.IsNullOrWhiteSpace(invalid.Error));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, invalid.Outcome?.Kind);
            var invalidEvents = CausedEvents(server, session, invalidId);
            Assert.Empty(invalidEvents);
            AssertRangeMatches(invalid, invalidEvents);

            var validId = Ids.NewV7();
            var valid = Send(server, validId, QuestionnaireResponse(interaction, "valid", false));
            Assert.Equal("ok", valid.Status);
            Assert.Null(valid.Error);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, valid.Outcome?.Kind);
            var resolved = CausedEvents(server, session, validId);
            Assert.Equal(new[] { "interaction.resolved" }, resolved.Select(evt => evt.Type.ToString()));
            AssertRangeMatches(valid, resolved);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string InteractionResponse(InteractionId interaction, string optionId) => Payload(
        JsonObj.Field("cmd", "interaction.respond"), JsonObj.Field("interactionId", interaction.ToString()),
        JsonObj.Field("optionId", optionId));

    private static string QuestionnaireResponse(InteractionId interaction, string selected, bool cancelled)
    {
        var answers = QuestionnaireCodec.EncodeAnswers(new[]
        {
            new QuestionAnswer("choice", new[] { selected }, null, null),
        });
        return Payload(JsonObj.Field("cmd", "interaction.respond"),
            JsonObj.Field("responseType", "questionnaire"),
            JsonObj.Field("interactionId", interaction.ToString()), JsonObj.FieldRaw("answers", answers),
            JsonObj.Field("cancelled", cancelled ? "true" : "false"));
    }

    private static void AssertRejectedWithoutWrites(OmniServer server, SessionId session, string commandId,
        CommandAck ack, string optionId)
    {
        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrWhiteSpace(ack.Error));
        Assert.Contains(optionId, ack.Error, StringComparison.Ordinal);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        var caused = CausedEvents(server, session, commandId);
        Assert.Empty(caused);
        AssertRangeMatches(ack, caused);
    }
}
