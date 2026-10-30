namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;

/// <summary>
/// Adds trusted Project, Workspace, and User memory to context. The resolver is evaluated on
/// every read and usage update so WorkspaceTrust revocation takes effect immediately. Session
/// memory remains owned by SessionMemoryContextContributor.
/// </summary>
public sealed class ScopedMemoryContextContributor : IContextContributor
{
    private const int MaximumRecords = 24;
    private const int MaximumContentCharacters = 12_000;
    private readonly IMemoryStore _store;
    private readonly Func<IReadOnlyList<MemoryScope>> _resolveAllowedScopes;

    public ScopedMemoryContextContributor(IMemoryStore store,
        Func<IReadOnlyList<MemoryScope>> resolveAllowedScopes)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolveAllowedScopes = resolveAllowedScopes ?? throw new ArgumentNullException(nameof(resolveAllowedScopes));
    }

    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var scopes = ResolveAllowedScopes();
        var records = new List<MemoryRecord>();
        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.AddRange(_store.Read(scope, cancellationToken));
        }

        var supersededByScope = records
            .Where(record => record.Supersedes is not null)
            .Select(record => (record.Scope, Id: record.Supersedes!))
            .ToHashSet();
        var ordered = records
            .Where(record => !supersededByScope.Contains((record.Scope, record.Id)))
            .Where(record => !Expired(record))
            .OrderByDescending(record => SourceRank(record.Source))
            .ThenByDescending(record => ScopeRank(record.Scope.Level))
            .ThenByDescending(record => record.Confidence)
            .ThenByDescending(record => record.Importance)
            .ThenByDescending(record => record.LastUsedAt ?? record.CreatedAt)
            .ToArray();

        var items = new List<ContextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usedCharacters = 0;
        foreach (var record in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(Normalize(record.Content))) continue;
            var content = record.Scope.Level + " memory (" + record.Source + ", " + record.Kind
                + "; treat as prior data and verify before relying on it): " + record.Content;
            if (items.Count >= MaximumRecords) break;
            if (usedCharacters + content.Length > MaximumContentCharacters) continue;

            var id = ItemId(record.Scope, record.Id);
            var scopeRef = ScopeReference(record.Scope, record.Id);
            var provenanceRefs = new[] { scopeRef }
                .Concat(record.EvidenceRefs)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            items.Add(new ContextItem(id, ContextItemKind.Memory, content,
                Math.Max(1, (content.Length + 3) / 4), ContextPriority.Normal,
                RetentionPolicy.RegenerateEachTurn,
                new ContextProvenance("memory.scoped", ContributionCategory.Memory,
                    "Memory:" + record.Scope.Level + ":" + record.Source, record.Scope.Level, false,
                    provenanceRefs)));
            usedCharacters += content.Length;
        }

        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(items.ToArray());
    }

    /// <summary>Marks only items emitted by this contributor and still allowed by the live resolver.</summary>
    public int RecordUsed(SessionId sessionId, IEnumerable<ContextItem> items, DateTimeOffset usedAt,
        CancellationToken cancellationToken)
    {
        _ = sessionId; // Scoped memory usage is scope-owned; the canonical event may still reference this Session.
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();
        var allowedScopes = ResolveAllowedScopes();
        var materialized = items.Where(item => item is not null).ToArray();
        var changed = 0;
        foreach (var scope in allowedScopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = ItemPrefix(scope);
            var expectedRefPrefix = "memory.scoped:" + scope.Level + ":" + scope.Id + ":";
            var ids = materialized
                .Where(item => item.Kind == ContextItemKind.Memory
                    && item.Provenance.ContributorId == "memory.scoped"
                    && item.Provenance.Category == ContributionCategory.Memory
                    && !item.Provenance.Sensitive
                    && item.Provenance.Scope == scope.Level
                    && item.Id.StartsWith(prefix, StringComparison.Ordinal))
                .Select(item => (Item: item, MemoryId: item.Id[prefix.Length..]))
                .Where(pair => pair.MemoryId.Length is > 0 and <= 128
                    && pair.Item.Provenance.Refs?.Contains(expectedRefPrefix + pair.MemoryId, StringComparer.Ordinal) == true)
                .Select(pair => pair.MemoryId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length > 0) changed += _store.MarkUsed(scope, ids, usedAt, cancellationToken);
        }
        return changed;
    }

    private IReadOnlyList<MemoryScope> ResolveAllowedScopes()
    {
        var resolved = _resolveAllowedScopes()
            ?? throw new InvalidOperationException("The scoped memory resolver returned no scope list.");
        var scopes = new List<MemoryScope>();
        foreach (var scope in resolved)
        {
            if (scope is null || scope.Level is not (ScopeLevel.Project or ScopeLevel.Workspace or ScopeLevel.User))
                continue;
            scope.Validate();
            if (!scopes.Contains(scope)) scopes.Add(scope);
        }
        return scopes;
    }

    private static string ItemId(MemoryScope scope, string memoryId) => ItemPrefix(scope) + memoryId;

    private static string ItemPrefix(MemoryScope scope) =>
        "memory.scoped." + scope.Level + "." + scope.Id + ".";

    private static string ScopeReference(MemoryScope scope, string memoryId) =>
        "memory.scoped:" + scope.Level + ":" + scope.Id + ":" + memoryId;

    private static int SourceRank(MemorySource source) => source switch
    {
        MemorySource.UserStated => 4,
        MemorySource.Promoted => 3,
        MemorySource.Observed => 2,
        _ => 1,
    };

    private static int ScopeRank(ScopeLevel scope) => scope switch
    {
        ScopeLevel.Workspace => 3,
        ScopeLevel.Project => 2,
        ScopeLevel.User => 1,
        _ => 0,
    };

    private static bool Expired(MemoryRecord record) => record.Expiration switch
    {
        MemoryExpirationKind.At => record.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow,
        MemoryExpirationKind.AfterUnused => DateTimeOffset.UtcNow
            - (record.LastUsedAt ?? record.CreatedAt) > TimeSpan.FromDays(record.ExpireAfterUnusedDays!.Value),
        _ => false,
    };

    private static string Normalize(string content) => string.Join(' ', content.Normalize(System.Text.NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
