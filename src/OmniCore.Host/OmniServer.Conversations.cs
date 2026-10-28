using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    private ConversationList Conversations()
    {
        var sessions = (_store as IWorkspaceJournalReader)?.ReadEvents(EventType.Of("session.created"))
            ?? (_lastSessionId is { } current ? _store.ReadFrom(current, 1).Where(evt => _codecs.Decode(evt) is SessionCreated).ToArray() : []);
        var rows = new List<ConversationRow>();
        foreach (var evt in sessions)
        {
            if (_codecs.Decode(evt) is not SessionCreated created) continue;
            if (_workspaceRoot is { } root && created.WorkspaceId != WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root)).ToString()) continue;
            var history = _store.ReadFrom(created.SessionId, 1);
            var first = history.Select(_codecs.Decode).OfType<UserInputReceived>().FirstOrDefault();
            var imported = history.Select(_codecs.Decode).OfType<ConversationTranscriptImported>().FirstOrDefault();
            var explicitTitle = history.Select(_codecs.Decode).OfType<SessionTitleChanged>().LastOrDefault();
            var importedUserTitle = imported?.Messages.FirstOrDefault(message => message.Role == "user")?.Text;
            var importedTitle = imported?.Title;
            var fallbackTitle = importedUserTitle is null
                ? first is null ? "" : InputTitle(first.InputPartsJson)
                : new string(importedUserTitle.Where(character => !char.IsControl(character)).Take(100).ToArray());
            var title = explicitTitle?.Title ?? (string.IsNullOrWhiteSpace(importedTitle) ? fallbackTitle : importedTitle);
            rows.Add(new(created.SessionId.ToString(), title, history.LastOrDefault()?.Timestamp ?? created.CreatedAt,
                created.SessionId == _lastSessionId, history.LastOrDefault()?.Sequence ?? 0));
        }
        return new(rows.OrderByDescending(row => row.UpdatedAt).ToArray());
    }

    private static string InputTitle(string json)
        => new string(InputText(json).Where(character => !char.IsControl(character)).Take(100).ToArray());

    private static string InputText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var text = document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? ""
            : string.Join(" ", document.RootElement.EnumerateArray().Select(part => part.ValueKind == JsonValueKind.String
            ? part.GetString() : part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var value) ? value.GetString() : ""));
        return text;
    }

    private CommandAck HandleConversationCommand(WireEnvelope command, Dictionary<string, string> fields,
        string name, bool trustedUserAction, CancellationToken token, bool workspaceReceiptReserved = false)
    {
        if (!trustedUserAction) return new(command.MessageId, "error", "conversation.trusted_required", RuntimeCommandOutcome.Rejected());
        lock (_modeAuthorityMutationGate)
        {
            var previous = (_lastSessionId, _lastRunId);
            SessionId? attempted = null;
            SessionId target;
            RunId? run = null;
            long sequenceBefore = 0;
            var acquiredTargetLease = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if ((name != "conversation.import" || workspaceReceiptReserved) && _lastSessionId is { } activeSession)
                {
                    var events = _store.ReadFrom(activeSession, 1).Select(_codecs.Decode).ToArray();
                    var closed = events.Select(payload => payload switch { TurnCompleted completed => completed.TurnId,
                        TurnAbandoned abandoned => abandoned.TurnId, _ => null }).Where(turn => turn is not null).ToHashSet();
                    if (events.OfType<TurnStarted>().Any(turn => !closed.Contains(turn.TurnId)))
                        return new(command.MessageId, "busy", "shell.conversation.busy", RuntimeCommandOutcome.Deferred("active_turn"));
                }
                if (name == "conversation.compact")
                {
                    attempted = _lastSessionId;
                    sequenceBefore = attempted is null ? 0 : _store.CurrentSequence(attempted);
                    if (attempted is { } active) EnsureSessionSecurityPolicy(active);
                    return CompactConversation(command, token);
                }
                if (name == "conversation.import")
                {
                    if (!trustedUserAction)
                        return new(command.MessageId, "error", "conversation.trusted_required", RuntimeCommandOutcome.Rejected());
                    var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
                    if (_store is not SqliteEventStore importReceiptStore)
                        return new(command.MessageId, "error", "conversation.import.storage_unavailable", RuntimeCommandOutcome.Rejected());
                    if (!workspaceReceiptReserved)
                    {
                        var priorReceipt = importReceiptStore.ReadCommandReceipt(command.MessageId,
                            importReceiptStore.CommandReceiptScope);
                        if (priorReceipt is not null)
                        {
                            if (!string.Equals(priorReceipt.RequestHash, requestHash, StringComparison.Ordinal))
                                return new(command.MessageId, "error", "command_id_conflict", RuntimeCommandOutcome.Rejected());
                            if (priorReceipt.State == CommandReceiptState.Completed)
                                return CommandAckFromReceipt(priorReceipt);
                            return new(command.MessageId, "unknown", "command_result_unknown",
                                RuntimeCommandOutcome.Deferred("command_result_unknown"));
                        }
                    }
                    ConversationTranscriptImport.ValidateCommandPayloadSize(command.PayloadJson);
                    using var payload = JsonDocument.Parse(command.PayloadJson);
                    var names = payload.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
                    if (names.Length != 2 || names.Distinct(StringComparer.Ordinal).Count() != 2
                        || !names.Contains("cmd", StringComparer.Ordinal)
                        || !payload.RootElement.TryGetProperty("cmd", out var cmd) || cmd.GetString() != "conversation.import")
                        return new(command.MessageId, "error", "conversation.import.invalid", RuntimeCommandOutcome.Rejected());
                    var fileSource = payload.RootElement.TryGetProperty("fileRelativePath", out var relativeNode);
                    string? recordedRoot = null, recordedIdentity = null;
                    if (fileSource)
                    {
                        if (_lastSessionId is not { } sourceSession || relativeNode.ValueKind != JsonValueKind.String
                            || !WorkspaceConversationImportSource.IsRelativePath(relativeNode.GetString()))
                            return new(command.MessageId, "error", "conversation.import.invalid", RuntimeCommandOutcome.Rejected());
                        (recordedRoot, recordedIdentity) = RecordedWorkspaceRoot(sourceSession);
                        if (recordedRoot is null || recordedIdentity is null || !WorkspaceRootIdentity.Verify(recordedRoot, recordedIdentity))
                            return new(command.MessageId, "error", "conversation.import.workspace_unavailable", RuntimeCommandOutcome.Rejected());
                    }
                    using var sourceLease = fileSource
                        ? WorkspaceConversationImportSource.Open(recordedRoot!, relativeNode.GetString()!) : null;
                    byte[] sourceBytes;
                    if (sourceLease is not null) sourceBytes = sourceLease.Bytes;
                    else if (payload.RootElement.TryGetProperty("documentBase64", out var base64Node)
                        && base64Node.ValueKind == JsonValueKind.String && base64Node.GetString() is { } base64)
                        sourceBytes = ConversationTranscriptImport.DecodeBase64Document(base64);
                    else return new(command.MessageId, "error", "conversation.import.invalid", RuntimeCommandOutcome.Rejected());
                    var document = ConversationTranscriptImport.Parse(sourceBytes);
                    var commandId = new CommandId(Guid.Parse(command.MessageId)).ToString();
                    if (!workspaceReceiptReserved)
                    {
                        if (_lastSessionId is { } activeImportSession)
                        {
                            var events = _store.ReadFrom(activeImportSession, 1).Select(_codecs.Decode).ToArray();
                            var closed = events.Select(payload => payload switch { TurnCompleted completed => completed.TurnId,
                                TurnAbandoned abandoned => abandoned.TurnId, _ => null }).Where(turn => turn is not null).ToHashSet();
                            if (events.OfType<TurnStarted>().Any(turn => !closed.Contains(turn.TurnId)))
                                return new(command.MessageId, "busy", "shell.conversation.busy",
                                    RuntimeCommandOutcome.Deferred("active_turn"));
                        }
                        var reservation = importReceiptStore.ReserveCommandReceipt(command.MessageId, requestHash,
                            importReceiptStore.CommandReceiptScope, _lastSessionId?.ToString());
                        if (reservation.State == CommandReceiptState.IdConflict)
                            return new(command.MessageId, "error", "command_id_conflict", RuntimeCommandOutcome.Rejected());
                        if (reservation.State == CommandReceiptState.Completed)
                            return CommandAckFromReceipt(reservation.Receipt);
                        if (reservation.State == CommandReceiptState.Unknown)
                            return new(command.MessageId, "unknown", "command_result_unknown",
                                RuntimeCommandOutcome.Deferred("command_result_unknown"));
                        // Reserved means this Host won the workspace-global SQL transaction and
                        // is the sole process allowed to create the Session for this command.
                    }
                    target = SessionId.New();
                    var workspace = sourceLease?.CanonicalRoot ?? _workspaceRoot ?? Directory.GetCurrentDirectory();
                    var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(workspace)).ToString();
                    var creation = new List<DomainEventPayload>
                    {
                        new SessionCreated(target, workspaceId, workspace, ProfileId.New(), DateTimeOffset.UtcNow),
                        SessionSecurityPolicySet.FromPreset(target, 1, ModelPolicyCategory.ObserveOnly,
                            SessionSecurityProvenance.Imported, SessionSecurityPolicySource.ImportReview, DateTimeOffset.UtcNow),
                        new SessionRoutingPolicySet(target, _newSessionRoutingPolicy),
                        new ConversationTranscriptImported(ConversationTranscriptImport.FormatId, 1, document.Sha256,
                            commandId, requestHash, document.Title, document.Messages),
                    };
                    if (_workspaceRoot is { Length: > 0 })
                    {
                        var physicalRoot = sourceLease?.CanonicalRoot ?? ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspace);
                        creation.Add(new WorkspaceRootEstablished(target, physicalRoot, DateTimeOffset.UtcNow,
                            sourceLease is not null ? recordedIdentity! : WorkspaceRootIdentity.Establish(physicalRoot)));
                    }
                    token.ThrowIfCancellationRequested();
                    attempted = target;
                    sequenceBefore = 0;
                    acquiredTargetLease = importReceiptStore.AcquireSessionWriterLease(target);
                    new EventStream(_store, _codecs, target).AppendBatch(creation, DurabilityClass.Barrier);
                    attempted = target;
                    if (!acquiredTargetLease && _store is SqliteEventStore selectedStore)
                        acquiredTargetLease = selectedStore.AcquireSessionWriterLease(target);
                    EnsureSessionSecurityPolicy(target);
                    if (_lastSessionId != target) InvalidateExtensionContextScope();
                    _lastSessionId = target; _lastRunId = run; _lastSnapshot = null; _lastWorkingStateText = "";
                    _writerOwnershipUnavailable = false;
                    _recoveryProblem = null;
                    SaveLastSession();
                    var importAck = CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                        target, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
                    if (!workspaceReceiptReserved)
                        importReceiptStore.CompleteCommandReceipt(command.MessageId, requestHash, importAck.Status,
                            importAck.Outcome?.Kind.ToString(), importAck.Outcome?.Reason, importAck.FirstSeq,
                            importAck.LastSeq, importReceiptStore.CommandReceiptScope, target.ToString());
                    if (_store is SqliteEventStore oldStore && previous._lastSessionId is { } oldSession && oldSession != target)
                        oldStore.ReleaseSessionWriterLease(oldSession);
                    acquiredTargetLease = false;
                    return importAck;
                }
                if (name == "conversation.rename")
                {
                    if (_lastSessionId is not { } active || !fields.TryGetValue("sessionId", out var sessionText)
                        || !Guid.TryParseExact(sessionText, "D", out var parsedSession) || parsedSession != active.Value
                        || !fields.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title)
                        || title.Length > 120 || title.Any(char.IsControl) || title != title.Trim()
                        || !long.TryParse(fields.GetValueOrDefault("expectedSequence"),
                            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                            out var expectedSequence) || expectedSequence < 1)
                        return new(command.MessageId, "error", "conversation.rename.invalid", RuntimeCommandOutcome.Rejected());
                    attempted = active;
                    sequenceBefore = _store.CurrentSequence(active);
                    var titleEvent = new SessionTitleChanged(active, title, expectedSequence);
                    var stream = new EventStream(_store, _codecs, active);
                    stream.AppendAdmitted(titleEvent, DurabilityClass.Barrier, journal =>
                    {
                        var payloads = journal.Select(_codecs.Decode).ToArray();
                        if (journal.Count == 0 || journal[^1].SessionId != active
                            || journal[^1].Sequence != expectedSequence
                            || payloads.OfType<SessionCreated>().LastOrDefault()?.SessionId != active)
                            throw new InvalidOperationException("conversation.rename.stale_revision");
                        var closedTurns = payloads.Select(payload => payload switch
                        {
                            TurnCompleted completed => (TurnId?)completed.TurnId,
                            TurnAbandoned abandoned => abandoned.TurnId,
                            _ => null,
                        }).OfType<TurnId>().ToHashSet();
                        if (payloads.OfType<TurnStarted>().Any(started => !closedTurns.Contains(started.TurnId)))
                            throw new InvalidOperationException("shell.conversation.busy");
                    });
                    return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                        active, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
                }
                if (name == "conversation.new") target = SessionId.New();
                else
                {
                    if (!fields.TryGetValue("sessionId", out var id) || !Guid.TryParse(id, out _)
                        || !Conversations().Conversations.Any(row => row.Id == id))
                        return new(command.MessageId, "error", "conversation.not_found", RuntimeCommandOutcome.Rejected());
                    target = SessionId.Parse(id);
                    run = _store.ReadFrom(target, 1).Select(_codecs.Decode).OfType<RunCreated>().LastOrDefault()?.RunId;
                }
                attempted = target; sequenceBefore = _store.CurrentSequence(target);
                if (_store is SqliteEventStore sqlite)
                    acquiredTargetLease = sqlite.AcquireSessionWriterLease(target);
                // New Sessions persist their configured routing baseline at creation. It is
                // the same user-configuration snapshot as other native Sessions, never grants
                // copied from the previously selected conversation.
                if (name == "conversation.new") StartSession(target);
                EnsureSessionSecurityPolicy(target);
                if (_lastSessionId != target) InvalidateExtensionContextScope();
                _lastSessionId = target; _lastRunId = run; _lastSnapshot = null; _lastWorkingStateText = "";
                _writerOwnershipUnavailable = false;
                _recoveryProblem = null;
                if (run is not null)
                {
                    RecoverPendingEffects();
                    RecoverPendingAgentOrchestration();
                }
                SaveLastSession();
                var ack = CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    target, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
                if (_store is SqliteEventStore owned && previous._lastSessionId is { } old && old != target)
                    owned.ReleaseSessionWriterLease(old);
                acquiredTargetLease = false;
                return ack;
            }
            catch (SessionWriterLeaseUnavailableException)
            { return new(command.MessageId, "busy", "session_writer_owned", RuntimeCommandOutcome.Deferred("session_writer_owned")); }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or System.Text.Json.JsonException)
            {
                (_lastSessionId, _lastRunId) = previous;
                if (attempted is { } failed)
                    return FailedDurableCommandAck(command.MessageId, failed, sequenceBefore, "conversation.failed", restoreRunIdentity: false);
                return new(command.MessageId, "error", "conversation.failed", RuntimeCommandOutcome.Rejected());
            }
            finally
            {
                // A failed selection must not retain a newly acquired destination lease.
                // Pre-existing leases (including the previous Session) remain owned.
                if (acquiredTargetLease && attempted is { } failedTarget && _store is SqliteEventStore owned)
                    owned.ReleaseSessionWriterLease(failedTarget);
            }
        }
    }
}
