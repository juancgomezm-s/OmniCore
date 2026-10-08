namespace OmniCore.Cli;

/// <summary>
/// Terminal adaptation of OmniCoder's MarkdownRenderer: emit styled spans, highlight
/// a whole fenced block, and leave unknown/incomplete markup readable.
/// Keeps OmniCore's stricter matching of fence character and opening length.
/// </summary>
internal static class MarkdownRenderer
{
    internal const int MaxLength = 400_000;

    internal static IReadOnlyList<IReadOnlyList<ConversationSpan>> Render(string text, int width = 120)
    {
        var source = text.Replace("\r\n", "\n").Split('\n');
        if (text.Length > MaxLength)
            return source.Select(line => (IReadOnlyList<ConversationSpan>)new[]
                { new ConversationSpan(line, ConversationStyle.Text) }).ToArray();

        var rows = new List<IReadOnlyList<ConversationSpan>>();
        string? fence = null;
        string? language = null;
        var body = new List<string>();
        for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
        {
            var raw = source[sourceIndex];
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
            if (MarkdownTableRenderer.TryRender(source, sourceIndex, width, out var table, out var consumed))
            {
                rows.AddRange(table);
                sourceIndex += consumed - 1;
                continue;
            }
            var heading = line.TakeWhile(c => c == '#').Count();
            if (heading is > 0 and <= 6 && line.Length > heading && line[heading] == ' ')
            {
                rows.Add(Inline(line[(heading + 1)..]).Select(span => span.Style == ConversationStyle.Text
                    ? span with { Style = ConversationStyle.Heading } : span).ToArray());
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

    internal static IReadOnlyList<ConversationSpan> Inline(string text)
    {
        var result = new List<ConversationSpan>();
        var plain = new System.Text.StringBuilder();
        var cursor = 0;
        void Flush()
        {
            if (plain.Length == 0) return;
            result.Add(new(plain.ToString(), ConversationStyle.Text));
            plain.Clear();
        }
        while (cursor < text.Length)
        {
            if (text[cursor] == '\\' && cursor + 1 < text.Length && IsEscapable(text[cursor + 1]))
            {
                plain.Append(text[cursor + 1]); cursor += 2; continue;
            }
            var code = text[cursor] == '`';
            var bold = text[cursor] == '*' && cursor + 1 < text.Length && text[cursor + 1] == '*';
            if (!code && !bold) { plain.Append(text[cursor++]); continue; }
            var length = 2;
            if (code)
            {
                length = 1;
                while (cursor + length < text.Length && text[cursor + length] == '`') length++;
            }
            var end = FindClosing(text, cursor + length, code ? '`' : '*', length, code);
            if (end < 0)
            {
                // Keep incomplete streamed markup intact, including its delimiters.
                plain.Append(text[cursor..]); cursor = text.Length; break;
            }
            Flush();
            var content = text[(cursor + length)..end];
            if (code && content.Length >= 2 && content.StartsWith(' ') && content.EndsWith(' ')
                && content.Any(c => c != ' ')) content = content[1..^1];
            result.Add(new(content, code ? ConversationStyle.InlineCode : ConversationStyle.Heading));
            cursor = end + length;
        }
        Flush();
        return result;
    }

    private static int FindClosing(string text, int start, char marker, int length, bool exactRun)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (!exactRun && text[index] == '\\' && index + 1 < text.Length && IsEscapable(text[index + 1]))
            { index++; continue; }
            if (text[index] != marker) continue;
            var end = index;
            while (end < text.Length && text[end] == marker) end++;
            if (exactRun ? end - index == length : end - index >= length) return index;
            index = end - 1;
        }
        return -1;
    }

    private static bool IsEscapable(char value) => value is >= '!' and <= '/' or >= ':' and <= '@'
        or >= '[' and <= '`' or >= '{' and <= '~';
}
