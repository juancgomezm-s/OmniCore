namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Actividad derivada de una Lane (ADR-0036 §3): se calcula de otros eventos y nunca se persiste
/// (INV-027). Prioridad cuando coinciden varias: permiso pendiente, input del usuario, tool en
/// curso, modelo en curso, estancada.
/// <list type="bullet">
/// <item><c>WaitingForModel</c>: un Turn de la Lane empezó y el modelo no ha respondido.</item>
/// <item><c>WaitingForTool</c>: una ToolCall pedida en el Turn abierto está <c>Started</c> sin outcome.</item>
/// <item><c>WaitingForPermission</c>: <c>InteractionRequested</c> de tipo Permission de la Lane sin resolver.</item>
/// <item><c>WaitingForInput</c>: <c>RunAwaitingInput</c> con esta Lane como raíz, sin input posterior.</item>
/// <item><c>Stalled</c>: <c>ProgressStalled</c> sin una señal de progreso posterior (ADR-0036 §7).</item>
/// </list>
/// <c>WaitingForSubtask</c> y <c>Validating</c> llegan con las Tasks hijas y los gates de Lane.
/// </summary>
public static class LaneActivityProjection
{
    public static LaneActivity Derive(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events, LaneId lane)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(lane);

        var laneState = (LaneState?) null;
        TurnId? openTurn = null;
        var modelResponded = false;
        var toolCallsInTurn = new HashSet<ToolCallId>();
        var runningTools = new HashSet<ToolCallId>();
        var pendingPermissions = new HashSet<InteractionId>();
        RunId? awaitingRun = null;
        var stalled = false;

        foreach (var evt in events)
        {
            var payload = codecs.Decode(evt);
            switch (payload)
            {
                case LaneCreated created when created.LaneId.Equals(lane):
                    laneState = LaneState.Queued;
                    break;
                case LaneStarted e when e.LaneId.Equals(lane): laneState = LaneState.Running; break;
                case LaneBlocked e when e.LaneId.Equals(lane): laneState = LaneState.Blocked; break;
                case LaneUnblocked e when e.LaneId.Equals(lane): laneState = LaneState.Running; break;
                case LaneCompleted e when e.LaneId.Equals(lane): laneState = LaneState.Completed; break;
                case LaneFailed e when e.LaneId.Equals(lane): laneState = LaneState.Failed; break;
                case LaneCancelled e when e.LaneId.Equals(lane): laneState = LaneState.Cancelled; break;

                case TurnStarted started when started.LaneId.Equals(lane):
                    openTurn = started.TurnId;
                    modelResponded = false;
                    toolCallsInTurn.Clear();
                    runningTools.Clear();
                    break;
                case ModelCompleted e when e.TurnId.Equals(openTurn): modelResponded = true; break;
                case TurnCompleted e when e.TurnId.Equals(openTurn): openTurn = null; break;
                case TurnInterrupted e when e.TurnId.Equals(openTurn): openTurn = null; break;
                case TurnAbandoned e when e.TurnId.Equals(openTurn): openTurn = null; break;

                case ToolCallRequested requested when openTurn is not null:
                    toolCallsInTurn.Add(requested.ToolCallId);
                    // Una tool pedida implica que el modelo ya respondió en este paso del Turn.
                    modelResponded = true;
                    break;
                case ToolCallStarted e when toolCallsInTurn.Contains(e.ToolCallId): runningTools.Add(e.ToolCallId); break;
                case ToolCallSucceeded e: runningTools.Remove(e.ToolCallId); break;
                case ToolCallFailed e: runningTools.Remove(e.ToolCallId); break;
                case ToolCallEffectUnknown e: runningTools.Remove(e.ToolCallId); break;
                case ToolCallReconciled e: runningTools.Remove(e.ToolCallId); break;

                case InteractionRequested requested
                    when requested.Kind == InteractionKind.Permission && lane.Equals(requested.Lane):
                    pendingPermissions.Add(requested.InteractionId);
                    break;
                case InteractionResolved e: pendingPermissions.Remove(e.InteractionId); break;
                case InteractionExpired e: pendingPermissions.Remove(e.InteractionId); break;

                case RunAwaitingInput awaiting when awaiting.RootLaneId.Equals(lane):
                    awaitingRun = awaiting.RunId;
                    break;
                case UserInputReceived input when input.RunId.Equals(awaitingRun): awaitingRun = null; break;
                case RunCancelled e when e.RunId.Equals(awaitingRun): awaitingRun = null; break;
                case RunFailed e when e.RunId.Equals(awaitingRun): awaitingRun = null; break;

                case ProgressStalled: stalled = true; break;
                // Señales de progreso (ADR-0036 §7): transición de item/Task o efecto aplicado.
                case PlanItemStarted or PlanItemCompleted or PlanItemBlocked or PlanItemUnblocked
                    or PlanItemFailed or TaskCompleted or TaskFailed or TaskBlocked:
                    stalled = false;
                    break;
            }
        }

        if (laneState is null || StateMachines.IsLaneTerminal(laneState.Value))
        {
            return LaneActivity.None;
        }

        if (pendingPermissions.Count > 0) return LaneActivity.WaitingForPermission;
        if (awaitingRun is not null) return LaneActivity.WaitingForInput;
        if (runningTools.Count > 0) return LaneActivity.WaitingForTool;
        if (openTurn is not null && !modelResponded) return LaneActivity.WaitingForModel;
        if (stalled) return LaneActivity.Stalled;
        return LaneActivity.None;
    }
}
