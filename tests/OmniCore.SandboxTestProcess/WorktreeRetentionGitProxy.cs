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
        string[] AllowedReferences, string[] ExpectedObjectIds, int ExpectedHostPid,
        string? Mode = null, string? DataRoot = null, string? WorktreePath = null,
        string? DispositionOperation = null, string? ExpectedLaneHead = null, string? ExpectedLaneTree = null,
        string? ExpectedRetainedReference = null, string? StatePath = null,
        string? SnapshotReference = null, string[]? AllowedGitPaths = null, string? RepositoryObjectsPath = null);

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
            if (string.Equals(configuration.Mode, "disposition", StringComparison.Ordinal))
                return await RunDispositionInvocationAsync(configuration, arguments, inputBytes, inputHash)
                    .ConfigureAwait(false);
            if (configuration.Mode is not (null or "retention"))
            {
                await AppendDiagnosticAsync(configuration.LogPath, "rejected_mode", arguments, inputHash, null)
                    .ConfigureAwait(false);
                return 91;
            }
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

        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                     "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
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
        if (string.Equals(configuration.Mode, "disposition", StringComparison.Ordinal))
        {
            if (configuration.DataRoot is null || !ValidFixturePath(configuration.FixtureRoot,
                    configuration.FixtureTempRoot, configuration.DataRoot)) failures.Add("data_root");
            if (configuration.WorktreePath is null || !ValidFixturePath(configuration.FixtureRoot,
                    configuration.FixtureTempRoot, configuration.WorktreePath)) failures.Add("worktree_path");
            if (configuration.RepositoryObjectsPath is null || !ValidFixturePath(configuration.FixtureRoot,
                    configuration.FixtureTempRoot, configuration.RepositoryObjectsPath)) failures.Add("repository_objects_path");
            if (configuration.DispositionOperation is not ("cleanup" or "discard")) failures.Add("disposition_operation");
            if (!IsObjectId(configuration.ExpectedLaneHead ?? string.Empty)) failures.Add("lane_head_format");
            if (!IsObjectId(configuration.ExpectedLaneTree ?? string.Empty)) failures.Add("lane_tree_format");
            if (configuration.SnapshotReference is not { } snapshotRef
                || !System.Text.RegularExpressions.Regex.IsMatch(snapshotRef,
                    "^refs/omnicore/snapshots/[0-9a-f]{16}/[0-9a-f]{16}/[0-9a-f]{32}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                failures.Add("snapshot_reference");
            if (configuration.AllowedGitPaths is null
                || configuration.AllowedGitPaths.Any(path => !IsAllowedRelativeFixturePath(path)))
                failures.Add("git_path_allowlist");
            if (configuration.DispositionOperation == "discard")
            {
                if (configuration.ExpectedRetainedReference is not { } retainedRef
                    || !System.Text.RegularExpressions.Regex.IsMatch(retainedRef,
                        "^refs/omnicore/retained-lanes/[0-9a-f]{16}/[0-9a-f]{16}/[0-9a-f]{32}$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    failures.Add("retained_reference");
                if (configuration.StatePath is null || !ValidFixturePath(configuration.FixtureRoot,
                        configuration.FixtureTempRoot, configuration.StatePath)) failures.Add("state_path");
                if (configuration.StatePath is not null
                    && !PathEquals(Path.GetDirectoryName(configuration.StatePath)!, Path.GetDirectoryName(configuration.LogPath)!))
                    failures.Add("state_directory");
            }
            else if (configuration.ExpectedRetainedReference is not null) failures.Add("unexpected_retained_reference");
        }
        else
        {
            if (configuration.AllowedReferences.Length is < 1 or > 2) failures.Add("reference_count");
            if (configuration.AllowedReferences.Length != configuration.ExpectedObjectIds.Length) failures.Add("oid_count");
            if (configuration.AllowedReferences.Distinct(StringComparer.Ordinal).Count() != configuration.AllowedReferences.Length)
                failures.Add("duplicate_reference");
            if (configuration.AllowedReferences.Any(name => !name.StartsWith("refs/omnicore/", StringComparison.Ordinal)))
                failures.Add("reference_namespace");
            if (configuration.ExpectedObjectIds.Any(value => !IsObjectId(value))) failures.Add("oid_format");
            if (!IsSha256(configuration.ExpectedInputSha256)) failures.Add("stdin_hash_format");
        }
        return failures.ToArray();
    }

    private static async Task<int> RunDispositionInvocationAsync(Configuration configuration, string[] arguments,
        byte[] inputBytes, string inputHash)
    {
        var environmentFailure = "configuration";
        if (configuration.DataRoot is null || configuration.WorktreePath is null
            || configuration.DispositionOperation is not ("cleanup" or "discard")
            || !TryDispositionTempEnvironment(configuration, out environmentFailure))
        {
            await AppendDispositionDiagnosticAsync(configuration.LogPath, "rejected_disposition_environment:" + environmentFailure,
                arguments, inputHash, null, configuration).ConfigureAwait(false);
            return 95;
        }

        var command = TryGetGitCommand(arguments);
        var isRemoval = IsExactDispositionRemoval(arguments, configuration)
            && PathEquals(Environment.CurrentDirectory, configuration.RepositoryRoot)
            && inputBytes.Length == 0;
        var isRetainedPin = configuration.DispositionOperation == "discard"
            && IsExactRetainedPin(arguments, configuration)
            && PathEquals(Environment.CurrentDirectory, configuration.RepositoryRoot)
            && inputBytes.Length == 0;
        var allowedPrivateMutation = IsAllowedDispositionPrivateMutation(arguments, configuration);
        var allowedRead = IsAllowedDispositionRead(arguments, configuration);

        if (command == "update-ref" && !isRetainedPin)
        {
            await AppendDispositionDiagnosticAsync(configuration.LogPath, "rejected_disposition_update_ref", arguments, inputHash, null, configuration)
                .ConfigureAwait(false);
            return 96;
        }
        if (command == "worktree" && !isRemoval && !allowedRead)
        {
            await AppendDispositionDiagnosticAsync(configuration.LogPath, "rejected_disposition_worktree_mutation", arguments, inputHash, null, configuration)
                .ConfigureAwait(false);
            return 97;
        }
        if (!isRemoval && !isRetainedPin && !allowedPrivateMutation && !allowedRead)
        {
            await AppendDispositionDiagnosticAsync(configuration.LogPath, "rejected_disposition_command", arguments, inputHash, null, configuration)
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
        CopySafeDispositionEnvironment(start, configuration);
        using var git = Process.Start(start);
        if (git is null) return 92;

        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var stdoutTask = git.StandardOutput.BaseStream.CopyToAsync(output);
        var stderrTask = git.StandardError.BaseStream.CopyToAsync(error);
        await git.StandardInput.BaseStream.WriteAsync(inputBytes).ConfigureAwait(false);
        git.StandardInput.Close();
        await git.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var stdoutBytes = output.ToArray();
        var stderrBytes = error.ToArray();
        await Console.OpenStandardOutput().WriteAsync(stdoutBytes).ConfigureAwait(false);
        await Console.OpenStandardError().WriteAsync(stderrBytes).ConfigureAwait(false);
        await Console.OpenStandardOutput().FlushAsync().ConfigureAwait(false);
        await Console.OpenStandardError().FlushAsync().ConfigureAwait(false);

        if (allowedPrivateMutation && git.ExitCode == 0
            && TryGetGitCommand(arguments) == "write-tree"
            && !string.Equals(Encoding.UTF8.GetString(stdoutBytes).Trim(), configuration.ExpectedLaneTree,
                StringComparison.OrdinalIgnoreCase))
        {
            await AppendDiagnosticAsync(configuration.LogPath, "rejected_disposition_tree_mismatch", arguments, inputHash,
                git.ExitCode).ConfigureAwait(false);
            return 98;
        }

        if (allowedPrivateMutation && git.ExitCode == 0 && IsExactDispositionCommitTree(arguments, configuration)
            && configuration.DispositionOperation == "discard"
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY")))
        {
            var commit = Encoding.UTF8.GetString(stdoutBytes).Trim();
            if (!IsObjectId(commit) || configuration.StatePath is null
                || !PathEquals(Path.GetDirectoryName(configuration.StatePath)!, Path.GetDirectoryName(configuration.LogPath)!))
            {
                await AppendDiagnosticAsync(configuration.LogPath, "rejected_disposition_commit_result", arguments,
                    inputHash, git.ExitCode).ConfigureAwait(false);
                return 98;
            }
            var state = JsonSerializer.Serialize(new { version = 1, commit, tree = configuration.ExpectedLaneTree,
                parent = configuration.ExpectedLaneHead });
            if (File.Exists(configuration.StatePath))
            {
                using var existing = JsonDocument.Parse(File.ReadAllBytes(configuration.StatePath));
                var existingRoot = existing.RootElement;
                if (existingRoot.GetProperty("commit").GetString() is not { } existingCommit || !IsObjectId(existingCommit)
                    || !string.Equals(existingRoot.GetProperty("tree").GetString(), configuration.ExpectedLaneTree, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existingRoot.GetProperty("parent").GetString(), configuration.ExpectedLaneHead, StringComparison.OrdinalIgnoreCase))
                {
                    await AppendDiagnosticAsync(configuration.LogPath, "rejected_disposition_commit_state_mismatch", arguments,
                        inputHash, git.ExitCode).ConfigureAwait(false);
                    return 98;
                }
            }
            else
            {
                var tempState = configuration.StatePath + "." + Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
                await using (var stateStream = new FileStream(tempState, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    var stateBytes = new UTF8Encoding(false).GetBytes(state);
                    await stateStream.WriteAsync(stateBytes).ConfigureAwait(false);
                    await stateStream.FlushAsync().ConfigureAwait(false);
                    stateStream.Flush(flushToDisk: true);
                }
                File.Move(tempState, configuration.StatePath, overwrite: false);
            }
        }
        if (isRetainedPin && git.ExitCode == 0 && configuration.StatePath is not null)
        {
            using var state = JsonDocument.Parse(File.ReadAllBytes(configuration.StatePath));
            var current = state.RootElement;
            var pinnedState = JsonSerializer.Serialize(new
            {
                version = 1,
                commit = current.GetProperty("commit").GetString(),
                tree = current.GetProperty("tree").GetString(),
                parent = current.GetProperty("parent").GetString(),
                pinned = true,
            });
            await File.WriteAllTextAsync(configuration.StatePath, pinnedState, new UTF8Encoding(false)).ConfigureAwait(false);
        }

        var operationName = isRemoval ? "disposition_worktree_removed_waiting_for_host_termination"
            : isRetainedPin ? "disposition_retained_ref_pinned" : "forwarded_disposition_git_fixture";
        await AppendDispositionDiagnosticAsync(configuration.LogPath, operationName, arguments, inputHash, git.ExitCode,
            configuration).ConfigureAwait(false);
        if (!isRemoval || git.ExitCode != 0) return git.ExitCode;

        var argumentsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", arguments))));
        var readyLog = JsonSerializer.Serialize(new
        {
            operation = operationName,
            phase = "ready_to_wait",
            proxyPid = Environment.ProcessId,
            proxyParentPid = GetParentProcessId(),
            hostPid = configuration.ExpectedHostPid,
            gitExitCode = git.ExitCode,
            operationKind = configuration.DispositionOperation,
            stdinSha256 = inputHash,
            argumentsSha256 = argumentsHash,
            laneHead = configuration.ExpectedLaneHead,
            laneTree = configuration.ExpectedLaneTree,
        }) + "\n";
        var readyLogBytes = new UTF8Encoding(false).GetBytes(readyLog);
        await using (var logStream = new FileStream(configuration.LogPath, FileMode.Append,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            await logStream.WriteAsync(readyLogBytes).ConfigureAwait(false);
            await logStream.FlushAsync().ConfigureAwait(false);
            logStream.Flush(flushToDisk: true);
        }
        var readyLogSha256 = Convert.ToHexString(SHA256.HashData(readyLogBytes));
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
            cwdMatchesFixtureRepository = PathEquals(Environment.CurrentDirectory, configuration.RepositoryRoot),
            operationKind = configuration.DispositionOperation,
            worktreePathSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration.WorktreePath))),
            laneHead = configuration.ExpectedLaneHead,
            laneTree = configuration.ExpectedLaneTree,
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

    private static bool TryDispositionTempEnvironment(Configuration configuration, out string failure)
    {
        failure = "temp_missing";
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Environment.GetEnvironmentVariable("TMP");
        if (string.IsNullOrWhiteSpace(temp) || !Path.IsPathFullyQualified(temp)) return false;
        var fixtureRoot = Path.GetFullPath(configuration.FixtureRoot);
        var dataRoot = Path.GetFullPath(configuration.DataRoot!);
        var tempRoot = Path.GetFullPath(configuration.FixtureTempRoot);
        var fullTemp = Path.GetFullPath(temp);
        if (!IsPhysicalDescendant(fullTemp, fixtureRoot) || !IsPhysicalDescendant(fixtureRoot, tempRoot)
            || !Directory.Exists(fullTemp) || !IsSafePhysicalPath(fullTemp, fixtureRoot))
        { failure = "temp_outside_fixture_or_reparse"; return false; }

        foreach (var name in new[] { "GIT_DIR", "GIT_COMMON_DIR", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) { failure = "unexpected_git_environment"; return false; }
        }
        var objectDirectory = Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(objectDirectory) && !IsPrivateDispositionObjectEnvironment(configuration))
        { failure = "object_directory_outside_private_alternates"; return false; }
        var index = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        if (!string.IsNullOrWhiteSpace(index))
        {
            var fullIndex = Path.GetFullPath(index);
            var parent = Path.GetDirectoryName(fullIndex);
            if (parent is null || !IsPhysicalDescendant(parent, dataRoot)
                || !IsOwnedPrivateTempPath(parent)
                || !IsSafePhysicalPath(parent, dataRoot))
            { failure = "index_outside_private_temp"; return false; }
            if (!string.IsNullOrWhiteSpace(objectDirectory)
                && !PathEquals(parent, Path.GetDirectoryName(Path.GetFullPath(objectDirectory))!))
            { failure = "index_object_temp_mismatch"; return false; }
        }
        var workTree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");
        if (!string.IsNullOrWhiteSpace(workTree))
        {
            var fullWorkTree = Path.GetFullPath(workTree);
            var parent = Path.GetDirectoryName(fullWorkTree);
            if (parent is null || !IsPhysicalDescendant(fullWorkTree, dataRoot)
                || !IsOwnedPrivateTempPath(parent)
                || !Directory.Exists(fullWorkTree) || !IsSafePhysicalPath(fullWorkTree, dataRoot)
                || string.IsNullOrWhiteSpace(index)
                || !PathEquals(parent, Path.GetDirectoryName(Path.GetFullPath(index))!))
            { failure = "materialized_worktree_outside_private_temp"; return false; }
        }
        failure = string.Empty;
        return true;
    }

    private static bool IsPhysicalDescendant(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static bool IsSafePhysicalPath(string path, string root)
    {
        if (!IsPhysicalDescendant(path, root)) return false;
        try
        {
            var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var current = new DirectoryInfo(Path.GetFullPath(path));
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (PathEquals(current.FullName, canonicalRoot)) return true;
                current = current.Parent;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
        return false;
    }

    private static bool IsOwnedPrivateTempPath(string path)
    {
        var name = Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
        return name.StartsWith(".m7-tmp-", StringComparison.Ordinal)
            && Guid.TryParseExact(name[".m7-tmp-".Length..], "N", out _);
    }

    private static bool IsAllowedRelativeFixturePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value)) return false;
        var normalized = value.Replace('\\', '/');
        return normalized.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..")
            && segment.IndexOfAny([':', '\0']) < 0);
    }

    private static bool IsExactDispositionRemoval(string[] arguments, Configuration configuration)
    {
        var suffix = arguments.Skip(GitRuntimePrefix.Length).ToArray();
        if (configuration.WorktreePath is null) return false;
        var expected = configuration.DispositionOperation == "discard"
            ? new[] { "worktree", "remove", "--force", "--", Path.GetFullPath(configuration.WorktreePath) }
            : new[] { "worktree", "remove", "--", Path.GetFullPath(configuration.WorktreePath) };
        return suffix.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool IsExactRetainedPin(string[] arguments, Configuration configuration)
    {
        if (configuration.ExpectedRetainedReference is null || configuration.StatePath is null) return false;
        var suffix = arguments.Skip(GitRuntimePrefix.Length).ToArray();
        var expectedPrefix = new[] { "update-ref", configuration.ExpectedRetainedReference };
        if (suffix.Length != 4 || !suffix.Take(2).SequenceEqual(expectedPrefix, StringComparer.Ordinal)
            || suffix[3] != new string('0', suffix[2].Length) || !IsObjectId(suffix[2])) return false;
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllBytes(configuration.StatePath));
            var root = state.RootElement;
            var pinnedAlready = root.TryGetProperty("pinned", out var pinned) && pinned.ValueKind == JsonValueKind.True;
            return root.GetProperty("version").GetInt32() == 1
                && string.Equals(root.GetProperty("commit").GetString(), suffix[2], StringComparison.OrdinalIgnoreCase)
                && string.Equals(root.GetProperty("tree").GetString(), configuration.ExpectedLaneTree, StringComparison.OrdinalIgnoreCase)
                && string.Equals(root.GetProperty("parent").GetString(), configuration.ExpectedLaneHead, StringComparison.OrdinalIgnoreCase)
                && !pinnedAlready;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        { return false; }
    }

    private static bool IsExactDispositionCommitTree(string[] arguments, Configuration configuration)
    {
        var suffix = arguments.Skip(GitRuntimePrefix.Length).ToArray();
        var commonExact = suffix.Length == 10
            && suffix.SequenceEqual(["-c", "user.name=OmniCore preview", "-c", "user.email=omnicore-preview@localhost",
                "commit-tree", configuration.ExpectedLaneTree!, "-p", configuration.ExpectedLaneHead!, "-m",
                "OmniCore temporary integration preview"], StringComparer.Ordinal)
            && PathEquals(Environment.CurrentDirectory, configuration.WorktreePath!)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIT_INDEX_FILE"));
        if (!commonExact) return false;
        var objectDirectory = Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY");
        return string.IsNullOrWhiteSpace(objectDirectory)
            ? configuration.DispositionOperation == "discard" && !string.IsNullOrWhiteSpace(configuration.StatePath)
            : IsPrivateDispositionObjectEnvironment(configuration);
    }

    private static bool IsAllowedDispositionPrivateMutation(string[] arguments, Configuration configuration)
    {
        var suffix = arguments.Skip(GitRuntimePrefix.Length).ToArray();
        if (suffix.Length == 2 && suffix[0] == "read-tree"
            && (suffix[1] == configuration.ExpectedLaneHead || suffix[1] == configuration.ExpectedLaneTree))
            return IsPrivateIndexEnvironment(configuration)
                && (IsDispositionVerificationIndex(configuration)
                    ? IsPrivateDispositionObjectEnvironment(configuration)
                    : configuration.DispositionOperation == "discard"
                        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY")))
                && (PathEquals(Environment.CurrentDirectory, configuration.WorktreePath!)
                    || PathEquals(Environment.CurrentDirectory, configuration.RepositoryRoot)
                        && suffix[1] == configuration.ExpectedLaneTree && IsPrivateMaterializationEnvironment(configuration));
        if (suffix.SequenceEqual(["add", "-A", "--", "."], StringComparer.Ordinal))
            return IsPrivateIndexEnvironment(configuration)
                && (IsDispositionVerificationIndex(configuration)
                    ? IsPrivateDispositionObjectEnvironment(configuration)
                    : configuration.DispositionOperation == "discard"
                        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY")))
                && PathEquals(Environment.CurrentDirectory, configuration.WorktreePath!);
        if (suffix.SequenceEqual(["write-tree"], StringComparer.Ordinal))
            return IsPrivateIndexEnvironment(configuration) && PathEquals(Environment.CurrentDirectory, configuration.WorktreePath!)
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIT_WORK_TREE"))
                && (IsDispositionVerificationIndex(configuration)
                    ? IsPrivateDispositionObjectEnvironment(configuration)
                    : configuration.DispositionOperation == "discard"
                        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY")));
        if (IsExactDispositionCommitTree(arguments, configuration)) return true;
        if (suffix.SequenceEqual(["checkout-index", "--all", "--force"], StringComparer.Ordinal))
            return IsPrivateIndexEnvironment(configuration) && IsPrivateMaterializationEnvironment(configuration);
        return false;
    }

    private static bool IsAllowedDispositionRead(string[] arguments, Configuration configuration)
    {
        if (!arguments.Take(GitRuntimePrefix.Length).SequenceEqual(GitRuntimePrefix, StringComparer.Ordinal)
            || !PathEquals(Environment.CurrentDirectory, configuration.RepositoryRoot)
                && !PathEquals(Environment.CurrentDirectory, configuration.WorktreePath!)) return false;
        var suffix = arguments.Skip(GitRuntimePrefix.Length).ToArray();
        if (suffix.Length >= 2 && suffix[0] == "rev-parse")
            return suffix.Skip(1).All(argument => argument is "--show-toplevel" or "--git-common-dir" or "--git-dir"
                or "--path-format=absolute" or "--verify" or "--git-path" or "index" or "objects"
                or "HEAD^{commit}" or "HEAD^{tree}"
                || argument == configuration.ExpectedLaneHead || argument == configuration.ExpectedLaneTree
                || argument == configuration.ExpectedLaneHead + "^{tree}"
                || argument == configuration.ExpectedLaneTree + "^{tree}"
                || argument == configuration.SnapshotReference || argument == configuration.SnapshotReference + "^{commit}"
                || argument == configuration.SnapshotReference + "^{tree}"
                || argument == configuration.ExpectedRetainedReference
                || argument == configuration.ExpectedRetainedReference + "^{commit}"
                || argument == configuration.ExpectedRetainedReference + "^{tree}"
                || argument == configuration.ExpectedRetainedReference + "^1");
        if (suffix.Length >= 2 && suffix[0] == "symbolic-ref")
            return suffix.Skip(1).All(argument => argument is "--quiet" or "--short"
                || argument == "HEAD" || argument == configuration.SnapshotReference
                || argument == configuration.ExpectedRetainedReference);
        if (suffix.SequenceEqual(["worktree", "list", "--porcelain"], StringComparer.Ordinal)) return true;
        if (suffix.SequenceEqual(["status", "--porcelain", "--untracked-files=all", "--ignored=matching"], StringComparer.Ordinal)
            || suffix.SequenceEqual(["status", "--porcelain", "--untracked-files", "--ignored=matching"], StringComparer.Ordinal)) return true;
        if (suffix.SequenceEqual(["ls-files", "--unmerged", "-z"], StringComparer.Ordinal)
            || suffix.SequenceEqual(["ls-files", "--stage", "-z"], StringComparer.Ordinal)
            || suffix.SequenceEqual(["ls-files", "--cached", "--others", "--exclude-standard", "-z"], StringComparer.Ordinal)) return true;
        if (suffix.Length == 5 && suffix[0] == "ls-tree" && suffix[1] == "-r" && suffix[2] == "-z"
            && suffix[3] == "--full-tree" && (suffix[4] == configuration.ExpectedLaneHead || suffix[4] == configuration.ExpectedLaneTree)) return true;
        if (suffix.Length >= 5 && suffix[0] == "check-attr")
        {
            var index = 1;
            if (suffix[index].StartsWith("--source=", StringComparison.Ordinal))
            {
                var source = suffix[index]["--source=".Length..];
                if (source != configuration.ExpectedLaneHead && source != configuration.ExpectedLaneTree) return false;
                index++;
            }
            if (suffix[index++] != "-z") return false;
            var separator = Array.IndexOf(suffix, "--", index);
            if (separator < index || !suffix.Skip(separator + 1).Any()) return false;
            var attributes = suffix.Skip(index).Take(separator - index).ToArray();
            var expectedAttributes = attributes.SequenceEqual(["filter"], StringComparer.Ordinal)
                || attributes.SequenceEqual(["merge"], StringComparer.Ordinal)
                || attributes.SequenceEqual(["text", "eol", "working-tree-encoding", "ident"], StringComparer.Ordinal);
            return expectedAttributes && suffix.Skip(separator + 1).All(path => configuration.AllowedGitPaths!
                .Contains(path, StringComparer.Ordinal));
        }
        return false;
    }

    private static bool IsPrivateIndexEnvironment(Configuration configuration)
    {
        var index = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        if (string.IsNullOrWhiteSpace(index)) return false;
        var indexName = Path.GetFileName(index);
        if (indexName is not ("disposition-verify" or "disposition-verify.materialized.index"
            or "retained-index" or "retained-index-final" or "retained-index-fence"
            or "retained-index.materialized.index" or "retained-index-final.materialized.index"
            or "retained-index-fence.materialized.index")) return false;
        var parent = Path.GetDirectoryName(Path.GetFullPath(index));
        return parent is not null
            && IsPhysicalDescendant(parent, Path.GetFullPath(configuration.DataRoot!))
            && IsOwnedPrivateTempPath(parent)
            && IsSafePhysicalPath(parent, configuration.DataRoot!);
    }

    private static bool IsDispositionVerificationIndex(Configuration configuration)
    {
        var index = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        return !string.IsNullOrWhiteSpace(index)
            && Path.GetFileName(index) is "disposition-verify" or "disposition-verify.materialized.index";
    }

    private static bool IsPrivateDispositionObjectEnvironment(Configuration configuration)
    {
        var objects = Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(objects) || configuration.RepositoryObjectsPath is null) return false;
        var fullObjects = Path.GetFullPath(objects);
        var privateTemp = Path.GetDirectoryName(fullObjects);
        if (privateTemp is null || Path.GetFileName(fullObjects) != "objects"
            || !IsPhysicalDescendant(privateTemp, Path.GetFullPath(configuration.DataRoot!))
            || !IsOwnedPrivateTempPath(privateTemp) || !IsSafePhysicalPath(fullObjects, configuration.DataRoot!)) return false;
        var info = Path.Combine(fullObjects, "info");
        var alternates = Path.Combine(info, "alternates");
        if (!Directory.Exists(info) || !File.Exists(alternates)
            || (File.GetAttributes(alternates) & FileAttributes.ReparsePoint) != 0
            || !IsSafePhysicalPath(info, configuration.DataRoot!)) return false;
        try
        {
            var actual = File.ReadAllText(alternates, new UTF8Encoding(false, true));
            var expected = Path.GetFullPath(configuration.RepositoryObjectsPath).Replace('\\', '/') + "\n";
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return false; }
    }

    private static bool IsPrivateMaterializationEnvironment(Configuration configuration)
    {
        var workTree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");
        var index = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        if (string.IsNullOrWhiteSpace(workTree) || string.IsNullOrWhiteSpace(index)) return false;
        var fullWorkTree = Path.GetFullPath(workTree);
        var fullIndex = Path.GetFullPath(index);
        var expectedView = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(Path.GetFileName(fullIndex)))
            + "-view";
        if (Path.GetFileName(fullWorkTree) != expectedView) return false;
        var verificationIndex = IsDispositionVerificationIndex(configuration);
        var objectDirectory = Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY");
        if (verificationIndex != !string.IsNullOrWhiteSpace(objectDirectory)
            || verificationIndex && !IsPrivateDispositionObjectEnvironment(configuration)
            || !verificationIndex && configuration.DispositionOperation != "discard") return false;
        var workTreeParent = Path.GetDirectoryName(fullWorkTree);
        var indexParent = Path.GetDirectoryName(fullIndex);
        return workTreeParent is not null && indexParent is not null && PathEquals(workTreeParent, indexParent)
            && IsPhysicalDescendant(fullWorkTree, Path.GetFullPath(configuration.DataRoot!))
            && IsOwnedPrivateTempPath(workTreeParent) && Directory.Exists(fullWorkTree)
            && IsSafePhysicalPath(fullWorkTree, configuration.DataRoot!);
    }

    private static void CopySafeDispositionEnvironment(ProcessStartInfo start, Configuration configuration)
    {
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var name in new[] { "GIT_INDEX_FILE", "GIT_WORK_TREE", "GIT_OBJECT_DIRECTORY" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["HOME"] = configuration.FixtureRoot;
        start.Environment["USERPROFILE"] = configuration.FixtureRoot;
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    private static Task AppendDispositionDiagnosticAsync(string path, string operation, string[] arguments,
        string stdinSha256, int? exitCode, Configuration configuration)
    {
        var command = TryGetGitCommand(arguments) ?? "unrecognized";
        var argumentsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", arguments))));
        var cwdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Environment.CurrentDirectory))));
        var record = JsonSerializer.Serialize(new { operation, command, arguments, exitCode, argumentsHash, stdinSha256, cwdHash,
            operationKind = configuration.DispositionOperation }) + "\n";
        return File.AppendAllTextAsync(path, record, new UTF8Encoding(false));
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
