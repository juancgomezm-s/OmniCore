namespace OmniCore.Infrastructure;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Registro de codecs de eventos con discriminador por EventType (ADR-0013 §2). Cada evento
/// canónico se serializa a JSON con su record concreto; no se usa polimorfismo implícito. Los
/// upcasters están vacíos en M1.
/// </summary>
public sealed class EventCodecs : IEventCodecRegistry
{
    private readonly Dictionary<EventType, IDomainEventCodec> _byType = new();

    private EventCodecs() { }

    public static EventCodecs Create() =>
        new EventCodecs()
            .Plus(Typed.RunCreated())
            .Plus(Typed.RunStarted())
            .Plus(Typed.RunAwaitingInput())
            .Plus(Typed.UserInputReceived())
            .Plus(Typed.AssistantMessageRecorded())
            .Plus(Typed.RunValidationStarted())
            .Plus(Typed.RunValidationRejected())
            .Plus(Typed.RunCompleted())
            .Plus(Typed.RunFailed())
            .Plus(Typed.RunCancelled())
            .Plus(Typed.RunModeChanged())
            .Plus(Typed.SessionCreated())
            .Plus(Typed.InteractionRequested())
            .Plus(Typed.InteractionResolved())
            .Plus(Typed.InteractionExpired())
            .Plus(Typed.ProgressStalled())
            .Plus(Typed.TaskCreated())
            .Plus(Typed.TaskReady())
            .Plus(Typed.TaskStarted())
            .Plus(Typed.TaskBlocked())
            .Plus(Typed.TaskUnblocked())
            .Plus(Typed.TaskCompleted())
            .Plus(Typed.TaskFailed())
            .Plus(Typed.TaskSkipped())
            .Plus(Typed.TaskCancelled())
            .Plus(Typed.LaneCreated())
            .Plus(Typed.LaneProvisioning())
            .Plus(Typed.LaneStarted())
            .Plus(Typed.LaneBlocked())
            .Plus(Typed.LaneUnblocked())
            .Plus(Typed.LaneCompleted())
            .Plus(Typed.LaneFailed())
            .Plus(Typed.LaneCancelled())
            .Plus(Typed.TurnStarted())
            .Plus(Typed.ModelCompleted())
            .Plus(Typed.TurnCompleted())
            .Plus(Typed.TurnInterrupted())
            .Plus(Typed.TurnAbandoned())
            .Plus(Typed.ToolCallRequested())
            .Plus(Typed.ToolCallPrepared())
            .Plus(Typed.ToolCallRejected())
            .Plus(Typed.PermissionEvaluated())
            .Plus(Typed.PermissionRequested())
            .Plus(Typed.PermissionGranted())
            .Plus(Typed.PermissionDenied())
            .Plus(Typed.ToolCallAuthorized())
            .Plus(Typed.ToolCallStarted())
            .Plus(Typed.ToolCallSucceeded())
            .Plus(Typed.ToolCallFailed())
            .Plus(Typed.ToolCallEffectUnknown())
            .Plus(Typed.ToolCallReconciled())
            .Plus(Typed.ToolCallCancelled())
            .Plus(Typed.PlanCreated())
            .Plus(Typed.PlanRevised())
            .Plus(Typed.PlanItemAdded())
            .Plus(Typed.PlanItemUpdated())
            .Plus(Typed.PlanItemStarted())
            .Plus(Typed.PlanItemReady())
            .Plus(Typed.PlanItemBlocked())
            .Plus(Typed.PlanItemUnblocked())
            .Plus(Typed.PlanItemCompleted())
            .Plus(Typed.PlanItemFailed())
            .Plus(Typed.PlanItemSkipped())
            .Plus(Typed.PlanItemCancelled())
            .Plus(Typed.PlanItemReopened())
            .Plus(Typed.PlanItemReordered())
            .Plus(Typed.PlanItemLinked())
            .Plus(Typed.PlanItemUnlinked())
            .Plus(Typed.PlanMutationRejected());

    public IDomainEventCodec CodecFor(EventType type)
    {
        if (!_byType.TryGetValue(type, out var found))
        {
            throw new UnknownEventTypeException(type.ToString());
        }

        return found!;
    }

    private EventCodecs Plus(Typed.CodecPair pair)
    {
        _byType[pair.Type] = pair.Codec;
        return this;
    }
}

/// <summary>Se desconoce un EventType persistido (evento de una versión futura sin upcaster).</summary>
public sealed class UnknownEventTypeException : InvalidOperationException
{
    public string EventType { get; }

    public UnknownEventTypeException(string type)
    {
        EventType = type;
    }
}

/// <summary>Codec por tipo concreto; captura solo el payload tipado.</summary>
public sealed class TypedCodec : IDomainEventCodec
{
    private readonly EventType _type;

    private readonly Func<string, DomainEventPayload> _decode;

    private readonly Func<DomainEventPayload, string> _encode;

    private TypedCodec(EventType type, Func<string, DomainEventPayload> decode,
        Func<DomainEventPayload, string> encode)
    {
        _type = type;
        _decode = decode;
        _encode = encode;
    }

    public static TypedCodec Of(EventType type, Func<string, DomainEventPayload> decode,
        Func<DomainEventPayload, string> encode) => new TypedCodec(type, decode, encode);

    public DomainEventPayload Decode(EventType type, string payloadJson)
    {
        try
        {
            return _decode(payloadJson);
        }
        catch (Exception ex)
        {
            throw new EventParseException(type.ToString(), ex.Message ?? "decode error");
        }
    }

    public string Encode(DomainEventPayload payload) => _encode(payload);

    public string DebugType() => _type.ToString();
}

/// <summary>Serializa un payload concreto a string JSON; el usuario del codec conoce el tipo.</summary>
public sealed class EventJson
{
    public static string Serialize(DomainEventPayload payload) =>
        new string(ToUtf8(JsonSerializer.SerializeToUtf8Bytes(payload)));

    public static char[] ToUtf8(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char) (bytes[i] & 0xFF);
        }

        return chars;
    }
}

/// <summary>Un payload no pudo deserializarse a su tipo concreto (schema futuro sin upcaster).</summary>
public sealed class EventParseException : InvalidOperationException
{
    public string EventType { get; }

    public string? Detail { get; }

    public EventParseException(string type, string detail)
    {
        EventType = type;
        Detail = detail;
    }
}

/// <summary>Fábrica de pares EventType → codec para todos los eventos canónicos.</summary>
public sealed class Typed
{
    public sealed class CodecPair
    {
        public EventType Type { get; }

        public IDomainEventCodec Codec { get; }

        public CodecPair(EventType type, IDomainEventCodec codec)
        {
            Type = type;
            Codec = codec;
        }
    }

    private static CodecPair Of(EventType type, Func<string, DomainEventPayload> decode,
        Func<DomainEventPayload, string> encode) =>
        new CodecPair(type, TypedCodec.Of(type, decode, encode));

    public static CodecPair RunCreated() =>
        Of(EventType.Of("run.created"),
            json => (RunCreated) (JsonSerializer.Deserialize<RunCreated>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunCreated) payload))));

    public static CodecPair RunStarted() =>
        Of(EventType.Of("run.started"),
            json => (RunStarted) (JsonSerializer.Deserialize<RunStarted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunStarted) payload))));

    public static CodecPair RunAwaitingInput() =>
        Of(EventType.Of("run.awaiting_input"),
            json => (RunAwaitingInput) (JsonSerializer.Deserialize<RunAwaitingInput>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunAwaitingInput) payload))));

    public static CodecPair UserInputReceived() =>
        Of(EventType.Of("user_input.received"),
            json => (UserInputReceived) (JsonSerializer.Deserialize<UserInputReceived>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((UserInputReceived) payload))));

    public static CodecPair AssistantMessageRecorded() =>
        Of(EventType.Of("assistant_message.recorded"),
            json => (AssistantMessageRecorded) (JsonSerializer.Deserialize<AssistantMessageRecorded>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((AssistantMessageRecorded) payload))));

    public static CodecPair RunValidationStarted() =>
        Of(EventType.Of("run.validation_started"),
            json => (RunValidationStarted) (JsonSerializer.Deserialize<RunValidationStarted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunValidationStarted) payload))));

    public static CodecPair RunValidationRejected() =>
        Of(EventType.Of("run.validation_rejected"),
            json => (RunValidationRejected) (JsonSerializer.Deserialize<RunValidationRejected>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunValidationRejected) payload))));

    public static CodecPair RunCompleted() =>
        Of(EventType.Of("run.completed"),
            json => (RunCompleted) (JsonSerializer.Deserialize<RunCompleted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunCompleted) payload))));

    public static CodecPair RunFailed() =>
        Of(EventType.Of("run.failed"),
            json => (RunFailed) (JsonSerializer.Deserialize<RunFailed>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunFailed) payload))));

    public static CodecPair RunCancelled() =>
        Of(EventType.Of("run.cancelled"),
            json => (RunCancelled) (JsonSerializer.Deserialize<RunCancelled>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunCancelled) payload))));

    public static CodecPair RunModeChanged() =>
        Of(EventType.Of("run.mode_changed"),
            json => (RunModeChanged) (JsonSerializer.Deserialize<RunModeChanged>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((RunModeChanged) payload))));

    public static CodecPair SessionCreated() =>
        Of(EventType.Of("session.created"),
            json => (SessionCreated) (JsonSerializer.Deserialize<SessionCreated>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((SessionCreated) payload))));

    public static CodecPair InteractionRequested() =>
        Of(EventType.Of("interaction.requested"),
            json => (InteractionRequested) (JsonSerializer.Deserialize<InteractionRequested>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((InteractionRequested) payload))));

    public static CodecPair InteractionResolved() =>
        Of(EventType.Of("interaction.resolved"),
            json => (InteractionResolved) (JsonSerializer.Deserialize<InteractionResolved>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((InteractionResolved) payload))));

    public static CodecPair InteractionExpired() =>
        Of(EventType.Of("interaction.expired"),
            json => (InteractionExpired) (JsonSerializer.Deserialize<InteractionExpired>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((InteractionExpired) payload))));

    public static CodecPair ProgressStalled() =>
        Of(EventType.Of("progress.stalled"),
            json => (ProgressStalled) (JsonSerializer.Deserialize<ProgressStalled>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ProgressStalled) payload))));

    public static CodecPair TaskCreated() =>
        Of(EventType.Of("task.created"),
            json => (TaskCreated) (JsonSerializer.Deserialize<TaskCreated>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskCreated) payload))));

    public static CodecPair TaskReady() =>
        Of(EventType.Of("task.ready"),
            json => (TaskReady) (JsonSerializer.Deserialize<TaskReady>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskReady) payload))));

    public static CodecPair TaskStarted() =>
        Of(EventType.Of("task.started"),
            json => (TaskStarted) (JsonSerializer.Deserialize<TaskStarted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskStarted) payload))));

    public static CodecPair TaskBlocked() =>
        Of(EventType.Of("task.blocked"),
            json => (TaskBlocked) (JsonSerializer.Deserialize<TaskBlocked>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskBlocked) payload))));

    public static CodecPair TaskUnblocked() =>
        Of(EventType.Of("task.unblocked"),
            json => (TaskUnblocked) (JsonSerializer.Deserialize<TaskUnblocked>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskUnblocked) payload))));

    public static CodecPair TaskCompleted() =>
        Of(EventType.Of("task.completed"),
            json => (TaskCompleted) (JsonSerializer.Deserialize<TaskCompleted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskCompleted) payload))));

    public static CodecPair TaskFailed() =>
        Of(EventType.Of("task.failed"),
            json => (TaskFailed) (JsonSerializer.Deserialize<TaskFailed>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskFailed) payload))));

    public static CodecPair TaskSkipped() =>
        Of(EventType.Of("task.skipped"),
            json => (TaskSkipped) (JsonSerializer.Deserialize<TaskSkipped>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskSkipped) payload))));

    public static CodecPair TaskCancelled() =>
        Of(EventType.Of("task.cancelled"),
            json => (TaskCancelled) (JsonSerializer.Deserialize<TaskCancelled>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TaskCancelled) payload))));

    public static CodecPair LaneCreated() =>
        Of(EventType.Of("lane.created"),
            json => (LaneCreated) (JsonSerializer.Deserialize<LaneCreated>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((LaneCreated) payload))));

    public static CodecPair LaneProvisioning() =>
        Of(EventType.Of("lane.provisioning"),
            json => (LaneProvisioning) (JsonSerializer.Deserialize<LaneProvisioning>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((LaneProvisioning) payload))));

    public static CodecPair LaneStarted() =>
        Of(EventType.Of("lane.started"),
            json => (LaneStarted) (JsonSerializer.Deserialize<LaneStarted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((LaneStarted) payload))));

    public static CodecPair LaneBlocked() =>
        Of(EventType.Of("lane.blocked"),
            json => (LaneBlocked) (JsonSerializer.Deserialize<LaneBlocked>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((LaneBlocked) payload))));

    public static CodecPair LaneUnblocked() =>
        Of(EventType.Of("lane.unblocked"),
            json => (LaneUnblocked) (JsonSerializer.Deserialize<LaneUnblocked>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((LaneUnblocked) payload))));

    public static CodecPair LaneCompleted() =>
        Of(EventType.Of("lane.completed"),
            json => (LaneCompleted) (JsonSerializer.Deserialize<LaneCompleted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((LaneCompleted) payload))));

    public static CodecPair LaneFailed() =>
        Of(EventType.Of("lane.failed"),
            json => (LaneFailed) (JsonSerializer.Deserialize<LaneFailed>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((LaneFailed) payload))));

    public static CodecPair LaneCancelled() =>
        Of(EventType.Of("lane.cancelled"),
            json => (LaneCancelled) (JsonSerializer.Deserialize<LaneCancelled>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((LaneCancelled) payload))));

    public static CodecPair TurnStarted() =>
        Of(EventType.Of("turn.started"),
            json => (TurnStarted) (JsonSerializer.Deserialize<TurnStarted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TurnStarted) payload))));

    public static CodecPair ModelCompleted() =>
        Of(EventType.Of("model.completed"),
            json => (ModelCompleted) (JsonSerializer.Deserialize<ModelCompleted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ModelCompleted) payload))));

    public static CodecPair TurnCompleted() =>
        Of(EventType.Of("turn.completed"),
            json => (TurnCompleted) (JsonSerializer.Deserialize<TurnCompleted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TurnCompleted) payload))));

    public static CodecPair TurnInterrupted() =>
        Of(EventType.Of("turn.interrupted"),
            json => (TurnInterrupted) (JsonSerializer.Deserialize<TurnInterrupted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TurnInterrupted) payload))));

    public static CodecPair TurnAbandoned() =>
        Of(EventType.Of("turn.abandoned"),
            json => (TurnAbandoned) (JsonSerializer.Deserialize<TurnAbandoned>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((TurnAbandoned) payload))));

    public static CodecPair ToolCallRequested() =>
        Of(EventType.Of("toolcall.requested"),
            json => (ToolCallRequested) (JsonSerializer.Deserialize<ToolCallRequested>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallRequested) payload))));

    public static CodecPair ToolCallPrepared() =>
        Of(EventType.Of("toolcall.prepared"),
            json => (ToolCallPrepared) (JsonSerializer.Deserialize<ToolCallPrepared>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallPrepared) payload))));

    public static CodecPair ToolCallRejected() =>
        Of(EventType.Of("toolcall.rejected"),
            json => (ToolCallRejected) (JsonSerializer.Deserialize<ToolCallRejected>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallRejected) payload))));

    public static CodecPair PermissionEvaluated() =>
        Of(EventType.Of("toolcall.permission_evaluated"),
            json => (PermissionEvaluated) (JsonSerializer.Deserialize<PermissionEvaluated>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PermissionEvaluated) payload))));

    public static CodecPair PermissionRequested() =>
        Of(EventType.Of("toolcall.permission_requested"),
            json => (PermissionRequested) (JsonSerializer.Deserialize<PermissionRequested>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PermissionRequested) payload))));

    public static CodecPair PermissionGranted() =>
        Of(EventType.Of("toolcall.permission_granted"),
            json => (PermissionGranted) (JsonSerializer.Deserialize<PermissionGranted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PermissionGranted) payload))));

    public static CodecPair PermissionDenied() =>
        Of(EventType.Of("toolcall.permission_denied"),
            json => (PermissionDenied) (JsonSerializer.Deserialize<PermissionDenied>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PermissionDenied) payload))));

    public static CodecPair ToolCallAuthorized() =>
        Of(EventType.Of("toolcall.authorized"),
            json => (ToolCallAuthorized) (JsonSerializer.Deserialize<ToolCallAuthorized>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallAuthorized) payload))));

    public static CodecPair ToolCallStarted() =>
        Of(EventType.Of("toolcall.started"),
            json => (ToolCallStarted) (JsonSerializer.Deserialize<ToolCallStarted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallStarted) payload))));

    public static CodecPair ToolCallSucceeded() =>
        Of(EventType.Of("toolcall.succeeded"),
            json => (ToolCallSucceeded) (JsonSerializer.Deserialize<ToolCallSucceeded>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallSucceeded) payload))));

    public static CodecPair ToolCallFailed() =>
        Of(EventType.Of("toolcall.failed"),
            json => (ToolCallFailed) (JsonSerializer.Deserialize<ToolCallFailed>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallFailed) payload))));

    public static CodecPair ToolCallEffectUnknown() =>
        Of(EventType.Of("toolcall.effect_unknown"),
            json => (ToolCallEffectUnknown) (JsonSerializer.Deserialize<ToolCallEffectUnknown>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallEffectUnknown) payload))));

    public static CodecPair ToolCallReconciled() =>
        Of(EventType.Of("toolcall.reconciled"),
            json => (ToolCallReconciled) (JsonSerializer.Deserialize<ToolCallReconciled>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallReconciled) payload))));

    public static CodecPair ToolCallCancelled() =>
        Of(EventType.Of("toolcall.cancelled"),
            json => (ToolCallCancelled) (JsonSerializer.Deserialize<ToolCallCancelled>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((ToolCallCancelled) payload))));

    public static CodecPair PlanCreated() =>
        Of(EventType.Of("plan.created"),
            json => (PlanCreated) (JsonSerializer.Deserialize<PlanCreated>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanCreated) payload))));

    public static CodecPair PlanRevised() =>
        Of(EventType.Of("plan.revised"),
            json => (PlanRevised) (JsonSerializer.Deserialize<PlanRevised>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanRevised) payload))));

    public static CodecPair PlanItemAdded() =>
        Of(EventType.Of("plan_item.added"),
            json => (PlanItemAdded) (JsonSerializer.Deserialize<PlanItemAdded>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemAdded) payload))));

    public static CodecPair PlanItemUpdated() =>
        Of(EventType.Of("plan_item.updated"),
            json => (PlanItemUpdated) (JsonSerializer.Deserialize<PlanItemUpdated>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemUpdated) payload))));

    public static CodecPair PlanItemStarted() =>
        Of(EventType.Of("plan_item.started"),
            json => (PlanItemStarted) (JsonSerializer.Deserialize<PlanItemStarted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemStarted) payload))));

    public static CodecPair PlanItemReady() =>
        Of(EventType.Of("plan_item.ready"),
            json => (PlanItemReady) (JsonSerializer.Deserialize<PlanItemReady>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemReady) payload))));

    public static CodecPair PlanItemBlocked() =>
        Of(EventType.Of("plan_item.blocked"),
            json => (PlanItemBlocked) (JsonSerializer.Deserialize<PlanItemBlocked>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemBlocked) payload))));

    public static CodecPair PlanItemUnblocked() =>
        Of(EventType.Of("plan_item.unblocked"),
            json => (PlanItemUnblocked) (JsonSerializer.Deserialize<PlanItemUnblocked>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemUnblocked) payload))));

    public static CodecPair PlanItemCompleted() =>
        Of(EventType.Of("plan_item.completed"),
            json => (PlanItemCompleted) (JsonSerializer.Deserialize<PlanItemCompleted>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemCompleted) payload))));

    public static CodecPair PlanItemFailed() =>
        Of(EventType.Of("plan_item.failed"),
            json => (PlanItemFailed) (JsonSerializer.Deserialize<PlanItemFailed>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemFailed) payload))));

    public static CodecPair PlanItemSkipped() =>
        Of(EventType.Of("plan_item.skipped"),
            json => (PlanItemSkipped) (JsonSerializer.Deserialize<PlanItemSkipped>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemSkipped) payload))));

    public static CodecPair PlanItemCancelled() =>
        Of(EventType.Of("plan_item.cancelled"),
            json => (PlanItemCancelled) (JsonSerializer.Deserialize<PlanItemCancelled>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemCancelled) payload))));

    public static CodecPair PlanItemReopened() =>
        Of(EventType.Of("plan_item.reopened"),
            json => (PlanItemReopened) (JsonSerializer.Deserialize<PlanItemReopened>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemReopened) payload))));

    public static CodecPair PlanItemReordered() =>
        Of(EventType.Of("plan_item.reordered"),
            json => (PlanItemReordered) (JsonSerializer.Deserialize<PlanItemReordered>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemReordered) payload))));

    public static CodecPair PlanItemLinked() =>
        Of(EventType.Of("plan_item.linked"),
            json => (PlanItemLinked) (JsonSerializer.Deserialize<PlanItemLinked>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemLinked) payload))));

    public static CodecPair PlanItemUnlinked() =>
        Of(EventType.Of("plan_item.unlinked"),
            json => (PlanItemUnlinked) (JsonSerializer.Deserialize<PlanItemUnlinked>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanItemUnlinked) payload))));

    public static CodecPair PlanMutationRejected() =>
        Of(EventType.Of("plan_mutation.rejected"),
            json => (PlanMutationRejected) (JsonSerializer.Deserialize<PlanMutationRejected>(json) ?? throw new FormatException("null")),
            payload => new string(EventJson.ToUtf8(JsonSerializer.SerializeToUtf8Bytes((PlanMutationRejected) payload))));
}