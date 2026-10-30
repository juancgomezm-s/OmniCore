namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Memory;
using OmniCore.Protocol;

/// <summary>
/// Human-originated lifecycle for shared memory. Project/Workspace can promote three matching
/// human Session observations; User and conflicts always ask. Prepared intent precedes mutation.
/// </summary>
public sealed class ScopedMemoryPolicy
{
    private static readonly Regex Email = new(@"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Phone = new(@"(?<!\w)(?:\+?\d[\d ()-]{8,}\d)(?!\w)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SessionId _session;
    private readonly ScopedMemoryStore _store;
    private readonly Func<IReadOnlyList<DomainEventPayload>> _journal;
    private readonly Action<DomainEventPayload> _emit;
    private readonly Func<MemoryScope, bool> _scopeAllowed;
    private readonly Func<SessionId, IReadOnlyList<DomainEventPayload>>? _evidenceJournal;
    private readonly PiiRedactor _redactor = new();

    public ScopedMemoryPolicy(SessionId session, ScopedMemoryStore store,
        Func<IReadOnlyList<DomainEventPayload>> journal, Action<DomainEventPayload> emit,
        Func<MemoryScope, bool> scopeAllowed,
        Func<SessionId, IReadOnlyList<DomainEventPayload>>? evidenceJournal = null)
    {
        _session = session;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _scopeAllowed = scopeAllowed ?? throw new ArgumentNullException(nameof(scopeAllowed));
        _evidenceJournal = evidenceJournal;
    }

    /// <summary>Creates or resumes a user-originated proposal. Model tools must not call this API.</summary>
    public MemoryCandidateView Propose(MemoryScope scope, string content, MemoryKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        EnsureSupported(scope);
        EnsureScopeAllowed(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (content.Length > 2048 || !Enum.IsDefined(kind))
            throw new ArgumentException("Scoped memory candidate is outside supported bounds.");
        cancellationToken.ThrowIfCancellationRequested();

        var redacted = _redactor.Redact(content);
        var sensitive = !string.Equals(content, redacted, StringComparison.Ordinal)
            || Email.IsMatch(content) || Phone.IsMatch(content);
        var safeContent = sensitive ? "[sensitive candidate withheld]" : content.Trim();
        var journal = _journal();
        var baseId = CandidateId(scope, kind, safeContent);
        var family = journal.OfType<MemoryCandidateProposed>()
            .Where(item => item.CandidateId == baseId || item.CandidateId.StartsWith(baseId + "-g", StringComparison.Ordinal))
            .ToArray();
        var candidateId = SelectProposalGeneration(baseId, family, journal, cancellationToken);
        var priorProposal = family.LastOrDefault(item => item.CandidateId == candidateId);
        if (priorProposal is not null)
        {
            var priorView = ProjectForPresentation(journal)
                .LastOrDefault(item => item.Id == candidateId);
            if (priorView is not null && priorView.Decision is "promoted" or "forgotten") return priorView;
            if (IsPrivacyRejected(journal, candidateId))
                return priorView ?? ToView(priorProposal, "rejected", "privacy.sensitive");
            if (HasPrepared(journal, candidateId))
                return priorView ?? ToView(priorProposal, "ask", null);
            var ask = FindScopedInteraction(journal, priorProposal);
            if (ask is { Resolution: null, Expired: false })
                return priorView ?? ToView(priorProposal, "ask", null);
            if (ask is null)
            {
                if (priorProposal.Decision == "promote-evidence")
                {
                    // A crash before Prepared never invents fresh automatic authority. Re-review
                    // current conflicts/revision through a normal human Ask.
                    var current = _store.Snapshot(scope, cancellationToken);
                    priorProposal = priorProposal with { Decision = "ask", EvidenceRefs = ["session:" + _session], Occurrences = 1,
                        ConflictMemoryId = FindConflict(current.Records, priorProposal.Content, priorProposal.Kind)?.Id };
                    _emit(priorProposal);
                    EnsurePromotionInteraction(priorProposal, current.Revision, cancellationToken);
                    return ToView(priorProposal, "ask", null);
                }
                EnsurePromotionInteraction(priorProposal,
                    _store.Revision(scope, cancellationToken), cancellationToken);
                return ToView(priorProposal, "ask", null);
            }
            // A closed ask without a Prepared mutation was stale or rejected. The former needs a
            // fresh generation and revision; a durable rejection is terminal for this generation.
        }

        if (sensitive)
        {
            var withheld = new MemoryCandidateProposed(candidateId, scope, kind, safeContent,
                ["session:" + _session], 1, MemoryCandidateOrigin.User, "reject", null, DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            _emit(withheld);
            _emit(new MemoryRejected(candidateId, "privacy.sensitive", DateTimeOffset.UtcNow));
            return ToView(withheld, "rejected", "privacy.sensitive");
        }

        var snapshot = _store.Snapshot(scope, cancellationToken);
        var conflict = FindConflict(snapshot.Records, content.Trim(), kind);
        var expectedRevision = snapshot.Revision;
        var proposal = new MemoryCandidateProposed(candidateId, scope, kind, safeContent,
            ["session:" + _session], 1, MemoryCandidateOrigin.User, "ask", conflict?.Id,
            DateTimeOffset.UtcNow);
        cancellationToken.ThrowIfCancellationRequested();
        _emit(proposal);
        EnsureScopeAllowed(scope);
        var evidence = _store.ObserveUserEvidence(scope, kind, safeContent, _session, cancellationToken);
        string[] references = evidence.Forgotten ? [] : evidence.SessionRefs.Where(reference => HasSourceEvidence(reference, proposal)).Take(3).ToArray();
        if (references.Length == 3 && conflict is null && scope.Level is ScopeLevel.Project or ScopeLevel.Workspace)
        {
            proposal = proposal with { Decision = "promote-evidence", EvidenceRefs = references, Occurrences = references.Length };
            _emit(proposal);
            return PromoteByEvidence(proposal, expectedRevision, cancellationToken);
        }
        EnsurePromotionInteraction(proposal, expectedRevision, cancellationToken);
        return ToView(proposal, "ask", null);
    }

    /// <summary>Promotes only after the exact scoped interaction was durably resolved by a user.</summary>
    public ScopedMemoryMutationResult Promote(string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        var journal = _journal();
        var proposal = FindProposal(journal, candidateId);
        EnsureScopeAllowed(proposal.ProposedScope);
        EnsureAsk(proposal);
        if (journal.OfType<MemoryRejected>().Any(item => item.CandidateId == candidateId))
            throw new InvalidOperationException("sessionPreferences.error.memory_rejected");
        var interaction = FindScopedInteraction(journal, proposal);
        if (interaction?.Resolution is not { OptionId: "promote", Cause: InteractionCause.User })
            throw new InvalidOperationException("sessionPreferences.error.memory_approval_not_pending");
        var operationId = PromotionOperationId(candidateId);
        var rejectedOperations = journal.OfType<ScopedMemoryMutationRejected>()
            .Select(item => item.OperationId).ToHashSet(StringComparer.Ordinal);
        var priorPrepared = journal.OfType<ScopedMemoryMutationPrepared>()
            .LastOrDefault(item => item.OperationId == operationId && item.Operation == "promote"
                && !rejectedOperations.Contains(item.OperationId));
        if (priorPrepared is not null)
        {
            var recovered = ApplyPreparedForAction(priorPrepared, cancellationToken);
            PublishPromotionReceipts(priorPrepared, proposal, recovered);
            return recovered;
        }
        if (journal.OfType<MemoryPromoted>().LastOrDefault(item => item.CandidateId == candidateId) is { } promoted)
        {
            var completion = FindCompletion(journal, PromotionOperationId(candidateId));
            return new ScopedMemoryMutationResult(completion?.MemoryId ?? promoted.MemoryId, false);
        }

        var expectedRevision = interaction.ExpectedRevision;
        var snapshot = _store.Snapshot(proposal.ProposedScope, cancellationToken);
        var currentConflict = FindConflict(snapshot.Records, proposal.Content, proposal.Kind);
        if (!string.Equals(currentConflict?.Id ?? "", proposal.ConflictMemoryId ?? "", StringComparison.Ordinal)
            || !string.Equals(snapshot.Revision, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("sessionPreferences.error.memory_changed");

        var memoryId = "mem-" + candidateId;
        var record = new MemoryRecord(memoryId, proposal.ProposedScope, proposal.Kind, proposal.Content,
            .75, 1, MemorySource.UserStated, proposal.ProposedAt, null, MemoryExpirationKind.WithScope,
            ["scoped", proposal.Origin.ToString().ToLowerInvariant()], proposal.EvidenceRefs,
            proposal.ConflictMemoryId);
        var prepared = new ScopedMemoryMutationPrepared(PromotionOperationId(candidateId),
            proposal.ProposedScope, "promote", candidateId, memoryId, record, expectedRevision,
            DateTimeOffset.UtcNow);
        cancellationToken.ThrowIfCancellationRequested();
        _emit(prepared);
        EnsureScopeAllowed(proposal.ProposedScope);
        var result = ApplyPreparedForAction(prepared, cancellationToken);
        PublishPromotionReceipts(prepared, proposal, result);
        return result;
    }

    /// <summary>Records a human rejection after the exact scoped Ask was resolved with reject.</summary>
    public void Reject(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        cancellationToken.ThrowIfCancellationRequested();
        var journal = _journal();
        var proposal = FindProposal(journal, candidateId);
        EnsureScopeAllowed(proposal.ProposedScope);
        EnsureAsk(proposal);
        if (journal.OfType<MemoryPromoted>().Any(item => item.CandidateId == candidateId))
            throw new InvalidOperationException("sessionPreferences.error.memory_already_promoted");
        if (journal.OfType<MemoryRejected>().Any(item => item.CandidateId == candidateId)) return;
        var interaction = FindScopedInteraction(journal, proposal);
        if (interaction?.Resolution is not { OptionId: "reject", Cause: InteractionCause.User })
            throw new InvalidOperationException("sessionPreferences.error.memory_approval_not_pending");
        _emit(new MemoryRejected(candidateId, "user.rejected", DateTimeOffset.UtcNow));
    }

    /// <summary>Explicitly forgets one record. The Prepared event is the durable user-action
    /// intent; database operation and its idempotency ledger are committed together.</summary>
    public bool Forget(MemoryScope scope, string memoryId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        EnsureSupported(scope);
        EnsureScopeAllowed(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        if (memoryId.Length > 128) throw new ArgumentException("Memory id is outside supported bounds.", nameof(memoryId));
        cancellationToken.ThrowIfCancellationRequested();
        var journal = _journal();
        var sourceCandidateId = journal.OfType<MemoryPromoted>()
            .LastOrDefault(item => item.MemoryId == memoryId)?.CandidateId ?? "";
        var rejectedOperations = journal.OfType<ScopedMemoryMutationRejected>()
            .Select(item => item.OperationId).ToHashSet(StringComparer.Ordinal);
        var existingPrepared = journal.OfType<ScopedMemoryMutationPrepared>()
            .LastOrDefault(item => item.Operation == "forget" && item.Scope == scope
                && item.MemoryId == memoryId
                && !journal.OfType<ScopedMemoryMutationCompleted>().Any(done => done.OperationId == item.OperationId)
                && !rejectedOperations.Contains(item.OperationId));
        if (existingPrepared is not null)
        {
            var recovered = ApplyPreparedForAction(existingPrepared, cancellationToken);
            PublishForgetReceipts(existingPrepared, recovered);
            return recovered.Removed;
        }
        var snapshot = _store.Snapshot(scope, cancellationToken);
        if (!snapshot.Records.Any(record => record.Id == memoryId)) return false;
        var expectedRevision = snapshot.Revision;
        var operationId = ForgetOperationId(scope, memoryId, expectedRevision);
        var prepared = new ScopedMemoryMutationPrepared(operationId, scope, "forget", sourceCandidateId,
            memoryId, null, expectedRevision, DateTimeOffset.UtcNow);
        cancellationToken.ThrowIfCancellationRequested();
        _emit(prepared);
        EnsureScopeAllowed(scope);
        var result = ApplyPreparedForAction(prepared, cancellationToken);
        PublishForgetReceipts(prepared, result);
        return result.Removed;
    }

    /// <summary>Completes only already-prepared, authorized mutations; never creates consent.</summary>
    public IReadOnlyList<ScopedMemoryMutationResult> Reconcile(CancellationToken cancellationToken = default)
    {
        var journal = _journal();
        var completedIds = journal.OfType<ScopedMemoryMutationCompleted>()
            .Select(item => item.OperationId).ToHashSet(StringComparer.Ordinal);
        completedIds.UnionWith(journal.OfType<ScopedMemoryMutationRejected>().Select(item => item.OperationId));
        var prepared = journal.OfType<ScopedMemoryMutationPrepared>()
            .GroupBy(item => item.OperationId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Where(item => !completedIds.Contains(item.OperationId))
            .ToArray();
        var results = new List<ScopedMemoryMutationResult>();
        foreach (var mutation in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSupported(mutation.Scope);
            if (!_scopeAllowed(mutation.Scope)) continue;
            if (mutation.Operation == "promote")
            {
                var proposal = FindProposal(journal, mutation.CandidateId);
                var evidence = proposal.Decision == "promote-evidence";
                if (proposal.ProposedScope != mutation.Scope || proposal.Decision is not ("ask" or "promote-evidence")
                    || mutation.Record is null || mutation.Record.Content != proposal.Content
                    || mutation.Record.Kind != proposal.Kind
                    || mutation.Record.Supersedes != proposal.ConflictMemoryId
                    || (evidence ? !ValidEvidencePromotion(proposal, mutation.Record)
                        : mutation.ExpectedRevision != FindScopedInteraction(journal, proposal)?.ExpectedRevision))
                    throw new InvalidDataException("The prepared scoped promotion does not match its candidate.");
                var interaction = FindScopedInteraction(journal, proposal);
                if (!evidence && interaction?.Resolution is not { OptionId: "promote", Cause: InteractionCause.User })
                    throw new InvalidDataException("The prepared scoped promotion has no matching user approval.");
            }

            ScopedMemoryMutationResult result;
            try { result = ApplyWithCurrentEvidence(mutation, cancellationToken); }
            catch (InvalidOperationException exception) when (IsTerminalPrecondition(exception))
            {
                EmitMutationRejected(mutation, exception.Message);
                journal = _journal();
                continue;
            }
            if (mutation.Operation == "promote")
                PublishPromotionReceipts(mutation, FindProposal(journal, mutation.CandidateId), result);
            else
                PublishForgetReceipts(mutation, result);
            results.Add(result);
            journal = _journal();
        }
        return results;
    }

    private MemoryCandidateView PromoteByEvidence(MemoryCandidateProposed proposal, string expectedRevision,
        CancellationToken token)
    {
        EnsureScopeAllowed(proposal.ProposedScope);
        var record = new MemoryRecord("mem-" + proposal.CandidateId, proposal.ProposedScope, proposal.Kind, proposal.Content,
            .75, 1, MemorySource.UserStated, proposal.ProposedAt, null, MemoryExpirationKind.WithScope,
            ["scoped", "user", "cross-session-evidence"], proposal.EvidenceRefs, null);
        if (!ValidEvidencePromotion(proposal, record))
            throw new InvalidDataException("Invalid cross-session promotion authority.");
        // A conflict or changed revision is rejected under the store transaction; no overwrite/retry.
        var prepared = new ScopedMemoryMutationPrepared(PromotionOperationId(proposal.CandidateId),
            proposal.ProposedScope, "promote", proposal.CandidateId, record.Id, record, expectedRevision, DateTimeOffset.UtcNow);
        token.ThrowIfCancellationRequested(); _emit(prepared);
        var result = ApplyPreparedForAction(prepared, token);
        PublishPromotionReceipts(prepared, proposal, result);
        return ToView(proposal, "promoted", "evidence.cross_session");
    }

    private bool ValidEvidencePromotion(MemoryCandidateProposed proposal, MemoryRecord record) =>
        proposal.Origin == MemoryCandidateOrigin.User && proposal.ProposedScope.Level is ScopeLevel.Project or ScopeLevel.Workspace
        && proposal.ConflictMemoryId is null && proposal.Occurrences >= 3 && proposal.EvidenceRefs.Count == 3
        && proposal.EvidenceRefs.Distinct(StringComparer.Ordinal).Count() == 3
        && proposal.EvidenceRefs.Contains("user-session:" + _session)
        && record.Tags.Contains("cross-session-evidence") && record.Source == MemorySource.UserStated
        && record.EvidenceRefs.SequenceEqual(proposal.EvidenceRefs);

    private bool HasSourceEvidence(string reference, MemoryCandidateProposed candidate)
    {
        const string prefix = "user-session:";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(reference[prefix.Length..], "D", out var id)) return false;
        var session = new SessionId(id);
        // Evidence is scoped to the Host's actual current workspace journal. A model-supplied
        // reference, another clone's inaccessible Session, or an unavailable journal never counts.
        var source = session == _session ? _journal() : _evidenceJournal?.Invoke(session) ?? [];
        return source.OfType<MemoryCandidateProposed>().GroupBy(item => item.CandidateId).Select(group => group.Last())
            .Any(item => item.Origin == MemoryCandidateOrigin.User && item.Decision is "ask" or "promote-evidence"
                && item.ProposedScope == candidate.ProposedScope
                && item.Kind == candidate.Kind && Normalize(item.Content) == Normalize(candidate.Content)
                && !source.OfType<MemoryRejected>().Any(rejected => rejected.CandidateId == item.CandidateId)
                && !source.OfType<MemoryForgotten>().Any(forgotten => forgotten.CandidateId == item.CandidateId)
                && FindScopedInteraction(source, item)?.Resolution is not { OptionId: "reject", Cause: InteractionCause.User });
    }

    private ScopedMemoryMutationResult ApplyWithCurrentEvidence(ScopedMemoryMutationPrepared prepared, CancellationToken token)
    {
        var prior = _store.ReadPreparedResult(prepared, token);
        if (prior is not null) return prior;
        if (prepared.Record?.Tags.Contains("cross-session-evidence") == true)
        {
            var proposal = FindProposal(_journal(), prepared.CandidateId);
            if (!ValidEvidencePromotion(proposal, prepared.Record)
                || !proposal.EvidenceRefs.All(reference => HasSourceEvidence(reference, proposal)))
                throw new InvalidOperationException("sessionPreferences.error.memory_changed");
        }
        return _store.ApplyPrepared(prepared, token);
    }

    public static IReadOnlyList<MemoryCandidateView> ProjectForPresentation(
        IReadOnlyList<DomainEventPayload> events, Func<MemoryScope, bool> scopeAllowed)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(scopeAllowed);
        var proposals = events.OfType<MemoryCandidateProposed>()
            .Where(item => IsSupportedScope(item.ProposedScope) && scopeAllowed(item.ProposedScope))
            .GroupBy(item => item.CandidateId, StringComparer.Ordinal)
            .Select(group => group.Last()).OrderByDescending(item => item.ProposedAt).Take(50).ToArray();
        var ids = proposals.Select(item => item.CandidateId).ToHashSet(StringComparer.Ordinal);
        var promoted = events.OfType<MemoryPromoted>().Where(item => ids.Contains(item.CandidateId))
            .Select(item => item.CandidateId).ToHashSet(StringComparer.Ordinal);
        var rejected = events.OfType<MemoryRejected>().Where(item => ids.Contains(item.CandidateId))
            .GroupBy(item => item.CandidateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().ReasonCode, StringComparer.Ordinal);
        var forgotten = events.OfType<MemoryForgotten>().Where(item => ids.Contains(item.CandidateId))
            .Select(item => item.CandidateId).ToHashSet(StringComparer.Ordinal);
        return proposals.Select(item => new MemoryCandidateView(item.CandidateId, item.Kind, item.Content,
            item.Origin.ToString(), forgotten.Contains(item.CandidateId) ? "forgotten"
                : promoted.Contains(item.CandidateId) ? "promoted"
                : rejected.ContainsKey(item.CandidateId) ? "rejected" : item.Decision,
            forgotten.Contains(item.CandidateId) ? "user.forgotten" : rejected.GetValueOrDefault(item.CandidateId),
            item.ConflictMemoryId, item.Occurrences, item.ProposedAt)).ToArray();
    }

    public static IReadOnlyList<MemoryCandidateView> ProjectForPresentation(
        IReadOnlyList<DomainEventPayload> events) => ProjectForPresentation(events,
            scope => IsSupportedScope(scope));

    private void EnsurePromotionInteraction(MemoryCandidateProposed proposal, string expectedRevision,
        CancellationToken cancellationToken)
    {
        var journal = _journal();
        var existing = FindScopedInteraction(journal, proposal);
        if (existing is { Resolution: null, Expired: false }) return;
        var pending = PendingInteractions(journal);
        var queuePosition = pending.Count;
        var conflict = proposal.ConflictMemoryId ?? "";
        var subject = "{" + JsonObj.Field("operation", "memory.scoped.promotion") + ","
            + JsonObj.Field("candidateId", proposal.CandidateId) + ","
            + JsonObj.Field("conflictMemoryId", conflict) + ","
            + JsonObj.Field("scopeLevel", proposal.ProposedScope.Level.ToString()) + ","
            + JsonObj.Field("scopeId", proposal.ProposedScope.Id) + ","
            + JsonObj.Field("expectedRevision", expectedRevision) + ","
            + JsonObj.Field("content", proposal.Content) + "}";
        cancellationToken.ThrowIfCancellationRequested();
        EnsureScopeAllowed(proposal.ProposedScope);
        _emit(new InteractionRequested(InteractionId.New(), InteractionKind.MemoryPromotion, subject,
            "[{\"id\":\"promote\",\"intent\":\"allow\"},{\"id\":\"reject\",\"intent\":\"deny\"}]",
            "reject", null, null, null, null, queuePosition, queuePosition + 1));
    }

    private void PublishPromotionReceipts(ScopedMemoryMutationPrepared prepared,
        MemoryCandidateProposed proposal, ScopedMemoryMutationResult result)
    {
        var journal = _journal();
        var promoted = journal.OfType<MemoryPromoted>().LastOrDefault(item => item.CandidateId == proposal.CandidateId);
        if (promoted is not null && promoted.MemoryId != result.MemoryId)
            throw new InvalidDataException("The scoped promotion receipt conflicts with its stored result.");
        if (promoted is null)
            _emit(new MemoryPromoted(proposal.CandidateId, result.MemoryId, proposal.ConflictMemoryId,
                DateTimeOffset.UtcNow));
        PublishCompletion(prepared.OperationId, result);
    }

    private void PublishForgetReceipts(ScopedMemoryMutationPrepared prepared,
        ScopedMemoryMutationResult result)
    {
        if (result.Removed && !_journal().OfType<MemoryForgotten>()
                .Any(item => item.MemoryId == result.MemoryId && item.CandidateId == prepared.CandidateId))
            _emit(new MemoryForgotten(prepared.CandidateId, result.MemoryId, DateTimeOffset.UtcNow));
        PublishCompletion(prepared.OperationId, result);
    }

    private void PublishCompletion(string operationId, ScopedMemoryMutationResult result)
    {
        var completed = _journal().OfType<ScopedMemoryMutationCompleted>()
            .LastOrDefault(item => item.OperationId == operationId);
        if (completed is not null)
        {
            if (completed.MemoryId != result.MemoryId || completed.Removed != result.Removed)
                throw new InvalidDataException("The scoped mutation completion conflicts with its stored result.");
            return;
        }
        _emit(new ScopedMemoryMutationCompleted(operationId, result.MemoryId, result.Removed,
            DateTimeOffset.UtcNow));
    }

    private ScopedMemoryMutationResult ApplyPreparedForAction(ScopedMemoryMutationPrepared prepared,
        CancellationToken cancellationToken)
    {
        EnsureScopeAllowed(prepared.Scope);
        try { return ApplyWithCurrentEvidence(prepared, cancellationToken); }
        catch (InvalidOperationException exception) when (IsTerminalPrecondition(exception))
        {
            EmitMutationRejected(prepared, exception.Message);
            throw;
        }
    }

    private void EmitMutationRejected(ScopedMemoryMutationPrepared prepared, string reasonCode)
    {
        if (_journal().OfType<ScopedMemoryMutationRejected>()
            .Any(item => item.OperationId == prepared.OperationId)) return;
        _emit(new ScopedMemoryMutationRejected(prepared.OperationId, reasonCode, DateTimeOffset.UtcNow));
    }

    private static bool IsTerminalPrecondition(InvalidOperationException exception) =>
        exception.Message is "sessionPreferences.error.memory_changed" or "client.memory.capacity_reached";

    private string SelectProposalGeneration(string baseId, IReadOnlyList<MemoryCandidateProposed> family,
        IReadOnlyList<DomainEventPayload> journal, CancellationToken cancellationToken)
    {
        if (family.Count == 0) return baseId;
        var latest = family[^1];
        if (IsPrivacyRejected(journal, latest.CandidateId)) return latest.CandidateId;
        if (journal.OfType<MemoryForgotten>().Any(item => item.CandidateId == latest.CandidateId)
            || journal.OfType<MemoryRejected>().Any(item => item.CandidateId == latest.CandidateId))
            return NextGeneration(baseId, family);
        if (journal.OfType<ScopedMemoryMutationRejected>().Any(item => item.OperationId == PromotionOperationId(latest.CandidateId)))
            return NextGeneration(baseId, family);
        if (journal.OfType<MemoryPromoted>().LastOrDefault(item => item.CandidateId == latest.CandidateId) is { } promoted)
            return _store.Read(latest.ProposedScope, cancellationToken).Any(item => item.Id == promoted.MemoryId)
                ? latest.CandidateId : NextGeneration(baseId, family);
        if (HasPrepared(journal, latest.CandidateId)) return latest.CandidateId;
        if (latest.Decision == "promote-evidence") return latest.CandidateId;
        var interaction = FindScopedInteraction(journal, latest);
        if (interaction is { Resolution: null, Expired: false }) return latest.CandidateId;
        if (interaction is null) return NextGeneration(baseId, family);
        // A completed promote ask without Prepared is stale: bind a fresh confirmation to current data.
        return NextGeneration(baseId, family);
    }

    private static string NextGeneration(string baseId, IReadOnlyList<MemoryCandidateProposed> family)
    {
        var latestGeneration = family.Select(item => item.CandidateId == baseId ? 1
                : int.TryParse(item.CandidateId[(baseId.Length + 2)..], out var value) ? value : 1)
            .DefaultIfEmpty(0).Max();
        return baseId + "-g" + (latestGeneration + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool HasPrepared(IReadOnlyList<DomainEventPayload> journal, string candidateId)
    {
        var rejected = journal.OfType<ScopedMemoryMutationRejected>()
            .Select(item => item.OperationId).ToHashSet(StringComparer.Ordinal);
        return journal.OfType<ScopedMemoryMutationPrepared>().Any(item => item.Operation == "promote"
            && item.CandidateId == candidateId && !rejected.Contains(item.OperationId));
    }

    private static ScopedMemoryMutationCompleted? FindCompletion(IReadOnlyList<DomainEventPayload> journal,
        string operationId) => journal.OfType<ScopedMemoryMutationCompleted>()
        .LastOrDefault(item => item.OperationId == operationId);

    private static bool IsPrivacyRejected(IReadOnlyList<DomainEventPayload> journal, string candidateId) =>
        journal.OfType<MemoryRejected>().Any(item => item.CandidateId == candidateId
            && item.ReasonCode == "privacy.sensitive");

    private static MemoryCandidateProposed FindProposal(IReadOnlyList<DomainEventPayload> journal,
        string candidateId) => journal.OfType<MemoryCandidateProposed>()
        .LastOrDefault(item => item.CandidateId == candidateId)
        ?? throw new InvalidOperationException("sessionPreferences.error.memory_not_found");

    private static void EnsureAsk(MemoryCandidateProposed proposal)
    {
        if (proposal.Decision != "ask" || !Enum.IsDefined(proposal.Origin))
            throw new InvalidOperationException("sessionPreferences.error.memory_approval_invalid");
    }

    private void EnsureScopeAllowed(MemoryScope scope)
    {
        EnsureSupported(scope);
        if (!_scopeAllowed(scope))
            throw new InvalidOperationException("sessionPreferences.error.memory_changed");
    }

    private static void EnsureSupported(MemoryScope scope)
    {
        scope.Validate();
        if (!IsSupportedScope(scope))
            throw new ArgumentException("Scoped memory supports only Project, Workspace, and User scopes.", nameof(scope));
    }

    private static bool IsSupportedScope(MemoryScope scope) => scope.Level is
        ScopeLevel.Project or ScopeLevel.Workspace or ScopeLevel.User;

    private MemoryRecord? FindConflict(IReadOnlyList<MemoryRecord> records, string content, MemoryKind kind)
    {
        var words = Tokens(content);
        var supersededIds = records.Where(record => record.Supersedes is not null)
            .Select(record => record.Supersedes!).ToHashSet(StringComparer.Ordinal);
        return records.Where(record => !supersededIds.Contains(record.Id) && !Expired(record))
            .Where(record => record.Kind == kind && record.Source is MemorySource.UserStated or MemorySource.Promoted)
            .Where(record => record.Confidence >= .7)
            .Select(record => (Record: record, Similarity: Similarity(words, Tokens(record.Content))))
            .Where(item => item.Similarity >= .4 && Normalize(item.Record.Content) != Normalize(content))
            .OrderByDescending(item => item.Record.Confidence)
            .ThenByDescending(item => item.Record.Importance)
            .Select(item => item.Record).FirstOrDefault();
    }

    private static ScopedInteraction? FindScopedInteraction(IReadOnlyList<DomainEventPayload> journal,
        MemoryCandidateProposed proposal)
    {
        InteractionRequested? request = null;
        InteractionResolved? resolution = null;
        var expired = false;
        foreach (var payload in journal)
        {
            if (payload is InteractionRequested requested && requested.Kind == InteractionKind.MemoryPromotion
                && MatchesSubject(requested, proposal))
            {
                request = requested;
                resolution = null;
                expired = false;
            }
            else if (request is not null && payload is InteractionResolved resolved
                && resolved.InteractionId == request.InteractionId)
                resolution = resolved;
            else if (request is not null && payload is InteractionExpired ended
                && ended.InteractionId == request.InteractionId)
                expired = true;
        }
        return request is null ? null : new ScopedInteraction(request, resolution, expired,
            ReadExpectedRevision(request.SubjectJson));
    }

    private static bool MatchesSubject(InteractionRequested request, MemoryCandidateProposed proposal)
    {
        try
        {
            using var document = JsonDocument.Parse(request.SubjectJson);
            var root = document.RootElement;
            return request.DefaultOptionId == "reject"
                && HasExactOptions(request.OptionsJson)
                && StringProperty(root, "operation") == "memory.scoped.promotion"
                && StringProperty(root, "candidateId") == proposal.CandidateId
                && StringProperty(root, "conflictMemoryId") == (proposal.ConflictMemoryId ?? "")
                && StringProperty(root, "scopeLevel") == proposal.ProposedScope.Level.ToString()
                && StringProperty(root, "scopeId") == proposal.ProposedScope.Id
                && StringProperty(root, "content") == proposal.Content
                && IsSha256(StringProperty(root, "expectedRevision"));
        }
        catch (JsonException) { return false; }
    }

    private static bool HasExactOptions(string optionsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 2)
                return false;
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var option in document.RootElement.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object
                    || !option.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !option.TryGetProperty("intent", out var intent) || intent.ValueKind != JsonValueKind.String
                    || !options.TryAdd(id.GetString()!, intent.GetString()!)) return false;
            }
            return options.Count == 2 && options.GetValueOrDefault("promote") == "allow"
                && options.GetValueOrDefault("reject") == "deny";
        }
        catch (JsonException) { return false; }
    }

    private static string ReadExpectedRevision(string subjectJson)
    {
        try
        {
            using var document = JsonDocument.Parse(subjectJson);
            var value = StringProperty(document.RootElement, "expectedRevision");
            return IsSha256(value) ? value! : "";
        }
        catch (JsonException) { return ""; }
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<InteractionRequested> PendingInteractions(IReadOnlyList<DomainEventPayload> journal)
    {
        var pending = new Dictionary<InteractionId, InteractionRequested>();
        foreach (var payload in journal)
        {
            switch (payload)
            {
                case InteractionRequested requested: pending[requested.InteractionId] = requested; break;
                case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
            }
        }
        return pending.Values.ToArray();
    }

    private string CandidateId(MemoryScope scope, MemoryKind kind, string content)
    {
        var identity = _session + "|" + scope.Level + ":" + scope.Id + "|" + kind + "|" + Normalize(content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string PromotionOperationId(string candidateId) => "promote-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidateId)));

    private string ForgetOperationId(MemoryScope scope, string memoryId, string expectedRevision) => "forget-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            _session + ":" + scope.Level + ":" + scope.Id + ":" + memoryId + ":" + expectedRevision)));

    private static MemoryCandidateView ToView(MemoryCandidateProposed proposal, string decision, string? reason) =>
        new(proposal.CandidateId, proposal.Kind, proposal.Content, proposal.Origin.ToString(), decision,
            reason, proposal.ConflictMemoryId, proposal.Occurrences, proposal.ProposedAt);

    private static HashSet<string> Tokens(string value) => Normalize(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length > 2)
        .ToHashSet(StringComparer.Ordinal);

    private static double Similarity(HashSet<string> left, HashSet<string> right) => left.Count == 0 || right.Count == 0
        ? 0 : (double)left.Intersect(right).Count() / left.Union(right).Count();

    private static string Normalize(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static bool Expired(MemoryRecord record) => record.Expiration switch
    {
        MemoryExpirationKind.At => record.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow,
        MemoryExpirationKind.AfterUnused => DateTimeOffset.UtcNow
            - (record.LastUsedAt ?? record.CreatedAt) > TimeSpan.FromDays(record.ExpireAfterUnusedDays!.Value),
        _ => false,
    };

    private sealed record ScopedInteraction(InteractionRequested Request, InteractionResolved? Resolution,
        bool Expired, string ExpectedRevision);
}
