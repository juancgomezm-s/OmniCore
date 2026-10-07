using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class GenerationRequestAttemptEvidenceTests
{
    [Theory]
    [InlineData(1L, 3L, true)]
    [InlineData(0L, 1L, true)]
    [InlineData(2L, 3L, false)]
    [InlineData(0L, 3L, false)]
    [InlineData(1L, null, false)]
    [InlineData(0L, null, false)]
    public void Coverage_requires_a_known_single_attempt_ceiling_or_one_observed_send(
        long observed, long? maximum, bool complete)
    {
        var evidence = new GenerationRequestAttemptEvidence(observed, maximum);

        Assert.True(evidence.IsValid);
        Assert.Equal(complete, evidence.HasCompleteUsageCoverage);
    }

    [Theory]
    [InlineData(-1L, 1L)]
    [InlineData(0L, 0L)]
    [InlineData(2L, 1L)]
    public void Invalid_counts_or_bounds_are_rejected_by_canonical_replay(long observed, long? maximum)
    {
        var evidence = new GenerationRequestAttemptEvidence(observed, maximum);
        var tracker = new OmniCore.Engine.CanonicalStateTracker();

        Assert.False(evidence.IsValid);
        Assert.False(evidence.HasCompleteUsageCoverage);
        Assert.Throws<InvalidStateTransitionException>(() => tracker.Apply(new ModelStepCompleted(
            TurnId.New(), 0, new TokenUsage(1, 1, 0, 0, 0), StopReason.EndTurn,
            null, "2026-10-07", null, TokenUsageFields.Input | TokenUsageFields.Output, evidence)));
        Assert.Throws<InvalidStateTransitionException>(() => tracker.Apply(new MetaModelInvocationFailed(
            "fixture", RunId.New(), "fixture", "fixture-fingerprint", "fixture-failure",
            null, null, TokenUsageFields.None, evidence)));
    }

    [Fact]
    public void Completed_and_meta_terminal_v3_round_trip_attempt_evidence_while_v2_remains_legacy_null()
    {
        var codecs = EventCodecs.Create();
        var attempts = new GenerationRequestAttemptEvidence(2, 3);
        DomainEventPayload[] current =
        [
            new ModelStepCompleted(TurnId.New(), 0, new TokenUsage(17, 4, 0, 0, 0), StopReason.EndTurn,
                null, "2026-10-07", null, TokenUsageFields.Input | TokenUsageFields.Output, attempts),
            new MetaModelInvocationCompleted("meta-complete", RunId.New(), "fixture", "fingerprint",
                Reference(), new TokenUsage(17, 4, 0, 0, 0), null,
                TokenUsageFields.Input | TokenUsageFields.Output, attempts),
            new MetaModelInvocationFailed("meta-failed", RunId.New(), "fixture", "fingerprint", "fixture-error",
                new TokenUsage(17, 4, 0, 0, 0), null, TokenUsageFields.Input | TokenUsageFields.Output, attempts),
        ];

        Assert.Equal(3, codecs.CurrentVersion(EventType.Of("model_step.completed")));
        Assert.Equal(3, codecs.CurrentVersion(EventType.Of("meta_model.invocation_completed")));
        Assert.Equal(3, codecs.CurrentVersion(EventType.Of("meta_model.invocation_failed")));

        foreach (var payload in current)
        {
            var currentEvent = Envelope(payload, codecs, payload.SchemaVersion());
            var decoded = codecs.Decode(currentEvent);
            var decodedAttempts = decoded switch
            {
                ModelStepCompleted step => step.GenerationAttempts,
                MetaModelInvocationCompleted completed => completed.GenerationAttempts,
                MetaModelInvocationFailed failed => failed.GenerationAttempts,
                _ => throw new Xunit.Sdk.XunitException("Unexpected terminal payload type."),
            };
            Assert.Equal(attempts, decodedAttempts);

            var legacyPayload = JsonObjectWithoutAttempts(codecs.CodecFor(payload.Type()).Encode(payload));
            var legacyEvent = DomainEvent.Create(currentEvent.SessionId, currentEvent.Type, 2, null,
                null, null, null, null, null, null, null, [], legacyPayload);
            var legacyDecoded = codecs.Decode(legacyEvent);
            var legacyAttempts = legacyDecoded switch
            {
                ModelStepCompleted step => step.GenerationAttempts,
                MetaModelInvocationCompleted completed => completed.GenerationAttempts,
                MetaModelInvocationFailed failed => failed.GenerationAttempts,
                _ => throw new Xunit.Sdk.XunitException("Unexpected legacy terminal payload type."),
            };
            Assert.Null(legacyAttempts);
        }
    }

    private static string JsonObjectWithoutAttempts(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("GenerationAttempts");
        return node.ToJsonString();
    }

    private static ArtifactRef Reference() => new(ArtifactId.New(),
        ContentHash.Sha256(new string('a', 64)), 1, "text/plain", ArtifactKind.ModelResponse,
        Sensitivity.Sensitive, Redacted: true);

    private static DomainEvent Envelope(DomainEventPayload payload, EventCodecs codecs, int? version = null) =>
        DomainEvent.Create(SessionId.New(), payload.Type(), version ?? payload.SchemaVersion(), null, null, null,
            null, null, null, null, null, [], codecs.CodecFor(payload.Type()).Encode(payload));
}
