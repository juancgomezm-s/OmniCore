using OmniCore.Client;

namespace OmniCore.Cli;

internal enum ConversationStyle
{
    Text, Heading, Muted, InlineCode, Code, CodeHeader,
    SyntaxComment, SyntaxKeyword, SyntaxFunction, SyntaxVariable,
    SyntaxString, SyntaxNumber, SyntaxType, SyntaxPunctuation,
    UserAccent, AssistantAccent, ToolAccent, NoticeAccent,
}
internal sealed record ConversationSpan(string Text, ConversationStyle Style,
    Terminal.Gui.Drawing.TextStyle Decoration = Terminal.Gui.Drawing.TextStyle.None);

/// <summary>Small, deterministic Markdown subset for terminal conversations; never modifies journal content.</summary>
internal static class ConversationPresentation
{
    internal static IReadOnlyList<IReadOnlyList<ConversationSpan>> RenderCards(IReadOnlyList<ConversationBlock> blocks, string locale, int width,
        bool compactExamples = false)
    {
        var result = new List<IReadOnlyList<ConversationSpan>>();
        foreach (var block in blocks)
        {
            if (result.Count > 0) result.Add(Array.Empty<ConversationSpan>());
            var accent = block.Role switch
            {
                ConversationRole.User => ConversationStyle.UserAccent,
                ConversationRole.Assistant => ConversationStyle.AssistantAccent,
                ConversationRole.Tool => ConversationStyle.ToolAccent,
                _ => ConversationStyle.NoticeAccent
            };
            var icon = block.Role switch { ConversationRole.User => "◉", ConversationRole.Assistant => "◈", ConversationRole.Tool => "⚙", _ => "·" };
            var rows = Render(new[] { block }, locale, Math.Max(1, width - 3), compactExamples);
            for (var index = 0; index < rows.Count; index++)
            {
                var row = index == 0 ? new[] { new ConversationSpan(icon + "  " + string.Concat(rows[index].Select(span => span.Text)),
                    block.Role == ConversationRole.Assistant ? ConversationStyle.Heading : ConversationStyle.Muted) } : rows[index];
                var current = new List<ConversationSpan>();
                var used = 0;
                var contentWidth = Math.Max(1, width - 3);
                var code = row.Any(span => span.Style is >= ConversationStyle.Code and <= ConversationStyle.SyntaxPunctuation);
                var listPrefix = block.Role == ConversationRole.Assistant && !code
                    ? System.Text.RegularExpressions.Regex.Match(string.Concat(row.Select(span => span.Text)), @"^\s*(?:•|☑|☐|\d+[.)])\s+").Length : 0;
                var hanging = Math.Min(listPrefix, Math.Max(0, contentWidth - 4));
                void AppendRow(IEnumerable<ConversationSpan> body) => result.Add(new[] { new ConversationSpan("┃  ", accent) }.Concat(body).ToArray());
                foreach (var span in row)
                {
                    var elements = System.Globalization.StringInfo.GetTextElementEnumerator(span.Text);
                    while (elements.MoveNext())
                    {
                        var grapheme = elements.GetTextElement();
                        var columns = Terminal.Gui.Text.StringExtensions.GetColumns(grapheme, false);
                        if (used > 0 && used + columns > contentWidth)
                        {
                            var split = code ? -1 : current.FindLastIndex(piece => piece.Text == " ");
                            if (split < hanging) split = -1;
                            if (split >= 0 && split < current.Count - 1)
                            {
                                AppendRow(current.Take(split + 1));
                                current = current.Skip(split + 1).ToList();
                                used = current.Sum(piece => Terminal.Gui.Text.StringExtensions.GetColumns(piece.Text, false));
                            }
                            else { AppendRow(current); current = new(); used = 0; }
                            if (hanging > 0)
                            {
                                current.Insert(0, new ConversationSpan(new string(' ', hanging), ConversationStyle.Text));
                                used += hanging;
                            }
                        }
                        current.Add(span with { Text = grapheme });
                        used += columns;
                    }
                }
                AppendRow(current);
            }
        }
        return result;
    }

    internal static IReadOnlyList<IReadOnlyList<ConversationSpan>> Render(IReadOnlyList<ConversationBlock> blocks, string locale, int width = 120,
        bool compactExamples = false)
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
            if (block.Role == ConversationRole.Assistant)
            {
                rows.AddRange(MarkdownRenderer.Render(compactExamples ? MarkdownExamples.Compact(block.Text) : block.Text, width));
                continue;
            }
            foreach (var source in block.Text.Replace("\r\n", "\n").Split('\n'))
                rows.Add(new[] { new ConversationSpan(source, block.Role == ConversationRole.System
                    ? ConversationStyle.Muted : ConversationStyle.Text) });
        }
        return rows;
    }
}
