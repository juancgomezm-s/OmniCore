namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Run-owned research admission and evidence. No provider can dispatch without its own reservation.</summary>
public sealed class WebResearchController : IDisposable
{
    private readonly IWebResearchStore _store;
    private readonly IWebSearchProvider[] _providers;
    private readonly IWebReadTransport _reader;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<string> _allowedOrigins;
    private readonly HashSet<string> _fallbackErrors;
    private readonly int _retries;
    private int _disposed;
    public string RunId { get; }
    internal IWebResearchStore Store => _store;
    public IReadOnlyList<Uri> SearchEndpoints => _providers.Select(provider => provider.Endpoint).ToArray();
    public IReadOnlyList<string> AllowedOrigins => _allowedOrigins;

    public WebResearchController(IWebResearchStore store, string runId,
        IReadOnlyList<WebResearchQuestion> questions, WebResearchLimits limits,
        IWebReadTransport reader, IReadOnlyList<string> allowedOrigins,
        IWebSearchProvider? primary = null, IWebSearchProvider? fallback = null,
        IReadOnlyList<string>? fallbackErrors = null, int retries = 0, TimeProvider? clock = null)
    {
        if (!Guid.TryParse(runId, out _)) throw new ArgumentException("A trusted Run identity is required.", nameof(runId));
        if (retries is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(retries));
        limits.Validate();
        _store = store;
        RunId = runId;
        _reader = reader;
        _clock = clock ?? TimeProvider.System;
        _allowedOrigins = allowedOrigins.Select(ValidateOrigin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _providers = new[] { primary, fallback }.OfType<IWebSearchProvider>().DistinctBy(provider => provider.Id).ToArray();
        _fallbackErrors = new(fallbackErrors ?? ["provider_unavailable", "rate_limited", "credentials_unavailable"], StringComparer.Ordinal);
        _retries = retries;
        var now = _clock.GetUtcNow();
        _store.Initialize(new WebResearchState
        {
            RunId = runId, StartedAt = now, Deadline = now.AddSeconds(limits.DurationSeconds), Limits = limits,
            Questions = questions.Select(question => new WebResearchQuestion(question.Id, Safe(question.Text, 2048))).ToList(),
        });
    }

    public WebResearchState State => _store.Read(RunId) ?? throw new InvalidOperationException("Research state unavailable.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var provider in _providers.OfType<IDisposable>()) provider.Dispose();
        (_reader as IDisposable)?.Dispose();
    }

    public async Task<WebResearchReply> SearchAsync(string toolCallId, WebSearchRequest request,
        int contextTokenCeiling = 0, CancellationToken cancellationToken = default)
    {
        ValidateGapInput(request.TargetedGap);
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 512 || request.MaxResults is < 1 or > 8
            || contextTokenCeiling != 0 && contextTokenCeiling is < 512 or > 32768 || request.RequiredDomains.Count > 8 || request.OptionalFilters.Count > 8)
            throw new ArgumentException("Search request is outside bounded limits.");
        if (new RedactionPolicy().Redact(request.Query) != request.Query)
            throw new ArgumentException("Search query contains protected data.");
        if (request.OptionalFilters.Any(pair => pair.Key is not ("freshness" or "language" or "country")
                || pair.Value.Length > 128 || new RedactionPolicy().Redact(pair.Value) != pair.Value))
            throw new ArgumentException("Search filters contain unsupported or protected data.");
        var domains = request.RequiredDomains.Select(ValidateDomain).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        request = request with { Query = request.Query.Trim(), RequiredDomains = domains };
        var signature = Hash(JsonSerializer.Serialize(request, WebResearchJson.Default.WebSearchRequest) + "|" + contextTokenCeiling);
        var queryKey = Hash(string.Join(' ', request.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant()
            + "|" + string.Join(',', domains) + "|" + request.PreferPrimarySources + "|"
            + string.Join('|', request.OptionalFilters.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)));
        var replay = Replay(toolCallId, signature, request.TargetedGap);
        if (replay is not null) return replay;
        if (contextTokenCeiling == 0) contextTokenCeiling = DefaultContextCeiling(request.TargetedGap, search: true);
        ValidateContextCeiling(State, request.TargetedGap, contextTokenCeiling);

        var cachedState = State;
        if (cachedState.QueryReceipts.TryGetValue(queryKey, out var cachedCall)
            && !cachedCall.StartsWith("pending:", StringComparison.Ordinal)
            && cachedState.ToolReceipts.TryGetValue(cachedCall, out var cachedJson))
        {
            var cached = JsonSerializer.Deserialize(cachedJson, WebResearchJson.Default.WebResearchReply)!;
            return CompleteCached(toolCallId, signature, request.TargetedGap, cached, contextTokenCeiling);
        }
        if (_providers.Length == 0)
        {
            var unavailable = _store.Update(RunId, state =>
            {
                if (state.Status != WebResearchStatus.Active || Expired(state)) return;
                CheckGap(state, request.TargetedGap);
                Terminal(state, WebResearchStatus.SourcesUnavailable, "providers_not_configured");
            });
            return SuppressedReply(unavailable, unavailable.TerminalReason ?? "providers_not_configured");
        }

        WebSearchResponse? response = null;
        foreach (var provider in _providers)
        {
            for (var retry = 0; retry <= _retries; retry++)
            {
                var attemptId = toolCallId + "/query/" + provider.Id + "/" + retry;
                var admission = Reserve(attemptId, toolCallId, signature, request.TargetedGap, queryKey,
                    provider.Id, WebResearchOperation.Query, contextTokenCeiling, newRound: response is null);
                if (admission is not null) return admission;
                var dispatchRefusal = Dispatch(attemptId, request.TargetedGap);
                if (dispatchRefusal is not null) return dispatchRefusal;
                using var timeout = DeadlineToken(cancellationToken);
                try { response = await provider.SearchAsync(request, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { response = new([], [], [], "request_cancelled"); }
                catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
                { response = new([], [], [], "provider_unavailable", Retryable: true); }
                if (response.ErrorCode is null)
                    return CompleteSearch(toolCallId, signature, request, queryKey, attemptId, response, contextTokenCeiling);
                Fail(attemptId, response.ErrorCode);
                if (!response.Retryable) break;
            }
            if (response is null || response.ErrorCode is null || !_fallbackErrors.Contains(response.ErrorCode)) break;
        }
        var stateAfterFailure = _store.Update(RunId, state =>
        {
            state.QueryReceipts.Remove(queryKey);
            if (state.Status == WebResearchStatus.Active && !state.Attempts.Any(attempt =>
                    attempt.ToolCallId != toolCallId && attempt.Status is WebResearchAttemptStatus.Reserved or WebResearchAttemptStatus.Dispatched))
                Terminal(state, WebResearchStatus.SourcesUnavailable, Code(response?.ErrorCode));
            SaveReceipt(state, toolCallId, signature, Reply(state, request.TargetedGap, error: Code(response?.ErrorCode)));
        });
        return ReadReceipt(stateAfterFailure, toolCallId);
    }

    public async Task<WebResearchReply> ReadAsync(string toolCallId, string gapId, string candidateId,
        string url, int offset = 0, int maxCharacters = 1000, int contextTokenCeiling = 0,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        ValidateGapInput(gapId);
        if (offset < 0 || maxCharacters is < 1 or > 8192 || contextTokenCeiling != 0 && contextTokenCeiling is < 512 or > 32768)
            throw new ArgumentException("Read request is outside bounded limits.");
        var signature = Hash(string.Join('|', gapId, candidateId, url, offset, maxCharacters, contextTokenCeiling, refresh));
        var replay = Replay(toolCallId, signature, gapId);
        if (replay is not null) return replay;
        if (contextTokenCeiling == 0) contextTokenCeiling = DefaultContextCeiling(gapId, search: false);
        var snapshot = State;
        ValidateContextCeiling(snapshot, gapId, contextTokenCeiling);
        var candidate = snapshot.Candidates.SingleOrDefault(item => item.Id == candidateId
            && (item.Url == url || item.OriginalUrls.Contains(url, StringComparer.Ordinal)) && item.GapIds.Contains(gapId, StringComparer.Ordinal))
            ?? throw new ArgumentException("Read URL must be a discovered candidate for the current question.");
        var uri = new Uri(url);
        if (!_allowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase))
            return Reply(snapshot, gapId, error: "origin_not_authorized");
        var cachedSource = refresh ? default : snapshot.Documents.SelectMany(document => document.Sources
                .Where(source => source.CandidateId == candidateId && source.OriginalUrl == url)
                .Select(source => (Document: document, Source: source)))
            .OrderBy(pair => pair.Source.ReadAt).LastOrDefault();
        var prior = cachedSource.Document is null ? null : cachedSource.Document with
        {
            OriginalUrl = cachedSource.Source.OriginalUrl, FinalUrl = cachedSource.Source.FinalUrl,
            ReadAt = cachedSource.Source.ReadAt, PublishedAt = cachedSource.Source.PublishedAt,
            UpdatedAt = cachedSource.Source.UpdatedAt, Version = cachedSource.Source.Version,
            RedirectChain = cachedSource.Source.RedirectChain.ToList(),
        };
        var key = "read:" + uri.AbsoluteUri;
        var attemptId = toolCallId + "/read";
        var admission = Reserve(attemptId, toolCallId, signature, gapId, key, "web.read",
            prior is null ? WebResearchOperation.Read : WebResearchOperation.CachedContext, contextTokenCeiling, newRound: false);
        if (admission is not null) return admission;
        WebResearchDocument document;
        if (prior is not null) document = prior;
        else
        {
            var dispatchRefusal = Dispatch(attemptId, gapId);
            if (dispatchRefusal is not null) return dispatchRefusal;
            WebReadResponse response;
            using var timeout = DeadlineToken(cancellationToken);
            try { response = await _reader.ReadAsync(new(uri, 256 * 1024, 0, _allowedOrigins), timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { response = new(url, url, "", "none", _clock.GetUtcNow(), [], false, ErrorCode: "request_cancelled"); }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
            { response = new(url, url, "", "none", _clock.GetUtcNow(), [], false, ErrorCode: "source_unavailable"); }
            if (response.ErrorCode is not null)
            {
                Fail(attemptId, response.ErrorCode);
                return CompleteReadFailure(toolCallId, signature, gapId, Code(response.ErrorCode), response);
            }
            if (!Uri.TryCreate(response.OriginalUrl, UriKind.Absolute, out var originalUri) || originalUri != uri
                || !Uri.TryCreate(response.FinalUrl, UriKind.Absolute, out var finalUri) || finalUri != uri
                || response.RedirectChain.Any(hop => !Uri.TryCreate(hop, UriKind.Absolute, out var hopUri) || hopUri != uri)
                || response.Text.Length > 256 * 1024
                || !_allowedOrigins.Contains(finalUri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase))
            {
                Fail(attemptId, "invalid_read_provenance");
                return CompleteReadFailure(toolCallId, signature, gapId, "invalid_read_provenance", response);
            }
            var text = new RedactionPolicy().Redact(response.Text);
            var contentHash = Hash(text);
            document = new WebResearchDocument(contentHash,
                url, response.FinalUrl, text, contentHash, Safe(response.Method, 64), response.ReadAt,
                Date(response.PublishedAt), Date(response.UpdatedAt), SafeNullable(response.Version, 256), response.Truncated,
                "content:" + contentHash)
            {
                OriginalUrls = [url], RedirectChain = response.RedirectChain.Select(item => Safe(item, 2048)).ToList(),
                Sources = [new(url, response.FinalUrl, response.ReadAt, Date(response.PublishedAt), Date(response.UpdatedAt),
                    SafeNullable(response.Version, 256), response.RedirectChain.ToArray(), candidate.Id)],
            };
        }
        var resultState = _store.Update(RunId, state =>
        {
            var attempt = state.Attempts.Single(item => item.Id == attemptId);
            attempt.Status = WebResearchAttemptStatus.Succeeded;
            Expired(state);
            if (!MayPublishAdmittedResult(state))
            {
                SaveReceipt(state, toolCallId, signature, Reply(state, gapId, error: state.TerminalReason));
                return;
            }
            var storedDocument = state.Documents.SingleOrDefault(item => item.Id == document.Id);
            if (storedDocument is null) state.Documents.Add(document);
            else
            {
                if (prior is null)
                {
                    foreach (var source in document.Sources)
                        if (!storedDocument.Sources.Contains(source)) storedDocument.Sources.Add(source);
                    if (!storedDocument.OriginalUrls.Contains(url, StringComparer.Ordinal)) storedDocument.OriginalUrls.Add(url);
                }
            }
            var length = Math.Min(maxCharacters, Math.Max(0, document.Text.Length - offset));
            WebResearchPassage? passage = null;
            WebResearchReply reply;
            while (true)
            {
                passage = length == 0 ? null : Passage(document, gapId, offset, length);
                reply = Reply(state, gapId, passages: passage is null ? [] : [passage], cached: prior is not null,
                    error: passage is null ? "passage_unavailable" : null);
                if (ContextTokens(reply) <= contextTokenCeiling || length == 0) break;
                length /= 2;
            }
            if (passage is not null && state.Passages.All(item => item.Id != passage.Id))
            {
                var novelRange = !state.Passages.Any(item => item.DocumentId == passage.DocumentId
                    && item.Offset <= passage.Offset && item.Offset + item.Length >= passage.Offset + passage.Length);
                state.Passages.Add(passage);
                Progress(state, novelRange);
            }
            else Progress(state, false);
            attempt.ActualTokens = ContextTokens(reply);
            SaveReceipt(state, toolCallId, signature, reply);
        });
        return resultState.ToolReceipts.ContainsKey(toolCallId) ? ReadReceipt(resultState, toolCallId)
            : Reply(resultState, gapId, error: "deadline_exhausted");
    }

    /// <summary>Assessment is an explicit caller judgment; the quote and receipt must be exact read evidence.</summary>
    public WebResearchReply Assess(string toolCallId, string gapId, string passageId, string exactQuote,
        bool resolvesGap, string? contradictsPassageId = null)
    {
        var signature = Hash(string.Join('|', "assess", gapId, passageId, exactQuote, resolvesGap, contradictsPassageId));
        var replay = Replay(toolCallId, signature, gapId);
        if (replay is not null) return replay;
        var admission = Reserve(toolCallId + "/assess", toolCallId, signature, gapId, "assessment", "controller",
            WebResearchOperation.CachedContext, ContextTokens(Reply(State, gapId)) + 256, newRound: false);
        if (admission is not null) return admission;
        var result = _store.Update(RunId, state =>
        {
            CheckGap(state, gapId);
            if (Expired(state) || state.Status != WebResearchStatus.Active) return;
            var passage = state.Passages.SingleOrDefault(item => item.Id == passageId && item.GapId == gapId);
            if (passage is null || string.IsNullOrWhiteSpace(exactQuote) || exactQuote.Length > 2048
                || !passage.Text.Contains(exactQuote, StringComparison.Ordinal))
                throw new ArgumentException("Assessment requires an exact quote from a passage read for this question.");
            var question = state.Questions.Single(item => item.Id == gapId);
            if (contradictsPassageId is not null)
            {
                var contradicted = state.Passages.SingleOrDefault(item => item.Id == contradictsPassageId && item.GapId == gapId)
                    ?? throw new ArgumentException("Contradiction must name another read passage for the same question.");
                if (contradicted.Id == passage.Id) throw new ArgumentException("A passage cannot contradict itself.");
                var conflictId = "conflict-" + Hash(gapId + "|" + passageId + "|" + contradicted.Id)[..16];
                if (state.Questions.All(item => item.Id != conflictId))
                {
                    if (state.Questions.Count >= 16) { Terminal(state, WebResearchStatus.BudgetExhausted, "question_limit"); return; }
                    state.Questions.Add(new(conflictId, "Resolve conflicting passages for " + question.Text)
                    { ContradictsEvidenceId = contradicted.Id });
                }
                question.Resolved = false;
                question.Answer = null;
            }
            else if (resolvesGap)
            {
                question.Resolved = true;
                question.Answer = exactQuote;
                if (!question.EvidenceIds.Contains(passageId, StringComparer.Ordinal)) question.EvidenceIds.Add(passageId);
                if (state.Questions.All(item => item.Resolved)) Terminal(state, WebResearchStatus.SufficientEvidence, "questions_answered_with_read_passages");
            }
            state.Attempts.Single(attempt => attempt.Id == toolCallId + "/assess").Status = WebResearchAttemptStatus.Succeeded;
            SaveReceipt(state, toolCallId, signature, Reply(state, gapId));
        });
        return result.ToolReceipts.ContainsKey(toolCallId) ? ReadReceipt(result, toolCallId) : Reply(result, gapId);
    }

    public WebResearchReply Status(string toolCallId)
    {
        var state = State;
        var gapId = state.Questions.FirstOrDefault(question => !question.Resolved)?.Id ?? state.Questions[0].Id;
        var signature = Hash("status");
        var replay = Replay(toolCallId, signature, gapId);
        if (replay is not null) return replay;
        var admission = Reserve(toolCallId + "/status", toolCallId, signature, gapId, "status", "controller",
            WebResearchOperation.CachedContext, ContextTokens(Reply(state, gapId)) + 256, newRound: false);
        if (admission is not null) return admission;
        var result = _store.Update(RunId, current =>
        {
            current.Attempts.Single(attempt => attempt.Id == toolCallId + "/status").Status = WebResearchAttemptStatus.Succeeded;
            SaveReceipt(current, toolCallId, signature, Reply(current, gapId, cached: true));
        });
        return ReadReceipt(result, toolCallId);
    }

    private WebResearchReply? Replay(string toolCallId, string signature, string gapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        var state = State;
        if (state.ReceiptSignatures.TryGetValue(toolCallId, out var prior) && prior != signature)
            throw new ArgumentException("Tool call identity reused with different research arguments.");
        if (state.ToolReceipts.ContainsKey(toolCallId))
            return SuppressedReply(state, state.Status == WebResearchStatus.Active ? "replay_suppressed" : state.TerminalReason);
        if (state.Status != WebResearchStatus.Active) return SuppressedReply(state, state.TerminalReason);
        if (state.Attempts.Any(attempt => attempt.ToolCallId == toolCallId))
            return SuppressedReply(state, "attempt_in_progress_or_uncertain");
        return null;
    }

    private WebResearchReply? Reserve(string attemptId, string callId, string signature, string gapId,
        string operationKey, string providerId, WebResearchOperation operation, long tokens, bool newRound)
    {
        string? refusal = null;
        var result = _store.Update(RunId, state =>
        {
            if (state.Status != WebResearchStatus.Active || Expired(state)) { refusal = state.TerminalReason; return; }
            CheckGap(state, gapId);
            if (state.ReceiptSignatures.TryGetValue(callId, out var oldSignature) && oldSignature != signature)
                throw new ArgumentException("Tool call identity reused with different arguments.");
            if (state.ToolReceipts.ContainsKey(callId) || state.Attempts.Any(item => item.Id == attemptId))
            { refusal = "attempt_in_progress_or_completed"; return; }
            if (operation == WebResearchOperation.Query && state.QueryReceipts.TryGetValue(operationKey, out var owner)
                && owner != "pending:" + callId)
            { refusal = "query_already_discovered_or_in_progress"; return; }
            if (operation == WebResearchOperation.Read && state.Attempts.Any(attempt =>
                    attempt.Operation == WebResearchOperation.Read && attempt.OperationKey == operationKey
                    && attempt.Status is WebResearchAttemptStatus.Reserved or WebResearchAttemptStatus.Dispatched))
            { refusal = "read_in_progress_or_uncertain"; return; }
            if (state.AttemptsUsed >= state.Limits.Attempts
                || state.EvidenceTokensUsed + state.CachedContextTokensUsed + tokens > state.Limits.EvidenceTokens
                || operation == WebResearchOperation.Query && state.QueriesUsed >= state.Limits.Queries
                || operation == WebResearchOperation.Read && state.ReadsUsed >= state.Limits.Reads
                || newRound && state.RoundsUsed >= state.Limits.Rounds)
            { Terminal(state, WebResearchStatus.BudgetExhausted, "shared_run_budget_exhausted"); refusal = state.TerminalReason; return; }
            state.ReceiptSignatures[callId] = signature;
            state.AttemptsUsed++;
            if (operation == WebResearchOperation.Query) { state.QueriesUsed++; state.QueryReceipts[operationKey] = "pending:" + callId; }
            if (operation == WebResearchOperation.Read) state.ReadsUsed++;
            if (operation == WebResearchOperation.CachedContext) state.CachedContextTokensUsed += tokens;
            else state.EvidenceTokensUsed += tokens;
            if (newRound) state.RoundsUsed++;
            state.Attempts.Add(new(attemptId, callId, gapId, operationKey, providerId, operation, tokens, _clock.GetUtcNow()));
        });
        return refusal is null ? null : SuppressedReply(result, refusal);
    }

    private WebResearchReply? Dispatch(string attemptId, string? gapId)
    {
        string? refusal = null;
        var state = _store.Update(RunId, current =>
        {
            var attempt = current.Attempts.Single(item => item.Id == attemptId);
            if (current.Status != WebResearchStatus.Active || Expired(current))
            {
                refusal = current.TerminalReason ?? "research_terminal";
                return;
            }
            if (attempt.Status != WebResearchAttemptStatus.Reserved)
            {
                refusal = "attempt_in_progress_or_uncertain";
                return;
            }
            attempt.Status = WebResearchAttemptStatus.Dispatched;
        });
        return refusal is null ? null : SuppressedReply(state, refusal);
    }

    private void Fail(string attemptId, string? error) => _store.Update(RunId, state =>
    {
        var attempt = state.Attempts.Single(item => item.Id == attemptId);
        attempt.Status = WebResearchAttemptStatus.Failed;
        attempt.ErrorCode = Code(error);
        Expired(state);
    });

    private WebResearchReply CompleteSearch(string callId, string signature, WebSearchRequest request,
        string queryKey, string attemptId, WebSearchResponse response, int tokenCeiling)
    {
        var result = _store.Update(RunId, state =>
        {
            var attempt = state.Attempts.Single(item => item.Id == attemptId);
            attempt.Status = WebResearchAttemptStatus.Succeeded;
            Expired(state);
            if (!MayPublishAdmittedResult(state))
            {
                SaveReceipt(state, callId, signature, Reply(state, request.TargetedGap, error: state.TerminalReason));
                return;
            }
            var discovered = new List<WebResearchCandidate>();
            var added = 0;
            foreach (var candidate in response.Candidates.Take(32))
            {
                if (!TryCandidate(candidate, request, out var normalized)) continue;
                var existing = state.Candidates.SingleOrDefault(item => item.Id == normalized.Id);
                if (existing is null) { existing = normalized; state.Candidates.Add(existing); added++; }
                if (!existing.Providers.Contains(attempt.ProviderId, StringComparer.Ordinal)) existing.Providers.Add(Safe(attempt.ProviderId, 64));
                if (!existing.OriginalUrls.Contains(candidate.Url, StringComparer.Ordinal)) existing.OriginalUrls.Add(candidate.Url);
                if (!existing.GapIds.Contains(request.TargetedGap, StringComparer.Ordinal)) existing.GapIds.Add(request.TargetedGap);
                discovered.Add(existing);
                if (discovered.Count >= request.MaxResults) break;
            }
            Progress(state, added > 0);
            var applied = response.AppliedFilters.Select(value => Safe(value, 128)).Take(16).ToList();
            if (request.RequiredDomains.Count > 0) applied.Add("required_domains_enforced_locally");
            var unsupported = response.UnsupportedFilters.Select(value => Safe(value, 128)).Take(16).ToArray();
            var reply = Reply(state, request.TargetedGap, discovered, applied: applied, unsupported: unsupported);
            while (ContextTokens(reply) > tokenCeiling && discovered.Count > 0)
            {
                discovered.RemoveAt(discovered.Count - 1);
                reply = Reply(state, request.TargetedGap, discovered, applied: applied, unsupported: unsupported,
                    error: discovered.Count == 0 ? "context_limit_too_small" : null);
            }
            if (ContextTokens(reply) > tokenCeiling)
                reply = Reply(state, request.TargetedGap, error: "context_limit_too_small", unsupported: ["filter_metadata_truncated"]);
            attempt.ActualTokens = ContextTokens(reply);
            state.QueryReceipts[queryKey] = callId;
            SaveReceipt(state, callId, signature, reply);
        });
        return result.ToolReceipts.ContainsKey(callId) ? ReadReceipt(result, callId) : Reply(result, request.TargetedGap, error: result.TerminalReason);
    }

    private WebResearchReply CompleteCached(string callId, string signature, string gapId, WebResearchReply cached, int tokenCeiling)
    {
        var admission = Reserve(callId + "/cache", callId, signature, gapId, "cache", "cache",
            WebResearchOperation.CachedContext, tokenCeiling, newRound: false);
        if (admission is not null) return admission;
        var result = _store.Update(RunId, state =>
        {
            Progress(state, false);
            var candidates = cached.Candidates.Where(candidate => state.Candidates.Any(item => item.Id == candidate.Id))
                .Select(candidate => state.Candidates.Single(item => item.Id == candidate.Id)).ToArray();
            foreach (var candidate in candidates)
            {
                var stored = state.Candidates.Single(item => item.Id == candidate.Id);
                if (!stored.GapIds.Contains(gapId, StringComparer.Ordinal)) stored.GapIds.Add(gapId);
            }
            var reply = Reply(state, gapId, candidates, cached.Passages, cached: true,
                applied: cached.AppliedFilters, unsupported: cached.UnsupportedFilters);
            while (ContextTokens(reply) > tokenCeiling && candidates.Length > 0)
            {
                candidates = candidates[..^1];
                reply = Reply(state, gapId, candidates, cached: true, error: "cached_context_truncated");
            }
            if (ContextTokens(reply) > tokenCeiling) reply = Reply(state, gapId, cached: true, error: "cached_context_truncated");
            var attempt = state.Attempts.Single(item => item.Id == callId + "/cache");
            attempt.Status = WebResearchAttemptStatus.Succeeded;
            attempt.ActualTokens = ContextTokens(reply);
            SaveReceipt(state, callId, signature, reply);
        });
        return ReadReceipt(result, callId);
    }

    private WebResearchReply CompleteReadFailure(string callId, string signature, string gapId, string error,
        WebReadResponse response)
    {
        var result = _store.Update(RunId, state =>
        {
            state.ReadFailures.Add(new(callId, Safe(response.OriginalUrl, 2048), Safe(response.FinalUrl, 2048),
                response.ReadAt, Safe(response.Method, 64), response.RedirectChain.Take(4).Select(hop => Safe(hop, 2048)).ToArray(),
                error, response.Truncated || response.RedirectChain.Any(hop => hop.Length > 2048)));
            Progress(state, false);
            if (state.Status == WebResearchStatus.Active && state.Candidates.All(candidate =>
                    candidate.OriginalUrls.All(url => state.Attempts.Any(attempt => attempt.OperationKey == "read:" + new Uri(url).AbsoluteUri
                        && attempt.Status == WebResearchAttemptStatus.Failed))))
                Terminal(state, WebResearchStatus.SourcesUnavailable, "discovered_sources_unavailable");
            SaveReceipt(state, callId, signature, Reply(state, gapId, error: error)
                with { ReadErrors = [state.ReadFailures[^1]] });
        });
        return ReadReceipt(result, callId);
    }

    private bool Expired(WebResearchState state)
    {
        if (state.Status != WebResearchStatus.Active) return true;
        if (_clock.GetUtcNow() < state.Deadline) return false;
        Terminal(state, WebResearchStatus.BudgetExhausted, "deadline_exhausted");
        return true;
    }

    private static void Progress(WebResearchState state, bool progress)
    {
        state.NoProgressCycles = progress ? 0 : state.NoProgressCycles + 1;
        if (state.Status == WebResearchStatus.Active && state.NoProgressCycles >= state.Limits.NoProgressCycles)
            Terminal(state, WebResearchStatus.NoNewEvidence, "no_progress_cycles");
    }

    private static void Terminal(WebResearchState state, WebResearchStatus status, string reason)
    {
        if (state.Status != WebResearchStatus.Active) return;
        state.Status = status;
        state.TerminalReason = reason;
    }

    private CancellationTokenSource DeadlineToken(CancellationToken cancellationToken)
    {
        var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = State.Deadline - _clock.GetUtcNow();
        token.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return token;
    }

    private static void CheckGap(WebResearchState state, string gapId)
    {
        if (!state.Questions.Any(question => question.Id == gapId && !question.Resolved))
            throw new ArgumentException("targetedGap must name a current unresolved question.");
    }

    private static void ValidateGapInput(string value)
    {
        if (value.Length is < 1 or > 128 || value.Any(char.IsControl)) throw new ArgumentException("Invalid targetedGap.");
    }

    private static WebResearchReply Reply(WebResearchState state, string? gapId,
        IReadOnlyList<WebResearchCandidate>? candidates = null, IReadOnlyList<WebResearchPassage>? passages = null,
        string? error = null, bool cached = false, IReadOnlyList<string>? applied = null, IReadOnlyList<string>? unsupported = null) =>
        new(StatusName(state.Status), gapId,
            state.Questions.Select(question => new WebResearchQuestion(question.Id, Safe(question.Text, 160)) { Resolved = question.Resolved }).ToArray(),
            candidates ?? [], passages ?? [],
            new(state.QueriesUsed, state.ReadsUsed, state.AttemptsUsed, state.RoundsUsed,
                state.EvidenceTokensUsed, state.CachedContextTokensUsed, state.Deadline),
            error, state.Status != WebResearchStatus.SufficientEvidence, cached, applied, unsupported);

    private static WebResearchReply SuppressedReply(WebResearchState state, string? error)
    {
        var reply = new WebResearchReply(StatusName(state.Status), null, [], [], [],
            new(state.QueriesUsed, state.ReadsUsed, state.AttemptsUsed, state.RoundsUsed,
                state.EvidenceTokensUsed, state.CachedContextTokensUsed, state.Deadline),
            Code(error) is "source_unavailable" ? "replay_suppressed" : Code(error), Partial: true)
        { ContextSuppressed = true };
        if (ContextTokens(reply) > 1024)
            throw new InvalidOperationException("Suppressed research status exceeded its global byte ceiling.");
        return reply;
    }

    private static void SaveReceipt(WebResearchState state, string callId, string signature, WebResearchReply reply)
    {
        var attempt = state.Attempts.LastOrDefault(item => item.ToolCallId == callId);
        if (attempt is not null)
        {
            reply = BoundReply(reply, attempt.ReservedTokens);
            attempt.ActualTokens = ContextTokens(reply);
        }
        state.ReceiptSignatures[callId] = signature;
        state.ToolReceipts[callId] = JsonSerializer.Serialize(reply, WebResearchJson.Default.WebResearchReply);
    }

    private static WebResearchReply ReadReceipt(WebResearchState state, string callId)
    {
        var reply = JsonSerializer.Deserialize(state.ToolReceipts[callId], WebResearchJson.Default.WebResearchReply)! with
        {
            Status = StatusName(state.Status),
            Budget = new(state.QueriesUsed, state.ReadsUsed, state.AttemptsUsed, state.RoundsUsed,
                state.EvidenceTokensUsed, state.CachedContextTokensUsed, state.Deadline),
        };
        return BoundReply(reply, state.Attempts.LastOrDefault(item => item.ToolCallId == callId)?.ReservedTokens ?? 4096);
    }

    private static bool TryCandidate(WebSearchCandidate candidate, WebSearchRequest request, out WebResearchCandidate normalized)
    {
        normalized = null!;
        if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0
            || candidate.Url.Length > 2048 || new RedactionPolicy().Redact(candidate.Url) != candidate.Url
            || request.RequiredDomains.Count > 0 && !request.RequiredDomains.Any(domain =>
                uri.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            return false;
        var canonical = CanonicalUrl(candidate.Url);
        normalized = new(Hash(canonical + "|" + candidate.Version + "|" + candidate.PublishedAt + "|" + candidate.UpdatedAt),
            canonical, candidate.Url, Safe(candidate.Title, 160), Safe(candidate.Snippet, 300),
            Date(candidate.PublishedAt), Date(candidate.UpdatedAt), SafeNullable(candidate.Version, 256),
            Safe(candidate.CommonOrigin ?? uri.IdnHost, 256));
        return true;
    }

    private static WebResearchPassage Passage(WebResearchDocument document, string gapId, int offset, int length)
    {
        var id = Hash(document.Id + "|" + gapId + "|" + document.OriginalUrl + "|" + document.Version
            + "|" + document.ReadAt.ToString("O") + "|" + offset + "|" + length);
        return new(id, gapId, document.Id, document.Text.Substring(offset, length), offset, length,
            "text:sha256:" + document.ContentHash + "#chars=" + offset + "-" + (offset + length),
            document.ContentHash, document.OriginalUrl, document.FinalUrl, document.ReadAt,
            document.PublishedAt, document.UpdatedAt, document.Version, document.Method,
            document.Truncated || offset + length < document.Text.Length, document.CommonOrigin)
        { RedirectChain = document.RedirectChain.ToList() };
    }

    public static string CanonicalUrl(string value)
    {
        var uri = new Uri(value);
        var builder = new UriBuilder(uri) { Fragment = "" };
        var parts = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                && !part.StartsWith("gclid=", StringComparison.OrdinalIgnoreCase) && !part.StartsWith("fbclid=", StringComparison.OrdinalIgnoreCase));
        builder.Query = string.Join('&', parts);
        return builder.Uri.AbsoluteUri;
    }

    private static string ValidateOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Allowed origins must be exact HTTPS origins.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string ValidateDomain(string value)
    {
        if (value.Length is < 1 or > 253 || value.Contains('/') || value.Contains(':')
            || Uri.CheckHostName(value) != UriHostNameType.Dns) throw new ArgumentException("Invalid required domain.");
        return value.ToLowerInvariant().TrimEnd('.');
    }

    private static string? Date(string? value) => DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var date) ? date.ToString("O") : null;
    private static string Safe(string value, int maximum)
    {
        var redacted = new RedactionPolicy().Redact(value);
        return redacted.Length > maximum ? redacted[..maximum] : redacted;
    }
    private static string? SafeNullable(string? value, int maximum) => value is null ? null : Safe(value, maximum);
    private static string Code(string? value) => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        ? value : "source_unavailable";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static long ContextTokens(WebResearchReply reply) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(reply, WebResearchJson.Default.WebResearchReply));
    private bool MayPublishAdmittedResult(WebResearchState state) => _clock.GetUtcNow() < state.Deadline
        && (state.Status == WebResearchStatus.Active || state.Status == WebResearchStatus.BudgetExhausted
            && state.TerminalReason == "shared_run_budget_exhausted");

    private static WebResearchReply BoundReply(WebResearchReply reply, long ceiling)
    {
        while (ContextTokens(reply) > ceiling && reply.Candidates.Count > 0)
            reply = reply with { Candidates = reply.Candidates.Take(reply.Candidates.Count - 1).ToArray(), ErrorCode = reply.ErrorCode ?? "context_truncated", Partial = true };
        while (ContextTokens(reply) > ceiling && reply.Passages.Count > 0)
            reply = reply with { Passages = reply.Passages.Take(reply.Passages.Count - 1).ToArray(), ErrorCode = reply.ErrorCode ?? "context_truncated", Partial = true };
        if (ContextTokens(reply) > ceiling)
            reply = reply with { AppliedFilters = null, UnsupportedFilters = ["filter_metadata_truncated"],
                ReadErrors = reply.ReadErrors.Select(error => error with { OriginalUrl = "", FinalUrl = "", RedirectChain = [], Truncated = true }).ToArray(),
                Questions = reply.Questions.Select(question => new WebResearchQuestion(question.Id, "") { Resolved = question.Resolved }).ToArray(),
                ErrorCode = reply.ErrorCode ?? "context_truncated", Partial = true };
        if (ContextTokens(reply) > ceiling)
            reply = reply with { TargetedGap = null, Questions = [], ReadErrors = [], UnsupportedFilters = null };
        if (ContextTokens(reply) > ceiling) throw new InvalidOperationException("Context reservation cannot hold bounded research metadata.");
        return reply;
    }
    private static void ValidateContextCeiling(WebResearchState state, string gapId, int ceiling)
    {
        if (ContextTokens(Reply(state, gapId)) + 256 > ceiling)
            throw new ArgumentException("Context ceiling cannot hold the question and accounting metadata.");
    }
    private int DefaultContextCeiling(string gapId, bool search)
    {
        var state = State;
        var available = Math.Max(0, state.Limits.EvidenceTokens - state.EvidenceTokensUsed - state.CachedContextTokensUsed);
        var closingReserve = ContextTokens(Reply(state, gapId)) + 256;
        // Keep room for a read and assessment with the smallest preset. Explicit caller ceilings remain strict.
        var suggested = search ? Math.Min(2048L, available / 3) : Math.Min(4096L, available - closingReserve);
        return (int)Math.Clamp(suggested, 512, 32768);
    }
    public static string StatusName(WebResearchStatus status) => status switch
    {
        WebResearchStatus.SufficientEvidence => "sufficient_evidence", WebResearchStatus.NoNewEvidence => "no_new_evidence",
        WebResearchStatus.BudgetExhausted => "budget_exhausted", WebResearchStatus.SourcesUnavailable => "sources_unavailable", _ => "active",
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebResearchReply))]
[JsonSerializable(typeof(WebSearchRequest))]
internal partial class WebResearchJson : JsonSerializerContext;
