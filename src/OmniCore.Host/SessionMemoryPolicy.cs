namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;

/// <summary>Session-scoped candidate lifecycle. The journal is the candidate/decision ledger;
/// SessionMemoryStore remains the sole promoted-record store.</summary>
public sealed class SessionMemoryPolicy
{
    private static readonly Regex Email = new(@"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Phone = new(@"(?<!\w)(?:\+?\d[\d ()-]{8,}\d)(?!\w)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SessionId _session;
    private readonly IMemoryStore _store;
    private readonly Func<IReadOnlyList<DomainEventPayload>> _journal;
    private readonly Action<DomainEventPayload> _emit;
    private readonly PiiRedactor _redactor = new();

    public SessionMemoryPolicy(SessionId session, IMemoryStore store,
        Func<IReadOnlyList<DomainEventPayload>> journal, Action<DomainEventPayload> emit)
    {
        _session = session; _store = store; _journal = journal; _emit = emit;
    }

    public IReadOnlyList<MemoryCandidateView> Candidates() => Project(_journal());

    public MemoryCandidateView Propose(string content, MemoryKind kind, MemoryCandidateOrigin origin,
        IReadOnlyList<string>? evidence = null, Action<DomainEventPayload>? emit = null)
    {
        var publish = emit ?? _emit;
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (content.Length > 2048 || !Enum.IsDefined(kind) || !Enum.IsDefined(origin)
            || (evidence?.Count ?? 0) > 8 || (evidence ?? []).Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 256))
            throw new ArgumentException("Memory candidate is outside the supported bounds.");
        var redacted = _redactor.Redact(content);
        var unsafeContent = !string.Equals(content, redacted, StringComparison.Ordinal)
            || Email.IsMatch(content) || Phone.IsMatch(content);
        var safeContent = unsafeContent ? "[sensitive candidate withheld]" : content.Trim();
        var id = CandidateId(kind, safeContent);
        var journal = _journal();
        var prior = journal.OfType<MemoryCandidateProposed>().LastOrDefault(item => item.CandidateId == id);
        var current = ProjectCandidate(journal, id);
        var occurrences = Math.Min(100, (prior?.Occurrences ?? 0) + 1);
        // Repetition of model-authored text is not independent evidence. Heuristic candidates
        // are admitted only by ObserveSuccessfulDotnetValidation below.
        var effectiveOrigin = origin == MemoryCandidateOrigin.Heuristic
            ? MemoryCandidateOrigin.Heuristic : prior?.Origin ?? origin;
        var scope = MemoryScope.ForSession(_session);
        if (current is { Decision: "rejected" }) return current;
        if (current is { Decision: "promoted" or "ask" } existing)
        {
            if (existing.Decision == "ask" || effectiveOrigin == MemoryCandidateOrigin.Heuristic)
            {
                var replayedProposal = new MemoryCandidateProposed(id, scope, kind, safeContent,
                    (evidence ?? []).Distinct(StringComparer.Ordinal).ToArray(), occurrences, effectiveOrigin,
                    existing.Decision, existing.ConflictMemoryId, DateTimeOffset.UtcNow);
                publish(replayedProposal);
                if (existing.Decision == "ask") EnsurePromotionInteraction(replayedProposal, publish);
            }
            return existing with { Origin = effectiveOrigin.ToString(), Occurrences = occurrences };
        }
        if (unsafeContent)
        {
            publish(new MemoryCandidateProposed(id, scope, kind, safeContent, [], occurrences, effectiveOrigin,
                "reject", null, DateTimeOffset.UtcNow));
            publish(new MemoryRejected(id, "privacy.sensitive", DateTimeOffset.UtcNow));
            return new MemoryCandidateView(id, kind, safeContent, effectiveOrigin.ToString(), "rejected",
                "privacy.sensitive", null, occurrences, DateTimeOffset.UtcNow);
        }

        var conflict = FindConflict(safeContent, kind);
        var decision = conflict is null ? "promote" : "ask";
        var proposed = new MemoryCandidateProposed(id, scope, kind, safeContent,
            (evidence ?? []).Distinct(StringComparer.Ordinal).ToArray(), occurrences, effectiveOrigin,
            decision, conflict?.Id, DateTimeOffset.UtcNow);
        publish(proposed);
        if (decision == "ask") EnsurePromotionInteraction(proposed, publish);
        if (decision == "promote")
        {
            // Leave an unterminalized proposal on transient storage/journal failure. Retrying the
            // same deterministic candidate reconciles a store write whose receipt did not land.
            PromoteRecord(proposed, null, publish);
        }
        return ProjectCandidate(_journal(), id)
            ?? new MemoryCandidateView(id, kind, safeContent, effectiveOrigin.ToString(), decision, null,
                conflict?.Id, occurrences, proposed.ProposedAt);
    }

    public MemoryRecord Promote(string candidateId, string? supersedes = null)
    {
        var events = _journal();
        var candidate = events.OfType<MemoryCandidateProposed>().LastOrDefault(item => item.CandidateId == candidateId)
            ?? throw new InvalidOperationException("sessionPreferences.error.memory_not_found");
        if (events.OfType<MemoryPromoted>().Any(item => item.CandidateId == candidateId))
        {
            var existing = events.OfType<MemoryPromoted>().Last(item => item.CandidateId == candidateId);
            return _store.Read(candidate.ProposedScope, CancellationToken.None).First(record => record.Id == existing.MemoryId);
        }
        if (events.OfType<MemoryRejected>().Any(item => item.CandidateId == candidateId))
            throw new InvalidOperationException("sessionPreferences.error.memory_rejected");
        if (candidate.Decision == "reject") throw new InvalidOperationException("sessionPreferences.error.memory_rejected");
        if (candidate.ConflictMemoryId is { } conflict && !string.Equals(supersedes, conflict, StringComparison.Ordinal))
            throw new InvalidOperationException("sessionPreferences.error.memory_confirm_supersedes");
        if (candidate.ConflictMemoryId is null && supersedes is not null)
            throw new InvalidOperationException("sessionPreferences.error.memory_invalid_supersedes");
        return PromoteRecord(candidate, supersedes, _emit);
    }

    public void Reject(string candidateId)
    {
        var events = _journal();
        if (!events.OfType<MemoryCandidateProposed>().Any(item => item.CandidateId == candidateId))
            throw new InvalidOperationException("sessionPreferences.error.memory_not_found");
        if (events.OfType<MemoryPromoted>().Any(item => item.CandidateId == candidateId))
            throw new InvalidOperationException("sessionPreferences.error.memory_already_promoted");
        if (events.OfType<MemoryRejected>().Any(item => item.CandidateId == candidateId)) return;
        _emit(new MemoryRejected(candidateId, "user.rejected", DateTimeOffset.UtcNow));
    }

    /// <summary>Creates a bounded heuristic only after three distinct successful .NET build/test
    /// calls are present in the canonical Session journal. Command text is classified locally;
    /// it is never copied into candidate content or evidence.</summary>
    public void ObserveSuccessfulDotnetValidation(string currentToolCallId, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(currentToolCallId) || currentToolCallId.Length > 128
            || !IsDotnetBuildOrTest(argumentsJson)) return;
        var events = _journal();
        ToolCallRequested? currentRequest = null;
        var requests = new Dictionary<string, ToolCallRequested>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var succeededIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in events)
        {
            switch (payload)
            {
                case ToolCallRequested request when request.ToolName == "process.exec":
                    requests[request.ToolCallId.ToString()] = request;
                    if (request.ToolCallId.ToString() == currentToolCallId) currentRequest = request;
                    break;
                case ToolCallSucceeded success:
                    completed.Add(success.ToolCallId.ToString());
                    succeededIds.Add(success.ToolCallId.ToString());
                    break;
                case ToolCallFailed failed:
                    completed.Add(failed.ToolCallId.ToString());
                    break;
                case ToolCallRejected rejected:
                    completed.Add(rejected.ToolCallId.ToString());
                    break;
            }
        }
        // Runtime appends the canonical outcome before calling this method. The supplied args
        // cheaply reject unrelated executions; count only the canonical request/outcome pairs.
        if (currentRequest is null || !completed.Contains(currentToolCallId)) return;
        var successfulIds = requests.Where(pair => succeededIds.Contains(pair.Key)
                && completed.Contains(pair.Key) && IsDotnetBuildOrTest(pair.Value.ArgumentsJson))
            .Select(pair => pair.Key).ToArray();
        if (!succeededIds.Contains(currentToolCallId) || successfulIds.Length < 3) return;
        var evidence = successfulIds.TakeLast(3).ToArray();
        Propose("Repeated successful .NET build or test validation observed in this Session.",
            MemoryKind.Convention, MemoryCandidateOrigin.Heuristic, evidence);
    }

    private MemoryRecord PromoteRecord(MemoryCandidateProposed candidate, string? supersedes,
        Action<DomainEventPayload> publish)
    {
        var source = candidate.Origin switch
        {
            MemoryCandidateOrigin.User => MemorySource.UserStated,
            MemoryCandidateOrigin.Heuristic or MemoryCandidateOrigin.Reconciler => MemorySource.Observed,
            _ => MemorySource.Promoted,
        };
        var record = new MemoryRecord("mem-" + candidate.CandidateId,
            candidate.ProposedScope, candidate.Kind, candidate.Content, source == MemorySource.UserStated ? .75 : .45,
            source == MemorySource.UserStated ? 1 : .65, source, candidate.ProposedAt, null,
            MemoryExpirationKind.WithScope, ["session", candidate.Origin.ToString().ToLowerInvariant()],
            candidate.EvidenceRefs, supersedes);
        var existedBefore = _store.Read(candidate.ProposedScope, CancellationToken.None)
            .Any(existing => existing.Id == record.Id);
        var added = _store.Add(record, CancellationToken.None);
        try { publish(new MemoryPromoted(candidate.CandidateId, added.Id, supersedes, DateTimeOffset.UtcNow)); }
        catch
        {
            // Add is intentionally idempotent. Never compensate by deleting a record that
            // predated this attempt; a later retry will reconcile its missing journal receipt.
            if (!existedBefore && added.Id == record.Id)
                _store.Remove(candidate.ProposedScope, record.Id, CancellationToken.None);
            throw;
        }
        return added;
    }

    private MemoryRecord? FindConflict(string content, MemoryKind kind)
    {
        var words = Tokens(content);
        var records = _store.Read(MemoryScope.ForSession(_session), CancellationToken.None);
        var supersededIds = records.Where(record => record.Supersedes is not null)
            .Select(record => record.Supersedes!).ToHashSet(StringComparer.Ordinal);
        return records
            .Where(record => !supersededIds.Contains(record.Id) && !Expired(record))
            .Where(record => record.Kind == kind && record.Source is MemorySource.UserStated or MemorySource.Promoted)
            .Where(record => record.Confidence >= .7)
            .Select(record => (Record: record, Similarity: Similarity(words, Tokens(record.Content))))
            .Where(item => item.Similarity >= .4 && Normalize(item.Record.Content) != Normalize(content))
            .OrderByDescending(item => item.Record.Confidence).Select(item => item.Record).FirstOrDefault();
    }

    private static bool IsDotnetBuildOrTest(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("executable", out var executable)
                || executable.ValueKind != System.Text.Json.JsonValueKind.String
                || !string.Equals(Path.GetFileNameWithoutExtension(executable.GetString()), "dotnet",
                    StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("argv", out var argv)
                || argv.ValueKind != System.Text.Json.JsonValueKind.Array || argv.GetArrayLength() == 0)
                return false;
            var command = argv[0].GetString();
            return string.Equals(command, "build", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "test", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static bool Expired(MemoryRecord record) => record.Expiration switch
    {
        MemoryExpirationKind.At => record.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow,
        MemoryExpirationKind.AfterUnused => DateTimeOffset.UtcNow
            - (record.LastUsedAt ?? record.CreatedAt) > TimeSpan.FromDays(record.ExpireAfterUnusedDays!.Value),
        _ => false,
    };

    public static IReadOnlyList<MemoryCandidateView> ProjectForPresentation(IReadOnlyList<DomainEventPayload> events)
    {
        var proposals = events.OfType<MemoryCandidateProposed>().GroupBy(item => item.CandidateId, StringComparer.Ordinal)
            .Select(group => group.Last()).OrderByDescending(item => item.ProposedAt).Take(50).ToArray();
        var promoted = events.OfType<MemoryPromoted>().Select(item => item.CandidateId).ToHashSet(StringComparer.Ordinal);
        var rejected = events.OfType<MemoryRejected>().GroupBy(item => item.CandidateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().ReasonCode, StringComparer.Ordinal);
        return proposals.Select(item => new MemoryCandidateView(item.CandidateId, item.Kind, item.Content,
            item.Origin.ToString(), promoted.Contains(item.CandidateId) ? "promoted"
                : rejected.ContainsKey(item.CandidateId) ? "rejected" : item.Decision,
            rejected.GetValueOrDefault(item.CandidateId), item.ConflictMemoryId, item.Occurrences, item.ProposedAt)).ToArray();
    }

    private IReadOnlyList<MemoryCandidateView> Project(IReadOnlyList<DomainEventPayload> events) => ProjectForPresentation(events);

    private static MemoryCandidateView? ProjectCandidate(IReadOnlyList<DomainEventPayload> events, string candidateId)
    {
        var proposal = events.OfType<MemoryCandidateProposed>()
            .LastOrDefault(item => item.CandidateId == candidateId);
        if (proposal is null) return null;
        var promoted = events.OfType<MemoryPromoted>().LastOrDefault(item => item.CandidateId == candidateId);
        var rejected = events.OfType<MemoryRejected>().LastOrDefault(item => item.CandidateId == candidateId);
        return new MemoryCandidateView(candidateId, proposal.Kind, proposal.Content, proposal.Origin.ToString(),
            promoted is not null ? "promoted" : rejected is not null ? "rejected" : proposal.Decision,
            rejected?.ReasonCode, proposal.ConflictMemoryId, proposal.Occurrences, proposal.ProposedAt);
    }

    private void EnsurePromotionInteraction(MemoryCandidateProposed candidate, Action<DomainEventPayload> publish)
    {
        var events = _journal();
        var pending = new Dictionary<InteractionId, InteractionRequested>();
        foreach (var payload in events)
        {
            switch (payload)
            {
                case InteractionRequested requested:
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
        if (pending.Values.Any(request => request.Kind == InteractionKind.MemoryPromotion
                && SubjectCandidateId(request.SubjectJson) == candidate.CandidateId)) return;
        var queuePosition = pending.Count;
        var subject = "{" + JsonObj.Field("operation", "memory.promotion") + ","
            + JsonObj.Field("candidateId", candidate.CandidateId) + ","
            + JsonObj.Field("conflictMemoryId", candidate.ConflictMemoryId ?? "") + ","
            + JsonObj.Field("kind", candidate.Kind.ToString()) + ","
            + JsonObj.Field("content", candidate.Content) + "}";
        publish(new InteractionRequested(InteractionId.New(), InteractionKind.MemoryPromotion, subject,
            "[{\"id\":\"promote\",\"intent\":\"allow\"},{\"id\":\"reject\",\"intent\":\"deny\"}]",
            "reject", null, null, null, null, queuePosition, queuePosition + 1));
    }

    private static string? SubjectCandidateId(string subjectJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(subjectJson);
            return document.RootElement.TryGetProperty("candidateId", out var id)
                && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private string CandidateId(MemoryKind kind, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_session + "|" + kind + "|" + Normalize(content)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    private static string Normalize(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static HashSet<string> Tokens(string value) => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length > 2).ToHashSet(StringComparer.Ordinal);
    private static double Similarity(HashSet<string> left, HashSet<string> right) => left.Count == 0 || right.Count == 0
        ? 0 : (double)left.Intersect(right).Count() / left.Union(right).Count();
}
