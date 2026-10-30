namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Host;

public sealed class RuntimeCalibrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-runtime-calibration-" + Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken None = CancellationToken.None;
    private static ModelQualificationKey Key(string model = "model") => ModelQualificationKey.For("local", model, ToolCallFormat.PromptedJson, ToolMode.Direct);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Collector_counts_only_typed_canonical_failures_and_later_step_recoveries_for_the_same_model()
    {
        var session = SessionId.New(); var turn = TurnId.New(); var lane = LaneId.New(); var run = RunId.New();
        var bad = ToolCallId.New(); var repaired = ToolCallId.New(); var other = ToolCallId.New();
        DomainEventPayload[] payloads = [
            new ModelStepStarted(turn,0,"model",8192,"Direct",null,null,null,RouteId:new RouteId("route-a")),
            new ToolCallRequested(bad,"provider-1","filesystem.patch","{}"),
            new ToolCallRejected(bad,"schema",ToolErrorCode.InvalidArguments),
            new ModelStepCompleted(turn,0,new TokenUsage(1,1,0,0,0),StopReason.ToolUse,null,"2026-10-30",null),
            new ModelStepStarted(turn,1,"model",8192,"Direct",null,null,null,RouteId:new RouteId("route-a")),
            new ToolCallRequested(repaired,"provider-2","filesystem.patch","{}"),
            new ToolCallPrepared(repaired,"{}"), new ToolCallSucceeded(repaired,"{}"),
            new ProgressStalled(PlanItemId.New(),3,DateTimeOffset.UtcNow),
            new RunValidationRejected(run,["tests"],["tests"]),
            new ModelStepStarted(turn,2,"other",8192,"Direct",null,null,null),
            new ToolCallRequested(other,"provider-3","filesystem.patch","{}"),
            new ToolCallRejected(other,"schema",ToolErrorCode.InvalidArguments)];
        var events = payloads.Select((p, index) => (DomainEvent.Stored(EventId.New(), session, index + 1, p.Type(), p.SchemaVersion(),
            DateTimeOffset.UtcNow, null, run, run, null, lane, turn, null, null, [], "{}"), p)).ToArray();
        var observed = RuntimeCalibrationObserver.Collect(events, "model", 1);
        Assert.Equal(observed.Count, RuntimeCalibrationObserver.Collect(events, "model", 1, new RouteId("route-a")).Count);
        Assert.Empty(RuntimeCalibrationObserver.Collect(events, "model", 1, new RouteId("route-b")));
        Assert.Equal(2, observed.Count(o => o.Counter == "tool_calls"));
        foreach (var name in new[] { "model_steps", "invalid_tool_arguments", "valid_tool_arguments", "argument_repairs", "progress_stalls", "completion_gate_rejections" })
            Assert.Single(observed, o => o.Counter == name);
        // Resuming after the first receipt keeps earlier failures as context but never recounts them.
        var resumed = RuntimeCalibrationObserver.Collect(events, "model", 5);
        Assert.DoesNotContain(resumed, o => o.Counter == "invalid_tool_arguments");
        Assert.Single(resumed, o => o.Counter == "argument_repairs");
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Full_and_calibration_resources_are_complete(string locale)
    {
        var text = new OmniCore.Client.Localization(locale);
        foreach (var key in OmniCore.Client.FullQualificationResources.Keys)
            Assert.False(text.ResolveDetailed(key).UsedFallback, key);
    }

    [Fact]
    public void Canonical_receipts_deduplicate_across_reopening_and_never_replace_benchmark_traits()
    {
        var key = Key(); var hash = key.QualificationKeyHash();
        var observations = Enumerable.Range(0, 20).SelectMany(_ => new[]{
            new RuntimeCalibrationObservation(Guid.NewGuid().ToString(),"model_steps"),
            new RuntimeCalibrationObservation(Guid.NewGuid().ToString(),"valid_tool_arguments")}).ToArray();
        using (var store = new SqliteModelQualificationStore(Path.Combine(_root, "user.db")))
        {
            store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified, "full", "1.0.0",
                [new(hash, 1, "InstructionFollowing", .8, .7, 10, "empirical:quick")], None);
            store.RecordRuntimeCalibration(key, observations, None);
        }
        using (var store = new SqliteModelQualificationStore(Path.Combine(_root, "user.db")))
        {
            store.RecordRuntimeCalibration(key, observations, None);
            Assert.Equal(ModelQualificationState.Calibrated, store.Get(key, None)!.State);
            var traits = store.Traits(key, 1, None);
            Assert.Equal(.8, Assert.Single(traits, t => t.Trait == "InstructionFollowing").Value);
            var validity = Assert.Single(traits, t => t.Trait == "ToolArgumentValidity");
            Assert.Equal(20, validity.Samples); Assert.Equal(1, validity.Value);
            Assert.DoesNotContain(traits, t => t.Trait is "ToolCallingReliability" or "FileMutationReliability");
        }
    }

    [Fact]
    public void Failures_and_repairs_are_measured_without_cross_key_credit_or_invented_qualification()
    {
        using var store = new SqliteModelQualificationStore(Path.Combine(_root, "user.db"));
        var key = Key(); var other = Key("other");
        RuntimeCalibrationObservation[] observations = [
            new(Guid.NewGuid().ToString(),"valid_tool_arguments"),
            new(Guid.NewGuid().ToString(),"invalid_tool_arguments"),
            new(Guid.NewGuid().ToString(),"argument_repairs")];
        store.RecordRuntimeCalibration(key, observations, None);
        Assert.Null(store.Get(key, None));
        store.Upsert(key, 0, ModelQualificationState.Qualified, "full", "1.0.0", None);
        store.Upsert(other, 0, ModelQualificationState.Qualified, "full", "1.0.0", None);
        store.RecordRuntimeCalibration(key, observations, None);
        store.RecordRuntimeCalibration(other, observations, None);
        var traits = store.Traits(key, 1, None);
        Assert.Equal(.5, Assert.Single(traits, t => t.Trait == "ToolArgumentValidity").Value);
        Assert.Equal(1, Assert.Single(traits, t => t.Trait == "ArgumentRepairSuccess").Value);
        Assert.Empty(store.Traits(other, 1, None));
        Assert.Equal(ModelQualificationState.Qualified, store.Get(key, None)!.State);
    }
}