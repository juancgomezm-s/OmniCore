using System.Text.Json;
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
            var title = first is null ? "" : InputTitle(first.InputPartsJson);
            rows.Add(new(created.SessionId.ToString(), title, history.LastOrDefault()?.Timestamp ?? created.CreatedAt,
                created.SessionId == _lastSessionId));
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
        text = new PiiRedactor().Redact(text);
        return text;
    }

    private CommandAck HandleConversationCommand(WireEnvelope command, Dictionary<string, string> fields,
        string name, bool trustedUserAction, CancellationToken token)
    {
        if (!trustedUserAction) return new(command.MessageId, "error", "conversation.trusted_required", RuntimeCommandOutcome.Rejected());
        lock (_modeAuthorityMutationGate)
        {
            var previous = (_lastSessionId, _lastRunId);
            SessionId? attempted = null;
            long sequenceBefore = 0;
            var acquiredTargetLease = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (_lastSessionId is { } activeSession)
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
                SessionId target;
                RunId? run = null;
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
                // First actual intent resolves the initial model route. A blank conversation
                // does not inherit the previous conversation's route grants.
                if (name == "conversation.new") StartSession(target, deferRouting: true);
                EnsureSessionSecurityPolicy(target);
                _lastSessionId = target; _lastRunId = run; _lastSnapshot = null; _lastWorkingStateText = "";
                _writerOwnershipUnavailable = false;
                _recoveryProblem = null;
                if (run is not null) { RecoverPendingEffects(); RecoverPendingAgentOrchestration(); }
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
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
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
                if (acquiredTargetLease && attempted is { } target && _store is SqliteEventStore owned)
                    owned.ReleaseSessionWriterLease(target);
            }
        }
    }
}
