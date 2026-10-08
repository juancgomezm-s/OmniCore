using System.Net;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Terminal.Gui.Drawing;

namespace OmniCore.Cli;

/// <summary>
/// Terminal adaptation of OmniCoder's MarkdownRenderer: emit styled spans, highlight
/// a whole fenced block, and leave unknown/incomplete markup readable.
/// Keeps OmniCore's stricter matching of fence character and opening length.
/// </summary>
internal static class MarkdownRenderer
{
    internal const int MaxLength = 400_000;
    private static readonly MarkdownPipeline InlinePipeline = CreateInlinePipeline();

    private static MarkdownPipeline CreateInlinePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough);
        // Blocks (including streaming fences and terminal-sized tables) are handled below.
        // A paragraph-only pipeline still uses CommonMark's inline delimiter parser.
        builder.BlockParsers.Clear();
        builder.BlockParsers.Add(new Markdig.Parsers.ParagraphBlockParser());
        builder.InlineParsers.Insert(0, new FootnoteReferenceParser());
        return builder.Build();
    }

    private sealed class FootnoteReferenceParser : Markdig.Parsers.InlineParser
    {
        public FootnoteReferenceParser() => OpeningCharacters = new[] { '[' };
        public override bool Match(Markdig.Parsers.InlineProcessor processor, ref Markdig.Helpers.StringSlice slice)
        {
            var start = slice.Start;
            if (start + 3 > slice.End || slice.Text[start + 1] != '^') return false;
            var end = slice.Text.IndexOf(']', start + 2, Math.Min(128, slice.End - start - 1));
            if (end <= start + 2 || slice.Text.AsSpan(start + 2, end - start - 2).Contains('\n')) return false;
            processor.Inline = new LiteralInline("[" + slice.Text[(start + 2)..end] + "]");
            slice.Start = end + 1;
            return true;
        }
    }

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
                    ? span with { Style = ConversationStyle.Heading, Decoration = span.Decoration | TextStyle.Bold } : span).ToArray());
                continue;
            }
            if (Regex.IsMatch(line, @"^(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$"))
            {
                rows.Add(new[] { new ConversationSpan(new string('─', Math.Clamp(width, 1, 48)), ConversationStyle.Muted) });
                continue;
            }
            var indent = raw[..(raw.Length - line.Length)].Replace("\t", "    ");
            var task = Regex.Match(line, @"^[-*+] \[([ xX])\]\s+(.*)$");
            var bullet = Regex.Match(line, @"^[-*+]\s+(.*)$");
            var note = Regex.Match(line, @"^\[\^([^\]]+)\]:\s*(.*)$");
            if (task.Success)
                rows.Add(new[] { new ConversationSpan(indent + (task.Groups[1].Value == " " ? "☐ " : "☑ "), ConversationStyle.Muted) }
                    .Concat(Inline(task.Groups[2].Value)).ToArray());
            else if (bullet.Success)
                rows.Add(new[] { new ConversationSpan(indent + "• ", ConversationStyle.Muted) }.Concat(Inline(bullet.Groups[1].Value)).ToArray());
            else if (note.Success)
                rows.Add(new[] { new ConversationSpan("[" + note.Groups[1].Value + "] ", ConversationStyle.Heading) }
                    .Concat(Inline(note.Groups[2].Value)).ToArray());
            else if (line.StartsWith(">", StringComparison.Ordinal))
                rows.Add(new[] { new ConversationSpan(indent + "│ ", ConversationStyle.Muted) }.Concat(Inline(line[1..].TrimStart())).ToArray());
            else rows.Add(new[] { new ConversationSpan(indent, ConversationStyle.Text) }.Where(span => span.Text.Length > 0)
                .Concat(Inline(line)).ToArray());
        }
        AppendCode(); // An incomplete fence must remain visible during streaming.
        return rows.SelectMany(SplitLineBreaks).ToArray();

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

    private static IEnumerable<IReadOnlyList<ConversationSpan>> SplitLineBreaks(IReadOnlyList<ConversationSpan> spans)
    {
        var line = new List<ConversationSpan>();
        foreach (var span in spans)
        {
            var parts = span.Text.Split('\n');
            for (var index = 0; index < parts.Length; index++)
            {
                if (index > 0) { yield return line; line = new(); }
                line.Add(span with { Text = parts[index] });
            }
        }
        yield return line;
    }

    internal static IReadOnlyList<ConversationSpan> Inline(string text)
    {
        var result = new List<ConversationSpan>();
        var html = new List<(string Tag, TextStyle Decoration, ConversationStyle Style, string? Title)>();
        void Add(string value, ConversationStyle style, TextStyle decoration)
        {
            if (value.Length == 0) return;
            if (result.LastOrDefault() is { } last && last.Style == style && last.Decoration == decoration)
                result[^1] = last with { Text = last.Text + value };
            else result.Add(new(value, style, decoration));
        }
        void Visit(ContainerInline container, TextStyle decoration = TextStyle.None, ConversationStyle style = ConversationStyle.Text)
        {
            foreach (var node in container)
            {
                var effective = decoration;
                var color = style;
                foreach (var tag in html)
                {
                    effective |= tag.Decoration;
                    if (tag.Style != ConversationStyle.Text) color = tag.Style;
                }
                switch (node)
                {
                    case LiteralInline literal:
                        Add(literal.Content.ToString(), color, effective);
                        break;
                    case HtmlEntityInline entity: Add(entity.Transcoded.ToString(), color, effective); break;
                    case CodeInline code: Add(code.Content, ConversationStyle.InlineCode, decoration); break;
                    case EmphasisInline emphasis:
                        var emphasisStyle = emphasis.DelimiterChar == '~' ? TextStyle.Strikethrough
                            : emphasis.DelimiterCount == 2 ? TextStyle.Bold : TextStyle.Italic;
                        Visit(emphasis, decoration | emphasisStyle, style);
                        break;
                    case LinkInline link:
                        if (link.IsImage) Add("Imagen: ", ConversationStyle.Muted, decoration);
                        Visit(link, decoration | TextStyle.Underline, ConversationStyle.Heading);
                        var label = string.Concat(result.TakeLast(1).Select(span => span.Text));
                        if (!string.IsNullOrEmpty(link.Url) && label != link.Url)
                            Add(" (" + link.Url + ")", ConversationStyle.Muted, decoration);
                        break;
                    case AutolinkInline auto: Add(auto.Url, ConversationStyle.Heading, effective | TextStyle.Underline); break;
                    case HtmlInline tag: RenderTag(tag.Tag, effective, color); break;
                    case LineBreakInline: Add("\n", color, effective); break;
                    case ContainerInline nested: Visit(nested, decoration, style); break;
                    default:
                        if (node.Span.Start >= 0 && node.Span.End < text.Length)
                            Add(text[node.Span.Start..(node.Span.End + 1)], color, effective);
                        break;
                }
            }
        }
        void RenderTag(string raw, TextStyle decoration, ConversationStyle style)
        {
            var match = Regex.Match(raw, @"^<\s*(/?)\s*([a-zA-Z0-9]+)\b[^>]*>$");
            if (!match.Success) { Add(raw, style, decoration); return; }
            var name = match.Groups[2].Value.ToLowerInvariant();
            if (name == "br") { Add("\n", style, decoration); return; }
            if (name is not ("b" or "strong" or "i" or "em" or "s" or "del" or "strike" or "u" or "kbd" or "code" or "mark" or "abbr" or "span"))
            { Add(raw, style, decoration); return; }
            if (match.Groups[1].Value == "/")
            {
                var index = html.FindLastIndex(item => item.Tag == name);
                if (index < 0) { Add(raw, style, decoration); return; }
                var title = html[index].Title;
                html.RemoveRange(index, html.Count - index);
                if (!string.IsNullOrEmpty(title)) Add(" (" + title + ")", ConversationStyle.Muted, TextStyle.None);
                return;
            }
            var titleMatch = Regex.Match(raw, "\\btitle\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase);
            html.Add((name, name switch
            {
                "b" or "strong" => TextStyle.Bold,
                "i" or "em" => TextStyle.Italic,
                "del" or "s" or "strike" => TextStyle.Strikethrough,
                "u" or "abbr" => TextStyle.Underline,
                "mark" => TextStyle.Reverse,
                _ => TextStyle.None
            }, name is "kbd" or "code" ? ConversationStyle.InlineCode : ConversationStyle.Text,
                name == "abbr" && titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups[2].Value) : null));
        }
        foreach (var paragraph in Markdown.Parse(text, InlinePipeline).OfType<ParagraphBlock>())
            if (paragraph.Inline is { } inline) Visit(inline);
        if (result.Count == 0) result.Add(new(text, ConversationStyle.Text));
        return result;
    }
}
