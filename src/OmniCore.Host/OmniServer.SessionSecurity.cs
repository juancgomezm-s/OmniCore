namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

public sealed partial class OmniServer
{
    private static string EncodeSessionSecurityPolicy(SessionSecurityPolicySet? policy)
    {
        if (policy is null) return "null";
        static string Q(string value) => "\"" + JsonObj.Escape(value) + "\"";
        var capabilities = "[" + string.Join(",", policy.CapabilityCeiling.Order()
            .Select(value => Q(value.ToString()))) + "]";
        return "{\"schemaVersion\":1,\"sessionId\":" + Q(policy.SessionId.ToString())
            + ",\"revision\":" + policy.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"category\":" + Q(policy.Category.ToString()) + ",\"presetId\":" + Q(policy.PresetId)
            + ",\"capabilityCeiling\":" + capabilities + ",\"toolMode\":" + Q(policy.ToolMode.ToString())
            + ",\"maxVisibleTools\":" + policy.MaxVisibleTools.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"allowToolDiscovery\":" + (policy.AllowToolDiscovery ? "true" : "false")
            + ",\"fileMutationMode\":" + Q(policy.FileMutationMode.ToString())
            + ",\"deletePolicy\":" + Q(policy.DeletePolicy.ToString())
            + ",\"moveOrRenamePolicy\":" + Q(policy.MoveOrRenamePolicy.ToString())
            + ",\"maxFilesPerTurn\":" + policy.MaxFilesPerTurn.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"maxChangedLinesPerTurn\":" + policy.MaxChangedLinesPerTurn.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"maxRewriteRatio\":" + policy.MaxRewriteRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + ",\"requirePriorRead\":" + (policy.RequirePriorRead ? "true" : "false")
            + ",\"requireExpectedVersionToken\":" + (policy.RequireExpectedVersionToken ? "true" : "false")
            + ",\"requirePostEditValidation\":" + (policy.RequirePostEditValidation ? "true" : "false")
            + ",\"allowParallelMutations\":" + (policy.AllowParallelMutations ? "true" : "false")
            + ",\"maxModelStepsPerTurn\":" + policy.MaxModelStepsPerTurn.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"maxToolCallsPerTurn\":" + policy.MaxToolCallsPerTurn.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"allowOrchestration\":" + (policy.AllowOrchestration ? "true" : "false")
            + ",\"maxGenerationSeconds\":" + policy.MaxGenerationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"provenance\":" + Q(policy.Provenance.ToString()) + ",\"source\":" + Q(policy.Source.ToString()) + "}";
    }

    internal SessionSecurityPolicySet? ReadSessionSecurityPolicy(SessionId session)
    {
        SessionSecurityPolicySet? current = null;
        foreach (var envelope in _store.ReadFrom(session, 1))
        {
            if (envelope.SessionId != session) throw new InvalidDataException("Session journal identity mismatch.");
            if (_codecs.Decode(envelope) is not SessionSecurityPolicySet policy) continue;
            policy.Validate();
            if (policy.SessionId != envelope.SessionId)
                throw new InvalidDataException("Session security payload identity mismatch.");
            if (current is null && policy.Source is not (SessionSecurityPolicySource.UserDefault
                or SessionSecurityPolicySource.LegacyMigration or SessionSecurityPolicySource.ImportReview))
                throw new InvalidDataException("The first Session security event must establish trusted provenance.");
            if (policy.PolicyRevision != (current?.PolicyRevision ?? 0) + 1)
                throw new InvalidDataException("Session security policy revision is discontinuous.");
            if (current is not null && current.Provenance != policy.Provenance)
                throw new InvalidDataException("Session security provenance cannot be changed by a policy selection.");
            current = policy;
        }
        return current;
    }

    internal SessionSecurityPolicySet EnsureSessionSecurityPolicy(SessionId session)
    {
        var current = ReadSessionSecurityPolicy(session);
        if (current is not null) return current;

        var journal = _store.ReadFrom(session, 1);
        var created = journal.Select(envelope =>
        {
            if (envelope.SessionId != session)
                throw new InvalidDataException("Session journal identity mismatch.");
            return _codecs.Decode(envelope);
        }).OfType<SessionCreated>().FirstOrDefault();
        if (created is null || created.SessionId != session)
            throw new InvalidDataException("Cannot initialize security policy for an unknown or invalid Session.");

        var migrated = SessionSecurityPolicySet.FromPreset(session, 1, ModelPolicyCategory.ObserveOnly,
            SessionSecurityProvenance.LegacyMigrated, SessionSecurityPolicySource.LegacyMigration,
            DateTimeOffset.UtcNow);
        var stream = new EventStream(_store, _codecs, session);
        stream.AppendIfNoEventsOfType(migrated.Type(), migrated, DurabilityClass.Barrier);
        return ReadSessionSecurityPolicy(session)
            ?? throw new InvalidDataException("Session security policy initialization did not persist.");
    }

    internal CommandAck SetSessionSecurityPolicy(WireEnvelope command, IReadOnlyDictionary<string, string> fields)
    {
        if (_lastSessionId is not { } active || !fields.TryGetValue("sessionId", out var rawSession)
            || !Guid.TryParse(rawSession, out var parsedSession) || parsedSession != active.Value
            || !fields.TryGetValue("expectedRevision", out var rawRevision)
            || !long.TryParse(rawRevision, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedRevision)
            || expectedRevision < 1 || !fields.TryGetValue("category", out var rawCategory)
            || !Enum.TryParse<ModelPolicyCategory>(rawCategory, ignoreCase: false, out var category)
            || !Enum.IsDefined(category) || category == ModelPolicyCategory.Custom)
            return new CommandAck(command.MessageId, "error", "session_security.invalid_request",
                RuntimeCommandOutcome.Rejected());

        var commandId = new CommandId(Guid.Parse(command.MessageId));
        var before = _store.CurrentSequence(active);
        try
        {
            lock (_modeAuthorityMutationGate)
            {
                var existing = ReadSessionSecurityPolicy(active);
                if (existing is null && expectedRevision != 1)
                    return CommandOutcomeAck(command.MessageId, "error", "session_security.stale_revision",
                        RuntimeCommandOutcome.Rejected(), active, before, commandId);
                var current = existing ?? EnsureSessionSecurityPolicy(active);
                if (current.PolicyRevision != expectedRevision)
                    return CommandOutcomeAck(command.MessageId, "error", "session_security.stale_revision",
                        RuntimeCommandOutcome.Rejected(), active, before, commandId);
                var next = SessionSecurityPolicySet.FromPreset(active, checked(current.PolicyRevision + 1), category,
                    current.Provenance, SessionSecurityPolicySource.UserSelection, DateTimeOffset.UtcNow);
                var activeRun = new RunControlService(_store, _codecs).ActiveRun(active);
                if (activeRun is not null && !current.IsMonotonicTightening(next))
                    return CommandOutcomeAck(command.MessageId, "error", "session_security.run_requires_tightening",
                        RuntimeCommandOutcome.Deferred("ActiveRunPolicyMustTighten"), active, before, commandId);
                if (HasSessionSecurityInFlightAuthority(active))
                    return CommandOutcomeAck(command.MessageId, "error", "session_security.execution_in_flight",
                        RuntimeCommandOutcome.Deferred("ExternalOrToolExecutionMustQuiesce"), active, before, commandId);
                var stream = new EventStream(_store, _codecs, active);
                stream.AppendAdmitted(next, DurabilityClass.Barrier, journal =>
                {
                    var latest = journal.Select(_codecs.Decode).OfType<SessionSecurityPolicySet>().LastOrDefault();
                    if (latest is null || latest.PolicyRevision != expectedRevision
                        || latest.Provenance != current.Provenance || latest.SessionId != active)
                        throw new SessionSecurityRevisionConflictException();
                });
                return CommandOutcomeAck(command.MessageId, "ok", null,
                    RuntimeCommandOutcome.Accepted(), active, before, commandId);
            }
        }
        catch (SessionSecurityRevisionConflictException)
        {
            return CommandOutcomeAck(command.MessageId, "error", "session_security.stale_revision",
                RuntimeCommandOutcome.Rejected(), active, before, commandId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            return FailedDurableCommandAck(command.MessageId, active, before,
                "session_security.update_failed: " + exception.GetType().Name, restoreRunIdentity: false);
        }
    }

    internal string CurrentSessionSecurityJson() => _lastSessionId is { } session
        ? EncodeSessionSecurityPolicy(ReadSessionSecurityPolicy(session)) : "null";

    // A dynamic Session ceiling revokes future dispatches, but it cannot stop a process that has
    // already crossed the Host boundary. Defer the revision until every started effect and child
    // execution has a durable terminal event. Admission uses the same authority gate, so no new
    // child can be launched between this check and the policy CAS.
    private bool HasSessionSecurityInFlightAuthority(SessionId session)
    {
        var journal = _store.ReadFrom(session, 1);
        var startedTools = new HashSet<ToolCallId>();
        var activeChildren = new HashSet<ExecutionId>();
        var processStates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var envelope in journal)
        {
            switch (_codecs.Decode(envelope))
            {
                case ToolCallStarted started:
                    startedTools.Add(started.ToolCallId);
                    break;
                case ToolCallSucceeded succeeded:
                    startedTools.Remove(succeeded.ToolCallId);
                    break;
                case ToolCallFailed failed:
                    if (failed.EffectOutcome != EffectOutcome.Unknown)
                        startedTools.Remove(failed.ToolCallId);
                    break;
                case ToolCallReconciled reconciled when reconciled.Outcome is ReconciliationOutcome.Applied
                    or ReconciliationOutcome.NotApplied:
                    startedTools.Remove(reconciled.ToolCallId);
                    break;
                case AgentExecutionStarted childStarted when childStarted.ParentExecutionId is not null:
                    activeChildren.Add(childStarted.ExecutionId);
                    break;
                case AgentExecutionCompleted completed:
                    activeChildren.Remove(completed.ExecutionId);
                    break;
                case AgentExecutionFailed childFailed:
                    activeChildren.Remove(childFailed.ExecutionId);
                    break;
                case ProcessSessionStateRecorded process:
                    processStates[process.HandleId] = process.State;
                    break;
            }
        }
        return startedTools.Count > 0 || activeChildren.Count > 0
            || processStates.Values.Any(state => state is "starting" or "running" or "stopping" or "unknown");
    }

    // A native process control is allowed to act on the one already-owned running root-lane
    // handle it names. Other in-flight effects, child work, and uncertain process states still
    // block it; this does not relax the general Session security revision fence.
    private bool HasSessionSecurityInFlightAuthorityExceptProcesses(SessionId session, RunId run,
        LaneId lane, PersistentProcessSessionManager manager)
    {
        var journal = _store.ReadFrom(session, 1);
        var startedTools = new HashSet<ToolCallId>();
        var activeChildren = new HashSet<ExecutionId>();
        var processStates = new Dictionary<string, ProcessSessionStateRecorded>(StringComparer.Ordinal);
        foreach (var envelope in journal)
        {
            switch (_codecs.Decode(envelope))
            {
                case ToolCallStarted started:
                    startedTools.Add(started.ToolCallId);
                    break;
                case ToolCallSucceeded succeeded:
                    startedTools.Remove(succeeded.ToolCallId);
                    break;
                case ToolCallFailed failed:
                    if (failed.EffectOutcome != EffectOutcome.Unknown)
                        startedTools.Remove(failed.ToolCallId);
                    break;
                case ToolCallReconciled reconciled when reconciled.Outcome is ReconciliationOutcome.Applied
                    or ReconciliationOutcome.NotApplied:
                    startedTools.Remove(reconciled.ToolCallId);
                    break;
                case AgentExecutionStarted childStarted when childStarted.ParentExecutionId is not null:
                    activeChildren.Add(childStarted.ExecutionId);
                    break;
                case AgentExecutionCompleted completed:
                    activeChildren.Remove(completed.ExecutionId);
                    break;
                case AgentExecutionFailed childFailed:
                    activeChildren.Remove(childFailed.ExecutionId);
                    break;
                case ProcessSessionStateRecorded process:
                    processStates[process.HandleId] = process;
                    break;
            }
        }

        if (startedTools.Count > 0 || activeChildren.Count > 0) return true;
        return processStates.Values.Any(process =>
            process.State is "starting" or "running" or "stopping" or "unknown"
            && !(process.State == "running" && process.RunId == run.ToString()
                && process.LaneId == lane.ToString()
                && manager.CanControlOwnedProcess(process.HandleId, run.ToString(), lane.ToString(), "process.read")));
    }

    internal string CurrentSessionSecurityDefaultJson()
    {
        var snapshot = new SessionSecurityDefaultConfiguration(_platformPathsFactory()).Read();
        static string Q(string value) => "\"" + JsonObj.Escape(value) + "\"";
        return "{\"revision\":" + Q(snapshot.Revision) + ",\"category\":" + Q(snapshot.Category.ToString())
            + ",\"isConfigured\":" + (snapshot.IsConfigured ? "true" : "false")
            + ",\"diagnostics\":[" + string.Join(",", snapshot.Diagnostics.Select(Q)) + "]}";
    }

    internal CommandAck SetSessionSecurityDefault(WireEnvelope command, IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        if (!fields.TryGetValue("category", out var rawCategory)
            || !Enum.TryParse<ModelPolicyCategory>(rawCategory, false, out var category)
            || !Enum.IsDefined(category) || category == ModelPolicyCategory.Custom
            || !fields.TryGetValue("expectedRevision", out var expectedRevision))
            return new CommandAck(command.MessageId, "error", "session_security.invalid_default",
                RuntimeCommandOutcome.Rejected());
        try
        {
            _ = new SessionSecurityDefaultConfiguration(_platformPathsFactory())
                .Set(expectedRevision, category, cancellationToken);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new CommandAck(command.MessageId, "error", "session_security.default_update_failed",
                RuntimeCommandOutcome.Rejected());
        }
    }
}

internal sealed class SessionSecurityRevisionConflictException()
    : InvalidOperationException("session_security.stale_revision") { }
