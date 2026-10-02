using System.Collections.Generic;
using System.Linq;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Xunit;

namespace OmniCore.Tests;

/// <summary>
/// Tests for M5.5 Phase A Bug 2: ENVELOPE GENERATION HALF ONLY.
/// Verifies that EventStream.BuildEnvelope correctly extracts ArtifactRefs from typed
/// domain event payloads and includes them in the envelope for indexing (ADR-0001 §3).
/// </summary>
public sealed class ArtifactRefsEnvelopeTests
{
    private static InMemoryEventStore Store() => new();

    private static EventCodecs Codecs() => EventCodecs.Create();

    private static SessionId Session() => SessionId.New();

    private static ArtifactRef MakeRef(string suffix = "") =>
        new(
            ArtifactId.New(),
            ContentHash.Sha256("a".Repeat(64)),
            100,
            "application/json",
            ArtifactKind.ModelResponse,
            Sensitivity.Normal);

    private static RunCreated NewRun(SessionId session, RunId run, TaskId rootTask) =>
        new(run, session, "objetivo", RunMode.Act, ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
            new TaskBudget(null, null, null, null), rootTask, DateTimeOffset.UtcNow);

    // ── Events WITH artifact references ────────────────────────────────────────────────

    [Fact]
    public void RunValidationRejected_carries_OutputArtifacts_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var artifacts = new[] { MakeRef("1"), MakeRef("2") };

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));
        stream.Append(new RunValidationStarted(run));
        stream.Append(new RunValidationRejected(run, ["gate1"], ["missing"], artifacts));

        var events = store.ReadFrom(session, 1);
        var rejected = events.First(e => e.Type.Value() == "run.validation_rejected");

        Assert.Equal(2, rejected.ArtifactRefs.Count);
        Assert.Equal(artifacts[0].Id, rejected.ArtifactRefs[0].Id);
        Assert.Equal(artifacts[1].Id, rejected.ArtifactRefs[1].Id);
    }

    [Fact]
    public void TurnStarted_carries_ContextSnapshotRef_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var turn = TurnId.New();
        var ctxRef = MakeRef("ctx");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "t", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));
        stream.Append(new LaneCreated(lane, task, ProfileId.New()));
        stream.Append(new TurnStarted(turn, lane, null, ctxRef));

        var events = store.ReadFrom(session, 1);
        var started = events.First(e => e.Type.Value() == "turn.started");

        Assert.Single(started.ArtifactRefs);
        Assert.Equal(ctxRef.Id, started.ArtifactRefs[0].Id);
    }

    [Fact]
    public void ModelCompleted_carries_ResponseArtifact_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var turn = TurnId.New();
        var respRef = MakeRef("resp");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "t", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));
        stream.Append(new LaneCreated(lane, task, ProfileId.New()));
        stream.Append(new TurnStarted(turn, lane));
        stream.Append(new ModelCompleted(turn, respRef));

        var events = store.ReadFrom(session, 1);
        var completed = events.First(e => e.Type.Value() == "model.completed");

        Assert.Single(completed.ArtifactRefs);
        Assert.Equal(respRef.Id, completed.ArtifactRefs[0].Id);
    }

    [Fact]
    public void UserInputReceived_carries_ContentRef_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var contentRef = MakeRef("input");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));
        stream.Append(new UserInputReceived(run, "[]", contentRef));

        var events = store.ReadFrom(session, 1);
        var input = events.First(e => e.Type.Value() == "user_input.received");

        Assert.Single(input.ArtifactRefs);
        Assert.Equal(contentRef.Id, input.ArtifactRefs[0].Id);
    }

    [Fact]
    public void AssistantMessageRecorded_carries_ContentRef_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var lane = LaneId.New();
        var turn = TurnId.New();
        var contentRef = MakeRef("assistant");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));
        stream.Append(new AssistantMessageRecorded(run, lane, turn, contentRef));

        var events = store.ReadFrom(session, 1);
        var msg = events.First(e => e.Type.Value() == "assistant_message.recorded");

        Assert.Single(msg.ArtifactRefs);
        Assert.Equal(contentRef.Id, msg.ArtifactRefs[0].Id);
    }

    [Fact]
    public void InteractionRequested_carries_QuestionnaireSchemaRef_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var interaction = InteractionId.New();
        var schemaRef = MakeRef("schema");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(new InteractionRequested(
            interaction,
            InteractionKind.Question,
            "{}",
            "[]",
            "opt1",
            null,
            null,
            null,
            null,
            0,
            1,
            schemaRef));

        var events = store.ReadFrom(session, 1);
        var requested = events.First(e => e.Type.Value() == "interaction.requested");

        Assert.Single(requested.ArtifactRefs);
        Assert.Equal(schemaRef.Id, requested.ArtifactRefs[0].Id);
    }

    [Fact]
    public void InteractionResolved_carries_AnswerRef_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var interaction = InteractionId.New();
        var answerRef = MakeRef("answer");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(new InteractionResolved(interaction, "opt1", InteractionCause.User, answerRef));

        var events = store.ReadFrom(session, 1);
        var resolved = events.First(e => e.Type.Value() == "interaction.resolved");

        Assert.Single(resolved.ArtifactRefs);
        Assert.Equal(answerRef.Id, resolved.ArtifactRefs[0].Id);
    }

    [Fact]
    public void ContextCheckpointRecorded_carries_CheckpointArtifact_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var checkpointRef = MakeRef("checkpoint");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));
        stream.Append(new ContextCheckpointRecorded("cp-1", run, 10, checkpointRef, "fingerprint"));

        var events = store.ReadFrom(session, 1);
        var checkpoint = events.First(e => e.Type.Value() == "context.checkpoint_recorded");

        Assert.Single(checkpoint.ArtifactRefs);
        Assert.Equal(checkpointRef.Id, checkpoint.ArtifactRefs[0].Id);
    }

    [Fact]
    public void MetaModelInvocationStarted_carries_InputArtifact_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var inputRef = MakeRef("input");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));
        stream.Append(new MetaModelInvocationStarted("inv-1", run, "compact", "fingerprint", inputRef));

        var events = store.ReadFrom(session, 1);
        var started = events.First(e => e.Type.Value() == "meta_model.invocation_started");

        Assert.Single(started.ArtifactRefs);
        Assert.Equal(inputRef.Id, started.ArtifactRefs[0].Id);
    }

    [Fact]
    public void MetaModelInvocationCompleted_carries_OutputArtifact_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var outputRef = MakeRef("output");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));
        stream.Append(new MetaModelInvocationCompleted("inv-1", run, "compact", "fingerprint", outputRef));

        var events = store.ReadFrom(session, 1);
        var completed = events.First(e => e.Type.Value() == "meta_model.invocation_completed");

        Assert.Single(completed.ArtifactRefs);
        Assert.Equal(outputRef.Id, completed.ArtifactRefs[0].Id);
    }

    [Fact]
    public void TaskCompleted_with_AgentResult_carries_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var artifacts = new[] { MakeRef("a1"), MakeRef("a2") };
        var result = new AgentResult(
            AgentOutcome.Succeeded,
            "summary",
            ["finding1"],
            artifacts,
            ["file1.cs"],
            [],
            ConfidenceLevel.High,
            []);

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "t", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));
        stream.Append(new TaskReady(task));
        stream.Append(new TaskStarted(task, lane));
        stream.Append(new TaskCompleted(task, result));

        var events = store.ReadFrom(session, 1);
        var completed = events.First(e => e.Type.Value() == "task.completed");

        Assert.Equal(2, completed.ArtifactRefs.Count);
        Assert.Equal(artifacts[0].Id, completed.ArtifactRefs[0].Id);
        Assert.Equal(artifacts[1].Id, completed.ArtifactRefs[1].Id);
    }

    [Fact]
    public void LaneCompleted_with_AgentResult_carries_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var artifacts = new[] { MakeRef("l1"), MakeRef("l2"), MakeRef("l3") };
        var result = new AgentResult(
            AgentOutcome.Succeeded,
            "summary",
            ["finding1"],
            artifacts,
            ["file1.cs"],
            [],
            ConfidenceLevel.High,
            []);

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "t", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));
        stream.Append(new LaneCreated(lane, task, ProfileId.New()));
        stream.Append(new LaneStarted(lane));
        stream.Append(new LaneCompleted(lane, result));

        var events = store.ReadFrom(session, 1);
        var completed = events.First(e => e.Type.Value() == "lane.completed");

        Assert.Equal(3, completed.ArtifactRefs.Count);
        Assert.Equal(artifacts[0].Id, completed.ArtifactRefs[0].Id);
        Assert.Equal(artifacts[1].Id, completed.ArtifactRefs[1].Id);
        Assert.Equal(artifacts[2].Id, completed.ArtifactRefs[2].Id);
    }

    // ── Events WITHOUT artifact references ────────────────────────────────────────────

    [Fact]
    public void RunCreated_has_empty_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));

        var events = store.ReadFrom(session, 1);
        var created = events.First(e => e.Type.Value() == "run.created");

        Assert.Empty(created.ArtifactRefs);
    }

    [Fact]
    public void RunStarted_has_empty_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));

        var events = store.ReadFrom(session, 1);
        var started = events.First(e => e.Type.Value() == "run.started");

        Assert.Empty(started.ArtifactRefs);
    }

    [Fact]
    public void TaskCreated_has_empty_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "t", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));

        var events = store.ReadFrom(session, 1);
        var created = events.First(e => e.Type.Value() == "task.created");

        Assert.Empty(created.ArtifactRefs);
    }

    [Fact]
    public void LaneCreated_has_empty_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "t", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));
        stream.Append(new LaneCreated(lane, task, ProfileId.New()));

        var events = store.ReadFrom(session, 1);
        var created = events.First(e => e.Type.Value() == "lane.created");

        Assert.Empty(created.ArtifactRefs);
    }

    [Fact]
    public void ToolCallRequested_has_empty_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var call = ToolCallId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(new ToolCallRequested(call, "pc-1", "fake.read", "{}"));

        var events = store.ReadFrom(session, 1);
        var requested = events.First(e => e.Type.Value() == "toolcall.requested");

        Assert.Empty(requested.ArtifactRefs);
    }

    [Fact]
    public void ToolCallSucceeded_has_empty_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var call = ToolCallId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(new ToolCallRequested(call, "pc-1", "fake.read", "{}"));
        stream.Append(new ToolCallPrepared(call, "{}"));
        stream.Append(new ToolCallAuthorized(call));
        stream.Append(new ToolCallStarted(call, EffectClass.None, null));
        stream.Append(new ToolCallSucceeded(call, "{}"));

        var events = store.ReadFrom(session, 1);
        var succeeded = events.First(e => e.Type.Value() == "toolcall.succeeded");

        Assert.Empty(succeeded.ArtifactRefs);
    }

    [Fact]
    public void PlanCreated_has_empty_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var plan = PlanId.New();
        var root = PlanItemId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new PlanCreated(plan, run, root, "objective"));

        var events = store.ReadFrom(session, 1);
        var created = events.First(e => e.Type.Value() == "plan.created");

        Assert.Empty(created.ArtifactRefs);
    }

    [Fact]
    public void InteractionExpired_has_empty_ArtifactRefs_in_envelope()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var interaction = InteractionId.New();

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(new InteractionExpired(interaction));

        var events = store.ReadFrom(session, 1);
        var expired = events.First(e => e.Type.Value() == "interaction.expired");

        Assert.Empty(expired.ArtifactRefs);
    }

    // ── Deduplication and order preservation ──────────────────────────────────────────

    [Fact]
    public void Duplicate_artifact_refs_are_deduplicated_by_Id_preserving_first_occurrence()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var shared = MakeRef("shared");
        var unique = MakeRef("unique");
        // Create an AgentResult with duplicate artifact IDs
        var artifacts = new[] { shared, unique, shared };
        var result = new AgentResult(
            AgentOutcome.Succeeded,
            "summary",
            [],
            artifacts,
            [],
            [],
            ConfidenceLevel.High,
            []);

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "t", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));
        stream.Append(new TaskReady(task));
        stream.Append(new TaskStarted(task, lane));
        stream.Append(new TaskCompleted(task, result));

        var events = store.ReadFrom(session, 1);
        var completed = events.First(e => e.Type.Value() == "task.completed");

        // Should have 2 unique refs (shared appears twice but deduplicated)
        Assert.Equal(2, completed.ArtifactRefs.Count);
        Assert.Equal(shared.Id, completed.ArtifactRefs[0].Id); // First occurrence preserved
        Assert.Equal(unique.Id, completed.ArtifactRefs[1].Id);
    }

    [Fact]
    public void RunValidationRejected_with_duplicate_OutputArtifacts_deduplicates_preserving_order()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var shared = MakeRef("shared");
        var unique = MakeRef("unique");
        var artifacts = new[] { shared, unique, shared };

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));
        stream.Append(new RunValidationStarted(run));
        stream.Append(new RunValidationRejected(run, ["gate1"], ["missing"], artifacts));

        var events = store.ReadFrom(session, 1);
        var rejected = events.First(e => e.Type.Value() == "run.validation_rejected");

        Assert.Equal(2, rejected.ArtifactRefs.Count);
        Assert.Equal(shared.Id, rejected.ArtifactRefs[0].Id);
        Assert.Equal(unique.Id, rejected.ArtifactRefs[1].Id);
    }

    // ── AppendBatch also extracts refs correctly ──────────────────────────────────────

    [Fact]
    public void AppendBatch_includes_ArtifactRefs_for_each_event()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var ctxRef = MakeRef("ctx");
        var respRef = MakeRef("resp");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, task));
        stream.Append(new TaskCreated(task, run, "t", Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));
        stream.Append(new LaneCreated(lane, task, ProfileId.New()));
        stream.Append(new LaneStarted(lane));

        var turn = TurnId.New();
        stream.AppendBatch(new DomainEventPayload[]
        {
            new TurnStarted(turn, lane, null, ctxRef),
            new ModelCompleted(turn, respRef),
        }, DurabilityClass.Standard);

        var events = store.ReadFrom(session, 1);
        var turnStarted = events.First(e => e.Type.Value() == "turn.started");
        var modelCompleted = events.First(e => e.Type.Value() == "model.completed");

        Assert.Single(turnStarted.ArtifactRefs);
        Assert.Equal(ctxRef.Id, turnStarted.ArtifactRefs[0].Id);

        Assert.Single(modelCompleted.ArtifactRefs);
        Assert.Equal(respRef.Id, modelCompleted.ArtifactRefs[0].Id);
    }

    // ── Round-trip through codec preserves envelope ArtifactRefs ──────────────────────

    [Fact]
    public void Envelope_ArtifactRefs_survive_codec_round_trip()
    {
        var store = Store();
        var codecs = Codecs();
        var session = Session();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var artifact = MakeRef("roundtrip");

        stream.Append(new SessionCreated(session, "ws", "/ws", ProfileId.New(), DateTimeOffset.UtcNow));
        stream.Append(NewRun(session, run, TaskId.New()));
        stream.Append(new RunStarted(run));
        stream.Append(new RunValidationStarted(run));
        stream.Append(new RunValidationRejected(run, ["gate1"], ["missing"], [artifact]));

        var events = store.ReadFrom(session, 1);
        var rejected = events.First(e => e.Type.Value() == "run.validation_rejected");

        // Decode through codec
        var payload = codecs.Decode(rejected);
        var decoded = Assert.IsType<RunValidationRejected>(payload);

        // The decoded payload should have the same artifact refs
        Assert.NotNull(decoded.OutputArtifacts);
        Assert.Single(decoded.OutputArtifacts!);
        Assert.Equal(artifact.Id, decoded.OutputArtifacts![0].Id);

        // And the envelope still has them
        Assert.Single(rejected.ArtifactRefs);
        Assert.Equal(artifact.Id, rejected.ArtifactRefs[0].Id);
    }
}

/// <summary>String extension for test data generation.</summary>
file static class StringExtensions
{
    public static string Repeat(this string s, int count) => string.Concat(Enumerable.Repeat(s, count));
}