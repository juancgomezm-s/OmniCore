namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;

/// <summary>
/// Traduce los eventos de dominio del journal a eventos del protocolo (ADR-0013 §1): el cliente
/// nunca ve los tipos del Engine. La tabla es explícita —qué eventos se exponen y con qué campos—
/// para que un cambio interno del dominio no rompa el wire. Un evento sin entrada no se expone.
/// Todos los campos son strings; los textos de usuario se redactan (ADR-0018).
/// </summary>
public sealed class ProtocolMapper
{
    private readonly IEventCodecRegistry _codecs;

    private readonly IArtifactStore? _artifacts;

    private readonly RedactionPolicy _redaction = new();

    public ProtocolMapper(IEventCodecRegistry codecs, IArtifactStore? artifacts = null)
    {
        _codecs = codecs;
        _artifacts = artifacts;
    }

    /// <summary>Eventos del protocolo para los eventos de dominio dados, en orden.</summary>
    public IReadOnlyList<WireEnvelope> Map(IReadOnlyList<DomainEvent> events) => Map(events, null);

    /// <summary>Maps events with canonical Run/Lane facts from the full journal for primary transcript attribution.</summary>
    public IReadOnlyList<WireEnvelope> Map(IReadOnlyList<DomainEvent> events,
        IReadOnlyList<DomainEvent>? attributionJournal)
    {
        var conversationScopes = attributionJournal is null ? null : BuildConversationScopes(attributionJournal);
        var result = new List<WireEnvelope>();
        foreach (var evt in events)
        {
            var fields = Fields(_codecs.Decode(evt));
            if (fields is null)
            {
                continue;
            }

            var parts = new List<string>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            void Add(string key, string? value)
            {
                if (value is not null && keys.Add(key)) parts.Add(JsonObj.Field(key, value));
            }
            var legacyRun = evt.CorrelationId is { } correlation && conversationScopes?.ContainsKey(correlation) == true
                ? correlation : null;
            Add("type", evt.Type.ToString());
            Add("seq", evt.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add("sessionId", evt.SessionId.ToString());
            Add("runId", (evt.RunId ?? legacyRun)?.ToString());
            Add("source", evt.Source is null ? null : _redaction.Redact(evt.Source));
            foreach (var field in fields) Add(field.Key, field.Value);
            Add("taskId", evt.TaskId?.ToString());
            Add("laneId", evt.LaneId?.ToString());
            Add("turnId", evt.TurnId?.ToString());
            Add("executionId", evt.ExecutionId?.ToString());
            Add("toolCallId", evt.ToolCallId?.ToString());
            if (evt.RunId is { } run && conversationScopes?.TryGetValue(run, out var scope) == true)
                Add("conversationScope", scope.Classify(evt));
            result.Add(WireEnvelope.Event(evt.EventId.ToString(), "{" + string.Join(",", parts) + "}"));
        }

        return result;
    }

    private sealed record RunConversationScope(TaskId? RootTask, LaneId? RootLane,
        HashSet<TaskId> ChildTasks, HashSet<LaneId> ChildLanes, bool HasChildren)
    {
        internal string Classify(DomainEvent evt)
        {
            if (evt.LaneId is { } lane)
            {
                if (RootLane == lane) return "primary";
                if (ChildLanes.Contains(lane)) return "child";
                return "ambiguous";
            }
            if (evt.TaskId is { } task)
            {
                if (RootTask == task) return "primary";
                if (ChildTasks.Contains(task)) return "child";
                return "ambiguous";
            }
            return HasChildren ? "ambiguous" : "primary";
        }
    }

    private Dictionary<RunId, RunConversationScope> BuildConversationScopes(IReadOnlyList<DomainEvent> journal)
    {
        var result = new Dictionary<RunId, RunConversationScope>();
        var runs = journal.Where(evt => evt.RunId is not null).Select(evt => evt.RunId!)
            .Distinct().ToArray();
        foreach (var run in runs)
        {
            var own = journal.Where(evt => evt.RunId == run).ToArray();
            var payloads = own.Select(evt => (Event: evt, Payload: _codecs.Decode(evt))).ToArray();
            var tasks = payloads.Where(item => item.Payload is TaskCreated)
                .Select(item => (item.Event, Task: (TaskCreated)item.Payload)).ToArray();
            var rootTasks = tasks.Where(item => item.Task.ParentTaskId is null).ToArray();
            TaskId? rootTask = rootTasks.Length == 1 ? rootTasks[0].Task.TaskId : null;
            var lanes = payloads.Where(item => item.Payload is LaneCreated)
                .Select(item => (item.Event, Lane: (LaneCreated)item.Payload)).ToArray();
            var laneIds = lanes.Select(item => item.Lane.LaneId).ToHashSet();
            var rootLanes = rootTask is null ? [] : lanes.Where(item => item.Lane.TaskId == rootTask).ToArray();
            LaneId? rootLane = rootLanes.Length == 1 ? rootLanes[0].Lane.LaneId
                : rootTask is null && laneIds.Count == 1 ? laneIds.Single() : null;
            if (rootTask is null && rootLanes.Length == 1)
                rootTask = rootLanes[0].Lane.TaskId;
            var taskIds = tasks.Select(item => item.Task.TaskId).ToHashSet();
            var childTasks = taskIds.Where(task => task != rootTask).ToHashSet();
            var childLanes = laneIds.Where(lane => lane != rootLane).ToHashSet();
            var hasChildren = childTasks.Count > 0 || childLanes.Count > 0;
            result[run] = new(rootTask, rootLane, childTasks, childLanes, hasChildren);
        }
        return result;
    }

    /// <summary>
    /// <c>toolcall.requested</c> con su presentación (ADR-0033 §2): categoría y solo los campos que la tool
    /// declara como resumen, tomados de los argumentos y redactados. Una tool sin presentación va sin ellos.
    /// </summary>
    private Dictionary<string, string> ToolRequestedFields(ToolCallRequested requested)
    {
        var fields = new Dictionary<string, string>
        {
            ["toolCallId"] = requested.ToolCallId.ToString(), ["tool"] = requested.ToolName,
        };
        if (ToolPresentation.Of(requested.ToolName) is not { } presentation) return fields;
        fields["category"] = presentation.Category.ToString();
        // Cada campo declarado viaja siempre (con «…» si no se puede leer) para que la etiqueta nunca quede a medias.
        foreach (var name in presentation.SummaryFields) fields["arg." + name] = "…";
        if (presentation.SummaryFields.Count == 0) return fields;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(requested.ArgumentsJson);
            foreach (var name in presentation.SummaryFields)
            {
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                    fields["arg." + name] = _redaction.Redact(text.Length > 120 ? text[..120] + "…" : text);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Argumentos ilegibles: queda el «…» y la etiqueta se muestra sin el resumen.
        }
        return fields;
    }

    private Dictionary<string, string>? Fields(DomainEventPayload payload) => payload switch
    {
        RunCreated e => new() { ["objective"] = _redaction.Redact(e.Objective), ["mode"] = Mode(e.Mode) },
        RunStarted => new(),
        RunAwaitingInput => new(),
        RunValidationRejected e => new() { ["missing"] = string.Join("; ", e.Missing) },
        RunCompleted e => new() { ["outcome"] = e.Outcome.ToString() },
        RunFailed e => new() { ["cause"] = _redaction.Redact(e.Cause) },
        RunCancelled => new(),
        RunModeChanged e => new() { ["from"] = Mode(e.From), ["to"] = Mode(e.To) },
        RunModeProposed e => new()
        {
            ["turnId"] = e.TurnId.ToString(), ["toolCallId"] = e.ToolCallId.ToString(),
            ["from"] = Mode(e.From), ["to"] = Mode(e.To), ["reason"] = _redaction.Redact(e.Reason),
            ["origin"] = "Model", ["advisory"] = "true",
            ["authorityRevision"] = e.AuthorityRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["objectiveRevision"] = e.ObjectiveRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["objectiveDigest"] = e.ObjectiveDigest,
            ["policyRevision"] = e.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        RunModeAuthoritySelected e => new()
        {
            ["mode"] = Mode(e.Authority.Mode), ["strategy"] = e.Authority.Strategy.ToString(),
            ["effort"] = e.Authority.ProductEffort.ToString().ToLowerInvariant(),
            ["authorityRevision"] = e.Authority.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["modePinned"] = e.Authority.ModePinned.ToString().ToLowerInvariant(),
            ["autoModeSwitch"] = e.Authority.AutoModeSwitch.ToString().ToLowerInvariant(),
        },
        RunModeTransitionAuthorized e => new()
        {
            ["from"] = Mode(e.From), ["to"] = Mode(e.To), ["reason"] = _redaction.Redact(e.Reason),
            ["origin"] = e.Origin, ["authorityRevision"] = e.AuthorityRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["proposalEventId"] = e.ProposalEventId?.ToString() ?? "",
            ["coveredPlanId"] = e.PlanCoverage?.PlanId.ToString() ?? "",
            ["coveredPlanRevision"] = e.PlanCoverage?.PlanRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        },
        RunModeAuthorityRevoked e => new()
        {
            ["authorityRevision"] = e.AuthorityRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["reason"] = _redaction.Redact(e.Reason), ["origin"] = e.Origin,
        },
        RunReasoningPreferenceSelected e => new()
        {
            ["revision"] = e.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hasSelection"] = "true", ["kind"] = e.Request?.Kind ?? "off",
            ["budgetTokens"] = e.Request?.BudgetTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            ["source"] = e.Source,
        },
        RunReasoningPreferenceRevoked e => new()
        {
            ["revision"] = e.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hasSelection"] = "false", ["origin"] = e.Origin,
        },
        RunInteractionResumed e => new()
        {
            ["interactionId"] = e.InteractionId.ToString(), ["commandId"] = e.CommandId, ["runState"] = "running",
        },
        ModelStepStarted e => new()
        {
            ["turnId"] = e.TurnId.ToString(), ["stepIndex"] = e.StepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["modelId"] = e.ModelId, ["reasoningKind"] = e.ReasoningKind ?? "",
            ["reasoningBudgetTokens"] = e.ReasoningBudgetTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        },
        UserInputReceived e => new() { ["text"] = _redaction.Redact(InputText(e.InputPartsJson)) },
        AssistantMessageRecorded e => new() { ["text"] = _redaction.Redact(ArtifactText(e.ContentRef)) },
        TurnStarted e => new() { ["turnId"] = e.TurnId.ToString(), ["laneId"] = e.LaneId.ToString() },
        TurnCompleted e => new() { ["turnId"] = e.TurnId.ToString() },
        TurnInterrupted e => new() { ["turnId"] = e.TurnId.ToString() },
        TurnAbandoned e => new() { ["turnId"] = e.TurnId.ToString(), ["reason"] = _redaction.Redact(e.Reason) },
        ToolCallRequested e => ToolRequestedFields(e),
        ToolCallSucceeded e => new() { ["toolCallId"] = e.ToolCallId.ToString() },
        ToolCallFailed e => new()
        {
            ["toolCallId"] = e.ToolCallId.ToString(), ["cause"] = _redaction.Redact(e.Cause),
            ["effect"] = e.EffectOutcome.ToString(),
            // Código tipado (spec §71) al wire; "" preserva los journals v1 sin campo.
            ["errorCode"] = e.ErrorCode?.Value ?? "",
        },
        ToolCallRejected e => new()
        {
            ["toolCallId"] = e.ToolCallId.ToString(), ["cause"] = _redaction.Redact(e.Reason),
            ["errorCode"] = e.ErrorCode?.Value ?? "",
        },
        PermissionDenied e => new() { ["toolCallId"] = e.ToolCallId.ToString(), ["cause"] = _redaction.Redact(e.Cause) },
        ToolCallCancelled e => new() { ["toolCallId"] = e.ToolCallId.ToString(), ["cause"] = _redaction.Redact(e.Cause) },
        ToolCallReconciled e => new()
        {
            ["toolCallId"] = e.ToolCallId.ToString(), ["outcome"] = e.Outcome.ToString(),
            ["cause"] = e.Cause?.ToString() ?? "",
        },
        InteractionRequested e => new()
        {
            ["interactionId"] = e.InteractionId.ToString(), ["kind"] = e.Kind.ToString(),
            ["options"] = string.Join(",", OptionIds(e.OptionsJson)), ["defaultOption"] = e.DefaultOptionId,
            ["subject"] = _redaction.Redact(e.SubjectJson),
            ["questionnaireSchemaHash"] = e.QuestionnaireSchemaRef?.Hash.ToString() ?? "",
            ["questionnaire"] = QuestionnaireText(e.QuestionnaireSchemaRef),
        },
        InteractionResolved e => new()
        {
            ["interactionId"] = e.InteractionId.ToString(), ["optionId"] = e.OptionId,
            ["state"] = e.State ?? "", ["answerHash"] = e.AnswerRef?.Hash.ToString() ?? "",
        },
        InteractionExpired e => new() { ["interactionId"] = e.InteractionId.ToString() },
        PlanItemAdded e => new() { ["planItemId"] = e.PlanItemId.ToString(), ["description"] = e.Description },
        PlanItemStarted e => new() { ["planItemId"] = e.PlanItemId.ToString() },
        PlanItemCompleted e => new() { ["planItemId"] = e.PlanItemId.ToString() },
        PlanItemBlocked e => new() { ["planItemId"] = e.PlanItemId.ToString(), ["reason"] = e.Reason },
        PlanItemFailed e => new() { ["planItemId"] = e.PlanItemId.ToString(), ["reason"] = e.Reason },
        DelegationCreated e => new()
        {
            ["delegationId"] = e.Delegation.DelegationId.ToString(),
            ["parentExecutionId"] = e.Delegation.ParentExecutionId.ToString(),
            ["childTaskId"] = e.Delegation.ChildTaskId.ToString(),
            ["childLaneId"] = e.Delegation.ChildLaneId.ToString(),
            ["priority"] = e.Delegation.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        DelegationAccepted e => new() { ["delegationId"] = e.DelegationId.ToString(), ["childExecutionId"] = e.ChildExecutionId.ToString() },
        DelegationReturned e => new() { ["delegationId"] = e.DelegationId.ToString(), ["resultHash"] = e.ResultRef.Hash.ToString() },
        DelegationFailed e => new() { ["delegationId"] = e.DelegationId.ToString(), ["reason"] = _redaction.Redact(e.Reason) },
        ExecutionJoinCreated e => new() { ["joinId"] = e.Join.JoinId.ToString(), ["ownerExecutionId"] = e.ExecutionId.ToString(), ["kind"] = e.Join.Policy.Kind.ToString() },
        ExecutionJoinResolved e => new() { ["joinId"] = e.JoinId.ToString(), ["members"] = string.Join(",", e.SatisfyingExecutionIds) },
        ExecutionJoinFailed e => new() { ["joinId"] = e.JoinId.ToString(), ["reason"] = _redaction.Redact(e.Reason) },
        FanOutGroupCreated e => new() { ["groupId"] = e.Group.GroupId.ToString(), ["ownerExecutionId"] = e.ExecutionId.ToString(), ["policy"] = e.Group.FanInPolicy.ToString(), ["delegationIds"] = string.Join(",", e.Group.MemberDelegationIds) },
        FanOutGroupMemberReplaced e => new() { ["groupId"] = e.GroupId.ToString(), ["previousDelegationId"] = e.PreviousDelegationId.ToString(), ["replacementDelegationId"] = e.ReplacementDelegationId.ToString() },
        FanOutGroupResolved e => new() { ["groupId"] = e.GroupId.ToString(), ["memberResultIds"] = string.Join(",", e.MemberResultRefs.Select(result => result.Id)), ["aggregateId"] = e.AggregateRef?.Id.ToString() ?? "" },
        SupervisionBindingCreated e => new() { ["bindingId"] = e.Binding.BindingId.ToString(), ["subjectExecutionId"] = e.Binding.SubjectExecutionId.ToString(), ["supervisorExecutionId"] = e.Binding.SupervisorExecutionId.ToString(), ["policyRevision"] = e.Binding.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        SupervisionBindingAccepted e => new() { ["bindingId"] = e.BindingId.ToString() },
        SupervisionBindingFailed e => new() { ["bindingId"] = e.BindingId.ToString(), ["reason"] = _redaction.Redact(e.Reason) },
        ExecutionMailboxCreated e => new() { ["mailboxId"] = e.Mailbox.MailboxId.ToString(), ["ownerExecutionId"] = e.Mailbox.OwnerExecutionId.ToString() },
        ExecutionMailboxMessageReceived e => new() { ["mailboxId"] = e.Message.MailboxId.ToString(), ["messageId"] = e.Message.MessageId.ToString(), ["senderExecutionId"] = e.Message.SenderExecutionId?.ToString() ?? "" },
        ExecutionMailboxMessageAcknowledged e => new() { ["mailboxId"] = e.MailboxId.ToString(), ["messageId"] = e.MessageId.ToString() },
        WakeRequestCreated e => new() { ["wakeRequestId"] = e.Request.WakeRequestId.ToString(), ["targetExecutionId"] = e.Request.TargetExecutionId.ToString(), ["sourceEventId"] = e.Request.SourceEvent.EventId.ToString(), ["reason"] = _redaction.Redact(e.Request.Reason) },
        WakeRequestAccepted e => new() { ["wakeRequestId"] = e.WakeRequestId.ToString() },
        WakeRequestResolved e => new() { ["wakeRequestId"] = e.WakeRequestId.ToString() },
        WakeRequestFailed e => new() { ["wakeRequestId"] = e.WakeRequestId.ToString(), ["reason"] = _redaction.Redact(e.Reason) },
        AgentResultProduced e => new() { ["executionId"] = e.ExecutionId.ToString(), ["resultId"] = e.ResultRef.Id.ToString(), ["revision"] = e.ResultRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), ["schemaId"] = e.ResultSchemaId },
        ResultDispositionRecorded e => new() { ["executionId"] = e.ExecutionId.ToString(), ["resultId"] = e.Disposition.ResultRef.Id.ToString(), ["outcome"] = e.Disposition.Outcome.ToString(), ["evaluatorExecutionId"] = e.Disposition.EvaluatorExecutionId?.ToString() ?? "", ["reason"] = _redaction.Redact(e.Disposition.Reason) },
        ValidationStateRecorded e => new() { ["executionId"] = e.ExecutionId.ToString(), ["runId"] = e.State.Scope.RunId?.ToString() ?? "", ["taskId"] = e.State.Scope.TaskId?.ToString() ?? "", ["laneId"] = e.State.Scope.LaneId?.ToString() ?? "", ["level"] = e.State.Level.ToString(), ["status"] = e.State.Status.ToString(), ["requiredChecks"] = string.Join(",", e.State.RequiredChecks), ["toolBackedReceiptCount"] = e.State.EvidenceRefs.Count(item => item.Kind == EvidenceKind.ToolBacked).ToString(System.Globalization.CultureInfo.InvariantCulture) },
        IntegrationStatusRecorded e => new() { ["executionId"] = e.ExecutionId.ToString(), ["runId"] = e.Status.Scope.RunId?.ToString() ?? "", ["taskId"] = e.Status.Scope.TaskId?.ToString() ?? "", ["laneId"] = e.Status.Scope.LaneId?.ToString() ?? "", ["status"] = e.Status.Status.ToString(), ["toolBackedReceiptCount"] = e.Status.EvidenceRefs.Count(item => item.Kind == EvidenceKind.ToolBacked).ToString(System.Globalization.CultureInfo.InvariantCulture) },
        ProgressStalled e => new()
        {
            ["planItemId"] = e.PlanItemId.ToString(),
            ["turns"] = e.TurnsWithoutProgress.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        StallResponseSelected e => new()
        {
            ["planItemId"] = e.PlanItemId.ToString(),
            ["policy"] = e.Policy.ToString(),
            ["step"] = e.Step.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["skipped"] = string.Join(",", e.Skipped),
        },
        _ => null,
    };

    private static string Mode(RunMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>Texto de las partes del input (array JSON de strings, o un string suelto).</summary>
    private static string InputText(string partsJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(partsJson);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return root.GetString() ?? "";
            }

            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return string.Join(" ", root.EnumerateArray()
                    .Where(part => part.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(part => part.GetString()));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // partes ilegibles: se muestra tal cual
        }

        return partsJson;
    }

    private string QuestionnaireText(ArtifactRef? content)
    {
        if (content is null || _artifacts is null) return "";
        try { return _redaction.Redact(_artifacts.GetText(content.Hash) ?? ""); }
        catch (InvalidDataException) { return ""; }
    }

    private string ArtifactText(ArtifactRef? content)
    {
        if (content is null || _artifacts is null)
        {
            return "";
        }

        try
        {
            return _artifacts.GetText(content.Hash) ?? "";
        }
        catch (InvalidDataException)
        {
            return ""; // artifact corrupto: no se muestra (el verify lo reporta aparte)
        }
    }

    private static IEnumerable<string> OptionIds(string optionsJson)
    {
        var ids = new List<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var option in document.RootElement.EnumerateArray())
                {
                    if (option.ValueKind == System.Text.Json.JsonValueKind.Object
                        && option.TryGetProperty("id", out var id) && id.GetString() is { } text)
                    {
                        ids.Add(text);
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // opciones ilegibles: ninguna
        }

        return ids;
    }
}
