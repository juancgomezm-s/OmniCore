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

public sealed class MetaModelSpendRegressionTests
{
    private const decimal RunCap = 0.50m;
    private static readonly TokenUsage OverCapUsage = new(300_000, 300_000, 0, 0, 0);
    private static readonly TokenUsage BelowCapUsage = new(50_000, 50_000, 0, 0, 0);
    private static readonly ModelPricing Pricing = new(1m, 1m);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Historical_meta_usage_over_run_cap_stops_before_another_meta_or_model_call(bool failed)
    {
        using var fixture = new Fixture(RunCap);
        var historical = fixture.AppendHistoricalMeta(failed, OverCapUsage);
        fixture.AppendOldConversation();
        var calls = new ScriptedMetaProvider("would compact", BelowCapUsage);
        var primaryCalls = 0;
        var turn = fixture.MakeTurn(calls, (_, _) =>
        {
            primaryCalls++;
            return EndTurn(new TokenUsage(0, 0, 0, 0, 0));
        });
        var beforeAsk = fixture.Store.CurrentSequence(fixture.Session);

        var result = turn.Ask("continue", "system", fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, "", CancellationToken.None);

        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(0, calls.Calls);
        Assert.Equal(0, primaryCalls);
        var appended = fixture.Store.ReadFrom(fixture.Session, beforeAsk + 1)
            .Select(evt => (Event: evt, Payload: fixture.Codecs.Decode(evt))).ToArray();
        Assert.DoesNotContain(appended, pair => pair.Payload is MetaModelInvocationStarted
            or ContextCheckpointRecorded or TurnStarted or ModelStepStarted);
        var budgetRequest = Assert.Single(appended, pair => pair.Payload is InteractionRequested request
            && request.Kind == InteractionKind.BudgetExceeded);
        Assert.Equal(fixture.Run.RunId, budgetRequest.Event.RunId);
        Assert.Equal(fixture.Run.RootTask, budgetRequest.Event.TaskId);
        Assert.Equal(fixture.Run.RootLane, budgetRequest.Event.LaneId);
        fixture.AssertHistoricalMeta(historical, failed, OverCapUsage, 0.60m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_compaction_usage_over_run_cap_stops_before_primary_model_call(bool failAfterUsage)
    {
        using var fixture = new Fixture(RunCap);
        fixture.AppendOldConversation();
        var meta = new ScriptedMetaProvider(failAfterUsage ? "" : "older facts preserved", OverCapUsage);
        var primaryCalls = 0;
        var turn = fixture.MakeTurn(meta, (_, _) =>
        {
            primaryCalls++;
            return EndTurn(new TokenUsage(0, 0, 0, 0, 0));
        });
        var beforeAsk = fixture.Store.CurrentSequence(fixture.Session);

        var result = turn.Ask("continue", "system", fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, "", CancellationToken.None);

        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(1, meta.Calls);
        Assert.Equal(0, primaryCalls);
        var decoded = fixture.Store.ReadFrom(fixture.Session, beforeAsk + 1)
            .Select(evt => (Event: evt, Payload: fixture.Codecs.Decode(evt))).ToArray();
        var started = Assert.Single(decoded, pair => pair.Payload is MetaModelInvocationStarted);
        var startPayload = Assert.IsType<MetaModelInvocationStarted>(started.Payload);
        Assert.Equal(fixture.Run.RunId, startPayload.RunId);
        Assert.Equal(fixture.Run.RunId, started.Event.RunId);
        Assert.Equal("CompressContext", startPayload.Operation);
        Assert.True(fixture.Artifacts.Verify(startPayload.InputArtifact.Hash, startPayload.InputArtifact.Size));
        Assert.Contains(started.Event.ArtifactRefs, reference => reference.Id == startPayload.InputArtifact.Id);

        if (failAfterUsage)
        {
            var failed = Assert.Single(decoded, pair => pair.Payload is MetaModelInvocationFailed);
            var payload = Assert.IsType<MetaModelInvocationFailed>(failed.Payload);
            Assert.Equal(startPayload.InvocationId, payload.InvocationId);
            Assert.Equal(OverCapUsage, payload.Usage);
            Assert.Equal(0.60m, payload.CostUsd);
            Assert.Equal(TokenUsageFields.All, payload.ReportedUsageFields);
            Assert.Equal(fixture.Run.RunId, failed.Event.RunId);
        }
        else
        {
            var completed = Assert.Single(decoded, pair => pair.Payload is MetaModelInvocationCompleted);
            var payload = Assert.IsType<MetaModelInvocationCompleted>(completed.Payload);
            Assert.Equal(startPayload.InvocationId, payload.InvocationId);
            Assert.Equal(OverCapUsage, payload.Usage);
            Assert.Equal(0.60m, payload.CostUsd);
            Assert.Equal(TokenUsageFields.All, payload.ReportedUsageFields);
            Assert.Equal(fixture.Run.RunId, completed.Event.RunId);
            Assert.True(fixture.Artifacts.Verify(payload.OutputArtifact.Hash, payload.OutputArtifact.Size));
            Assert.Contains(completed.Event.ArtifactRefs, reference => reference.Id == payload.OutputArtifact.Id);
        }

        Assert.DoesNotContain(decoded, pair => pair.Payload is ModelStepStarted);
        var budgetRequest = Assert.Single(decoded, pair => pair.Payload is InteractionRequested request
            && request.Kind == InteractionKind.BudgetExceeded);
        Assert.Equal(fixture.Run.RunId, budgetRequest.Event.RunId);
        Assert.Equal(fixture.Run.RootTask, budgetRequest.Event.TaskId);
        Assert.Equal(fixture.Run.RootLane, budgetRequest.Event.LaneId);
    }

    [Fact]
    public void Current_compaction_usage_below_run_cap_allows_the_primary_model_call()
    {
        using var fixture = new Fixture(RunCap);
        fixture.AppendOldConversation();
        var meta = new ScriptedMetaProvider("older facts preserved", BelowCapUsage);
        var primaryCalls = 0;
        var turn = fixture.MakeTurn(meta, (_, _) =>
        {
            primaryCalls++;
            return EndTurn(new TokenUsage(0, 0, 0, 0, 0));
        });

        var result = turn.Ask("continue", "system", fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, "", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal(1, meta.Calls);
        Assert.Equal(1, primaryCalls);
        var metaCompleted = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(BelowCapUsage, metaCompleted.Usage);
        Assert.Equal(0.10m, metaCompleted.CostUsd);
        Assert.Equal(TokenUsageFields.All, metaCompleted.ReportedUsageFields);
        Assert.True(fixture.Artifacts.Verify(metaCompleted.OutputArtifact.Hash, metaCompleted.OutputArtifact.Size));
        Assert.DoesNotContain(fixture.Payloads().OfType<InteractionRequested>(), request =>
            request.Kind == InteractionKind.BudgetExceeded);
        Assert.Single(fixture.Payloads().OfType<ModelStepCompleted>());
    }

    [Fact]
    public void Completed_and_failed_outcomes_for_one_meta_invocation_are_not_double_counted_across_asks()
    {
        using var fixture = new Fixture(RunCap);
        var usage = new TokenUsage(150_000, 150_000, 0, 0, 0);
        var history = fixture.AppendHistoricalCompletedAndFailed(usage, 0.30m);
        var meta = new ScriptedMetaProvider("must not compact", new TokenUsage(0, 0, 0, 0, 0));
        var primaryCalls = 0;
        var turn = fixture.MakeTurn(meta, (_, _) =>
        {
            primaryCalls++;
            return EndTurn(new TokenUsage(25_000, 25_000, 0, 0, 0));
        }, compact: false);

        var first = turn.Ask("first", "system", fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, "", CancellationToken.None);
        var second = turn.Ask("second", "system", fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, "", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, first.StopReason);
        Assert.Equal(StopReason.EndTurn, second.StopReason);
        Assert.Equal(2, primaryCalls);
        Assert.Equal(0, meta.Calls);
        Assert.Equal(2, fixture.Payloads().OfType<ModelStepCompleted>().Count());
        Assert.DoesNotContain(fixture.Payloads().OfType<InteractionRequested>(), request =>
            request.Kind == InteractionKind.BudgetExceeded);
        Assert.True(fixture.Artifacts.Verify(history.Started.InputArtifact.Hash, history.Started.InputArtifact.Size));
        Assert.Contains(history.StartedEvent.ArtifactRefs,
            reference => reference.Id == history.Started.InputArtifact.Id);
        Assert.Equal(usage, history.Completed.Usage);
        Assert.Equal(usage, history.Failed.Usage);
        Assert.Equal(0.30m, history.Completed.CostUsd);
        Assert.Equal(0.30m, history.Failed.CostUsd);
        Assert.Equal(TokenUsageFields.All, history.Completed.ReportedUsageFields);
        Assert.Equal(TokenUsageFields.All, history.Failed.ReportedUsageFields);
        Assert.True(fixture.Artifacts.Verify(history.Completed.OutputArtifact.Hash, history.Completed.OutputArtifact.Size));
        Assert.Contains(history.CompletedEvent.ArtifactRefs,
            reference => reference.Id == history.Completed.OutputArtifact.Id);
    }

    [Theory]
    [InlineData(IncompleteMetaEvidence.StartedWithoutOutcome)]
    [InlineData(IncompleteMetaEvidence.CompletedWithoutCost)]
    [InlineData(IncompleteMetaEvidence.CompletedWithoutUsageFields)]
    public void Incomplete_historical_meta_evidence_fails_closed_before_any_provider_call(
        IncompleteMetaEvidence evidence)
    {
        using var fixture = new Fixture(RunCap);
        var seeded = fixture.AppendIncompleteMeta(evidence);
        var meta = new ScriptedMetaProvider("must not compact", new TokenUsage(0, 0, 0, 0, 0));
        var primaryCalls = 0;
        var turn = fixture.MakeTurn(meta, (_, _) =>
        {
            primaryCalls++;
            return EndTurn(new TokenUsage(0, 0, 0, 0, 0));
        }, compact: false);
        var beforeAsk = fixture.Store.CurrentSequence(fixture.Session);

        var result = turn.Ask("request", "system", fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, "", CancellationToken.None);

        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(0, meta.Calls);
        Assert.Equal(0, primaryCalls);
        var appended = fixture.Store.ReadFrom(fixture.Session, beforeAsk + 1)
            .Select(evt => (Event: evt, Payload: fixture.Codecs.Decode(evt))).ToArray();
        var budgetRequest = Assert.Single(appended, pair => pair.Payload is InteractionRequested request
            && request.Kind == InteractionKind.BudgetExceeded);
        Assert.Equal(fixture.Run.RunId, budgetRequest.Event.RunId);
        Assert.Equal(fixture.Run.RootTask, budgetRequest.Event.TaskId);
        Assert.Equal(fixture.Run.RootLane, budgetRequest.Event.LaneId);
        Assert.DoesNotContain(appended, pair => pair.Payload is MetaModelInvocationStarted
            or ModelStepStarted);
        Assert.True(fixture.Artifacts.Verify(seeded.Started.InputArtifact.Hash, seeded.Started.InputArtifact.Size));
        Assert.Contains(seeded.StartedEvent.ArtifactRefs,
            reference => reference.Id == seeded.Started.InputArtifact.Id);
    }

    [Fact]
    public void Meta_and_primary_step_costs_are_combined_before_tool_execution()
    {
        using var fixture = new Fixture(RunCap);
        fixture.AppendOldConversation();
        var meta = new ScriptedMetaProvider("older facts preserved", new TokenUsage(150_000, 150_000, 0, 0, 0));
        var primaryCalls = 0;
        var toolExecutor = new CountingNoTools();
        var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.inspect"));
        var turn = fixture.MakeTurn(meta, (_, _) =>
        {
            primaryCalls++;
            return new ModelResponse(new ContentBlock[]
            {
                new ToolCallBlock(ToolCallId.New(), "fixture-call", "fixture.inspect", "{}"),
            }, StopReason.ToolUse, new TokenUsage(125_000, 125_000, 0, 0, 0), null,
                new ProviderMetadata("scripted-primary", "fixture-model", null), TokenUsageFields.All);
        }, catalog: catalog, executor: toolExecutor);

        var result = turn.Ask("continue", "system", fixture.Session, fixture.Run.RunId,
            fixture.Run.RootLane, "", CancellationToken.None);

        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(1, meta.Calls);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(0, toolExecutor.Calls);
        var payloads = fixture.Payloads();
        var metaCompleted = Assert.Single(payloads.OfType<MetaModelInvocationCompleted>());
        var step = Assert.Single(payloads.OfType<ModelStepCompleted>());
        Assert.Equal(0.30m, metaCompleted.CostUsd);
        Assert.Equal(0.25m, step.CostUsd);
        Assert.Equal(new TokenUsage(125_000, 125_000, 0, 0, 0), step.Usage);
        Assert.Equal(TokenUsageFields.All, step.ReportedUsageFields);
        Assert.DoesNotContain(payloads, payload => payload is ToolCallRequested or ToolCallStarted
            or ToolCallEffectUnknown or ToolCallSucceeded or ToolCallFailed or ToolCallRejected);
        var budgetRequest = Assert.Single(payloads.OfType<InteractionRequested>(), request =>
            request.Kind == InteractionKind.BudgetExceeded);
        var eventEnvelope = fixture.Store.ReadFrom(fixture.Session, 1).Single(evt =>
            fixture.Codecs.Decode(evt) is InteractionRequested request
                && request.InteractionId == budgetRequest.InteractionId);
        Assert.Equal(fixture.Run.RunId, eventEnvelope.RunId);
        Assert.Equal(fixture.Run.RootLane, eventEnvelope.LaneId);
    }

    private static ModelResponse EndTurn(TokenUsage usage) => new(
        new ContentBlock[] { new TextBlock("primary complete") }, StopReason.EndTurn, usage, null,
        new ProviderMetadata("scripted-primary", "fixture-model", null));

    private sealed class Fixture : IDisposable
    {
        private const string MetaFingerprint = "historical-meta-fingerprint";
        private readonly EventStream _stream;
        private readonly string _root;

        public InMemoryEventStore Store { get; } = new();
        public IEventCodecRegistry Codecs { get; } = EventCodecs.Create();
        public SessionId Session { get; } = SessionId.New();
        public TestRun.Opened Run { get; }
        public FileArtifactStore Artifacts { get; }

        public Fixture(decimal cap)
        {
            _root = Path.Combine(Path.GetTempPath(), "omni-meta-spend-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Artifacts = new FileArtifactStore(_root);
            _stream = new EventStream(Store, Codecs, Session);
            var runId = RunId.New();
            var taskId = TaskId.New();
            var laneId = LaneId.New();
            var budget = new TaskBudget(cap, null, null, null);
            _stream.AppendBatch(new DomainEventPayload[]
            {
                new RunCreated(runId, Session, "meta spend fixture", RunMode.Plan,
                    ExecutionStrategy.Direct, FailurePolicy.BlockDependents, budget, taskId, DateTimeOffset.UtcNow),
                new RunStarted(runId),
                new TaskCreated(taskId, runId, "meta spend fixture", Array.Empty<TaskDependency>(), budget),
                new TaskReady(taskId),
                new LaneCreated(laneId, taskId, ProfileId.New()),
                new LaneStarted(laneId),
                new TaskStarted(taskId, laneId),
            }, DurabilityClass.Standard);
            Run = new TestRun.Opened(Session, runId, taskId, laneId);
        }

        public void AppendOldConversation()
        {
            _stream.Append(new UserInputReceived(Run.RunId, "[\"older user content\"]", null, "fixture"));
            _stream.Append(new UserInputReceived(Run.RunId, "[\"second older user content\"]", null, "fixture"));
        }

        public (MetaModelInvocationStarted Started, DomainEvent StartedEvent, DomainEvent Outcome) AppendHistoricalMeta(
            bool failed, TokenUsage usage)
        {
            const string invocationId = "fixture-meta-invocation";
            var input = Artifacts.PutText("historical compaction input", "text/plain", ArtifactKind.Other,
                Sensitivity.Sensitive);
            var started = new MetaModelInvocationStarted(invocationId, Run.RunId, "CompressContext",
                MetaFingerprint, input);
            DomainEventPayload outcome;
            if (failed)
            {
                outcome = new MetaModelInvocationFailed(invocationId, Run.RunId, "CompressContext",
                    MetaFingerprint, "scripted-failure-after-usage", usage, 0.60m, TokenUsageFields.All);
            }
            else
            {
                var output = Artifacts.PutText("historical compacted summary", "text/plain",
                    ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                outcome = new MetaModelInvocationCompleted(invocationId, Run.RunId, "CompressContext",
                    MetaFingerprint, output, usage, 0.60m, TokenUsageFields.All);
            }
            _stream.AppendBatch(new DomainEventPayload[] { started, outcome }, DurabilityClass.Barrier);
            var events = Store.ReadFrom(Session, 1);
            return (started,
                events.Single(evt => Codecs.Decode(evt) is MetaModelInvocationStarted),
                events.Single(evt => Codecs.Decode(evt) is MetaModelInvocationCompleted or MetaModelInvocationFailed));
        }

        public HistoricalMetaPair AppendHistoricalCompletedAndFailed(TokenUsage usage, decimal cost)
        {
            const string invocationId = "duplicate-meta-invocation";
            var input = Artifacts.PutText("same invocation input", "text/plain", ArtifactKind.Other,
                Sensitivity.Sensitive);
            var output = Artifacts.PutText("same invocation output", "text/plain", ArtifactKind.ModelResponse,
                Sensitivity.Sensitive);
            var started = new MetaModelInvocationStarted(invocationId, Run.RunId, "CompressContext",
                MetaFingerprint, input);
            var completed = new MetaModelInvocationCompleted(invocationId, Run.RunId, "CompressContext",
                MetaFingerprint, output, usage, cost, TokenUsageFields.All);
            var failed = new MetaModelInvocationFailed(invocationId, Run.RunId, "CompressContext",
                MetaFingerprint, "duplicate-terminal-evidence", usage, cost, TokenUsageFields.All);
            _stream.AppendBatch(new DomainEventPayload[] { started, completed, failed }, DurabilityClass.Barrier);
            var events = Store.ReadFrom(Session, 1);
            return new HistoricalMetaPair(started,
                events.Single(evt => Codecs.Decode(evt) is MetaModelInvocationStarted),
                Assert.IsType<MetaModelInvocationCompleted>(completed),
                events.Single(evt => Codecs.Decode(evt) is MetaModelInvocationCompleted),
                Assert.IsType<MetaModelInvocationFailed>(failed),
                events.Single(evt => Codecs.Decode(evt) is MetaModelInvocationFailed));
        }

        public (MetaModelInvocationStarted Started, DomainEvent StartedEvent) AppendIncompleteMeta(
            IncompleteMetaEvidence evidence)
        {
            var invocationId = "incomplete-meta-" + evidence;
            var input = Artifacts.PutText("incomplete invocation input", "text/plain", ArtifactKind.Other,
                Sensitivity.Sensitive);
            var started = new MetaModelInvocationStarted(invocationId, Run.RunId, "CompressContext",
                MetaFingerprint, input);
            _stream.Append(started, DurabilityClass.Barrier);
            var startedEvent = Store.ReadFrom(Session, 1).Single(evt =>
                Codecs.Decode(evt) is MetaModelInvocationStarted payload
                    && payload.InvocationId == invocationId);
            if (evidence != IncompleteMetaEvidence.StartedWithoutOutcome)
            {
                var output = Artifacts.PutText("incomplete invocation output", "text/plain",
                    ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                var usage = new TokenUsage(150_000, 150_000, 0, 0, 0);
                decimal? cost = evidence == IncompleteMetaEvidence.CompletedWithoutCost ? null : 0.30m;
                var fields = evidence == IncompleteMetaEvidence.CompletedWithoutUsageFields
                    ? TokenUsageFields.None : TokenUsageFields.All;
                _stream.Append(new MetaModelInvocationCompleted(invocationId, Run.RunId,
                    "CompressContext", MetaFingerprint, output, usage, cost, fields), DurabilityClass.Barrier);
            }
            return (started, startedEvent);
        }

        public void AssertHistoricalMeta((MetaModelInvocationStarted Started, DomainEvent StartedEvent,
            DomainEvent Outcome) history,
            bool failed, TokenUsage usage, decimal cost)
        {
            Assert.True(Artifacts.Verify(history.Started.InputArtifact.Hash, history.Started.InputArtifact.Size));
            Assert.Equal(Run.RunId, history.Started.RunId);
            Assert.Contains(history.StartedEvent.ArtifactRefs,
                reference => reference.Id == history.Started.InputArtifact.Id);
            Assert.Equal(Run.RunId, history.StartedEvent.RunId);
            if (failed)
            {
                var actual = Assert.IsType<MetaModelInvocationFailed>(Codecs.Decode(history.Outcome));
                Assert.Equal(history.Started.InvocationId, actual.InvocationId);
                Assert.Equal(usage, actual.Usage);
                Assert.Equal(cost, actual.CostUsd);
                Assert.Equal(TokenUsageFields.All, actual.ReportedUsageFields);
                Assert.Equal(Run.RunId, history.Outcome.RunId);
            }
            else
            {
                var actual = Assert.IsType<MetaModelInvocationCompleted>(Codecs.Decode(history.Outcome));
                Assert.Equal(history.Started.InvocationId, actual.InvocationId);
                Assert.Equal(usage, actual.Usage);
                Assert.Equal(cost, actual.CostUsd);
                Assert.Equal(TokenUsageFields.All, actual.ReportedUsageFields);
                Assert.Equal(Run.RunId, history.Outcome.RunId);
                Assert.True(Artifacts.Verify(actual.OutputArtifact.Hash, actual.OutputArtifact.Size));
                Assert.Contains(history.Outcome.ArtifactRefs,
                    reference => reference.Id == actual.OutputArtifact.Id);
            }
        }

        public ExplorerTurn MakeTurn(IModelProvider metaProvider,
            Func<ModelRequest, CancellationToken, ModelResponse> primary, bool compact = true,
            FakeCatalog? catalog = null, IToolExecutor? executor = null)
        {
            catalog ??= new FakeCatalog();
            var selection = new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null);
            var policy = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off,
                0, PlanControl.Assisted, 4,
                new ContextManagementPolicy(4096, 1000, 0, compact ? 1 : 1000, 3000));
            return new ExplorerTurn(primary,
                executor ?? new NoTools(), catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("fixture-model", "h", "t", "c", "o", "fixture-build"),
                selection, Store, Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy(), policy,
                pricing: Pricing, metaModelProvider: metaProvider, recordEffectiveFingerprint: true);
        }

        public DomainEventPayload[] Payloads() => Store.ReadFrom(Session, 1).Select(Codecs.Decode).ToArray();

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class ScriptedMetaProvider(string summary, TokenUsage usage) : IModelProvider
    {
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public int Calls { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await System.Threading.Tasks.Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ResponseCompleted(new ModelResponse(
                new ContentBlock[] { new TextBlock(summary) }, StopReason.EndTurn, usage, null,
                new ProviderMetadata("scripted-meta", "fixture-model", null), TokenUsageFields.All));
        }
    }

    private sealed class NoTools : IToolExecutor
    {
        public ToolOutcome ExecuteTool(ValidatedToolCall call, bool approve, CancellationToken token,
            EventStream stream) => throw new InvalidOperationException("Fixture must not execute tools.");
    }

    private sealed class CountingNoTools : IToolExecutor
    {
        public int Calls { get; private set; }
        public ToolOutcome ExecuteTool(ValidatedToolCall call, bool approve, CancellationToken token,
            EventStream stream)
        {
            Calls++;
            throw new InvalidOperationException("Budget guard must stop before tool execution.");
        }
    }

    public enum IncompleteMetaEvidence { StartedWithoutOutcome, CompletedWithoutCost, CompletedWithoutUsageFields }

    private sealed record HistoricalMetaPair(MetaModelInvocationStarted Started, DomainEvent StartedEvent,
        MetaModelInvocationCompleted Completed, DomainEvent CompletedEvent,
        MetaModelInvocationFailed Failed, DomainEvent FailedEvent);
}
