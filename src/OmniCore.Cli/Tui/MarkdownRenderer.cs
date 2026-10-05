namespace OmniCore.Cli;

/// <summary>
/// Terminal adaptation of OmniCoder's MarkdownRenderer: emit styled spans, highlight
/// a whole fenced block, and leave unknown/incomplete markup readable.
/// Keeps OmniCore's stricter matching of fence character and opening length.
/// </summary>
internal static class MarkdownRenderer
{
    internal const int MaxLength = 400_000;

    internal static IReadOnlyList<IReadOnlyList<ConversationSpan>> Render(string text)
    {
        var source = text.Replace("\r\n", "\n").Split('\n');
        if (text.Length > MaxLength)
            return source.Select(line => (IReadOnlyList<ConversationSpan>)new[]
                { new ConversationSpan(line, ConversationStyle.Text) }).ToArray();

        var rows = new List<IReadOnlyList<ConversationSpan>>();
        string? fence = null;
        string? language = null;
        var body = new List<string>();
        foreach (var raw in source)
        {
            var line = raw.TrimStart();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = line[0];
                var count = line.TakeWhile(c => c == marker).Count();
                var suffix = line[count..].Trim();
                if (fence is null)
                {
                    fence = new string(marker, count);
                    language = suffix;
                    rows.Add(new[] { new ConversationSpan("  </> " + (suffix.Length == 0 ? "code" : suffix),
                        ConversationStyle.CodeHeader) });
                    continue;
                }
                if (marker == fence[0] && count >= fence.Length && suffix.Length == 0)
                {
                    AppendCode();
                    fence = null;
                    language = null;
                    rows.Add(new[] { new ConversationSpan("  ", ConversationStyle.CodeHeader) });
                    continue;
                }
            }
            if (fence is not null) { body.Add(raw); continue; }
            var heading = line.TakeWhile(c => c == '#').Count();
            if (heading is > 0 and <= 6 && line.Length > heading && line[heading] == ' ')
            {
                rows.Add(new[] { new ConversationSpan(line[(heading + 1)..], ConversationStyle.Heading) });
                continue;
            }
            var content = line.StartsWith("- ") || line.StartsWith("* ") ? "• " + line[2..] : raw;
            rows.Add(Inline(content));
        }
        AppendCode(); // An incomplete fence must remain visible during streaming.
        return rows;

        void AppendCode()
        {
            if (body.Count == 0) return;
            foreach (var highlighted in SyntaxHighlighter.Highlight(string.Join('\n', body), language))
            {
                var spans = new List<ConversationSpan> { new("  ", ConversationStyle.Code) };
                spans.AddRange(highlighted);
                rows.Add(spans);
            }
            body.Clear();
        }
    }

    private static IReadOnlyList<ConversationSpan> Inline(string text)
    {
        var result = new List<ConversationSpan>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var code = text.IndexOf('`', cursor);
            var bold = text.IndexOf("**", cursor, StringComparison.Ordinal);
            var start = code < 0 ? bold : bold < 0 ? code : Math.Min(code, bold);
            if (start < 0) break;
            var delimiter = start == code ? "`" : "**";
            var end = text.IndexOf(delimiter, start + delimiter.Length, StringComparison.Ordinal);
            if (end < 0) break; // Unsupported/unclosed markup stays literal, not lost.
            if (start > cursor) result.Add(new(text[cursor..start], ConversationStyle.Text));
            result.Add(new(text[(start + delimiter.Length)..end], delimiter == "`" ? ConversationStyle.InlineCode : ConversationStyle.Heading));
            cursor = end + delimiter.Length;
        }
        if (cursor < text.Length) result.Add(new(text[cursor..], ConversationStyle.Text));
        return result;
    }
}
