using OmniCore.Client;

namespace OmniCore.Cli;

internal enum ConversationStyle { Text, Heading, Muted, InlineCode, Code, CodeHeader }
internal sealed record ConversationSpan(string Text, ConversationStyle Style);

/// <summary>Small, deterministic Markdown subset for terminal conversations; never modifies journal content.</summary>
internal static class ConversationPresentation
{
    internal static IReadOnlyList<IReadOnlyList<ConversationSpan>> Render(IReadOnlyList<ConversationBlock> blocks, string locale)
    {
        var rows = new List<IReadOnlyList<ConversationSpan>>();
        foreach (var block in blocks)
        {
            if (rows.Count > 0) rows.Add(Array.Empty<ConversationSpan>());
            var label = block.Role switch
            {
                ConversationRole.User => locale == "en" ? "YOU" : "TÚ",
                ConversationRole.Assistant => "OMNICORE",
                ConversationRole.Tool => "▪ " + (block.ToolName ?? "tool"),
                ConversationRole.Interaction => "!",
                _ => "○",
            };
            rows.Add(new[] { new ConversationSpan(label, block.Role == ConversationRole.Assistant
                ? ConversationStyle.Heading : ConversationStyle.Muted) });
            string? fence = null;
            foreach (var source in block.Text.Replace("\r\n", "\n").Split('\n'))
            {
                var raw = source;
                var line = raw.TrimStart();
                if (block.Role == ConversationRole.Assistant && (line.StartsWith("```") || line.StartsWith("~~~")))
                {
                    var marker = line[0];
                    var count = line.TakeWhile(c => c == marker).Count();
                    var suffix = line[count..].Trim();
                    if (fence is null)
                    {
                        fence = new string(marker, count);
                        rows.Add(new[] { new ConversationSpan("┌─ </> " + (suffix.Length == 0 ? "code" : suffix), ConversationStyle.CodeHeader) });
                        continue;
                    }
                    if (marker == fence[0] && count >= fence.Length && suffix.Length == 0)
                    {
                        fence = null;
                        rows.Add(new[] { new ConversationSpan("└─", ConversationStyle.CodeHeader) });
                        continue;
                    }
                }
                if (fence is not null)
                {
                    rows.Add(new[] { new ConversationSpan("│ " + raw, ConversationStyle.Code) });
                    continue;
                }
                if (block.Role != ConversationRole.Assistant)
                {
                    rows.Add(new[] { new ConversationSpan(raw, block.Role == ConversationRole.System ? ConversationStyle.Muted : ConversationStyle.Text) });
                    continue;
                }
                var heading = line.TakeWhile(c => c == '#').Count();
                if (heading is > 0 and <= 6 && line.Length > heading && line[heading] == ' ')
                {
                    rows.Add(new[] { new ConversationSpan(line[(heading + 1)..], ConversationStyle.Heading) });
                    continue;
                }
                if (line.StartsWith("- ") || line.StartsWith("* ")) raw = "• " + line[2..];
                rows.Add(Inline(raw));
            }
        }
        return rows;
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
