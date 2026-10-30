namespace OmniCore.Tests;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed class ReleaseUpdateDownloaderTests
{
    [Fact]
    public async Task Verified_download_writes_the_asset_and_reports_its_digest()
    {
        var bytes = Package();
        using var directory = new TempDirectory();
        using var downloader = new ReleaseUpdateDownloader(new Handler(_ => Bytes(bytes)));
        var result = await downloader.DownloadAsync(Asset(bytes), "v0.2.0", directory.Path, "0.2.0",
            TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateDownloadStatus.Downloaded, result.Status);
        Assert.NotNull(result.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.Path!, TestContext.Current.CancellationToken));
        Assert.Equal(Digest(bytes), result.Sha256);
    }

    [Fact]
    public async Task Altered_digest_is_rejected_without_publishing_the_asset()
    {
        var bytes = Package();
        using var directory = new TempDirectory();
        using var downloader = new ReleaseUpdateDownloader(new Handler(_ => Bytes(bytes)));
        var asset = Asset(bytes) with { Sha256 = new string('0', 64) };
        var result = await downloader.DownloadAsync(asset, "v0.2.0", directory.Path, "0.2.0",
            TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateDownloadStatus.DigestMismatch, result.Status);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Declared_size_mismatch_is_rejected_before_writing()
    {
        var bytes = Package();
        using var directory = new TempDirectory();
        using var downloader = new ReleaseUpdateDownloader(new Handler(_ => Bytes(bytes)));
        var asset = Asset(bytes) with { Size = bytes.Length + 10 };
        var result = await downloader.DownloadAsync(asset, "v0.2.0", directory.Path, "0.2.0",
            TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateDownloadStatus.SizeMismatch, result.Status);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Redirect_outside_the_allowed_hosts_is_rejected()
    {
        using var directory = new TempDirectory();
        using var downloader = new ReleaseUpdateDownloader(new Handler(_ => new(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.example/asset.tgz") },
        }));
        var bytes = Package();
        var result = await downloader.DownloadAsync(Asset(bytes), "v0.2.0", directory.Path, "0.2.0",
            TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateDownloadStatus.RedirectRejected, result.Status);
    }

    [Fact]
    public async Task Package_identity_mismatch_is_rejected()
    {
        var bytes = Package(name: "other-package");
        using var directory = new TempDirectory();
        using var downloader = new ReleaseUpdateDownloader(new Handler(_ => Bytes(bytes)));
        var result = await downloader.DownloadAsync(Asset(bytes), "v0.2.0", directory.Path, "0.2.0",
            TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateDownloadStatus.PackageIdentityMismatch, result.Status);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Asset_larger_than_the_limit_is_rejected_without_a_request()
    {
        using var directory = new TempDirectory();
        using var downloader = new ReleaseUpdateDownloader(new Handler(_ =>
            throw new InvalidOperationException("must not download")));
        var bytes = Package();
        var asset = Asset(bytes) with { Size = ReleaseUpdateDownloader.MaximumAssetBytes + 1 };
        var result = await downloader.DownloadAsync(asset, "v0.2.0", directory.Path, "0.2.0",
            TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateDownloadStatus.AssetUnavailable, result.Status);
        Assert.Equal("release_asset_invalid", result.DiagnosticCode);
    }

    private static ReleaseUpdateAsset Asset(byte[] bytes) =>
        new(42, "omnicore-cli-0.2.0.tgz", bytes.Length, Digest(bytes), "win-x64");

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    internal static byte[] Package(string name = "omnicore-cli", string version = "0.2.0",
        params (string Name, string Text)[] extraEntries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new { name, version });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/package.json")
            { DataStream = new MemoryStream(manifest) });
            foreach (var (entryName, text) in extraEntries)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName)
                { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }
        return output.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

public sealed class ReleaseUpdateStateStoreTests
{
    [Fact]
    public void State_roundtrips_through_the_durable_store()
    {
        using var directory = new TempDirectory();
        var store = new ReleaseUpdateStateStore(directory.Path);
        var state = new ReleaseUpdateState(ReleaseUpdateStateStore.SchemaVersion, ReleaseUpdateOperation.Apply,
            ReleaseUpdateOperationStatus.Pending, "0.1.0", "0.2.0", @"C:\staging\asset.tgz", @"C:\backup\0.1.0",
            1234, 5678, DateTimeOffset.UtcNow, "waiting_for_parent_exit", new string('b', 32),
            new string('a', 64), 128, @"C:\prefix", new string('c', 64), 9876);
        store.Write(state);
        Assert.True(store.TryRead(out var read, out var diagnostic));
        Assert.Null(diagnostic);
        Assert.NotNull(read);
        Assert.Equal(ReleaseUpdateOperation.Apply, read!.Operation);
        Assert.Equal(ReleaseUpdateOperationStatus.Pending, read.Status);
        Assert.Equal("0.1.0", read.FromVersion);
        Assert.Equal("0.2.0", read.ToVersion);
        Assert.Equal(1234, read.ParentPid);
        Assert.Equal(5678, read.HelperPid);
        Assert.Equal(new string('b', 32), read.OperationId);
        Assert.Equal(128, read.ExpectedSize);
        Assert.Equal(@"C:\prefix", read.NpmPrefix);
        Assert.Equal(new string('c', 64), read.BackupSha256);
        Assert.Equal(9876, read.NpmPid);
        Assert.Contains("\"npmPid\": 9876", File.ReadAllText(store.StatePath));
    }

    [Fact]
    public void Corrupt_state_is_reported_instead_of_throwing()
    {
        using var directory = new TempDirectory();
        var store = new ReleaseUpdateStateStore(directory.Path);
        Directory.CreateDirectory(store.UpdatesDirectory);
        File.WriteAllText(store.StatePath, "{not json");
        Assert.False(store.TryRead(out var state, out var diagnostic));
        Assert.Null(state);
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void Lock_contention_times_out()
    {
        using var directory = new TempDirectory();
        var store = new ReleaseUpdateStateStore(directory.Path);
        using (store.AcquireLock(TimeSpan.FromMilliseconds(50)))
        {
            Assert.Throws<TimeoutException>(() => store.AcquireLock(TimeSpan.FromMilliseconds(150)));
        }
    }
}

public sealed class ReleaseInstallationInspectorTests
{
    [Fact]
    public void Npm_global_layout_is_recognized_with_the_package_version()
    {
        using var directory = new TempDirectory();
        var package = System.IO.Path.Combine(directory.Path, "node_modules", "omnicore-cli");
        var npm = System.IO.Path.Combine(package, "dist", "npm");
        Directory.CreateDirectory(npm);
        File.WriteAllText(System.IO.Path.Combine(package, "package.json"),
            """{"name":"omnicore-cli","version":"0.1.0"}""");
        var installation = new ReleaseInstallationInspector(npm, "npm.cmd", "node.exe").Inspect();
        Assert.Equal(ReleaseInstallKind.NpmGlobal, installation.Kind);
        Assert.Equal(package, installation.PackageDirectory);
        Assert.Equal("0.1.0", installation.InstalledVersion);
        Assert.Equal("npm.cmd", installation.NpmPath);
        Assert.Equal("node.exe", installation.NodePath);
        Assert.Equal(directory.Path, installation.NpmPrefix);
    }

    [Fact]
    public void Unix_npm_prefix_layout_is_derived_as_prefix_above_lib_node_modules()
    {
        using var directory = new TempDirectory();
        var package = System.IO.Path.Combine(directory.Path, "lib", "node_modules", "omnicore-cli");
        var npm = System.IO.Path.Combine(package, "dist", "npm");
        Directory.CreateDirectory(npm);
        File.WriteAllText(System.IO.Path.Combine(package, "package.json"),
            """{"name":"omnicore-cli","version":"0.1.0"}""");
        var installation = new ReleaseInstallationInspector(npm, "npm", "node").Inspect();
        Assert.Equal(directory.Path, installation.NpmPrefix);
    }

    [Fact]
    public void Development_checkout_is_recognized_from_the_repository_root()
    {
        using var directory = new TempDirectory();
        var repository = System.IO.Path.Combine(directory.Path, "repo");
        var binary = System.IO.Path.Combine(repository, "src", "OmniCore.Cli", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(System.IO.Path.Combine(repository, ".git"));
        File.WriteAllText(System.IO.Path.Combine(repository, "OmniCore.slnx"), string.Empty);
        File.WriteAllText(System.IO.Path.Combine(repository, "package.json"),
            """{"name":"omnicore-cli","version":"0.1.0"}""");
        var installation = new ReleaseInstallationInspector(binary).Inspect();
        Assert.Equal(ReleaseInstallKind.DevelopmentCheckout, installation.Kind);
        Assert.Equal(repository, installation.PackageDirectory);
    }

    [Fact]
    public void Unrecognized_layout_stays_unknown()
    {
        using var directory = new TempDirectory();
        var installation = new ReleaseInstallationInspector(directory.Path).Inspect();
        Assert.Equal(ReleaseInstallKind.Unknown, installation.Kind);
        Assert.Equal("install_kind_unknown", installation.Detail);
    }
}

public sealed class ReleaseUpdateApplierTests
{
    [Fact]
    public async Task Apply_backs_up_the_installation_and_prepares_the_helper()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        var launcher = new FakeLauncher();
        var applier = new ReleaseUpdateApplier(store, new FakeInspector(package, "0.1.0"),
            new FakeDownloader(), launcher, publisherVerifier: new FakePublisher());
        var result = await applier.ApplyAsync(Check(), confirmed: true, TestContext.Current.CancellationToken);
        Assert.True(result.Prepared, result.DiagnosticCode);
        Assert.NotNull(result.State);
        Assert.Equal(ReleaseUpdateOperationStatus.Pending, result.State!.Status);
        Assert.Equal(int.MaxValue, result.State.HelperPid);
        Assert.Equal("apply", launcher.Request!.Mode);
        Assert.True(Directory.Exists(result.State.BackupPath));
        Assert.True(File.Exists(System.IO.Path.Combine(result.State.BackupPath!, "package.json")));
        Assert.True(store.TryRead(out var persisted, out _));
        Assert.Equal(ReleaseUpdateOperationStatus.Pending, persisted!.Status);
        Assert.Equal(Check().Asset!.Sha256, persisted.ExpectedSha256);
        Assert.Equal(Check().Asset!.Size, persisted.ExpectedSize);
        Assert.Equal(directory.Path, persisted.NpmPrefix);
        Assert.Equal(persisted.NpmPrefix, launcher.Request!.NpmPrefix);
        Assert.Equal(store.RecoveryHelperPath(persisted.OperationId!), launcher.Request.HelperPath);
        Assert.Equal(result.State.RecoveryCommand, persisted.RecoveryCommand);
        Assert.Contains("--mode recover", persisted.RecoveryCommand);
        Assert.Contains("--yes true", persisted.RecoveryCommand);
        Directory.Delete(package, recursive: true);
        Assert.True(File.Exists(launcher.Request.HelperPath));
        Assert.True(File.Exists(System.IO.Path.Combine(persisted.BackupPath!, "package.json")));
    }

    [Fact]
    public async Task Apply_uses_a_fresh_backup_even_when_same_version_backup_exists()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        var staleBackup = store.BackupDirectory("0.1.0");
        Directory.CreateDirectory(staleBackup);
        File.WriteAllText(System.IO.Path.Combine(staleBackup, "package.json"), "stale backup");
        var result = await new ReleaseUpdateApplier(store, new FakeInspector(package, "0.1.0"),
            new FakeDownloader(), new FakeLauncher(), publisherVerifier: new FakePublisher()).ApplyAsync(Check(), confirmed: true, TestContext.Current.CancellationToken);
        Assert.True(result.Prepared, result.DiagnosticCode);
        Assert.NotEqual(staleBackup, result.State!.BackupPath);
        Assert.Equal("""{"name":"omnicore-cli","version":"0.1.0"}""",
            File.ReadAllText(System.IO.Path.Combine(result.State.BackupPath!, "package.json")));
    }

    [Fact]
    public async Task Apply_requires_explicit_confirmation()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var applier = new ReleaseUpdateApplier(new ReleaseUpdateStateStore(directory.Path),
            new FakeInspector(package, "0.1.0"), new FakeDownloader(), new FakeLauncher());
        var result = await applier.ApplyAsync(Check(), confirmed: false, TestContext.Current.CancellationToken);
        Assert.False(result.Prepared);
        Assert.Equal("update_confirmation_required", result.DiagnosticCode);
    }

    [Fact]
    public async Task Development_checkout_is_refused_before_downloading()
    {
        using var directory = new TempDirectory();
        var applier = new ReleaseUpdateApplier(new ReleaseUpdateStateStore(directory.Path),
            new FakeInspector(directory.Path, "0.1.0", ReleaseInstallKind.DevelopmentCheckout),
            new FakeDownloader(), new FakeLauncher());
        var result = await applier.ApplyAsync(Check(), confirmed: true, TestContext.Current.CancellationToken);
        Assert.False(result.Prepared);
        Assert.Equal("update_development_checkout", result.DiagnosticCode);
    }

    [Fact]
    public async Task Prefix_that_does_not_match_package_layout_is_refused_before_download()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var downloader = new CountingDownloader();
        var applier = new ReleaseUpdateApplier(new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data")),
            new FakeInspector(package, "0.1.0", npmPrefix: System.IO.Path.Combine(directory.Path, "different")),
            downloader, new FakeLauncher());
        var result = await applier.ApplyAsync(Check(), confirmed: true, TestContext.Current.CancellationToken);
        Assert.False(result.Prepared);
        Assert.Equal("update_prefix_mismatch", result.DiagnosticCode);
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task Rollback_uses_the_backed_up_previous_version()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        var launcher = new FakeLauncher();
        var applier = new ReleaseUpdateApplier(store, new FakeInspector(package, "0.1.0"),
            new FakeDownloader(), launcher, publisherVerifier: new FakePublisher());
        Assert.True((await applier.ApplyAsync(Check(), confirmed: true,
            TestContext.Current.CancellationToken)).Prepared);
        Assert.True(store.TryRead(out var pending, out _));
        store.Write(pending! with { Status = ReleaseUpdateOperationStatus.Succeeded, ParentPid = null, HelperPid = null });
        var result = applier.Rollback(confirmed: true);
        Assert.True(result.Prepared, result.DiagnosticCode);
        Assert.Equal("rollback", launcher.Request!.Mode);
        Assert.NotNull(launcher.Request.BackupPath);
        Assert.Equal(ReleaseUpdateOperation.Rollback, result.State!.Operation);
    }

    [Fact]
    public void Rollback_without_a_recorded_backup_is_refused()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var applier = new ReleaseUpdateApplier(new ReleaseUpdateStateStore(directory.Path),
            new FakeInspector(package, "0.1.0"), new FakeDownloader(), new FakeLauncher());
        var result = applier.Rollback(confirmed: true);
        Assert.False(result.Prepared);
        Assert.Equal("update_rollback_unavailable", result.DiagnosticCode);
    }

    [Fact]
    public async Task Orphaned_pending_apply_is_marked_unknown_and_never_retried()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        store.Write(new(ReleaseUpdateStateStore.SchemaVersion, ReleaseUpdateOperation.Apply,
            ReleaseUpdateOperationStatus.Pending, "0.1.0", "0.2.0", "asset.tgz", "backup",
            int.MaxValue, null, DateTimeOffset.UtcNow, "waiting_for_parent_exit", new string('c', 32)));
        var downloader = new CountingDownloader();
        var applier = new ReleaseUpdateApplier(store, new FakeInspector(package, "0.1.0"), downloader, new FakeLauncher());
        var result = await applier.ApplyAsync(Check(), confirmed: true, TestContext.Current.CancellationToken);
        Assert.False(result.Prepared);
        Assert.Equal("update_operation_unknown", result.DiagnosticCode);
        Assert.Equal(0, downloader.Calls);
        Assert.True(store.TryRead(out var state, out _));
        Assert.Equal(ReleaseUpdateOperationStatus.Unknown, state!.Status);
    }

    [Fact]
    public async Task Pending_operation_with_live_parent_and_missing_helper_pid_is_not_retried()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        store.Write(new(ReleaseUpdateStateStore.SchemaVersion, ReleaseUpdateOperation.Apply,
            ReleaseUpdateOperationStatus.Pending, "0.1.0", "0.2.0", "asset.tgz", "backup",
            Environment.ProcessId, null, DateTimeOffset.UtcNow, "waiting_for_parent_exit", new string('e', 32)));
        var downloader = new CountingDownloader();
        var result = await new ReleaseUpdateApplier(store, new FakeInspector(package, "0.1.0"),
            downloader, new FakeLauncher()).ApplyAsync(Check(), confirmed: true, TestContext.Current.CancellationToken);
        Assert.False(result.Prepared);
        Assert.Equal("update_operation_in_progress", result.DiagnosticCode);
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task Apply_with_live_npm_pid_is_refused_without_marking_unknown_or_downloading()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        var operationId = new string('f', 32);
        store.Write(new(ReleaseUpdateStateStore.SchemaVersion, ReleaseUpdateOperation.Apply,
            ReleaseUpdateOperationStatus.Pending, "0.1.0", "0.2.0", "asset.tgz", "backup",
            null, null, DateTimeOffset.UtcNow, "waiting_for_parent_exit", operationId,
            null, null, directory.Path, null, Environment.ProcessId));
        var downloader = new CountingDownloader();

        var result = await new ReleaseUpdateApplier(store, new FakeInspector(package, "0.1.0"),
            downloader, new FakeLauncher()).ApplyAsync(Check(), confirmed: true, TestContext.Current.CancellationToken);

        Assert.False(result.Prepared);
        Assert.Equal("update_operation_in_progress", result.DiagnosticCode);
        Assert.Equal(0, downloader.Calls);
        Assert.True(store.TryRead(out var state, out _));
        Assert.Equal(ReleaseUpdateOperationStatus.Pending, state!.Status);
        Assert.Equal(Environment.ProcessId, state.NpmPid);
    }

    [Fact]
    public void Rollback_with_live_npm_pid_is_refused_without_replacing_the_operation()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var backup = System.IO.Path.Combine(directory.Path, "backup-live-npm");
        Directory.CreateDirectory(backup);
        File.WriteAllText(System.IO.Path.Combine(backup, "package.json"),
            """{"name":"omnicore-cli","version":"0.1.0"}""");
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        var operationId = new string('a', 32);
        var backupHash = ReleaseUpdateApplier.PackageFingerprint(backup);
        store.Write(new(ReleaseUpdateStateStore.SchemaVersion, ReleaseUpdateOperation.Apply,
            ReleaseUpdateOperationStatus.Unknown, "0.1.0", "0.2.0", null, backup,
            null, null, DateTimeOffset.UtcNow, "helper_outcome_unknown", operationId,
            null, null, directory.Path, backupHash, Environment.ProcessId));
        var launcher = new FakeLauncher();

        var result = new ReleaseUpdateApplier(store, new FakeInspector(package, "0.2.0"),
            new FakeDownloader(), launcher).Rollback(confirmed: true);

        Assert.False(result.Prepared);
        Assert.Equal("update_operation_in_progress", result.DiagnosticCode);
        Assert.Null(launcher.Request);
        Assert.True(store.TryRead(out var state, out _));
        Assert.Equal(ReleaseUpdateOperationStatus.Unknown, state!.Status);
        Assert.Equal(operationId, state.OperationId);
        Assert.Equal(Environment.ProcessId, state.NpmPid);
    }

    [Fact]
    public void Rollback_refuses_unverified_backup_after_unknown_operation()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var backup = System.IO.Path.Combine(directory.Path, "backup");
        Directory.CreateDirectory(backup);
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        store.Write(new(ReleaseUpdateStateStore.SchemaVersion, ReleaseUpdateOperation.Apply,
            ReleaseUpdateOperationStatus.Unknown, "0.1.0", "0.2.0", "asset.tgz", backup,
            null, null, DateTimeOffset.UtcNow, "helper_outcome_unknown", new string('d', 32),
            null, null, System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(package))!));
        var result = new ReleaseUpdateApplier(store, new FakeInspector(package, "0.1.0"),
            new FakeDownloader(), new FakeLauncher()).Rollback(confirmed: true);
        Assert.False(result.Prepared);
        Assert.Equal("update_backup_unverified", result.DiagnosticCode);
    }

    [Fact]
    public void Rollback_reports_a_backup_symlink_as_unverified_when_windows_allows_fixture_creation()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This fixture verifies Windows package-backup link handling.");
            return;
        }

        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var backup = System.IO.Path.Combine(directory.Path, "backup-with-link");
        Directory.CreateDirectory(backup);
        File.WriteAllText(System.IO.Path.Combine(backup, "package.json"),
            """{"name":"omnicore-cli","version":"0.1.0"}""");
        var outside = System.IO.Path.Combine(directory.Path, "outside.txt");
        File.WriteAllText(outside, "outside backup tree");
        try
        {
            File.CreateSymbolicLink(System.IO.Path.Combine(backup, "outside-link.txt"), outside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("The Windows environment does not permit symbolic-link fixture creation.");
            return;
        }

        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        store.Write(new(ReleaseUpdateStateStore.SchemaVersion, ReleaseUpdateOperation.Apply,
            ReleaseUpdateOperationStatus.Unknown, "0.1.0", "0.2.0", null, backup,
            null, null, DateTimeOffset.UtcNow, "helper_outcome_unknown", new string('b', 32),
            null, null, directory.Path, new string('c', 64)));

        var result = new ReleaseUpdateApplier(store, new FakeInspector(package, "0.2.0"),
            new FakeDownloader(), new FakeLauncher()).Rollback(confirmed: true);

        Assert.False(result.Prepared);
        Assert.Equal("update_backup_unverified", result.DiagnosticCode);
    }

    [Fact]
    public async Task Unverified_publisher_cannot_create_a_backup_or_launch_activation()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        var launcher = new FakeLauncher();
        var applier = new ReleaseUpdateApplier(store, new FakeInspector(package, "0.1.0"),
            new FakeDownloader(), launcher, publisherVerifier: new FakePublisher(false));
        var result = await applier.ApplyAsync(Check(), true, TestContext.Current.CancellationToken);
        Assert.False(result.Prepared);
        Assert.Equal("update_publisher_verification_failed", result.DiagnosticCode);
        Assert.Null(launcher.Request);
        Assert.False(Directory.Exists(System.IO.Path.Combine(store.UpdatesDirectory, "backup")));
        Assert.False(File.Exists(store.StatePath));
        Assert.Equal("""{"name":"omnicore-cli","version":"0.1.0"}""", File.ReadAllText(System.IO.Path.Combine(package, "package.json")));
    }

    [Fact]
    public void Explicit_rollback_can_compensate_unknown_activation_only_with_verified_backup_and_dead_matching_lock()
    {
        using var directory = new TempDirectory();
        var package = CreatePackage(directory.Path);
        var backup = System.IO.Path.Combine(directory.Path, "backup"); Directory.CreateDirectory(backup);
        File.WriteAllText(System.IO.Path.Combine(backup, "package.json"), """{"name":"omnicore-cli","version":"0.1.0"}""");
        var store = new ReleaseUpdateStateStore(System.IO.Path.Combine(directory.Path, "data"));
        var id = new string('e', 32);
        store.Write(new(1, ReleaseUpdateOperation.Apply, ReleaseUpdateOperationStatus.Unknown,
            "0.1.0", "0.2.0", null, backup, int.MaxValue, int.MaxValue, DateTimeOffset.UtcNow, "interrupted",
            id, null, null, directory.Path, ReleaseUpdateApplier.PackageFingerprint(backup)));
        File.WriteAllText(store.StatePath + ".helper.lock", System.Text.Json.JsonSerializer.Serialize(new
            { pid = int.MaxValue, operation = "Apply", operationId = id, statePath = store.StatePath }));
        var launcher = new FakeLauncher();
        var result = new ReleaseUpdateApplier(store, new FakeInspector(package, "0.2.0"), new FakeDownloader(), launcher).Rollback(true);
        Assert.True(result.Prepared, result.DiagnosticCode);
        Assert.Equal("rollback", launcher.Request!.Mode);
        Assert.Equal("0.1.0", launcher.Request.ExpectedVersion);
        Assert.Equal(package, launcher.Request.PackageDirectory);
        Assert.True(File.Exists(store.StatePath + ".helper.lock.abandoned-" + id));
        Assert.False(File.Exists(store.StatePath + ".helper.lock"));
    }

    private static string CreatePackage(string root)
    {
        var package = System.IO.Path.Combine(root, "node_modules", "omnicore-cli");
        Directory.CreateDirectory(System.IO.Path.Combine(package, "bin"));
        File.WriteAllText(System.IO.Path.Combine(package, "package.json"),
            """{"name":"omnicore-cli","version":"0.1.0"}""");
        File.WriteAllText(System.IO.Path.Combine(package, "bin", "update-helper.js"), "// helper");
        return package;
    }

    private static ReleaseUpdateCheckResult Check() => new(ReleaseUpdateChannel.Stable,
        ReleaseUpdateStatus.UpdateAvailable, ReleaseCompatibility.Compatible, "0.1.0", "0.2.0", "v0.2.0",
        CommitSha: new string('b', 40),
        Asset: new(42, "omnicore-cli-0.2.0.tgz", 128, new string('a', 64), "win-x64"));

    private sealed class FakeInspector(string packageDirectory, string version,
        ReleaseInstallKind kind = ReleaseInstallKind.NpmGlobal, string? npmPrefix = null) : IReleaseInstallationInspector
    {
        public ReleaseInstallation Inspect() => new(kind, packageDirectory, "npm.cmd", "node.exe", version,
            "npm_prefix_layout", npmPrefix ?? System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(packageDirectory))!);
    }

    private sealed class FakeDownloader : IReleaseUpdateDownloader
    {
        public Task<ReleaseUpdateDownloadResult> DownloadAsync(ReleaseUpdateAsset asset, string tag,
            string destinationDirectory, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(destinationDirectory);
            var path = System.IO.Path.Combine(destinationDirectory, asset.Name);
            File.WriteAllBytes(path, [1, 2, 3]);
            return Task.FromResult(new ReleaseUpdateDownloadResult(ReleaseUpdateDownloadStatus.Downloaded,
                "release_asset_downloaded", path, asset.Sha256, 3));
        }
    }

    private sealed class FakePublisher(bool verified = true) : IReleasePublisherVerifier
    {
        public Task<ReleasePublisherVerification> VerifyAsync(string archivePath, ReleaseUpdateCheckResult release,
            CancellationToken cancellationToken) => Task.FromResult(new ReleasePublisherVerification(verified,
                verified ? "update_publisher_verified" : "update_publisher_verification_failed"));
    }

    private sealed class CountingDownloader : IReleaseUpdateDownloader
    {
        public int Calls { get; private set; }
        public Task<ReleaseUpdateDownloadResult> DownloadAsync(ReleaseUpdateAsset asset, string tag,
            string destinationDirectory, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReleaseUpdateDownloadResult(ReleaseUpdateDownloadStatus.AssetUnavailable));
        }
    }

    private sealed class FakeLauncher : IReleaseUpdateHelperLauncher
    {
        public ReleaseUpdateHelperRequest? Request { get; private set; }

        public int Launch(ReleaseUpdateHelperRequest request)
        {
            Request = request;
            return int.MaxValue;
        }
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omnicore-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
