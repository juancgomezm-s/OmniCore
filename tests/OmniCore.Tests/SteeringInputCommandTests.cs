using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class SteeringInputCommandTests
{
    [Fact]
    public void Explicit_steering_is_durable_scoped_and_idempotent_without_new_intent()
    {
        var fixture = new Fixture();
        var commandId = Ids.NewV7();
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var command = fixture.Input(commandId, "steering", "retain the original intention");
        var ack = fixture.Server.Send(command, TestContext.Current.CancellationToken);
        Assert.Equal("ok", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var evt = Assert.Single(fixture.Store.ReadFrom(fixture.Session, before + 1));
        var received = Assert.IsType<TurnSteeringReceived>(fixture.Codecs.Decode(evt));
        Assert.Equal(new SteeringId(Guid.Parse(commandId)), received.SteeringId);
        Assert.Equal(fixture.Run, received.RunId);
        Assert.Equal(fixture.Lane, received.LaneId);
        Assert.Equal(fixture.Turn, received.TurnId);
        Assert.Equal(fixture.Task, evt.TaskId);
        Assert.Equal(fixture.Run, evt.RunId);
        Assert.Equal(fixture.Lane, evt.LaneId);
        Assert.Equal(fixture.Turn, evt.TurnId);
        Assert.Equal(Guid.Parse(commandId), Assert.IsType<CommandCausation>(evt.Causation).CommandId.Value);
        Assert.Equal(evt.Sequence, ack.FirstSeq);
        Assert.Equal(evt.Sequence, ack.LastSeq);
        var replay = fixture.Server.Send(command, TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, replay.Outcome?.Kind);
        Assert.Null(replay.FirstSeq);
        Assert.Null(replay.LastSeq);
        Assert.Equal(before + 1, fixture.Store.CurrentSequence(fixture.Session));
        var conflict = fixture.Server.Send(fixture.Input(commandId, "steering", "different intent"),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, conflict.Outcome?.Kind);
        Assert.Equal(before + 1, fixture.Store.CurrentSequence(fixture.Session));
    }

    [Fact]
    public void Undeclared_input_remains_followup()
    {
        var fixture = new Fixture();
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var ack = fixture.Server.Send(fixture.Input(Ids.NewV7(), null, "next intention"),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var payload = Assert.IsType<FollowUpQueued>(fixture.Codecs.Decode(
            Assert.Single(fixture.Store.ReadFrom(fixture.Session, before + 1))));
        Assert.Equal(fixture.Turn, payload.TurnId);
    }

    [Theory]
    [InlineData("run.cancel")]
    [InlineData("run.interrupt")]
    public void Explicit_control_drops_queued_steering_in_the_same_command_batch(string operation)
    {
        var fixture = new Fixture();
        var inputId = Ids.NewV7();
        var accepted = fixture.Server.Send(fixture.Input(inputId, "steering", "pending direction"),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, accepted.Outcome?.Kind);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var id = Ids.NewV7();
        var ack = fixture.Server.Send(WireEnvelope.Command(id, "{" + JsonObj.Field("cmd", operation)
            + "," + JsonObj.Field("runId", fixture.Run.ToString()) + "}"), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var written = fixture.Store.ReadFrom(fixture.Session, before + 1);
        var dropped = Assert.Single(written, evt => fixture.Codecs.Decode(evt) is TurnSteeringDropped);
        Assert.Equal(new SteeringId(Guid.Parse(inputId)), Assert.IsType<TurnSteeringDropped>(fixture.Codecs.Decode(dropped)).SteeringId);
        Assert.Equal(Guid.Parse(id), Assert.IsType<CommandCausation>(dropped.Causation).CommandId.Value);
        Assert.Equal(written.Min(evt => evt.Sequence), ack.FirstSeq);
        Assert.Equal(written.Max(evt => evt.Sequence), ack.LastSeq);
        Assert.Empty(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session, fixture.Run, fixture.Lane, fixture.Turn));
    }

    [Fact]
    public void Denied_budget_drops_steering_with_original_task_and_command_attribution()
    {
        var fixture = new Fixture();
        var inputId = Ids.NewV7();
        var accepted = fixture.Server.Send(fixture.Input(inputId, "steering", "pending budget direction"),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, accepted.Outcome?.Kind);
        var interaction = InteractionId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(fixture.Run, fixture.Task, fixture.Lane, fixture.Turn)))
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(new InteractionRequested(
                interaction, InteractionKind.BudgetExceeded, "{}", "[{\"id\":\"deny\"}]", "deny",
                null, fixture.Lane, fixture.Task, null, 0, 1));
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var id = Ids.NewV7();
        var ack = fixture.Server.Send(WireEnvelope.Command(id, "{" + JsonObj.Field("cmd", "interaction.respond")
            + "," + JsonObj.Field("interactionId", interaction.ToString()) + "," + JsonObj.Field("optionId", "deny")
            + "}"), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var dropped = Assert.Single(fixture.Store.ReadFrom(fixture.Session, before + 1),
            evt => fixture.Codecs.Decode(evt) is TurnSteeringDropped);
        Assert.Equal(fixture.Task, dropped.TaskId);
        Assert.Equal(fixture.Run, dropped.RunId);
        Assert.Equal(Guid.Parse(id), Assert.IsType<CommandCausation>(dropped.Causation).CommandId.Value);
        Assert.Equal(RunState.Failed, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).State);
        Assert.Empty(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session, fixture.Run, fixture.Lane, fixture.Turn));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("run")]
    [InlineData("lane")]
    [InlineData("turn")]
    [InlineData("kind")]
    [InlineData("terminal")]
    public void Invalid_target_or_kind_is_rejected_without_fallback_to_followup(string mismatch)
    {
        var fixture = new Fixture();
        if (mismatch == "terminal")
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(new TurnCompleted(fixture.Turn));
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var ack = fixture.Server.Send(fixture.Input(Ids.NewV7(), mismatch == "kind" ? "typo" : "steering",
            "do not retarget", mismatch), TestContext.Current.CancellationToken);
        Assert.Equal("error", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
    }

    private sealed class Fixture
    {
        public readonly InMemoryEventStore Store = new();
        public readonly EventCodecs Codecs = EventCodecs.Create();
        public readonly OmniServer Server;
        public readonly SessionId Session;
        public readonly RunId Run;
        public readonly TaskId Task;
        public readonly LaneId Lane;
        public readonly TurnId Turn = TurnId.New();

        public Fixture()
        {
            Server = new OmniServer(Store, Codecs, new InMemoryAuditSink());
            var ack = Server.Send(WireEnvelope.Command(Ids.NewV7(), "{"
                + JsonObj.Field("cmd", "session.input") + "," + JsonObj.Field("text", "initial objective") + "}"),
                TestContext.Current.CancellationToken);
            Assert.Equal("ok", ack.Status);
            Session = Assert.IsType<SessionId>(Server.LastSessionId());
            Run = Assert.IsType<RunId>(Server.LastRunId());
            Task = RunProjection.Replay(Session, Run, Codecs, Store.ReadFrom(Session, 1)).RootTask!;
            Lane = Assert.Single(LaneProjection.Replay(Codecs, Store.ReadFrom(Session, 1)).ForTask(Task)).Id;
            new EventStream(Store, Codecs, Session).Append(new TurnStarted(Turn, Lane));
        }

        public WireEnvelope Input(string id, string? kind, string text, string? mismatch = null)
        {
            var fields = new List<string> { JsonObj.Field("cmd", "session.input"), JsonObj.Field("text", text),
                JsonObj.Field("sessionId", (mismatch == "session" ? SessionId.New() : Session).ToString()),
                JsonObj.Field("runId", (mismatch == "run" ? RunId.New() : Run).ToString()),
                JsonObj.Field("laneId", (mismatch == "lane" ? LaneId.New() : Lane).ToString()),
                JsonObj.Field("turnId", (mismatch == "turn" ? TurnId.New() : Turn).ToString()) };
            if (kind is not null) fields.Add(JsonObj.Field("kind", kind));
            return WireEnvelope.Command(id, "{" + string.Join(",", fields) + "}");
        }
    }
}
