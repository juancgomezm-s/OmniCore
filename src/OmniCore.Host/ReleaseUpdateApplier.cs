namespace OmniCore.Host;

using System.Diagnostics;
using OmniCore.Protocol;

/// <summary>
/// Prepares a verified update or rollback and hands the replacement to a detached helper.
/// This process never replaces the files it is running from.
/// </summary>
public sealed class ReleaseUpdateApplier
{
    private readonly ReleaseUpdateStateStore _store;
    private readonly IReleaseInstallationInspector _inspector;
    private readonly IReleaseUpdateDownloader _downloader;
    private readonly IReleaseUpdateHelperLauncher _launcher;
    private readonly TimeSpan _lockTimeout;
    private readonly IReleasePublisherVerifier _publisherVerifier;
    private readonly Func<bool>? _activationPrecondition;

    public ReleaseUpdateApplier(ReleaseUpdateStateStore store, IReleaseInstallationInspector inspector,
        IReleaseUpdateDownloader downloader, IReleaseUpdateHelperLauncher launcher, TimeSpan? lockTimeout = null,
        IReleasePublisherVerifier? publisherVerifier = null, Func<bool>? activationPrecondition = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        _publisherVerifier = publisherVerifier ?? new GitHubReleasePublisherVerifier();
        _activationPrecondition = activationPrecondition;
    }

    public async Task<ReleaseUpdateApplyResult> ApplyAsync(ReleaseUpdateCheckResult check, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!confirmed) return new(false, "update_confirmation_required");
        if (check.Asset is not { } asset || check.AvailableVersion is not { } version || check.Tag is not { } tag)
            return new(false, "update_asset_unavailable");
        if (check.Compatibility != ReleaseCompatibility.Compatible)
            return new(false, "update_compatibility_unverified");

        var installation = _inspector.Inspect();
        if (Refusal(installation) is { } refusal) return new(false, refusal);
        if (installation.PackageDirectory is not { } packageDirectory || installation.NpmPath is not { } npm
            || installation.NodePath is not { } node)
            return new(false, installation.Detail ?? "update_toolchain_unavailable");
        if (installation.NpmPrefix is not { } npmPrefix || !PrefixMatchesPackage(packageDirectory, npmPrefix))
            return new(false, "update_prefix_mismatch");
        if (HelperPath(packageDirectory) is not { } helper) return new(false, "update_helper_missing");

        IDisposable lease;
        try { lease = _store.AcquireLock(_lockTimeout); }
        catch (TimeoutException) { return new(false, "update_operation_in_progress"); }
        using (lease)
        {
            var instances = new ReleaseUpdateInstances(_store, npmPrefix);
            using var instanceGate = instances.TryAcquireGate();
            if (instanceGate is null) return new(false, "update_instance_gate_unavailable");
            if (instances.ReadBarrier() is { } barrier) return new(false, barrier);
            if (instances.HasOtherInstances(Environment.ProcessId))
                return new(false, "update_other_instances_active");
            if (_activationPrecondition?.Invoke() == false) return new(false, "update_decision_stale");
            if (!_store.TryRead(out var existing, out var diagnostic))
                return new(false, diagnostic ?? "update_state_invalid");
            if (existing is { Status: ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running })
                return RefuseOrMarkUnknown(existing);
            if (existing is { Status: ReleaseUpdateOperationStatus.Unknown })
                return new(false, "update_operation_unknown", existing);

            ReleaseUpdateDownloadResult download;
            try
            {
                download = await _downloader.DownloadAsync(asset, tag, _store.StagingDirectory(version), version,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            if (download.Status != ReleaseUpdateDownloadStatus.Downloaded || download.Path is null)
                return new(false, download.DiagnosticCode ?? "update_download_failed");

            var publisher = await _publisherVerifier.VerifyAsync(download.Path, check, cancellationToken).ConfigureAwait(false);
            if (!publisher.Verified) return new(false, publisher.DiagnosticCode);

            if (_activationPrecondition?.Invoke() == false) return new(false, "update_decision_stale");
            var operationId = Guid.NewGuid().ToString("N");
            var backup = _store.BackupDirectory(installation.InstalledVersion ?? "unknown", operationId);
            string backupHash;
            try
            {
                if (!Directory.Exists(backup)) CopyDirectory(packageDirectory, backup, cancellationToken);
                backupHash = PackageFingerprint(backup);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new(false, "update_backup_failed");
            }

            var pending = new ReleaseUpdateState(ReleaseUpdateStateStore.SchemaVersion, ReleaseUpdateOperation.Apply,
                ReleaseUpdateOperationStatus.Pending, installation.InstalledVersion, version, download.Path, backup,
                Environment.ProcessId, null, DateTimeOffset.UtcNow, "waiting_for_parent_exit",
                operationId, asset.Sha256.ToLowerInvariant(), asset.Size, npmPrefix, backupHash,
                RecoveryCommand: RecoveryCommand(operationId, npm, node, npmPrefix, packageDirectory));
            _store.Write(pending);
            try { instances.PublishBarrier(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _store.Write(pending with { Status = ReleaseUpdateOperationStatus.Failed,
                    Detail = "update_instance_gate_unavailable" });
                return new(false, "update_instance_gate_unavailable");
            }
            var helperPid = Launch(helper, "apply", download.Path, null, npm, node, pending, packageDirectory);
            if (helperPid <= 0)
            {
                _store.Write(pending with
                { Status = ReleaseUpdateOperationStatus.Failed, Detail = "update_helper_start_failed" });
                return new(false, "update_helper_start_failed");
            }
            var prepared = pending with { HelperPid = helperPid };
            _store.Write(prepared);
            return new(true, "update_prepared", prepared);
        }
    }

    public ReleaseUpdateRollbackResult Rollback(bool confirmed)
    {
        if (!confirmed) return new(false, "update_confirmation_required");
        var installation = _inspector.Inspect();
        if (Refusal(installation) is { } refusal) return new(false, refusal);
        if (installation.PackageDirectory is not { } packageDirectory || installation.NpmPath is not { } npm
            || installation.NodePath is not { } node)
            return new(false, installation.Detail ?? "update_toolchain_unavailable");
        if (installation.NpmPrefix is not { } npmPrefix || !PrefixMatchesPackage(packageDirectory, npmPrefix))
            return new(false, "update_prefix_mismatch");
        if (HelperPath(packageDirectory) is not { } helper) return new(false, "update_helper_missing");

        IDisposable lease;
        try { lease = _store.AcquireLock(_lockTimeout); }
        catch (TimeoutException) { return new(false, "update_operation_in_progress"); }
        using (lease)
        {
            var instances = new ReleaseUpdateInstances(_store, npmPrefix);
            using var instanceGate = instances.TryAcquireGate();
            if (instanceGate is null) return new(false, "update_instance_gate_unavailable");
            if (instances.ReadBarrier() is { } barrier) return new(false, barrier);
            if (instances.HasOtherInstances(Environment.ProcessId))
                return new(false, "update_other_instances_active");
            if (!_store.TryRead(out var state, out var diagnostic))
                return new(false, diagnostic ?? "update_state_invalid");
            if (state is { Status: ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running })
            {
                var pendingRefusal = RefuseOrMarkUnknown(state);
                return new(false, pendingRefusal.DiagnosticCode, pendingRefusal.State ?? state);
            }
            // An explicit rollback compensates an uncertain installation; it never retries it.
            if (state is null || state.BackupPath is not { } backup || !Directory.Exists(backup)
                || state.FromVersion is null)
                return new(false, "update_rollback_unavailable", state);
            if (state.NpmPrefix is null || !SamePath(state.NpmPrefix, npmPrefix))
                return new(false, "update_prefix_mismatch", state);
            if (!BackupMatches(backup, state.BackupSha256))
                return new(false, "update_backup_unverified", state);
            if (IsAlive(state.HelperPid) || IsAlive(state.NpmPid)) return new(false, "update_operation_in_progress", state);
            if (!ArchiveAbandonedHelperLock(state)) return new(false, "update_operation_in_progress", state);
            if (state.Status is ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running
                && IsAlive(state.HelperPid))
                return new(false, "update_operation_in_progress", state);

            var operationId = Guid.NewGuid().ToString("N");
            var pending = state with
            {
                Operation = ReleaseUpdateOperation.Rollback,
                Status = ReleaseUpdateOperationStatus.Pending,
                ParentPid = Environment.ProcessId,
                HelperPid = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = "waiting_for_parent_exit",
                OperationId = operationId,
                ExpectedSha256 = null,
                ExpectedSize = null,
                NpmPrefix = npmPrefix,
                NpmPid = null,
                RecoveryCommand = RecoveryCommand(operationId, npm, node, npmPrefix, packageDirectory),
            };
            _store.Write(pending);
            try { instances.PublishBarrier(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _store.Write(pending with { Status = ReleaseUpdateOperationStatus.Failed,
                    Detail = "update_instance_gate_unavailable" });
                return new(false, "update_instance_gate_unavailable");
            }
            var helperPid = Launch(helper, "rollback", null, backup, npm, node, pending, installation.PackageDirectory!);
            if (helperPid <= 0)
            {
                _store.Write(pending with
                { Status = ReleaseUpdateOperationStatus.Failed, Detail = "update_helper_start_failed" });
                return new(false, "update_helper_start_failed");
            }
            var prepared = pending with { HelperPid = helperPid };
            _store.Write(prepared);
            return new(true, "update_rollback_prepared", prepared);
        }
    }

    private int Launch(string helper, string mode, string? packagePath, string? backupPath, string npm,
        string node, ReleaseUpdateState state, string packageDirectory)
    {
        Directory.CreateDirectory(_store.UpdatesDirectory);
        try
        {
            // Recovery must survive an interrupted npm replacement of the active package.
            var recoveryHelper = _store.RecoveryHelperPath(state.OperationId!);
            for (var current = Path.GetDirectoryName(recoveryHelper); current is not null; current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The update recovery path contains a link.");
            Directory.CreateDirectory(Path.GetDirectoryName(recoveryHelper)!);
            File.Copy(helper, recoveryHelper, overwrite: false);
            return _launcher.Launch(new(recoveryHelper, mode, Environment.ProcessId, _store.StatePath, packagePath,
                backupPath, npm, node, "omnicore-cli", _store.UpdatesDirectory, TimeSpan.FromMinutes(15),
                state.OperationId ?? string.Empty, state.ExpectedSha256, state.ExpectedSize, state.NpmPrefix ?? string.Empty,
                mode == "apply" ? state.ToVersion ?? "" : state.FromVersion ?? "",
                packageDirectory, state.BackupSha256));
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _store.Write(state with { Status = ReleaseUpdateOperationStatus.Failed, Detail = "update_helper_start_failed" });
            return 0;
        }
    }

    private string RecoveryCommand(string operationId, string npm, string node, string prefix, string packageDirectory)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "& " + Quote(node) + " " + Quote(_store.RecoveryHelperPath(operationId))
            + " --mode recover --state " + Quote(_store.StatePath) + " --npm " + Quote(npm) + " --node " + Quote(node)
            + " --package-directory " + Quote(packageDirectory) + " --npm-prefix " + Quote(prefix) + " --yes true";
    }

    private static string? Refusal(ReleaseInstallation installation) => installation.Kind switch
    {
        ReleaseInstallKind.NpmGlobal => null,
        ReleaseInstallKind.DevelopmentCheckout => "update_development_checkout",
        ReleaseInstallKind.ReleasePackage => "update_release_package_unsupported",
        _ => "update_install_kind_unknown",
    };

    internal static string PackageFingerprint(string directory)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var folder))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update backup contains a link.");
            files.AddRange(Directory.EnumerateFiles(folder));
            foreach (var child in Directory.EnumerateDirectories(folder)) pending.Push(child);
        }
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update backup contains a link.");
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var name = System.Text.Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
            using var stream = File.OpenRead(file);
            hash.AppendData(System.Security.Cryptography.SHA256.HashData(stream));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool BackupMatches(string directory, string? expected)
    {
        if (expected is null) return false;
        try { return PackageFingerprint(directory) == expected; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        { return false; }
    }

    private bool ArchiveAbandonedHelperLock(ReleaseUpdateState state)
    {
        var path = _store.StatePath + ".helper.lock";
        if (!File.Exists(path)) return true;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 4096) return false;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            var owner = document.RootElement;
            if (owner.GetProperty("operationId").GetString() != state.OperationId
                || owner.GetProperty("operation").GetString() != state.Operation.ToString()
                || !SamePath(owner.GetProperty("statePath").GetString() ?? "", _store.StatePath)
                || !owner.GetProperty("pid").TryGetInt32(out var pid) || pid <= 0
                || state.HelperPid != pid || IsAlive(pid)) return false;
            File.Move(path, path + ".abandoned-" + state.OperationId, overwrite: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private ReleaseUpdateApplyResult RefuseOrMarkUnknown(ReleaseUpdateState state)
    {
        if (IsAlive(state.HelperPid) || IsAlive(state.ParentPid) || IsAlive(state.NpmPid))
            return new(false, "update_operation_in_progress", state);
        var unknown = state with
        {
            Status = ReleaseUpdateOperationStatus.Unknown,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Detail = "helper_outcome_unknown",
        };
        _store.Write(unknown);
        return new(false, "update_operation_unknown", unknown);
    }

    private static bool PrefixMatchesPackage(string packageDirectory, string prefix)
    {
        var modules = Path.GetDirectoryName(packageDirectory);
        if (modules is null || !Path.GetFileName(modules).Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            return false;
        var parent = Path.GetDirectoryName(modules);
        if (parent is null) return false;
        if (Path.GetFileName(parent).Equals("lib", StringComparison.OrdinalIgnoreCase))
            parent = Path.GetDirectoryName(parent);
        return parent is not null && SamePath(parent, prefix);
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), comparison);
        }
        catch (ArgumentException) { return false; }
    }

    private static string? HelperPath(string packageDirectory)
    {
        var path = Path.Combine(packageDirectory, "bin", "update-helper.js");
        return File.Exists(path) ? path : null;
    }

    private static bool IsAlive(int? pid)
    {
        if (pid is not > 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(temporary);
            CopyRecursive(new DirectoryInfo(source), temporary, cancellationToken);
            var parent = Path.GetDirectoryName(destination);
            if (parent is not null) Directory.CreateDirectory(parent);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(temporary, destination);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                try { Directory.Delete(temporary, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void CopyRecursive(DirectoryInfo source, string destination, CancellationToken cancellationToken)
    {
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("update_backup_reparse_point");
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                Directory.CreateDirectory(target);
                CopyRecursive(directory, target, cancellationToken);
            }
            else if (entry is FileInfo file)
            {
                file.CopyTo(target, overwrite: false);
            }
        }
    }
}
