namespace OmniCore.Host;

using System.Globalization;
using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Memory;
using OmniCore.Protocol;

public sealed partial class OmniServer
{
    private static bool IsScopedMemoryPromotion(InteractionRequested request)
    {
        using var document = System.Text.Json.JsonDocument.Parse(request.SubjectJson);
        return document.RootElement.TryGetProperty("operation", out var operation)
            && operation.GetString() == "memory.scoped.promotion";
    }

    private void ApplyScopedMemoryPromotionDecision(SessionId session, InteractionRequested request, string optionId,
        string commandId, RunControlService control, bool resolveInteraction)
    {
        // The existing native transport advertises Session-only memory consent. A wider
        // native capability needs its own binding; the Core TUI uses trusted in-process actions.
        if (!resolveInteraction) throw new InvalidOperationException("client.memory.trusted_required");
        var identity = ReadMemoryPromotionIdentity(request.SubjectJson);
        using var subject = System.Text.Json.JsonDocument.Parse(request.SubjectJson);
        using var options = System.Text.Json.JsonDocument.Parse(request.OptionsJson);
        var ids = options.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        var candidate = _store.ReadFrom(session, 1).Select(_codecs.Decode).OfType<MemoryCandidateProposed>()
            .LastOrDefault(item => item.CandidateId == identity.CandidateId)
            ?? throw new InvalidDataException("sessionPreferences.error.memory_approval_invalid");
        var root = subject.RootElement;
        if (request.DefaultOptionId != "reject" || !ids.SetEquals(["promote", "reject"])
            || optionId is not ("promote" or "reject") || candidate.Decision != "ask"
            || root.GetProperty("scopeLevel").GetString() != candidate.ProposedScope.Level.ToString()
            || root.GetProperty("scopeId").GetString() != candidate.ProposedScope.Id
            || root.GetProperty("content").GetString() != candidate.Content
            || identity.ConflictMemoryId != (candidate.ConflictMemoryId ?? "")
            || _workspaceRoot is null
            || !ScopedMemoryAccess.Allowed(_platformPathsFactory(), _workspaceRoot).Contains(candidate.ProposedScope))
            throw new InvalidDataException("sessionPreferences.error.memory_approval_invalid");
        // The exact human decision is durable before any prepared mutation can become authority.
        control.Respond(session, request.InteractionId, optionId, commandId);
        var policy = ScopedMemoryPolicyFor(session);
        if (optionId == "promote") policy.Promote(identity.CandidateId, CancellationToken.None);
        else policy.Reject(identity.CandidateId, CancellationToken.None);
    }

    private ScopedMemoryPolicy ScopedMemoryPolicyFor(SessionId session)
    {
        var root = _workspaceRoot ?? throw new InvalidOperationException("sessionPreferences.error.session_unavailable");
        return new ScopedMemoryPolicy(session, new ScopedMemoryStore(_platformPathsFactory().DataDirectory),
            () => _store.ReadFrom(session, 1).Select(_codecs.Decode).ToArray(),
            payload => new EventStream(_store, _codecs, session).Append(payload, DurabilityClass.Barrier),
            scope => ScopedMemoryAccess.Allowed(_platformPathsFactory(), root).Contains(scope),
            sourceSession => _store.ReadFrom(sourceSession, 1).Select(_codecs.Decode).ToArray());
    }

    internal void ReconcileScopedMemory(SessionId session, CancellationToken cancellationToken)
    {
        lock (_modeAuthorityMutationGate)
        {
            if (!EnsureWriterOwnershipForCommand() || _lastSessionId != session)
                throw new InvalidOperationException("sessionPreferences.error.session_unavailable");
            ScopedMemoryPolicyFor(session).Reconcile(cancellationToken);
        }
    }

    private CommandAck ChangeScopedMemory(WireEnvelope command, string commandName,
        IReadOnlyDictionary<string, string> fields, bool trustedUserAction, CancellationToken cancellationToken)
    {
        CommandAck Reject(string error) => new(command.MessageId, "error", error, RuntimeCommandOutcome.Rejected());
        if (!trustedUserAction) return Reject("client.memory.trusted_required");
        if (_workspaceRoot is null || _lastSessionId is not { } session)
            return Reject("sessionPreferences.error.session_unavailable");
        if (fields.GetValueOrDefault("expectedSessionId") != session.ToString())
            return Reject("sessionPreferences.error.session_changed");
        if (!CanMutateSessionPreference(session))
            return new(command.MessageId, "busy", "sessionPreferences.error.execution_busy",
                RuntimeCommandOutcome.Deferred("session_preferences_busy"));
        try
        {
            var paths = _platformPathsFactory();
            var scope = ScopedMemoryAccess.Resolve(paths, _workspaceRoot, fields.GetValueOrDefault("scope") ?? "");
            if (fields.GetValueOrDefault("expectedScopeId") != scope.Id) return Reject("client.memory.scope_changed");
            var policy = ScopedMemoryPolicyFor(session);
            policy.Reconcile(cancellationToken);
            var store = new ScopedMemoryStore(paths.DataDirectory);
            if (commandName is "memory.scoped.propose" or "memory.scoped.forget"
                && fields.GetValueOrDefault("expectedRevision") != store.Revision(scope, cancellationToken))
                return Reject("sessionPreferences.error.memory_changed");
            switch (commandName)
            {
                case "memory.scoped.propose":
                    var content = fields.GetValueOrDefault("content") ?? "";
                    if (string.IsNullOrWhiteSpace(content) || content.Length > 2048)
                        return Reject("sessionPreferences.error.memory_invalid");
                    policy.Propose(scope, content, MemoryKind.Fact, cancellationToken);
                    break;
                case "memory.scoped.forget":
                    if (!policy.Forget(scope, fields.GetValueOrDefault("memoryId") ?? "", cancellationToken))
                        return Reject("sessionPreferences.error.memory_not_found");
                    break;
                case "memory.scoped.promote":
                case "memory.scoped.reject":
                    var candidateId = fields.GetValueOrDefault("candidateId") ?? "";
                    var candidate = _store.ReadFrom(session, 1).Select(_codecs.Decode).OfType<MemoryCandidateProposed>()
                        .LastOrDefault(item => item.CandidateId == candidateId);
                    if (candidate?.ProposedScope != scope) return Reject("client.memory.scope_changed");
                    var pending = FindPendingMemoryPromotion(session, candidateId)
                        ?? throw new InvalidOperationException("sessionPreferences.error.memory_approval_not_pending");
                    ApplyMemoryPromotionDecision(session, pending,
                        commandName == "memory.scoped.promote" ? "promote" : "reject", command.MessageId,
                        new RunControlService(_store, _codecs));
                    break;
                default: return Reject("client.memory.scope_invalid");
            }
            return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or InvalidDataException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return Reject(ScopedMemoryError(exception));
        }
    }

    private SessionQueryResult QueryScopedMemory(string name, CancellationToken cancellationToken)
    {
        var scopeName = name["memory.".Length..];
        try
        {
            if (_workspaceRoot is null || _lastSessionId is not { } session)
                throw new InvalidOperationException("sessionPreferences.error.session_unavailable");
            var paths = _platformPathsFactory();
            var scope = ScopedMemoryAccess.Resolve(paths, _workspaceRoot, scopeName);
            var store = new ScopedMemoryStore(paths.DataDirectory);
            var snapshot = store.Snapshot(scope, cancellationToken);
            var records = snapshot.Records;
            var journal = _store.ReadFrom(session, 1).Select(_codecs.Decode).ToArray();
            var candidates = ScopedMemoryPolicy.ProjectForPresentation(journal, item => item == scope);
            var entries = records.Select(record => "{" + JsonObj.Field("id", record.Id) + ","
                + JsonObj.Field("kind", record.Kind.ToString()) + "," + JsonObj.Field("content", new PiiRedactor().Redact(record.Content))
                + "," + JsonObj.Field("source", record.Source.ToString()) + ",\"tags\":[],\"confidence\":"
                + record.Confidence.ToString("R", CultureInfo.InvariantCulture) + "}");
            var proposals = candidates.Select(candidate => "{" + JsonObj.Field("id", candidate.Id) + ","
                + JsonObj.Field("kind", candidate.Kind.ToString()) + "," + JsonObj.Field("content", new PiiRedactor().Redact(candidate.Content))
                + "," + JsonObj.Field("origin", candidate.Origin) + "," + JsonObj.Field("decision", candidate.Decision) + "}");
            return new(name, "{\"available\":true," + JsonObj.Field("scope", scopeName) + "," + JsonObj.Field("scopeId", scope.Id)
                + "," + JsonObj.Field("session", session.ToString()) + "," + JsonObj.Field("revision", snapshot.Revision)
                + ",\"records\":[" + string.Join(",", entries) + "],\"candidates\":[" + string.Join(",", proposals) + "]}");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or InvalidDataException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return new(name, "{\"available\":false," + JsonObj.Field("scope", scopeName) + ","
                + JsonObj.Field("error", ScopedMemoryError(exception))
                + ",\"session\":null,\"records\":[],\"candidates\":[]}");
        }
    }

    private static string ScopedMemoryError(Exception exception) => exception is ArgumentException
        ? "sessionPreferences.error.memory_invalid"
        : exception.Message.StartsWith("sessionPreferences.error.", StringComparison.Ordinal)
            || exception.Message.StartsWith("client.memory.", StringComparison.Ordinal)
            ? exception.Message : "client.memory.storage_unavailable";
}
