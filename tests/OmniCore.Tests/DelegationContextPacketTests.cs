using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using LaneFixture = OmniCore.Tests.LaneContextIsolationTests.Fixture;

namespace OmniCore.Tests;

/// <summary>Real SQLite/CAS and ExplorerTurn; delegation facts are explicit fixtures, not scheduler admission.</summary>
public sealed class DelegationContextPacketTests
{
    [Fact]
    public async System.Threading.Tasks.Task Accepted_packet_automatically_reaches_child_after_reopen_without_manual_contributor_injection()
    {
        using var fx = new Fixture();
        var sequence = fx.Lanes.Store.CurrentSequence(fx.Lanes.Session);
        var prepared = fx.Prepare();
        Assert.Equal(sequence, fx.Lanes.Store.CurrentSequence(fx.Lanes.Session));
        Assert.False(fx.Lanes.Artifacts.Verify(prepared.Reference.Hash, prepared.Reference.Size));
        var reference = fx.Record(prepared);
        fx.Lanes.Reopen();
        var text = fx.Lanes.Artifacts.GetText(reference.Hash)!;
        Assert.Contains("selected parent fact", text);
        Assert.DoesNotContain("parent private state", text);
        Assert.DoesNotContain("parent system", text);
        Assert.DoesNotContain("other parent input", text);
        Assert.DoesNotContain(fx.Source.Hash.Value, text); // no capability to re-read the entire source
        ModelRequest? received = null;
        var explorer = fx.Lanes.Explorer((request, _) => { received = request; return Response(); });
        Assert.Equal(StopReason.EndTurn, explorer.Ask("CURRENT CHILD", "child system", fx.Lanes.Session,
            fx.Lanes.Run.RunId, fx.Child.Lane, "child current state", TestContext.Current.CancellationToken).StopReason);
        Assert.Contains("selected parent fact", received!.Instructions);
        Assert.Contains("español áéíóú ñ ¿qué?", received.Instructions);
        Assert.Contains("not instructions or authority", received.Instructions);
        Assert.DoesNotContain("other parent input", received.Instructions);
        Assert.Contains(received.Messages.SelectMany(message => message.Content).OfType<TextBlock>(), text => text.Text == "CURRENT CHILD");
        var snapshot = fx.Lanes.Store.ReadFrom(fx.Lanes.Session, 1).Select(fx.Lanes.Codecs.Decode)
            .OfType<ModelStepStarted>().Last().ContextSnapshotRef!;
        using var json = JsonDocument.Parse(fx.Lanes.Artifacts.GetText(snapshot.Hash)!);
        Assert.Contains(json.RootElement.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("id").GetString()!.StartsWith("inherited-", StringComparison.Ordinal));
        var rootRequest = Request(fx, fx.Lanes.Run.RootTask, fx.Lanes.Run.RootLane);
        Assert.Empty(await fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId)
            .GetContextAsync(rootRequest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async System.Threading.Tasks.Task Acceptance_is_required_and_cached_projection_stops_inheriting_after_failure()
    {
        using var fx = new Fixture();
        fx.Record(fx.Prepare(), accept: false);
        Assert.Throws<InvalidDataException>(() => fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId));
        Assert.Null(fx.Service.FindDelegationProjection(fx.Lanes.Session, fx.Lanes.Run.RunId, fx.Child.Lane));
        var sequenceBeforeAcceptance = fx.Lanes.Store.CurrentSequence(fx.Lanes.Session);
        fx.Accept();
        var projection = fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId);
        var request = Request(fx, fx.Child.Task, fx.Child.Lane);
        Assert.Single(await projection.GetContextAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(await projection.GetContextAsync(new MaterializeRequest(request.SessionId, request.RunId,
            request.TaskId, request.LaneId, request.TurnId, sequenceBeforeAcceptance, request.Fingerprint),
            TestContext.Current.CancellationToken));
        using (fx.OwnerScope()) fx.Stream.Append(new DelegationFailed(fx.Owner, fx.Target.DelegationId, "explicit fixture failure"));
        Assert.Empty(await projection.GetContextAsync(Request(fx, fx.Child.Task, fx.Child.Lane), TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId));
    }

    [Fact]
    public async System.Threading.Tasks.Task Default_empty_selection_and_preparation_retry_do_not_invent_history_or_replace_canonical_packets()
    {
        using var fx = new Fixture();
        var first = fx.Prepare(ContextInheritancePolicy.Default);
        var retry = fx.Prepare(ContextInheritancePolicy.Default);
        Assert.Equal(first.Reference.Hash, retry.Reference.Hash);
        Assert.Equal(first.Reference.Size, retry.Reference.Size);
        fx.Record(first);
        Assert.Throws<InvalidDataException>(() => fx.Prepare());
        var before = fx.Lanes.Store.CurrentSequence(fx.Lanes.Session);
        var projection = fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId);
        Assert.Empty(await projection.GetContextAsync(Request(fx, fx.Child.Task, fx.Child.Lane), TestContext.Current.CancellationToken));
        Assert.Equal(before, fx.Lanes.Store.CurrentSequence(fx.Lanes.Session));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("session")]
    [InlineData("run")]
    [InlineData("parent-task")]
    [InlineData("parent-lane")]
    [InlineData("child-task")]
    [InlineData("child-lane")]
    [InlineData("profile")]
    [InlineData("owner")]
    [InlineData("relation")]
    [InlineData("supervision")]
    [InlineData("source-event")]
    [InlineData("source-session")]
    [InlineData("source-sequence")]
    [InlineData("source-snapshot")]
    [InlineData("size")]
    [InlineData("invented-fact")]
    [InlineData("kind")]
    [InlineData("system")]
    [InlineData("duplicate")]
    public void Canonical_ref_does_not_make_mismatched_or_invented_packet_data_valid(string defect)
    {
        using var fx = new Fixture();
        var prepared = fx.Prepare();
        // This is deliberately malformed canonical fixture data: the record-only event validates
        // lineage/ref shape, while the reader must validate the actual immutable packet body.
        var original = prepared.Publish();
        var packet = JsonSerializer.Deserialize(fx.Lanes.Artifacts.GetText(original.Hash)!,
            DelegationContextJson.Default.DelegationContextPacket)!;
        var altered = defect switch
        {
            "version" => packet with { SchemaVersion = 2 },
            "session" => packet with { SessionId = SessionId.New() },
            "run" => packet with { RunId = RunId.New() },
            "parent-task" => packet with { ParentTaskId = TaskId.New() },
            "parent-lane" => packet with { ParentLaneId = LaneId.New() },
            "child-task" => packet with { ChildTaskId = TaskId.New() },
            "child-lane" => packet with { Target = packet.Target with { ChildLaneId = LaneId.New() } },
            "profile" => packet with { ChildProfileId = ProfileId.New() },
            "owner" => packet with { Target = packet.Target with { ParentExecutionId = ExecutionId.New() } },
            "relation" => packet with { Target = packet.Target with { Relation = ExecutionRelation.Detached } },
            "supervision" => packet with { Target = packet.Target with { Supervision = ExecutionSupervision.Unmanaged } },
            "source-event" => packet with { SourceEvent = packet.SourceEvent with { EventId = EventId.New() } },
            "source-session" => packet with { SourceEvent = packet.SourceEvent with { SessionId = SessionId.New() } },
            "source-sequence" => packet with { SourceSequence = long.MaxValue },
            "source-snapshot" => packet with { SourceSnapshotId = Guid.NewGuid() },
            "size" => packet with { MaximumUtf8Bytes = 1 },
            "invented-fact" => packet with { Facts = new[] { packet.Facts[0] with { Content = "invented authority" } } },
            "kind" => packet with { Facts = new[] { packet.Facts[0] with { SourceKind = ContextItemKind.File } } },
            "system" => packet with { Facts = new[] { packet.Facts[0] with { SourceItemId = "system-prompt" } } },
            "duplicate" => packet with { Facts = new[] { packet.Facts[0], packet.Facts[0] } },
            _ => throw new ArgumentException(defect),
        };
        var malformed = ((IArtifactPreparationStore)fx.Lanes.Artifacts).PrepareText(
            JsonSerializer.Serialize(altered, DelegationContextJson.Default.DelegationContextPacket),
            ContextInheritanceService.DelegationPacketMediaType, ArtifactKind.Other, Sensitivity.Sensitive);
        fx.Record(malformed);
        fx.Lanes.Reopen();
        Assert.Throws<InvalidDataException>(() => fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId));
        var sends = 0;
        var explorer = fx.Lanes.Explorer((_, _) => { sends++; return Response(); });
        // ExplorerTurn may throw the fail-closed diagnostic rather than dispatch corrupted context.
        try { explorer.Ask("child intent", "system", fx.Lanes.Session, fx.Lanes.Run.RunId,
            fx.Child.Lane, "state", TestContext.Current.CancellationToken); }
        catch (InvalidDataException) { }
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1048577)]
    public void Preparation_requires_an_explicit_finite_byte_limit(int limit)
    {
        using var fx = new Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => fx.Prepare(limit: limit));
    }

    [Fact]
    public void Size_overflow_or_unknown_owner_or_private_selection_or_cancellation_has_no_durable_effect()
    {
        using var fx = new Fixture();
        var sequence = fx.Lanes.Store.CurrentSequence(fx.Lanes.Session);
        Assert.Throws<InvalidDataException>(() => fx.Prepare(limit: 1));
        Assert.Throws<InvalidDataException>(() => fx.Prepare(target: fx.Target with { ParentExecutionId = ExecutionId.New() }));
        Assert.Throws<InvalidDataException>(() => fx.Prepare(new ContextInheritancePolicy(new[] { "working-state" })));
        Assert.Throws<OperationCanceledException>(() => fx.Service.PrepareDelegationPacket(fx.Lanes.Session,
            fx.Lanes.Run.RunId, fx.Target, fx.Source, ContextInheritancePolicy.Default, 65536, new CancellationToken(true)));
        Assert.Equal(sequence, fx.Lanes.Store.CurrentSequence(fx.Lanes.Session));
    }

    [Fact]
    public void Selection_is_immutable_and_packet_does_not_claim_to_schedule_or_close_tasks()
    {
        var facts = new[] { new InheritedContextFact("item", ContextItemKind.Decision, "original") };
        using var fx = new Fixture();
        var packet = new DelegationContextPacket(1, fx.Lanes.Session, fx.Lanes.Run.RunId, fx.Target,
            fx.Lanes.Run.RootTask, fx.Lanes.Run.RootLane, fx.Child.Task, fx.Profile, new(fx.Lanes.Session, EventId.New()),
            1, Guid.NewGuid(), 65536, facts);
        facts[0] = facts[0] with { Content = "mutated" };
        Assert.Equal("original", Assert.Single(packet.Facts).Content);
        fx.Record(fx.Prepare());
        var state = CanonicalStateTracker.Replay(fx.Lanes.Codecs, fx.Lanes.Store.ReadFrom(fx.Lanes.Session, 1));
        Assert.Equal(TaskState.Running, state.Task(fx.Child.Task));
        Assert.Equal(TaskState.Running, state.Task(fx.Lanes.Run.RootTask));
        Assert.DoesNotContain(fx.Lanes.Store.ReadFrom(fx.Lanes.Session, 1), evt => fx.Lanes.Codecs.Decode(evt)
            is RunCompleted or TaskCompleted or AgentResultProduced);
    }

    [Fact]
    public void Automatic_inheritance_remains_under_child_budget_and_cannot_evict_current_intent()
    {
        using var fx = new Fixture(string.Join(' ', Enumerable.Repeat("old-parent-context", 1000)));
        fx.Record(fx.Prepare());
        ModelRequest? received = null;
        var explorer = fx.Lanes.Explorer((request, _) => { received = request; return Response(); }, budget: 500);
        Assert.Equal(StopReason.EndTurn, explorer.Ask("CURRENT CHILD INTENT", "system", fx.Lanes.Session,
            fx.Lanes.Run.RunId, fx.Child.Lane, "CURRENT CHILD STATE", TestContext.Current.CancellationToken).StopReason);
        Assert.Contains(received!.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
            text => text.Text == "CURRENT CHILD INTENT");
        Assert.Contains("CURRENT CHILD STATE", received.Instructions);
        var snapshot = fx.Lanes.Store.ReadFrom(fx.Lanes.Session, 1).Select(fx.Lanes.Codecs.Decode)
            .OfType<ModelStepStarted>().Last().ContextSnapshotRef!;
        using var json = JsonDocument.Parse(fx.Lanes.Artifacts.GetText(snapshot.Hash)!);
        Assert.True(json.RootElement.GetProperty("tokenCount").GetInt32() <= 500);
    }

    [Theory]
    [InlineData("media")]
    [InlineData("kind")]
    [InlineData("sensitivity")]
    [InlineData("size")]
    [InlineData("missing")]
    public void Packet_metadata_and_cas_integrity_are_not_assumed_from_the_record(string defect)
    {
        using var fx = new Fixture();
        var prepared = fx.Prepare();
        var bad = prepared.Reference;
        bad = defect switch
        {
            "media" => bad with { MediaType = "application/json" },
            "kind" => bad with { Kind = ArtifactKind.ContextSnapshot },
            "sensitivity" => bad with { Sensitivity = Sensitivity.Normal },
            "size" => bad with { Size = bad.Size + 1 },
            "missing" => bad with { Hash = ContentHash.Sha256(new string('0', 64)) },
            _ => throw new ArgumentException(defect),
        };
        fx.Record(new AlteredArtifact(prepared, bad));
        Assert.Throws<InvalidDataException>(() => fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId));
    }

    [Theory]
    [InlineData("{not-json}")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":1,\"permissions\":\"allow-everything\"}")]
    public void Malformed_or_unsupported_packet_payload_fails_closed(string json)
    {
        using var fx = new Fixture();
        fx.Record(((IArtifactPreparationStore)fx.Lanes.Artifacts).PrepareText(json,
            ContextInheritanceService.DelegationPacketMediaType, ArtifactKind.Other, Sensitivity.Sensitive));
        Assert.Throws<InvalidDataException>(() => fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId));
    }

    [Fact]
    public void Store_without_preparation_support_cannot_fall_back_to_writing_during_preflight()
    {
        using var fx = new Fixture();
        var readOnly = new ReadOnlyArtifacts(fx.Lanes.Artifacts);
        var service = new ContextInheritanceService(fx.Lanes.Store, fx.Lanes.Codecs, readOnly);
        Assert.Throws<NotSupportedException>(() => service.PrepareDelegationPacket(fx.Lanes.Session, fx.Lanes.Run.RunId,
            fx.Target, fx.Source, ContextInheritancePolicy.Default, 65536, TestContext.Current.CancellationToken));
        Assert.Equal(0, readOnly.PutCalls);
    }

    [Fact]
    public void Multiple_accepted_packets_for_one_lane_are_ambiguous_not_merged_or_selected_by_recency()
    {
        using var fx = new Fixture();
        fx.Record(fx.Prepare());
        var second = fx.Target with { DelegationId = DelegationId.New() };
        var prepared = fx.Prepare(target: second);
        using (fx.OwnerScope())
        using (fx.Lanes.Artifacts.AcquirePublicationLease(TestContext.Current.CancellationToken))
        {
            fx.Stream.Append(new DelegationCreated(fx.Owner, new(second.DelegationId, fx.Owner, fx.Child.Task,
                fx.Child.Lane, fx.Profile, prepared.Publish(), second.Relation, second.Supervision)));
            fx.Stream.Append(new DelegationAccepted(fx.Owner, second.DelegationId, fx.ChildExecution));
        }
        Assert.Throws<InvalidDataException>(() => fx.Service.FindDelegationProjection(fx.Lanes.Session,
            fx.Lanes.Run.RunId, fx.Child.Lane));
    }

    private sealed class AlteredArtifact(IPreparedArtifact original, ArtifactRef reference) : IPreparedArtifact
    {
        public ArtifactRef Reference => reference;
        public ArtifactRef Publish() { original.Publish(); return reference; }
    }

    [Fact]
    public void Unattributed_snapshot_with_multiple_parent_executions_is_ambiguous_not_owned_by_a_chosen_start()
    {
        using var fx = new Fixture();
        var rootProfile = fx.Lanes.Store.ReadFrom(fx.Lanes.Session, 1).Select(fx.Lanes.Codecs.Decode)
            .OfType<LaneCreated>().Single(lane => lane.LaneId == fx.Lanes.Run.RootLane).AgentProfile;
        using (fx.OwnerScope()) fx.Stream.Append(new AgentExecutionStarted(ExecutionId.New(), fx.Lanes.Run.RootLane,
            rootProfile, null, ExecutionRelation.Awaited, ExecutionSupervision.Managed));
        Assert.Equal(StopReason.EndTurn, fx.Lanes.Explorer().Ask("new execution's input", "system", fx.Lanes.Session,
            fx.Lanes.Run.RunId, fx.Lanes.Run.RootLane, "state", TestContext.Current.CancellationToken).StopReason);
        var receipt = fx.Lanes.Store.ReadFrom(fx.Lanes.Session, 1)
            .Last(evt => fx.Lanes.Codecs.Decode(evt) is ModelStepStarted);
        Assert.Null(receipt.ExecutionId); // current legacy executor has Lane attribution, not an execution identity
        var source = ((ModelStepStarted)fx.Lanes.Codecs.Decode(receipt)).ContextSnapshotRef!;
        Assert.Throws<InvalidDataException>(() => fx.Service.PrepareDelegationPacket(fx.Lanes.Session,
            fx.Lanes.Run.RunId, fx.Target, source, ContextInheritancePolicy.Default, 65536,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Packet_reader_independently_rejects_ambiguous_execution_source_even_if_a_publisher_bypassed_preflight()
    {
        using var fx = new Fixture();
        var template = fx.Prepare(ContextInheritancePolicy.Default).Publish();
        var packet = JsonSerializer.Deserialize(fx.Lanes.Artifacts.GetText(template.Hash)!,
            DelegationContextJson.Default.DelegationContextPacket)!;
        var rootProfile = fx.Lanes.Store.ReadFrom(fx.Lanes.Session, 1).Select(fx.Lanes.Codecs.Decode)
            .OfType<LaneCreated>().Single(lane => lane.LaneId == fx.Lanes.Run.RootLane).AgentProfile;
        using (fx.OwnerScope()) fx.Stream.Append(new AgentExecutionStarted(ExecutionId.New(), fx.Lanes.Run.RootLane,
            rootProfile, null, ExecutionRelation.Awaited, ExecutionSupervision.Managed));
        Assert.Equal(StopReason.EndTurn, fx.Lanes.Explorer().Ask("new execution's input", "system", fx.Lanes.Session,
            fx.Lanes.Run.RunId, fx.Lanes.Run.RootLane, "state", TestContext.Current.CancellationToken).StopReason);
        var receipt = fx.Lanes.Store.ReadFrom(fx.Lanes.Session, 1)
            .Last(evt => fx.Lanes.Codecs.Decode(evt) is ModelStepStarted);
        var source = ((ModelStepStarted)fx.Lanes.Codecs.Decode(receipt)).ContextSnapshotRef!;
        using var json = JsonDocument.Parse(fx.Lanes.Artifacts.GetText(source.Hash)!);
        packet = packet with { SourceEvent = new(fx.Lanes.Session, receipt.EventId), SourceSequence = receipt.Sequence,
            SourceSnapshotId = json.RootElement.GetProperty("snapshotId").GetGuid() };
        var malformed = ((IArtifactPreparationStore)fx.Lanes.Artifacts).PrepareText(
            JsonSerializer.Serialize(packet, DelegationContextJson.Default.DelegationContextPacket),
            ContextInheritanceService.DelegationPacketMediaType, ArtifactKind.Other, Sensitivity.Sensitive);
        fx.Record(malformed);
        Assert.Throws<InvalidDataException>(() => fx.Service.CreateDelegationProjection(fx.Lanes.Session, fx.Target.DelegationId));
    }

    private sealed class ReadOnlyArtifacts(IArtifactStore original) : IArtifactStore
    {
        public int PutCalls { get; private set; }
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        { PutCalls++; throw new InvalidOperationException("Preflight must not write artifacts."); }
        public string? GetText(ContentHash hash) => original.GetText(hash);
        public bool Verify(ContentHash hash, long expectedSize) => original.Verify(hash, expectedSize);
    }

    private static MaterializeRequest Request(Fixture fx, TaskId task, LaneId lane) => new(fx.Lanes.Session,
        fx.Lanes.Run.RunId, task, lane, TurnId.New(), fx.Lanes.Store.CurrentSequence(fx.Lanes.Session),
        new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture"));
    private static ModelResponse Response() => new(new ContentBlock[] { new TextBlock("fixture answer") },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));

    private sealed class Fixture : IDisposable
    {
        public LaneFixture Lanes { get; } = new();
        public (TaskId Task, LaneId Lane) Child { get; }
        public ProfileId Profile { get; }
        public ExecutionId Owner { get; } = ExecutionId.New();
        public ExecutionId ChildExecution { get; } = ExecutionId.New();
        public DelegationContextTarget Target { get; }
        public ArtifactRef Source { get; }
        public ContextInheritanceService Service => new(Lanes.Store, Lanes.Codecs, Lanes.Artifacts);
        public EventStream Stream => new(Lanes.Store, Lanes.Codecs, Lanes.Session);
        public Fixture(string first = "selected parent fact: español áéíóú ñ ¿qué?")
        {
            Child = Lanes.Child();
            var rootProfile = Lanes.Store.ReadFrom(Lanes.Session, 1).Select(Lanes.Codecs.Decode).OfType<LaneCreated>()
                .Single(lane => lane.LaneId == Lanes.Run.RootLane).AgentProfile;
            Profile = Lanes.Store.ReadFrom(Lanes.Session, 1).Select(Lanes.Codecs.Decode).OfType<LaneCreated>()
                .Single(lane => lane.LaneId == Child.Lane).AgentProfile;
            using (OwnerScope()) Stream.Append(new AgentExecutionStarted(Owner, Lanes.Run.RootLane,
                rootProfile, null, ExecutionRelation.Awaited, ExecutionSupervision.Managed));
            var explorer = Lanes.Explorer();
            foreach (var input in new[] { first, "other parent input" })
                Assert.Equal(StopReason.EndTurn, explorer.Ask(input, "parent system", Lanes.Session, Lanes.Run.RunId,
                    Lanes.Run.RootLane, "parent private state", TestContext.Current.CancellationToken).StopReason);
            Source = Lanes.Store.ReadFrom(Lanes.Session, 1).Select(Lanes.Codecs.Decode).OfType<ModelStepStarted>()
                .Last().ContextSnapshotRef!;
            Target = new(DelegationId.New(), Owner, Child.Lane, ExecutionRelation.Awaited, ExecutionSupervision.Managed);
        }
        public IDisposable OwnerScope() => ExecutionScope.Begin(new(Lanes.Run.RunId, Lanes.Run.RootTask,
            Lanes.Run.RootLane, ExecutionId: Owner));
        public IPreparedArtifact Prepare(ContextInheritancePolicy? policy = null, int limit = 65536,
            DelegationContextTarget? target = null) => Service.PrepareDelegationPacket(Lanes.Session, Lanes.Run.RunId,
                target ?? Target, Source, policy ?? new ContextInheritancePolicy(new[] { "conversation-000000" }),
                limit, TestContext.Current.CancellationToken);
        public ArtifactRef Record(IPreparedArtifact prepared, bool accept = true)
        {
            ArtifactRef reference;
            using (OwnerScope())
            using (Lanes.Artifacts.AcquirePublicationLease(TestContext.Current.CancellationToken))
            {
                reference = prepared.Publish();
                Stream.Append(new DelegationCreated(Owner, new(Target.DelegationId, Owner, Child.Task, Child.Lane,
                    Profile, reference, Target.Relation, Target.Supervision)), DurabilityClass.Barrier);
            }
            if (accept) Accept();
            return reference;
        }
        public void Accept()
        {
            using (ExecutionScope.Begin(new(Lanes.Run.RunId, Child.Task, Child.Lane, ExecutionId: ChildExecution)))
                Stream.Append(new AgentExecutionStarted(ChildExecution, Child.Lane, Profile, Owner, Target.Relation, Target.Supervision));
            using (OwnerScope()) Stream.Append(new DelegationAccepted(Owner, Target.DelegationId, ChildExecution), DurabilityClass.Barrier);
        }
        public void Dispose() => Lanes.Dispose();
    }
}
