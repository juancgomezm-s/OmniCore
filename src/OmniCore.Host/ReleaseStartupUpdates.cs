namespace OmniCore.Host;

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Protocol;

public sealed record StartupUpdatePreferences(int SchemaVersion = 1,
    ReleaseUpdateChannel Channel = ReleaseUpdateChannel.Stable, bool AutomaticDownload = true);
public sealed record StartupUpdateSnapshot(int SchemaVersion, string Revision, string Phase,
    ReleaseUpdateChannel Channel, string FromVersion, ReleaseUpdateCheckResult? Release = null,
    string? PackagePath = null, string Decision = "None", string? DecisionLaunchId = null,
    string? OperationId = null, string? Diagnostic = null, string? Workspace = null,
    string? SessionId = null, DateTimeOffset UpdatedAtUtc = default);
public sealed record StartupUpdateLaunch(string Action, string DataDirectory, string StatePath,
    string? OperationId = null, string? Diagnostic = null, string? RecoveryCommand = null,
    string? Workspace = null, string? SessionId = null, bool PrepareInBackground = false);
public sealed record StartupUpdateChoice(bool Accepted, string? Diagnostic, StartupUpdateSnapshot? Snapshot);

/// <summary>Download preparation is independent of activation. Only an exact human decision
/// authorizes the existing applier; an uncertain activation is never dispatched again.</summary>
public sealed class ReleaseStartupUpdates : IDisposable
{
    private readonly string _data;
    private readonly string _preferencesPath;
    private readonly string _snapshotPath;
    private readonly ReleaseUpdateStateStore _operations;
    private readonly IReleaseInstallationInspector _inspector;
    private readonly IReleaseUpdateDownloader _downloader;
    private readonly IReleasePublisherVerifier _publisher;
    private readonly IReleaseUpdateHelperLauncher _helper;
    private readonly Func<ReleaseUpdateCheckRequest, CancellationToken, Task<ReleaseUpdateCheckResult>> _check;
    private readonly Func<ReleaseUpdateEnvironment> _environment;
    private readonly bool _development;
    private readonly bool _ownsServices;

    public ReleaseStartupUpdates(string dataDirectory, string configDirectory,
        IReleaseInstallationInspector inspector, IReleaseUpdateDownloader downloader,
        IReleasePublisherVerifier publisher, IReleaseUpdateHelperLauncher helper,
        Func<ReleaseUpdateCheckRequest, CancellationToken, Task<ReleaseUpdateCheckResult>> check,
        Func<ReleaseUpdateEnvironment>? environment = null, bool development = false, bool ownsServices = false)
    {
        _data = Path.GetFullPath(dataDirectory);
        _preferencesPath = Path.Combine(configDirectory, "updates.json");
        _operations = new(_data);
        _inspector = inspector; _downloader = downloader; _publisher = publisher; _helper = helper;
        _check = check; _environment = environment ?? ReleaseUpdateRuntimeFacts.Inspect;
        _development = development; _ownsServices = ownsServices;
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inspector.Inspect().NpmPrefix ?? "unmanaged"));
        var normalized = OperatingSystem.IsWindows() ? prefix.ToUpperInvariant() : prefix;
        var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        _snapshotPath = Path.Combine(_operations.UpdatesDirectory, "startup", key + ".json");
    }

    public static ReleaseStartupUpdates CreateDefault()
    {
        var paths = OmniHost.CreatePlatformPaths();
        return new(paths.DataDirectory, paths.ConfigDirectory, new ReleaseInstallationInspector(),
            new ReleaseUpdateDownloader(), new GitHubReleasePublisherVerifier(), new NodeReleaseUpdateHelperLauncher(),
            async (request, token) => { using var checker = new GitHubReleaseUpdateChecker(); return await checker.CheckAsync(request, token).ConfigureAwait(false); },
            development: !OperatingSystem.IsWindows() || !Environment.Is64BitProcess
                || typeof(ReleaseStartupUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Contains('+', StringComparison.Ordinal) == true,
            ownsServices: true);
    }

    public bool Supported => !_development && _inspector.Inspect() is
        { Kind: ReleaseInstallKind.NpmGlobal, NpmPrefix: not null, PackageDirectory: not null };

    public StartupUpdatePreferences Preferences() => Read(_preferencesPath, StartupUpdateJson.Default.StartupUpdatePreferences)
        ?? new();

    public StartupUpdateSnapshot? Snapshot()
    {
        var state = Read(_snapshotPath, StartupUpdateJson.Default.StartupUpdateSnapshot);
        if (state is not null && (state.SchemaVersion != 1 || !Enum.IsDefined(state.Channel)
            || !Guid.TryParseExact(state.Revision, "N", out _)
            || state.Phase is not ("Checking" or "Downloading" or "Ready" or "Idle" or "Installing" or "Applied" or "Failed")
            || state.Decision is not ("None" or "NextLaunch" or "Restart")
            || state.Decision != "None" && !Guid.TryParseExact(state.DecisionLaunchId, "N", out _)
            || state.Phase == "Installing" && !Guid.TryParseExact(state.OperationId, "N", out _)))
            throw new InvalidDataException("update_startup_state_invalid");
        return state;
    }

    private IDisposable LockSnapshot()
    {
        RejectLinks(_snapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
        return new FileStream(_snapshotPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void SavePreferences(StartupUpdatePreferences preferences)
    {
        if (preferences.SchemaVersion != 1 || !Enum.IsDefined(preferences.Channel))
            throw new InvalidDataException("update_preferences_invalid");
        using var gate = _operations.AcquireLock(TimeSpan.FromSeconds(1));
        if (!_operations.TryRead(out var operation, out _)
            || operation is { Status: ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running
                or ReleaseUpdateOperationStatus.Unknown }) throw new InvalidOperationException("update_operation_in_progress");
        Write(_preferencesPath, preferences, StartupUpdateJson.Default.StartupUpdatePreferences);
    }

    public async Task<StartupUpdateSnapshot?> PrepareAsync(CancellationToken token)
    {
        if (!Supported) return null;
        var preferences = Preferences();
        if (preferences.SchemaVersion != 1 || !Enum.IsDefined(preferences.Channel) || !preferences.AutomaticDownload)
            return Snapshot();
        var installation = _inspector.Inspect();
        using var snapshotLease = LockSnapshot();
        if (!_operations.TryRead(out var operation, out _)
            || operation is { Status: ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running
                or ReleaseUpdateOperationStatus.Unknown }) return Snapshot();
        var previous = Snapshot();
        if (previous?.Phase == "Installing") return previous;
        if (previous is { Phase: "Ready", Decision: "NextLaunch" or "Restart", Release: not null }
            && ValidRelease(previous.Release, preferences, installation)
            && previous.FromVersion == installation.InstalledVersion) return previous;
        if (previous?.Release?.RetryAfterUtc > DateTimeOffset.UtcNow) return previous;
        var revision = Guid.NewGuid().ToString("N");
        var downloading = new StartupUpdateSnapshot(1, revision, "Checking", preferences.Channel,
            installation.InstalledVersion ?? "", UpdatedAtUtc: DateTimeOffset.UtcNow);
        Save(downloading);
        try
        {
            var check = await _check(new(preferences.Channel, installation.InstalledVersion ?? "", false,
                _environment()), token).ConfigureAwait(false);
            if (check.Status != ReleaseUpdateStatus.UpdateAvailable || !ValidRelease(check, preferences, installation))
                return Save(downloading with { Phase = "Idle", Release = check, Diagnostic = check.DiagnosticCode });
            downloading = downloading with { Phase = "Downloading", Release = check };
            Save(downloading);
            var cached = previous is { Phase: "Ready", PackagePath: not null }
                && previous.Channel == check.Channel && previous.Release?.Asset == check.Asset
                && previous.Release?.CommitSha == check.CommitSha && previous.FromVersion == check.InstalledVersion
                && await VerifyCached(previous, token).ConfigureAwait(false);
            string? archive;
            if (cached) archive = previous!.PackagePath;
            else
            {
                var download = await _downloader.DownloadAsync(check.Asset!, check.Tag!,
                    _operations.StagingDirectory(check.AvailableVersion!), check.AvailableVersion!, token).ConfigureAwait(false);
                if (download.Status != ReleaseUpdateDownloadStatus.Downloaded || download.Path is null)
                    return Save(downloading with { Phase = "Failed", Diagnostic = download.DiagnosticCode ?? "update_download_failed",
                        Release = check with { RetryAfterUtc = download.RetryAfterUtc } });
                archive = download.Path;
            }
            var verification = await _publisher.VerifyAsync(archive!, check, token).ConfigureAwait(false);
            if (!verification.Verified)
                return Save(downloading with { Phase = "Failed", Diagnostic = verification.DiagnosticCode });
            if (Preferences() != preferences || _inspector.Inspect().InstalledVersion != installation.InstalledVersion)
                return Save(downloading with { Phase = "Idle", Diagnostic = "update_decision_stale" });
            if (!_operations.TryRead(out var currentOperation, out _)
                || currentOperation is { Status: ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running
                    or ReleaseUpdateOperationStatus.Unknown })
                return Save(downloading with { Phase = "Idle", Diagnostic = "update_operation_in_progress" });
            // Keep a prior decision only for the very same release, prefix and installed version.
            var ready = downloading with { Phase = "Ready", PackagePath = archive,
                Release = check with { MetadataOnly = false, ArchiveVerificationPending = false, PublisherVerificationPending = false },
                Decision = cached ? previous!.Decision : "None",
                DecisionLaunchId = cached ? previous!.DecisionLaunchId : null,
                Workspace = cached ? previous!.Workspace : null, SessionId = cached ? previous!.SessionId : null,
                Diagnostic = null };
            return Save(ready);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Save(downloading with { Phase = "Failed", Diagnostic = "update_download_cancelled" }); throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Net.Http.HttpRequestException or TimeoutException or JsonException)
        { return Save(downloading with { Phase = "Failed", Diagnostic = "update_preparation_failed" }); }
    }

    public StartupUpdateChoice Choose(string revision, string decision, string launchId,
        string? workspace = null, string? sessionId = null)
    {
        if (decision is not ("NextLaunch" or "Restart") || !ValidLaunchId(launchId))
            return new(false, "update_decision_invalid", null);
        using var snapshotLease = LockSnapshot();
        using var lease = _operations.AcquireLock(TimeSpan.FromSeconds(1));
        var state = Snapshot(); var preferences = Preferences(); var installation = _inspector.Inspect();
        if (!_operations.TryRead(out var active, out _) || active is { Status: ReleaseUpdateOperationStatus.Pending
            or ReleaseUpdateOperationStatus.Running or ReleaseUpdateOperationStatus.Unknown })
            return new(false, "update_operation_in_progress", state);
        if (state is not { Phase: "Ready", Release: not null } || state.Revision != revision
            || !ValidRelease(state.Release, preferences, installation) || state.FromVersion != installation.InstalledVersion)
            return new(false, "update_decision_stale", state);
        if (decision == "Restart" && installation.NpmPrefix is { } prefix)
        {
            var instances = new ReleaseUpdateInstances(_operations, prefix);
            using var gate = instances.TryAcquireGate();
            if (gate is null || instances.HasOtherInstances(Environment.ProcessId))
                return new(false, "update_other_instances_active", state);
        }
        if (workspace is not null && (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace)))
            return new(false, "update_resume_invalid", state);
        if (sessionId is not null && !Guid.TryParse(sessionId, out _))
            return new(false, "update_resume_invalid", state);
        return new(true, null, Save(state with { Decision = decision, DecisionLaunchId = launchId,
            Workspace = workspace, SessionId = sessionId }));
    }

    public async Task<StartupUpdateLaunch> LaunchAsync(string launchId, bool restart, CancellationToken token)
    {
        if (!ValidLaunchId(launchId)) return Launch("blocked", diagnostic: "update_launch_id_invalid");
        if (!Supported) return Launch("continue");
        using var snapshotLease = LockSnapshot();
        if (!_operations.TryRead(out var operation, out var error)) return Launch("blocked", diagnostic: error);
        var state = Snapshot();
        var resume = restart && state?.Decision == "Restart" && state.DecisionLaunchId == launchId;
        var resumeWorkspace = resume ? state!.Workspace : null;
        var resumeSession = resume ? state!.SessionId : null;
        StartupUpdateLaunch Finish(string action, string? operationId = null, string? diagnostic = null,
            string? recovery = null, string? workspace = null, string? session = null) =>
            Launch(action, operationId, diagnostic, recovery, workspace ?? resumeWorkspace, session ?? resumeSession);
        if (operation is { Status: ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running })
        {
            if (!Alive(operation.ParentPid) && !Alive(operation.HelperPid) && !Alive(operation.NpmPid))
            {
                using var operationLease = _operations.AcquireLock(TimeSpan.FromMilliseconds(100));
                if (_operations.TryRead(out var current, out _) && current?.OperationId == operation.OperationId
                    && current!.Status is ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running
                    && !Alive(current.ParentPid) && !Alive(current.HelperPid) && !Alive(current.NpmPid))
                    _operations.Write(current with { Status = ReleaseUpdateOperationStatus.Unknown,
                        Detail = "helper_outcome_unknown", UpdatedAtUtc = DateTimeOffset.UtcNow });
                return Finish("blocked", operation.OperationId, "update_operation_unknown", operation.RecoveryCommand);
            }
            return Finish("wait", operation.OperationId, "update_operation_in_progress", operation.RecoveryCommand);
        }
        if (operation is { Status: ReleaseUpdateOperationStatus.Unknown })
            return Finish("blocked", operation.OperationId, "update_operation_unknown", operation.RecoveryCommand);
        if (state?.Phase == "Installing")
        {
            if (operation?.OperationId != state.OperationId)
                return Finish("blocked", state.OperationId, "update_operation_unknown");
            if (operation!.Status == ReleaseUpdateOperationStatus.Succeeded)
            {
                if (_inspector.Inspect().InstalledVersion != operation.ToVersion)
                    return Finish("blocked", operation.OperationId, "update_installed_version_mismatch", operation.RecoveryCommand);
                Save(state with { Phase = "Applied", Decision = "None" });
                return Finish("continue", workspace: state.Workspace, session: state.SessionId);
            }
            Save(state with { Phase = "Failed", Decision = "None", Diagnostic = operation.Detail });
            return Finish("blocked", state.OperationId, operation.Detail, operation.RecoveryCommand);
        }
        // Crash after helper handoff but before Installing was saved: classify the exact
        // completed operation; never submit the Ready decision a second time.
        if (state is { Phase: "Ready", Decision: not "None", Release.Asset: not null }
            && operation is { Operation: ReleaseUpdateOperation.Apply,
                Status: ReleaseUpdateOperationStatus.Succeeded or ReleaseUpdateOperationStatus.Failed }
            && operation.FromVersion == state.FromVersion && operation.ToVersion == state.Release.AvailableVersion
            && operation.ExpectedSha256 == state.Release.Asset.Sha256
            && operation.NpmPrefix == _inspector.Inspect().NpmPrefix)
        {
            if (operation.Status == ReleaseUpdateOperationStatus.Failed)
            {
                Save(state with { Phase = "Failed", Decision = "None", OperationId = operation.OperationId,
                    Diagnostic = operation.Detail });
                return Finish("continue", diagnostic: operation.Detail);
            }
            if (_inspector.Inspect().InstalledVersion != operation.ToVersion)
                return Finish("blocked", operation.OperationId, "update_installed_version_mismatch", operation.RecoveryCommand);
            Save(state with { Phase = "Applied", Decision = "None", OperationId = operation.OperationId });
            return Finish("continue", workspace: state.Workspace, session: state.SessionId);
        }
        var permitted = state is { Phase: "Ready", Release: not null }
            && (state.Decision == "NextLaunch" && state.DecisionLaunchId != launchId
                || restart && state.Decision == "Restart" && state.DecisionLaunchId == launchId);
        if (!permitted) return Finish("continue");
        // Decision, channel and version are re-read under the decision lease before activation.
        state = Snapshot();
        if (state is not { Phase: "Ready", Release: not null }
            || !(state.Decision == "NextLaunch" && state.DecisionLaunchId != launchId
                || restart && state.Decision == "Restart" && state.DecisionLaunchId == launchId))
            return Finish("continue");
        var installation = _inspector.Inspect();
        if (!ValidRelease(state!.Release!, Preferences(), installation)
            || state.FromVersion != installation.InstalledVersion)
        { Save(state with { Phase = "Failed", Decision = "None", Diagnostic = "update_decision_stale" }); return Finish("continue"); }
        if (!await VerifyCached(state, token).ConfigureAwait(false))
        { Save(state with { Phase = "Failed", Decision = "None", Diagnostic = "update_staged_archive_changed" }); return Finish("continue"); }
        var requirements = state.Release!.Requirements;
        var facts = _environment();
        if (requirements is null || state.Release.Asset!.Rid != facts.Rid
            || GitHubReleaseUpdateChecker.CheckCompatibility(new(requirements.DotNetMinimum, requirements.DotNetMaximum,
                requirements.NodeMinimum, requirements.NodeMaximum, requirements.ProtocolMinimum, requirements.ProtocolMaximum,
                requirements.ConfigurationMinimum, requirements.ConfigurationMaximum), facts).Status != ReleaseCompatibility.Compatible)
            return Finish("continue", diagnostic: "update_compatibility_unverified");
        var cached = new CachedDownload(state.PackagePath!, state.Release.Asset);
        var selectedPreferences = Preferences();
        var applier = new ReleaseUpdateApplier(_operations, _inspector, cached, _helper, publisherVerifier: _publisher,
            activationPrecondition: () => Preferences() == selectedPreferences
                && ValidRelease(state.Release, selectedPreferences, _inspector.Inspect()));
        var applied = await applier.ApplyAsync(state.Release, confirmed: true, token).ConfigureAwait(false);
        if (!applied.Prepared)
        {
            // No automatic replay if an activation record was already produced.
            if (applied.State?.OperationId is not null)
                Save(state with { Phase = "Failed", Decision = "None", OperationId = applied.State.OperationId,
                    Diagnostic = applied.DiagnosticCode });
            else if (_operations.TryRead(out var failedOperation, out _)
                && failedOperation?.OperationId != operation?.OperationId
                && failedOperation?.ToVersion == state.Release.AvailableVersion)
                Save(state with { Phase = "Failed", Decision = "None", OperationId = failedOperation!.OperationId,
                    Diagnostic = applied.DiagnosticCode });
            return Finish("continue", diagnostic: applied.DiagnosticCode);
        }
        Save(state with { Phase = "Installing", OperationId = applied.State!.OperationId });
        return Finish("wait", applied.State.OperationId, recovery: applied.State.RecoveryCommand);
    }

    public static string UnavailableLaunchJson()
    {
        var paths = OmniHost.CreatePlatformPaths();
        return Encode(new("continue", paths.DataDirectory, new ReleaseUpdateStateStore(paths.DataDirectory).StatePath,
            Diagnostic: "update_startup_unavailable"));
    }

    public string SnapshotJson() => JsonSerializer.Serialize(Snapshot(), StartupUpdateJson.Default.StartupUpdateSnapshot);
    public static string Encode(StartupUpdateLaunch value) => JsonSerializer.Serialize(value, StartupUpdateJson.Default.StartupUpdateLaunch);

    private StartupUpdateLaunch Launch(string action, string? operationId = null, string? diagnostic = null,
        string? recovery = null, string? workspace = null, string? session = null) =>
        new(action, _data, _operations.StatePath, operationId, diagnostic, recovery, workspace, session,
            Supported && Preferences().AutomaticDownload);

    private static bool Alive(int? pid)
    {
        if (pid is not > 0) return false;
        try { using var process = System.Diagnostics.Process.GetProcessById(pid.Value); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    private static bool ValidLaunchId(string value) => Guid.TryParseExact(value, "N", out _);
    private static bool ValidRelease(ReleaseUpdateCheckResult check, StartupUpdatePreferences preferences, ReleaseInstallation installation) =>
        preferences.SchemaVersion == 1 && Enum.IsDefined(preferences.Channel) && check.Channel == preferences.Channel
        && check.Status == ReleaseUpdateStatus.UpdateAvailable && check.Compatibility == ReleaseCompatibility.Compatible
        && check.Asset is { Size: > 0 and <= ReleaseUpdateDownloader.MaximumAssetBytes, Sha256.Length: 64 } asset
        && asset.Sha256.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(asset.Name)
        && Path.GetFileName(asset.Name) == asset.Name && asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && check.Tag is not null && (check.Tag == check.AvailableVersion || check.Tag == "v" + check.AvailableVersion)
        && check.CommitSha is { Length: 40 } commit && commit.All(Uri.IsHexDigit)
        && SemVer.TryParse(check.AvailableVersion ?? "", out var target)
        && SemVer.TryParse(installation.InstalledVersion ?? "", out var current)
        && target!.CompareTo(current) > 0
        && (preferences.Channel == ReleaseUpdateChannel.Preview || target.Prerelease is null);

    private async Task<bool> VerifyCached(StartupUpdateSnapshot state, CancellationToken token)
    {
        if (state.Release?.Asset is not { } asset || state.Release.AvailableVersion is null || state.PackagePath is null) return false;
        var expected = Path.Combine(_operations.StagingDirectory(state.Release.AvailableVersion), asset.Name);
        if (!string.Equals(Path.GetFullPath(state.PackagePath), Path.GetFullPath(expected),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        try
        {
            RejectLinks(state.PackagePath);
            await using var stream = new FileStream(state.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length == asset.Size && string.Equals(Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), asset.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private StartupUpdateSnapshot Save(StartupUpdateSnapshot state)
    {
        state = state with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        Write(_snapshotPath, state, StartupUpdateJson.Default.StartupUpdateSnapshot);
        return state;
    }

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(path)) return default;
        RejectLinks(path);
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("update_startup_state_invalid");
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), type)
            ?? throw new InvalidDataException("update_startup_state_invalid");
    }

    private static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, type); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("update_startup_path_link");
    }

    public void Dispose()
    {
        if (!_ownsServices) return;
        (_downloader as IDisposable)?.Dispose(); (_publisher as IDisposable)?.Dispose();
    }

    private sealed class CachedDownload(string path, ReleaseUpdateAsset expected) : IReleaseUpdateDownloader
    {
        public Task<ReleaseUpdateDownloadResult> DownloadAsync(ReleaseUpdateAsset asset, string tag,
            string destinationDirectory, string expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(asset == expected
                ? new ReleaseUpdateDownloadResult(ReleaseUpdateDownloadStatus.Downloaded, Path: path,
                    Sha256: expected.Sha256, Size: expected.Size)
                : new(ReleaseUpdateDownloadStatus.DigestMismatch, "update_staged_archive_changed"));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StartupUpdatePreferences))]
[JsonSerializable(typeof(StartupUpdateSnapshot))]
[JsonSerializable(typeof(StartupUpdateLaunch))]
internal partial class StartupUpdateJson : JsonSerializerContext;
