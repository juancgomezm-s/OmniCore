using System.Globalization;

namespace OmniCore.Cli;

/// <summary>Pipe tables sized in terminal cells. Narrow layouts keep all values as labelled rows.</summary>
internal static class MarkdownTableRenderer
{
    internal static bool TryRender(string[] source, int start, int width,
        out IReadOnlyList<IReadOnlyList<ConversationSpan>> result, out int consumed)
    {
        result = Array.Empty<IReadOnlyList<ConversationSpan>>(); consumed = 0;
        if (start + 1 >= source.Length || !source[start].Contains('|')) return false;
        var headers = Cells(source[start]);
        var separators = Cells(source[start + 1]);
        if (headers.Length < 2 || headers.Length > 16 || separators.Length != headers.Length
            || !separators.All(cell => cell.Trim(':').Length >= 3 && cell.Trim(':').All(c => c == '-'))) return false;
        var data = new List<string[]> { headers };
        consumed = 2;
        while (start + consumed < source.Length && source[start + consumed].Contains('|'))
        {
            var row = Cells(source[start + consumed]);
            if (row.Length > headers.Length || string.IsNullOrWhiteSpace(source[start + consumed])) break;
            data.Add(Enumerable.Range(0, headers.Length).Select(index => index < row.Length ? row[index] : "").ToArray());
            consumed++;
        }
        var rows = new List<IReadOnlyList<ConversationSpan>>();
        width = Math.Max(1, width);
        if (width < headers.Length * 7 + 1)
        {
            foreach (var record in data.Skip(1))
            {
                for (var col = 0; col < headers.Length; col++)
                    rows.Add(new[] { new ConversationSpan(Plain(headers[col]) + ": ", ConversationStyle.Heading),
                        new ConversationSpan(Plain(record[col]), ConversationStyle.Text) });
                rows.Add(Array.Empty<ConversationSpan>());
            }
            if (data.Count == 1) rows.Add(headers.Select(header => new ConversationSpan(Plain(header) + "  ", ConversationStyle.Heading)).ToArray());
            result = rows; return true;
        }
        var sizes = Enumerable.Range(0, headers.Length)
            .Select(col => Math.Max(4, Math.Min(width, data.Max(record => Columns(Plain(record[col])))))).ToArray();
        var available = width - headers.Length * 3 - 1;
        while (sizes.Sum() > available)
        {
            var largest = Array.IndexOf(sizes, sizes.Max());
            sizes[largest]--;
        }
        void Rule(string left, string middle, string right) => rows.Add(new[] {
            new ConversationSpan(left + string.Join(middle, sizes.Select(size => new string('─', size + 2))) + right, ConversationStyle.Muted) });
        Rule("┌", "┬", "┐");
        for (var record = 0; record < data.Count; record++)
        {
            var wrapped = Enumerable.Range(0, headers.Length).Select(col => Wrap(Spans(data[record][col], record == 0), sizes[col])).ToArray();
            for (var line = 0; line < wrapped.Max(cell => cell.Count); line++)
            {
                var spans = new List<ConversationSpan> { new("│ ", ConversationStyle.Muted) };
                for (var col = 0; col < headers.Length; col++)
                {
                    var value = line < wrapped[col].Count ? wrapped[col][line] : Array.Empty<ConversationSpan>();
                    var used = value.Sum(span => Columns(span.Text));
                    var centered = separators[col].StartsWith(':') && separators[col].EndsWith(':');
                    var before = centered ? (sizes[col] - used) / 2 : separators[col].EndsWith(':') ? sizes[col] - used : 0;
                    spans.Add(new(new string(' ', before), ConversationStyle.Text));
                    spans.AddRange(value);
                    spans.Add(new(new string(' ', sizes[col] - used - before) + " │" + (col + 1 < headers.Length ? " " : ""), ConversationStyle.Muted));
                }
                rows.Add(spans);
            }
            if (record + 1 < data.Count) Rule("├", "┼", "┤");
        }
        Rule("└", "┴", "┘");
        result = rows; return true;
    }

    private static IReadOnlyList<ConversationSpan> Spans(string cell, bool header) =>
        header ? new[] { new ConversationSpan(Plain(cell), ConversationStyle.Heading) } : MarkdownRenderer.Inline(cell);
    private static string Plain(string cell) => string.Concat(MarkdownRenderer.Inline(cell).Select(span => span.Text));
    private static int Columns(string text) => Terminal.Gui.Text.StringExtensions.GetColumns(text, false);

    private static List<IReadOnlyList<ConversationSpan>> Wrap(IReadOnlyList<ConversationSpan> spans, int width)
    {
        var rows = new List<IReadOnlyList<ConversationSpan>>();
        var current = new List<ConversationSpan>(); var used = 0;
        foreach (var span in spans)
        {
            var elements = StringInfo.GetTextElementEnumerator(span.Text);
            while (elements.MoveNext())
            {
                var value = elements.GetTextElement(); var size = Columns(value);
                if (used + size > width && used > 0)
                {
                    var split = current.FindLastIndex(piece => piece.Text == " ");
                    if (split >= 0 && split < current.Count - 1)
                    {
                        rows.Add(current.Take(split + 1).ToArray());
                        current = current.Skip(split + 1).ToList();
                        used = current.Sum(piece => Columns(piece.Text));
                    }
                    else { rows.Add(current); current = new(); used = 0; }
                }
                current.Add(new(value, span.Style)); used += size;
            }
        }
        rows.Add(current); return rows;
    }

    private static string[] Cells(string line)
    {
        line = line.Trim();
        if (line.StartsWith('|')) line = line[1..];
        if (line.EndsWith('|') && (line.Length < 2 || line[^2] != '\\')) line = line[..^1];
        var cells = new List<string>(); var value = new System.Text.StringBuilder();
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '\\' && index + 1 < line.Length && line[index + 1] == '|') { value.Append('|'); index++; }
            else if (line[index] == '|') { cells.Add(value.ToString().Trim()); value.Clear(); }
            else value.Append(line[index]);
        }
        cells.Add(value.ToString().Trim()); return cells.ToArray();
    }
}
