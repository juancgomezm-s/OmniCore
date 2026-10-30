namespace OmniCore.Tests;

using System.Security.Cryptography;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed class ReleaseStartupUpdatesTests
{
    [Fact]
    public async Task Preparation_verifies_but_never_installs_until_a_later_launch_is_chosen()
    {
        using var fx = new Fixture();
        var ready = await fx.Ready();
        Assert.Equal("Ready", ready.Phase);
        Assert.False(ready.Release!.PublisherVerificationPending);
        Assert.Equal(1, fx.Publisher.Calls); Assert.Equal(0, fx.Helper.Calls);
        Assert.Equal("continue", (await fx.Service.LaunchAsync(fx.First, false, fx.Token)).Action);
        Assert.True(fx.Service.Choose(ready.Revision, "NextLaunch", fx.First).Accepted);
        Assert.Equal("continue", (await fx.Service.LaunchAsync(fx.First, false, fx.Token)).Action);
        Assert.Equal(0, fx.Helper.Calls);
        var launch = await fx.Service.LaunchAsync(fx.Second, false, fx.Token);
        Assert.Equal("wait", launch.Action); Assert.Equal(1, fx.Helper.Calls);
        Assert.Equal(2, fx.Publisher.Calls); // Reauthenticated at activation, not a cached trust flag.
        Assert.Equal(1, fx.Downloads);
        Assert.True(fx.Operations.TryRead(out var operation, out _));
        fx.Operations.Write(operation! with { Status = ReleaseUpdateOperationStatus.Succeeded });
        fx.Inspector.Version = "0.2.0";
        Assert.Equal("continue", (await fx.Service.LaunchAsync(fx.Second, false, fx.Token)).Action);
        Assert.Equal("Applied", fx.Service.Snapshot()!.Phase);
        Assert.Equal(1, fx.Helper.Calls);
    }

    [Fact]
    public async Task Immediate_choice_is_bound_to_origin_launch_and_preserves_resume_context()
    {
        using var fx = new Fixture(); var ready = await fx.Ready();
        var session = Guid.NewGuid().ToString();
        Assert.True(fx.Service.Choose(ready.Revision, "Restart", fx.First, fx.Root, session).Accepted);
        Assert.Equal("continue", (await fx.Service.LaunchAsync(fx.Second, true, fx.Token)).Action);
        Assert.Equal("continue", (await fx.Service.LaunchAsync(fx.First, false, fx.Token)).Action);
        Assert.Equal("wait", (await fx.Service.LaunchAsync(fx.First, true, fx.Token)).Action);
        fx.Operations.TryRead(out var op, out _);
        fx.Operations.Write(op! with { Status = ReleaseUpdateOperationStatus.Succeeded });
        fx.Inspector.Version = "0.2.0";
        var resumed = await fx.Service.LaunchAsync(fx.First, true, fx.Token);
        Assert.Equal(fx.Root, resumed.Workspace); Assert.Equal(session, resumed.SessionId);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("compatibility")]
    [InlineData("publisher")]
    public async Task Changed_conditions_refuse_activation_without_effect(string changed)
    {
        using var fx = new Fixture(); var ready = await fx.Ready();
        Assert.True(fx.Service.Choose(ready.Revision, "NextLaunch", fx.First).Accepted);
        switch (changed)
        {
            case "archive": File.WriteAllBytes(ready.PackagePath!, [9, 9, 9]); break;
            case "channel": fx.Service.SavePreferences(new(Channel: ReleaseUpdateChannel.Preview)); break;
            case "version": fx.Inspector.Version = "0.1.1"; break;
            case "compatibility": fx.Facts = fx.Facts with { NodeVersion = "1.0" }; break;
            case "publisher": fx.Publisher.Verified = false; break;
        }
        Assert.Equal("continue", (await fx.Service.LaunchAsync(fx.Second, false, fx.Token)).Action);
        Assert.Equal(0, fx.Helper.Calls);
    }

    [Fact]
    public async Task Disabled_download_and_preview_selection_change_behavior_without_implicit_channel_switch()
    {
        using var fx = new Fixture();
        fx.Service.SavePreferences(new(AutomaticDownload: false));
        Assert.Null(await fx.Service.PrepareAsync(fx.Token)); Assert.Equal(0, fx.Downloads);
        fx.Service.SavePreferences(new(Channel: ReleaseUpdateChannel.Preview));
        var ready = await fx.Ready(); Assert.Equal(ReleaseUpdateChannel.Preview, fx.Request!.Channel);
        Assert.Equal(ReleaseUpdateChannel.Preview, ready.Channel);
    }

    [Fact]
    public async Task Offline_or_unverified_preparation_keeps_installation_and_decision_unchanged()
    {
        using var fx = new Fixture();
        fx.Offline = true;
        Assert.Equal("Idle", (await fx.Service.PrepareAsync(fx.Token))!.Phase);
        Assert.Equal(0, fx.Downloads); Assert.Equal(0, fx.Helper.Calls);
        fx.Offline = false; fx.Publisher.Verified = false;
        Assert.Equal("Failed", (await fx.Service.PrepareAsync(fx.Token))!.Phase);
        Assert.False(fx.Service.Choose(fx.Service.Snapshot()!.Revision, "NextLaunch", fx.First).Accepted);
        Assert.Equal("0.1.0", fx.Inspector.Version); Assert.Equal(0, fx.Helper.Calls);
    }

    [Fact]
    public async Task Active_instance_in_another_data_profile_blocks_activation_then_allows_a_later_launch()
    {
        using var fx = new Fixture(); var ready = await fx.Ready();
        var other = new ReleaseUpdateInstances(new(Path.Combine(fx.Root, "other-profile")), fx.Root);
        using (other.AcquireGate()) { }
        var file = Directory.GetDirectories(Path.Combine(fx.Root, ".omnicore-updates", "instances")).Single();
        var leasePath = Path.Combine(file, "999-" + Guid.NewGuid().ToString("N") + ".lease");
        using (var lease = new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(fx.Service.Choose(ready.Revision, "Restart", fx.First).Accepted);
            Assert.True(fx.Service.Choose(ready.Revision, "NextLaunch", fx.First).Accepted);
            Assert.Equal("continue", (await fx.Service.LaunchAsync(fx.Second, false, fx.Token)).Action);
            Assert.Equal(0, fx.Helper.Calls);
        }
        Assert.Equal("wait", (await fx.Service.LaunchAsync(fx.Second, false, fx.Token)).Action);
        Assert.Equal(1, fx.Helper.Calls);
        Assert.Null(other.TryEnter(out var diagnostic)); Assert.NotNull(diagnostic);
    }

    [Fact]
    public async Task Unknown_activation_never_replays_or_opens_a_session()
    {
        using var fx = new Fixture(); var ready = await fx.Ready();
        fx.Service.Choose(ready.Revision, "NextLaunch", fx.First);
        await fx.Service.LaunchAsync(fx.Second, false, fx.Token);
        fx.Operations.TryRead(out var op, out _);
        fx.Operations.Write(op! with { Status = ReleaseUpdateOperationStatus.Unknown });
        Assert.Equal("blocked", (await fx.Service.LaunchAsync(fx.Second, false, fx.Token)).Action);
        Assert.Equal("blocked", (await fx.Service.LaunchAsync(fx.Second, false, fx.Token)).Action);
        Assert.Equal(1, fx.Helper.Calls);
    }

    [Fact]
    public async Task Read_only_runtime_fallback_never_bypasses_a_pending_unknown_or_corrupt_operation()
    {
        using var fx = new Fixture();
        var instances = new ReleaseUpdateInstances(fx.Operations, fx.Root);
        Assert.True(instances.CanRunReadOnly());
        var ready = await fx.Ready(); fx.Service.Choose(ready.Revision, "NextLaunch", fx.First);
        await fx.Service.LaunchAsync(fx.Second, false, fx.Token);
        fx.Operations.TryRead(out var op, out _);
        Assert.False(instances.CanRunReadOnly());
        fx.Operations.Write(op! with { Status = ReleaseUpdateOperationStatus.Unknown });
        Assert.False(instances.CanRunReadOnly());
        fx.Operations.Write(op! with { Status = ReleaseUpdateOperationStatus.Failed });
        Assert.True(instances.CanRunReadOnly());
        File.WriteAllText(fx.Operations.StatePath, "{corrupt");
        Assert.False(instances.CanRunReadOnly());
    }

    [Fact]
    public async Task Stale_revision_and_corrupt_state_do_not_authorize_a_replacement()
    {
        using var fx = new Fixture(); await fx.Ready();
        Assert.False(fx.Service.Choose(Guid.NewGuid().ToString("N"), "Restart", fx.First).Accepted);
        Assert.False(fx.Service.Choose(fx.Service.Snapshot()!.Revision, "Restart", "invalid").Accepted);
        File.WriteAllText(fx.Operations.StatePath, "{broken");
        Assert.Equal("blocked", (await fx.Service.LaunchAsync(fx.Second, false, fx.Token)).Action);
        Assert.Equal(0, fx.Helper.Calls);
    }

    [Fact]
    public async Task Successful_helper_with_wrong_installed_version_is_not_treated_as_applied()
    {
        using var fx = new Fixture(); var ready = await fx.Ready();
        fx.Service.Choose(ready.Revision, "NextLaunch", fx.First);
        await fx.Service.LaunchAsync(fx.Second, false, fx.Token);
        fx.Operations.TryRead(out var op, out _);
        fx.Operations.Write(op! with { Status = ReleaseUpdateOperationStatus.Succeeded });
        Assert.Equal("blocked", (await fx.Service.LaunchAsync(fx.Second, false, fx.Token)).Action);
        Assert.Equal("Installing", fx.Service.Snapshot()!.Phase);
    }

    [Fact]
    public async Task Preparation_cancellation_records_no_authorized_effect()
    {
        using var fx = new Fixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fx.Service.PrepareAsync(cancellation.Token));
        Assert.Equal("Failed", fx.Service.Snapshot()!.Phase);
        Assert.Equal(0, fx.Helper.Calls);
        Assert.False(fx.Service.Choose(fx.Service.Snapshot()!.Revision, "Restart", fx.First).Accepted);
    }

    [Fact]
    public async Task Interrupted_download_and_permission_failure_never_become_ready()
    {
        using var fx = new Fixture(); fx.DownloadFails = true;
        Assert.Equal("Failed", (await fx.Service.PrepareAsync(fx.Token))!.Phase);
        Assert.Equal(0, fx.Publisher.Calls); Assert.Equal(0, fx.Helper.Calls);
        fx.DownloadFails = false; fx.DownloadDenied = true;
        Assert.Equal("Failed", (await fx.Service.PrepareAsync(fx.Token))!.Phase);
        Assert.Equal(0, fx.Publisher.Calls); Assert.Equal(0, fx.Helper.Calls);
    }

    [Fact]
    public async Task Concurrent_preparation_keeps_the_original_human_decision_and_does_not_check_again()
    {
        using var fx = new Fixture(); var ready = await fx.Ready();
        fx.Service.Choose(ready.Revision, "NextLaunch", fx.First);
        Assert.Equal(ready.Revision, (await fx.Service.PrepareAsync(fx.Token))!.Revision);
        Assert.Equal(1, fx.Downloads); Assert.Equal(1, fx.Publisher.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_cli_bootstrap_runs_before_user_configuration_or_session_initialization(bool invalidId)
    {
        using var directory = new TempDirectory();
        var config = Path.Combine(directory.Path, "config"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.yaml"), "[ deliberately invalid: yaml");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory.Path,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        start.ArgumentList.Add(typeof(OmniCore.Cli.CliApp).Assembly.Location);
        foreach (var argument in new[] { "update", "startup", "launch", invalidId ? "invalid" : Guid.NewGuid().ToString("N") })
            start.ArgumentList.Add(argument);
        var inherited = new Dictionary<string, string?>();
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "USERPROFILE", "HOME", "TEMP", "TMP", "DOTNET_ROOT" })
            inherited[key] = Environment.GetEnvironmentVariable(key);
        start.Environment.Clear();
        foreach (var pair in inherited) if (pair.Value is not null) start.Environment[pair.Key] = pair.Value;
        start.Environment["OMNICORE_DATA_DIR"] = Path.Combine(directory.Path, "data");
        start.Environment["OMNICORE_CONFIG_DIR"] = config;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            Assert.Equal(0, process.ExitCode);
            using var json = System.Text.Json.JsonDocument.Parse(await output);
            Assert.Equal(invalidId ? "blocked" : "continue", json.RootElement.GetProperty("action").GetString());
            Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.db*", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.sqlite*", SearchOption.AllDirectories));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _directory = new();
        internal string Root => _directory.Path;
        internal readonly string First = Guid.NewGuid().ToString("N"), Second = Guid.NewGuid().ToString("N");
        internal CancellationToken Token => TestContext.Current.CancellationToken;
        internal ReleaseStartupUpdates Service { get; }
        internal ReleaseUpdateStateStore Operations { get; }
        internal Inspector Inspector { get; }
        internal Publisher Publisher { get; } = new();
        internal Helper Helper { get; } = new();
        internal int Downloads;
        internal bool Offline;
        internal bool DownloadFails, DownloadDenied;
        internal ReleaseUpdateCheckRequest? Request;
        internal ReleaseUpdateEnvironment Facts = new("win-x64", "10.0.1", "24.0.0", "1.0", "1");
        internal Fixture(bool development = false)
        {
            var package = Path.Combine(Root, "node_modules", "omnicore-cli");
            Directory.CreateDirectory(Path.Combine(package, "bin"));
            File.WriteAllText(Path.Combine(package, "package.json"), """{"name":"omnicore-cli","version":"0.1.0"}""");
            File.WriteAllText(Path.Combine(package, "bin", "update-helper.js"), "// fixture");
            Inspector = new(package, Root);
            Operations = new(Path.Combine(Root, "data"));
            Service = new(Path.Combine(Root, "data"), Path.Combine(Root, "config"), Inspector,
                new Download(this), Publisher, Helper, (request, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Request = request;
                    return Task.FromResult(new ReleaseUpdateCheckResult(request.Channel,
                        Offline ? ReleaseUpdateStatus.Offline : ReleaseUpdateStatus.UpdateAvailable,
                        ReleaseCompatibility.Compatible, "0.1.0", "0.2.0", "v0.2.0", new string('a', 40),
                        Requirements: new("10.0", "10.99", "20.0", "99.0", "1.0", "1.99", "1", "1.99"),
                        Asset: new(42, "package.tgz", 3, Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), "win-x64")));
                }, () => Facts, development: development);
        }
        internal async Task<StartupUpdateSnapshot> Ready() => (await Service.PrepareAsync(Token))!;
        public void Dispose() { Service.Dispose(); _directory.Dispose(); }
    }

    internal sealed class Inspector(string package, string prefix) : IReleaseInstallationInspector
    {
        internal string Version = "0.1.0";
        public ReleaseInstallation Inspect() => new(ReleaseInstallKind.NpmGlobal, package, "npm", "node", Version, NpmPrefix: prefix);
    }
    internal sealed class Publisher : IReleasePublisherVerifier
    {
        internal int Calls; internal bool Verified = true;
        public Task<ReleasePublisherVerification> VerifyAsync(string archivePath, ReleaseUpdateCheckResult release,
            CancellationToken cancellationToken) { Calls++; return Task.FromResult(new ReleasePublisherVerification(Verified, "fixture")); }
    }
    internal sealed class Helper : IReleaseUpdateHelperLauncher
    {
        internal int Calls;
        public int Launch(ReleaseUpdateHelperRequest request) { Calls++; return int.MaxValue; }
    }
    private sealed class Download(Fixture fixture) : IReleaseUpdateDownloader
    {
        public Task<ReleaseUpdateDownloadResult> DownloadAsync(ReleaseUpdateAsset asset, string tag,
            string destinationDirectory, string expectedVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fixture.DownloadDenied) throw new UnauthorizedAccessException("fixture");
            if (fixture.DownloadFails) return Task.FromResult(new ReleaseUpdateDownloadResult(ReleaseUpdateDownloadStatus.NetworkError));
            fixture.Downloads++; Directory.CreateDirectory(destinationDirectory);
            var path = Path.Combine(destinationDirectory, asset.Name); File.WriteAllBytes(path, [1, 2, 3]);
            return Task.FromResult(new ReleaseUpdateDownloadResult(ReleaseUpdateDownloadStatus.Downloaded, Path: path));
        }
    }
}
