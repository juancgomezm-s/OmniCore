using System.Text.Json.Nodes;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class EscalationLineageContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Escalation_v2_roundtrips_lineage_and_legacy_v1_keeps_fields_without_inventing_ids(int kind)
    {
        var run = RunId.New();
        var lane = LaneId.New();
        var turn = TurnId.New();
        var codecs = EventCodecs.Create();
        var payload = Payload(kind, run, turn, lane);
        var codec = codecs.CodecFor(payload.Type());
        Assert.Equal(2, payload.SchemaVersion());
        Assert.Equal(2, codecs.CurrentVersion(payload.Type()));
        var json = codec.Encode(payload);
        Assert.Equal(payload, codecs.Decode(Envelope(payload, run, 2, json)));

        var legacyJson = JsonNode.Parse(codec.Encode(Payload(kind, run, null, null)))!.AsObject();
        Assert.True(legacyJson.Remove("TurnId"));
        Assert.True(legacyJson.Remove("LaneId"));
        var legacy = codecs.Decode(Envelope(payload, run, 1, legacyJson.ToJsonString()));
        Assert.Equal(Payload(kind, run, null, null), legacy);
        // Explicit nulls from a v2 writer also remain null, rather than being assigned new IDs.
        var nullable = Payload(kind, run, null, null);
        Assert.Equal(nullable, codecs.Decode(Envelope(nullable, run, 2, codec.Encode(nullable))));
    }

    [Fact]
    public void Escalation_payload_lineage_wins_over_ambient_scope_without_changing_canonical_state()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var codecs = EventCodecs.Create();
        var stream = new EventStream(store, codecs, session);
        var run = TestRun.Open(stream, session);
        var before = stream.LiveState().Snapshot();
        var explicitTurn = TurnId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId.New(), LaneId: LaneId.New(),
                   TurnId: TurnId.New())))
        {
            stream.AppendBatch(Enumerable.Range(0, 3)
                .Select(kind => Payload(kind, run.RunId, explicitTurn, run.RootLane)).ToArray(),
                DurabilityClass.Standard);
        }

        var persisted = store.ReadFrom(session, 1).Where(evt =>
            evt.Type.ToString().StartsWith("model.escalation_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, persisted.Length);
        Assert.All(persisted, evt =>
        {
            Assert.Equal(run.RunId, evt.RunId);
            Assert.Equal(run.RunId, evt.CorrelationId);
            Assert.Equal(run.RootLane, evt.LaneId);
            Assert.Equal(explicitTurn, evt.TurnId);
            Assert.Equal(2, evt.SchemaVersion);
        });
        Assert.Equal(before, stream.LiveState().Snapshot());
        Assert.Equal(before, CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1)).Snapshot());
    }

    private static DomainEventPayload Payload(int kind, RunId run, TurnId? turn, LaneId? lane) => kind switch
    {
        0 => new ModelEscalationRequested(run, "source", "target", EscalationCause.ContextLimit, turn, lane),
        1 => new ModelEscalationApproved(run, "target", "policy:test", turn, lane),
        2 => new ModelEscalationCompleted(run, "target", turn, lane),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DomainEvent Envelope(DomainEventPayload payload, RunId run, int version, string json) =>
        DomainEvent.Create(SessionId.New(), payload.Type(), version, null, run, run, null, null, null,
            null, null, Array.Empty<ArtifactRef>(), json);
}
