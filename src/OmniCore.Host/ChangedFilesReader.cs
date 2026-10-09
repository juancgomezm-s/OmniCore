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
            }).ToArray();
            return new(session.ToString(), all.LastOrDefault()?.Sequence ?? 0, rows);
        }
        catch (Exception exception) when (Unreadable(exception))
        { return new(session.ToString(), all.LastOrDefault()?.Sequence ?? 0, [], true); }
    }

    public static FileDiffSnapshot ReadDiff(IEventStore store, IEventCodecRegistry codecs, SessionId session, string id, IArtifactStore? artifacts)
    {
        try
        {
            var effect = Effects(store.ReadFrom(session, 1), codecs, session).FirstOrDefault(e => e.Start.ToolCallId.ToString() == id);
            return effect is null ? new(session.ToString(), id, "", false, "EffectNotFound", null, null) : Diff(effect, session, artifacts);
        }
        catch (Exception exception) when (Unreadable(exception))
        { return new(session.ToString(), id, "", false, "JournalUnavailable", null, null); }
    }

    private static bool Unreadable(Exception exception) => exception is InvalidOperationException
        or ArgumentException or System.Text.Json.JsonException or NotSupportedException;
}
