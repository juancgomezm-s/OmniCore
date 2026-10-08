using System.Text.RegularExpressions;

namespace OmniCore.Cli;

/// <summary>Presentation-only folding of Markdown source examples followed by their preview.
/// The original assistant message remains available through /fuente and in the journal.</summary>
internal static class MarkdownExamples
{
    internal static string Compact(string text)
    {
        if (text.Length > MarkdownRenderer.MaxLength) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var opening = Regex.Match(lines[index], @"^\s{0,3}(`{3,}|~{3,})(.*)$");
            if (!opening.Success) { result.Add(lines[index]); continue; }
            var marker = opening.Groups[1].Value;
            var language = opening.Groups[2].Value.Trim().ToLowerInvariant();
            var close = index + 1;
            for (; close < lines.Length; close++)
            {
                var candidate = lines[close].Trim();
                if (candidate.Length >= marker.Length && candidate.All(c => c == marker[0])) break;
            }
            if (close == lines.Length)
            {
                result.AddRange(lines.Skip(index));
                break; // An unfinished streamed example must stay visible.
            }
            var first = index + 1;
            while (first < close && string.IsNullOrWhiteSpace(lines[first])) first++;
            var next = close + 1;
            while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
            // Only demo fences are foldable. A real code block (C#, HTML, etc.) is
            // never removed, including Markdown-looking text nested inside it.
            var repeated = language is "markdown" or "md"
                && first < close && next < lines.Length
                && PreviewKey(lines[first]) is { Length: > 0 } key && key == PreviewKey(lines[next]);
            if (!repeated) result.AddRange(lines.Skip(index).Take(close - index + 1));
            index = repeated ? next - 1 : close;
        }
        return string.Join('\n', result);
    }

    private static string PreviewKey(string line)
    {
        // Demo links commonly use example.com while their preview uses a real URL.
        // Match their visible label; the visible preview retains the real target.
        line = Regex.Replace(line, @"!?\[([^\]]+)\]\([^)]*\)", "$1");
        var rendered = MarkdownRenderer.Render(line);
        return Regex.Replace(string.Concat(rendered.SelectMany(row => row).Select(span => span.Text)), @"\s+", " ").Trim();
    }
}
