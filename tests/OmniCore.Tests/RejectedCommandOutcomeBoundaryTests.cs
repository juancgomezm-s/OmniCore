using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class RejectedCommandOutcomeBoundaryTests
{
    [Theory]
    [InlineData("unknown-command", "{\"cmd\":\"command.not_registered\"}")]
    [InlineData("missing-command", "{}")]
    [InlineData("malformed-json", "{")]
    [InlineData("json-array", "[]")]
    [InlineData("json-scalar", "\"not an object\"")]
    public void Invalid_command_payload_returns_correlated_rejected_ack_without_events(string _, string payload)
    {
        var (server, store, session) = ServerWithExistingSession();
        var commandId = Ids.NewV7();
        var sequenceBefore = store.CurrentSequence(session);

        var ack = server.Send(WireEnvelope.Command(commandId, payload), CancellationToken.None);

        AssertRejectedAck(ack, commandId);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
        Assert.Equal(sequenceBefore, store.CurrentSequence(session));
        Assert.Empty(server.AcquireStore().ReadFrom(session, sequenceBefore + 1));
    }

    [Fact]
    public void Non_command_message_with_valid_uuid_returns_correlated_rejected_ack_without_events()
    {
        var (server, store, session) = ServerWithExistingSession();
        var messageId = Ids.NewV7();
        var sequenceBefore = store.CurrentSequence(session);

        var ack = server.Send(WireEnvelope.Event(messageId, "{}"), CancellationToken.None);

        AssertRejectedAck(ack, messageId);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
        Assert.Equal(sequenceBefore, store.CurrentSequence(session));
        Assert.Empty(store.ReadFrom(session, sequenceBefore + 1));
    }

    private static void AssertRejectedAck(CommandAck ack, string messageId)
    {
        Assert.Equal(messageId, ack.CommandId);
        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrWhiteSpace(ack.Error));
        Assert.NotNull(ack.Outcome);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome!.Kind);
    }

    private static (OmniServer Server, InMemoryEventStore Store, SessionId Session) ServerWithExistingSession()
    {
        var server = OmniHost.CreateInMemoryServer();
        var startId = Ids.NewV7();
        var start = server.Send(WireEnvelope.Command(startId,
            "{\"cmd\":\"session.input\",\"text\":\"rejection-boundary setup\"}"), CancellationToken.None);
        Assert.Equal("ok", start.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, start.Outcome?.Kind);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var store = Assert.IsType<InMemoryEventStore>(server.AcquireStore());
        Assert.True(store.CurrentSequence(session) > 0);
        return (server, store, session);
    }
}
