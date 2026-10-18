namespace OmniCore.Engine;

using OmniCore.Domain;

/// <summary>
/// Extracts <see cref="ArtifactRef"/> references from typed domain event payloads for envelope indexing.
/// This is a pure, deterministic function with no side effects. It uses explicit pattern matching
/// over the known canonical event types (ADR-0013) to avoid arbitrary JSON scanning.
///
/// Deduplication rule: references are deduplicated by <see cref="ArtifactRef.Id"/> (stable artifact
/// identity) preserving the order of first occurrence. This matches the envelope's purpose of
/// providing an index without parsing the payload (ADR-0001 §3).
/// </summary>
internal static class ArtifactRefExtractor
{
    /// <summary>
    /// Extracts all artifact references from a domain event payload.
    /// Returns an empty list for events that carry no artifact references.
    /// </summary>
    public static IReadOnlyList<ArtifactRef> Extract(DomainEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var refs = new List<ArtifactRef>();

        // Add references in a deterministic order based on the event type.
        // For events with multiple ref fields, the order follows the field declaration order.
        switch (payload)
        {
            case IPreM6ContractEvent record:
                AddRange(refs, record.RecordArtifacts());
                break;
            case FanOutGroupResolved resolved:
                AddRange(refs, resolved.MemberResultRefs);
                if (resolved.AggregateRef is { } aggregate) refs.Add(aggregate);
                break;
            // Run/Task events
            case RunSummaryRecorded summary:
                refs.Add(summary.SummaryArtifact);
                break;
            case RunValidationRejected r when r.OutputArtifacts is not null:
                AddRange(refs, r.OutputArtifacts);
                break;

            case TaskCompleted t when t.Result is not null:
                AddRange(refs, t.Result.ArtifactRefs);
                break;

            case LaneCompleted l when l.Result is not null:
                AddRange(refs, l.Result.ArtifactRefs);
                break;

            case LaneWorktreeCreated w:
                refs.Add(w.TaskPacketRef);
                refs.Add(w.SelectedProjectionRef);
                break;

            case LaneWorktreeBound w:
                refs.Add(w.TaskPacketRef);
                refs.Add(w.SelectedProjectionRef);
                break;

            case ExternalLaneUsageRecorded external:
                refs.Add(external.ResultRef);
                break;

            case LaneAcceptancePending acceptance:
                refs.Add(acceptance.ResultRef);
                break;

            // Lane/Turn events
            case TurnStarted t:
                if (t.ContextSnapshotRef is not null) refs.Add(t.ContextSnapshotRef);
                foreach (var component in t.Fingerprint?.Components ?? Array.Empty<FingerprintComponent>())
                    if (component.Content is not null) refs.Add(component.Content);
                break;

            case ModelCompleted m when m.ResponseArtifact is not null:
                refs.Add(m.ResponseArtifact!);
                break;

            case ModelStepStarted m when m.ContextSnapshotRef is not null:
                refs.Add(m.ContextSnapshotRef!);
                break;

            case ModelStepCompleted m when m.ResponseArtifact is not null:
                refs.Add(m.ResponseArtifact!);
                break;

            // Global interaction events
            case ToolCallStarted t when t.BeforeStateRef is not null:
                refs.Add(t.BeforeStateRef);
                break;

            case ToolCallSucceeded t:
                if (t.AfterStateRef is not null) refs.Add(t.AfterStateRef);
                if (t.ResultArtifact is not null) refs.Add(t.ResultArtifact);
                break;

            case UserInputReceived u:
                if (u.ContentRef is not null) refs.Add(u.ContentRef);
                if (u.Attachments is not null) AddRange(refs, u.Attachments);
                break;

            case NativeCommandInvocationAdmitted command:
                refs.Add(command.ProtectedPayload);
                break;

            case AssistantMessageRecorded a when a.ContentRef is not null:
                refs.Add(a.ContentRef!);
                break;
            case AssistantImagesRecorded images:
                foreach (var image in images.Images) refs.Add(image.Artifact);
                if (images.OrderedParts is not null)
                    foreach (var part in images.OrderedParts) refs.Add(part.Artifact);
                break;

            case InteractionRequested i when i.QuestionnaireSchemaRef is not null:
                refs.Add(i.QuestionnaireSchemaRef!);
                break;

            case InteractionResolved i when i.AnswerRef is not null:
                refs.Add(i.AnswerRef!);
                break;

            // Context/Checkpoint events
            case ContextCheckpointRecorded c:
                refs.Add(c.CheckpointArtifact);
                break;

            case MetaModelInvocationStarted m:
                refs.Add(m.InputArtifact);
                break;

            case MetaModelInvocationCompleted m:
                refs.Add(m.OutputArtifact);
                break;

            // No artifact references in other event types
            default:
                break;
        }

        // Deduplicate by ArtifactId (stable identity) preserving first occurrence order.
        // ArtifactRef is a record with ArtifactId as primary identity component.
        return DeduplicateById(refs);
    }

    private static void AddRange(List<ArtifactRef> target, IReadOnlyList<ArtifactRef> source)
    {
        if (source is not null)
        {
            foreach (var r in source)
            {
                target.Add(r);
            }
        }
    }

    private static IReadOnlyList<ArtifactRef> DeduplicateById(List<ArtifactRef> refs)
    {
        if (refs.Count <= 1)
        {
            return refs;
        }

        var seen = new HashSet<ArtifactId>();
        var result = new List<ArtifactRef>(refs.Count);

        foreach (var r in refs)
        {
            if (seen.Add(r.Id))
            {
                result.Add(r);
            }
        }

        return result;
    }
}
