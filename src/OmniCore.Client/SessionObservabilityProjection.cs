using OmniCore.Protocol;

namespace OmniCore.Client;

/// <summary>Framework-neutral panel state; explicit session ownership prevents cross-chat metrics and late events.</summary>
public sealed class SessionObservabilityProjection
{
    private readonly Dictionary<string, ChatActivityEvent> _activity = new();
    public string? SessionId { get; private set; }
    public SessionObservabilitySnapshot? Snapshot { get; private set; }
    public long ActivitySequence { get; private set; }
    public IReadOnlyList<ChatActivityEvent> Activity => _activity.Values.OrderBy(e => e.Sequence).ToArray();
    public bool WaitingForFirstAnswer => Activity.Any(e => !e.FirstAnswerTextReceived && e.Phase == ChatActivityPhase.WaitingForResponse);
    public bool Reasoning => Activity.Any(e => e.Phase == ChatActivityPhase.Reasoning);
    public bool AnswerText => Activity.Any(e => e.Phase == ChatActivityPhase.AnswerText);
    public bool Tools => Activity.Any(e => e.Phase == ChatActivityPhase.Tools);
    public bool WaitingForApproval => Activity.Any(e => e.Phase == ChatActivityPhase.WaitingForApproval);
    public void Activate(string? sessionId)
    {
        if (sessionId == SessionId) return;
        SessionId = sessionId; Snapshot = null; ActivitySequence = 0; _activity.Clear();
    }
    public bool Apply(SessionObservabilitySnapshot snapshot)
    {
        if (snapshot.SessionId != SessionId || snapshot.BasedOnJournalSequence < (Snapshot?.BasedOnJournalSequence ?? 0)) return false;
        if (snapshot.Consumption.SessionId != SessionId || snapshot.Context is { } owned && owned.SessionId != SessionId
            || snapshot.PendingContext is { } pending && pending.SessionId != SessionId) return false;
        var context = snapshot.Context;
        if (snapshot.Updating && context is null) context = Snapshot?.Context;
        Snapshot = snapshot with { Context = context };
        foreach (var e in snapshot.Activities)
        {
            if (e.SessionId != SessionId || e.Sequence <= ActivitySequence) continue;
            if (!_activity.TryGetValue(e.TurnId, out var previous) || e.Sequence > previous.Sequence) _activity[e.TurnId] = e;
        }
        ActivitySequence = Math.Max(ActivitySequence, snapshot.ActivitySequence);
        return true;
    }
    public bool Poll(IOmniClient client, CancellationToken cancellationToken = default)
    {
        var query = client.Query("sessionObservability:" + ActivitySequence, cancellationToken);
        return query is not null && ObservabilityJson.Decode(query.Json) is { } snapshot && Apply(snapshot);
    }
}
