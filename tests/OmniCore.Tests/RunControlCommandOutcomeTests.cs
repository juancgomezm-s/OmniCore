using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class RunControlCommandOutcomeTests
{
    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";

    private static CommandAck Send(IOmniClient client, string commandId, string payload) =>
        client.Send(WireEnvelope.Command(commandId, payload), TestContext.Current.CancellationToken);

    private static IReadOnlyList<DomainEvent> CausedEvents(OmniServer server, string commandId)
    {
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var command = Guid.Parse(commandId);
        return server.AcquireStore().ReadFrom(session, 1)
            .Where(evt => evt.Causation is CommandCausation causation
                && causation.CommandId.Value == command)
            .ToArray();
    }

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

    private static CommandAck StartRun(OmniServer server)
    {
        var inputId = Ids.NewV7();
        return Send(server, inputId, Payload(JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "test objective")));
    }

    private static string RunCommand(string name, RunId run) => Payload(
        JsonObj.Field("cmd", name), JsonObj.Field("runId", run.ToString()));

    [Fact]
    public void Interrupt_then_second_interrupt_cancel_returns_exact_command_ranges()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", StartRun(server).Status);
        var run = Assert.IsType<RunId>(server.LastRunId());

        var firstId = Ids.NewV7();
        var first = Send(server, firstId, RunCommand("run.interrupt", run));
        Assert.Equal("ok", first.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, first.Outcome?.Kind);
        var firstEvents = CausedEvents(server, firstId);
        Assert.Equal(new[] { "run.awaiting_input" }, firstEvents.Select(evt => evt.Type.ToString()));
        AssertRangeMatches(first, firstEvents);

        var secondId = Ids.NewV7();
        var second = Send(server, secondId, RunCommand("run.interrupt", run));
        Assert.Equal("ok", second.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, second.Outcome?.Kind);
        var secondEvents = CausedEvents(server, secondId);
        Assert.Contains(secondEvents, evt => evt.Type.ToString() == "run.cancelled");
        Assert.NotEmpty(secondEvents);
        AssertRangeMatches(second, secondEvents);

        // The earlier command's range stays bounded to its own session-local causal events.
        AssertRangeMatches(first, firstEvents);
        Assert.DoesNotContain(secondEvents, evt => evt.Sequence >= first.FirstSeq && evt.Sequence <= first.LastSeq);
    }

    [Fact]
    public void Explicit_cancel_reports_range_and_legacy_status_unchanged()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", StartRun(server).Status);
        var run = Assert.IsType<RunId>(server.LastRunId());

        var commandId = Ids.NewV7();
        var ack = Send(server, commandId, RunCommand("run.cancel", run));

        Assert.Equal("ok", ack.Status);
        Assert.Null(ack.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var caused = CausedEvents(server, commandId);
        Assert.Contains(caused, evt => evt.Type.ToString() == "run.cancelled");
        AssertRangeMatches(ack, caused);

        var session = server.LastSessionId()!;
        var journal = server.AcquireStore().ReadFrom(session, 1);
        Assert.Equal(RunState.Cancelled,
            RunProjection.Replay(session, run, EventCodecs.Create(), journal).State);
        Assert.Contains(server.SubscribeSince(0), wire =>
            JsonObj.Parse(wire.PayloadJson).TryGetValue("type", out var type) && type == "run.cancelled");
    }

    [Fact]
    public void Missing_or_terminal_run_is_rejected_without_sequence_range()
    {
        var noSession = OmniHost.CreateInMemoryServer();
        var noSessionAck = Send(noSession, Ids.NewV7(), Payload(JsonObj.Field("cmd", "run.cancel")));
        Assert.Equal("error", noSessionAck.Status);
        Assert.False(string.IsNullOrWhiteSpace(noSessionAck.Error));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, noSessionAck.Outcome?.Kind);
        Assert.Null(noSessionAck.FirstSeq);
        Assert.Null(noSessionAck.LastSeq);

        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", StartRun(server).Status);
        var run = Assert.IsType<RunId>(server.LastRunId());
        Assert.Equal("ok", Send(server, Ids.NewV7(), RunCommand("run.cancel", run)).Status);

        var rejectedId = Ids.NewV7();
        var rejected = Send(server, rejectedId, RunCommand("run.cancel", run));
        Assert.Equal("error", rejected.Status);
        Assert.False(string.IsNullOrWhiteSpace(rejected.Error));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, rejected.Outcome?.Kind);
        var events = CausedEvents(server, rejectedId);
        Assert.Empty(events);
        AssertRangeMatches(rejected, events);
    }
}
