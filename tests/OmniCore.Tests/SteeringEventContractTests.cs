namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Infrastructure;
using Xunit;

public sealed class SteeringEventContractTests
{
    [Fact]
    public void Received_roundtrips_identity_parts_and_origin()
    {
        var codecs = EventCodecs.Create();
        var expected = new TurnSteeringReceived(SteeringId.New(), RunId.New(), LaneId.New(), TurnId.New(),
            "[{\"type\":\"text\",\"text\":\"keep this\\nexactly — 東京\"},{\"type\":\"text\",\"text\":\"second line\"}]",
            "composer\nmanual");

        var actual = Assert.IsType<TurnSteeringReceived>(codecs.Decode(Encode(codecs, expected)));

        Assert.Equal(expected, actual);
        Assert.Equal("turn.steering_received", actual.Type().ToString());
        Assert.Equal(1, actual.SchemaVersion());
    }

    [Fact]
    public void Applied_roundtrips_step_and_correlation_ids()
    {
        var codecs = EventCodecs.Create();
        var expected = new TurnSteeringApplied(SteeringId.New(), RunId.New(), LaneId.New(), TurnId.New(), 17);

        var actual = Assert.IsType<TurnSteeringApplied>(codecs.Decode(Encode(codecs, expected)));

        Assert.Equal(expected, actual);
        Assert.Equal("turn.steering_applied", actual.Type().ToString());
        Assert.Equal(1, actual.SchemaVersion());
    }

    [Fact]
    public void Dropped_roundtrips_nonempty_reason_and_correlation_ids()
    {
        var codecs = EventCodecs.Create();
        var expected = new TurnSteeringDropped(SteeringId.New(), RunId.New(), LaneId.New(), TurnId.New(),
            "turn ended without another model step");

        var actual = Assert.IsType<TurnSteeringDropped>(codecs.Decode(Encode(codecs, expected)));

        Assert.Equal(expected, actual);
        Assert.Equal("turn.steering_dropped", actual.Type().ToString());
        Assert.Equal(1, actual.SchemaVersion());
        Assert.NotEmpty(actual.Reason);
    }

    private static DomainEvent Encode(EventCodecs codecs, DomainEventPayload payload)
    {
        var json = codecs.CodecFor(payload.Type()).Encode(payload);
        return DomainEvent.Create(SessionId.New(), payload.Type(), payload.SchemaVersion(), null,
            null, null, null, null, null, null, null, Array.Empty<ArtifactRef>(), json);
    }
}
