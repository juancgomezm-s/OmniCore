using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ReleaseUpdateCheckerTests
{
    private static ReleaseUpdateCheckRequest Request(ReleaseUpdateChannel channel = ReleaseUpdateChannel.Stable,
        string installed = "1.0.0", bool development = false, ReleaseUpdateEnvironment? environment = null) =>
        new(channel, installed, development, environment ?? new("win-x64", "10.0.1"));

    [Theory]
    [InlineData("59")]
    [InlineData("0")]
    public async Task Successful_responses_with_routine_reset_headers_are_not_rejected(string remaining)
    {
        using var checker = new GitHubReleaseUpdateChecker(new Handler(_ =>
        {
            var response = Json(HttpStatusCode.OK, "[]");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1793150000");
            return response;
        }));
        var result = await checker.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.NoPublishedRelease, result.Status);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    public async Task Out_of_range_reset_headers_preserve_rate_limit_classification(string reset)
    {
        using var checker = new GitHubReleaseUpdateChecker(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);
            return response;
        }));
        var result = await checker.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.RateLimited, result.Status);
        Assert.NotNull(result.RetryAfterUtc);
    }

    [Fact]
    public async Task Empty_public_catalog_is_distinguished_from_an_unavailable_repository()
    {
        using var empty = new GitHubReleaseUpdateChecker(new Handler(_ => Json(HttpStatusCode.OK, "[]")));
        var noReleases = await empty.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.NoPublishedRelease, noReleases.Status);
        Assert.True(noReleases.CatalogComplete);

        using var unavailable = new GitHubReleaseUpdateChecker(new Handler(_ => new(HttpStatusCode.NotFound)));
        Assert.Equal(ReleaseUpdateStatus.RepositoryUnavailable, (await unavailable.CheckAsync(Request(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Rate_limits_and_malformed_catalogs_return_bounded_diagnostics()
    {
        using var limited = new GitHubReleaseUpdateChecker(new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
            return response;
        }));
        var rate = await limited.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.RateLimited, rate.Status);
        Assert.NotNull(rate.RetryAfterUtc);

        using var malformed = new GitHubReleaseUpdateChecker(new Handler(_ => Json(HttpStatusCode.OK, "[{\"draft\":\"no\"}]")));
        Assert.Equal(ReleaseUpdateStatus.CatalogIncomplete, (await malformed.CheckAsync(Request(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task First_query_discovers_metadata_and_unknown_requirements_never_become_compatible()
    {
        const string sha = "1111111111111111111111111111111111111111";
        const string packageSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var manifest = "{\"schemaVersion\":1,\"repository\":\"juancgomezm-s/OmniCore\",\"version\":\"1.2.3\",\"tag\":\"v1.2.3\",\"commitSha\":\"" + sha + "\",\"assets\":[{\"rid\":\"win-x64\",\"name\":\"omnicore-win-x64.tgz\",\"size\":123,\"sha256\":\"" + packageSha + "\",\"requirements\":{\"dotnetMin\":\"10.0\",\"dotnetMax\":null,\"nodeMin\":null,\"nodeMax\":null,\"protocolMin\":null,\"protocolMax\":null,\"configurationMin\":null,\"configurationMax\":null}}]}";
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/juancgomezm-s/OmniCore/releases" => Json(HttpStatusCode.OK,
                "[{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1.2.3\",\"html_url\":\"https://github.com/juancgomezm-s/OmniCore/releases/tag/v1.2.3\",\"body\":\"Release\\u001b[31m\",\"assets\":[{\"id\":10,\"name\":\"omnicore-release-manifest.json\",\"size\":" + manifestBytes.Length + "},{\"id\":11,\"name\":\"omnicore-win-x64.tgz\",\"size\":123,\"digest\":\"sha256:" + packageSha + "\"}]}]"),
            "/repos/juancgomezm-s/OmniCore/git/ref/tags/v1.2.3" => Json(HttpStatusCode.OK,
                "{\"ref\":\"refs/tags/v1.2.3\",\"object\":{\"type\":\"commit\",\"sha\":\"" + sha + "\"}}"),
            "/repos/juancgomezm-s/OmniCore/releases/assets/10" => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(manifestBytes) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var checker = new GitHubReleaseUpdateChecker(handler);
        var result = await checker.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.CompatibilityUnknown, result.Status);
        Assert.Equal(ReleaseCompatibility.Unknown, result.Compatibility);
        Assert.Equal(sha, result.CommitSha);
        Assert.Equal(123, result.AssetSize);
        Assert.DoesNotContain('\u001b', result.Notes ?? "");
        Assert.True(result.MetadataOnly && result.ArchiveVerificationPending && result.PublisherVerificationPending);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task Body_read_is_covered_by_the_overall_deadline()
    {
        using var checker = new GitHubReleaseUpdateChecker(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new SlowContent() }), TimeSpan.FromMilliseconds(30));
        var result = await checker.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.NetworkError, result.Status);
        Assert.Equal("github_timeout", result.DiagnosticCode);
    }

    [Fact]
    public async Task Manifest_version_identity_includes_build_metadata()
    {
        const string sha = "1111111111111111111111111111111111111111";
        var manifest = "{\"schemaVersion\":1,\"repository\":\"juancgomezm-s/OmniCore\",\"version\":\"1.2.3+two\",\"tag\":\"v1.2.3+one\",\"commitSha\":\"" + sha + "\",\"assets\":[{\"rid\":\"win-x64\",\"name\":\"package.tgz\",\"size\":123,\"sha256\":\"" + new string('a', 64) + "\",\"requirements\":{}}]}";
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        using var checker = new GitHubReleaseUpdateChecker(new Handler(request =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            return path switch
            {
                "/repos/juancgomezm-s/OmniCore/releases" => Json(HttpStatusCode.OK,
                    "[{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1.2.3+one\",\"html_url\":\"https://github.com/juancgomezm-s/OmniCore/releases/tag/v1.2.3%2Bone\",\"assets\":[{\"id\":10,\"name\":\"omnicore-release-manifest.json\",\"size\":" + manifestBytes.Length + "}]}]"),
                "/repos/juancgomezm-s/OmniCore/git/ref/tags/v1.2.3+one" => Json(HttpStatusCode.OK,
                    "{\"ref\":\"refs/tags/v1.2.3+one\",\"object\":{\"type\":\"commit\",\"sha\":\"" + sha + "\"}}"),
                "/repos/juancgomezm-s/OmniCore/releases/assets/10" => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(manifestBytes) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }));
        var result = await checker.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.ManifestInvalid, result.Status);
        Assert.Equal("release_manifest_identity_mismatch", result.DiagnosticCode);
    }

    [Fact]
    public void Duplicate_manifest_properties_are_rejected_and_update_copy_is_localized()
    {
        var duplicate = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1}");
        Assert.Throws<GitHubReleaseUpdateChecker.ManifestException>(() => ReleaseManifest.Parse(duplicate));
        foreach (var locale in new[] { "es", "en", "fr", "it" })
        foreach (var key in ReleaseUpdateResources.Keys)
            Assert.False(new Localization(locale).ResolveDetailed(key).UsedFallback, $"{locale} missing {key}");
    }

    [Fact]
    public async Task Stable_skips_higher_drafts_while_preview_selects_the_newer_prerelease()
    {
        var stable = Fixture("v1.2.3", false, 1);
        var draft = Fixture("v9.9.9", false, 2, draft: true);
        var preview = Fixture("v2.0.0-rc.1", true, 3);
        using var checker = new GitHubReleaseUpdateChecker(CatalogHandler([stable, draft, preview]));
        var stableResult = await checker.CheckAsync(Request(ReleaseUpdateChannel.Stable), TestContext.Current.CancellationToken);
        var previewResult = await checker.CheckAsync(Request(ReleaseUpdateChannel.Preview), TestContext.Current.CancellationToken);
        Assert.Equal("1.2.3", stableResult.AvailableVersion);
        Assert.Equal("2.0.0-rc.1", previewResult.AvailableVersion);
    }

    [Fact]
    public async Task Compatible_candidate_comparison_distinguishes_up_to_date_and_development_builds()
    {
        var candidate = Fixture("v1.2.3", false, 4, requirements: KnownRequirements);
        using var checker = new GitHubReleaseUpdateChecker(CatalogHandler([candidate]));
        var environment = new ReleaseUpdateEnvironment("win-x64", "10.0.1", "20.11.0", "1.0", "1");
        var update = await checker.CheckAsync(Request(installed: "1.0.0", environment: environment), TestContext.Current.CancellationToken);
        var current = await checker.CheckAsync(Request(installed: "2.0.0", environment: environment), TestContext.Current.CancellationToken);
        var development = await checker.CheckAsync(Request(installed: "1.2.3+commitabc", development: true, environment: environment), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseCompatibility.Compatible, update.Compatibility);
        Assert.Equal(ReleaseUpdateStatus.UpdateAvailable, update.Status);
        Assert.Equal(ReleaseUpdateStatus.UpToDate, current.Status);
        Assert.Equal(ReleaseUpdateStatus.DevelopmentBuild, development.Status);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("tag")]
    [InlineData("commit")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("asset")]
    public async Task Inconsistent_manifest_and_asset_identity_is_rejected(string mutation)
    {
        var fixture = Fixture("v1.2.3", false, 5,
            manifestRepository: mutation == "repository" ? "someone-else/OmniCore" : null,
            manifestTag: mutation == "tag" ? "v1.2.4" : null,
            manifestCommit: mutation == "commit" ? new string('f', 40) : null,
            packageSha: mutation == "hash" ? new string('b', 64) : null,
            packageSize: mutation == "size" ? 124 : 123,
            publishPackage: mutation != "asset");
        using var checker = new GitHubReleaseUpdateChecker(CatalogHandler([fixture]));
        var result = await checker.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task Off_origin_manifest_redirect_and_oversized_catalog_are_rejected()
    {
        var fixture = Fixture("v1.2.3", false, 6);
        using var redirected = new GitHubReleaseUpdateChecker(CatalogHandler([fixture], _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/manifest.json");
            return response;
        }));
        Assert.Equal(ReleaseUpdateStatus.ManifestInvalid,
            (await redirected.CheckAsync(Request(), TestContext.Current.CancellationToken)).Status);
        using var crossRepositoryRedirect = new GitHubReleaseUpdateChecker(CatalogHandler([fixture], _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://github.com/other/repository/releases/download/v1.2.3/manifest.json");
            return response;
        }));
        Assert.Equal(ReleaseUpdateStatus.ManifestInvalid,
            (await crossRepositoryRedirect.CheckAsync(Request(), TestContext.Current.CancellationToken)).Status);

        using var oversizedManifest = new GitHubReleaseUpdateChecker(CatalogHandler([fixture], _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GitHubReleaseUpdateChecker.MaximumManifestBytes + 1]) }));
        Assert.Equal("release_manifest_too_large",
            (await oversizedManifest.CheckAsync(Request(), TestContext.Current.CancellationToken)).DiagnosticCode);

        using var oversized = new GitHubReleaseUpdateChecker(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[GitHubReleaseUpdateChecker.MaximumListBytes + 1]) }));
        Assert.Equal("github_payload_too_large",
            (await oversized.CheckAsync(Request(), TestContext.Current.CancellationToken)).DiagnosticCode);
    }

    [Fact]
    public async Task Catalog_page_cap_and_caller_cancellation_are_observable()
    {
        var pageRequests = 0;
        using var pages = new GitHubReleaseUpdateChecker(new Handler(_ =>
        {
            pageRequests++;
            var response = Json(HttpStatusCode.OK, "[" + string.Join(",", Enumerable.Range(0, 100).Select(index =>
                "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1." + (pageRequests - 1) + "." + index + "\",\"html_url\":\"https://github.com/juancgomezm-s/OmniCore/releases/tag/v1." + (pageRequests - 1) + "." + index + "\",\"assets\":[]}")) + "]");
            response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/juancgomezm-s/OmniCore/releases?page=" + (pageRequests + 1) + ">; rel=\"next\"");
            return response;
        }));
        var capped = await pages.CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseUpdateStatus.CatalogIncomplete, capped.Status);
        Assert.Equal(3, pageRequests);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waiting = new GitHubReleaseUpdateChecker(new Handler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var callerCancellation = new CancellationTokenSource();
        var pending = waiting.CheckAsync(Request(), callerCancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData("1.2.3\n", false)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2.3-01", false)]
    [InlineData("1.2.3+build.4", true)]
    public void Semver_is_strict_and_build_metadata_is_valid(string value, bool valid) =>
        Assert.Equal(valid, SemVer.TryParse(value, out _));

    [Fact]
    public void Long_numeric_prerelease_identifiers_keep_semver_numeric_ordering()
    {
        Assert.True(SemVerComparer.Instance.Compare(Parse("1.0.0-99999999999999999999"), Parse("1.0.0-1a")) < 0);
        Assert.True(SemVerComparer.Instance.Compare(Parse("1.0.0-99999999999999999999"), Parse("1.0.0-9999999999")) > 0);
        Assert.Equal(0, SemVerComparer.Instance.Compare(Parse("1.0.0+one"), Parse("1.0.0+two")));
    }

    private static SemVer Parse(string text) => SemVer.TryParse(text, out var version) ? version! : throw new InvalidOperationException();
    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string KnownRequirements = "\"dotnetMin\":\"10.0\",\"dotnetMax\":\"10.99\",\"nodeMin\":\"20.0\",\"nodeMax\":\"20.99\",\"protocolMin\":\"1.0\",\"protocolMax\":\"1.99\",\"configurationMin\":\"1\",\"configurationMax\":\"1.99\"";
    private sealed record ReleaseFixture(string Tag, string Commit, int ManifestId, byte[] Manifest, string ReleaseJson);

    private static ReleaseFixture Fixture(string tag, bool prerelease, int id, bool draft = false,
        string? manifestRepository = null, string? manifestTag = null, string? manifestVersion = null,
        string? manifestCommit = null, string? packageSha = null, int packageSize = 123,
        bool publishPackage = true, string? requirements = null)
    {
        var commit = new string("123456789abcdef"[id % 15], 40);
        var packageDigest = packageSha ?? new string('a', 64);
        var packageName = "omnicore-win-x64.tgz";
        var semver = tag.StartsWith('v') ? tag[1..] : tag;
        var manifest = "{\"schemaVersion\":1,\"repository\":\"" + (manifestRepository ?? "juancgomezm-s/OmniCore")
            + "\",\"version\":\"" + (manifestVersion ?? semver) + "\",\"tag\":\"" + (manifestTag ?? tag)
            + "\",\"commitSha\":\"" + (manifestCommit ?? commit) + "\",\"assets\":[{\"rid\":\"win-x64\",\"name\":\""
            + packageName + "\",\"size\":" + packageSize + ",\"sha256\":\"" + packageDigest
            + "\",\"requirements\":{" + (requirements ?? "\"dotnetMin\":null,\"dotnetMax\":null,\"nodeMin\":null,\"nodeMax\":null,\"protocolMin\":null,\"protocolMax\":null,\"configurationMin\":null,\"configurationMax\":null") + "}}]}";
        var bytes = Encoding.UTF8.GetBytes(manifest);
        var manifestId = 100 + id * 2; var packageId = manifestId + 1;
        var assets = "{\"id\":" + manifestId + ",\"name\":\"omnicore-release-manifest.json\",\"size\":" + bytes.Length + "}"
            + (publishPackage ? ",{\"id\":" + packageId + ",\"name\":\"" + packageName + "\",\"size\":123,\"digest\":\"sha256:" + new string('a', 64) + "\"}" : "");
        var urlTag = Uri.EscapeDataString(tag);
        var row = "{\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (prerelease ? "true" : "false")
            + ",\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/juancgomezm-s/OmniCore/releases/tag/" + urlTag
            + "\",\"assets\":[" + assets + "]}";
        return new(tag, commit, manifestId, bytes, row);
    }

    private static Handler CatalogHandler(IReadOnlyList<ReleaseFixture> releases,
        Func<ReleaseFixture, HttpResponseMessage>? overrideManifest = null) => new(request =>
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        if (path == "/repos/juancgomezm-s/OmniCore/releases") return Json(HttpStatusCode.OK, "[" + string.Join(',', releases.Select(item => item.ReleaseJson)) + "]");
        var tag = releases.FirstOrDefault(item => path == "/repos/juancgomezm-s/OmniCore/git/ref/tags/" + item.Tag);
        if (tag is not null) return Json(HttpStatusCode.OK, "{\"ref\":\"refs/tags/" + tag.Tag + "\",\"object\":{\"type\":\"commit\",\"sha\":\"" + tag.Commit + "\"}}");
        var asset = releases.FirstOrDefault(item => path == "/repos/juancgomezm-s/OmniCore/releases/assets/" + item.ManifestId);
        if (asset is not null) return overrideManifest?.Invoke(asset) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(asset.Manifest) };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((request, _) => Task.FromResult(respond(request))) { }
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return _respond(request, cancellationToken); }
    }

    private sealed class SlowContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowStream());
        private sealed class SlowStream : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        }
    }
}
