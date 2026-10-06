namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;
using Xunit;

/// <summary>Black-box replay guards for explicitly synthetic legacy journal rows (no trusted cost proof).</summary>
public sealed class PersistedUsageReplayRegressionTests
{
    private const string UnsafeUsageReason = "Persisted token usage cannot be represented safely.";

    [Theory]
    [InlineData("negative")]
    [InlineData("overflow")]
    public void Unsafe_usage_on_open_legacy_turn_abandons_before_provider_without_writing_new_model_evidence(
        string scenario)
    {
        var fixture = OpenFixture();
        try
        {
            var turnId = TurnId.New();
            var usage = scenario == "negative"
                ? new[] { new TokenUsage(-1, 0, 0, 0, 0) }
                : new[] { new TokenUsage(long.MaxValue, 0, 0, 0, 0), new TokenUsage(1, 0, 0, 0, 0) };
            AppendLegacyOpenTurn(fixture, turnId, usage);
            var before = fixture.Store.ReadFrom(fixture.Session, 1);
            var beforeLastSequence = before[^1].Sequence;
            var beforeStepStarts = before.Select(fixture.Codecs.Decode).OfType<ModelStepStarted>()
                .Where(step => step.TurnId == turnId).ToArray();
            var providerCalls = 0;
            var turn = MakeTurn(fixture, (_, _) =>
            {
                providerCalls++;
                return EndTurnResponse();
            });

            var result = turn.Ask("resume", "system", fixture.Session, fixture.Run.RunId,
                fixture.Run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(0, providerCalls);
            var after = fixture.Store.ReadFrom(fixture.Session, 1);
            Assert.True(after[^1].Sequence > beforeLastSequence);
            var payloads = after.Select(fixture.Codecs.Decode).ToArray();
            var abandoned = Assert.Single(payloads.OfType<TurnAbandoned>(), item => item.TurnId == turnId);
            Assert.Contains(UnsafeUsageReason, abandoned.Reason, StringComparison.Ordinal);
            Assert.Equal(beforeStepStarts, payloads.OfType<ModelStepStarted>()
                .Where(step => step.TurnId == turnId).ToArray());
            Assert.DoesNotContain(payloads, payload => payload is ModelCompleted completed && completed.TurnId == turnId);
            Assert.DoesNotContain(payloads, payload => payload is TurnCompleted completed && completed.TurnId == turnId);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Fact]
    public void Unsafe_usage_in_a_different_closed_turn_does_not_block_new_turn()
    {
        var fixture = OpenFixture();
        try
        {
            var closedTurn = TurnId.New();
            AppendLegacyClosedTurn(fixture, closedTurn, new TokenUsage(-1, 0, 0, 0, 0));
            var providerCalls = 0;
            var turn = MakeTurn(fixture, (_, _) =>
            {
                providerCalls++;
                return EndTurnResponse();
            });

            var result = turn.Ask("new turn", "system", fixture.Session, fixture.Run.RunId,
                fixture.Run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(1, providerCalls);
            var payloads = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            Assert.Contains(payloads, payload => payload is TurnAbandoned abandoned
                && abandoned.TurnId == closedTurn);
            Assert.DoesNotContain(payloads, payload => payload is TurnAbandoned abandoned
                && abandoned.Reason.Contains(UnsafeUsageReason, StringComparison.Ordinal)
                && abandoned.TurnId != closedTurn);
            Assert.Contains(payloads, payload => payload is TurnCompleted completed
                && completed.TurnId != closedTurn);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    private sealed record Fixture(IEventStore Store, IEventCodecRegistry Codecs, SessionId Session,
        TestRun.Opened Run, string TempRoot)
    {
        public void DeleteArtifacts()
        {
            try { Directory.Delete(TempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Fixture OpenFixture()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "omnicore-persisted-usage-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(new EventStream(store, codecs, session), session);
        return new Fixture(store, codecs, session, run, tempRoot);
    }

    private static void AppendLegacyOpenTurn(Fixture fixture, TurnId turnId, IReadOnlyList<TokenUsage> usages)
    {
        var stream = new EventStream(fixture.Store, fixture.Codecs, fixture.Session);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId, fixture.Run.RootTask,
            fixture.Run.RootLane, turnId));
        var events = LegacyStepEvents(turnId, fixture.Run.RootLane, usages).ToList();
        stream.AppendBatch(events, DurabilityClass.Standard);
    }

    private static void AppendLegacyClosedTurn(Fixture fixture, TurnId turnId, TokenUsage usage)
    {
        var stream = new EventStream(fixture.Store, fixture.Codecs, fixture.Session);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId, fixture.Run.RootTask,
            fixture.Run.RootLane, turnId));
        var events = LegacyStepEvents(turnId, fixture.Run.RootLane, new[] { usage }).ToList();
        events.Add(new TurnAbandoned(turnId, "synthetic legacy closed turn"));
        stream.AppendBatch(events, DurabilityClass.Standard);
    }

    private static IReadOnlyList<DomainEventPayload> LegacyStepEvents(TurnId turnId, LaneId laneId,
        IReadOnlyList<TokenUsage> usages)
    {
        var events = new List<DomainEventPayload> { new TurnStarted(turnId, laneId) };
        for (var index = 0; index < usages.Count; index++)
        {
            // Synthetic pre-fingerprint journal evidence: no authenticated pricing/artifact claim.
            events.Add(new ModelStepStarted(turnId, index, "legacy-model", 8192, "Direct",
                null, null, null));
            events.Add(new ModelStepCompleted(turnId, index, usages[index], StopReason.ToolUse,
                null, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                null, null));
        }
        return events;
    }

    private static ExplorerTurn MakeTurn(Fixture fixture,
        Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(complete,
        new ScriptedToolExecutor(), new FakeCatalog(),
        new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
        new ExecutionFingerprint("legacy-model", "h", "t", "context", "o", "test-build"),
        new ModelSelection(new ModelIdValue("legacy-model"), 8192, ToolMode.Direct, null),
        fixture.Store, fixture.Codecs, new FileArtifactStore(Path.Combine(fixture.TempRoot, "artifacts")),
        new InMemoryAuditSink(), new RedactionPolicy(), enforceDefaultSpendCaps: false);

    private static ModelResponse EndTurnResponse() => new(new ContentBlock[] { new TextBlock("done") },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
        new ProviderMetadata("scripted", "legacy-model", null));

    private sealed class ScriptedToolExecutor : IToolExecutor
    {
        public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
            CancellationToken cancellationToken, EventStream stream) =>
            throw new InvalidOperationException("No tools are expected in this replay test.");
    }
}
