namespace OmniCore.Engine;

using System.Globalization;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>User-only increases, reconstructed from the existing durable interaction pair.</summary>
public sealed record BudgetContinuationOffer(string Scope, decimal BaselineUsd, decimal CurrentUsd,
    string? RunId, string? Day)
{
    public decimal NewLimitUsd => checked(CurrentUsd + 10m);
}

public static class BudgetContinuation
{
    public static string Context(string detail, BudgetContinuationOffer? offer)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("detail", detail);
            if (offer is not null)
            {
                writer.WriteNumber("budgetContinuation", 1);
                writer.WriteString("scope", offer.Scope);
                writer.WriteNumber("baselineUsd", offer.BaselineUsd);
                writer.WriteNumber("currentUsd", offer.CurrentUsd);
                writer.WriteNumber("newLimitUsd", offer.NewLimitUsd);
                writer.WriteString("runId", offer.RunId);
                writer.WriteString("day", offer.Day);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static BudgetContinuationOffer? Offer(InteractionRequested request)
    {
        if (request.Kind != InteractionKind.BudgetExceeded) return null;
        try
        {
            using var document = JsonDocument.Parse(request.SubjectJson);
            var json = document.RootElement;
            if (!json.TryGetProperty("budgetContinuation", out var version) || version.GetInt32() != 1) return null;
            var scope = json.GetProperty("scope").GetString();
            var baseline = json.GetProperty("baselineUsd").GetDecimal();
            var current = json.GetProperty("currentUsd").GetDecimal();
            var limit = json.GetProperty("newLimitUsd").GetDecimal();
            var run = json.GetProperty("runId").GetString();
            var day = json.GetProperty("day").GetString();
            if (baseline < 0 || current < baseline || limit != checked(current + 10m)
                || scope is not ("run" or "session" or "daily")) return null;
            if (scope == "run" && (!Guid.TryParse(run, out _) || day is not null)) return null;
            if (scope == "session" && (run is not null || day is not null)) return null;
            if (scope == "daily" && (run is not null || !DateOnly.TryParseExact(day, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) return null;
            using var options = JsonDocument.Parse(request.OptionsJson);
            var option = options.RootElement.EnumerateArray().SingleOrDefault(item =>
                item.GetProperty("id").GetString() == "allow_plus");
            if (option.ValueKind == JsonValueKind.Undefined || option.GetProperty("value").GetDecimal() != 10m) return null;
            return new(scope!, baseline, current, run, day);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException
            or OverflowException or KeyNotFoundException) { return null; }
    }

    public static decimal Limit(IEnumerable<DomainEvent> events, IEventCodecRegistry codecs, SessionId session,
        RunId run, string day, string scope, decimal baseline)
    {
        var requests = new Dictionary<(SessionId, InteractionId), BudgetContinuationOffer>();
        var responses = new HashSet<(SessionId, InteractionId)>();
        var terminal = new HashSet<(SessionId, InteractionId)>();
        foreach (var evt in events.OrderBy(evt => evt.SessionId.ToString(), StringComparer.Ordinal).ThenBy(evt => evt.Sequence))
        {
            if (scope != "daily" && evt.SessionId != session) continue;
            switch (codecs.Decode(evt))
            {
                case InteractionRequested request when Offer(request) is { } offer:
                    if (!terminal.Contains((evt.SessionId, request.InteractionId)))
                        requests.TryAdd((evt.SessionId, request.InteractionId), offer);
                    break;
                case InteractionResolved response when response.Cause == InteractionCause.User
                    && response.OptionId == "allow_plus":
                    if (terminal.Add((evt.SessionId, response.InteractionId))
                        && requests.ContainsKey((evt.SessionId, response.InteractionId)))
                        responses.Add((evt.SessionId, response.InteractionId));
                    break;
                case InteractionResolved response:
                    if (terminal.Add((evt.SessionId, response.InteractionId)))
                        requests.Remove((evt.SessionId, response.InteractionId));
                    break;
                case InteractionExpired expired:
                    if (terminal.Add((evt.SessionId, expired.InteractionId)))
                        requests.Remove((evt.SessionId, expired.InteractionId));
                    break;
            }
        }
        var result = baseline;
        foreach (var pair in requests)
        {
            var offer = pair.Value;
            if (!responses.Contains(pair.Key) || offer.Scope != scope || offer.BaselineUsd != baseline
                || (scope == "run" && offer.RunId != run.ToString())
                || (scope == "daily" && offer.Day != day)) continue;
            result = Math.Max(result, offer.NewLimitUsd);
        }
        return result;
    }
}
