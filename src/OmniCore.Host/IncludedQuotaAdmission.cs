namespace OmniCore.Host;

using System.Text.Json;
using System.Text.Json.Nodes;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

/// <summary>One invocation's consent to consume a reported low subscription window.
/// Unknown data is not zero, and this consent never increases a monetary cap.</summary>
internal static class IncludedQuotaAdmission
{
    private static bool SameSubject(string left, string right)
    {
        try
        {
            var a = JsonNode.Parse(left);
            var b = JsonNode.Parse(right);
            // A read-only refresh advances its query timestamp, not the consumption
            // being consented to. Keep the timestamp as evidence, but bind permission
            // to account/source/window/reset/remaining and the exact invocation.
            StripMeasurementDates(a); StripMeasurementDates(b);
            return JsonNode.DeepEquals(a, b);
        }
        catch (JsonException) { return false; }
    }
    private static void StripMeasurementDates(JsonNode? node)
    {
        if (node is not JsonObject subject) return;
        subject.Remove("asOf"); subject.Remove("detail");
        if (subject["windows"] is JsonArray windows)
            foreach (var window in windows.OfType<JsonObject>()) window.Remove("asOf");
    }
    internal static bool AllowsMeta(OmniServer server, SessionId session, string provider) =>
        LowWindows(server.Observability.Quota(session, provider)).Length == 0;

    internal static ProviderUsageWindow[] LowWindows(ProviderQuotaSnapshot? quota)
    {
        var now = DateTimeOffset.UtcNow;
        if (quota is null || quota.AsOf > now
            || quota.Availability is not (MetricAvailability.Reported or MetricAvailability.Stale)) return [];
        return quota.Windows.Where(window => window.RemainingPercent.Availability == MetricAvailability.Reported
            && window.RemainingPercent.Value is >= 0 and < 10
            && (window.ResetsAt is null || window.ResetsAt > now)).OrderBy(window => window.Id, StringComparer.Ordinal).ToArray();
    }

    internal static InteractionId? Check(OmniServer server, SessionId session, RunId run, LaneId lane,
        TurnId turn, int step, string provider, string model)
    {
        var quota = server.Observability.Quota(session, provider);
        var low = LowWindows(quota);
        if (low.Length == 0) return null;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("includedQuotaConsent", 1);
            json.WriteString("detail", $"{provider}: {low.Min(window => window.RemainingPercent.Value):0.##}% quota remaining; reported {quota!.AsOf:O}. Consent applies to one invocation only.");
            json.WriteString("providerId", provider); json.WriteString("modelId", model);
            json.WriteString("accountId", quota!.AccountId); json.WriteString("source", quota.Source);
            json.WriteString("asOf", quota.AsOf); json.WriteString("availability", quota.Availability.ToString());
            json.WriteString("turnId", turn.ToString()); json.WriteNumber("stepIndex", step);
            json.WriteStartArray("windows");
            foreach (var window in low)
            {
                json.WriteStartObject(); json.WriteString("id", window.Id); json.WriteString("limitId", window.LimitId);
                json.WriteNumber("remainingPercent", window.RemainingPercent.Value!.Value);
                json.WriteString("source", window.RemainingPercent.Source);
                if (window.RemainingPercent.AsOf is { } asOf) json.WriteString("asOf", asOf);
                if (window.ResetsAt is { } reset) json.WriteString("resetsAt", reset);
                json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        var subject = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        var events = server.AcquireStore().ReadFrom(session, 1);
        var requests = new Dictionary<InteractionId, InteractionRequested>();
        var resolved = new HashSet<InteractionId>();
        foreach (var evt in events)
        {
            switch (server.AcquireCodecs().Decode(evt))
            {
                case InteractionRequested request when evt.RunId == run && evt.LaneId == lane
                    && evt.TurnId == turn && request.Kind == InteractionKind.BudgetExceeded && SameSubject(request.SubjectJson, subject):
                    requests.TryAdd(request.InteractionId, request); break;
                case InteractionResolved response:
                    resolved.Add(response.InteractionId);
                    if (requests.ContainsKey(response.InteractionId) && response.Cause == InteractionCause.User
                        && response.OptionId == "allow_quota") return null;
                    break;
                case InteractionExpired expired: resolved.Add(expired.InteractionId); break;
            }
        }
        var pending = requests.Keys.FirstOrDefault(id => !resolved.Contains(id));
        if (pending is not null) return pending;
        var interaction = InteractionId.New();
        var owner = ExecutionScope.Current;
        using var execution = ExecutionScope.Begin(new ExecutionScopeState(run,
            owner?.RunId == run && owner.LaneId == lane ? owner.TaskId : null, lane, turn,
            ExecutionId: owner?.RunId == run && owner.LaneId == lane ? owner.ExecutionId : null));
        new EventStream(server.AcquireStore(), server.AcquireCodecs(), session).Append(
            new InteractionRequested(interaction, InteractionKind.BudgetExceeded, subject,
                "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_quota\",\"intent\":\"allow\"}]",
                "deny", null, null, null, null, 0, 1), DurabilityClass.Barrier);
        return interaction;
    }
}
