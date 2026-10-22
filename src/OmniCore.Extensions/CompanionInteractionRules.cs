using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OmniCore.Extensions;

/// <summary>Local interaction signals, never a user personality classification or model input.</summary>
public sealed class CompanionInteractionRules
{
    public const string Version = "interaction-v1";
    public static readonly string[] Species = ["ox", "fox", "owl", "beaver", "cat", "capybara"];
    public static IReadOnlyDictionary<string, string> DefaultPatterns { get; } = new Dictionary<string, string>
    {
        ["ox"] = @"\b(ya te dije|de una vez|sin excusas|maldita sea|damn|bullshit|stop messing|hazlo bien|carajo|chingad\w*)\b",
        ["fox"] = @"\b(alternativa\w*|ingenio\w*|reto|otra solucion|otra forma|alternative\w*|clever|challenge|workaround)\b",
        ["owl"] = @"\b(por que|como funciona|explica\w*|aprend\w*|ensen\w*|why|how does|explain\w*|learn\w*|teach)\b",
        ["beaver"] = @"\b(constru\w*|implement\w*|continu\w*|termin\w*|complet\w*|cierra|build|finish|persist\w*)\b",
        ["cat"] = @"\b(explora\w*|decide tu|a tu criterio|autonomia|por tu cuenta|explore|your judgment|on your own|you decide)\b",
        ["capybara"] = @"\b(con calma|sin prisa|gracias|por favor|juntos|paciencia|take your time|thank\w*|please|together|no rush)\b",
    };
    private readonly IReadOnlyDictionary<string, Regex> _rules;
    public CompanionInteractionRules(IReadOnlyDictionary<string, string>? patterns = null)
    {
        var source = patterns ?? DefaultPatterns;
        if (source.Count != 6 || Species.Any(s => !source.ContainsKey(s))) throw new ArgumentException("Six explicit interaction rules are required.");
        if (source.Values.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 512))
            throw new ArgumentException("Interaction patterns must contain 1–512 characters.");
        _rules = source.ToDictionary(p => p.Key, p => new Regex(p.Value, RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(25)));
    }
    public static string Prose(string text)
    {
        // Remove source material before normalization. An unclosed fence consumes the rest.
        var lines = new List<string>(); bool fenced = false; bool xml = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```") || line.StartsWith("~~~")) { fenced = !fenced; continue; }
            if (fenced) continue;
            if (line.StartsWith('<')) { xml = !line.StartsWith("</"); continue; }
            if (xml || line.StartsWith('>') || raw.StartsWith("    ") || raw.StartsWith('\t')) continue;
            if (Regex.IsMatch(line, @"(^\[|^\d{2}:\d{2}|^\d{4}-\d{2}-\d{2}|\b(INFO|DEBUG|WARN|ERROR|TRACE)\b|[{};=]|://|^at\s)", RegexOptions.CultureInvariant)) continue;
            line = Regex.Replace(line, "`[^`]*`|\"[^\"]*\"|“[^”]*”|«[^»]*»|'[^']*'", " ");
            lines.Add(line);
        }
        var normalized = string.Join(" ", lines).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }
    public (bool Valid, string? Signal) Classify(string message)
    {
        if (message.Length > 65_536) return (false, null);
        var prose = Prose(message);
        if (Regex.Matches(prose, @"\b[a-z]{2,}\b").Count < 4 || prose.Count(char.IsLetter) < 18) return (false, null);
        try
        {
            var matches = _rules.Where(rule => rule.Value.IsMatch(prose)).Select(rule => rule.Key).ToArray();
            return (true, matches.Length == 1 ? matches[0] : null);
        }
        catch (RegexMatchTimeoutException) { return (true, null); }
    }
    public static IReadOnlyDictionary<string, double> Weights(IReadOnlyDictionary<string, int> signals, int interactions) =>
        Species.ToDictionary(s => s, s => 1d + (signals.GetValueOrDefault(s) >= 3
            ? 4d * signals.GetValueOrDefault(s) / Math.Max(1, interactions) : 0));
    public static string Draw(IReadOnlyDictionary<string, double> weights, double sample)
    {
        if (sample is < 0 or >= 1 || !double.IsFinite(sample)) throw new ArgumentOutOfRangeException(nameof(sample));
        var target = sample * Species.Sum(s => weights[s]);
        foreach (var species in Species) { target -= weights[species]; if (target < 0) return species; }
        return Species[^1];
    }
}
