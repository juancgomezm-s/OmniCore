using System.Globalization;
using System.Text.Json;
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

/// <summary>Tests accumulation overflow after a real, durably priced provider invocation.</summary>
public sealed class CurrentMonetaryOverflowRegressionTests
{
    private static readonly decimal HistoricalOverflowCost = decimal.MaxValue - 1m;
    private static readonly decimal HistoricalControlCost = decimal.MaxValue - 3m;

    [Theory]
    [InlineData(PersistedFormat.LegacyModelCompleted, true, StopReason.Cancelled)]
    [InlineData(PersistedFormat.ModelStepCompleted, true, StopReason.Cancelled)]
    [InlineData(PersistedFormat.LegacyModelCompleted, false, StopReason.Error)]
    [InlineData(PersistedFormat.ModelStepCompleted, false, StopReason.Error)]
    public void Current_step_that_overflows_persisted_spend_keeps_usage_and_stops_before_tools(
        PersistedFormat format, bool enforceDefaultCaps, StopReason expectedStop)
    {
        var fixture = OpenFixture();
        try
        {
            var run = TestRun.Open(new EventStream(fixture.Store, fixture.Codecs, fixture.Session),
                fixture.Session, "synthetic prior spend");
            var priorTurn = AppendHistoricalCost(fixture, run, format, HistoricalOverflowCost, 0);
            var sequenceBeforeAsk = fixture.Store.CurrentSequence(fixture.Session);
            var priorEvidence = CaptureHistoricalEvidence(fixture, format, priorTurn);
            var toolExecutor = new CountingExecutor(CreateExecutor(fixture));
            var providerCalls = 0;
            var turn = MakeTurn(fixture, toolExecutor, enforceDefaultCaps, (_, _) =>
            {
                providerCalls++;
                return ToolUseResponse(new TokenUsage(1, 0, 0, 0, 0));
            }, cap: decimal.MaxValue);

            var result = turn.Ask("request", "system", fixture.Session, run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.Equal(expectedStop, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Equal(0, toolExecutor.ExecuteCount);
            var rawEvents = fixture.Store.ReadFrom(fixture.Session, 1);
            var decoded = rawEvents.Select(evt => (Event: evt, Payload: fixture.Codecs.Decode(evt))).ToArray();
            var currentEvents = decoded.Where(item => item.Event.Sequence > sequenceBeforeAsk)
                .Select(item => item.Payload).ToArray();
            var currentCompletion = Assert.Single(currentEvents.OfType<ModelStepCompleted>());
            Assert.Equal(2m, currentCompletion.CostUsd);
            Assert.Equal(new TokenUsage(1, 0, 0, 0, 0), currentCompletion.Usage);
            Assert.Equal(TokenUsageFields.Input | TokenUsageFields.Output, currentCompletion.ReportedUsageFields);
            Assert.Equal(2m, decimal.Parse(ReadCost(fixture.Artifacts, currentCompletion.ResponseArtifact!),
                CultureInfo.InvariantCulture));
            Assert.True(fixture.Artifacts.Verify(currentCompletion.ResponseArtifact!.Hash,
                currentCompletion.ResponseArtifact.Size));
            Assert.DoesNotContain(currentEvents, payload => payload is ToolCallRequested
                or ToolCallStarted or ToolCallEffectUnknown or ToolCallReconciled);

            if (enforceDefaultCaps)
            {
                var budgetRequest = Assert.Single(currentEvents.OfType<InteractionRequested>(), request =>
                    request.Kind == InteractionKind.BudgetExceeded);
                Assert.DoesNotContain("allow_plus", budgetRequest.OptionsJson, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(currentEvents.OfType<TurnAbandoned>(), abandoned => abandoned.Reason.Length > 0);
                Assert.DoesNotContain(currentEvents, payload => payload is InteractionRequested request
                    && request.Kind == InteractionKind.BudgetExceeded);
            }

            AssertHistoricalEvidence(fixture, format, priorTurn, priorEvidence);
        }
        finally
        {
            DeleteRoot(fixture.Root);
        }
    }

    [Theory]
    [InlineData(PersistedFormat.LegacyModelCompleted)]
    [InlineData(PersistedFormat.ModelStepCompleted)]
    public void Representable_near_maximum_history_plus_current_cost_can_complete(
        PersistedFormat format)
    {
        var fixture = OpenFixture();
        try
        {
            var run = TestRun.Open(new EventStream(fixture.Store, fixture.Codecs, fixture.Session),
                fixture.Session, "synthetic prior spend");
            var priorTurn = AppendHistoricalCost(fixture, run, format, HistoricalControlCost, 0);
            var priorEvidence = CaptureHistoricalEvidence(fixture, format, priorTurn);
            var toolExecutor = new CountingExecutor(CreateExecutor(fixture));
            var providerCalls = 0;
            var turn = MakeTurn(fixture, toolExecutor, enforceDefaultCaps: true,
                (_, _) =>
                {
                    providerCalls++;
                    return EndTurnResponse(new TokenUsage(1, 0, 0, 0, 0));
                }, cap: decimal.MaxValue);

            var result = turn.Ask("control", "system", fixture.Session, run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Equal(0, toolExecutor.ExecuteCount);
            AssertHistoricalEvidence(fixture, format, priorTurn, priorEvidence);
        }
        finally
        {
            DeleteRoot(fixture.Root);
        }
    }

    private static TurnId AppendHistoricalCost(Fixture fixture, TestRun.Opened run,
        PersistedFormat format, decimal cost, int index)
    {
        var turnId = TurnId.New();
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var usage = new TokenUsage(1, 0, 0, 0, 0);
        var artifact = fixture.Artifacts.PutText(UsageRecord("historical " + index, run.RunId, day, usage, cost),
            "application/vnd.omnicore.model-usage+json", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        var events = new List<DomainEventPayload> { new TurnStarted(turnId, run.RootLane) };
        if (format == PersistedFormat.LegacyModelCompleted)
        {
            events.Add(new ModelCompleted(turnId, artifact));
        }
        else
        {
            events.Add(new ModelStepStarted(turnId, 0, "synthetic-model", 8192, "Direct",
                null, null, null));
            events.Add(new ModelStepCompleted(turnId, 0, usage, StopReason.EndTurn, artifact,
                day, cost, TokenUsageFields.Input | TokenUsageFields.Output));
        }
        events.Add(new TurnCompleted(turnId));
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask,
            run.RootLane, turnId));
        new EventStream(fixture.Store, fixture.Codecs, fixture.Session)
            .AppendBatch(events, DurabilityClass.Barrier);
        return turnId;
    }

    private static HistoricalEvidence CaptureHistoricalEvidence(Fixture fixture, PersistedFormat format,
        TurnId turnId)
    {
        var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
        if (format == PersistedFormat.LegacyModelCompleted)
        {
            var summary = Assert.Single(events.OfType<ModelCompleted>(), item => item.TurnId == turnId);
            var artifact = Assert.IsType<ArtifactRef>(summary.ResponseArtifact);
            return new HistoricalEvidence(turnId, artifact.Hash.ToString(), ReadCost(fixture.Artifacts, artifact), null, null);
        }

        var step = Assert.Single(events.OfType<ModelStepCompleted>(), item => item.TurnId == turnId);
        var response = Assert.IsType<ArtifactRef>(step.ResponseArtifact);
        return new HistoricalEvidence(turnId, response.Hash.ToString(), ReadCost(fixture.Artifacts, response),
            step.CostUsd, step.Usage);
    }

    private static void AssertHistoricalEvidence(Fixture fixture, PersistedFormat format, TurnId turnId,
        HistoricalEvidence expected)
    {
        var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
        Assert.Equal(expected.TurnId, turnId);
        if (format == PersistedFormat.LegacyModelCompleted)
        {
            var summary = Assert.Single(events.OfType<ModelCompleted>(), item => item.TurnId == turnId);
            var artifact = Assert.IsType<ArtifactRef>(summary.ResponseArtifact);
            Assert.Equal(expected.ArtifactHash, artifact.Hash.ToString());
            Assert.Equal(expected.CostText, ReadCost(fixture.Artifacts, artifact));
            return;
        }

        var step = Assert.Single(events.OfType<ModelStepCompleted>(), item => item.TurnId == turnId);
        var response = Assert.IsType<ArtifactRef>(step.ResponseArtifact);
        Assert.Equal(expected.ArtifactHash, response.Hash.ToString());
        Assert.Equal(expected.CostText, ReadCost(fixture.Artifacts, response));
        Assert.Equal(expected.CostUsd, step.CostUsd);
        Assert.Equal(expected.Usage, step.Usage);
    }

    private static string ReadCost(IArtifactStore artifacts, ArtifactRef artifact)
    {
        var json = artifacts.GetText(artifact.Hash);
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json!);
        return document.RootElement.GetProperty("costUsd").GetString()!;
    }

    private static string UsageRecord(string response, RunId runId, string day, TokenUsage usage, decimal cost) =>
        "{\"omnicoreUsage\":1,\"response\":" + JsonSerializer.Serialize(response)
        + ",\"runId\":" + JsonSerializer.Serialize(runId.ToString())
        + ",\"day\":" + JsonSerializer.Serialize(day)
        + ",\"input\":" + usage.Input.ToString(CultureInfo.InvariantCulture)
        + ",\"output\":" + usage.Output.ToString(CultureInfo.InvariantCulture)
        + ",\"cacheRead\":" + usage.CacheRead.ToString(CultureInfo.InvariantCulture)
        + ",\"cacheWrite\":" + usage.CacheWrite.ToString(CultureInfo.InvariantCulture)
        + ",\"reasoning\":" + usage.Reasoning.ToString(CultureInfo.InvariantCulture)
        + ",\"costUsd\":" + JsonSerializer.Serialize(cost.ToString(CultureInfo.InvariantCulture)) + "}";

    private static IToolExecutor CreateExecutor(Fixture fixture)
    {
        var catalog = OmniHost.CreateExplorerTools().Catalog();
        return ScriptedToolExecutor.WithWorkspace(catalog,
            ScriptedPermissionPolicy.WithTool("filesystem.list", PermissionDecision.Allow), fixture.Root);
    }

    private static ExplorerTurn MakeTurn(Fixture fixture, IToolExecutor tools, bool enforceDefaultCaps,
        Func<ModelRequest, CancellationToken, ModelResponse> complete, decimal cap) => new(complete, tools,
        OmniHost.CreateExplorerTools().Catalog(),
        new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
        new ExecutionFingerprint("synthetic-model", "h", "t", "c", "o", "test-build"),
        new ModelSelection(new ModelIdValue("synthetic-model"), 8192, ToolMode.Direct, null),
        fixture.Store, fixture.Codecs, fixture.Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
        pricing: new ModelPricing(2_000_000m, 1m), enforceDefaultSpendCaps: enforceDefaultCaps,
        sessionCapUsd: cap, dailyCapUsd: cap);

    private static ModelResponse ToolUseResponse(TokenUsage usage) => new(
        new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "provider-call", "filesystem.list", "{\"path\":\".\"}") },
        StopReason.ToolUse, usage, null, new ProviderMetadata("scripted", "synthetic-model", null),
        TokenUsageFields.Input | TokenUsageFields.Output);

    private static ModelResponse EndTurnResponse(TokenUsage usage) => new(
        new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn, usage, null,
        new ProviderMetadata("scripted", "synthetic-model", null),
        TokenUsageFields.Input | TokenUsageFields.Output);

    private static Fixture OpenFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-current-money-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        return new Fixture(root, store, codecs, SessionId.New(), new FileArtifactStore(Path.Combine(root, "blobs")));
    }

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Fixture(string Root, IEventStore Store, IEventCodecRegistry Codecs,
        SessionId Session, FileArtifactStore Artifacts);

    private sealed record HistoricalEvidence(TurnId TurnId, string ArtifactHash, string CostText,
        decimal? CostUsd, TokenUsage? Usage);

    private sealed class CountingExecutor(IToolExecutor inner) : IToolExecutor
    {
        public int ExecuteCount { get; private set; }

        public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
            CancellationToken cancellationToken, EventStream stream)
        {
            ExecuteCount++;
            return inner.ExecuteTool(validated, userApprovesAsk, cancellationToken, stream);
        }
    }

    public enum PersistedFormat { LegacyModelCompleted, ModelStepCompleted }
}
