namespace OmniCore.SandboxTestProcess;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Fixture-only Git proxy used under an isolated child Host PATH. It delegates every
/// invocation to the configured real Git executable; for one exact fixture transaction
/// it persists a post-commit barrier and waits so the parent can terminate its own Host.
/// </summary>
internal static class WorktreeRetentionGitProxy
{
    private sealed record Configuration(int Version, string FixtureRoot, string FixtureTempRoot, string RepositoryRoot,
        string GitExecutable, string BarrierPath, string LogPath, string ExpectedInputSha256,
        string[] AllowedReferences, string[] ExpectedObjectIds, int ExpectedHostPid);

    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            var configurationPath = Path.Combine(AppContext.BaseDirectory, "retention-proxy.json");
            var configuration = JsonSerializer.Deserialize<Configuration>(
                await File.ReadAllTextAsync(configurationPath, new UTF8Encoding(false, true)).ConfigureAwait(false));
            if (configuration is null)
            {
                await WriteBootstrapDiagnosticAsync(["null"]).ConfigureAwait(false);
                return 91;
            }
            var invalidConfiguration = ConfigurationFailureCodes(configuration);
            if (invalidConfiguration.Length != 0)
            {
                await WriteBootstrapDiagnosticAsync(invalidConfiguration).ConfigureAwait(false);
                return 91;
            }

            using var input = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(input).ConfigureAwait(false);
            var inputBytes = input.ToArray();
            var inputHash = Convert.ToHexString(SHA256.HashData(inputBytes));
            if (!IsSafeFixtureInvocation(configuration, arguments))
            {
                await AppendDiagnosticAsync(configuration.LogPath, "rejected_path_fence", arguments, inputHash, null)
                    .ConfigureAwait(false);
                return 95;
            }

            var command = TryGetGitCommand(arguments);
            var isUpdateRef = string.Equals(command, "update-ref", StringComparison.Ordinal);
            var isTarget = IsExactRetentionUpdate(arguments)
                && PathEquals(Environment.CurrentDirectory, configuration.RepositoryRoot)
                && string.Equals(inputHash, configuration.ExpectedInputSha256, StringComparison.Ordinal)
                && MatchesAllowlistedTransaction(inputBytes, configuration);
            if (isUpdateRef && !isTarget)
            {
                await AppendDiagnosticAsync(configuration.LogPath, "rejected_update_ref_fence", arguments, inputHash, null)
                    .ConfigureAwait(false);
                return 96;
            }
            if (!isTarget && !IsAllowedReadOnlyCommand(arguments))
            {
                await AppendDiagnosticAsync(configuration.LogPath, "rejected_command_fence", arguments, inputHash, null)
                    .ConfigureAwait(false);
                return 97;
            }

            var start = new ProcessStartInfo(configuration.GitExecutable)
            {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var git = Process.Start(start);
            if (git is null) return 92;

            var stdoutTask = git.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            var stderrTask = git.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
            await git.StandardInput.BaseStream.WriteAsync(inputBytes).ConfigureAwait(false);
            git.StandardInput.Close();
            await git.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await Console.OpenStandardOutput().FlushAsync().ConfigureAwait(false);
            await Console.OpenStandardError().FlushAsync().ConfigureAwait(false);

            if (!isTarget)
                await AppendDiagnosticAsync(configuration.LogPath, "forwarded_fixture_read", arguments, inputHash, git.ExitCode)
                    .ConfigureAwait(false);

            if (!isTarget || git.ExitCode != 0) return git.ExitCode;

            var argumentsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("\0", arguments))));
            var log = JsonSerializer.Serialize(new
            {
                operation = "retention_update_ref_committed_waiting_for_host_termination",
                phase = "ready_to_wait",
                proxyPid = Environment.ProcessId,
                proxyParentPid = GetParentProcessId(),
                hostPid = configuration.ExpectedHostPid,
                gitExitCode = git.ExitCode,
                stdinSha256 = inputHash,
                argumentsSha256 = argumentsHash,
            }) + "\n";
            var logBytes = new UTF8Encoding(false).GetBytes(log);
            await using (var logStream = new FileStream(configuration.LogPath, FileMode.Append,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                await logStream.WriteAsync(logBytes).ConfigureAwait(false);
                await logStream.FlushAsync().ConfigureAwait(false);
                logStream.Flush(flushToDisk: true);
            }
            var readyLogSha256 = Convert.ToHexString(SHA256.HashData(logBytes));
            var barrier = JsonSerializer.Serialize(new
            {
                version = 1,
                proxyPid = Environment.ProcessId,
                proxyParentPid = GetParentProcessId(),
                realGitPid = git.Id,
                expectedHostPid = configuration.ExpectedHostPid,
                gitExitCode = git.ExitCode,
                stdinSha256 = inputHash,
                argumentsSha256 = argumentsHash,
                readyLogSha256,
                cwdMatchesFixtureRepository = true,
            });
            var temporaryBarrierPath = configuration.BarrierPath + "." + Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            await using (var stream = new FileStream(temporaryBarrierPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(barrier);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryBarrierPath, configuration.BarrierPath, overwrite: false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 93;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        {
            try { await Console.Error.WriteLineAsync("retention_proxy_" + exception.GetType().Name).ConfigureAwait(false); }
            catch (IOException) { }
            return 94;
        }
    }

    private static readonly string[] GitRuntimePrefix =
    [
        "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL",
        "-c", "core.autocrlf=false", "-c", "core.eol=lf", "-c", "core.safecrlf=false",
        "-c", "commit.gpgSign=false", "-c", "merge.default=text", "-c", "merge.renormalize=false",
    ];

    private static bool IsExactRetentionUpdate(string[] arguments) => arguments.Length == GitRuntimePrefix.Length + 4
        && arguments.Take(GitRuntimePrefix.Length).SequenceEqual(GitRuntimePrefix, StringComparer.Ordinal)
        && arguments[^4] == "update-ref" && arguments[^3] == "--stdin"
        && arguments[^2] == "-z" && arguments[^1] == "--no-deref";

    private static string? TryGetGitCommand(string[] arguments)
    {
        if (arguments.Length <= GitRuntimePrefix.Length
            || !arguments.Take(GitRuntimePrefix.Length).SequenceEqual(GitRuntimePrefix, StringComparer.Ordinal))
            return null;
        return arguments[GitRuntimePrefix.Length];
    }

    private static bool IsAllowedReadOnlyCommand(string[] arguments)
    {
        if (arguments.Length <= GitRuntimePrefix.Length
            || !arguments.Take(GitRuntimePrefix.Length).SequenceEqual(GitRuntimePrefix, StringComparer.Ordinal))
            return false;

        var command = arguments.AsSpan(GitRuntimePrefix.Length);
        return command[0] switch
        {
            "rev-parse" => command.Length >= 2,
            "symbolic-ref" => command.Length >= 2,
            "worktree" => command.Length == 3 && command[1] == "list" && command[2] == "--porcelain",
            _ => false,
        };
    }

    private static bool IsProxyExecutable(string gitExecutable)
    {
        var proxyExecutable = Environment.ProcessPath ?? string.Empty;
        if (PathEquals(gitExecutable, proxyExecutable)) return true;
        try
        {
            var configured = new FileInfo(gitExecutable);
            var proxy = new FileInfo(proxyExecutable);
            if ((configured.Attributes & FileAttributes.ReparsePoint) != 0
                || (proxy.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            var configuredTarget = configured.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? configured.FullName;
            var proxyTarget = proxy.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? proxy.FullName;
            return PathEquals(configuredTarget, proxyTarget);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return true; }
    }

    private static bool MatchesAllowlistedTransaction(byte[] input, Configuration configuration)
    {
        var transaction = new StringBuilder("start\0");
        foreach (var (reference, index) in configuration.AllowedReferences.Select((name, index) => (name, index)))
            transaction.Append("delete ").Append(reference).Append('\0')
                .Append(configuration.ExpectedObjectIds[index]).Append('\0');
        transaction.Append("prepare\0commit\0");
        return input.AsSpan().SequenceEqual(new UTF8Encoding(false, true).GetBytes(transaction.ToString()));
    }

    private static bool IsSafeFixtureInvocation(Configuration configuration, string[] arguments)
    {
        if (!ValidFixturePath(configuration.FixtureRoot, configuration.FixtureTempRoot, Environment.CurrentDirectory)
            || HasReparseDirectory(Environment.CurrentDirectory, configuration.FixtureRoot)) return false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is "-C" or "--git-dir" or "--work-tree")
            {
                if (++index >= arguments.Length || !ValidArgumentPath(arguments[index], configuration)) return false;
            }
            else if (argument.StartsWith("-C", StringComparison.Ordinal) && argument.Length > 2)
            {
                if (!ValidArgumentPath(argument[2..], configuration)) return false;
            }
            else if (argument.StartsWith("--git-dir=", StringComparison.Ordinal))
            {
                if (!ValidArgumentPath(argument["--git-dir=".Length..], configuration)) return false;
            }
            else if (argument.StartsWith("--work-tree=", StringComparison.Ordinal))
            {
                if (!ValidArgumentPath(argument["--work-tree=".Length..], configuration)) return false;
            }
        }

        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && !ValidArgumentPath(value, configuration)) return false;
        }
        return true;
    }

    private static bool ValidArgumentPath(string value, Configuration configuration)
    {
        try
        {
            var path = Path.IsPathFullyQualified(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(value, Environment.CurrentDirectory);
            if (!ValidFixturePath(configuration.FixtureRoot, configuration.FixtureTempRoot, path)) return false;
            if (Directory.Exists(path)) return !HasReparseDirectory(path, configuration.FixtureRoot);
            return !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static bool HasReparseDirectory(string path, string fixtureRoot)
    {
        try
        {
            var canonicalRoot = Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar);
            var current = new DirectoryInfo(Path.GetFullPath(path));
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                if (PathEquals(current.FullName, canonicalRoot)) return false;
                current = current.Parent;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return true; }
    }

    private static int GetParentProcessId()
    {
        var status = NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0,
            out var information, (uint)Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0) return -1;
        return checked((int)information.InheritedFromUniqueProcessId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal IntPtr Reserved1;
        internal IntPtr PebBaseAddress;
        internal IntPtr Reserved2_0;
        internal IntPtr Reserved2_1;
        internal IntPtr UniqueProcessId;
        internal IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        out ProcessBasicInformation processInformation, uint processInformationLength, out uint returnLength);

    private static bool ValidFixturePath(string root, string fixtureTempRoot, string path)
    {
        try
        {
            var canonicalTempRoot = Path.GetFullPath(fixtureTempRoot).TrimEnd(Path.DirectorySeparatorChar);
            var canonicalRootWithoutSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var canonicalRoot = canonicalRootWithoutSeparator + Path.DirectorySeparatorChar;
            var canonicalPath = Path.GetFullPath(path);
            const string fixturePrefix = "omni-native-command-handoff-";
            var fixtureName = Path.GetFileName(canonicalRootWithoutSeparator);
            if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(fixtureTempRoot)
                || !Path.IsPathFullyQualified(path)
                || !fixtureName.StartsWith(fixturePrefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(fixtureName[fixturePrefix.Length..], "N", out _)
                || !canonicalRootWithoutSeparator.StartsWith(canonicalTempRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)
                || HasReparseDirectory(canonicalRootWithoutSeparator, canonicalTempRoot)) return false;
            var directory = Directory.Exists(canonicalPath) ? canonicalPath : Path.GetDirectoryName(canonicalPath);
            return directory is not null && !HasReparseDirectory(directory, canonicalRootWithoutSeparator);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static bool PathEquals(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static string[] ConfigurationFailureCodes(Configuration configuration)
    {
        var failures = new List<string>();
        if (configuration.Version != 1) failures.Add("version");
        if (!ValidFixturePath(configuration.FixtureRoot, configuration.FixtureTempRoot, configuration.RepositoryRoot))
            failures.Add("repository_path");
        if (!ValidFixturePath(configuration.FixtureRoot, configuration.FixtureTempRoot, configuration.BarrierPath))
            failures.Add("barrier_path");
        if (!ValidFixturePath(configuration.FixtureRoot, configuration.FixtureTempRoot, configuration.LogPath))
            failures.Add("log_path");
        if (!ValidFixturePath(configuration.FixtureRoot, configuration.FixtureTempRoot,
                Environment.ProcessPath ?? string.Empty)) failures.Add("proxy_executable_path");
        if (!Path.IsPathFullyQualified(configuration.GitExecutable)) failures.Add("git_path_not_absolute");
        if (IsProxyExecutable(configuration.GitExecutable)) failures.Add("git_resolves_to_proxy");
        if (!File.Exists(configuration.GitExecutable)) failures.Add("git_missing");
        if (configuration.AllowedReferences.Length is < 1 or > 2) failures.Add("reference_count");
        if (configuration.AllowedReferences.Length != configuration.ExpectedObjectIds.Length) failures.Add("oid_count");
        if (configuration.AllowedReferences.Distinct(StringComparer.Ordinal).Count() != configuration.AllowedReferences.Length)
            failures.Add("duplicate_reference");
        if (configuration.AllowedReferences.Any(name => !name.StartsWith("refs/omnicore/", StringComparison.Ordinal)))
            failures.Add("reference_namespace");
        if (configuration.ExpectedObjectIds.Any(value => !IsObjectId(value))) failures.Add("oid_format");
        if (!IsSha256(configuration.ExpectedInputSha256)) failures.Add("stdin_hash_format");
        return failures.ToArray();
    }

    private static async Task WriteBootstrapDiagnosticAsync(string[] failureCodes)
    {
        var appDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var parentName = Directory.GetParent(appDirectory)?.Name ?? string.Empty;
        if (!parentName.StartsWith("omni-native-command-handoff-", StringComparison.Ordinal)) return;
        var line = JsonSerializer.Serialize(new
        {
            operation = "configuration_rejected",
            processName = Path.GetFileName(Environment.ProcessPath ?? string.Empty),
            failureCodes,
        }) + "\n";
        await File.AppendAllTextAsync(Path.Combine(appDirectory, "proxy-bootstrap-diagnostics.jsonl"),
            line, new UTF8Encoding(false)).ConfigureAwait(false);
    }

    private static Task AppendDiagnosticAsync(string path, string operation, string[] arguments,
        string stdinSha256, int? exitCode)
    {
        var command = TryGetGitCommand(arguments) ?? "unrecognized";
        var argumentsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", arguments))));
        var cwdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Environment.CurrentDirectory))));
        var record = JsonSerializer.Serialize(new { operation, command, exitCode, argumentsHash, stdinSha256, cwdHash }) + "\n";
        return File.AppendAllTextAsync(path, record, new UTF8Encoding(false));
    }
}
