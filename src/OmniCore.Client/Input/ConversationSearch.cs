namespace OmniCore.Client;

public sealed record ConversationSearchHit(string BlockId, int BlockIndex, ConversationRole Role,
    int Offset, int Length, string Snippet, int SnippetOffset);

public sealed record ConversationSearchResult(IReadOnlyList<ConversationSearchHit> Hits,
    bool Limited = false, bool QueryTooLong = false);

/// <summary>Searches only the caller's already visible transcript. No IO, retrieval or authority.</summary>
public static class ConversationSearch
{
    public const int MaximumQueryLength = 256;
    public const int MaximumResults = 200;
    public const int MaximumCharacters = 2_000_000;
    public const int MaximumBlocks = 10_000;

    public static ConversationSearchResult Search(IReadOnlyList<ConversationBlock> blocks, string query)
    {
        if (query.Length > MaximumQueryLength) return new([], QueryTooLong: true);
        if (string.IsNullOrWhiteSpace(query)) return new([]);
        var hits = new List<ConversationSearchHit>();
        var remaining = MaximumCharacters;
        for (var index = 0; index < blocks.Count; index++)
        {
            if (index >= MaximumBlocks || remaining == 0) return new(hits, Limited: true);
            var block = blocks[index];
            var count = Math.Min(remaining, block.Text.Length);
            var start = 0;
            while (start <= count - query.Length)
            {
                var match = block.Text.IndexOf(query, start, count - start, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;
                var left = Math.Max(0, match - 80);
                var right = Math.Min(block.Text.Length, match + query.Length + 80);
                // Keep UTF-16 pairs intact without changing the original source or match offsets.
                if (left > 0 && char.IsLowSurrogate(block.Text[left])) left--;
                if (right < block.Text.Length && char.IsLowSurrogate(block.Text[right])) right++;
                hits.Add(new(block.Id, index, block.Role, match, query.Length,
                    block.Text[left..right], left));
                if (hits.Count == MaximumResults) return new(hits, Limited: true);
                start = match + query.Length;
            }
            remaining -= count;
            if (count < block.Text.Length) return new(hits, Limited: true);
        }
        return new(hits);
    }
}
