using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class RunCreationCommandOutcomeTests
{
    private static readonly EventCodecs Codecs = EventCodecs.Create();

    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";

    private static CommandAck Send(IOmniClient client, string commandId, string payload) =>
        client.Send(WireEnvelope.Command(commandId, payload), TestContext.Current.CancellationToken);

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

    [Fact]
    public void Explore_and_act_ack_ranges_belong_to_their_new_sessions_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-run-outcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var server = OmniHost.CreateInMemoryServer();
            var exploreId = Ids.NewV7();
            var exploreAck = Send(server, exploreId, Payload(
                JsonObj.Field("cmd", "explore.start"), JsonObj.Field("objective", "inspect a project")));
            Assert.Equal("ok", exploreAck.Status);
            Assert.Null(exploreAck.Error);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, exploreAck.Outcome?.Kind);
            var exploreSession = Assert.IsType<SessionId>(server.LastSessionId());
            var exploreRun = Assert.IsType<RunId>(server.LastRunId());
            var exploreEvents = CausedEvents(server, exploreSession, exploreId);
            Assert.Contains(exploreEvents, evt => evt.Type.ToString() == "session.created");
            Assert.Contains(exploreEvents, evt => evt.Type.ToString() == "run.created");
            AssertRangeMatches(exploreAck, exploreEvents);
            var exploreProjection = RunProjection.Replay(exploreSession, exploreRun, Codecs,
                server.AcquireStore().ReadFrom(exploreSession, 1));
            Assert.Equal(RunMode.Plan, exploreProjection.Mode);
            Assert.Equal(RunState.Running, exploreProjection.State);

            // Give the old session a larger sequence so a leaked baseline/range is observable.
            var oldSequence = server.AcquireStore().CurrentSequence(exploreSession);
            var oldSessionStream = new EventStream(server.AcquireStore(), Codecs, exploreSession);
            for (var i = 0; i < 20; i++)
            {
                oldSessionStream.Append(new UserInputReceived(exploreRun, "[\"prior\"]", null));
            }

            Assert.True(server.AcquireStore().CurrentSequence(exploreSession) > oldSequence);
            var actId = Ids.NewV7();
            var actAck = Send(server, actId, Payload(JsonObj.Field("cmd", "act"),
                JsonObj.Field("objective", "apply a safe change"), JsonObj.Field("workspace", root)));
            Assert.Equal("ok", actAck.Status);
            Assert.Null(actAck.Error);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, actAck.Outcome?.Kind);

            var actSession = Assert.IsType<SessionId>(server.LastSessionId());
            var actRun = Assert.IsType<RunId>(server.LastRunId());
            Assert.NotEqual(exploreSession, actSession);
            var actEvents = CausedEvents(server, actSession, actId);
            Assert.Contains(actEvents, evt => evt.Type.ToString() == "session.created");
            Assert.Contains(actEvents, evt => evt.Type.ToString() == "workspace.root_established");
            Assert.Contains(actEvents, evt => evt.Type.ToString() == "run.created");
            AssertRangeMatches(actAck, actEvents);
            Assert.Equal(1, actAck.FirstSeq);
            Assert.Equal(server.AcquireStore().CurrentSequence(actSession), actAck.LastSeq);
            Assert.True(actAck.LastSeq < server.AcquireStore().CurrentSequence(exploreSession));
            var actProjection = RunProjection.Replay(actSession, actRun, Codecs,
                server.AcquireStore().ReadFrom(actSession, 1));
            Assert.Equal(RunMode.Act, actProjection.Mode);
            Assert.Equal(RunState.Running, actProjection.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_objectives_are_rejected_without_creating_sessions()
    {
        var server = OmniHost.CreateInMemoryServer();
        var exploreId = Ids.NewV7();
        var explore = Send(server, exploreId, Payload(JsonObj.Field("cmd", "explore.start")));
        Assert.Equal("error", explore.Status);
        Assert.Equal("falta el objetivo del Explorer", explore.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, explore.Outcome?.Kind);
        Assert.Null(explore.FirstSeq);
        Assert.Null(explore.LastSeq);
        Assert.Null(server.LastSessionId());

        var actId = Ids.NewV7();
        var act = Send(server, actId, Payload(JsonObj.Field("cmd", "act"),
            JsonObj.Field("objective", " ")));
        Assert.Equal("error", act.Status);
        Assert.Equal("falta el objetivo del act", act.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, act.Outcome?.Kind);
        Assert.Null(act.FirstSeq);
        Assert.Null(act.LastSeq);
        Assert.Null(server.LastSessionId());
    }

    [Fact]
    public void Command_invoke_reports_expansion_success_without_events_and_rejects_invalid_invocations()
    {
        var server = OmniHost.CreateInMemoryServer();
        var validId = Ids.NewV7();
        var valid = Send(server, validId,
            CommandInvocationJson.Encode(new CommandInvocation("explain", new[] { "src/tests" }, "Typed")));
        Assert.Equal("ok", valid.Status);
        Assert.Null(valid.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, valid.Outcome?.Kind);
        Assert.Null(valid.FirstSeq);
        Assert.Null(valid.LastSeq);
        Assert.Null(server.LastSessionId());
        var expandedJson = server.Query("commandOutcome", CancellationToken.None)!.Json;
        using var expanded = System.Text.Json.JsonDocument.Parse(expandedJson);
        Assert.Contains("src/tests", expanded.RootElement.GetProperty("outcome").GetProperty("text").GetString());

        var unknownCommandId = Ids.NewV7();
        var unknownCommand = Send(server, unknownCommandId,
            CommandInvocationJson.Encode(new CommandInvocation("not-registered", Array.Empty<string>(), "Typed")));
        Assert.Equal("error", unknownCommand.Status);
        Assert.Equal("command.prompt_not_found:not-registered", unknownCommand.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, unknownCommand.Outcome?.Kind);
        Assert.Null(unknownCommand.FirstSeq);
        Assert.Null(unknownCommand.LastSeq);

        var malformedId = Ids.NewV7();
        var malformed = Send(server, malformedId, Payload(JsonObj.Field("cmd", "command.invoke"),
            JsonObj.FieldRaw("name", "42"), JsonObj.FieldRaw("arguments", "[]")));
        Assert.Equal("error", malformed.Status);
        Assert.False(string.IsNullOrWhiteSpace(malformed.Error));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, malformed.Outcome?.Kind);
        Assert.Null(malformed.FirstSeq);
        Assert.Null(malformed.LastSeq);
    }
}
