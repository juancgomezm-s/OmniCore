using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Journal/codec fixtures, not authenticated consumption or account queries.</summary>
public sealed class ConversationRetryCoverageTests
{
    [Theory]
    [InlineData("primary", 1L, 3L, true)]
    [InlineData("primary", 0L, 1L, true)]
    [InlineData("primary", 0L, 3L, false)]
    [InlineData("primary", 2L, 3L, false)]
    [InlineData("primary", 1L, null, false)]
    [InlineData("meta", 1L, 3L, true)]
    [InlineData("meta", 0L, 1L, true)]
    [InlineData("meta", 0L, 3L, false)]
    [InlineData("meta", 2L, 3L, false)]
    [InlineData("meta", 1L, null, false)]
    [InlineData("meta_failed", 1L, 3L, true)]
    [InlineData("meta_failed", 0L, 1L, true)]
    [InlineData("meta_failed", 0L, 3L, false)]
    [InlineData("meta_failed", 2L, 3L, false)]
    [InlineData("meta_failed", 1L, null, false)]
    public void Final_response_does_not_mask_unknown_prior_attempts(string source, long sends, long? bound, bool known)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-retry-coverage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new FileArtifactStore(root);
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = RunId.New();
            var turn = TurnId.New();
            var attempts = new GenerationRequestAttemptEvidence(sends, bound);
            var usage = new TokenUsage(17, 4, 0, 0, 0);
            void Append(DomainEventPayload payload, SessionId owner)
            {
                store.Append(owner, DomainEvent.Create(owner, payload.Type(), payload.SchemaVersion(), null,
                    run, run, null, null, turn, null, null, [], codecs.CodecFor(payload.Type()).Encode(payload)),
                    DurabilityClass.Barrier, TestContext.Current.CancellationToken);
            }
            if (source == "primary")
            {
                Append(new ModelStepStarted(turn, 0, "fixture", 100, "Direct", null, null, null), session);
                var terminal = new ModelStepCompleted(turn, 0, usage, StopReason.EndTurn, null, "2026-10-07",
                    0.000021m, TokenUsageFields.Input | TokenUsageFields.Output, attempts);
                Append(terminal, session);
                Append(terminal, session); // duplicate snapshots/receipts cannot duplicate usage
                Append(new ModelCompleted(turn, null), session); // compatibility summary is not another call
            }
            else
            {
                var input = artifacts.PutText("fixture input", "text/plain", ArtifactKind.Other, Sensitivity.Normal);
                var output = artifacts.PutText("fixture output", "text/plain", ArtifactKind.ModelResponse, Sensitivity.Normal);
                Append(new MetaModelInvocationStarted("meta", run, "compact", "fixture", input), session);
                DomainEventPayload terminal = source == "meta"
                    ? new MetaModelInvocationCompleted("meta", run, "compact", "fixture", output, usage, 0.000021m,
                        TokenUsageFields.Input | TokenUsageFields.Output, attempts)
                    : new MetaModelInvocationFailed("meta", run, "compact", "fixture", "fixture-post-response-fault", usage,
                        0.000021m, TokenUsageFields.Input | TokenUsageFields.Output, attempts);
                Append(terminal, session);
                Append(terminal, session);
            }
            Append(new ModelStepCompleted(TurnId.New(), 0, new TokenUsage(1000, 1000, 0, 0, 0),
                StopReason.EndTurn, null, "2026-10-07", 1m, TokenUsageFields.All,
                new GenerationRequestAttemptEvidence(1, 1)), SessionId.New());

            var measurement = SessionUsageReporter.ReadConversation(store, codecs, artifacts, session);
            Assert.Equal(session.ToString(), measurement.SessionId);
            Assert.Equal(1, measurement.ModelInvocations);
            Assert.Equal(known ? 0 : 1, measurement.IncompleteInvocations);
            Assert.Equal(known ? MetricAvailability.Reported : MetricAvailability.Unknown, measurement.Total.Availability);
            Assert.Equal(known ? 21L : (long?)null, measurement.Total.Value);
            Assert.Equal(known ? MetricAvailability.Estimated : MetricAvailability.Unknown, measurement.Cost.Availability);
            Assert.Equal(known ? new Money(0.000021m, "USD") : null, measurement.Cost.Value);
            Assert.Equal(measurement, SessionUsageReporter.ReadConversation(store, codecs, artifacts, session));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
