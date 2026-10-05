using System.Text.RegularExpressions;

namespace OmniCore.Cli;

/// <summary>
/// Lossless lexical highlighting for HTML and Markdown source, not rendering or
/// execution. Embedded script/style bodies use the existing bounded highlighter.
/// </summary>
internal static class MarkupSyntaxHighlighter
{
    private static readonly Regex TagName = new(@"^</?([a-zA-Z][\w:-]*)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ScriptType = new("\\btype\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex InlineMarkdown = new(@"`+[^`\n]*`+|\*\*[^*\n]+\*\*|__[^_\n]+__|!?\[[^\]\n]*\]\([^\)\n]*\)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static IReadOnlyList<IReadOnlyList<ConversationSpan>> Html(string text)
    {
        var output = new Lines();
        var position = 0;
        while (position < text.Length)
        {
            if (text.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var close = text.IndexOf("-->", position + 4, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 3;
                output.Add(text[position..end], ConversationStyle.SyntaxComment);
                position = end;
                continue;
            }
            if (text[position] != '<')
            {
                var next = text.IndexOf('<', position);
                var end = next < 0 ? text.Length : next;
                output.Add(text[position..end], ConversationStyle.Code);
                position = end;
                continue;
            }
            // Quotes can contain '>'; only an unquoted one closes a tag.
            var tagEnd = position + 1;
            var quote = '\0';
            for (; tagEnd < text.Length; tagEnd++)
            {
                var c = text[tagEnd];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '"' or '\'') quote = c;
                else if (c == '>') { tagEnd++; break; }
            }
            var tag = text[position..tagEnd];
            var name = TagName.Match(tag);
            AddTag(output, tag, name);
            position = tagEnd;
            if (!name.Success || tag.StartsWith("</", StringComparison.Ordinal) || !tag.EndsWith('>')) continue;
            var element = name.Groups[1].Value.ToLowerInvariant();
            if (element is not ("script" or "style")) continue;
            var closing = FindClosingTag(text, position, element);
            var endBody = closing < 0 ? text.Length : closing;
            var language = element == "style" ? "css" : ScriptLanguage(tag);
            output.AddHighlighted(text[position..endBody], language);
            position = endBody;
        }
        return output.Finish();
    }

    private static string? ScriptLanguage(string tag)
    {
        var match = ScriptType.Match(tag);
        var type = match.Success ? match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.Trim().ToLowerInvariant() : "";
        return type switch
        {
            "" or "module" or "text/javascript" or "application/javascript" => "javascript",
            "application/json" or "application/ld+json" => "json",
            _ => null, // Templates and other script types must not be mislabelled JS.
        };
    }

    private static int FindClosingTag(string text, int start, string element)
    {
        var marker = "</" + element;
        while (start < text.Length)
        {
            var next = text.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (next < 0) return -1;
            var boundary = next + marker.Length;
            if (boundary == text.Length || char.IsWhiteSpace(text[boundary]) || text[boundary] == '>') return next;
            start = boundary;
        }
        return -1;
    }

    private static void AddTag(Lines output, string tag, Match name)
    {
        var position = 0;
        string? attribute = null;
        while (position < tag.Length)
        {
            var start = position;
            var c = tag[position];
            if (c is '"' or '\'')
            {
                var end = tag.IndexOf(c, position + 1);
                if (end < 0) end = tag.Length;
                output.Add(tag[position..(position + 1)], ConversationStyle.SyntaxString);
                var value = tag[(position + 1)..end];
                if (attribute?.Equals("style", StringComparison.OrdinalIgnoreCase) == true)
                    output.AddHighlighted(value, "css");
                else if (attribute?.StartsWith("on", StringComparison.OrdinalIgnoreCase) == true)
                    output.AddHighlighted(value, "javascript");
                else output.Add(value, ConversationStyle.SyntaxString);
                if (end < tag.Length) output.Add(tag[end..(end + 1)], ConversationStyle.SyntaxString);
                position = Math.Min(end + 1, tag.Length);
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '-')
            {
                while (position < tag.Length && (char.IsLetterOrDigit(tag[position]) || tag[position] is '_' or '-' or ':')) position++;
                var word = tag[start..position];
                var isName = name.Success && start == name.Groups[1].Index;
                output.Add(word, isName ? ConversationStyle.SyntaxType : ConversationStyle.SyntaxVariable);
                if (!isName) attribute = word;
            }
            else
            {
                output.Add(tag[position++].ToString(), char.IsWhiteSpace(c) ? ConversationStyle.Code : ConversationStyle.SyntaxPunctuation);
            }
        }
    }

    internal static IReadOnlyList<IReadOnlyList<ConversationSpan>> Markdown(string text)
    {
        var output = new Lines();
        var source = text.Split('\n');
        string? fence = null;
        string? language = null;
        var body = new List<string>();
        for (var index = 0; index < source.Length; index++)
        {
            var line = source[index];
            var trimmed = line.TrimStart();
            var count = trimmed.Length == 0 ? 0 : trimmed.TakeWhile(c => c == trimmed[0]).Count();
            var marker = count >= 3 && trimmed[0] is '`' or '~';
            if (fence is not null)
            {
                if (marker && trimmed[0] == fence[0] && count >= fence.Length && trimmed[count..].Trim().Length == 0)
                {
                    FlushBody();
                    output.Add(line, ConversationStyle.CodeHeader);
                    fence = null;
                }
                else { body.Add(line + (index < source.Length - 1 ? "\n" : "")); continue; }
            }
            else if (marker)
            {
                fence = new string(trimmed[0], count);
                language = trimmed[count..].Trim();
                output.Add(line, ConversationStyle.CodeHeader);
            }
            else if (trimmed.StartsWith('#') || trimmed.StartsWith('>'))
                output.Add(line, trimmed.StartsWith('#') ? ConversationStyle.SyntaxType : ConversationStyle.SyntaxComment);
            else
            {
                var cursor = 0;
                foreach (Match match in InlineMarkdown.Matches(line))
                {
                    output.Add(line[cursor..match.Index], ConversationStyle.Code);
                    output.Add(match.Value, match.Value.StartsWith('`') ? ConversationStyle.SyntaxString : ConversationStyle.SyntaxKeyword);
                    cursor = match.Index + match.Length;
                }
                output.Add(line[cursor..], ConversationStyle.Code);
            }
            if (index < source.Length - 1) output.Add("\n", ConversationStyle.Code);
        }
        FlushBody();
        return output.Finish();

        void FlushBody()
        {
            if (body.Count == 0) return;
            // No recursive Markdown-in-Markdown expansion; retain nested source.
            output.AddHighlighted(string.Concat(body), Languages.Find(language)?.Name == "markdown" ? null : language);
            body.Clear();
        }
    }

    private sealed class Lines
    {
        private readonly List<IReadOnlyList<ConversationSpan>> _rows = [];
        private List<ConversationSpan> _current = [];

        public void Add(string text, ConversationStyle style)
        {
            var pieces = text.Split('\n');
            for (var index = 0; index < pieces.Length; index++)
            {
                if (index > 0) { _rows.Add(_current); _current = []; }
                if (pieces[index].Length > 0) _current.Add(new(pieces[index], style));
            }
        }

        public void AddHighlighted(string text, string? language)
        {
            var rows = SyntaxHighlighter.Highlight(text, language);
            for (var index = 0; index < rows.Count; index++)
            {
                if (index > 0) Add("\n", ConversationStyle.Code);
                foreach (var span in rows[index]) Add(span.Text, span.Style);
            }
        }

        public IReadOnlyList<IReadOnlyList<ConversationSpan>> Finish()
        {
            _rows.Add(_current);
            return _rows;
        }
    }
}
