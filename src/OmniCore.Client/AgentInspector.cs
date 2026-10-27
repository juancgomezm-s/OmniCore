using System.Globalization;
using System.Text;
using OmniCore.Protocol;

namespace OmniCore.Client;

public enum AgentInspectorStatus { Ready, NoSession, Unavailable, ScopeChanged, Stale, Invalid }

/// <summary>A read-only capture bound to a Session and Run, including an explicit freshness floor.</summary>
public sealed class AgentInspectorProjection(string? sessionId, string? runId)
{
    public const int MaximumLanes = 512;
    public AgentInspectorStatus Status { get; private set; } = sessionId is null ? AgentInspectorStatus.NoSession : AgentInspectorStatus.Unavailable;
    public AgentsSnapshot? Snapshot { get; private set; }
    private long _highWater;

    public void Invalidate(AgentInspectorStatus status = AgentInspectorStatus.Unavailable)
    { Snapshot = null; Status = status; }

    public bool Apply(AgentsSnapshot? snapshot, long minimumSequence = 0)
    {
        Snapshot = null;
        Status = sessionId is null ? AgentInspectorStatus.NoSession : snapshot is null || snapshot.ProjectionUnavailable
            ? AgentInspectorStatus.Unavailable : snapshot.SessionId != sessionId || snapshot.RunId != runId
                ? AgentInspectorStatus.ScopeChanged : snapshot.BasedOnJournalSequence < Math.Max(_highWater, minimumSequence)
                    ? AgentInspectorStatus.Stale : Invalid(snapshot)
                        ? AgentInspectorStatus.Invalid : AgentInspectorStatus.Ready;
        if (Status != AgentInspectorStatus.Ready) return false;
        Snapshot = snapshot; _highWater = snapshot!.BasedOnJournalSequence; return true;
    }

    private static bool Invalid(AgentsSnapshot snapshot) => snapshot.Lanes is null
        || snapshot.Lanes.Any(lane => lane is null || string.IsNullOrEmpty(lane.LaneId)
            || string.IsNullOrEmpty(lane.TaskId) || lane.Objective is null
            || lane.ValidationDebts?.Any(debt => debt is null || debt.MissingChecks is null) == true
            || lane.Transcript?.Any(entry => entry is null
                || entry.RunId is { } run && run != snapshot.RunId || entry.TaskId is { } task && task != lane.TaskId
                || entry.LaneId is { } owner && owner != lane.LaneId
                || entry.ExecutionId is { } execution && execution != lane.ExecutionId) == true)
        || snapshot.Lanes.Select(lane => lane.LaneId).Distinct(StringComparer.Ordinal).Count() != snapshot.Lanes.Count;

    public IReadOnlyList<AgentLaneSnapshot> Search(string query)
    {
        if (Snapshot is null || query.Length > 256) return [];
        return Snapshot.Lanes.Take(MaximumLanes).Where(lane => new[] { lane.Objective, lane.LaneId, lane.TaskId,
                lane.ExecutionId, lane.Model, lane.DelegationId }.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .ToArray();
    }
}

/// <summary>Reports Host facts and their provenance. A returned result never proves integration.</summary>
public static class AgentInspectorPresentation
{
    public static string State(string? value, Localization text) => value is null ? "—"
        : ShellUiResources.ContainsKey("state." + value) ? text.Resolve("state." + value) : value;

    public static string Describe(AgentsSnapshot snapshot, AgentLaneSnapshot lane, Localization text)
    {
        string T(string key) => text.Resolve("agentInspector." + key);
        var result = new StringBuilder();
        var limited = false;
        var full = false;
        string Clip(string value)
        {
            if (value.Length <= 2048) return value;
            limited = true;
            var end = char.IsHighSurrogate(value[2047]) ? 2047 : 2048;
            return value[..end] + "…";
        }
        void Line(string label, string? value)
        {
            if (full) return;
            var clipped = Clip(value ?? "—");
            if (result.Length + label.Length + clipped.Length + 8 > 32768) { full = limited = true; return; }
            result.Append("  ").Append(label).Append(": ").AppendLine(clipped);
        }
        void Section(string title) { if (full) return; if (result.Length > 0) result.AppendLine(); result.AppendLine(T(title)); }
        Section("validation");
        var debts = lane.ValidationDebts ?? [];
        result.AppendLine("  " + T(lane.ExecutionAmbiguous ? "ambiguous" : debts.Count > 0 ? "debt_pending"
            : lane.ParentTaskId is null ? "root_validation" : lane.ValidationDebts is not null && lane.ExecutionId is not null
                && lane.IntegrationVerified && lane.IntegrationValidationPassed && lane.ToolBackedEvidenceCount > 0
                ? "verified" : "unverified"));
        Line(T("linked_receipts"), lane.ToolBackedEvidenceCount.ToString(CultureInfo.InvariantCulture));
        Line(T("debts"), lane.ValidationDebts?.Count.ToString(CultureInfo.InvariantCulture));
        Section("source");
        Line("Session", snapshot.SessionId); Line("Run", snapshot.RunId);
        Line(T("sequence"), snapshot.BasedOnJournalSequence.ToString(CultureInfo.InvariantCulture));
        Section("identity");
        Line("Lane", lane.LaneId); Line("Task", lane.TaskId); Line("Execution", lane.ExecutionId);
        Line(T("lane_state"), State(lane.LaneState, text)); Line(T("execution_state"), State(lane.ExecutionState, text));
        if (lane.ExecutionAmbiguous) result.AppendLine("  " + T("ambiguous"));
        if (lane.ExecutionId is null) result.AppendLine("  " + T("no_execution"));
        Line(T("objective"), lane.Objective); Line(T("model"), lane.Model);
        Section("capacity");
        if (snapshot.Capacity is not { } capacity) result.AppendLine("  " + T("unknown"));
        else
        {
            Line(T("active_maximum"), capacity.Active.ToString(CultureInfo.InvariantCulture) + "/" + capacity.Maximum.ToString(CultureInfo.InvariantCulture));
            Line(T("waiting"), capacity.Waiting.ToString(CultureInfo.InvariantCulture));
            Line(T("writer"), T(capacity.WriterActive ? "yes" : "no"));
        }
        if (lane.LaneState == "Queued") result.AppendLine("  " + text.Resolve("sidebar.queued_no_worker"));
        Section("supervision");
        Line(T("state"), lane.SupervisionState is { } supervision
            && AgentInspectorResources.ContainsKey("supervision." + supervision) ? T("supervision." + supervision) : lane.SupervisionState);
        Line(T("observation"), State(lane.Heartbeat?.State, text));
        Line(T("observation_source"), lane.Heartbeat?.Source);
        Line(T("last_progress"), lane.Heartbeat?.LastProgressAt?.ToString("O", CultureInfo.InvariantCulture));
        if (debts.Count > 0) Section("debt_details");
        foreach (var debt in debts.Take(64))
        {
            Line("  " + T("debt"), debt.DebtId);
            Line("    " + T("level"), AgentInspectorResources.ContainsKey("level." + debt.RequiredLevel) ? T("level." + debt.RequiredLevel) : debt.RequiredLevel);
            foreach (var check in debt.MissingChecks.Take(32)) Line("    " + T("missing_check"), check);
            limited |= debt.MissingChecks.Count > 32;
        }
        limited |= debts.Count > 64;
        Section("reported_result");
        result.AppendLine("  " + T("claim"));
        Line(T("result_id"), lane.ResultId); Line(T("outcome"), State(lane.ResultOutcome, text));
        Line(T("disposition"), State(lane.ResultDisposition, text));
        Line(T("summary"), lane.ResultSummary);
        Section("references");
        var transcript = lane.Transcript ?? [];
        foreach (var entry in transcript.TakeLast(12))
        {
            Line(T("sequence"), entry.Sequence.ToString(CultureInfo.InvariantCulture));
            Line("  Event", entry.EventId); Line("  Execution", entry.ExecutionId); Line("  ToolCall", entry.ToolCallId);
            Line("  " + T("kind"), AgentInspectorResources.ContainsKey("kind." + entry.Kind) ? T("kind." + entry.Kind) : entry.Kind);
        }
        if (transcript.Count == 0) result.AppendLine("  " + T("unknown"));
        limited |= transcript.Count > 12;
        if (limited) result.AppendLine("\n" + T("detail_limited"));
        return result.ToString();
    }
}
