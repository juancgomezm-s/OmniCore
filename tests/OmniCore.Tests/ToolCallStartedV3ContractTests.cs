namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Engine;
using OmniCore.Abstractions;
using OmniCore.Tools;
using OmniCore.Security;

public sealed class ToolCallStartedV3ContractTests
{
    [Fact]
    public void Started_contract_and_registered_codec_are_version_three()
    {
        var payload = new ToolCallStarted(ToolCallId.New(), EffectClass.None, null);
        Assert.Equal(3, payload.SchemaVersion());
        Assert.Equal(3, EventCodecs.Create().CurrentVersion(payload.Type()));
    }

    [Fact]
    public void Started_exposes_reversibility_without_reusing_effect_class()
    {
        var property = typeof(ToolCallStarted).GetProperty("Reversibility");
        Assert.NotNull(property);
        Assert.True(property.PropertyType.IsEnum);
        Assert.Equal(new[] { "Unknown", "Reversible", "Compensatable", "Irreversible" },
            Enum.GetNames(property.PropertyType));
    }

    [Fact]
    public void Before_state_reuses_the_existing_durable_artifact_reference()
    {
        var property = typeof(ToolCallStarted).GetProperty("BeforeStateRef");
        Assert.NotNull(property);
        Assert.Equal(typeof(ArtifactRef), property.PropertyType);
        Assert.NotNull(typeof(ToolCallStarted).GetProperty("TargetRef"));
    }

    [Theory]
    [InlineData(1, EffectClass.None)]
    [InlineData(1, EffectClass.Reconcilable)]
    [InlineData(2, EffectClass.None)]
    [InlineData(2, EffectClass.Reconcilable)]
    public void Legacy_events_preserve_reconciliation_without_inventing_reversibility(int version, EffectClass effect)
    {
        var call = ToolCallId.New();
        var codecs = EventCodecs.Create();
        var payload = new ToolCallStarted(call, effect, version == 2 ? "{\"path\":\"file.txt\"}" : null);
        var json = System.Text.Json.Nodes.JsonNode.Parse(codecs.CodecFor(payload.Type()).Encode(payload))!.AsObject();
        json.Remove("Reversibility");
        json.Remove("TargetRef");
        json.Remove("BeforeStateRef");
        if (version == 1) json.Remove("ReconciliationJson");
        var stored = DomainEvent.Stored(EventId.New(), SessionId.New(), 1, payload.Type(), version,
            DateTimeOffset.UtcNow, null, null, null, null, null, null, null, call, [], json.ToJsonString());
        var decoded = Assert.IsType<ToolCallStarted>(codecs.Decode(stored));
        Assert.Equal(call, decoded.ToolCallId);
        Assert.Equal(effect, decoded.EffectClass);
        Assert.Equal(payload.ReconciliationJson, decoded.ReconciliationJson);
        Assert.Equal(Reversibility.Unknown, decoded.Reversibility);
        Assert.Null(decoded.TargetRef);
        Assert.Null(decoded.BeforeStateRef);
        Assert.Equal(version, stored.SchemaVersion);
        Assert.Equal(json.ToJsonString(), stored.PayloadJson);
    }

    [Theory]
    [InlineData(Reversibility.Unknown)]
    [InlineData(Reversibility.Reversible)]
    [InlineData(Reversibility.Compensatable)]
    [InlineData(Reversibility.Irreversible)]
    public void Version_three_fields_round_trip_independently_of_effect_class(Reversibility reversibility)
    {
        var codecs = EventCodecs.Create();
        var payload = new ToolCallStarted(ToolCallId.New(), EffectClass.NonIdempotent, "{\"pre\":\"hash\"}")
        {
            Reversibility = reversibility,
            TargetRef = "src/one.txt",
        };
        var decoded = codecs.CodecFor(payload.Type()).Decode(payload.Type(), codecs.CodecFor(payload.Type()).Encode(payload));
        Assert.Equal(payload, decoded);
        // Existing callers keep their constructor and three-value deconstruction.
        var (call, effect, reconciliation) = payload;
        Assert.Equal(payload.ToolCallId, call);
        Assert.Equal(EffectClass.NonIdempotent, effect);
        Assert.Equal(payload.ReconciliationJson, reconciliation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SQLite_reopen_preserves_attribution_and_before_state_envelope_index(bool hasBeforeState)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-toolcall-v3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            var database = Path.Combine(root, "journal.db");
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var before = hasBeforeState ? artifacts.PutText("original fixture text", "text/plain",
                ArtifactKind.Other, Sensitivity.Sensitive) : null;
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            store = new SqliteEventStore(database);
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session);
            var turn = TurnId.New();
            var execution = ExecutionId.New();
            var call = ToolCallId.New();
            stream.Append(new TurnStarted(turn, run.RootLane));
            var payload = new ToolCallStarted(call, EffectClass.NonIdempotent, "{\"pre\":\"fixture\"}")
            {
                Reversibility = hasBeforeState ? Reversibility.Reversible : Reversibility.Unknown,
                TargetRef = "src/one.txt",
                BeforeStateRef = before,
            };
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn,
                call, execution)))
            {
                stream.Append(new ToolCallRequested(call, "fixture-call", "filesystem.write", "{}"));
                stream.Append(new ToolCallPrepared(call, "{}"));
                stream.Append(new ToolCallAuthorized(call));
                stream.Append(payload, DurabilityClass.Barrier);
            }
            var original = Assert.Single(store.ReadFrom(session, 1), evt => evt.Type.Value() == payload.Type().Value());
            store.Close();
            store = new SqliteEventStore(database);
            var reopened = Assert.Single(store.ReadFrom(session, 1), evt => evt.Type.Value() == payload.Type().Value());
            Assert.Equal(payload, codecs.Decode(reopened));
            Assert.Equal(original.PayloadJson, reopened.PayloadJson);
            Assert.Equal(original.Sequence, reopened.Sequence);
            Assert.Equal(run.RunId, reopened.RunId);
            Assert.Equal(run.RootTask, reopened.TaskId);
            Assert.Equal(run.RootLane, reopened.LaneId);
            Assert.Equal(turn, reopened.TurnId);
            Assert.Equal(execution, reopened.ExecutionId);
            Assert.Equal(call, reopened.ToolCallId);
            Assert.Equal(TimeSpan.Zero, reopened.Timestamp.Offset);
            Assert.Equal(original.Timestamp, reopened.Timestamp);
            if (before is null) Assert.Empty(reopened.ArtifactRefs);
            else
            {
                Assert.Equal(before, Assert.Single(reopened.ArtifactRefs));
                Assert.True(artifacts.Verify(before.Hash, before.Size));
                Assert.Equal("original fixture text", artifacts.GetText(before.Hash));
            }
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
            var orphan = artifacts.PutText("unreferenced fixture pre-image", "text/plain",
                ArtifactKind.Other, Sensitivity.Sensitive);
            var sweep = new ArtifactGc(Path.Combine(root, "cas")).Sweep(database, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, sweep.Deleted);
            Assert.False(artifacts.Verify(orphan.Hash, orphan.Size));
            if (before is not null) Assert.True(artifacts.Verify(before.Hash, before.Size));
        }
        finally
        {
            store?.Close();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + Path.Combine(root, "journal.db"));
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Runtime_does_not_guess_a_target_or_reversibility_from_reconciliation(int writeCount)
    {
        var events = new List<DomainEventPayload>();
        var tool = new ClaimTool(writeCount, events);
        var runtime = ToolRuntime.For(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("fixture.claims", PermissionDecision.Allow),
            payload => { events.Add(payload); return VoidBox.Instance; });
        var result = runtime.Run(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "fixture-call", "{}"),
            new ToolPreparationContext(".", DateTimeOffset.UtcNow), new ToolExecutionContext("."), false,
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(1, tool.Calls);
        var started = Assert.Single(events.OfType<ToolCallStarted>());
        Assert.Equal(writeCount == 1 ? "src/file-0.txt" : null, started.TargetRef);
        Assert.Equal(Reversibility.Unknown, started.Reversibility);
        Assert.Null(started.BeforeStateRef);
        Assert.Equal(writeCount == 1, started.ReconciliationJson is not null);
    }

    private sealed class ClaimTool(int writeCount, List<DomainEventPayload> events) : ITool
    {
        public ToolDescriptor Descriptor { get; } = FakeTool.Write("fixture.claims").Descriptor;
        public int Calls { get; private set; }

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.Reconcilable, new ResourceClaims([], Enumerable.Range(0, writeCount)
                    .Select(index => "src/file-" + index + ".txt").ToArray(), [], null, []), ToolRisk.Low,
                new ReconciliationSpec("pre-hash", "post-hash", null)));

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Actual pipeline ordering, not a manufactured journal: Started precedes Execute.
            Assert.Single(events.OfType<ToolCallStarted>());
            Calls++;
            return System.Threading.Tasks.Task.FromResult(new ToolResult("fixture success", "fixture result", null, 0, false,
                EffectOutcome.Applied));
        }
    }
}
