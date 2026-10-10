using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;
using OmniCore.Tools;

namespace OmniCore.Host;

/// <summary>CAS/journal-only read model. Never reads the live workspace or attributes Git/process changes.</summary>
internal static class ChangedFilesReader
{
    private sealed record Effect(ToolCallStarted Start, DomainEvent Envelope,
        FilesystemReconciliationMetadata? Metadata, ToolCallSucceeded? Success, string Outcome);

    private static IReadOnlyList<Effect> Effects(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs, SessionId session)
    {
        var effects = new Dictionary<ToolCallId, Effect>();
        foreach (var envelope in events.Where(e => e.SessionId == session))
        {
            if (!envelope.Type.ToString().StartsWith("toolcall.", StringComparison.Ordinal)) continue;
            var payload = codecs.Decode(envelope);
            if (payload is ToolCallStarted start && start.TargetRef is { } target && envelope.RunId is not null)
            {
                var metadata = FilesystemReconciliationMetadata.Parse(start.ReconciliationJson);
                // A single explicit write claim is required. Legacy/invalid paths are never treated as filesystem targets.
                if (!FilesystemReconciliationMetadata.IsSaneRelativePath(target)) continue;
                if (metadata is not null && metadata.Path.Replace('\\', '/') != target.Replace('\\', '/')) metadata = null;
                effects[start.ToolCallId] = new(start, envelope, metadata, null, "Unknown");
                continue;
            }
            var id = payload switch
            {
                ToolCallSucceeded e => e.ToolCallId, ToolCallFailed e => e.ToolCallId,
                ToolCallEffectUnknown e => e.ToolCallId, ToolCallReconciled e => e.ToolCallId, _ => null,
            };
            if (id is null || !effects.TryGetValue(id, out var effect)) continue;
            // Completion from a different scope cannot claim this effect.
            if (envelope.RunId != effect.Envelope.RunId || envelope.LaneId != effect.Envelope.LaneId
                || envelope.TaskId != effect.Envelope.TaskId || envelope.TurnId != effect.Envelope.TurnId
                || envelope.ExecutionId != effect.Envelope.ExecutionId) continue;
            effects[id] = payload switch
            {
                ToolCallSucceeded e => effect with { Success = e, Outcome = "Applied" },
                ToolCallFailed { EffectOutcome: EffectOutcome.None } => effect with { Outcome = "None" },
                ToolCallFailed { EffectOutcome: EffectOutcome.Applied } => effect with { Outcome = "Applied" },
                ToolCallReconciled { Outcome: ReconciliationOutcome.NotApplied } => effect with { Outcome = "None" },
                ToolCallReconciled { Outcome: ReconciliationOutcome.Applied } => effect with { Outcome = "Applied" },
                _ => effect with { Outcome = "Unknown" },
            };
        }
        return effects.Values.Where(e => e.Outcome != "None").OrderBy(e => e.Envelope.Sequence).ToArray();
    }

    private static FileDiffSnapshot Diff(Effect effect, SessionId session, IArtifactStore? artifacts)
    {
        var path = OmniCliRuntime.RedactSensitive(effect.Start.TargetRef!);
        FileDiffSnapshot Unavailable(string reason) => new(session.ToString(), effect.Start.ToolCallId.ToString(), path, false, reason, null, null);
        if (effect.Outcome != "Applied") return Unavailable("EffectUnknown");
        if (effect.Metadata is null) return Unavailable("MetadataUnavailable");
        if (artifacts is null || effect.Start.BeforeStateRef is null
            || effect.Start.BeforeStateRef.MediaType != FilesystemPreimage.MediaType) return Unavailable("PreimageUnavailable");
        if (effect.Success?.AfterStateRef is null || effect.Success.AfterStateRef.MediaType != FilesystemPreimage.PostMediaType)
            return Unavailable("PostimageUnavailable");
        try
        {
            if (effect.Start.BeforeStateRef.Size > 12 * 1024 * 1024 || effect.Success.AfterStateRef.Size > 12 * 1024 * 1024)
                return Unavailable("PreviewTooLarge");
            var beforeBytes = FilesystemPreimage.Read(artifacts, effect.Start.BeforeStateRef);
            var afterBytes = FilesystemPreimage.Read(artifacts, effect.Success.AfterStateRef);
            if ((beforeBytes is null ? FilesystemReconciliationMetadata.AbsentPreHash : FileVersion.VersionToken(beforeBytes)) != effect.Metadata.ExpectedPreHash
                || afterBytes is null || FileVersion.VersionToken(afterBytes) != effect.Metadata.ExpectedPostHash)
                return Unavailable("ImageHashMismatch");
            var before = beforeBytes is null ? "" : FileVersion.Decode(beforeBytes).Text;
            var after = FileVersion.Decode(afterBytes).Text;
            // Stored evidence is re-redacted with the current policy; raw source never crosses Protocol.
            return new(session.ToString(), effect.Start.ToolCallId.ToString(), path, true, null,
                OmniCliRuntime.RedactSensitive(before), OmniCliRuntime.RedactSensitive(after));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException
            or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        { return Unavailable("ImageUnavailable"); }
    }

    private sealed record BatchDiff(string Id, string Path, string? RunId, string? LaneId,
        byte[]? Before, byte[]? After, bool Available, string? Reason);

    // Batch integration has one journal effect for many paths. The manifest is revalidated by
    // WorkspaceSnapshotStore, the same authority used by undo; raw protected JSON never crosses
    // the protocol boundary. Row IDs include the path so each diff remains addressable.
    private static IReadOnlyList<BatchDiff> BatchDiffs(IReadOnlyList<DomainEvent> events,
        IEventCodecRegistry codecs, SessionId session, IArtifactStore? artifacts)
    {
        if (artifacts is null) return [];
        var decoded = events.Select(item => (Envelope: item, Payload: codecs.Decode(item))).ToArray();
        var roots = decoded.Select(item => item.Payload).OfType<WorkspaceRootEstablished>()
            .Where(item => item.SessionId == session).ToArray();
        if (roots.Length != 1 || string.IsNullOrWhiteSpace(roots[0].DurableIdentity)) return [];
        WorkspaceSnapshotStore snapshots;
        try { snapshots = new WorkspaceSnapshotStore(roots[0].CanonicalRoot,
            new SnapshotEventStore(events), codecs, artifacts, roots[0].DurableIdentity, verifyPhysicalRoot: false); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException) { return []; }
        var starts = decoded.Where(item => item.Payload is ToolCallStarted)
            .ToDictionary(item => ((ToolCallStarted)item.Payload).ToolCallId);
        var result = new List<BatchDiff>();
        foreach (var item in decoded.Where(item => item.Payload is WorktreeIntegrationStateRecorded
                     { State: WorktreeIntegrationState.Completed }))
        {
            var state = (WorktreeIntegrationStateRecorded)item.Payload;
            if (!starts.TryGetValue(state.ToolCallId, out var start) || !SameScope(start.Envelope, item.Envelope)) continue;
            foreach (var entry in snapshots.BuildPlan(session, state.ToolCallId, includeCompensated: true).Entries.Where(row => row.State == "Ready"))
            {
                var diffId = entry.OriginalEffect + ":" + entry.RelativePath;
                result.Add(new(diffId, OmniCliRuntime.RedactSensitive(entry.RelativePath),
                    start.Envelope.RunId?.ToString(), start.Envelope.LaneId?.ToString(), entry.RestoreBytes,
                    entry.PostimageBytes, true, null));
            }
        }
        return result;
    }

    private sealed class SnapshotEventStore(IReadOnlyList<DomainEvent> events) : IEventStore
    {
        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public long CurrentSequence(SessionId sessionId) => events.Where(item => item.SessionId == sessionId)
            .Select(item => item.Sequence).DefaultIfEmpty().Max();
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) => events
            .Where(item => item.SessionId == sessionId && item.Sequence >= fromSequenceInclusive).ToArray();
    }

    private static bool SameScope(DomainEvent left, DomainEvent right) => left.RunId == right.RunId
        && left.TaskId == right.TaskId && left.LaneId == right.LaneId
        && left.TurnId == right.TurnId && left.ExecutionId == right.ExecutionId;

    public static ChangedFilesSnapshot Read(IEventStore store, IEventCodecRegistry codecs, SessionId session, IArtifactStore? artifacts)
    {
        var all = store.ReadFrom(session, 1).Where(e => e.SessionId == session).ToArray();
        try
        {
            var rows = Effects(all, codecs, session).Select(effect =>
            {
                var diff = Diff(effect, session, artifacts);
                int? added = null, deleted = null;
                if (diff.Available) { FileVersion.ChangedLines(diff.Before!, diff.After!, out var d, out var a); added = a; deleted = d; }
                return new ChangedFile(effect.Start.ToolCallId.ToString(), diff.Path, effect.Envelope.RunId?.ToString(),
                    effect.Envelope.LaneId?.ToString(), effect.Outcome == "Unknown" || effect.Metadata is null ? "?"
                        : effect.Metadata?.ExpectedPreHash == FilesystemReconciliationMetadata.AbsentPreHash ? "A" : "M",
                    added, deleted, diff.Available, diff.Reason);
            }).Concat(BatchDiffs(all, codecs, session, artifacts).Select(diff =>
            {
                int? added = null, deleted = null;
                var before = diff.Before is null ? "" : FileVersion.Decode(diff.Before).Text;
                var after = diff.After is null ? "" : FileVersion.Decode(diff.After).Text;
                if (diff.Available) { FileVersion.ChangedLines(before, after, out var d, out var a); added = a; deleted = d; }
                return new ChangedFile(diff.Id, diff.Path, diff.RunId, diff.LaneId,
                    diff.Before is null ? "A" : diff.After is null ? "D" : "M", added, deleted, diff.Available, diff.Reason);
            })).ToArray();
            return new(session.ToString(), all.LastOrDefault()?.Sequence ?? 0, rows);
        }
        catch (Exception exception) when (Unreadable(exception))
        { return new(session.ToString(), all.LastOrDefault()?.Sequence ?? 0, [], true); }
    }

    public static FileDiffSnapshot ReadDiff(IEventStore store, IEventCodecRegistry codecs, SessionId session, string id, IArtifactStore? artifacts)
    {
        try
        {
            var all = store.ReadFrom(session, 1);
            var effect = Effects(all, codecs, session).FirstOrDefault(e => e.Start.ToolCallId.ToString() == id);
            if (effect is not null) return Diff(effect, session, artifacts);
            var batch = BatchDiffs(all, codecs, session, artifacts).FirstOrDefault(item => item.Id == id);
            if (batch is not null)
            {
                if (!batch.Available) return new(session.ToString(), id, batch.Path, false, batch.Reason, null, null);
                var before = batch.Before is null ? "" : FileVersion.Decode(batch.Before).Text;
                var after = batch.After is null ? "" : FileVersion.Decode(batch.After).Text;
                return new(session.ToString(), id, batch.Path, true, null,
                    OmniCliRuntime.RedactSensitive(before), OmniCliRuntime.RedactSensitive(after));
            }
            return new(session.ToString(), id, "", false, "EffectNotFound", null, null);
        }
        catch (Exception exception) when (Unreadable(exception))
        { return new(session.ToString(), id, "", false, "JournalUnavailable", null, null); }
    }

    private static bool Unreadable(Exception exception) => exception is InvalidOperationException
        or ArgumentException or System.Text.Json.JsonException or NotSupportedException;
}
