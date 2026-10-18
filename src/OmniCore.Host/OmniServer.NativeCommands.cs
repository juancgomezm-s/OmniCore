namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;
using OmniCore.Infrastructure;
using System.Text.Json;

public sealed partial class OmniServer
{
    internal sealed record NativeCommandPayload(string CanonicalCommandId, string Kind, string[] Arguments,
        string? Prompt, string? Origin, string? ForcedToolName, string? WorkflowId, string? WorkflowVersion);

    private static string NativeCommandPurpose(SessionId session, CommandId invocation) =>
        "native-command:" + session + ":" + invocation;

    private CommandAck HandleNativeCommandAction(WireEnvelope command, string commandName,
        CancellationToken cancellationToken)
    {
        if (_artifacts is not IProtectedArtifactStore protectedStore)
            return new CommandAck(command.MessageId, "error", "command.protected_store_unavailable",
                RuntimeCommandOutcome.Rejected());
        using var request = JsonDocument.Parse(command.PayloadJson);
        var root = request.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !WireString(root, "expectedSessionId", out var expectedSession)
            || !Guid.TryParseExact(expectedSession, "D", out var sessionGuid)
            || expectedSession != sessionGuid.ToString("D")
            || !TryNativeNullableId(root, "expectedRunId", out var expectedRun)
            || !TryNativeNullableRevision(root, "expectedModeRevision", out var expectedMode)
            || !root.TryGetProperty("expectedPolicyRevision", out var policyNode)
            || !policyNode.TryGetInt64(out var expectedPolicy) || expectedPolicy < 1
            || !root.TryGetProperty("expectedSelectionRevision", out var selectionNode)
            || !selectionNode.TryGetInt32(out var expectedSelection) || expectedSelection < 0
            || !TryNativeNullableRevision(root, "expectedRoutingPolicyRevision", out var expectedRouting)
            || !WireString(root, "catalogRevision", out var expectedCatalog)
            || !WireString(root, "sourceFingerprint", out var expectedSource)
            || !NativeCommandIsSha256(expectedCatalog) || !NativeCommandIsSha256(expectedSource))
            return NativeCommandRejected(command.MessageId, "command.invalid_request");

        if (_lastSessionId != new SessionId(sessionGuid))
            return NativeCommandRejected(command.MessageId, "command.session_changed");
        if (commandName == "command.invoke.user")
            return AdmitNativeCommand(command, root, expectedRun, expectedMode, expectedPolicy, expectedSelection,
                expectedRouting, expectedCatalog, expectedSource, protectedStore, cancellationToken);

        if (!WireString(root, "invocationId", out var invocationText)
            || !Guid.TryParseExact(invocationText, "D", out var invocationGuid)
            || invocationText != invocationGuid.ToString("D"))
            return NativeCommandRejected(command.MessageId, "command.invalid_request");
        if (commandName == "workflow.continue")
            return AcceptNativeWorkflowContinuation(command, new CommandId(invocationGuid), expectedRun,
                expectedMode, expectedPolicy, expectedSelection, expectedRouting, expectedCatalog, expectedSource);
        return AcceptNativeCommandContinuation(command, new CommandId(invocationGuid), expectedRun, expectedMode,
            expectedPolicy, expectedSelection, expectedRouting, expectedCatalog, expectedSource);
    }

    private CommandAck AcceptNativeWorkflowContinuation(WireEnvelope command, CommandId invocation,
        RunId? expectedRun, long? expectedMode, long expectedPolicy, int expectedSelection,
        long? expectedRouting, string expectedCatalog, string expectedSource)
    {
        var before = _lastSessionId;
        if (before is not { } session || expectedRun is not { } run || !EnsureWriterOwnershipForCommand())
            return NativeCommandRejected(command.MessageId, "workflow.session_unavailable");
        lock (_modeAuthorityMutationGate)
        {
            var history = _store.ReadFrom(session, 1);
            var admissionEvent = history.LastOrDefault(evt => _codecs.Decode(evt) is NativeCommandInvocationAdmitted admitted
                && admitted.InvocationId == invocation && admitted.SessionId == session
                && admitted.RunId == run && admitted.Kind == "compiledWorkflow");
            if (admissionEvent is null || _codecs.Decode(admissionEvent) is not NativeCommandInvocationAdmitted admission
                || !history.Any(evt => evt.Sequence > admissionEvent.Sequence
                    && _codecs.Decode(evt) is NativeCommandInvocationStarted started
                    && started.InvocationId == invocation && started.Kind == "compiledWorkflow")
                || history.Any(evt => evt.Sequence > admissionEvent.Sequence
                    && _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                    && finished.InvocationId == invocation))
                return NativeCommandRejected(command.MessageId, "workflow.recovery_unavailable");
            if (!TryReadNativeCommandPayload(invocation, out var readable, out var payloadJson)
                || readable != admission || !TryDecodeNativeCommandPayload(payloadJson, out var payload)
                || payload.Kind != "compiledWorkflow"
                || !FixedHashEquals(expectedSource, admission.SourceFingerprint))
                return NativeCommandRejected(command.MessageId, "workflow.admission_invalid");
            var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
            if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required" })
                return NativeCommandRejected(command.MessageId, "workflow.execution_uncertain");
            if (admission.RunId != run || admission.ExpectedModeRevision != expectedMode
                || admission.ExpectedPolicyRevision != expectedPolicy
                || admission.ExpectedSelectionRevision != expectedSelection
                || admission.ExpectedRoutingPolicyRevision != expectedRouting
                || !FixedHashEquals(admission.CatalogRevision, expectedCatalog)
                || !NativeCommandScopeMatches(session, expectedRun, expectedMode, expectedPolicy,
                    expectedSelection, expectedRouting, expectedCatalog))
                return NativeCommandRejected(command.MessageId, "workflow.scope_changed");

            WorkflowRuntimeAuthorization authorization;
            try
            {
                authorization = AuthorizeNativeWorkflowContinuation(invocation, admission, payload);
                _ = ReadWorkflowContext(authorization);
                if (!NativeWorkflowInvocationIsReady(authorization, out _, out _))
                    return NativeCommandRejected(command.MessageId, "workflow.continuation_unavailable");
            }
            catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
                or ArgumentException or KeyNotFoundException)
            {
                return NativeCommandRejected(command.MessageId, "workflow.authorization_unavailable");
            }
            _lastWorkflowRequested = authorization.Request;
            _lastWorkflowInvocation = new WorkflowInvocation(invocation.ToString(), authorization.Request,
                session, run);
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session,
                _store.CurrentSequence(session), new CommandId(Guid.Parse(command.MessageId)));
        }
    }

    internal bool NativeWorkflowContinuationIsAllowed(CommandId invocation, out string reasonCode,
        out string? workflowId, out string? stage)
    {
        reasonCode = "workflow.recovery_unavailable";
        workflowId = null;
        stage = null;
        if (_lastSessionId is not { } session || _lastRunId is not { } run) return false;
        var history = _store.ReadFrom(session, 1);
        var admissionEvent = history.LastOrDefault(evt => _codecs.Decode(evt) is NativeCommandInvocationAdmitted admitted
            && admitted.InvocationId == invocation && admitted.SessionId == session
            && admitted.RunId == run && admitted.Kind == "compiledWorkflow");
        if (admissionEvent is null || _codecs.Decode(admissionEvent) is not NativeCommandInvocationAdmitted admission
            || !history.Any(evt => evt.Sequence > admissionEvent.Sequence
                && _codecs.Decode(evt) is NativeCommandInvocationStarted started
                && started.InvocationId == invocation && started.Kind == "compiledWorkflow")
            || history.Any(evt => evt.Sequence > admissionEvent.Sequence
                && _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                && finished.InvocationId == invocation)) return false;
        var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
        if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required" })
        { reasonCode = "workflow.execution_uncertain"; return false; }
        if (!NativeCommandRunIsQuiescent(session, run))
        { reasonCode = "workflow.run_not_quiescent"; return false; }
        if (!TryReadNativeCommandPayload(invocation, out var readable, out var json) || readable != admission
            || !TryDecodeNativeCommandPayload(json, out var payload) || payload.Kind != "compiledWorkflow"
            || payload.WorkflowId is not { Length: > 0 } id || payload.WorkflowVersion is not { Length: > 0 })
        { reasonCode = "workflow.admission_invalid"; return false; }
        workflowId = id;
        try
        {
            var authorization = AuthorizeNativeWorkflowContinuation(invocation, admission, payload);
            _ = ReadWorkflowContext(authorization);
            if (!NativeWorkflowInvocationIsReady(authorization, out reasonCode, out stage)) return false;
            return true;
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or ArgumentException or KeyNotFoundException)
        {
            reasonCode = failure is InvalidDataException ? "workflow.journal_invalid" : "workflow.authorization_unavailable";
            return false;
        }
    }

    private bool NativeWorkflowInvocationIsReady(WorkflowRuntimeAuthorization authorization,
        out string reasonCode, out string? stageName)
    {
        reasonCode = "workflow.stage_state_uncertain";
        stageName = null;
        if (!WorkflowAuthorizationIsCurrent(authorization))
        { reasonCode = "workflow.authorization_unavailable"; return false; }
        foreach (var stage in Enum.GetValues<WorkflowStage>())
        {
            var found = FindWorkflowStage(ForStage(authorization, stage, null), stage);
            if (found is null)
            {
                stageName = stage.ToString();
                reasonCode = "";
                return true;
            }
            var (item, _, phase) = found.Value;
            stageName = stage.ToString();
            if (phase == PreM6RecordPhase.Failed || item.State == PlanItemState.Failed)
            { reasonCode = "workflow.stage_failed"; return false; }
            if (item.State == PlanItemState.Completed && phase is PreM6RecordPhase.Resolved or PreM6RecordPhase.Recorded)
                continue;
            if (phase is PreM6RecordPhase.Created or PreM6RecordPhase.Returned)
            { reasonCode = ""; return true; }
            if (phase == PreM6RecordPhase.Accepted)
            { reasonCode = "workflow.child_pending"; return false; }
            reasonCode = "workflow.stage_state_uncertain";
            return false;
        }
        stageName = "completion";
        reasonCode = "";
        return true;
    }

    private CommandAck AdmitNativeCommand(WireEnvelope command, JsonElement root, RunId? expectedRun,
        long? expectedMode, long expectedPolicy, int expectedSelection, long? expectedRouting,
        string expectedCatalog, string expectedSource, IProtectedArtifactStore protectedStore,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.MessageId, out var commandGuid)
            || !WireString(root, "canonicalCommandId", out var canonicalId)
            || string.IsNullOrWhiteSpace(canonicalId) || canonicalId.Length > 256
            || !root.TryGetProperty("arguments", out var argsNode) || argsNode.ValueKind != JsonValueKind.Array
            || argsNode.GetArrayLength() > 32)
            return NativeCommandRejected(command.MessageId, "command.invalid_request");
        var arguments = new List<string>();
        var argumentChars = 0;
        foreach (var item in argsNode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value || value.Length > 16_384)
                return NativeCommandRejected(command.MessageId, "command.invalid_request");
            argumentChars += value.Length;
            if (argumentChars > 65_536) return NativeCommandRejected(command.MessageId, "command.payload_too_large");
            arguments.Add(value);
        }
        var session = _lastSessionId;
        if (session is null || session.Value.ToString() != WireStringValue(root, "expectedSessionId"))
            return NativeCommandRejected(command.MessageId, "command.session_changed");
        var invocation = new CommandId(commandGuid);
        var before = _store.CurrentSequence(session);
        try
        {
            lock (_modeAuthorityMutationGate)
            {
                if (_lastSessionId != session || !EnsureWriterOwnershipForCommand()
                    || !NativeCommandScopeMatches(session, expectedRun, expectedMode, expectedPolicy,
                        expectedSelection, expectedRouting, expectedCatalog))
                    return NativeCommandRejected(command.MessageId, "command.scope_changed");
                var context = expectedRun is null
                    ? new CommandContext(session, null, ReadModePreference().Mode, _workspaceRoot, false,
                        "A Run is required for workflow authorization.")
                    : CommandContextForCurrentRun();
                var nativeSnapshot = CurrentNativeCommandCatalog();
                if (!FixedHashEquals(expectedCatalog, nativeSnapshot.CatalogRevision))
                    return NativeCommandRejected(command.MessageId, "command.catalog_changed");
                var nativeEntry = nativeSnapshot.Commands.SingleOrDefault(entry =>
                    entry.CanonicalCommandId == canonicalId);
                if (nativeEntry is not { Enabled: true }
                    || !FixedHashEquals(expectedSource, nativeEntry.SourceFingerprint))
                    return NativeCommandRejected(command.MessageId, nativeEntry?.DisabledReason ?? "command.source_changed");
                var snapshot = _commandService.Catalog(context);
                var catalogEntry = snapshot.Commands.SingleOrDefault(entry => entry.Id == canonicalId);
                var handler = _commandService.ResolveCanonical(canonicalId);
                var fingerprint = CommandService.Fingerprint(handler.Descriptor);
                if (catalogEntry is null || !catalogEntry.Enabled || !FixedHashEquals(expectedSource, fingerprint))
                    return NativeCommandRejected(command.MessageId, "command.source_changed");

                var outcome = _commandService.InvokeAsync(new CommandInvocation(canonicalId, arguments, "NativeCommand"),
                    context, cancellationToken).AsTask().GetAwaiter().GetResult();
                var payload = outcome switch
                {
                    PromptCommandRequested prompt when prompt.Text.Length <= 65_536
                        => new NativeCommandPayload(canonicalId,
                            prompt.ForcedToolName is null ? "prompt" : "forcedExtension", arguments.ToArray(),
                            prompt.Text, prompt.Origin, prompt.ForcedToolName, null, null),
                    WorkflowRequested workflow when workflow.Arguments.Sum(value => (long)value.Length) <= 65_536
                        => new NativeCommandPayload(canonicalId, "compiledWorkflow", arguments.ToArray(), null, null,
                            null, workflow.Workflow.Id, workflow.Workflow.Version),
                    _ => throw new InvalidOperationException("command.unsupported_outcome"),
                };
                var serialized = EncodeNativeCommandPayload(payload);
                if (Encoding.UTF8.GetByteCount(serialized) > 92_000)
                    return NativeCommandRejected(command.MessageId, "command.payload_too_large");
                var purpose = NativeCommandPurpose(session, invocation);
                using var publication = (_artifacts as IArtifactPublicationLease)?.AcquirePublicationLease(cancellationToken);
                var reference = protectedStore.PutProtectedText(serialized, purpose, "application/json", ArtifactKind.Other);
                if (reference.Size is <= 0 or > 131_072)
                    return NativeCommandRejected(command.MessageId, "command.payload_too_large");
                var security = ReadSessionSecurityPolicy(session);
                if (security?.PolicyRevision != expectedPolicy)
                    return NativeCommandRejected(command.MessageId, "command.policy_changed");
                if (expectedRun is null)
                {
                    // A blank Session may start only the built-in explanatory prompt. Create the
                    // canonical Run graph and its admission in one journal transaction; no synthetic
                    // user-input event, new Session, or mode/capability elevation is introduced.
                    if (canonicalId != "core:explain" || payload.Kind != "prompt")
                        return NativeCommandRejected(command.MessageId, "command.run_required");
                    var modePreference = ReadModePreference();
                    if (!FixedHashEquals(expectedCatalog, CurrentNativeCommandCatalog(modePreference).CatalogRevision))
                        return NativeCommandRejected(command.MessageId, "command.catalog_changed");
                    var safeObjective = new PiiRedactor().Redact(payload.Prompt ?? "");
                    if (string.IsNullOrWhiteSpace(safeObjective)) safeObjective = "Native prompt command: core:explain";
                    var profile = _agentProfiles.DefaultProfile;
                    var runControl = new RunControlService(_store, _codecs,
                        rootProfile: profile?.Id, rootProfileRevision: profile?.Revision,
                        rootProfileHash: profile is null ? null : AgentProfileFingerprint.Hash(profile),
                        initialReasoningSelection: runId =>
                        {
                            var preference = ReadReasoningPreference();
                            return preference.HasSelection
                                ? new RunReasoningPreferenceSelected(runId, 1, preference.Request,
                                    "UserDefault", "RunCreated", preference.Revision)
                                : null;
                        });
                    using var causation = CausationScope.Begin(new CommandCausation(invocation));
                    var run = runControl.StartRun(session, safeObjective, modePreference.Mode,
                        runId => new DomainEventPayload[]
                        {
                            new NativeCommandInvocationAdmitted(invocation, session, runId, canonicalId,
                                payload.Kind, expectedCatalog, fingerprint, expectedPolicy, 1, expectedSelection,
                                expectedRouting, reference),
                        }, DurabilityClass.Barrier);
                    _lastRunId = run;
                    _lastSnapshot = null;
                    _lastWorkingStateText = "";
                    SaveLastSession();
                    DispatchRunStartLifecycle(session, run);
                }
                else
                {
                    using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: expectedRun));
                    new EventStream(_store, _codecs, session).Append(new NativeCommandInvocationAdmitted(
                        invocation, session, expectedRun, canonicalId, payload.Kind, expectedCatalog, fingerprint,
                        expectedPolicy, expectedMode, expectedSelection, expectedRouting, reference), DurabilityClass.Barrier);
                }
                return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    session, before, invocation);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            return before < _store.CurrentSequence(session)
                ? FailedDurableCommandAck(command, session, before, "command.admission_unavailable")
                : NativeCommandRejected(command.MessageId, "command.admission_rejected");
        }
    }

    private CommandAck AcceptNativeCommandContinuation(WireEnvelope command, CommandId invocation,
        RunId? expectedRun, long? expectedMode, long expectedPolicy, int expectedSelection,
        long? expectedRouting, string expectedCatalog, string expectedSource)
    {
        if (_lastSessionId is not { } session || !EnsureWriterOwnershipForCommand())
            return NativeCommandRejected(command.MessageId, "command.session_unavailable");
        lock (_modeAuthorityMutationGate)
        {
            var history = _store.ReadFrom(session, 1);
            var admittedEvent = history.LastOrDefault(item => _codecs.Decode(item) is NativeCommandInvocationAdmitted admitted
                && admitted.InvocationId == invocation && admitted.SessionId == session);
            if (admittedEvent is null || _codecs.Decode(admittedEvent) is not NativeCommandInvocationAdmitted admittedPayload)
                return NativeCommandRejected(command.MessageId, "command.recovery_unavailable");
            if (history.Any(item => item.Sequence > admittedEvent.Sequence
                && _codecs.Decode(item) is NativeCommandInvocationStarted started && started.InvocationId == invocation))
                return NativeCommandRejected(command.MessageId, "command.recovery_blocked");
            if (!NativeCommandScopeMatches(session, expectedRun, expectedMode, expectedPolicy, expectedSelection,
                    expectedRouting, expectedCatalog)
                || admittedPayload.ExpectedPolicyRevision != expectedPolicy
                || admittedPayload.ExpectedModeRevision != expectedMode
                || admittedPayload.ExpectedSelectionRevision != expectedSelection
                || admittedPayload.ExpectedRoutingPolicyRevision != expectedRouting
                || !FixedHashEquals(admittedPayload.CatalogRevision, expectedCatalog)
                || !FixedHashEquals(admittedPayload.SourceFingerprint, expectedSource))
                return NativeCommandRejected(command.MessageId, "command.scope_changed");
            var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
            if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: null or "cancelled" })
                return NativeCommandRejected(command.MessageId, "command.recovery_blocked");
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
    }

    private bool NativeCommandScopeMatches(SessionId session, RunId? expectedRun, long? expectedMode,
        long expectedPolicy, int expectedSelection, long? expectedRouting, string expectedCatalog)
    {
        if (_lastSessionId != session || !EnsureWriterOwnershipForCommand()
            || new RunControlService(_store, _codecs).ActiveRun(session) != expectedRun
            || ReadSessionSecurityPolicy(session)?.PolicyRevision != expectedPolicy)
            return false;
        if (expectedRun is { } activeRun)
        {
            if (!NativeCommandRunIsQuiescent(session, activeRun)) return false;
        }
        else
        {
            if (expectedMode is not null || HasSessionSecurityInFlightAuthority(session)
                || new RunControlService(_store, _codecs).UnreconciledEffects(session).Count != 0)
                return false;
        }
        var history = _store.ReadFrom(session, 1);
        var selection = SessionModelSelectionProjection.Read(history, _codecs, session);
        var routing = SessionRoutingAuthorization.Read(history, _codecs, session);
        if ((selection?.Revision ?? 0) != expectedSelection || routing?.Revision != expectedRouting)
            return false;
        var mode = expectedRun is null ? null : CurrentModeAuthority()?.Revision;
        if (mode != expectedMode) return false;
        return FixedHashEquals(expectedCatalog, CurrentNativeCommandCatalog().CatalogRevision);
    }

    private static CommandAck NativeCommandRejected(string commandId, string reason) =>
        new(commandId, "error", reason, RuntimeCommandOutcome.Rejected());

    private static bool NativeCommandIsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool TryNativeNullableId(JsonElement root, string name, out RunId? run)
    {
        run = null;
        if (!root.TryGetProperty(name, out var node)) return false;
        if (node.ValueKind == JsonValueKind.Null) return true;
        if (node.ValueKind != JsonValueKind.String || node.GetString() is not { } text
            || !Guid.TryParseExact(text, "D", out var value) || text != value.ToString("D")) return false;
        run = new RunId(value);
        return true;
    }

    private static bool TryNativeNullableRevision(JsonElement root, string name, out long? revision)
    {
        revision = null;
        if (!root.TryGetProperty(name, out var node)) return false;
        if (node.ValueKind == JsonValueKind.Null) return true;
        if (!node.TryGetInt64(out var value) || value < 1) return false;
        revision = value;
        return true;
    }

    private static string WireStringValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string EncodeNativeCommandPayload(NativeCommandPayload payload)
    {
        static string NullableField(string key, string? value) => value is null
            ? JsonObj.FieldRaw(key, "null") : JsonObj.Field(key, value);
        var args = "[" + string.Join(",", payload.Arguments.Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]";
        var fields = new List<string>
        {
            JsonObj.Field("canonicalCommandId", payload.CanonicalCommandId), JsonObj.Field("kind", payload.Kind),
            JsonObj.FieldRaw("arguments", args), NullableField("prompt", payload.Prompt),
            NullableField("origin", payload.Origin), NullableField("forcedToolName", payload.ForcedToolName),
            NullableField("workflowId", payload.WorkflowId), NullableField("workflowVersion", payload.WorkflowVersion),
        };
        return "{" + string.Join(",", fields) + "}";
    }

    internal bool TryReadNativeCommandPayload(CommandId invocation, out NativeCommandInvocationAdmitted admission,
        out string payloadJson)
    {
        admission = null!;
        payloadJson = "";
        if (_lastSessionId is not { } session || _artifacts is not IProtectedArtifactStore protectedStore) return false;
        var events = _store.ReadFrom(session, 1);
        var evt = events.LastOrDefault(item => _codecs.Decode(item) is NativeCommandInvocationAdmitted candidate
            && candidate.InvocationId == invocation && candidate.SessionId == session);
        if (evt is null || _codecs.Decode(evt) is not NativeCommandInvocationAdmitted decoded) return false;
        try
        {
            admission = decoded;
            payloadJson = protectedStore.GetProtectedText(decoded.ProtectedPayload,
                NativeCommandPurpose(session, invocation));
            return Encoding.UTF8.GetByteCount(payloadJson) <= 131_072;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            admission = null!;
            payloadJson = "";
            return false;
        }
    }

    internal bool TryBeginNativeCommand(CommandId invocation, bool explicitRecovery,
        out NativeCommandInvocationAdmitted admission, out NativeCommandPayload payload)
    {
        admission = null!;
        payload = null!;
        if (!TryReadNativeCommandPayload(invocation, out var admitted, out var payloadJson)
            || !TryDecodeNativeCommandPayload(payloadJson, out var decoded)) return false;
        lock (_modeAuthorityMutationGate)
        {
            var session = admitted.SessionId;
            if (_lastSessionId != session || admitted.RunId is not { } expectedRun
                || new RunControlService(_store, _codecs).ActiveRun(session) != expectedRun
                || !EnsureWriterOwnershipForCommand()
                || !NativeCommandScopeMatches(session, expectedRun, admitted.ExpectedModeRevision,
                    admitted.ExpectedPolicyRevision, admitted.ExpectedSelectionRevision,
                    admitted.ExpectedRoutingPolicyRevision, admitted.CatalogRevision)) return false;
            var history = _store.ReadFrom(session, 1);
            if (history.Any(evt => _codecs.Decode(evt) is NativeCommandInvocationStarted started
                    && started.InvocationId == invocation)
                || history.Any(evt => _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                    && finished.InvocationId == invocation)) return false;
            var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
            if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted" }
                || receipt.ExecutionState is not null
                    && !(explicitRecovery && receipt.ExecutionState == "cancelled")) return false;

            var liveCatalog = CurrentNativeCommandCatalog();
            var liveDescriptor = liveCatalog.Commands.SingleOrDefault(item =>
                item.CanonicalCommandId == admitted.CanonicalCommandId);
            if (liveDescriptor is not { Enabled: true }
                || !FixedHashEquals(liveDescriptor.SourceFingerprint, admitted.SourceFingerprint)) return false;

            WorkflowRequested? workflowToSet = null;
            PromptExpanded? promptToSet = null;
            if (decoded.Kind == "compiledWorkflow")
            {
                if (decoded.WorkflowId is not { Length: > 0 } workflowId
                    || decoded.WorkflowVersion is not { Length: > 0 } workflowVersion) return false;
                var requested = new WorkflowRequested(new WorkflowRef(workflowId, workflowVersion), decoded.Arguments);
                requested.Validate();
                workflowToSet = requested;
            }
            else if (decoded.Kind is "prompt" or "forcedExtension")
            {
                if (decoded.Prompt is not { Length: > 0 } prompt
                    || decoded.Kind == "forcedExtension" && decoded.ForcedToolName is not { Length: > 0 }
                    || decoded.Kind == "prompt" && decoded.ForcedToolName is not null) return false;
                promptToSet = new PromptExpanded(prompt, decoded.Origin ?? "NativeCommand");
            }
            else return false;

            if (decoded.CanonicalCommandId != admitted.CanonicalCommandId || decoded.Kind != admitted.Kind)
                return false;
            using var causation = CausationScope.Begin(new CommandCausation(invocation));
            new EventStream(_store, _codecs, session).Append(new NativeCommandInvocationStarted(invocation,
                decoded.Kind, expectedRun), DurabilityClass.Barrier);
            _lastWorkflowRequested = workflowToSet;
            _lastWorkflowInvocation = workflowToSet is null ? null
                : new WorkflowInvocation(invocation.ToString(), workflowToSet, session, expectedRun);
            _lastPromptExpanded = promptToSet;
            _lastForcedCommandTool = decoded.ForcedToolName;
            _pendingPromptOrigin = decoded.Origin;
            admission = admitted;
            payload = decoded;
            return true;
        }
    }

    internal void FinishNativeCommand(CommandId invocation, string outcome)
    {
        if (_lastSessionId is not { } session || !EnsureWriterOwnershipForCommand()) return;
        lock (_modeAuthorityMutationGate)
        {
            var history = _store.ReadFrom(session, 1);
            if (!history.Any(evt => _codecs.Decode(evt) is NativeCommandInvocationStarted started
                    && started.InvocationId == invocation)
                || history.Any(evt => _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                    && finished.InvocationId == invocation)) return;
            using var causation = CausationScope.Begin(new CommandCausation(invocation));
            new EventStream(_store, _codecs, session).Append(new NativeCommandInvocationFinished(invocation, outcome,
                new RunControlService(_store, _codecs).ActiveRun(session)), DurabilityClass.Barrier);
        }
    }

    internal bool NativeCommandRequiredEffectSucceeded(CommandId invocation)
    {
        if (!TryReadNativeCommandPayload(invocation, out var admission, out var payloadJson)
            || !TryDecodeNativeCommandPayload(payloadJson, out var payload)) return false;
        if (admission.Kind is "prompt" or "compiledWorkflow") return admission.Kind == payload.Kind;
        if (admission.Kind != "forcedExtension" || admission.RunId is not { } run
            || payload.Kind != "forcedExtension" || payload.ForcedToolName is not { Length: > 0 } forcedToolName)
            return false;

        var history = _store.ReadFrom(admission.SessionId, 1);
        var started = history.FirstOrDefault(evt => evt.RunId == run
            && _codecs.Decode(evt) is NativeCommandInvocationStarted item
            && item.InvocationId == invocation && item.Kind == "forcedExtension");
        if (started is null) return false;

        var runEvents = history.Where(evt => evt.RunId == run).ToArray();
        LaneId? rootLane;
        try
        {
            var runProjection = RunProjection.Replay(admission.SessionId, run, _codecs, runEvents);
            if (runProjection.RootTask is not { } rootTask) return false;
            var rootLanes = LaneProjection.Replay(_codecs, runEvents).ForTask(rootTask);
            if (rootLanes.Count != 1) return false;
            rootLane = rootLanes[0].Id;
        }
        catch (Exception)
        {
            return false;
        }
        if (rootLane is null) return false;
        var turnStarted = runEvents.FirstOrDefault(evt => evt.Sequence > started.Sequence
            && evt.LaneId == rootLane && _codecs.Decode(evt) is TurnStarted turn
            && turn.LaneId == rootLane);
        if (turnStarted?.TurnId is not { } requiredTurn) return false;

        var requested = new HashSet<ToolCallId>();
        var succeeded = new HashSet<ToolCallId>();
        foreach (var evt in runEvents.Where(evt => evt.Sequence > turnStarted.Sequence
            && evt.LaneId == rootLane && evt.TurnId == requiredTurn))
        {
            switch (_codecs.Decode(evt))
            {
                case ToolCallRequested call when string.Equals(call.ToolName, forcedToolName, StringComparison.Ordinal):
                    requested.Add(call.ToolCallId);
                    break;
                case ToolCallSucceeded success:
                    succeeded.Add(success.ToolCallId);
                    break;
            }
        }
        return requested.Overlaps(succeeded);
    }

    private string EncodeNativeCommandRecovery()
    {
        if (_lastSessionId is not { } session)
            return CommandCatalogJson.Encode(new NativeCommandRecoverySnapshot([]));
        var events = _store.ReadFrom(session, 1);
        var admitted = new Dictionary<CommandId, (NativeCommandInvocationAdmitted Event, long Sequence)>();
        var started = new HashSet<CommandId>();
        var finished = new Dictionary<CommandId, NativeCommandInvocationFinished>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case NativeCommandInvocationAdmitted admission when admission.SessionId == session:
                    admitted[admission.InvocationId] = (admission, evt.Sequence);
                    break;
                case NativeCommandInvocationStarted execution:
                    started.Add(execution.InvocationId);
                    break;
                case NativeCommandInvocationFinished completion:
                    finished[completion.InvocationId] = completion;
                    break;
            }
        }

        var receipts = _store is OmniCore.Infrastructure.SqliteEventStore receiptStore ? receiptStore : null;
        var rows = new List<NativeCommandRecoveryEntry>();
        foreach (var (id, value) in admitted.OrderByDescending(item => item.Value.Sequence).Take(32))
        {
            var admission = value.Event;
            var receipt = receipts?.ReadCommandReceipt(id.ToString());
            string state;
            string? reason;
            if (finished.TryGetValue(id, out var completion))
            {
                state = completion.OutcomeCode == "completed" ? "completed" : "blocked";
                reason = completion.OutcomeCode == "completed" ? null : "invocation_pipeline_incomplete";
            }
            else if (started.Contains(id))
            {
                state = "blocked";
                reason = "execution_started_or_uncertain";
            }
            else if (receipt is { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                         OutcomeKind: "Accepted", ExecutionState: null or "cancelled" })
            {
                state = "ready";
                reason = null;
            }
            else
            {
                state = "blocked";
                reason = "invocation_receipt_unavailable";
            }
            rows.Add(new NativeCommandRecoveryEntry(id.ToString(), admission.SessionId.ToString(),
                admission.RunId?.ToString(), admission.CanonicalCommandId, admission.Kind, state, reason, value.Sequence));
        }
        return CommandCatalogJson.Encode(new NativeCommandRecoverySnapshot(rows));
    }

    private string EncodeNativeWorkflowRecovery()
    {
        if (_lastSessionId is not { } session)
            return CommandCatalogJson.Encode(new NativeWorkflowRecoverySnapshot(null, null, null, null, []));
        var run = new RunControlService(_store, _codecs).ActiveRun(session);
        var policyRevision = ReadSessionSecurityPolicy(session)?.PolicyRevision;
        var mode = CurrentModeAuthority();
        if (run is null)
            return CommandCatalogJson.Encode(new NativeWorkflowRecoverySnapshot(session.ToString(), null,
                null, policyRevision, []));
        var events = _store.ReadFrom(session, 1);
        var admitted = new Dictionary<CommandId, (NativeCommandInvocationAdmitted Admission, long Sequence)>();
        var started = new HashSet<CommandId>();
        var finished = new Dictionary<CommandId, NativeCommandInvocationFinished>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case NativeCommandInvocationAdmitted value when value.SessionId == session
                    && value.RunId == run && value.Kind == "compiledWorkflow":
                    admitted[value.InvocationId] = (value, evt.Sequence);
                    break;
                case NativeCommandInvocationStarted value when value.Kind == "compiledWorkflow":
                    started.Add(value.InvocationId);
                    break;
                case NativeCommandInvocationFinished value:
                    finished[value.InvocationId] = value;
                    break;
            }
        }
        var rows = new List<NativeWorkflowRecoveryEntry>();
        foreach (var (id, value) in admitted.OrderByDescending(item => item.Value.Sequence).Take(16))
        {
            var admission = value.Admission;
            var state = "blocked";
            string? reason = "workflow_invocation_unavailable";
            string? stageName = null;
            string workflowId = "";
            if (TryReadNativeCommandPayload(id, out var payloadAdmission, out var payloadJson)
                && payloadAdmission == admission && TryDecodeNativeCommandPayload(payloadJson, out var decoded)
                && decoded.WorkflowId is { Length: > 0 } idValue)
                workflowId = idValue;
            if (finished.TryGetValue(id, out var completion))
            {
                state = completion.OutcomeCode == "completed" ? "completed" : "blocked";
                reason = completion.OutcomeCode == "completed" ? null : "workflow_invocation_incomplete";
            }
            else if (!started.Contains(id))
            {
                // The generic command recovery view owns the admitted-but-not-started case.
                continue;
            }
            else if (NativeWorkflowContinuationIsAllowed(id, out var reasonCode, out _, out var nextStage))
            { state = "ready"; reason = null; stageName = nextStage; }
            else
            {
                reason = reasonCode;
                state = reasonCode == "workflow.child_pending" ? "pending" : "blocked";
                stageName = nextStage;
            }
            rows.Add(new NativeWorkflowRecoveryEntry(id.ToString(), session.ToString(), run.ToString(),
                admission.CanonicalCommandId, workflowId, state, reason, stageName, value.Sequence));
        }
        return CommandCatalogJson.Encode(new NativeWorkflowRecoverySnapshot(session.ToString(), run.ToString(),
            mode?.Revision, policyRevision, rows));
    }

    private NativeCommandCatalogSnapshot CurrentNativeCommandCatalog(RunModePreference? modePreferenceOverride = null)
    {
        RefreshProjectExtensionCommands();
        var session = _lastSessionId;
        var run = session is { } currentSession
            ? new RunControlService(_store, _codecs).ActiveRun(currentSession) : null;
        var policy = session is { } policySession ? ReadSessionSecurityPolicy(policySession) : null;
        var modePreference = modePreferenceOverride ?? ReadModePreference();
        var mode = run is null ? null : CurrentModeAuthority();
        SessionModelSelection? selection = null;
        SessionRoutingPolicy? routing = null;
        if (session is { } selectedSession)
        {
            var history = _store.ReadFrom(selectedSession, 1);
            selection = SessionModelSelectionProjection.Read(history, _codecs, selectedSession);
            routing = SessionRoutingAuthorization.Read(history, _codecs, selectedSession);
        }

        var catalogContext = run is not null
            ? CommandContextForCurrentRun()
            : new CommandContext(session, null, modePreference.Mode, _workspaceRoot, false,
                "A Run is required for workflow authorization.");
        var catalog = _commandService.Catalog(catalogContext);
        var modelCatalog = SessionModelCatalog.Read(_platformPathsFactory(), CancellationToken.None);
        var revisionInput = new StringBuilder();
        foreach (var command in catalog.Commands.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            // Enabled/disabled is scoped by the separate Run/mode CAS fields. The catalog token
            // hashes command definitions and sources so it remains stable when the first Run is
            // created atomically with a native invocation.
            AppendRevisionPart(revisionInput, command.Id);
            AppendRevisionPart(revisionInput, command.Name);
            AppendRevisionPart(revisionInput, command.Kind);
            AppendRevisionPart(revisionInput, command.Source);
            AppendRevisionPart(revisionInput, command.SourceFingerprint ?? "");
            AppendRevisionPart(revisionInput, command.Description);
            AppendRevisionPart(revisionInput, command.Arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var argument in command.Arguments) AppendRevisionPart(revisionInput, argument);
            AppendRevisionPart(revisionInput, "command-end");
        }
        AppendRevisionPart(revisionInput, session?.ToString() ?? "");
        AppendRevisionPart(revisionInput, modePreference.Mode.ToString());
        AppendRevisionPart(revisionInput, modePreference.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendRevisionPart(revisionInput, policy?.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        AppendRevisionPart(revisionInput, selection?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0");
        AppendRevisionPart(revisionInput, routing?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        AppendRevisionPart(revisionInput, modelCatalog.Revision);
        var catalogRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput.ToString())));
        var hasQuiescentScope = session is { } current && policy is not null && (run is { } activeRun
            ? mode is not null && NativeCommandRunIsQuiescent(current, activeRun)
            : !HasSessionSecurityInFlightAuthority(current)
                && new RunControlService(_store, _codecs).UnreconciledEffects(current).Count == 0);
        var effectiveMode = mode?.Mode ?? modePreference.Mode;
        var workflowRootReady = session is { } workflowSession && run is { } workflowRun
            && mode?.Mode == RunMode.Orchestrate && HasNativeWorkflowRootContext(workflowSession, workflowRun);
        var commands = catalog.Commands.Select(command =>
        {
            var descriptor = _commandService.ResolveCanonical(command.Id).Descriptor;
            var kind = descriptor.Source.Kind == SourceKind.Extension ? "forcedExtension" : descriptor.Kind switch
            {
                CommandKind.Prompt => "prompt",
                CommandKind.Workflow => "compiledWorkflow",
                _ => "unsupported",
            };
            var modeCompatible = kind switch
            {
                "prompt" => effectiveMode is RunMode.Plan or RunMode.Act,
                "forcedExtension" => run is not null && mode?.Mode == RunMode.Act,
                "compiledWorkflow" => workflowRootReady,
                _ => false,
            };
            var firstPromptAllowed = run is not null || command.Id == "core:explain" && kind == "prompt";
            var enabled = command.Enabled && hasQuiescentScope && firstPromptAllowed && modeCompatible;
            var disabledReason = enabled ? null : command.DisabledReason
                ?? (policy is null ? "A configured Session security policy is required." : !firstPromptAllowed
                    ? "This command requires an existing Session Run." : !hasQuiescentScope
                        ? "The Session Run must be quiescent." : kind == "compiledWorkflow" && !workflowRootReady
                            ? "A live root execution with its persisted context is required for this workflow."
                            : !modeCompatible
                    ? "The command is unavailable in the current Run mode." : "The Session Run must be quiescent.");
            return new NativeCommandCatalogEntry(command.Id, kind, enabled, disabledReason,
                CommandService.Fingerprint(descriptor), command.Description, command.Arguments);
        }).ToArray();
        return new NativeCommandCatalogSnapshot(session?.ToString(), run?.ToString(), mode?.Revision,
            policy?.PolicyRevision, selection?.Revision ?? 0, routing?.Revision, catalogRevision, commands);
    }

    private static void AppendRevisionPart(StringBuilder target, string value) =>
        target.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append('\n');

    private bool NativeCommandRunIsQuiescent(SessionId session, RunId run)
    {
        if (HasSessionSecurityInFlightAuthority(session)) return false;
        var history = _store.ReadFrom(session, 1);
        if (IsSessionTurnInFlight(history, run)) return false;
        return RunProjection.Replay(session, run, _codecs, EventsForRun(history, run)).State == RunState.Running;
    }

    internal static bool TryDecodeNativeCommandPayload(string json, out NativeCommandPayload payload)
    {
        payload = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !WireString(root, "canonicalCommandId", out var canonicalId)
                || !WireString(root, "kind", out var kind)
                || !root.TryGetProperty("arguments", out var argsNode) || argsNode.ValueKind != JsonValueKind.Array)
                return false;
            var arguments = new List<string>();
            foreach (var item in argsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } argument) return false;
                arguments.Add(argument);
            }
            string? ReadNullable(string name) => root.TryGetProperty(name, out var item)
                && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            payload = new NativeCommandPayload(canonicalId, kind, arguments.ToArray(), ReadNullable("prompt"),
                ReadNullable("origin"), ReadNullable("forcedToolName"), ReadNullable("workflowId"),
                ReadNullable("workflowVersion"));
            return true;
        }
        catch (JsonException) { return false; }
    }
}
