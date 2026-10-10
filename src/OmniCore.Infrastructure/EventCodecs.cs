namespace OmniCore.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Registro de codecs de eventos con discriminador por EventType (ADR-0013 §2). Cada evento
/// canónico se serializa a JSON con su record concreto, sin polimorfismo implícito ni reflexión
/// (contexto generado en compilación). Al leer se aplican los upcasters de la versión persistida
/// a la actual.
/// </summary>
public sealed class EventCodecs : IEventCodecRegistry
{
    private readonly Dictionary<EventType, IDomainEventCodec> _byType = new();

    private readonly Dictionary<EventType, int> _versions = new();

    private readonly Dictionary<(EventType Type, int From), IEventUpcaster> _upcasters = new();

    private EventCodecs() { }

    public static EventCodecs Create() =>
        new EventCodecs()
            .Plus(Typed.RunCreated())
            .Plus(Typed.RunStarted())
            .Plus(Typed.RunAwaitingInput())
            .Plus(Typed.UserInputReceived())
            .Plus(Typed.FollowUpQueued())
            .Plus(Typed.FollowUpPromoted())
            .Plus(Typed.TurnSteeringReceived())
            .Plus(Typed.TurnSteeringApplied())
            .Plus(Typed.TurnSteeringDropped())
            .Plus(Typed.AssistantMessageRecorded())
            .Plus(Typed.RunValidationStarted())
            .Plus(Typed.RunValidationRejected())
            .Plus(Typed.PostEditValidationPending())
            .Plus(Typed.PostEditValidationConsumed())
            .Plus(Typed.RunCompleted())
            .Plus(Typed.RunSummaryRecorded())
            .Plus(Typed.RunFailed())
            .Plus(Typed.RunCancelled())
            .Plus(Typed.RunModeChanged())
            .Plus(Typed.RunModeProposed())
            .Plus(Typed.RunModeAuthoritySelected())
            .Plus(Typed.RunModeTransitionAuthorized())
            .Plus(Typed.RunModeAuthorityRevoked())
            .Plus(Typed.RunReasoningPreferenceSelected())
            .Plus(Typed.RunReasoningPreferenceRevoked())
            .Plus(Typed.RunInteractionResumed())
            .Plus(Typed.SessionCreated())
            .Plus(Typed.SessionRoutingPolicySet())
            .Plus(Typed.SessionRoutingPolicyRevised())
            .Plus(Typed.WorkspaceRootEstablished())
            .Plus(Typed.InteractionRequested())
            .Plus(Typed.InteractionResolved())
            .Plus(Typed.InteractionExpired())
            .Plus(Typed.ProgressStalled())
            .Plus(Typed.StallResponseSelected())
            .Plus(Typed.TaskCreated())
            .Plus(Typed.AgentExecutionStarted())
            .Plus(Typed.AgentExecutionCompleted())
            .Plus(Typed.AgentExecutionFailed())
            .Plus(Typed.DelegationCreated())
            .Plus(Typed.DelegationAccepted())
            .Plus(Typed.DelegationReturned())
            .Plus(Typed.DelegationFailed())
            .Plus(Typed.DelegationCancellationRequested())
            .Plus(Typed.ExecutionJoinCreated())
            .Plus(Typed.ExecutionJoinResolved())
            .Plus(Typed.ExecutionJoinFailed())
            .Plus(Typed.FanOutGroupCreated())
            .Plus(Typed.FanOutGroupMemberReplaced())
            .Plus(Typed.FanOutGroupResolved())
            .Plus(Typed.SupervisionBindingCreated())
            .Plus(Typed.SupervisionBindingAccepted())
            .Plus(Typed.SupervisionBindingFailed())
            .Plus(Typed.ExecutionMailboxCreated())
            .Plus(Typed.ExecutionMailboxMessageReceived())
            .Plus(Typed.ExecutionMailboxMessageAcknowledged())
            .Plus(Typed.WakeRequestCreated())
            .Plus(Typed.WakeRequestAccepted())
            .Plus(Typed.WakeRequestResolved())
            .Plus(Typed.WakeRequestFailed())
            .Plus(Typed.AgentResultProduced())
            .Plus(Typed.ResultDispositionRecorded())
            .Plus(Typed.ValidationStateRecorded())
            .Plus(Typed.ValidationDebtCreated())
            .Plus(Typed.ValidationDebtResolved())
            .Plus(Typed.IntegrationStatusRecorded())
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
            .Plus(Typed.ModelEscalationRequested())
            .Plus(Typed.ModelEscalationApproved())
            .Plus(Typed.ModelEscalationCompleted())
            .Plus(Typed.ModelStepStarted())
            .Plus(Typed.ModelStepNotDispatched())
            .Plus(Typed.ModelStepCompleted())
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
            .Plus(Typed.PlanMutationRejected())
            .Plus(Typed.ContextCheckpointRecorded())
            .Plus(Typed.MetaModelInvocationStarted())
            .Plus(Typed.MetaModelInvocationCompleted())
            .Plus(Typed.MetaModelInvocationFailed())
            .Plus(Typed.MetaModelInvocationNotDispatched())
            // v1 → v2 añadieron un campo opcional: upcaster trivial (ADR-0013, tabla de cambios).
            .WithUpcaster(new IdentityUpcaster(EventType.Of("user_input.received"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("workspace.root_established"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("interaction.requested"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("interaction.resolved"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("toolcall.started"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("toolcall.started"), 2))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("toolcall.succeeded"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("turn.started"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("turn.started"), 2))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("lane.created"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("model_step.started"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("model_step.started"), 2))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("model_step.started"), 3))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("model_step.completed"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("model_step.completed"), 2))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("meta_model.invocation_completed"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("meta_model.invocation_completed"), 2))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("meta_model.invocation_failed"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("meta_model.invocation_failed"), 2))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("toolcall.reconciled"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("toolcall.failed"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("toolcall.rejected"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("task.created"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("model.escalation_requested"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("model.escalation_approved"), 1))
            .WithUpcaster(new IdentityUpcaster(EventType.Of("model.escalation_completed"), 1));

    public IDomainEventCodec CodecFor(EventType type)
    {
        if (!_byType.TryGetValue(type, out var found))
        {
            throw new UnknownEventTypeException(type.ToString());
        }

        return found!;
    }

    public int CurrentVersion(EventType type)
    {
        if (!_versions.TryGetValue(type, out var version))
        {
            throw new UnknownEventTypeException(type.ToString());
        }

        return version;
    }

    public DomainEventPayload Decode(DomainEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var current = CurrentVersion(evt.Type);
        if (evt.SchemaVersion > current)
        {
            throw new UnsupportedEventVersionException(evt.Type.ToString(), evt.SchemaVersion, current);
        }

        var json = evt.PayloadJson;
        for (var version = Math.Max(evt.SchemaVersion, 1); version < current; version++)
        {
            if (!_upcasters.TryGetValue((evt.Type, version), out var upcaster))
            {
                throw new EventParseException(evt.Type.ToString(),
                    "falta el upcaster v" + version + " → v" + (version + 1));
            }

            json = upcaster.Upcast(json);
        }

        var payload = CodecFor(evt.Type).Decode(evt.Type, json);
        if (payload is IValidatedDomainEventPayload && evt.SchemaVersion != current)
            throw new EventParseException(evt.Type.ToString(), "M6 validated records require their exact declared schema version");
        if (payload is IPreM6ContractEvent or RunModeProposed && evt.SchemaVersion < 1)
            throw new EventParseException(evt.Type.ToString(), "new contracts start at schema version 1");
        return payload;
    }

    /// <summary>Registra un upcaster (se usa también en tests para versiones sintéticas).</summary>
    public EventCodecs WithUpcaster(IEventUpcaster upcaster)
    {
        ArgumentNullException.ThrowIfNull(upcaster);
        _upcasters[(upcaster.Type, upcaster.FromVersion)] = upcaster;
        return this;
    }

    /// <summary>Registra un codec adicional (tests de evolución de schema).</summary>
    public EventCodecs With(Typed.CodecPair pair) => Plus(pair);

    private EventCodecs Plus(Typed.CodecPair pair)
    {
        _byType[pair.Type] = pair.Codec;
        _versions[pair.Type] = pair.CurrentVersion;
        return this;
    }
}

/// <summary>Upcaster para un campo nuevo opcional: el JSON antiguo ya decodifica con valor por defecto.</summary>
public sealed class IdentityUpcaster : IEventUpcaster
{
    public IdentityUpcaster(EventType type, int fromVersion)
    {
        Type = type;
        FromVersion = fromVersion;
    }

    public EventType Type { get; }

    public int FromVersion { get; }

    public string Upcast(string payloadJson) => payloadJson;
}

/// <summary>Se desconoce un EventType persistido (evento de una versión futura sin upcaster).</summary>
public sealed class UnknownEventTypeException : InvalidOperationException
{
    public string EventType { get; }

    public UnknownEventTypeException(string type)
        : base("tipo de evento desconocido: " + type)
    {
        EventType = type;
    }
}

/// <summary>
/// Codec por tipo concreto sobre el contexto de System.Text.Json generado en compilación
/// (<see cref="EventJsonContext"/>): sin reflexión en tiempo de ejecución (analizadores AOT).
/// </summary>
public sealed class TypedCodec<T> : IDomainEventCodec where T : class, DomainEventPayload
{
    private readonly EventType _type;

    private readonly JsonTypeInfo<T> _info;

    public TypedCodec(EventType type, JsonTypeInfo<T> info, int currentVersion)
    {
        _type = type;
        _info = info;
        CurrentVersion = currentVersion;
    }

    /// <summary>Versión de schema que produce <see cref="Encode"/> y que espera <see cref="Decode"/>.</summary>
    public int CurrentVersion { get; }

    public DomainEventPayload Decode(EventType type, string payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize(payloadJson, _info) ?? throw new FormatException("payload null");
            if (payload is IValidatedDomainEventPayload validated) validated.Validate();
            else if (payload is IPreM6ContractEvent record) record.Validate();
            return payload;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NotSupportedException or ArgumentException)
        {
            throw new EventParseException(type.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Serializa el payload y redacta los secretos conocidos del proceso (ADR-0018 §3): el payload
    /// redactado es lo único que llega al journal, venga del camino que venga.
    /// </summary>
    public string Encode(DomainEventPayload payload)
    {
        if (payload is IValidatedDomainEventPayload validated) validated.Validate();
        else if (payload is IPreM6ContractEvent record) record.Validate();
        var json = JsonSerializer.Serialize((T) payload, _info);
        var redactor = OmniCore.Abstractions.SecretRedactorRegistry.Current;
        // Por valor y respetando ids: sustituir sobre el JSON crudo podía corromper un GUID que
        // contuviera un secreto corto y dejar el evento ilegible.
        return redactor is null ? json : JournalRedaction.RedactStringValues(json, redactor);
    }

    public string DebugType() => _type.ToString();
}

/// <summary>Un payload no pudo deserializarse a su tipo concreto (schema futuro sin upcaster).</summary>
public sealed class EventParseException : InvalidOperationException
{
    public string EventType { get; }

    public string? Detail { get; }

    public EventParseException(string type, string detail)
        : base("evento " + type + " no se pudo leer: " + detail)
    {
        EventType = type;
        Detail = detail;
    }
}

/// <summary>
/// Un evento persistido tiene una versión de schema posterior a la que conoce este binario (lo
/// escribió una versión más nueva de OmniCore): no se interpreta a ciegas (ADR-0013).
/// </summary>
public sealed class UnsupportedEventVersionException : InvalidOperationException
{
    public string EventType { get; }

    public int StoredVersion { get; }

    public int CurrentVersion { get; }

    public UnsupportedEventVersionException(string type, int storedVersion, int currentVersion)
        : base("evento " + type + " v" + storedVersion + " es posterior a la versión conocida v" + currentVersion)
    {
        EventType = type;
        StoredVersion = storedVersion;
        CurrentVersion = currentVersion;
    }
}

/// <summary>Contexto de serialización generado en compilación para todos los eventos canónicos.</summary>
[JsonSerializable(typeof(RunCreated))]
[JsonSerializable(typeof(RunStarted))]
[JsonSerializable(typeof(RunAwaitingInput))]
[JsonSerializable(typeof(UserInputReceived))]
[JsonSerializable(typeof(FollowUpQueued))]
[JsonSerializable(typeof(FollowUpPromoted))]
[JsonSerializable(typeof(TurnSteeringReceived))]
[JsonSerializable(typeof(TurnSteeringApplied))]
[JsonSerializable(typeof(TurnSteeringDropped))]
[JsonSerializable(typeof(AssistantMessageRecorded))]
[JsonSerializable(typeof(RunValidationStarted))]
[JsonSerializable(typeof(RunValidationRejected))]
[JsonSerializable(typeof(PostEditValidationPending))]
[JsonSerializable(typeof(PostEditValidationConsumed))]
[JsonSerializable(typeof(RunCompleted))]
[JsonSerializable(typeof(RunSummaryRecorded))]
[JsonSerializable(typeof(RunFailed))]
[JsonSerializable(typeof(RunCancelled))]
[JsonSerializable(typeof(RunModeChanged))]
[JsonSerializable(typeof(RunModeProposed))]
[JsonSerializable(typeof(RunModeAuthoritySelected))]
[JsonSerializable(typeof(RunModeTransitionAuthorized))]
[JsonSerializable(typeof(RunModeAuthorityRevoked))]
[JsonSerializable(typeof(RunReasoningPreferenceSelected))]
[JsonSerializable(typeof(RunReasoningPreferenceRevoked))]
[JsonSerializable(typeof(RunInteractionResumed))]
[JsonSerializable(typeof(RunModeAuthority))]
[JsonSerializable(typeof(ModeSwitchAuthorization))]
[JsonSerializable(typeof(ModeSwitchLimits))]
[JsonSerializable(typeof(ProductEffort))]
[JsonSerializable(typeof(SessionCreated))]
[JsonSerializable(typeof(SessionRoutingPolicySet))]
[JsonSerializable(typeof(SessionRoutingPolicyRevised))]
[JsonSerializable(typeof(WorkspaceRootEstablished))]
[JsonSerializable(typeof(InteractionRequested))]
[JsonSerializable(typeof(InteractionResolved))]
[JsonSerializable(typeof(InteractionExpired))]
[JsonSerializable(typeof(ProgressStalled))]
[JsonSerializable(typeof(StallResponseSelected))]
[JsonSerializable(typeof(TaskCreated))]
[JsonSerializable(typeof(AgentExecutionStarted))]
[JsonSerializable(typeof(AgentExecutionCompleted))]
[JsonSerializable(typeof(AgentExecutionFailed))]
[JsonSerializable(typeof(DelegationCreated))]
[JsonSerializable(typeof(DelegationAccepted))]
[JsonSerializable(typeof(DelegationReturned))]
[JsonSerializable(typeof(DelegationFailed))]
[JsonSerializable(typeof(DelegationCancellationRequested))]
[JsonSerializable(typeof(ExecutionJoinCreated))]
[JsonSerializable(typeof(ExecutionJoinResolved))]
[JsonSerializable(typeof(ExecutionJoinFailed))]
[JsonSerializable(typeof(FanOutGroupCreated))]
[JsonSerializable(typeof(FanOutGroupMemberReplaced))]
[JsonSerializable(typeof(FanOutGroupResolved))]
[JsonSerializable(typeof(SupervisionBindingCreated))]
[JsonSerializable(typeof(SupervisionBindingAccepted))]
[JsonSerializable(typeof(SupervisionBindingFailed))]
[JsonSerializable(typeof(ExecutionMailboxCreated))]
[JsonSerializable(typeof(ExecutionMailboxMessageReceived))]
[JsonSerializable(typeof(ExecutionMailboxMessageAcknowledged))]
[JsonSerializable(typeof(WakeRequestCreated))]
[JsonSerializable(typeof(WakeRequestAccepted))]
[JsonSerializable(typeof(WakeRequestResolved))]
[JsonSerializable(typeof(WakeRequestFailed))]
[JsonSerializable(typeof(AgentResultProduced))]
[JsonSerializable(typeof(ResultDispositionRecorded))]
[JsonSerializable(typeof(ValidationStateRecorded))]
[JsonSerializable(typeof(ValidationDebtCreated))]
[JsonSerializable(typeof(ValidationDebtResolved))]
[JsonSerializable(typeof(IntegrationStatusRecorded))]
[JsonSerializable(typeof(TaskReady))]
[JsonSerializable(typeof(TaskStarted))]
[JsonSerializable(typeof(TaskBlocked))]
[JsonSerializable(typeof(TaskUnblocked))]
[JsonSerializable(typeof(TaskCompleted))]
[JsonSerializable(typeof(TaskFailed))]
[JsonSerializable(typeof(TaskSkipped))]
[JsonSerializable(typeof(TaskCancelled))]
[JsonSerializable(typeof(LaneCreated))]
[JsonSerializable(typeof(LaneProvisioning))]
[JsonSerializable(typeof(LaneStarted))]
[JsonSerializable(typeof(LaneBlocked))]
[JsonSerializable(typeof(LaneUnblocked))]
[JsonSerializable(typeof(LaneCompleted))]
[JsonSerializable(typeof(LaneFailed))]
[JsonSerializable(typeof(LaneCancelled))]
[JsonSerializable(typeof(TurnStarted))]
[JsonSerializable(typeof(TurnInstructionSnapshot))]
[JsonSerializable(typeof(ReasoningResolution))]
[JsonSerializable(typeof(ReasoningSelectionSource))]
[JsonSerializable(typeof(ReasoningReduction))]
[JsonSerializable(typeof(ModelEscalationRequested))]
[JsonSerializable(typeof(ModelEscalationApproved))]
[JsonSerializable(typeof(ModelEscalationCompleted))]
[JsonSerializable(typeof(EscalationCause))]
[JsonSerializable(typeof(ModelCompleted))]
[JsonSerializable(typeof(ModelStepStarted))]
[JsonSerializable(typeof(GenerationRequestAttemptEvidence))]
[JsonSerializable(typeof(ModelStepNotDispatched))]
[JsonSerializable(typeof(ModelStepCompleted))]
[JsonSerializable(typeof(TurnCompleted))]
[JsonSerializable(typeof(TurnInterrupted))]
[JsonSerializable(typeof(TurnAbandoned))]
[JsonSerializable(typeof(ToolCallRequested))]
[JsonSerializable(typeof(ToolCallPrepared))]
[JsonSerializable(typeof(ToolCallRejected))]
[JsonSerializable(typeof(PermissionEvaluated))]
[JsonSerializable(typeof(PermissionRequested))]
[JsonSerializable(typeof(PermissionGranted))]
[JsonSerializable(typeof(PermissionDenied))]
[JsonSerializable(typeof(ToolCallAuthorized))]
[JsonSerializable(typeof(ToolCallStarted))]
[JsonSerializable(typeof(ToolCallSucceeded))]
[JsonSerializable(typeof(ToolCallFailed))]
[JsonSerializable(typeof(ToolCallEffectUnknown))]
[JsonSerializable(typeof(ToolCallReconciled))]
[JsonSerializable(typeof(ToolCallCancelled))]
[JsonSerializable(typeof(PlanCreated))]
[JsonSerializable(typeof(PlanRevised))]
[JsonSerializable(typeof(PlanItemAdded))]
[JsonSerializable(typeof(PlanItemUpdated))]
[JsonSerializable(typeof(PlanItemStarted))]
[JsonSerializable(typeof(PlanItemReady))]
[JsonSerializable(typeof(PlanItemBlocked))]
[JsonSerializable(typeof(PlanItemUnblocked))]
[JsonSerializable(typeof(PlanItemCompleted))]
[JsonSerializable(typeof(PlanItemFailed))]
[JsonSerializable(typeof(PlanItemSkipped))]
[JsonSerializable(typeof(PlanItemCancelled))]
[JsonSerializable(typeof(PlanItemReopened))]
[JsonSerializable(typeof(PlanItemReordered))]
[JsonSerializable(typeof(PlanItemLinked))]
[JsonSerializable(typeof(PlanItemUnlinked))]
[JsonSerializable(typeof(PlanMutationRejected))]
[JsonSerializable(typeof(ContextCheckpointRecorded))]
[JsonSerializable(typeof(MetaModelInvocationStarted))]
[JsonSerializable(typeof(MetaModelInvocationCompleted))]
[JsonSerializable(typeof(MetaModelInvocationFailed))]
[JsonSerializable(typeof(MetaModelInvocationNotDispatched))]
internal sealed partial class EventJsonContext : JsonSerializerContext
{
}

/// <summary>Fábrica de pares EventType → codec para todos los eventos canónicos.</summary>
public sealed class Typed
{
    public sealed class CodecPair
    {
        public EventType Type { get; }

        public IDomainEventCodec Codec { get; }

        public int CurrentVersion { get; }

        public CodecPair(EventType type, IDomainEventCodec codec, int currentVersion)
        {
            Type = type;
            Codec = codec;
            CurrentVersion = currentVersion;
        }
    }

    private static CodecPair Of<T>(EventType type, JsonTypeInfo<T> info, int currentVersion = 1)
        where T : class, DomainEventPayload =>
        new(type, new TypedCodec<T>(type, info, currentVersion), currentVersion);

    public static CodecPair RunCreated() =>
        Of(EventType.Of("run.created"), EventJsonContext.Default.RunCreated);

    public static CodecPair RunStarted() =>
        Of(EventType.Of("run.started"), EventJsonContext.Default.RunStarted);

    public static CodecPair RunAwaitingInput() =>
        Of(EventType.Of("run.awaiting_input"), EventJsonContext.Default.RunAwaitingInput);

    public static CodecPair UserInputReceived() =>
        Of(EventType.Of("user_input.received"), EventJsonContext.Default.UserInputReceived, currentVersion: 2);

    public static CodecPair FollowUpQueued() =>
        Of(EventType.Of("followup.queued"), EventJsonContext.Default.FollowUpQueued);

    public static CodecPair FollowUpPromoted() =>
        Of(EventType.Of("followup.promoted"), EventJsonContext.Default.FollowUpPromoted);

    public static CodecPair TurnSteeringReceived() =>
        Of(EventType.Of("turn.steering_received"), EventJsonContext.Default.TurnSteeringReceived);

    public static CodecPair TurnSteeringApplied() =>
        Of(EventType.Of("turn.steering_applied"), EventJsonContext.Default.TurnSteeringApplied);

    public static CodecPair TurnSteeringDropped() =>
        Of(EventType.Of("turn.steering_dropped"), EventJsonContext.Default.TurnSteeringDropped);

    public static CodecPair AssistantMessageRecorded() =>
        Of(EventType.Of("assistant_message.recorded"), EventJsonContext.Default.AssistantMessageRecorded);

    public static CodecPair RunValidationStarted() =>
        Of(EventType.Of("run.validation_started"), EventJsonContext.Default.RunValidationStarted);

    public static CodecPair RunValidationRejected() =>
        Of(EventType.Of("run.validation_rejected"), EventJsonContext.Default.RunValidationRejected);

    public static CodecPair PostEditValidationPending() =>
        Of(EventType.Of("post_edit_validation.pending"), EventJsonContext.Default.PostEditValidationPending);

    public static CodecPair PostEditValidationConsumed() =>
        Of(EventType.Of("post_edit_validation.consumed"), EventJsonContext.Default.PostEditValidationConsumed);

    public static CodecPair RunCompleted() =>
        Of(EventType.Of("run.completed"), EventJsonContext.Default.RunCompleted);

    public static CodecPair RunSummaryRecorded() =>
        Of(EventType.Of("run.summary_recorded"), EventJsonContext.Default.RunSummaryRecorded);

    public static CodecPair RunFailed() =>
        Of(EventType.Of("run.failed"), EventJsonContext.Default.RunFailed);

    public static CodecPair RunCancelled() =>
        Of(EventType.Of("run.cancelled"), EventJsonContext.Default.RunCancelled);

    public static CodecPair RunModeChanged() =>
        Of(EventType.Of("run.mode_changed"), EventJsonContext.Default.RunModeChanged);

    public static CodecPair RunModeProposed() =>
        Of(EventType.Of("run.mode_proposed"), EventJsonContext.Default.RunModeProposed);

    public static CodecPair RunModeAuthoritySelected() =>
        Of(EventType.Of("run.mode_authority_selected"), EventJsonContext.Default.RunModeAuthoritySelected);

    public static CodecPair RunModeTransitionAuthorized() =>
        Of(EventType.Of("run.mode_transition_authorized"), EventJsonContext.Default.RunModeTransitionAuthorized);

    public static CodecPair RunModeAuthorityRevoked() =>
        Of(EventType.Of("run.mode_authority_revoked"), EventJsonContext.Default.RunModeAuthorityRevoked);

    public static CodecPair RunReasoningPreferenceSelected() =>
        Of(EventType.Of("run.reasoning_preference_selected"), EventJsonContext.Default.RunReasoningPreferenceSelected);

    public static CodecPair RunReasoningPreferenceRevoked() =>
        Of(EventType.Of("run.reasoning_preference_revoked"), EventJsonContext.Default.RunReasoningPreferenceRevoked);

    public static CodecPair RunInteractionResumed() =>
        Of(EventType.Of("run.interaction_resumed"), EventJsonContext.Default.RunInteractionResumed);

    public static CodecPair SessionCreated() =>
        Of(EventType.Of("session.created"), EventJsonContext.Default.SessionCreated);

    public static CodecPair SessionRoutingPolicySet() =>
        Of(EventType.Of("session.routing_policy_set"), EventJsonContext.Default.SessionRoutingPolicySet);

    public static CodecPair SessionRoutingPolicyRevised() =>
        Of(EventType.Of("session.routing_policy_revised"), EventJsonContext.Default.SessionRoutingPolicyRevised);

    public static CodecPair WorkspaceRootEstablished() =>
        Of(EventType.Of("workspace.root_established"), EventJsonContext.Default.WorkspaceRootEstablished, currentVersion: 2);

    public static CodecPair InteractionRequested() =>
        Of(EventType.Of("interaction.requested"), EventJsonContext.Default.InteractionRequested, currentVersion: 2);

    public static CodecPair InteractionResolved() =>
        Of(EventType.Of("interaction.resolved"), EventJsonContext.Default.InteractionResolved, currentVersion: 2);

    public static CodecPair InteractionExpired() =>
        Of(EventType.Of("interaction.expired"), EventJsonContext.Default.InteractionExpired);

    public static CodecPair ProgressStalled() =>
        Of(EventType.Of("progress.stalled"), EventJsonContext.Default.ProgressStalled);

    public static CodecPair StallResponseSelected() =>
        Of(EventType.Of("stall.response_selected"), EventJsonContext.Default.StallResponseSelected);

    public static CodecPair TaskCreated() =>
        Of(EventType.Of("task.created"), EventJsonContext.Default.TaskCreated, currentVersion: 2);

    public static CodecPair DelegationCreated() =>
        Of(EventType.Of("delegation.created"), EventJsonContext.Default.DelegationCreated);

    public static CodecPair DelegationAccepted() =>
        Of(EventType.Of("delegation.accepted"), EventJsonContext.Default.DelegationAccepted);

    public static CodecPair DelegationReturned() =>
        Of(EventType.Of("delegation.returned"), EventJsonContext.Default.DelegationReturned);

    public static CodecPair DelegationFailed() =>
        Of(EventType.Of("delegation.failed"), EventJsonContext.Default.DelegationFailed);

    public static CodecPair DelegationCancellationRequested() =>
        Of(EventType.Of("delegation.cancellation_requested"), EventJsonContext.Default.DelegationCancellationRequested);

    public static CodecPair ExecutionJoinCreated() =>
        Of(EventType.Of("execution_join.created"), EventJsonContext.Default.ExecutionJoinCreated);

    public static CodecPair ExecutionJoinResolved() =>
        Of(EventType.Of("execution_join.resolved"), EventJsonContext.Default.ExecutionJoinResolved);

    public static CodecPair ExecutionJoinFailed() =>
        Of(EventType.Of("execution_join.failed"), EventJsonContext.Default.ExecutionJoinFailed);

    public static CodecPair FanOutGroupCreated() =>
        Of(EventType.Of("fanout_group.created"), EventJsonContext.Default.FanOutGroupCreated);

    public static CodecPair FanOutGroupMemberReplaced() =>
        Of(EventType.Of("fanout_group.member_replaced"), EventJsonContext.Default.FanOutGroupMemberReplaced);

    public static CodecPair FanOutGroupResolved() =>
        Of(EventType.Of("fanout_group.resolved"), EventJsonContext.Default.FanOutGroupResolved);

    public static CodecPair SupervisionBindingCreated() =>
        Of(EventType.Of("supervision_binding.created"), EventJsonContext.Default.SupervisionBindingCreated);

    public static CodecPair SupervisionBindingAccepted() =>
        Of(EventType.Of("supervision_binding.accepted"), EventJsonContext.Default.SupervisionBindingAccepted);

    public static CodecPair SupervisionBindingFailed() =>
        Of(EventType.Of("supervision_binding.failed"), EventJsonContext.Default.SupervisionBindingFailed);

    public static CodecPair ExecutionMailboxCreated() =>
        Of(EventType.Of("execution_mailbox.created"), EventJsonContext.Default.ExecutionMailboxCreated);

    public static CodecPair ExecutionMailboxMessageReceived() =>
        Of(EventType.Of("execution_mailbox.message_received"), EventJsonContext.Default.ExecutionMailboxMessageReceived);

    public static CodecPair ExecutionMailboxMessageAcknowledged() =>
        Of(EventType.Of("execution_mailbox.message_acknowledged"), EventJsonContext.Default.ExecutionMailboxMessageAcknowledged);

    public static CodecPair WakeRequestCreated() =>
        Of(EventType.Of("wake_request.created"), EventJsonContext.Default.WakeRequestCreated);

    public static CodecPair WakeRequestAccepted() =>
        Of(EventType.Of("wake_request.accepted"), EventJsonContext.Default.WakeRequestAccepted);

    public static CodecPair WakeRequestResolved() =>
        Of(EventType.Of("wake_request.resolved"), EventJsonContext.Default.WakeRequestResolved);

    public static CodecPair WakeRequestFailed() =>
        Of(EventType.Of("wake_request.failed"), EventJsonContext.Default.WakeRequestFailed);

    public static CodecPair AgentResultProduced() =>
        Of(EventType.Of("agent_result.produced"), EventJsonContext.Default.AgentResultProduced);

    public static CodecPair ResultDispositionRecorded() =>
        Of(EventType.Of("result_disposition.recorded"), EventJsonContext.Default.ResultDispositionRecorded);

    public static CodecPair ValidationStateRecorded() =>
        Of(EventType.Of("validation_state.recorded"), EventJsonContext.Default.ValidationStateRecorded);

    public static CodecPair ValidationDebtCreated() =>
        Of(EventType.Of("validation_debt.created"), EventJsonContext.Default.ValidationDebtCreated);

    public static CodecPair ValidationDebtResolved() =>
        Of(EventType.Of("validation_debt.resolved"), EventJsonContext.Default.ValidationDebtResolved);

    public static CodecPair IntegrationStatusRecorded() =>
        Of(EventType.Of("integration_status.recorded"), EventJsonContext.Default.IntegrationStatusRecorded);

    public static CodecPair AgentExecutionStarted() =>
        Of(EventType.Of("agent_execution.started"), EventJsonContext.Default.AgentExecutionStarted);

    public static CodecPair AgentExecutionCompleted() =>
        Of(EventType.Of("agent_execution.completed"), EventJsonContext.Default.AgentExecutionCompleted);

    public static CodecPair AgentExecutionFailed() =>
        Of(EventType.Of("agent_execution.failed"), EventJsonContext.Default.AgentExecutionFailed);

    public static CodecPair TaskReady() =>
        Of(EventType.Of("task.ready"), EventJsonContext.Default.TaskReady);

    public static CodecPair TaskStarted() =>
        Of(EventType.Of("task.started"), EventJsonContext.Default.TaskStarted);

    public static CodecPair TaskBlocked() =>
        Of(EventType.Of("task.blocked"), EventJsonContext.Default.TaskBlocked);

    public static CodecPair TaskUnblocked() =>
        Of(EventType.Of("task.unblocked"), EventJsonContext.Default.TaskUnblocked);

    public static CodecPair TaskCompleted() =>
        Of(EventType.Of("task.completed"), EventJsonContext.Default.TaskCompleted);

    public static CodecPair TaskFailed() =>
        Of(EventType.Of("task.failed"), EventJsonContext.Default.TaskFailed);

    public static CodecPair TaskSkipped() =>
        Of(EventType.Of("task.skipped"), EventJsonContext.Default.TaskSkipped);

    public static CodecPair TaskCancelled() =>
        Of(EventType.Of("task.cancelled"), EventJsonContext.Default.TaskCancelled);

    public static CodecPair LaneCreated() =>
        Of(EventType.Of("lane.created"), EventJsonContext.Default.LaneCreated, currentVersion: 2);

    public static CodecPair LaneProvisioning() =>
        Of(EventType.Of("lane.provisioning"), EventJsonContext.Default.LaneProvisioning);

    public static CodecPair LaneStarted() =>
        Of(EventType.Of("lane.started"), EventJsonContext.Default.LaneStarted);

    public static CodecPair LaneBlocked() =>
        Of(EventType.Of("lane.blocked"), EventJsonContext.Default.LaneBlocked);

    public static CodecPair LaneUnblocked() =>
        Of(EventType.Of("lane.unblocked"), EventJsonContext.Default.LaneUnblocked);

    public static CodecPair LaneCompleted() =>
        Of(EventType.Of("lane.completed"), EventJsonContext.Default.LaneCompleted);

    public static CodecPair LaneFailed() =>
        Of(EventType.Of("lane.failed"), EventJsonContext.Default.LaneFailed);

    public static CodecPair LaneCancelled() =>
        Of(EventType.Of("lane.cancelled"), EventJsonContext.Default.LaneCancelled);

    public static CodecPair TurnStarted() =>
        Of(EventType.Of("turn.started"), EventJsonContext.Default.TurnStarted, currentVersion: 3);

    public static CodecPair ModelEscalationRequested() =>
        Of(EventType.Of("model.escalation_requested"), EventJsonContext.Default.ModelEscalationRequested, currentVersion: 2);

    public static CodecPair ModelEscalationApproved() =>
        Of(EventType.Of("model.escalation_approved"), EventJsonContext.Default.ModelEscalationApproved, currentVersion: 2);

    public static CodecPair ModelEscalationCompleted() =>
        Of(EventType.Of("model.escalation_completed"), EventJsonContext.Default.ModelEscalationCompleted, currentVersion: 2);

    public static CodecPair ModelCompleted() =>
        Of(EventType.Of("model.completed"), EventJsonContext.Default.ModelCompleted);

    public static CodecPair ModelStepStarted() =>
        Of(EventType.Of("model_step.started"), EventJsonContext.Default.ModelStepStarted, currentVersion: 4);

    public static CodecPair ModelStepCompleted() =>
        Of(EventType.Of("model_step.completed"), EventJsonContext.Default.ModelStepCompleted, currentVersion: 3);

    public static CodecPair ModelStepNotDispatched() =>
        Of(EventType.Of("model_step.not_dispatched"), EventJsonContext.Default.ModelStepNotDispatched);

    public static CodecPair TurnCompleted() =>
        Of(EventType.Of("turn.completed"), EventJsonContext.Default.TurnCompleted);

    public static CodecPair TurnInterrupted() =>
        Of(EventType.Of("turn.interrupted"), EventJsonContext.Default.TurnInterrupted);

    public static CodecPair TurnAbandoned() =>
        Of(EventType.Of("turn.abandoned"), EventJsonContext.Default.TurnAbandoned);

    public static CodecPair ToolCallRequested() =>
        Of(EventType.Of("toolcall.requested"), EventJsonContext.Default.ToolCallRequested);

    public static CodecPair ToolCallPrepared() =>
        Of(EventType.Of("toolcall.prepared"), EventJsonContext.Default.ToolCallPrepared);

    public static CodecPair ToolCallRejected() =>
        Of(EventType.Of("toolcall.rejected"), EventJsonContext.Default.ToolCallRejected, currentVersion: 2);

    public static CodecPair PermissionEvaluated() =>
        Of(EventType.Of("toolcall.permission_evaluated"), EventJsonContext.Default.PermissionEvaluated);

    public static CodecPair PermissionRequested() =>
        Of(EventType.Of("toolcall.permission_requested"), EventJsonContext.Default.PermissionRequested);

    public static CodecPair PermissionGranted() =>
        Of(EventType.Of("toolcall.permission_granted"), EventJsonContext.Default.PermissionGranted);

    public static CodecPair PermissionDenied() =>
        Of(EventType.Of("toolcall.permission_denied"), EventJsonContext.Default.PermissionDenied);

    public static CodecPair ToolCallAuthorized() =>
        Of(EventType.Of("toolcall.authorized"), EventJsonContext.Default.ToolCallAuthorized);

    public static CodecPair ToolCallStarted() =>
        Of(EventType.Of("toolcall.started"), EventJsonContext.Default.ToolCallStarted, currentVersion: 3);

    public static CodecPair ToolCallSucceeded() =>
        Of(EventType.Of("toolcall.succeeded"), EventJsonContext.Default.ToolCallSucceeded, currentVersion: 2);

    public static CodecPair ToolCallFailed() =>
        Of(EventType.Of("toolcall.failed"), EventJsonContext.Default.ToolCallFailed, currentVersion: 2);

    public static CodecPair ToolCallEffectUnknown() =>
        Of(EventType.Of("toolcall.effect_unknown"), EventJsonContext.Default.ToolCallEffectUnknown);

    public static CodecPair ToolCallReconciled() =>
        Of(EventType.Of("toolcall.reconciled"), EventJsonContext.Default.ToolCallReconciled, currentVersion: 2);

    public static CodecPair ToolCallCancelled() =>
        Of(EventType.Of("toolcall.cancelled"), EventJsonContext.Default.ToolCallCancelled);

    public static CodecPair PlanCreated() =>
        Of(EventType.Of("plan.created"), EventJsonContext.Default.PlanCreated);

    public static CodecPair PlanRevised() =>
        Of(EventType.Of("plan.revised"), EventJsonContext.Default.PlanRevised);

    public static CodecPair PlanItemAdded() =>
        Of(EventType.Of("plan_item.added"), EventJsonContext.Default.PlanItemAdded);

    public static CodecPair PlanItemUpdated() =>
        Of(EventType.Of("plan_item.updated"), EventJsonContext.Default.PlanItemUpdated);

    public static CodecPair PlanItemStarted() =>
        Of(EventType.Of("plan_item.started"), EventJsonContext.Default.PlanItemStarted);

    public static CodecPair PlanItemReady() =>
        Of(EventType.Of("plan_item.ready"), EventJsonContext.Default.PlanItemReady);

    public static CodecPair PlanItemBlocked() =>
        Of(EventType.Of("plan_item.blocked"), EventJsonContext.Default.PlanItemBlocked);

    public static CodecPair PlanItemUnblocked() =>
        Of(EventType.Of("plan_item.unblocked"), EventJsonContext.Default.PlanItemUnblocked);

    public static CodecPair PlanItemCompleted() =>
        Of(EventType.Of("plan_item.completed"), EventJsonContext.Default.PlanItemCompleted);

    public static CodecPair PlanItemFailed() =>
        Of(EventType.Of("plan_item.failed"), EventJsonContext.Default.PlanItemFailed);

    public static CodecPair PlanItemSkipped() =>
        Of(EventType.Of("plan_item.skipped"), EventJsonContext.Default.PlanItemSkipped);

    public static CodecPair PlanItemCancelled() =>
        Of(EventType.Of("plan_item.cancelled"), EventJsonContext.Default.PlanItemCancelled);

    public static CodecPair PlanItemReopened() =>
        Of(EventType.Of("plan_item.reopened"), EventJsonContext.Default.PlanItemReopened);

    public static CodecPair PlanItemReordered() =>
        Of(EventType.Of("plan_item.reordered"), EventJsonContext.Default.PlanItemReordered);

    public static CodecPair PlanItemLinked() =>
        Of(EventType.Of("plan_item.linked"), EventJsonContext.Default.PlanItemLinked);

    public static CodecPair PlanItemUnlinked() =>
        Of(EventType.Of("plan_item.unlinked"), EventJsonContext.Default.PlanItemUnlinked);

    public static CodecPair PlanMutationRejected() =>
        Of(EventType.Of("plan_mutation.rejected"), EventJsonContext.Default.PlanMutationRejected);

    public static CodecPair ContextCheckpointRecorded() =>
        Of(EventType.Of("context.checkpoint_recorded"), EventJsonContext.Default.ContextCheckpointRecorded);

    public static CodecPair MetaModelInvocationStarted() =>
        Of(EventType.Of("meta_model.invocation_started"), EventJsonContext.Default.MetaModelInvocationStarted);

    public static CodecPair MetaModelInvocationCompleted() =>
        Of(EventType.Of("meta_model.invocation_completed"), EventJsonContext.Default.MetaModelInvocationCompleted, currentVersion: 3);

    public static CodecPair MetaModelInvocationFailed() =>
        Of(EventType.Of("meta_model.invocation_failed"), EventJsonContext.Default.MetaModelInvocationFailed, currentVersion: 3);

    public static CodecPair MetaModelInvocationNotDispatched() =>
        Of(EventType.Of("meta_model.invocation_not_dispatched"), EventJsonContext.Default.MetaModelInvocationNotDispatched);
}
