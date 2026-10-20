using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

namespace OmniCore.Host;

internal sealed record SessionConversationCheckpoint(string Id, long ThroughSequence, string Summary, ArtifactRef Artifact)
{
    internal const string Fingerprint = "manual-session-v1";

    internal static SessionConversationCheckpoint? Read(IReadOnlyList<DomainEvent> journal, IEventCodecRegistry codecs,
        IArtifactStore artifacts, RunId run, LaneId? lane)
    {
        if (!new LaneConversationScope(journal, codecs, run, lane).AllowsSessionHistory) return null;
        foreach (var evt in journal.Reverse())
        {
            if (codecs.Decode(evt) is not ContextCheckpointRecorded checkpoint || checkpoint.MetaModelFingerprint != Fingerprint
                || evt.RunId != checkpoint.RunId) continue;
            var owner = new LaneConversationScope(journal, codecs, checkpoint.RunId, evt.LaneId);
            if (!owner.AllowsSessionHistory || !owner.CanRead(evt)) continue;
            var json = artifacts.GetText(checkpoint.CheckpointArtifact.Hash)
                ?? throw new InvalidDataException("Conversation checkpoint is missing or corrupt.");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.GetProperty("throughEventSequence").GetInt64() != checkpoint.ThroughEventSequence
                || checkpoint.ThroughEventSequence < 1 || checkpoint.ThroughEventSequence >= evt.Sequence
                || document.RootElement.GetProperty("runId").GetString() != checkpoint.RunId.ToString()
                || document.RootElement.GetProperty("laneId").GetString() != owner.TargetLane?.ToString())
                throw new InvalidDataException("Conversation checkpoint boundary is invalid.");
            return new(checkpoint.CheckpointId, checkpoint.ThroughEventSequence,
                document.RootElement.GetProperty("summary").GetString() ?? "", checkpoint.CheckpointArtifact);
        }
        return null;
    }
}

public sealed partial class OmniServer
{
    private CommandAck CompactConversation(WireEnvelope command, CancellationToken token)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run || _artifacts is null)
            return new(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp());
        var journal = _store.ReadFrom(session, 1);
        var scope = new LaneConversationScope(journal, _codecs, run, null);
        var previous = SessionConversationCheckpoint.Read(journal, _codecs, _artifacts, run, scope.TargetLane);
        var messages = journal.Where(scope.CanRead).Where(evt => _codecs.Decode(evt) is UserInputReceived or AssistantMessageRecorded).ToArray();
        // Keep the first user request and the two most recent exchanges verbatim.
        var older = messages.Skip(1).Take(Math.Max(0, messages.Length - 5))
            .Where(evt => evt.Sequence > (previous?.ThroughSequence ?? 0)).ToArray();
        if (older.Length == 0) return new(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp());
        var lines = new List<string>();
        if (previous is not null) lines.Add(previous.Summary);
        else if (journal.Select(_codecs.Decode).OfType<ConversationHistoryImported>().SingleOrDefault() is { } imported)
            foreach (var message in imported.Messages)
                lines.Add(message.Role == "tool" ? "Tool " + message.ToolName + ": " + message.Text
                    : (message.Role == "user" ? "User: " : "Assistant: ") + message.Text);
        var firstSequence = messages[0].Sequence;
        var through = older[^1].Sequence;
        var scoped = journal.Where(scope.CanRead).ToArray();
        var steering = scoped.Select(_codecs.Decode).OfType<TurnSteeringReceived>()
            .ToDictionary(input => input.SteeringId);
        foreach (var evt in scoped.Where(evt => evt.Sequence > Math.Max(firstSequence, previous?.ThroughSequence ?? 0)
            && evt.Sequence <= through))
        {
            token.ThrowIfCancellationRequested();
            var payload = _codecs.Decode(evt);
            var text = payload switch
            {
                UserInputReceived input => InputText(input.InputPartsJson),
                AssistantMessageRecorded { ContentRef: { } content } => _artifacts.GetText(content.Hash)
                    ?? throw new InvalidDataException("Conversation message is missing or corrupt."),
                ToolCallRequested call => "Tool call " + call.ToolName + " " + call.ArgumentsJson,
                ToolCallSucceeded result => "Tool result " + ToolTraceSummary.Extract(result.ResultJson)
                    + (result.ResultArtifact is { } resultArtifact ? " artifact=" + resultArtifact.Hash : ""),
                ToolCallFailed failure => "Tool ERROR " + failure.Cause + "; effect=" + failure.EffectOutcome,
                ToolCallRejected rejection => "Tool rejected " + rejection.Reason,
                TurnSteeringApplied applied when steering.TryGetValue(applied.SteeringId, out var input)
                    => "User correction: " + InputText(input.InputPartsJson),
                _ => "",
            };
            if (string.IsNullOrWhiteSpace(text)) continue;
            lines.Add((payload is UserInputReceived ? "User: " : payload is AssistantMessageRecorded ? "Assistant: " : "") + text);
        }
        var limit = ContextManagementPolicy.Default.MaxCheckpointCharacters;
        var snapshotRef = journal.Where(scope.CanRead).Select(_codecs.Decode).OfType<ModelStepStarted>()
            .LastOrDefault(step => step.ContextSnapshotRef is not null)?.ContextSnapshotRef;
        if (snapshotRef is not null && _artifacts.GetText(snapshotRef.Hash) is { } snapshotJson)
        {
            using var snapshot = JsonDocument.Parse(snapshotJson);
            if (snapshot.RootElement.TryGetProperty("tokenBudget", out var budget) && budget.TryGetInt64(out var tokens) && tokens > 0)
                limit = (int)Math.Clamp(tokens, 64, limit);
        }
        // Short instructions and receipts must not be split just because long
        // assistant prose consumes the same per-message share.
        var shortSize = lines.Where(line => line.Length <= 120).Sum(line => line.Length + 1);
        var preserveShort = shortSize <= limit / 2;
        var longCount = lines.Count(line => line.Length > 120);
        var share = Math.Max(24, preserveShort
            ? (limit - shortSize) / Math.Max(1, longCount) - 2
            : limit / Math.Max(1, lines.Count) - 2);
        var material = string.Join("\n", lines.Select(line => line.Length <= share || preserveShort && line.Length <= 120 ? line
            : line[..(share * 2 / 3 - 2)] + " … " + line[^(share / 3 - 2)..]));
        var summary = material.Length <= limit ? material : material[..(limit / 2 - 32)]
            + "\n[Earlier history retained in the journal]\n" + material[^(limit / 2 - 32)..];
        var id = Guid.NewGuid().ToString("N");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteString("checkpointId", id); writer.WriteString("runId", run.ToString());
            writer.WriteString("laneId", scope.TargetLane?.ToString()); writer.WriteNumber("throughEventSequence", through);
            writer.WriteNumber("compactedThroughItemIndex", -1); writer.WriteString("summary", summary); writer.WriteEndObject();
        }
        using var publication = (_artifacts as IArtifactPublicationLease)?.AcquirePublicationLease(token);
        var artifact = _artifacts.PutText(System.Text.Encoding.UTF8.GetString(output.ToArray()),
            "application/vnd.omnicore.context-checkpoint+json", ArtifactKind.ContextSnapshot, Sensitivity.Sensitive);
        using var execution = ExecutionScope.Begin(new(run, scope.TargetTask, scope.TargetLane));
        var sequenceBefore = _store.CurrentSequence(session);
        new EventStream(_store, _codecs, session).Append(new ContextCheckpointRecorded(id, run, through, artifact,
            SessionConversationCheckpoint.Fingerprint), DurabilityClass.Barrier);
        return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session,
            sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
    }
}
