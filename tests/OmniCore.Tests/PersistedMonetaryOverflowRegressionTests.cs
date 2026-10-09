using System.Globalization;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Contract-valid synthetic accounting evidence; it does not claim provider-authenticated prices.</summary>
public sealed class PersistedMonetaryOverflowRegressionTests
{
    private static readonly decimal OverflowA = decimal.Parse(
        "50000000000000000000000000000", CultureInfo.InvariantCulture);
    private static readonly decimal OverflowB = decimal.Parse(
        "40000000000000000000000000000", CultureInfo.InvariantCulture);
    private static readonly decimal NearLimitA = decimal.Parse(
        "40000000000000000000000000000", CultureInfo.InvariantCulture);
    private static readonly decimal NearLimitB = decimal.Parse(
        "30000000000000000000000000000", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(PersistedFormat.LegacyModelCompleted, true, StopReason.Cancelled)]
    [InlineData(PersistedFormat.ModelStepCompleted, true, StopReason.Cancelled)]
    [InlineData(PersistedFormat.LegacyModelCompleted, false, StopReason.Error)]
    [InlineData(PersistedFormat.ModelStepCompleted, false, StopReason.Error)]
    public void Persisted_cost_sum_overflow_fails_closed_before_another_provider_call(
        PersistedFormat format, bool enforceDefaultCaps, StopReason expectedStop)
    {
        var fixture = OpenFixture();
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(fixture.Journal);
            var codecs = EventCodecs.Create();
            var run = TestRun.Open(store, fixture.Session, "synthetic prior spend");
            var historicalTurns = new[]
            {
                AppendCost(store, codecs, fixture.Artifacts, run, format, OverflowA, turnOrdinal: 0),
                AppendCost(store, codecs, fixture.Artifacts, run, format, OverflowB, turnOrdinal: 1),
            };
            var historicalSequence = store.CurrentSequence(fixture.Session);
            var historicalEvents = store.ReadFrom(fixture.Session, 1).ToArray();
            store.Close();
            store = new SqliteEventStore(fixture.Journal);

            var calls = 0;
            var turn = MakeTurn(store, codecs, fixture.Artifacts, fixture.Root, enforceDefaultCaps,
                () => calls++);
            var result = turn.Ask("new request", "system", fixture.Session, run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.Equal(expectedStop, result.StopReason);
            Assert.Equal(0, calls);
            var decoded = store.ReadFrom(fixture.Session, 1).Select(codecs.Decode).ToArray();
            var historicalAfter = store.ReadFrom(fixture.Session, 1).Take((int)historicalSequence).ToArray();
            Assert.Equal(historicalEvents.Length, historicalAfter.Length);
            for (var index = 0; index < historicalEvents.Length; index++)
                AssertEnvelopeUnchanged(historicalEvents[index], historicalAfter[index]);

            if (enforceDefaultCaps)
            {
                var request = Assert.Single(decoded.OfType<InteractionRequested>(), request =>
                    request.Kind == InteractionKind.BudgetExceeded);
                Assert.DoesNotContain("allow_plus", request.OptionsJson, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(decoded.OfType<TurnAbandoned>(), abandoned =>
                    abandoned.Reason.Length > 0);
                Assert.DoesNotContain(decoded.OfType<InteractionRequested>(), request =>
                    request.Kind == InteractionKind.BudgetExceeded);
            }

            AssertHistoricalEvidenceUnchanged(decoded, fixture.Artifacts, format, historicalTurns,
                new[] { OverflowA, OverflowB });
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + fixture.Journal);
            SqliteConnection.ClearPool(connection);
            DeleteRoot(fixture.Root);
        }
    }

    [Theory]
    [InlineData(PersistedFormat.LegacyModelCompleted)]
    [InlineData(PersistedFormat.ModelStepCompleted)]
    public void Representable_near_limit_history_remains_readable_and_does_not_block(
        PersistedFormat format)
    {
        var fixture = OpenFixture();
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(fixture.Journal);
            var codecs = EventCodecs.Create();
            var run = TestRun.Open(store, fixture.Session, "synthetic prior spend");
            var historicalTurns = new[]
            {
                AppendCost(store, codecs, fixture.Artifacts, run, format, NearLimitA, turnOrdinal: 0),
                AppendCost(store, codecs, fixture.Artifacts, run, format, NearLimitB, turnOrdinal: 1),
            };
            store.Close();
            store = new SqliteEventStore(fixture.Journal);

            var calls = 0;
            var turn = MakeTurn(store, codecs, fixture.Artifacts, fixture.Root,
                enforceDefaultCaps: true, providerCalled: () => calls++, cap: decimal.MaxValue);
            var result = turn.Ask("near limit control", "system", fixture.Session,
                run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(1, calls);
            AssertHistoricalEvidenceUnchanged(store.ReadFrom(fixture.Session, 1)
                .Select(codecs.Decode).ToArray(), fixture.Artifacts, format, historicalTurns,
                new[] { NearLimitA, NearLimitB });
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + fixture.Journal);
            SqliteConnection.ClearPool(connection);
            DeleteRoot(fixture.Root);
        }
    }

    [Theory]
    [InlineData(PersistedFormat.LegacyModelCompleted)]
    [InlineData(PersistedFormat.ModelStepCompleted)]
    public void Daily_cost_overflow_across_sessions_blocks_only_the_requesting_session(PersistedFormat format)
    {
        var fixture = OpenFixture();
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(fixture.Journal);
            var codecs = EventCodecs.Create();
            var first = TestRun.Open(store, fixture.Session, "synthetic first session spend");
            var firstTurn = AppendCost(store, codecs, fixture.Artifacts, first, format, OverflowA, 0);
            var secondSession = SessionId.New();
            var second = TestRun.Open(store, secondSession, "synthetic second session spend");
            var secondTurn = AppendCost(store, codecs, fixture.Artifacts, second, format, OverflowB, 1);
            var original = store.ReadFrom(fixture.Session, 1).ToArray();
            var originalSequence = store.CurrentSequence(fixture.Session);
            store.Close();
            store = new SqliteEventStore(fixture.Journal);
            var calls = 0;
            var result = MakeTurn(store, codecs, fixture.Artifacts, fixture.Root, true,
                () => calls++, decimal.MaxValue).Ask("new session request", "system", secondSession,
                    second.RunId, second.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Equal(0, calls);
            Assert.Equal(originalSequence, store.CurrentSequence(fixture.Session));
            var originalAfter = store.ReadFrom(fixture.Session, 1).ToArray();
            Assert.Equal(original.Length, originalAfter.Length);
            for (var index = 0; index < original.Length; index++)
                AssertEnvelopeUnchanged(original[index], originalAfter[index]);
            var currentEvents = store.ReadFrom(secondSession, 1).ToArray();
            var request = Assert.Single(currentEvents.Select(codecs.Decode).OfType<InteractionRequested>(),
                request => request.Kind == InteractionKind.BudgetExceeded);
            Assert.DoesNotContain("allow_plus", request.OptionsJson, StringComparison.Ordinal);
            Assert.All(currentEvents, evt => Assert.Equal(secondSession, evt.SessionId));
            AssertHistoricalEvidenceUnchanged(originalAfter.Concat(currentEvents).Select(codecs.Decode).ToArray(),
                fixture.Artifacts, format, new[] { firstTurn, secondTurn }, new[] { OverflowA, OverflowB });
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + fixture.Journal);
            SqliteConnection.ClearPool(connection);
            DeleteRoot(fixture.Root);
        }
    }

    private static TurnId AppendCost(IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        TestRun.Opened run, PersistedFormat format, decimal cost, int turnOrdinal)
    {
        var stream = new EventStream(store, codecs, run.SessionId);
        var turnId = TurnId.New();
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var response = "synthetic prior response " + turnOrdinal;
        var usage = new TokenUsage(1, 0, 0, 0, 0);
        var record = UsageRecord(response, run.RunId, day, usage, cost);
        var artifact = artifacts.PutText(record, "application/vnd.omnicore.model-usage+json",
            ArtifactKind.ModelResponse, Sensitivity.Sensitive);

        using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask,
            run.RootLane, turnId));
        var events = new List<DomainEventPayload> { new TurnStarted(turnId, run.RootLane) };
        if (format == PersistedFormat.LegacyModelCompleted)
        {
            events.Add(new ModelCompleted(turnId, artifact));
        }
        else
        {
            events.Add(new ModelStepStarted(turnId, 0, "synthetic-model", 8192, "Direct",
                null, null, null));
            events.Add(new ModelStepCompleted(turnId, 0, usage, StopReason.EndTurn,
                artifact, day, cost, TokenUsageFields.Input | TokenUsageFields.Output));
        }
        events.Add(new TurnCompleted(turnId));
        stream.AppendBatch(events, DurabilityClass.Barrier);
        return turnId;
    }

    private static void AssertHistoricalEvidenceUnchanged(DomainEventPayload[] events, IArtifactStore artifacts,
        PersistedFormat format, TurnId[] historicalTurns, decimal[] expectedCosts)
    {
        if (format == PersistedFormat.LegacyModelCompleted)
        {
            var summaries = events.OfType<ModelCompleted>()
                .Where(summary => historicalTurns.Contains(summary.TurnId)).ToArray();
            Assert.Equal(expectedCosts.Length, summaries.Length);
            Assert.Equal(expectedCosts.Select(value => value.ToString(CultureInfo.InvariantCulture)),
                summaries.Select(summary => ReadArtifactCost(artifacts, summary.ResponseArtifact!)));
        }
        else
        {
            var steps = events.OfType<ModelStepCompleted>()
                .Where(step => historicalTurns.Contains(step.TurnId)).ToArray();
            Assert.Equal(expectedCosts.Length, steps.Length);
            Assert.Equal(expectedCosts, steps.Select(step => step.CostUsd!.Value));
            Assert.All(steps, step => Assert.Equal(new TokenUsage(1, 0, 0, 0, 0), step.Usage));
            for (var index = 0; index < steps.Length; index++)
                Assert.Equal(expectedCosts[index], decimal.Parse(
                    ReadArtifactCost(artifacts, steps[index].ResponseArtifact!), CultureInfo.InvariantCulture));
        }
    }

    private static void AssertEnvelopeUnchanged(DomainEvent before, DomainEvent after)
    {
        // DomainEvent has reference equality. Compare every durable envelope field after reopen.
        Assert.Equal(before.EventId, after.EventId);
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.Type, after.Type);
        Assert.Equal(before.SchemaVersion, after.SchemaVersion);
        Assert.Equal(before.Timestamp, after.Timestamp);
        Assert.Equal(before.Causation, after.Causation);
        Assert.Equal(before.CorrelationId, after.CorrelationId);
        Assert.Equal(before.RunId, after.RunId);
        Assert.Equal(before.TaskId, after.TaskId);
        Assert.Equal(before.LaneId, after.LaneId);
        Assert.Equal(before.TurnId, after.TurnId);
        Assert.Equal(before.PlanItemId, after.PlanItemId);
        Assert.Equal(before.ToolCallId, after.ToolCallId);
        Assert.Equal(before.ExecutionId, after.ExecutionId);
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(before.PayloadJson, after.PayloadJson);
        Assert.Equal(before.ArtifactRefs.Select(reference => System.Text.Json.JsonSerializer.Serialize(reference)),
            after.ArtifactRefs.Select(reference => System.Text.Json.JsonSerializer.Serialize(reference)));
    }

    private static string ReadArtifactCost(IArtifactStore artifacts, ArtifactRef artifact)
    {
        var text = artifacts.GetText(artifact.Hash);
        Assert.NotNull(text);
        using var document = System.Text.Json.JsonDocument.Parse(text!);
        return document.RootElement.GetProperty("costUsd").GetString()!;
    }

    private static string UsageRecord(string response, RunId run, string day, TokenUsage usage, decimal cost) =>
        "{\"omnicoreUsage\":1,\"response\":" + System.Text.Json.JsonSerializer.Serialize(response)
        + ",\"runId\":" + System.Text.Json.JsonSerializer.Serialize(run.ToString())
        + ",\"day\":" + System.Text.Json.JsonSerializer.Serialize(day)
        + ",\"input\":" + usage.Input.ToString(CultureInfo.InvariantCulture)
        + ",\"output\":" + usage.Output.ToString(CultureInfo.InvariantCulture)
        + ",\"cacheRead\":" + usage.CacheRead.ToString(CultureInfo.InvariantCulture)
        + ",\"cacheWrite\":" + usage.CacheWrite.ToString(CultureInfo.InvariantCulture)
        + ",\"reasoning\":" + usage.Reasoning.ToString(CultureInfo.InvariantCulture)
        + ",\"costUsd\":" + System.Text.Json.JsonSerializer.Serialize(cost.ToString(CultureInfo.InvariantCulture)) + "}";

    private static ExplorerTurn MakeTurn(IEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts, string workspace, bool enforceDefaultCaps, Action providerCalled,
        decimal cap = 5m)
    {
        var catalog = OmniHost.CreateExplorerTools().Catalog();
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), workspace);
        return new ExplorerTurn((_, _) =>
        {
            providerCalled();
            return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                new TokenUsage(1, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "synthetic-model", null));
        }, executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("synthetic-model", "h", "t", "c", "o", "test-build"),
            new ModelSelection(new ModelIdValue("synthetic-model"), 8192, ToolMode.Direct, null),
            store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: enforceDefaultCaps,
            sessionCapUsd: cap, dailyCapUsd: cap,
            maximumGenerationRequestAttempts: 1); // scripted provider callback is a single response
    }

    private static (string Root, string Journal, SessionId Session, FileArtifactStore Artifacts) OpenFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-persisted-money-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (root, Path.Combine(root, "journal.db"), SessionId.New(),
            new FileArtifactStore(Path.Combine(root, "blobs")));
    }

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public enum PersistedFormat { LegacyModelCompleted, ModelStepCompleted }
}
