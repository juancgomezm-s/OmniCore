using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;

namespace OmniCore.Host;

public sealed partial class ContextInheritanceService
{
    public const string DelegationPacketMediaType = "application/vnd.omnicore.selected-context+json";
    public const int MaximumDelegationPacketBytes = 1024 * 1024;

    internal IContextContributor? FindDelegationProjection(SessionId session, RunId run, LaneId lane)
    {
        var journal = store.ReadFrom(session, 1);
        if (!journal.Any(evt => evt.RunId == run && codecs.Decode(evt) is DelegationCreated created
            && created.Delegation.ChildLaneId == lane && created.Delegation.PacketRef.MediaType == DelegationPacketMediaType))
            return null;
        var records = PreM6RecordProjection.Replay(session, codecs, journal);
        var candidates = records.Records.Values.Where(history => history.Phase == PreM6RecordPhase.Accepted)
            .SelectMany(history => history.Facts.OfType<DelegationCreated>())
            .Where(created => created.Delegation.ChildLaneId == lane
                && created.Delegation.PacketRef.MediaType == DelegationPacketMediaType).ToArray();
        if (candidates.Length > 1)
            throw new InvalidDataException("A child Lane has multiple active context delegations.");
        return candidates.Length == 0 ? null : CreateDelegationProjection(session, candidates[0].Delegation.DelegationId);
    }

    /// <summary>Prepares redacted immutable bytes without CAS or journal writes. The owning command
    /// must publish under its artifact lease and root the exact ref in DelegationCreated.
    /// The byte limit bounds this payload only; it does not authorize a worker or allocate TaskBudget.</summary>
    public IPreparedArtifact PrepareDelegationPacket(SessionId session, RunId run,
        DelegationContextTarget target, ArtifactRef sourceSnapshot, ContextInheritancePolicy policy,
        int maximumUtf8Bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        if (maximumUtf8Bytes is <= 0 or > MaximumDelegationPacketBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        if (target.DelegationId is null || target.DelegationId.Value == Guid.Empty
            || !Enum.IsDefined(target.Relation) || !Enum.IsDefined(target.Supervision))
            throw new ArgumentException("A valid delegation identity and relation are required.", nameof(target));
        var journal = store.ReadFrom(session, 1);
        var records = PreM6RecordProjection.Replay(session, codecs, journal);
        if (records.Records.ContainsKey("delegation:" + target.DelegationId))
            throw new InvalidDataException("A recorded delegation packet cannot be replaced.");
        var started = journal.FirstOrDefault(evt => codecs.Decode(evt) is AgentExecutionStarted execution
            && execution.ExecutionId == target.ParentExecutionId && evt.RunId == run)
            ?? throw new InvalidDataException("The packet owner has no canonical execution in this Run.");
        var parent = (AgentExecutionStarted)codecs.Decode(started);
        var selection = ResolveSelection(session, run, parent.LaneId, target.ChildLaneId, sourceSnapshot, policy);
        if (selection.Receipt.Sequence < started.Sequence
            || selection.Receipt.ExecutionId is { } sourceOwner && sourceOwner != target.ParentExecutionId)
            throw new InvalidDataException("The context source predates or belongs to another parent execution.");
        ValidateSourceExecution(journal, selection.Receipt, target.ParentExecutionId, parent.LaneId);
        var child = journal.Select(codecs.Decode).OfType<LaneCreated>().Single(lane => lane.LaneId == target.ChildLaneId);
        var packet = new DelegationContextPacket(1, session, run, target, selection.ParentTask,
            parent.LaneId, selection.ChildTask, child.AgentProfile, new(session, selection.Receipt.EventId),
            selection.Receipt.Sequence, selection.SnapshotId, maximumUtf8Bytes, selection.Facts);
        var text = JsonSerializer.Serialize(packet, DelegationContextJson.Default.DelegationContextPacket);
        if (Encoding.UTF8.GetByteCount(text) > maximumUtf8Bytes)
            throw new InvalidDataException("Selected context exceeds the explicit packet byte limit.");
        var preparation = artifacts as IArtifactPreparationStore
            ?? throw new NotSupportedException("Delegation packets require side-effect-free artifact preparation.");
        var prepared = preparation.PrepareText(text, DelegationPacketMediaType, ArtifactKind.Other, Sensitivity.Sensitive);
        if (prepared.Reference.Size > maximumUtf8Bytes)
            throw new InvalidDataException("Redacted context exceeds the explicit packet byte limit.");
        cancellationToken.ThrowIfCancellationRequested();
        return prepared;
    }

    /// <summary>Reads the accepted delegation's exact canonical PacketRef after restart, verifies
    /// packet/source/lineage, and returns data only for that child's materialization scope.
    /// This does not admit, schedule, wake, accept a result or grant authority to any worker.</summary>
    public IContextContributor CreateDelegationProjection(SessionId session, DelegationId delegationId)
    {
        var journal = store.ReadFrom(session, 1);
        var records = PreM6RecordProjection.Replay(session, codecs, journal);
        if (!records.Records.TryGetValue("delegation:" + delegationId, out var history)
            || history.Phase != PreM6RecordPhase.Accepted)
            throw new InvalidDataException("The delegation has no active canonical acceptance.");
        var created = history.Facts.OfType<DelegationCreated>().Single();
        var delegation = created.Delegation;
        var root = journal.First(evt => codecs.Decode(evt) is DelegationCreated fact && fact == created);
        var reference = delegation.PacketRef;
        if (reference.Kind != ArtifactKind.Other || reference.MediaType != DelegationPacketMediaType
            || reference.Sensitivity != Sensitivity.Sensitive || reference.Size > MaximumDelegationPacketBytes
            || !artifacts.Verify(reference.Hash, reference.Size))
            throw new InvalidDataException("The canonical delegation packet is invalid or unavailable.");
        DelegationContextPacket packet;
        try
        {
            packet = JsonSerializer.Deserialize(artifacts.GetText(reference.Hash)
                ?? throw new InvalidDataException("The delegation packet is missing."),
                DelegationContextJson.Default.DelegationContextPacket)
                ?? throw new InvalidDataException("The delegation packet is empty.");
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("The delegation packet is malformed.", failure); }
        var target = new DelegationContextTarget(delegation.DelegationId, delegation.ParentExecutionId,
            delegation.ChildLaneId, delegation.Relation, delegation.Supervision);
        if (packet.SchemaVersion != 1 || packet.SessionId != session || packet.RunId != root.RunId
            || packet.Target != target || packet.ParentTaskId != root.TaskId || packet.ParentLaneId != root.LaneId
            || packet.ChildTaskId != delegation.ChildTaskId || packet.ChildProfileId != delegation.ProfileId
            || packet.SourceEvent is null || packet.SourceEvent.SessionId != session
            || packet.SourceSequence >= root.Sequence || packet.SourceSequence <= 0
            || packet.MaximumUtf8Bytes is <= 0 or > MaximumDelegationPacketBytes
            || reference.Size > packet.MaximumUtf8Bytes || packet.Facts.Any(fact => fact is null))
            throw new InvalidDataException("The packet body does not match its canonical delegation scope.");
        var source = journal.SingleOrDefault(evt => evt.EventId == packet.SourceEvent.EventId
            && evt.Sequence == packet.SourceSequence && evt.RunId == packet.RunId);
        if (source is null || codecs.Decode(source) is not ModelStepStarted { ContextSnapshotRef: { } snapshot })
            throw new InvalidDataException("The packet source is not a canonical context snapshot.");
        Selection selection;
        try
        {
            selection = ResolveSelection(session, packet.RunId, packet.ParentLaneId, target.ChildLaneId, snapshot,
                new ContextInheritancePolicy(packet.Facts.Select(fact => fact.SourceItemId).ToArray()));
        }
        catch (ArgumentException failure)
        { throw new InvalidDataException("The packet selection is malformed.", failure); }
        var owner = journal.First(evt => codecs.Decode(evt) is AgentExecutionStarted execution
            && execution.ExecutionId == target.ParentExecutionId);
        if (owner.Sequence > source.Sequence || source.ExecutionId is { } executionId && executionId != target.ParentExecutionId
            || selection.Receipt.EventId != source.EventId || selection.SnapshotId != packet.SourceSnapshotId
            || !selection.Facts.SequenceEqual(packet.Facts))
            throw new InvalidDataException("The packet facts differ from the selected canonical parent context.");
        ValidateSourceExecution(journal, source, target.ParentExecutionId, packet.ParentLaneId);
        var acceptance = journal.First(evt => codecs.Decode(evt) is DelegationAccepted accepted
            && accepted.DelegationId == delegationId);
        return new AcceptedPacketContributor(selection.Contributor, acceptance.Sequence, () =>
            PreM6RecordProjection.Replay(session, codecs, store.ReadFrom(session, 1)).Records
                .TryGetValue("delegation:" + delegationId, out var current) && current.Phase == PreM6RecordPhase.Accepted);
    }

    private void ValidateSourceExecution(IReadOnlyList<DomainEvent> journal, DomainEvent source,
        ExecutionId owner, LaneId parentLane)
    {
        if (source.ExecutionId is { } attributed)
        {
            if (attributed != owner) throw new InvalidDataException("The source execution is not the packet owner.");
            return;
        }
        // A legacy null can identify the owner only if it was the sole execution ever
        // started in this Lane through the receipt. Never infer from the latest start.
        var candidates = journal.Where(evt => evt.RunId == source.RunId && evt.Sequence <= source.Sequence)
            .Select(codecs.Decode).OfType<AgentExecutionStarted>().Where(execution => execution.LaneId == parentLane)
            .Select(execution => execution.ExecutionId).Distinct().ToArray();
        if (candidates.Length != 1 || candidates[0] != owner)
            throw new InvalidDataException("The legacy context source has ambiguous execution ownership.");
    }

    private sealed class AcceptedPacketContributor(IContextContributor selected, long acceptedThrough,
        Func<bool> remainsAccepted) : IContextContributor
    {
        public System.Threading.Tasks.Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return request.BasedOnEventSequence < acceptedThrough || !remainsAccepted()
                ? System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>())
                : selected.GetContextAsync(request, cancellationToken);
        }
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DelegationContextPacket))]
internal sealed partial class DelegationContextJson : JsonSerializerContext;
