using System.Text.RegularExpressions;

namespace OmniCore.Cli;

/// <summary>Presentation-only previews of complete Markdown examples, folding repeated copies.
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
            var last = close - 1;
            while (last >= first && string.IsNullOrWhiteSpace(lines[last])) last--;
            var next = close + 1;
            while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
            // Only explicit Markdown wrappers become previews. Other code stays literal,
            // including Markdown-looking text nested inside a text/C#/HTML fence.
            var markdown = language is "markdown" or "md";
            // Compare the entire example. A shared heading must not discard different data.
            var repeated = markdown && first <= last && next + (last - first) < lines.Length
                && Enumerable.Range(0, last - first + 1).All(offset =>
                    PreviewKey(lines[first + offset]) == PreviewKey(lines[next + offset]));
            if (!repeated)
                result.AddRange(markdown ? lines.Skip(index + 1).Take(close - index - 1)
                    : lines.Skip(index).Take(close - index + 1));
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
