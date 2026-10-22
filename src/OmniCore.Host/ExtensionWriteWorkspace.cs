namespace OmniCore.Host;

using System.Diagnostics;
using System.Security.Cryptography;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>
/// A disposable, private copy used by the Strong v1 extension writer. Only files known to Git
/// at the source root are copied; the original .git directory, ignored files and untracked user
/// files are never made available to the child process. Output enumeration deliberately does
/// not consult Git ignore rules.
/// </summary>
internal sealed class ExtensionWriteWorkspace : IDisposable
{
    private const int MaximumFiles = 4096;
    private const int MaximumEntries = 8192;
    private const int MaximumDepth = 64;
    private const long MaximumBytes = 64L * 1024 * 1024;
    private readonly string _privateRoot;
    private readonly Dictionary<string, string> _before;
    private readonly string[] _writePatterns;
    private readonly RedactionPolicy _redaction = new();
    private bool _disposed;

    private ExtensionWriteWorkspace(string privateRoot, Dictionary<string, string> before, string[] writePatterns)
    {
        _privateRoot = privateRoot;
        _before = before;
        _writePatterns = writePatterns;
    }

    internal string Root => _privateRoot;

    internal static ExtensionWriteWorkspace Create(string workspaceRoot, string temporaryRoot, ResourceClaims claims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        ArgumentNullException.ThrowIfNull(claims);
        var source = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot);
        var files = TrackedFiles(source);
        var readPatterns = claims.Reads.Select(NormalizePattern).Distinct(Comparer).ToArray();
        var writePatterns = claims.Writes.Select(NormalizePattern).Distinct(Comparer).ToArray();
        if (writePatterns.Length == 0) throw new InvalidOperationException("Extension writer has no declared write scope.");
        var eligible = files.Where(relative => !new RedactionPolicy().IsSecretPath(relative)
            && (MatchesAny(relative, readPatterns) || MatchesAny(relative, writePatterns))).ToArray();
        if (eligible.Length > MaximumFiles) throw new InvalidOperationException("Extension writer workspace exceeds the file limit.");

        var privateBase = Path.GetFullPath(temporaryRoot);
        Directory.CreateDirectory(privateBase);
        RejectLinks(privateBase);
        var privateRoot = Path.Combine(privateBase, "omni-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRoot);
        try
        {
            var ignored = IgnoredFiles(source, eligible);
            var redaction = new RedactionPolicy();
            var before = new Dictionary<string, string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            long total = 0;
            foreach (var relative in eligible)
            {
                if (redaction.IsSecretPath(relative) || ignored.Contains(relative)
                    || !MatchesAny(relative, readPatterns) && !MatchesAny(relative, writePatterns)) continue;
                ValidateRelativePath(relative);
                var from = Resolve(source, relative);
                if (!File.Exists(from) || HasReparseComponent(source, from)) continue;
                var info = new FileInfo(from);
                if (info.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Extension writer input file exceeds the size limit.");
                total = checked(total + info.Length);
                if (total > MaximumBytes) throw new InvalidOperationException("Extension writer workspace exceeds the byte limit.");
                var bytes = File.ReadAllBytes(from);
                var destination = Resolve(privateRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // A fresh file is always created: never use links to the source checkout.
                using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes);
                before[relative] = Hash(bytes);
            }
            return new ExtensionWriteWorkspace(privateRoot, before, writePatterns);
        }
        catch
        {
            try { Directory.Delete(privateRoot, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Returns the complete bounded output set, including new files ignored by Git.</summary>
    internal IReadOnlyList<ExtensionWorkspaceChange> CaptureChanges()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var after = EnumerateFiles(_privateRoot);
        if (after.Count > MaximumFiles) throw new InvalidOperationException("Extension writer output exceeds the file limit.");
        long total = 0;
        foreach (var path in after.Values)
        {
            var length = new FileInfo(path).Length;
            if (length > 2 * 1024 * 1024) throw new InvalidOperationException("Extension writer output file exceeds the size limit.");
            total = checked(total + length);
        }
        if (total > MaximumBytes) throw new InvalidOperationException("Extension writer output exceeds the byte limit.");

        var all = _before.Keys.Concat(after.Keys).Distinct(Comparer).OrderBy(path => path, StringComparer.Ordinal);
        var changes = new List<ExtensionWorkspaceChange>();
        foreach (var relative in all)
        {
            if (_redaction.IsSecretPath(relative) || relative.Split('/')[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Extension writer output contains a protected path.");
            _before.TryGetValue(relative, out var oldHash);
            after.TryGetValue(relative, out var fullPath);
            var bytes = fullPath is null ? null : File.ReadAllBytes(fullPath);
            var newHash = bytes is null ? null : Hash(bytes);
            if (oldHash == newHash) continue;
            if (!_writePatterns.Any(pattern => IsMatch(pattern, relative)))
                throw new InvalidOperationException("Extension writer output escaped its declared write paths.");
            changes.Add(new ExtensionWorkspaceChange(relative, oldHash, newHash, bytes));
        }
        return changes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_privateRoot, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static List<string> TrackedFiles(string root)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("--cached");
        start.ArgumentList.Add("-z");
        using var process = Process.Start(start) ?? throw new IOException("Git could not start for extension staging.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Extension writer requires a Git workspace: " + stderr.Trim());
        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Replace('\\', '/'))
            .Distinct(Comparer).ToList();
    }

    private static HashSet<string> IgnoredFiles(string root, IReadOnlyList<string> tracked)
    {
        if (tracked.Count == 0) return new HashSet<string>(Comparer);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add("check-ignore"); start.ArgumentList.Add("--no-index");
        start.ArgumentList.Add("-z"); start.ArgumentList.Add("--stdin");
        using var process = Process.Start(start) ?? throw new IOException("Git could not inspect ignored extension paths.");
        process.StandardInput.Write(string.Join('\0', tracked) + "\0");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode is not (0 or 1)) throw new InvalidOperationException("Git could not inspect ignored extension paths.");
        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Replace('\\', '/'))
            .ToHashSet(Comparer);
    }

    private static bool MatchesAny(string relative, IEnumerable<string> patterns) =>
        patterns.Any(pattern => IsMatch(pattern, relative));

    private static bool IsMatch(string pattern, string relativePath)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(relativePath, regex,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | (OperatingSystem.IsWindows() ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None));
    }

    private static string NormalizePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || Path.IsPathRooted(pattern)
            || pattern.Replace('\\', '/').Split('/').Any(part => part is ".." or "."))
            throw new InvalidDataException("Extension path pattern is invalid.");
        return pattern.Replace('\\', '/');
    }

    private static Dictionary<string, string> EnumerateFiles(string root)
    {
        var result = new Dictionary<string, string>(Comparer);
        var pending = new Stack<string>();
        var entryCount = 0;
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entryCount > MaximumEntries)
                    throw new InvalidOperationException("Extension writer output exceeds the entry limit.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Extension writer output contains a link or reparse point.");
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var depth = Path.GetRelativePath(root, entry).Count(ch => ch == Path.DirectorySeparatorChar) + 1;
                    if (depth > MaximumDepth)
                        throw new InvalidOperationException("Extension writer output exceeds the directory depth limit.");
                    pending.Push(entry);
                }
                else
                {
                    result.Add(relative, entry);
                    if (result.Count > MaximumFiles)
                        throw new InvalidOperationException("Extension writer output exceeds the file limit.");
                }
            }
        }
        return result;
    }

    private static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Git returned an unsafe extension staging path.");
    }

    private static string Resolve(string root, string relative)
    {
        ValidateRelativePath(relative);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Extension staging path escaped its root.");
        return full;
    }

    private static bool HasReparseComponent(string root, string file)
    {
        for (var current = file; !PathEquals(current, root); current = Path.GetDirectoryName(current)
            ?? throw new InvalidDataException("Extension source path is invalid."))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Extension staging directory cannot contain reparse points.");
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
