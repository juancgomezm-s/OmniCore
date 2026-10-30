namespace OmniCore.Tests;

using System.Net;
using System.Text;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed class ReleasePublisherVerificationTests
{
    [Theory]
    [InlineData("1386758159", true, true)]
    [InlineData("another-repository", true, false)]
    [InlineData("1386758159", false, false)]
    public void Verified_tool_output_is_still_bound_to_repository_id_and_archive_digest(string id, bool matchingDigest, bool expected)
    {
        var digest = new string('a', 64);
        var json = "[{\"verificationResult\":{\"signature\":{\"certificate\":{\"sourceRepositoryIdentifier\":\"" + id
            + "\"}},\"statement\":{\"subject\":[{\"digest\":{\"sha256\":\"" + (matchingDigest ? digest : new string('b', 64))
            + "\"}}]}}}]";
        Assert.Equal(expected, GitHubReleasePublisherVerifier.VerifiedRepository(json, digest));
    }

    [Fact]
    public async Task Release_cannot_supply_its_own_verifier_or_bypass_the_compiled_binary_pin()
    {
        using var directory = new TempDirectory();
        var fake = Path.Combine(directory.Path, "gh.exe");
        File.WriteAllText(fake, "untrusted verifier");
        var handler = new NeverDownload();
        using var verifier = new GitHubReleasePublisherVerifier(fake, handler);
        var release = new ReleaseUpdateCheckResult(ReleaseUpdateChannel.Stable, ReleaseUpdateStatus.UpdateAvailable,
            ReleaseCompatibility.Compatible, "0.1.0", "0.2.0", "v0.2.0", new string('c', 40),
            Asset: new(42, "package.tgz", 128, new string('a', 64), "win-x64"));
        var result = await verifier.VerifyAsync(Path.Combine(directory.Path, "package.tgz"), release, TestContext.Current.CancellationToken);
        Assert.False(result.Verified);
        Assert.Equal(OperatingSystem.IsWindows() ? "update_publisher_verifier_invalid" : "update_publisher_verifier_unavailable", result.DiagnosticCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task File_access_failure_while_hashing_the_pinned_verifier_returns_a_diagnostic()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Assert.Skip("Pinned verifier file-sharing behavior is Windows x64 specific.");
            return;
        }

        using var directory = new TempDirectory();
        var pinnedPath = Path.Combine(directory.Path, "gh.exe");
        File.WriteAllText(pinnedPath, "pinned verifier fixture");
        using var locked = new FileStream(pinnedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var verifier = new GitHubReleasePublisherVerifier(pinnedPath, new NeverDownload());
        var release = new ReleaseUpdateCheckResult(ReleaseUpdateChannel.Stable, ReleaseUpdateStatus.UpdateAvailable,
            ReleaseCompatibility.Compatible, "0.1.0", "0.2.0", "v0.2.0", new string('c', 40),
            Asset: new(42, "package.tgz", 128, new string('a', 64), "win-x64"));

        var result = await verifier.VerifyAsync(Path.Combine(directory.Path, "package.tgz"), release,
            TestContext.Current.CancellationToken);

        Assert.False(result.Verified);
        Assert.Equal("update_publisher_verifier_unavailable", result.DiagnosticCode);
    }

    [Fact]
    public void Backup_fingerprint_changes_for_mutated_added_and_missing_files()
    {
        using var directory = new TempDirectory();
        var file = Path.Combine(directory.Path, "payload.dll"); File.WriteAllText(file, "original");
        var original = ReleaseUpdateApplier.PackageFingerprint(directory.Path);
        File.WriteAllText(file, "modified");
        Assert.NotEqual(original, ReleaseUpdateApplier.PackageFingerprint(directory.Path));
        File.WriteAllText(file, "original");
        Assert.Equal(original, ReleaseUpdateApplier.PackageFingerprint(directory.Path));
        var extra = Path.Combine(directory.Path, "extra"); File.WriteAllText(extra, "extra");
        Assert.NotEqual(original, ReleaseUpdateApplier.PackageFingerprint(directory.Path));
        File.Delete(extra); File.Delete(file);
        Assert.NotEqual(original, ReleaseUpdateApplier.PackageFingerprint(directory.Path));
    }

    private sealed class NeverDownload : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8) });
        }
    }
}
