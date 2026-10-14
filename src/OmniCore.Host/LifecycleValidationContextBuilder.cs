namespace OmniCore.Host;

using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;

/// <summary>Builds bounded, redacted journal evidence for declared lifecycle validators.</summary>
internal static class LifecycleValidationContextBuilder
{
    private const int MaximumUtf8Bytes = 8_192;
    private const int MaximumEvidenceEntries = 32;
    private const int MaximumSummaryCharacters = 512;

    public static string Build(SessionId sessionId, RunId runId, TurnId turnId, string objective,
        IEnumerable<DomainEvent> runEvents, IEventCodecRegistry codecs, RedactionPolicy redaction)
    {
        var events = runEvents.TakeLast(256).ToArray();
        var toolNames = new Dictionary<ToolCallId, string>();
        var effects = new Dictionary<ToolCallId, string>();
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case ToolCallRequested requested:
                    toolNames[requested.ToolCallId] = Safe(requested.ToolName, 128, redaction);
                    break;
                case ToolCallStarted started:
                    effects[started.ToolCallId] = started.EffectClass.ToString();
                    break;
            }
        }

        var evidence = new List<LifecycleEvidenceJson>();
        foreach (var evt in events.TakeLast(MaximumEvidenceEntries))
        {
            var payload = codecs.Decode(evt);
            string? tool = null;
            string? outcome = null;
            string? effect = null;
            string? summary = null;
            string? provenance = null;
            switch (payload)
            {
                case ToolCallSucceeded succeeded:
                    toolNames.TryGetValue(succeeded.ToolCallId, out tool);
                    effects.TryGetValue(succeeded.ToolCallId, out effect);
                    outcome = "succeeded";
                    summary = Safe(succeeded.ResultJson, MaximumSummaryCharacters, redaction);
                    provenance = "journal.tool-result";
                    break;
                case ToolCallFailed failed:
                    toolNames.TryGetValue(failed.ToolCallId, out tool);
                    effects.TryGetValue(failed.ToolCallId, out effect);
                    outcome = "failed:" + failed.EffectOutcome;
                    summary = Safe(failed.Cause, MaximumSummaryCharacters, redaction);
                    provenance = "journal.tool-result";
                    break;
                case ToolCallEffectUnknown unknown:
                    toolNames.TryGetValue(unknown.ToolCallId, out tool);
                    effect = unknown.EffectClass.ToString();
                    outcome = "effect-unknown";
                    provenance = "journal.tool-result";
                    break;
            }
            evidence.Add(new LifecycleEvidenceJson(evt.Sequence, payload.GetType().Name,
                evt.TaskId?.ToString(), evt.LaneId?.ToString(), evt.TurnId?.ToString(),
                evt.ArtifactRefs.Take(2).Select(reference => reference.Hash.ToString()).ToArray(),
                provenance, tool, outcome, effect, summary));
        }

        var truncated = objective.Length > 1_024 || events.Length > MaximumEvidenceEntries;
        var safeObjective = Safe(objective, 1_024, redaction);
        var context = new LifecycleValidationContextJson(sessionId.ToString(), runId.ToString(),
            turnId.ToString(), safeObjective, evidence, truncated);
        var json = Serialize(context);
        while (Encoding.UTF8.GetByteCount(json) > MaximumUtf8Bytes && evidence.Count > 0)
        {
            evidence.RemoveAt(0);
            context = context with { Evidence = evidence, Truncated = true };
            json = Serialize(context);
        }
        while (Encoding.UTF8.GetByteCount(json) > MaximumUtf8Bytes && safeObjective.Length > 0)
        {
            safeObjective = safeObjective[..(safeObjective.Length / 2)];
            context = context with { Objective = safeObjective, Truncated = true };
            json = Serialize(context);
        }
        if (Encoding.UTF8.GetByteCount(json) > MaximumUtf8Bytes)
            throw new InvalidDataException("Lifecycle validation context exceeds its byte limit.");
        return json;
    }

    private static string Serialize(LifecycleValidationContextJson context) => JsonSerializer.Serialize(context,
        LifecycleJsonContext.Default.LifecycleValidationContextJson);

    private static string Safe(string value, int maximumCharacters, RedactionPolicy redaction)
    {
        var redacted = redaction.Redact(value);
        if (redacted.Length <= maximumCharacters) return redacted;
        return redacted[..maximumCharacters] + " [truncated]";
    }
}
