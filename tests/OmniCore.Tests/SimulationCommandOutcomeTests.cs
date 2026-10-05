using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class SimulationCommandOutcomeTests
{
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

    private static string Scenario(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "sim", name + ".yaml");
            if (File.Exists(Path.Combine(dir.FullName, "OmniCore.slnx")) && File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("simulation scenario not found: " + name);
    }

    [Fact]
    public void Successful_and_run_failed_simulations_are_accepted_with_ranges_in_new_sessions()
    {
        var server = OmniHost.CreateInMemoryServer();
        var successId = Ids.NewV7();
        var success = Send(server, successId, Payload(JsonObj.Field("cmd", "sim")));

        Assert.Equal("ok", success.Status);
        Assert.Null(success.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, success.Outcome?.Kind);
        var successSession = Assert.IsType<SessionId>(server.LastSessionId());
        var successEvents = CausedEvents(server, successSession, successId);
        Assert.Contains(successEvents, evt => evt.Type.ToString() == "run.created");
        AssertRangeMatches(success, successEvents);
        Assert.Equal(1, success.FirstSeq);

        // The command is accepted even though the scenario's own expectation produces exit code 1.
        var failYaml = Scenario("multi-item-plan").Replace("run: Completed", "run: Failed",
            StringComparison.Ordinal);
        Assert.NotEqual(Scenario("multi-item-plan"), failYaml);
        var failureId = Ids.NewV7();
        var failure = Send(server, failureId, Payload(JsonObj.Field("cmd", "sim"),
            JsonObj.Field("scenarioYaml", failYaml)));

        Assert.Equal("error", failure.Status);
        Assert.Contains("sim falló:", failure.Error, StringComparison.Ordinal);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, failure.Outcome?.Kind);
        var failureSession = Assert.IsType<SessionId>(server.LastSessionId());
        Assert.NotEqual(successSession, failureSession);
        var failureEvents = CausedEvents(server, failureSession, failureId);
        Assert.Contains(failureEvents, evt => evt.Type.ToString() == "run.created");
        AssertRangeMatches(failure, failureEvents);
        Assert.Equal(1, failure.FirstSeq);
        Assert.Equal(server.AcquireStore().CurrentSequence(failureSession), failure.LastSeq);
        Assert.Contains(server.SubscribeSince(0), wire =>
            JsonObj.Parse(wire.PayloadJson).TryGetValue("exitCode", out var code) && code == "1");
    }

    [Fact]
    public void Malformed_scenario_is_rejected_without_a_journal_range()
    {
        var server = OmniHost.CreateInMemoryServer();
        var commandId = Ids.NewV7();
        var ack = Send(server, commandId, Payload(JsonObj.Field("cmd", "sim"),
            JsonObj.Field("scenarioYaml", "not: [valid")));

        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrWhiteSpace(ack.Error));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
        Assert.Null(server.LastSessionId());
    }

    [Fact]
    public void Resume_without_a_run_is_rejected_without_a_range()
    {
        var server = OmniHost.CreateInMemoryServer();
        var commandId = Ids.NewV7();
        var ack = Send(server, commandId, Payload(JsonObj.Field("cmd", "sim.resume")));

        Assert.Equal("error", ack.Status);
        Assert.Equal("no hay un run previo para reanudar", ack.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
    }

    [Fact]
    public void Resume_reports_only_events_caused_by_its_command_after_the_existing_checkpoint()
    {
        var server = OmniHost.CreateInMemoryServer();
        var simId = Ids.NewV7();
        var sim = Send(server, simId, Payload(JsonObj.Field("cmd", "sim"),
            JsonObj.Field("scenario", "with-tool-crash")));
        Assert.Equal("ok", sim.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, sim.Outcome?.Kind);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var beforeResume = server.AcquireStore().CurrentSequence(session);

        var resumeId = Ids.NewV7();
        var resume = Send(server, resumeId, Payload(JsonObj.Field("cmd", "sim.resume")));

        Assert.Equal("ok", resume.Status);
        Assert.Null(resume.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, resume.Outcome?.Kind);
        var caused = CausedEvents(server, session, resumeId);
        Assert.Contains(caused, evt => evt.Type.ToString() == "toolcall.reconciled");
        AssertRangeMatches(resume, caused);
        Assert.True(resume.FirstSeq > beforeResume);
        Assert.Equal(server.AcquireStore().CurrentSequence(session), resume.LastSeq);
    }
}
