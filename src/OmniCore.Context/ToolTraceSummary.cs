namespace OmniCore.Context;

/// <summary>Bounded, extractive tool preview; the immutable artifact remains authoritative.</summary>
public static class ToolTraceSummary
{
    public static string Extract(string content, int maxCharacters = 1200)
    {
        if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        if (content.Length <= maxCharacters) return content;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<string>();
        // Reserve space for the beginning and final result as well as diagnostics in
        // the middle. No inference of success from absence of an error keyword.
        var share = Math.Max(1, (maxCharacters - 100) / 3);
        Add("Start: ", lines.Take(3), share);
        Add("Evidence: ", lines.Where(IsEvidence), share);
        Add("End: ", lines.TakeLast(6), share);
        var result = "[tool output excerpts; omitted lines remain in the full artifact]\n"
            + string.Join("\n", selected);
        return result.Length <= maxCharacters ? result : result[..maxCharacters];

        void Add(string label, IEnumerable<string> candidates, int budget)
        {
            var used = 0;
            foreach (var line in candidates)
            {
                if (string.IsNullOrWhiteSpace(line) || !seen.Add(line)) continue;
                var remaining = budget - used - label.Length - 1;
                if (remaining <= 0) break;
                var excerpt = line.Length <= remaining ? line : line[..Math.Max(0, remaining - 1)] + "…";
                selected.Add(label + excerpt); used += label.Length + excerpt.Length + 1;
            }
        }
    }

    private static bool IsEvidence(string line) => new[]
    {
        "error", "failed", "failure", "exception", "warning", "exit code", "exit_code",
        "passed", "tests:", "test result", "modified", "created", "deleted", "denied",
        "falló", "fallido", "aprobado", "advertencia", "modificado",
    }.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase));
}
