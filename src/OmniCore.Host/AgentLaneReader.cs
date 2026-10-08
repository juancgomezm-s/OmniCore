using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;
using System.Text.Json;

namespace OmniCore.Host;

internal static class AgentLaneReader
{
    internal static AgentsSnapshot Read(IEventStore store, IEventCodecRegistry codecs, SessionId session, RunId? run, IArtifactStore? artifacts = null)
    {
        var journal = store.ReadFrom(session, 1);
        var empty = new AgentsSnapshot(session.ToString(), run?.ToString(), journal.LastOrDefault()?.Sequence ?? 0,
            false, Array.Empty<AgentLaneSnapshot>());
        if (run is null) return empty;
        try
        {
            var records = PreM6RecordProjection.Replay(session, codecs, journal);
            var own = journal.Where(e => e.RunId == run).ToArray();
            if (!own.Any(e => codecs.Decode(e) is RunCreated)) return empty with { ProjectionUnavailable = true };
            var payloads = own.Select(codecs.Decode).ToArray();
            var tasks = TaskGraphProjection.Replay(codecs, own);
            var lanes = LaneProjection.Replay(codecs, own);
            var rows = new List<AgentLaneSnapshot>();
            foreach (var lane in payloads.OfType<LaneCreated>())
            {
                var task = tasks.Get(lane.TaskId) ?? throw new InvalidDataException("Lane Task is missing.");
                var starts = payloads.OfType<AgentExecutionStarted>().Where(e => e.LaneId == lane.LaneId).Distinct().ToArray();
                // Never choose the latest of multiple executors by chronology.
                var execution = starts.Length == 1 ? starts[0] : null;
                var history = records.Records.Values.SingleOrDefault(h => h.Facts.OfType<DelegationCreated>()
                    .Any(d => d.Delegation.ChildLaneId == lane.LaneId));
                var delegation = history?.Facts.OfType<DelegationCreated>().Single().Delegation;
                var step = own.LastOrDefault(e => e.LaneId == lane.LaneId && codecs.Decode(e) is ModelStepStarted);
                var state = execution is null ? null : payloads.Any(e => e is AgentExecutionFailed f && f.ExecutionId == execution.ExecutionId)
                    ? "Failed" : payloads.Any(e => e is AgentExecutionCompleted c && c.ExecutionId == execution.ExecutionId)
                        ? "Completed" : "Started";
                var definition = payloads.OfType<TaskCreated>().Single(t => t.TaskId == lane.TaskId);
                var result = execution is null ? null : payloads.OfType<AgentResultProduced>().LastOrDefault(e => e.ExecutionId == execution.ExecutionId);
                var disposition = result is null ? null : payloads.OfType<ResultDispositionRecorded>()
                    .LastOrDefault(e => e.Disposition.ResultRef == result.ResultRef)?.Disposition.Outcome.ToString();
                string? summary = null;
                if (result is { ResultSchemaId: "core.explorer.v1" } && artifacts?.GetText(result.ResultRef.Hash) is { } text)
                {
                    var document = JsonSerializer.Deserialize(text, DelegationResultJson.Default.DelegationResultDocument);
                    if (document?.Version == 1)
                    { summary = new PiiRedactor().Redact(document.Summary); if (summary.Length > 4096) summary = summary[..4096] + "…"; }
                }
                var joins = execution is null ? [] : records.Records.Values.Where(h => h.OwnerExecutionId == execution.ExecutionId
                    && h.Phase == PreM6RecordPhase.Created).SelectMany(h => h.Facts.OfType<ExecutionJoinCreated>())
                    .Select(e => e.Join.JoinId.ToString()).ToArray();
                rows.Add(new(lane.LaneId.ToString(), lane.TaskId.ToString(), definition.ParentTaskId?.ToString(),
                    new PiiRedactor().Redact(task.Objective), lanes.StateOf(lane.LaneId)!.Value.ToString(), task.State.ToString(),
                    lane.AgentProfile.ToString(), lane.AgentProfileRevision, execution?.ExecutionId.ToString(),
                    execution?.ParentExecutionId?.ToString(), state, delegation?.DelegationId.ToString(),
                    history?.Phase.ToString(), step is null ? null : ((ModelStepStarted)codecs.Decode(step)).ModelId,
                    step is null || ((ModelStepStarted)codecs.Decode(step)).ContextSnapshotRef is null ? null : step.EventId.ToString(),
                    starts.Length > 1, step is null ? null : ContextInheritanceService.SelectableItems(artifacts, codecs, step),
                    result?.ResultRef.Id.ToString(), disposition, summary, joins,
                    history?.Facts.OfType<DelegationCancellationRequested>().Any() == true));
            }
            return empty with { Lanes = rows.ToArray() };
        }
        catch (Exception failure) when (failure is InvalidDataException or InvalidStateTransitionException or InvalidOperationException or JsonException)
        {
            return empty with { ProjectionUnavailable = true };
        }
    }
}
