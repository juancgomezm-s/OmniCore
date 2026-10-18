namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;

public sealed partial class OmniServer
{
    internal SessionModelCatalogEntry? ResolveSessionModelSelection(SessionId session)
    {
        var history = _store.ReadFrom(session, 1);
        var selection = SessionModelSelectionProjection.Read(history, _codecs, session);
        if (selection is null) return null;
        var catalog = SessionModelCatalog.Read(_platformPathsFactory(), CancellationToken.None);
        var entry = SessionModelCatalog.Resolve(catalog, selection.ModelId);
        if (entry is null || !entry.Route.Id.Equals(selection.RouteId)
            || !StringComparer.Ordinal.Equals(entry.Provider.Id, selection.ProviderId)
            || entry.BillingMode != selection.BillingMode
            || !FixedHashEquals(selection.RouteIdentityHash,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Route.CanonicalJson())))))
            throw new InvalidDataException("The Session's selected model route changed; explicit reselection is required.");
        return entry;
    }

    internal bool HasOpenSessionTurn(SessionId session, RunId? run) =>
        IsSessionTurnInFlight(_store.ReadFrom(session, 1), run);

    private string EncodeSessionModelCatalog(CancellationToken cancellationToken)
    {
        var snapshot = SessionModelCatalog.Read(_platformPathsFactory(), cancellationToken);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("catalogRevision", snapshot.Revision);
            writer.WritePropertyName("models");
            writer.WriteStartArray();
            foreach (var entry in snapshot.Models)
            {
                writer.WriteStartObject();
                writer.WriteString("modelId", entry.Model.Id);
                writer.WriteString("providerId", entry.Provider.Id);
                writer.WriteString("routeId", entry.Route.Id.Value);
                writer.WriteString("billingMode", entry.BillingMode.ToString());
                writer.WriteNumber("contextWindow", entry.Model.ContextWindow);
                writer.WriteNumber("recommendedUsableContext", entry.Model.RecommendedUsableContext);
                writer.WriteNumber("maxOutputTokens", entry.Model.MaxOutputTokens);
                WriteStringArray(writer, "inputModalities", entry.Model.InputModalities);
                WriteStringArray(writer, "outputModalities", entry.Model.OutputModalities);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal string EncodeCurrentSessionModelSelection()
    {
        var session = _lastSessionId;
        var run = session is { } activeSession ? new RunControlService(_store, _codecs).ActiveRun(activeSession) : null;
        SessionModelSelection? selection = null;
        SessionRoutingPolicy? routing = null;
        SessionSecurityPolicySet? security = null;
        InteractionRequested? pendingConsent = null;
        if (session is { } id)
        {
            var history = _store.ReadFrom(id, 1);
            selection = SessionModelSelectionProjection.Read(history, _codecs, id);
            routing = SessionRoutingAuthorization.Read(history, _codecs, id);
            security = ReadSessionSecurityPolicy(id);
            pendingConsent = PendingModelRouteConsent(history).FirstOrDefault();
        }
        var catalog = SessionModelCatalog.Read(_platformPathsFactory(), CancellationToken.None);
        var pending = pendingConsent is null ? "null" : EncodePendingModelConsent(pendingConsent);
        return "{\"schemaVersion\":1,\"sessionId\":" + Q(session?.ToString())
            + ",\"selection\":" + EncodeSelection(selection)
            + ",\"selectionRevision\":" + (selection?.Revision ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"routingPolicyRevision\":" + (routing is null ? "null" : routing.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + ",\"sessionPolicyRevision\":" + (security is null ? "null" : security.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + ",\"activeRunId\":" + Q(run?.ToString())
            + ",\"catalogRevision\":" + Q(catalog.Revision)
            + ",\"pendingConsent\":" + pending + "}";
    }

    private CommandAck HandleSessionModelCommand(WireEnvelope command,
        IReadOnlyDictionary<string, string> fields, string commandName, CancellationToken cancellationToken)
    {
        var messageId = command.MessageId;
        if (_lastSessionId is not { } session || !Guid.TryParse(messageId, out var parsedCommand))
            return new CommandAck(messageId, "error", "session_model.session_unavailable", RuntimeCommandOutcome.Rejected());
        var commandId = new CommandId(parsedCommand);
        var before = _store.CurrentSequence(session);
        try
        {
            lock (_modeAuthorityMutationGate)
            {
                if (_lastSessionId != session || !EnsureWriterOwnershipForCommand())
                    return WriterOwnershipFailureAck(messageId);
                var history = _store.ReadFrom(session, 1);
                var activeRun = new RunControlService(_store, _codecs).ActiveRun(session);
                var expectedRunRaw = fields.GetValueOrDefault("expectedRunId");
                if (!fields.ContainsKey("expectedRunId"))
                    return ModelAck(messageId, "error", "session_model.stale_run", RuntimeCommandOutcome.Rejected(), session, before, commandId);
                // Closing a native offer has no authority effect and must remain possible
                // after a run, route, or policy revision changed.
                if (commandName == "session.model.route.respond"
                    && fields.GetValueOrDefault("optionId") == "deny")
                    return ResolveNativeRouteDenial(command, fields, commandId, session, before, history);
                if ((expectedRunRaw == "null" ? null : expectedRunRaw) != activeRun?.ToString())
                    return ModelAck(messageId, "error", "session_model.stale_run", RuntimeCommandOutcome.Rejected(), session, before, commandId);

                if (HasSessionSecurityInFlightAuthority(session) || IsSessionTurnInFlight(history, activeRun)
                    || activeRun is { } run && RunProjection.Replay(session, run, _codecs, EventsForRun(history, run)).State == RunState.AwaitingInput)
                    return ModelAck(messageId, "error", "session_model.execution_busy", RuntimeCommandOutcome.Deferred("SessionExecutionMustQuiesce"), session, before, commandId);

                var security = ReadSessionSecurityPolicy(session);
                var routing = SessionRoutingAuthorization.Read(history, _codecs, session);
                var selection = SessionModelSelectionProjection.Read(history, _codecs, session);
                if (security is null || routing is null
                    || !ReadRevision(fields, "expectedSessionPolicyRevision", out var expectedSecurityRevision)
                    || expectedSecurityRevision != security.PolicyRevision
                    || !ReadRevision(fields, "expectedRoutingPolicyRevision", out var expectedRoutingRevision)
                    || expectedRoutingRevision != routing.Revision
                    || !ReadRevision(fields, "expectedSelectionRevision", out var expectedSelectionRevision)
                    || expectedSelectionRevision != (selection?.Revision ?? 0))
                    return ModelAck(messageId, "error", "session_model.stale_revision", RuntimeCommandOutcome.Rejected(), session, before, commandId);

                var catalog = SessionModelCatalog.Read(_platformPathsFactory(), cancellationToken);
                if (!fields.TryGetValue("catalogRevision", out var catalogRevision)
                    || !FixedHashEquals(catalogRevision, catalog.Revision))
                    return ModelAck(messageId, "error", "session_model.catalog_changed", RuntimeCommandOutcome.Rejected(), session, before, commandId);

                if (commandName == "session.model.select")
                {
                    var requested = fields.GetValueOrDefault("modelId");
                    var entry = requested is null ? null : SessionModelCatalog.Resolve(catalog, requested);
                    if (entry is null)
                        return ModelAck(messageId, "error", "session_model.model_unavailable", RuntimeCommandOutcome.Rejected(), session, before, commandId);
                    var pendingOffers = PendingModelRouteConsent(history).ToArray();
                    if (pendingOffers.Length != 0)
                    {
                        var sameOffer = pendingOffers.Any(request =>
                            SessionRoutingAuthorization.Parse(request) == new SessionRoutingAuthorization.Offer(
                                routing.Revision, AuthorizedModelRoute.From(entry.Route, entry.BillingMode))
                            && TryString(request.SubjectJson, "modelId") == entry.Model.Id);
                        return sameOffer
                            ? ModelAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelRouteConsent"), session, before, commandId)
                            : ModelAck(messageId, "error", "session_model.consent_pending", RuntimeCommandOutcome.Rejected(), session, before, commandId);
                    }
                    var binding = CreateSelection(entry, checked((selection?.Revision ?? 0) + 1));
                    if (routing.Allows(entry.Route, entry.BillingMode))
                    {
                        AppendSelection(session, selection, binding, null, history, expectedSecurityRevision,
                            expectedRoutingRevision, catalog.Revision, cancellationToken);
                        return ModelAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before, commandId);
                    }

                    var offer = SessionRoutingAuthorization.Context(new SessionRoutingAuthorization.Offer(
                        routing.Revision, AuthorizedModelRoute.From(entry.Route, entry.BillingMode)));
                    var subject = offer[..^1] + ",\"modelId\":" + Q(entry.Model.Id)
                        + ",\"selectionRevision\":" + expectedSelectionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"securityRevision\":" + expectedSecurityRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"catalogRevision\":" + Q(catalog.Revision) + "}";
                    var interaction = InteractionId.New();
                    var nativeSubject = subject[..^1] + ",\"nativeSessionModel\":1}";
                    var request = new InteractionRequested(interaction, InteractionKind.ModelRouteConsent, nativeSubject,
                        "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_route\",\"intent\":\"allow\"}]",
                        "deny", null, null, null, null, 0, 1);
                    using var cause = CausationScope.Begin(new CommandCausation(commandId));
                    new EventStream(_store, _codecs, session).Append(request, DurabilityClass.Barrier);
                    return ModelAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelRouteConsent"), session, before, commandId);
                }

                return ResolveSessionModelRoute(command, fields, commandId, session, before, history,
                    selection, routing, security, catalog, expectedSelectionRevision, expectedRoutingRevision,
                    expectedSecurityRevision, activeRun, cancellationToken);
            }
        }
        catch (SessionModelRevisionConflictException)
        {
            return ModelAck(messageId, "error", "session_model.stale_revision", RuntimeCommandOutcome.Rejected(), session, before, commandId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or ConfigValidationException)
        {
            return FailedDurableCommandAck(messageId, session, before,
                "session_model.update_failed: " + exception.GetType().Name, restoreRunIdentity: false);
        }
    }

    private CommandAck ResolveSessionModelRoute(WireEnvelope command, IReadOnlyDictionary<string, string> fields,
        CommandId commandId, SessionId session, long before, IReadOnlyList<DomainEvent> history,
        SessionModelSelection? selection, SessionRoutingPolicy routing, SessionSecurityPolicySet security,
        SessionModelCatalog.Snapshot catalog, long expectedSelectionRevision, long expectedRoutingRevision,
        long expectedSecurityRevision, RunId? activeRun, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(fields.GetValueOrDefault("interactionId"), out var parsedInteraction))
            return ModelAck(command.MessageId, "error", "session_model.interaction_invalid", RuntimeCommandOutcome.Rejected(), session, before, commandId);
        var interactionId = new InteractionId(parsedInteraction);
        var matches = history.Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .Where(item => item.Event.SessionId == session && item.Payload is InteractionRequested asked
                && item.Event.RunId is null && asked.InteractionId == interactionId
                && asked.Kind == InteractionKind.ModelRouteConsent
                && IsNativeModelOffer(asked))
            .Select(item => item.Event).ToArray();
        if (matches.Length != 1)
            return ModelAck(command.MessageId, "error", "session_model.consent_stale", RuntimeCommandOutcome.Rejected(), session, before, commandId);
        var request = matches[0];
        if (history.Any(evt => _codecs.Decode(evt) is InteractionResolved resolved
                && resolved.InteractionId == interactionId)
            || history.Any(evt => _codecs.Decode(evt) is InteractionExpired expired && expired.InteractionId == interactionId)
            || _codecs.Decode(request) is not InteractionRequested consent
            || SessionRoutingAuthorization.Parse(consent) is not { } offer
            || TryString(consent.SubjectJson, "modelId") is not { } modelId
            || !StringComparer.Ordinal.Equals(TryString(consent.SubjectJson, "catalogRevision"), catalog.Revision)
            || !ReadLong(consent.SubjectJson, "selectionRevision", out var offeredSelectionRevision)
            || offeredSelectionRevision != expectedSelectionRevision
            || !ReadLong(consent.SubjectJson, "securityRevision", out var offeredSecurityRevision)
            || offeredSecurityRevision != expectedSecurityRevision
            || offer.PolicyRevision != routing.Revision
            || security.PolicyRevision != expectedSecurityRevision)
            return ModelAck(command.MessageId, "error", "session_model.consent_stale", RuntimeCommandOutcome.Rejected(), session, before, commandId);
        var entry = SessionModelCatalog.Resolve(catalog, modelId);
        if (entry is null || AuthorizedModelRoute.From(entry.Route, entry.BillingMode) != offer.Route)
            return ModelAck(command.MessageId, "error", "session_model.route_changed", RuntimeCommandOutcome.Rejected(), session, before, commandId);
        var option = fields.GetValueOrDefault("optionId");
        if (option != "allow_route" || !HasCanonicalOption(consent.OptionsJson, option))
            return ModelAck(command.MessageId, "error", "session_model.option_invalid", RuntimeCommandOutcome.Rejected(), session, before, commandId);
        var payloads = new List<DomainEventPayload>
        {
            new InteractionResolved(interactionId, option, InteractionCause.User),
        };
        if (option == "allow_route")
        {
            var latestCatalog = SessionModelCatalog.Read(_platformPathsFactory(), cancellationToken);
            var latestHistory = _store.ReadFrom(session, 1);
            var latestSelection = SessionModelSelectionProjection.Read(latestHistory, _codecs, session);
            var latestRouting = SessionRoutingAuthorization.Read(latestHistory, _codecs, session);
            var latestSecurity = ReadSessionSecurityPolicy(session);
            if (latestRouting is null)
                return ModelAck(command.MessageId, "error", "session_model.consent_stale", RuntimeCommandOutcome.Rejected(), session, before, commandId);
            if (!StringComparer.Ordinal.Equals(latestCatalog.Revision, catalog.Revision)
                || latestSelection?.Revision != selection?.Revision
                || latestRouting.Revision != expectedRoutingRevision
                || latestSecurity?.PolicyRevision != expectedSecurityRevision
                || latestRouting.Allows(entry.Route, entry.BillingMode))
                return ModelAck(command.MessageId, "error", "session_model.consent_stale", RuntimeCommandOutcome.Rejected(), session, before, commandId);
            var revised = latestRouting.Grant(offer.Route);
            payloads.Add(new SessionRoutingPolicyRevised(session, revised, interactionId));
            var binding = CreateSelection(entry, checked((latestSelection?.Revision ?? 0) + 1));
            payloads.Add(latestSelection is null ? new SessionModelSelectionSet(session, binding)
                : new SessionModelSelectionRevised(session, binding, latestSelection.Revision, interactionId));
        }
        using var cause = CausationScope.Begin(new CommandCausation(commandId));
        new EventStream(_store, _codecs, session).AppendBatch(payloads, DurabilityClass.Barrier);
        return ModelAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before, commandId);
    }

    private void AppendSelection(SessionId session, SessionModelSelection? current,
        SessionModelSelection next, InteractionId? interaction, IReadOnlyList<DomainEvent> history,
        long expectedSecurityRevision, long expectedRoutingRevision, string catalogRevision,
        CancellationToken cancellationToken)
    {
        var payload = current is null ? (DomainEventPayload)new SessionModelSelectionSet(session, next)
            : new SessionModelSelectionRevised(session, next, current.Revision, interaction);
        var paths = _platformPathsFactory();
        var latestCatalog = SessionModelCatalog.Read(paths, cancellationToken);
        if (latestCatalog.Revision != catalogRevision
            || ReadSessionSecurityPolicy(session)?.PolicyRevision != expectedSecurityRevision
            || SessionRoutingAuthorization.Read(_store.ReadFrom(session, 1), _codecs, session)?.Revision != expectedRoutingRevision)
            throw new SessionModelRevisionConflictException();
        new EventStream(_store, _codecs, session).Append(payload, DurabilityClass.Barrier);
    }

    private bool IsSessionTurnInFlight(IReadOnlyList<DomainEvent> history, RunId? activeRun)
    {
        if (activeRun is null) return false;
        var own = history.Where(evt => evt.RunId == activeRun).OrderBy(evt => evt.Sequence).ToArray();
        TurnId? latest = null;
        foreach (var evt in own)
        {
            switch (_codecs.Decode(evt))
            {
                case TurnStarted started:
                    latest = started.TurnId;
                    break;
                case TurnCompleted completed when completed.TurnId == latest:
                case TurnInterrupted interrupted when interrupted.TurnId == latest:
                case TurnAbandoned abandoned when abandoned.TurnId == latest:
                    latest = null;
                    break;
            }
        }
        return latest is not null;
    }

    private IEnumerable<InteractionRequested> PendingModelRouteConsent(IReadOnlyList<DomainEvent> history)
    {
        var pending = new Dictionary<InteractionId, InteractionRequested>();
        foreach (var evt in history.OrderBy(evt => evt.Sequence))
        {
            switch (_codecs.Decode(evt))
            {
                case InteractionRequested requested when evt.RunId is null
                    && requested.Kind == InteractionKind.ModelRouteConsent && IsNativeModelOffer(requested):
                    pending[requested.InteractionId] = requested;
                    break;
                case InteractionResolved resolved:
                    pending.Remove(resolved.InteractionId);
                    break;
                case InteractionExpired expired:
                    pending.Remove(expired.InteractionId);
                    break;
            }
        }
        return pending.Values;
    }

    private CommandAck ResolveNativeRouteDenial(WireEnvelope command, IReadOnlyDictionary<string, string> fields,
        CommandId commandId, SessionId session, long before, IReadOnlyList<DomainEvent> history)
    {
        if (!Guid.TryParse(fields.GetValueOrDefault("interactionId"), out var parsed))
            return ModelAck(command.MessageId, "error", "session_model.interaction_invalid", RuntimeCommandOutcome.Rejected(), session, before, commandId);
        var id = new InteractionId(parsed);
        var candidates = history.Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .Where(item => item.Event.RunId is null && item.Event.SessionId == session
                && item.Payload is InteractionRequested request && request.InteractionId == id
                && request.Kind == InteractionKind.ModelRouteConsent && IsNativeModelOffer(request))
            .ToArray();
        if (candidates.Length != 1 || candidates[0].Payload is not InteractionRequested consent
            || !HasCanonicalOption(consent.OptionsJson, "deny")
            || history.Any(evt => _codecs.Decode(evt) is InteractionResolved resolved && resolved.InteractionId == id)
            || history.Any(evt => _codecs.Decode(evt) is InteractionExpired expired && expired.InteractionId == id))
            return ModelAck(command.MessageId, "error", "session_model.consent_stale", RuntimeCommandOutcome.Rejected(), session, before, commandId);
        using var cause = CausationScope.Begin(new CommandCausation(commandId));
        new EventStream(_store, _codecs, session).Append(
            new InteractionResolved(id, "deny", InteractionCause.User), DurabilityClass.Barrier);
        return ModelAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before, commandId);
    }

    private static bool IsNativeModelOffer(InteractionRequested request) =>
        ReadLong(request.SubjectJson, "nativeSessionModel", out var marker) && marker == 1
        && TryString(request.SubjectJson, "modelId") is { Length: > 0 };

    private static bool HasCanonicalOption(string optionsJson, string optionId)
    {
        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && StringComparer.Ordinal.Equals(id.GetString(), optionId));
        }
        catch (JsonException) { return false; }
    }

    private static SessionModelSelection CreateSelection(SessionModelCatalogEntry entry, int revision) =>
        new SessionModelSelection(revision, entry.Model.Id, entry.Route.Id, entry.Provider.Id,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Route.CanonicalJson()))),
            entry.BillingMode).Freeze();

    private string EncodePendingModelConsent(InteractionRequested request)
    {
        var offer = SessionRoutingAuthorization.Parse(request);
        return "{\"interactionId\":" + Q(request.InteractionId.ToString())
            + ",\"modelId\":" + Q(TryString(request.SubjectJson, "modelId"))
            + ",\"providerId\":" + Q(offer?.Route.ProviderId)
            + ",\"routeId\":" + Q(offer?.Route.RouteId.Value)
            + ",\"billingMode\":" + Q(offer?.Route.BillingMode.ToString())
            + ",\"optionIds\":[\"deny\",\"allow_route\"]}";
    }

    private static string EncodeSelection(SessionModelSelection? selection) => selection is null ? "null"
        : "{\"revision\":" + selection.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"modelId\":" + Q(selection.ModelId) + ",\"providerId\":" + Q(selection.ProviderId)
            + ",\"routeId\":" + Q(selection.RouteId.Value) + ",\"routeIdentityHash\":" + Q(selection.RouteIdentityHash)
            + ",\"billingMode\":" + Q(selection.BillingMode.ToString()) + "}";

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name); writer.WriteStartArray();
        foreach (var value in values.Order(StringComparer.Ordinal)) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string Q(string? value) => value is null ? "null" : "\"" + JsonObj.Escape(value) + "\"";
    private static string? TryString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
    private static bool ReadLong(string json, string property, out long value)
    {
        value = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var item)
                && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out value);
        }
        catch (JsonException) { return false; }
    }
    private static bool ReadRevision(IReadOnlyDictionary<string, string> fields, string property, out long value)
    {
        value = 0;
        return fields.TryGetValue(property, out var raw) && long.TryParse(raw,
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private CommandAck ModelAck(string messageId, string status, string? error, RuntimeCommandOutcome outcome,
        SessionId session, long sequenceBefore, CommandId commandId) =>
        CommandOutcomeAck(messageId, status, error, outcome, session, sequenceBefore, commandId);
}

internal sealed class SessionModelRevisionConflictException() : InvalidOperationException("session_model.stale_revision");
