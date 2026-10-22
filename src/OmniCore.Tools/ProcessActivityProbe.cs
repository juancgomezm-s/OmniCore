using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OmniCore.Domain;

namespace OmniCore.Tools;

/// <summary>Conservative executor evidence. No inference from exit status, prose or a tool name alone.</summary>
internal sealed class ProcessActivityProbe
{
    private readonly string _root;
    internal bool IsCommit { get; }
    private readonly string[] _reports;
    private readonly Dictionary<string, string?> _priorReports;
    private ProcessActivityProbe(string root, bool commit, IEnumerable<string> reports)
    {
        _root = root; IsCommit = commit;
        _reports = reports.Take(32).ToArray();
        _priorReports = _reports.ToDictionary(p => p, ReadDigest);
    }
    internal static ProcessActivityProbe Start(string executable, IReadOnlyList<string> args, string cwd, string root)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
            if (name is "cmd" or "powershell" or "pwsh" or "sh" or "bash")
            {
                var command = args.LastOrDefault() ?? "";
                // Evidence only; shell permission is never inferred or narrowed by this parser.
                if (!command.Any(c => ";&|><`$\r\n".Contains(c)))
                {
                    var tokens = Regex.Matches(command, "\"[^\"]*\"|'[^']*'|[^\\s]+").Select(m => m.Value.Trim('"', '\'')).ToArray();
                    if (tokens.Length > 0) return Start(tokens[0], tokens.Skip(1).ToArray(), cwd, root);
                }
            }
            var commit = name == "git" && args.FirstOrDefault() == "commit" && !args.Contains("--dry-run");
            if (name is "cmd" or "powershell" or "pwsh" or "sh" or "bash")
            {
                var command = args.LastOrDefault() ?? "";
                commit = Regex.IsMatch(command, @"^git\s+commit(?:\s|$)") && !command.Contains("--dry-run", StringComparison.Ordinal)
                    && !command.Any(c => ";&|><`$\r\n".Contains(c));
            }
            var reports = new List<string>();
            var test = name is "pytest" or "py.test" || name == "dotnet" && args.FirstOrDefault() == "test"
                || name == "python" && args.Take(2).SequenceEqual(new[] { "-m", "pytest" });
            if (test)
            {
                for (var i = 0; i < args.Count; i++)
                {
                    if (args[i].StartsWith("--junitxml=", StringComparison.Ordinal)) reports.Add(Path.GetFullPath(args[i][11..], cwd));
                    if (args[i] is "--junitxml" && i + 1 < args.Count) reports.Add(Path.GetFullPath(args[i + 1], cwd));
                }
                if (name == "dotnet")
                {
                    var directory = Path.Combine(cwd, "TestResults");
                    var at = args.ToList().FindIndex(a => a is "--results-directory" or "-r");
                    if (at >= 0 && at + 1 < args.Count) directory = Path.GetFullPath(args[at + 1], cwd);
                    // A logger may create a new report during execution; remember its requested path too.
                    foreach (var arg in args)
                    {
                        var match = Regex.Match(arg, @"(?:^|;)LogFileName=([^;]+)", RegexOptions.IgnoreCase);
                        if (match.Success) reports.Add(Path.GetFullPath(match.Groups[1].Value, directory));
                    }
                    if (Safe(directory, root) && Directory.Exists(directory))
                        reports.AddRange(Directory.EnumerateFiles(directory, "*.trx", SearchOption.TopDirectoryOnly).Take(32));
                }
            }
            return new(root, commit, reports.Distinct().Where(p => Safe(p, root)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(root, false, []); }
    }
    internal ActivityEvidence? Finish(int exitCode, GitActivitySnapshot? before = null, GitActivitySnapshot? after = null)
    {
        try
        {
            if (IsCommit && exitCode == 0 && after is not null)
            {
                var emptyTreeBytes = Encoding.ASCII.GetBytes("tree 0\0");
                var emptyTree = Convert.ToHexString(after.Tree.Length == 40 ? SHA1.HashData(emptyTreeBytes) : SHA256.HashData(emptyTreeBytes));
                if (before is null && after.Tree.Equals(emptyTree, StringComparison.OrdinalIgnoreCase)) return null;
                if (after.Revision != before?.Revision && after.Tree != before?.Tree
                    && (before is null ? after.Parents.Count == 0 : after.Parents.Contains(before.Revision)))
                    return new("commit", true, true, exitCode, BeforeRevision: before?.Revision, AfterRevision: after.Revision);
            }
            if (exitCode is not (0 or 1)) return null;
            foreach (var path in _reports)
            {
                if (!Safe(path, _root) || ReadDigest(path) is not { } digest || digest == _priorReports[path]) continue;
                using var stream = File.OpenRead(path);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var document = XDocument.Load(reader);
                var counters = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
                int passed = 0, failed = 0;
                if (counters is not null)
                {
                    if (!int.TryParse(counters.Attribute("passed")?.Value, out passed)
                        || !int.TryParse(counters.Attribute("failed")?.Value, out failed)) continue;
                    var outcome = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResultSummary")?.Attribute("outcome")?.Value;
                    if (outcome is not ("Completed" or "Passed" or "Failed")) continue;
                    if (counters.Attributes().Any(a => a.Name.LocalName is "aborted" or "error" or "timeout"
                        && a.Value != "0")) continue;
                }
                else
                {
                    var cases = document.Descendants().Where(e => e.Name.LocalName == "testcase").ToArray();
                    if (cases.Length == 0 || cases.Any(e => e.Elements().Any(c => c.Name.LocalName == "error"))) continue;
                    failed = cases.Count(e => e.Elements().Any(c => c.Name.LocalName == "failure"));
                    passed = cases.Count(e => !e.Elements().Any(c => c.Name.LocalName is "skipped" or "failure"));
                }
                if (passed >= 0 && failed >= 0 && (long)passed + failed > 0)
                    return new("test", true, true, exitCode, passed, failed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException) { }
        return null;
    }
    private static bool Safe(string path, string root)
    {
        if (!ProcessToolJson.IsLexicallyWithin(path, root)) return false;
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            if (current.Equals(Path.GetFullPath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
        }
        return false;
    }
    private static string? ReadDigest(string path) => File.Exists(path) && new FileInfo(path).Length <= 4 * 1024 * 1024
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
}
