namespace OmniCore.Abstractions;

/// <summary>Provider-independent web discovery. Snippets are candidates, never read evidence.</summary>
public sealed record WebSearchRequest(string Query, string TargetedGap, int MaxResults,
    IReadOnlyList<string> RequiredDomains, bool PreferPrimarySources,
    IReadOnlyDictionary<string, string> OptionalFilters);

public sealed record WebSearchCandidate(string Url, string Title, string Snippet, string ProviderId,
    string? PublishedAt = null, string? UpdatedAt = null, string? Version = null, string? CommonOrigin = null);

public sealed record WebSearchResponse(IReadOnlyList<WebSearchCandidate> Candidates,
    IReadOnlyList<string> AppliedFilters, IReadOnlyList<string> UnsupportedFilters,
    string? ErrorCode = null, bool Retryable = false);

public interface IWebSearchProvider
{
    string Id { get; }
    Uri Endpoint { get; }
    Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken);
}

/// <summary>Only explicit HTTPS origins may be contacted, including every redirect hop.</summary>
public sealed record WebReadRequest(Uri Url, int MaximumBytes, int MaximumRedirects,
    IReadOnlyList<string> AllowedOrigins);

/// <summary>Text is an untrusted snapshot. Missing dates remain null; extraction failures are explicit.</summary>
public sealed record WebReadResponse(string OriginalUrl, string FinalUrl, string Text, string Method,
    DateTimeOffset ReadAt, IReadOnlyList<string> RedirectChain, bool Truncated,
    string? PublishedAt = null, string? UpdatedAt = null, string? Version = null, string? ErrorCode = null);

public interface IWebReadTransport
{
    Task<WebReadResponse> ReadAsync(WebReadRequest request, CancellationToken cancellationToken);
}

/// <summary>Initial, configurable limits. Attempts include retries and failed provider dispatches.</summary>
public sealed record WebResearchLimits(int Queries, int Reads, int Rounds, long EvidenceTokens,
    int DurationSeconds, int Attempts = 32, int Renderings = 0, int OcrOperations = 0,
    int NoProgressCycles = 2)
{
    public static WebResearchLimits Point { get; } = new(2, 3, 1, 4000, 45);
    public static WebResearchLimits Normal { get; } = new(4, 6, 2, 10000, 120);
    public static WebResearchLimits Broad { get; } = new(8, 12, 3, 20000, 300);

    public void Validate()
    {
        if (Queries is < 1 or > 100 || Reads is < 1 or > 100 || Rounds is < 1 or > 20
            || EvidenceTokens is < 64 or > 1_000_000 || DurationSeconds is < 1 or > 3600
            || Attempts is < 1 or > 1000 || Renderings is < 0 or > 100 || OcrOperations is < 0 or > 100
            || NoProgressCycles is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(WebResearchLimits));
    }
}

public enum WebResearchStatus { Active, SufficientEvidence, NoNewEvidence, BudgetExhausted, SourcesUnavailable }
public enum WebResearchOperation { Query, Read, Rendering, Ocr, CachedContext }
public enum WebResearchAttemptStatus { Reserved, Dispatched, Succeeded, Failed }

public sealed record WebResearchQuestion(string Id, string Text)
{
    public bool Resolved { get; set; }
    public string? Answer { get; set; }
    public List<string> EvidenceIds { get; init; } = [];
    public string? ContradictsEvidenceId { get; init; }
}

public sealed record WebResearchAttempt(string Id, string ToolCallId, string GapId, string OperationKey,
    string ProviderId, WebResearchOperation Operation, long ReservedTokens, DateTimeOffset ReservedAt)
{
    public WebResearchAttemptStatus Status { get; set; }
    public string? ErrorCode { get; set; }
    public long ActualTokens { get; set; }
}

public sealed record WebResearchCandidate(string Id, string CanonicalUrl, string Url, string Title,
    string Snippet, string? PublishedAt, string? UpdatedAt, string? Version, string CommonOrigin)
{
    public List<string> Providers { get; init; } = [];
    public List<string> GapIds { get; init; } = [];
    public List<string> OriginalUrls { get; init; } = [];
}

public sealed record WebResearchDocumentSource(string OriginalUrl, string FinalUrl, DateTimeOffset ReadAt,
    string? PublishedAt, string? UpdatedAt, string? Version, IReadOnlyList<string> RedirectChain, string CandidateId);

public sealed record WebResearchDocument(string Id, string OriginalUrl, string FinalUrl, string Text,
    string ContentHash, string Method, DateTimeOffset ReadAt, string? PublishedAt, string? UpdatedAt,
    string? Version, bool Truncated, string CommonOrigin)
{
    public List<string> OriginalUrls { get; init; } = [];
    public List<string> RedirectChain { get; init; } = [];
    public List<WebResearchDocumentSource> Sources { get; init; } = [];
}

public sealed record WebResearchPassage(string Id, string GapId, string DocumentId, string Text,
    int Offset, int Length, string Locator, string ContentHash, string OriginalUrl, string FinalUrl,
    DateTimeOffset ReadAt, string? PublishedAt, string? UpdatedAt, string? Version, string Method,
    bool Truncated, string CommonOrigin)
{
    public List<string> RedirectChain { get; init; } = [];
}

public sealed record WebResearchBudgetSnapshot(int Queries, int Reads, int Attempts, int Rounds,
    long EvidenceTokens, long CachedContextTokens, DateTimeOffset Deadline);

public sealed record WebResearchReadFailure(string ToolCallId, string OriginalUrl, string FinalUrl,
    DateTimeOffset ReadAt, string Method, IReadOnlyList<string> RedirectChain, string ErrorCode, bool Truncated);

public sealed record WebResearchReply(string Status, string? TargetedGap,
    IReadOnlyList<WebResearchQuestion> Questions, IReadOnlyList<WebResearchCandidate> Candidates,
    IReadOnlyList<WebResearchPassage> Passages, WebResearchBudgetSnapshot Budget,
    string? ErrorCode = null, bool Partial = false, bool Cached = false,
    IReadOnlyList<string>? AppliedFilters = null, IReadOnlyList<string>? UnsupportedFilters = null,
    string Trust = "untrusted_web_data")
{
    public IReadOnlyList<WebResearchReadFailure> ReadErrors { get; init; } = [];
    public bool ContextSuppressed { get; init; }
}

/// <summary>Durable admission of actual web tool text into a model invocation.</summary>
public sealed record WebResearchContextDelivery(string Id, IReadOnlyList<string> ToolCallIds,
    long Utf8Bytes, long AdditionalTokens, DateTimeOffset ReservedAt, bool PhysicalRetry = false);

/// <summary>One bounded durable controller state shared by the entire Run, never by lane/agent.</summary>
public sealed class WebResearchState
{
    public string RunId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset Deadline { get; init; }
    public WebResearchLimits Limits { get; init; } = WebResearchLimits.Normal;
    public WebResearchStatus Status { get; set; }
    public string? TerminalReason { get; set; }
    public int QueriesUsed { get; set; }
    public int ReadsUsed { get; set; }
    public int AttemptsUsed { get; set; }
    public int RoundsUsed { get; set; }
    public int RenderingsUsed { get; set; }
    public int OcrOperationsUsed { get; set; }
    public long EvidenceTokensUsed { get; set; }
    public long CachedContextTokensUsed { get; set; }
    public int NoProgressCycles { get; set; }
    public List<WebResearchQuestion> Questions { get; init; } = [];
    public List<WebResearchAttempt> Attempts { get; init; } = [];
    public List<WebResearchCandidate> Candidates { get; init; } = [];
    public List<WebResearchDocument> Documents { get; init; } = [];
    public List<WebResearchPassage> Passages { get; init; } = [];
    public List<WebResearchReadFailure> ReadFailures { get; init; } = [];
    public Dictionary<string, string> QueryReceipts { get; init; } = [];
    public Dictionary<string, string> ToolReceipts { get; init; } = [];
    public Dictionary<string, string> ReceiptSignatures { get; init; } = [];
    public Dictionary<string, string> DeliveredToolCalls { get; set; } = [];
    public List<WebResearchContextDelivery> ContextDeliveries { get; set; } = [];
}

public interface IWebResearchStore
{
    WebResearchState? Read(string runId);
    WebResearchState Initialize(WebResearchState initial);
    WebResearchState Update(string runId, Action<WebResearchState> mutation);
}
