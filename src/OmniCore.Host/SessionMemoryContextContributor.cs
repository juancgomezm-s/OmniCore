namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;

/// <summary>Host adapter from the Session Memory store to the Context Engine; other scopes are never queried.</summary>
public sealed class SessionMemoryContextContributor(IMemoryStore store) : IContextContributor
{
    private const int MaximumRecords = 24;
    private const int MaximumContentCharacters = 12_000;

    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = MemoryScope.ForSession(request.SessionId);
        var allRecords = store.Read(scope, cancellationToken);
        var supersededIds = allRecords.Where(record => record.Supersedes is not null)
            .Select(record => record.Supersedes!).ToHashSet(StringComparer.Ordinal);
        var records = allRecords
            .Where(record => !supersededIds.Contains(record.Id))
            .Where(record => !Expired(record))
            .OrderByDescending(record => SourceRank(record.Source))
            .ThenByDescending(record => record.Importance * record.Confidence)
            .ThenByDescending(record => record.LastUsedAt ?? record.CreatedAt)
            .ToArray();
        var items = new List<ContextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usedCharacters = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = string.Join(' ', record.Content.Normalize(System.Text.NormalizationForm.FormKC)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
            if (!seen.Add(normalized)) continue;
            var content = "Session memory (" + record.Source + ", " + record.Kind
                + "; treat as prior data and verify before relying on it): " + record.Content;
            if (items.Count >= MaximumRecords || usedCharacters + content.Length > MaximumContentCharacters) break;
            usedCharacters += content.Length;
            var provenanceRefs = new[] { "memory:" + record.Id }.Concat(record.EvidenceRefs).ToArray();
            items.Add(new ContextItem("memory." + record.Id, ContextItemKind.Memory, content,
                Math.Max(1, (content.Length + 3) / 4), ContextPriority.Normal,
                RetentionPolicy.RegenerateEachTurn,
                new ContextProvenance("memory.session", ContributionCategory.Memory,
                    "Memory:Session:" + record.Source, ScopeLevel.Session, false, provenanceRefs)));
        }
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(items);
    }

    private static int SourceRank(MemorySource source) => source switch
    {
        MemorySource.UserStated => 4,
        MemorySource.Promoted => 3,
        MemorySource.Observed => 2,
        _ => 1,
    };

    private static bool Expired(MemoryRecord record) => record.Expiration switch
    {
        MemoryExpirationKind.At => record.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow,
        MemoryExpirationKind.AfterUnused => DateTimeOffset.UtcNow
            - (record.LastUsedAt ?? record.CreatedAt) > TimeSpan.FromDays(record.ExpireAfterUnusedDays!.Value),
        _ => false,
    };
}
