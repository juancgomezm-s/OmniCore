namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Infrastructure;

/// <summary>Observes this invocation's new canonical receipts. No model text/arguments are retained.
/// An argument repair means a later model step succeeds on a tool whose typed arguments failed.</summary>
internal sealed class RuntimeCalibrationObserver : IDisposable
{
    private readonly OmniServer _server;
    private readonly ModelQualificationSnapshot? _qualification;
    private readonly string? _dataDirectory;
    private readonly SessionId _session;
    private readonly RunId _run;
    private readonly LaneId _lane;
    private readonly RouteId? _route;
    private readonly long _first;

    internal RuntimeCalibrationObserver(OmniServer server, ModelQualificationSnapshot? qualification, string? dataDirectory,
        SessionId session, RunId run, LaneId lane, RouteId? route)
    {
        _server = server; _qualification = qualification; _dataDirectory = dataDirectory; _session = session; _run = run; _lane = lane;
        _route = route;
        _first = server.AcquireStore().ReadFrom(session, 1).LastOrDefault()?.Sequence + 1 ?? 1;
    }

    public void Dispose()
    {
        if (_qualification is null) return;
        try
        {
            var codecs = _server.AcquireCodecs();
            var events = _server.AcquireStore().ReadFrom(_session, 1).Where(e => e.RunId == _run && e.LaneId == _lane).OrderBy(e => e.Sequence).ToArray();
            var observations = Collect(events.Select(e => (e, codecs.Decode(e))).ToArray(), _qualification.Key.ModelId, _first, _route);
            if (observations.Count == 0) return;
            using var store = OmniHost.CreateModelQualificationStore(_dataDirectory);
            store.RecordRuntimeCalibration(_qualification.Key, observations, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
            or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        { /* Telemetry failure cannot repeat a provider call or weaken the operation's outcome. */ }
    }

    internal static IReadOnlyList<RuntimeCalibrationObservation> Collect(
        IReadOnlyList<(DomainEvent Event, DomainEventPayload Payload)> events, string modelId, long first, RouteId? route = null)
    {
        List<RuntimeCalibrationObservation> result = [];
        Dictionary<ToolCallId, (string Name, TurnId? Turn, int Step)> calls = [];
        Dictionary<TurnId, (string Model, int Step, RouteId? Route)> steps = [];
        Dictionary<(TurnId?, string), int> failures = [];
        foreach (var (evt, payload) in events.OrderBy(item => item.Event.Sequence))
        {
            if (payload is ModelStepStarted start) steps[start.TurnId] = (start.ModelId, start.StepIndex, start.RouteId);
            var turn = evt.TurnId;
            var current = turn is not null && steps.TryGetValue(turn, out var active) ? active : default;
            if (current.Model != modelId || route is not null && !route.Equals(current.Route)) continue;
            void Count(string counter) { if (evt.Sequence >= first) result.Add(new(evt.EventId.ToString(), counter)); }
            switch (payload)
            {
                case ModelStepCompleted: Count("model_steps"); break;
                case ToolCallRequested request when request.ParentToolCallId is null
                    && !request.ProviderCallId.StartsWith("native-action-", StringComparison.Ordinal):
                    calls[request.ToolCallId] = (request.ToolName, turn, current.Step); Count("tool_calls"); break;
                case ToolCallRejected rejected when rejected.ErrorCode == ToolErrorCode.InvalidArguments && calls.TryGetValue(rejected.ToolCallId, out var call):
                    failures[(call.Turn, call.Name)] = call.Step; Count("invalid_tool_arguments"); break;
                case ToolCallPrepared prepared when calls.ContainsKey(prepared.ToolCallId): Count("valid_tool_arguments"); break;
                case ToolCallSucceeded succeeded when calls.TryGetValue(succeeded.ToolCallId, out var call):
                    Count("tool_successes");
                    if (failures.TryGetValue((call.Turn, call.Name), out var failedStep) && call.Step > failedStep)
                    { Count("argument_repairs"); failures.Remove((call.Turn, call.Name)); }
                    break;
                case ProgressStalled: Count("progress_stalls"); break;
                case RunValidationRejected: Count("completion_gate_rejections"); break;
            }
        }
        return result;
    }
}