using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Contracts of started v4 and completed v3, including legacy journals, without a provider.</summary>
public sealed class ModelStepEventContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Started_and_completed_round_trip_all_fields_and_optional_nulls(bool optionalNulls)
    {
        var codecs = EventCodecs.Create();
        var turn = TurnId.New();
        var context = optionalNulls ? null : Reference(ArtifactKind.ContextSnapshot);
        var response = optionalNulls ? null : Reference(ArtifactKind.ModelResponse);
        var started = new ModelStepStarted(turn, 7, "contract-model", 4_294_967_296L,
            "Grammar", optionalNulls ? null : "Enabled", optionalNulls ? null : 4096, context,
            optionalNulls ? null : 16000, optionalNulls ? null : new RouteId("provider/route"));
        var completed = new ModelStepCompleted(turn, 7,
            new TokenUsage(4_294_967_297L, 53, 17, 29, 41), StopReason.MaxOutputTokens,
            response, "2026-10-04", optionalNulls ? null : 0.1234567890123456789012345678m,
            optionalNulls ? null : TokenUsageFields.All);

        Assert.Equal("model_step.started", started.Type().Value());
        Assert.Equal("model_step.completed", completed.Type().Value());
        foreach (var payload in new DomainEventPayload[] { started, completed })
        {
            var expectedVersion = payload is ModelStepStarted ? 4 : 3;
            Assert.Equal(expectedVersion, payload.SchemaVersion());
            Assert.Equal(expectedVersion, codecs.CurrentVersion(payload.Type()));
            var restored = codecs.Decode(Envelope(payload, codecs));
            Assert.Equal(payload, restored);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_v1_missing_observation_fields_remains_readable(bool completed)
    {
        var codecs = EventCodecs.Create();
        var payload = Step(completed);
        var envelope = Envelope(payload, codecs);
        var json = JsonNode.Parse(envelope.PayloadJson)!.AsObject();
        json.Remove("ReportedUsageFields"); json.Remove("ModelContextCapacity"); json.Remove("RouteId");
        var legacy = DomainEvent.Create(envelope.SessionId, envelope.Type, 1, null, null, null,
            null, null, null, null, null, [], json.ToJsonString());
        var decoded = codecs.Decode(legacy);
        if (completed) Assert.Null(Assert.IsType<ModelStepCompleted>(decoded).ReportedUsageFields);
        else
        {
            var started = Assert.IsType<ModelStepStarted>(decoded);
            Assert.Null(started.ModelContextCapacity);
            Assert.Null(started.RouteId);
        }
    }

    [Fact]
    public void Legacy_v2_without_route_preserves_capacity_and_identity()
    {
        var codecs = EventCodecs.Create();
        var payload = new ModelStepStarted(TurnId.New(), 2, "legacy-model", 8192, "Direct", null, null, null, 16000);
        var envelope = Envelope(payload, codecs);
        var json = JsonNode.Parse(envelope.PayloadJson)!.AsObject();
        json.Remove("RouteId");
        var legacy = DomainEvent.Create(envelope.SessionId, envelope.Type, 2, null, null, null,
            null, null, null, null, null, [], json.ToJsonString());
        var decoded = Assert.IsType<ModelStepStarted>(codecs.Decode(legacy));
        Assert.Equal(payload, decoded);
        Assert.Null(decoded.RouteId);
        Assert.Equal(16000L, decoded.ModelContextCapacity);
    }

    [Theory]
    [InlineData(false, int.MinValue)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, int.MinValue)]
    [InlineData(true, int.MaxValue)]
    public void StepIndex_round_trips_at_int_boundaries(bool completed, int stepIndex)
    {
        // v1 defines an int, but its codec does not impose a nonnegative business constraint.
        var codecs = EventCodecs.Create();
        var payload = Step(completed, stepIndex);
        Assert.Equal(payload, codecs.Decode(Envelope(payload, codecs)));
    }

    [Theory]
    [InlineData(false, "2147483648")]
    [InlineData(false, "-2147483649")]
    [InlineData(false, "1.5")]
    [InlineData(false, "\"7\"")]
    [InlineData(true, "2147483648")]
    [InlineData(true, "-2147483649")]
    [InlineData(true, "1.5")]
    [InlineData(true, "\"7\"")]
    public void Invalid_StepIndex_fails_with_the_registered_typed_parse_error(bool completed, string invalidJson)
    {
        var codecs = EventCodecs.Create();
        var evt = Envelope(Step(completed), codecs);
        var json = JsonNode.Parse(evt.PayloadJson)!.AsObject();
        json["StepIndex"] = JsonNode.Parse(invalidJson);
        evt = DomainEvent.Create(evt.SessionId, evt.Type, evt.SchemaVersion, null, null, null,
            null, null, null, null, null, Array.Empty<ArtifactRef>(), json.ToJsonString());

        var error = Assert.Throws<EventParseException>(() => codecs.Decode(evt));

        Assert.Equal(evt.Type.Value(), error.EventType);
        Assert.Contains("StepIndex", error.Detail!);
    }

    [Fact]
    public void EventStream_SQLite_reopen_retains_step_payload_and_envelope_reference_metadata()
    {
        using var fx = new Fixture();
        var session = SessionId.New();
        var turn = TurnId.New();
        var context = fx.Artifacts.PutText("context snapshot", "application/json",
            ArtifactKind.ContextSnapshot, Sensitivity.Sensitive) with { Redacted = true };
        var response = fx.Artifacts.PutText("model response", "text/plain",
            ArtifactKind.ModelResponse, Sensitivity.Sensitive) with { Redacted = true };
        var started = new ModelStepStarted(turn, 2, "contract-model", 8192, "Native", "Enabled", 128, context,
            null, new RouteId("reopened-route"));
        var completed = new ModelStepCompleted(turn, 2, new TokenUsage(101, 23, 5, 7, 11),
            StopReason.ToolUse, response, "2026-10-04", 0.012345m);
        var stream = new EventStream(fx.Store, fx.Codecs, session);
        stream.Append(started);
        stream.Append(completed);
        fx.Store.Close();

        var reopened = new SqliteEventStore(fx.JournalPath);
        try
        {
            var events = reopened.ReadFrom(session, 1);
            Assert.Equal(2, events.Count);
            Assert.Equal(started, Assert.IsType<ModelStepStarted>(fx.Codecs.Decode(events[0])));
            Assert.Equal(completed, Assert.IsType<ModelStepCompleted>(fx.Codecs.Decode(events[1])));
            Assert.Equal(context, Assert.Single(events[0].ArtifactRefs));
            Assert.Equal(response, Assert.Single(events[1].ArtifactRefs));
            Assert.All(events, evt => Assert.Equal(turn, evt.TurnId));
        }
        finally
        {
            reopened.Close();
        }

        var report = new JournalVerifier(fx.Codecs, fx.Artifacts).VerifyJournal(fx.JournalPath,
            TestContext.Current.CancellationToken);
        Assert.True(report.Ok, string.Join("; ", report.Issues.Select(issue => issue.Detail)));
        Assert.Equal(4, report.ArtifactRefCount); // Each envelope ref and each typed payload ref.
    }

    [Fact]
    public void Null_step_artifacts_produce_empty_envelope_indexes()
    {
        using var fx = new Fixture();
        var session = SessionId.New();
        var stream = new EventStream(fx.Store, fx.Codecs, session);
        stream.Append(Step(false));
        stream.Append(Step(true));

        Assert.All(fx.Store.ReadFrom(session, 1), evt => Assert.Empty(evt.ArtifactRefs));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Verifier_checks_step_payload_artifact_even_without_envelope_index(bool completed, bool wrongSize)
    {
        using var fx = new Fixture();
        var artifact = fx.Artifacts.PutText("referenced payload", "text/plain",
            completed ? ArtifactKind.ModelResponse : ArtifactKind.ContextSnapshot, Sensitivity.Normal);
        var payload = Step(completed, artifact: wrongSize ? artifact with { Size = artifact.Size + 1 } : artifact);
        fx.AppendPayloadOnly(payload);
        fx.Store.Close();
        if (!wrongSize)
            File.Delete(fx.BlobPath(artifact));

        var report = new JournalVerifier(fx.Codecs, fx.Artifacts).VerifyJournal(fx.JournalPath,
            TestContext.Current.CancellationToken);

        Assert.False(report.Ok);
        Assert.Equal(1, report.ArtifactRefCount);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(wrongSize ? JournalIssueCode.ArtifactSizeMismatch : JournalIssueCode.ArtifactMissing,
            issue.Code);
        Assert.Equal(payload.Type().Value() + (completed ? ".responseArtifact" : ".contextSnapshotRef"),
            issue.Field);
        Assert.Equal(1, issue.Sequence);
    }

    [Fact]
    public void Gc_retains_both_step_artifacts_and_collects_an_unreferenced_blob()
    {
        using var fx = new Fixture();
        var context = fx.Artifacts.PutText("live context", "application/json",
            ArtifactKind.ContextSnapshot, Sensitivity.Normal);
        var response = fx.Artifacts.PutText("live response", "text/plain",
            ArtifactKind.ModelResponse, Sensitivity.Normal);
        var orphan = fx.Artifacts.PutText("unreferenced", "text/plain", ArtifactKind.Other, Sensitivity.Normal);
        var session = SessionId.New();
        var stream = new EventStream(fx.Store, fx.Codecs, session);
        stream.Append(Step(false, artifact: context));
        stream.Append(Step(true, artifact: response));
        fx.Store.Close();
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        foreach (var artifact in new[] { context, response, orphan })
            File.SetLastWriteTimeUtc(fx.BlobPath(artifact), now.UtcDateTime.AddDays(-3));

        var result = new ArtifactGc(fx.DataDir).Sweep(fx.JournalPath, TimeSpan.Zero, false, now,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.LiveReferenced);
        Assert.Equal(1, result.Deleted);
        Assert.Equal("live context", fx.Artifacts.GetText(context.Hash));
        Assert.Equal("live response", fx.Artifacts.GetText(response.Hash));
        Assert.False(File.Exists(fx.BlobPath(orphan)));
    }

    private static ArtifactRef Reference(ArtifactKind kind) => new(ArtifactId.New(),
        ContentHash.Sha256(new string('a', 64)), 4_294_967_298L, "application/json", kind,
        Sensitivity.Sensitive, Redacted: true);

    private static DomainEventPayload Step(bool completed, int stepIndex = 0, ArtifactRef? artifact = null) =>
        completed
            ? new ModelStepCompleted(TurnId.New(), stepIndex, new TokenUsage(1, 2, 3, 4, 5),
                StopReason.EndTurn, artifact, "2026-10-04", null)
            : new ModelStepStarted(TurnId.New(), stepIndex, "contract-model", 8192, "Native", null, null, artifact);

    private static DomainEvent Envelope(DomainEventPayload payload, EventCodecs codecs,
        SessionId? session = null) => DomainEvent.Create(session ?? SessionId.New(), payload.Type(),
            payload.SchemaVersion(), null, null, null, null, null, null, null, null,
            Array.Empty<ArtifactRef>(), codecs.CodecFor(payload.Type()).Encode(payload));

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "omnicore-model-step-contract-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            DataDir = Path.Combine(_root, "data");
            JournalPath = Path.Combine(_root, "journal.db");
            Artifacts = new FileArtifactStore(DataDir);
            Store = new SqliteEventStore(JournalPath);
        }

        public string DataDir { get; }
        public string JournalPath { get; }
        public FileArtifactStore Artifacts { get; }
        public SqliteEventStore Store { get; }
        public EventCodecs Codecs { get; } = EventCodecs.Create();

        public void AppendPayloadOnly(DomainEventPayload payload)
        {
            var evt = Envelope(payload, Codecs);
            Store.Append(evt.SessionId, evt, DurabilityClass.Standard, CancellationToken.None);
        }

        public string BlobPath(ArtifactRef artifact) => Path.Combine(DataDir, "blobs", artifact.Hash.Algorithm,
            artifact.Hash.Value[..2], artifact.Hash.Value.Substring(2, 2), artifact.Hash.Value);

        public void Dispose()
        {
            Store.Close();
            SqliteConnection.ClearPool((SqliteConnection)Store.Connection);
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best effort if Windows still holds a transient SQLite handle. */ }
        }
    }
}
