using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Protocol;

namespace OmniCore.Host;

/// <summary>
/// Deterministic in-process supervision over the ordinary IOmniClient boundary. It reads the
/// Host's event/query views and submits an explicit disposition command. It has no model, tool,
/// filesystem, or trusted-user-action capability.
/// </summary>
internal sealed class InProcessSupervisor(IOmniClient client, ExecutionId supervisorExecutionId)
{
    internal void AcknowledgeBinding(ExecutionId childExecutionId, BindingId bindingId,
        CancellationToken cancellationToken = default)
    {
        var command = WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "supervisor.binding.accept")
            + "," + JsonObj.Field("executionId", childExecutionId.ToString())
            + "," + JsonObj.Field("bindingId", bindingId.ToString()) + "}");
        var ack = client.Send(command, cancellationToken);
        if (ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted)
            throw new InvalidOperationException(ack.Error ?? ack.Outcome?.Reason ?? "Supervisor binding handshake failed.");
    }

    internal int ReviewReturnedResults(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = client.Query("agents", cancellationToken)
            ?? throw new InvalidOperationException("Supervisor agent query is unavailable.");
        var snapshot = AgentsJson.Decode(query.Json)
            ?? throw new InvalidDataException("Supervisor received an invalid lane snapshot.");
        var resultFacts = new HashSet<(string ExecutionId, string ResultId)>(StringTupleComparer.Instance);
        foreach (var envelope in client.SubscribeSince(1))
        {
            if (envelope.MessageType != MessageTypes.Event) continue;
            using var evt = JsonDocument.Parse(envelope.PayloadJson);
            var root = evt.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "agent_result.produced"
                || !root.TryGetProperty("executionId", out var execution)
                || !root.TryGetProperty("resultId", out var result)) continue;
            resultFacts.Add((execution.GetString() ?? "", result.GetString() ?? ""));
        }

        var reviewed = 0;
        foreach (var lane in snapshot.Lanes.Where(lane => lane.ParentExecutionId == supervisorExecutionId.ToString()
            && lane.ExecutionState == "Completed" && lane.ResultId is not null && lane.ResultDisposition is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resultFacts.Contains((lane.ExecutionId ?? "", lane.ResultId!)) || lane.ResultIssueCount is null)
                continue;
            var gatesReady = lane.IntegrationVerified && lane.IntegrationValidationPassed
                && lane.ToolBackedEvidenceCount > 0;
            var outcome = lane.ResultIssueCount == 0 && gatesReady
                ? ResultDispositionOutcome.Accepted : ResultDispositionOutcome.ReworkRequested;
            var reason = outcome == ResultDispositionOutcome.Accepted
                ? "Integración y validación quedaron verificadas con recibos de herramientas."
                : lane.ResultIssueCount > 0
                    ? $"ReworkRequested: el resultado conserva {lane.ResultIssueCount} incidencia(s) pendiente(s)."
                    : "ReworkRequested: faltan integración verificada y validación con recibos tool-backed del mismo Task/Lane.";
            var command = WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "supervisor.result.disposition")
                + "," + JsonObj.Field("executionId", lane.ExecutionId!) + "," + JsonObj.Field("resultId", lane.ResultId!)
                + "," + JsonObj.Field("outcome", outcome.ToString()) + "," + JsonObj.Field("reason", reason) + "}");
            var ack = client.Send(command, cancellationToken);
            if (ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted) reviewed++;
            else if (ack.Outcome?.Kind is RuntimeCommandOutcomeKind.Rejected or null)
                throw new InvalidOperationException(ack.Error ?? ack.Outcome?.Reason ?? "Supervisor disposition was not accepted.");
        }
        return reviewed;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ExecutionId, string ResultId)>
    {
        internal static StringTupleComparer Instance { get; } = new();
        public bool Equals((string ExecutionId, string ResultId) x, (string ExecutionId, string ResultId) y)
            => string.Equals(x.ExecutionId, y.ExecutionId, StringComparison.Ordinal)
                && string.Equals(x.ResultId, y.ResultId, StringComparison.Ordinal);
        public int GetHashCode((string ExecutionId, string ResultId) value)
            => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.ExecutionId), StringComparer.Ordinal.GetHashCode(value.ResultId));
    }
}
