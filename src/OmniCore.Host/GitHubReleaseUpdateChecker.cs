namespace OmniCore.Host;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Protocol;

/// <summary>Bounded, anonymous, manual metadata lookup for the official public Releases API.</summary>
public sealed class GitHubReleaseUpdateChecker : IDisposable
{
    public const string Repository = "juancgomezm-s/OmniCore";
    public const string ManifestAssetName = "omnicore-release-manifest.json";
    public const int MaximumReleasePages = 3;
    public const int MaximumReleasesPerPage = 100;
    public const int MaximumListBytes = 2 * 1024 * 1024;
    public const int MaximumManifestBytes = 64 * 1024;
    private static readonly Uri ApiRoot = new("https://api.github.com/");
    private static readonly HashSet<string> AssetRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    { "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github.com" };
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public GitHubReleaseUpdateChecker(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<ReleaseUpdateCheckResult> CheckAsync(ReleaseUpdateCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Environment);
        SemVer? wanted = null;
        if (!Enum.IsDefined(request.Channel) || request.InstalledVersion is null
            || !SemVer.TryParse(request.InstalledVersion, out _)
            || request.TargetVersion is { } targetText && !SemVer.TryParse(targetText, out wanted)
            || string.IsNullOrWhiteSpace(request.Environment.Rid) || request.Environment.Rid.Length > 80
            || request.Environment.Rid.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            || string.IsNullOrWhiteSpace(request.Environment.DotNetVersion) || request.Environment.DotNetVersion.Length > 80)
            throw new ArgumentException("Invalid release check request.", nameof(request));
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var page = 1;
        var releases = new List<ReleaseRow>();
        var complete = false;
        try
        {
            for (; page <= MaximumReleasePages; page++)
            {
                token.ThrowIfCancellationRequested();
                var uri = new Uri(ApiRoot, $"repos/{Repository}/releases?per_page={MaximumReleasesPerPage}&page={page}");
                using var response = await GetApiAsync(uri, "application/vnd.github+json", token).ConfigureAwait(false);
                var rateLimit = RateLimit(response);
                if (rateLimit is not null) return Result(request, ReleaseUpdateStatus.RateLimited,
                    "github_rate_limited", retryAfterUtc: rateLimit, releasesExamined: releases.Count);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return Result(request, ReleaseUpdateStatus.RepositoryUnavailable, "github_repository_not_found",
                        releasesExamined: releases.Count);
                if (!response.IsSuccessStatusCode)
                    return Result(request, ReleaseUpdateStatus.NetworkError, "github_api_unavailable",
                        releasesExamined: releases.Count);
                var bytes = await ReadBoundedAsync(response.Content, MaximumListBytes, token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return Result(request, ReleaseUpdateStatus.NetworkError, "github_catalog_invalid", releasesExamined: releases.Count);
                foreach (var value in document.RootElement.EnumerateArray())
                {
                    if (releases.Count >= MaximumReleasePages * MaximumReleasesPerPage)
                        return Result(request, ReleaseUpdateStatus.CatalogIncomplete, "github_catalog_limit",
                            catalogComplete: false, releasesExamined: releases.Count);
                    if (!TryReadRelease(value, out var release))
                        return Result(request, ReleaseUpdateStatus.CatalogIncomplete, "github_release_record_invalid",
                            catalogComplete: false, releasesExamined: releases.Count);
                    if (release is not null) releases.Add(release);
                }
                if (!HasNext(response.Headers)) { complete = true; break; }
            }

            if (!complete)
                return Result(request, ReleaseUpdateStatus.CatalogIncomplete, "github_catalog_limit",
                    catalogComplete: false, releasesExamined: releases.Count);
            var eligible = releases.Where(release => Eligible(release, request.Channel))
                .OrderByDescending(release => release.Version, SemVerComparer.Instance)
                .ThenBy(release => release.Tag, StringComparer.Ordinal).ToArray();
            var latest = wanted is null ? eligible.FirstOrDefault()
                : eligible.FirstOrDefault(release => SemVerComparer.Instance.Compare(release.Version, wanted) == 0);
            if (latest is null)
                return Result(request, ReleaseUpdateStatus.NoPublishedRelease, "no_published_release",
                    releasesExamined: releases.Count);

            var manifestAsset = latest.Assets.SingleOrDefault(asset => asset.Name == ManifestAssetName);
            if (manifestAsset is null)
                return Result(request, ReleaseUpdateStatus.ManifestMissing, "release_manifest_missing",
                    release: latest, releasesExamined: releases.Count);
            if (manifestAsset.Id <= 0 || manifestAsset.Size is < 2 or > MaximumManifestBytes)
                return Result(request, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_asset_invalid",
                    release: latest, releasesExamined: releases.Count);

            var resolvedCommit = await ResolveTagCommitAsync(latest.Tag, token).ConfigureAwait(false);
            if (resolvedCommit.Status is not null)
                return Result(request, resolvedCommit.Status.Value, resolvedCommit.Code!, release: latest,
                    releasesExamined: releases.Count);
            var fetched = await FetchManifestAsync(manifestAsset, latest.Tag, token).ConfigureAwait(false);
            if (fetched.ErrorStatus is { } fetchStatus)
                return Result(request, fetchStatus, fetched.ErrorCode!, release: latest,
                    retryAfterUtc: fetched.RetryAfterUtc, releasesExamined: releases.Count);
            if (fetched.Bytes is null || fetched.Bytes.LongLength != manifestAsset.Size)
                return Result(request, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_size_mismatch",
                    release: latest, releasesExamined: releases.Count);
            if (manifestAsset.Digest is { } digest && !DigestMatches(digest, fetched.Bytes))
                return Result(request, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_digest_mismatch",
                    release: latest, releasesExamined: releases.Count);

            ReleaseManifest manifest;
            try { manifest = ReleaseManifest.Parse(fetched.Bytes); }
            catch (JsonException) { return Result(request, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_json_invalid", release: latest, releasesExamined: releases.Count); }
            if (manifest.Repository != Repository || manifest.Tag != latest.Tag
                || !SemVer.TryParse(manifest.Version, out var manifestVersion)
                || !string.Equals(manifestVersion!.ToString(), latest.Version.ToString(), StringComparison.Ordinal)
                || !string.Equals(manifest.CommitSha, resolvedCommit.CommitSha, StringComparison.OrdinalIgnoreCase))
                return Result(request, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_identity_mismatch",
                    release: latest, commitSha: resolvedCommit.CommitSha, releasesExamined: releases.Count);
            var selected = manifest.Assets.SingleOrDefault(asset => asset.Rid == request.Environment.Rid);
            if (selected is null)
                return Result(request, ReleaseUpdateStatus.NoCompatibleAsset, "release_asset_rid_unavailable",
                    release: latest, commitSha: resolvedCommit.CommitSha, releasesExamined: releases.Count);
            var publishedAsset = latest.Assets.SingleOrDefault(asset => asset.Name == selected.Name);
            if (publishedAsset is null || publishedAsset.Size != selected.Size
                || publishedAsset.Digest is { } packageDigest && !DigestEquals(packageDigest, selected.Sha256))
                return Result(request, ReleaseUpdateStatus.ManifestInvalid, "release_asset_metadata_mismatch",
                    release: latest, commitSha: resolvedCommit.CommitSha, rid: selected.Rid, assetName: selected.Name,
                    assetSize: selected.Size, assetSha256: selected.Sha256, releasesExamined: releases.Count);

            var compatibility = CheckCompatibility(selected.Requirements, request.Environment);
            var comparison = SemVerComparer.Instance.Compare(manifestVersion, InstalledVersion(request.InstalledVersion));
            var status = compatibility.Status == ReleaseCompatibility.Incompatible ? ReleaseUpdateStatus.NoCompatibleAsset
                : request.DevelopmentBuild ? ReleaseUpdateStatus.DevelopmentBuild
                : compatibility.Status == ReleaseCompatibility.Unknown ? ReleaseUpdateStatus.CompatibilityUnknown
                : comparison <= 0 ? ReleaseUpdateStatus.UpToDate : ReleaseUpdateStatus.UpdateAvailable;
            return new(request.Channel, status, compatibility.Status, SafeVersion(request.InstalledVersion),
                manifest.Version, SafeText(latest.Tag, 160), resolvedCommit.CommitSha, latest.Url, latest.Notes,
                selected.Rid, selected.Name, selected.Size, selected.Sha256, compatibility.Detail,
                ReleasesExamined: releases.Count, Requirements: new(selected.Requirements.DotNetMin, selected.Requirements.DotNetMax,
                    selected.Requirements.NodeMin, selected.Requirements.NodeMax, selected.Requirements.ProtocolMin,
                    selected.Requirements.ProtocolMax, selected.Requirements.ConfigurationMin, selected.Requirements.ConfigurationMax),
                Asset: new(publishedAsset.Id, selected.Name, selected.Size, selected.Sha256, selected.Rid));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { return Result(request, ReleaseUpdateStatus.NetworkError, "github_timeout", releasesExamined: releases.Count); }
        catch (RateLimitedException limit)
        { return Result(request, ReleaseUpdateStatus.RateLimited, "github_rate_limited", retryAfterUtc: limit.RetryAfter, releasesExamined: releases.Count); }
        catch (HttpRequestException)
        { return Result(request, ReleaseUpdateStatus.Offline, "github_offline", releasesExamined: releases.Count); }
        catch (PayloadLimitException)
        { return Result(request, ReleaseUpdateStatus.NetworkError, "github_payload_too_large", releasesExamined: releases.Count); }
        catch (JsonException)
        { return Result(request, ReleaseUpdateStatus.NetworkError, "github_catalog_invalid", releasesExamined: releases.Count); }
        catch (ManifestException error)
        { return Result(request, ReleaseUpdateStatus.ManifestInvalid, error.Code, releasesExamined: releases.Count); }
        catch (IOException)
        { return Result(request, ReleaseUpdateStatus.Offline, "github_offline", releasesExamined: releases.Count); }
    }

    private async Task<HttpResponseMessage> GetApiAsync(Uri uri, string accept, CancellationToken token)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.github.com") throw new HttpRequestException("release_api_origin_invalid");
        using var request = CreateRequest(uri, accept, api: true);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (RateLimit(response) is { } retry)
        { response.Dispose(); throw new RateLimitedException(retry); }
        return response;
    }

    private async Task<(byte[]? Bytes, ReleaseUpdateStatus? ErrorStatus, string? ErrorCode, DateTimeOffset? RetryAfterUtc)> FetchManifestAsync(
        ReleaseAsset asset, string tag, CancellationToken token)
    {
        var current = new Uri(ApiRoot, $"repos/{Repository}/releases/assets/{asset.Id}");
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var request = CreateRequest(current, "application/octet-stream", api: current.Host == "api.github.com");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var retry = RateLimit(response);
            if (retry is not null) return (null, ReleaseUpdateStatus.RateLimited, "github_rate_limited", retry);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirect == 3 || response.Headers.Location is not { } location
                    || !Uri.TryCreate(current, location, out var next) || !AllowedAssetRedirect(next, tag))
                    return (null, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_redirect_rejected", null);
                current = next; continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_asset_not_found", null);
            if (!response.IsSuccessStatusCode)
                return (null, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_fetch_failed", null);
            try { return (await ReadBoundedAsync(response.Content, MaximumManifestBytes, token).ConfigureAwait(false), null, null, null); }
            catch (PayloadLimitException) { return (null, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_too_large", null); }
        }
        return (null, ReleaseUpdateStatus.ManifestInvalid, "release_manifest_redirect_rejected", null);
    }

    private async Task<(string? CommitSha, ReleaseUpdateStatus? Status, string? Code)> ResolveTagCommitAsync(string tag, CancellationToken token)
    {
        var encoded = Uri.EscapeDataString(tag);
        var uri = new Uri(ApiRoot, $"repos/{Repository}/git/ref/tags/{encoded}");
        using var response = await GetApiAsync(uri, "application/vnd.github+json", token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (null, ReleaseUpdateStatus.ManifestInvalid, "release_tag_ref_missing");
        if (!response.IsSuccessStatusCode)
            return (null, ReleaseUpdateStatus.NetworkError, "github_api_unavailable");
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 16 * 1024, token).ConfigureAwait(false));
        var root = document.RootElement;
        if (!TryString(root, "ref", 256, out var reference) || reference != "refs/tags/" + tag
            || !root.TryGetProperty("object", out var obj) || !TryString(obj, "type", 32, out var type)
            || !TryString(obj, "sha", 64, out var sha) || !ValidSha(sha))
            return (null, ReleaseUpdateStatus.ManifestInvalid, "release_tag_ref_invalid");
        if (type == "commit") return (sha.ToLowerInvariant(), null, null);
        if (type != "tag") return (null, ReleaseUpdateStatus.ManifestInvalid, "release_tag_ref_invalid");
        using var tagResponse = await GetApiAsync(new Uri(ApiRoot, $"repos/{Repository}/git/tags/{sha}"), "application/vnd.github+json", token).ConfigureAwait(false);
        if (!tagResponse.IsSuccessStatusCode)
            return (null, ReleaseUpdateStatus.ManifestInvalid, "release_tag_object_invalid");
        using var tagDocument = JsonDocument.Parse(await ReadBoundedAsync(tagResponse.Content, 16 * 1024, token).ConfigureAwait(false));
        if (tagDocument.RootElement.ValueKind != JsonValueKind.Object || !tagDocument.RootElement.TryGetProperty("object", out var target)
            || !TryString(target, "type", 32, out var targetType) || targetType != "commit"
            || !TryString(target, "sha", 64, out var commit) || !ValidSha(commit))
            return (null, ReleaseUpdateStatus.ManifestInvalid, "release_tag_object_invalid");
        return (commit.ToLowerInvariant(), null, null);
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string accept, bool api)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd("OmniCore-UpdateCheck/1.0 (+https://github.com/juancgomezm-s/OmniCore)");
        if (api) request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static bool AllowedAssetRedirect(Uri uri, string tag) => uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Port == 443 && string.IsNullOrEmpty(uri.Fragment)
        && (AssetRedirectHosts.Contains(uri.Host) && !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(uri.Query)
                && uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal));

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength is { } length && length > maximum) throw new PayloadLimitException();
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximum, (int)(content.Headers.ContentLength ?? 4096)));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximum) throw new PayloadLimitException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static DateTimeOffset? RateLimit(HttpResponseHeaders headers)
    {
        var exhausted = headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0";
        var now = DateTimeOffset.UtcNow;
        if (headers.RetryAfter?.Date is { } date) return date;
        if (headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero
            && delta <= DateTimeOffset.MaxValue - now) return now + delta;
        if (headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var unix)
            && unix is >= -62_135_596_800 and <= 253_402_300_799)
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        return exhausted ? DateTimeOffset.UtcNow.AddMinutes(1) : null;
    }

    private static bool HasNext(HttpResponseHeaders headers) => headers.TryGetValues("Link", out var values)
        && values.Any(value => Regex.IsMatch(value, @";\s*rel\s*=\s*""?next""?(?:,|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static DateTimeOffset? RateLimit(HttpResponseMessage response)
    {
        // Successful GitHub responses also carry reset headers. A reset timestamp
        // alone never means this request was rejected for rate limiting.
        if ((int)response.StatusCode == 429)
            return RateLimit(response.Headers) ?? DateTimeOffset.UtcNow.AddMinutes(1);
        if ((int)response.StatusCode != 403) return null;
        var exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0";
        return exhausted || response.Headers.RetryAfter is not null
            ? RateLimit(response.Headers) ?? DateTimeOffset.UtcNow.AddMinutes(1) : null;
    }

    private static bool TryReadRelease(JsonElement value, out ReleaseRow? release)
    {
        release = null;
        if (value.ValueKind != JsonValueKind.Object || !TryBool(value, "draft", out var draft)) return false;
        if (draft) return true;
        if (!TryBool(value, "prerelease", out var prerelease)
            || !TryString(value, "tag_name", 160, out var tag)
            || !TryString(value, "html_url", 512, out var htmlUrl)
            || !SemVer.TryParseTag(tag, out var version)) return false;
        var assets = new List<ReleaseAsset>();
        if (!value.TryGetProperty("assets", out var assetRows) || assetRows.ValueKind != JsonValueKind.Array)
            return false;
        if (assetRows.ValueKind == JsonValueKind.Array)
        {
            if (assetRows.GetArrayLength() > 64) return false;
            foreach (var item in assetRows.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !TryLong(item, "id", out var id) || id <= 0
                    || !TryLong(item, "size", out var size) || size < 0
                    || !TryString(item, "name", 240, out var name)) return false;
                string? digest = TryString(item, "digest", 80, out var digestValue) ? digestValue : null;
                assets.Add(new(id, name, size, digest));
            }
        }
        if (assets.Select(asset => asset.Name).Distinct(StringComparer.Ordinal).Count() != assets.Count) return false;
        if (!ValidReleaseUrl(htmlUrl, tag)) return false;
        var html = htmlUrl;
        var notes = value.TryGetProperty("body", out var notesValue) && notesValue.ValueKind == JsonValueKind.String
            && notesValue.GetString() is { Length: <= 100_000 } body ? SafeText(body, 12_000) : "";
        release = new(tag, version!, prerelease, assets, html, notes);
        return true;
    }

    private static bool Eligible(ReleaseRow release, ReleaseUpdateChannel channel) => channel switch
    {
        ReleaseUpdateChannel.Stable => !release.Prerelease && release.Version.Prerelease is null,
        ReleaseUpdateChannel.Preview => true,
        _ => false,
    };

    private static bool ValidReleaseUrl(string value, string tag) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsolutePath == "/" + Repository + "/releases/tag/" + Uri.EscapeDataString(tag);

    private static ReleaseUpdateCheckResult Result(ReleaseUpdateCheckRequest request, ReleaseUpdateStatus status,
        string code, ReleaseRow? release = null, string? commitSha = null, string? rid = null, string? assetName = null,
        long? assetSize = null, string? assetSha256 = null, DateTimeOffset? retryAfterUtc = null,
        bool catalogComplete = true, int releasesExamined = 0) =>
        new(request.Channel, status, ReleaseCompatibility.Unknown, SafeVersion(request.InstalledVersion),
            release is null ? null : release.Version.ToString(), release is null ? null : SafeText(release.Tag, 160),
            commitSha, release?.Url, release?.Notes, rid, assetName, assetSize, assetSha256,
            DiagnosticCode: code, RetryAfterUtc: retryAfterUtc, CatalogComplete: catalogComplete, ReleasesExamined: releasesExamined);

    private static string SafeVersion(string version) => SafeText(version, 120);
    private static string SafeText(string value, int max)
    {
        var clean = new string(value.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());
        return clean.Length <= max ? clean : clean[..max];
    }

    private static bool TryString(JsonElement element, string name, int maximum, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String) return false;
        var text = property.GetString();
        if (text is null || text.Length > maximum || text.Any(character => char.IsControl(character) && character is not '\n' and not '\t')) return false;
        value = text; return true;
    }

    private static bool TryBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean(); return true;
    }
    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }
    private static bool ValidSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool DigestMatches(string digest, byte[] bytes) => digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && string.Equals(digest[7..], Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase);
    private static bool DigestEquals(string digest, string sha256) => digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && string.Equals(digest[7..], sha256, StringComparison.OrdinalIgnoreCase);

    internal static (ReleaseCompatibility Status, string Detail) CheckCompatibility(ReleaseRequirements requirements,
        ReleaseUpdateEnvironment environment)
    {
        var known = false;
        var unknown = false;
        var failures = new List<string>();
        Check(".NET", requirements.DotNetMin, requirements.DotNetMax, environment.DotNetVersion);
        Check("Node", requirements.NodeMin, requirements.NodeMax, environment.NodeVersion);
        Check("protocol", requirements.ProtocolMin, requirements.ProtocolMax, environment.ProtocolVersion);
        Check("configuration", requirements.ConfigurationMin, requirements.ConfigurationMax, environment.ConfigurationVersion);
        return failures.Count > 0 ? (ReleaseCompatibility.Incompatible, "requirements_mismatch")
            : unknown || !known ? (ReleaseCompatibility.Unknown, "requirements_unknown")
            : (ReleaseCompatibility.Compatible, "requirements_match");

        void Check(string label, string? minimum, string? maximum, string? current)
        {
            if (minimum is null && maximum is null) { unknown = true; return; }
            if (current is null || !RuntimeVersion.TryParse(current, out var present)) { unknown = true; return; }
            known = true;
            if (minimum is not null && (!RuntimeVersion.TryParse(minimum, out var min) || present.CompareTo(min) < 0)
                || maximum is not null && (!RuntimeVersion.TryParse(maximum, out var max) || present.CompareTo(max) > 0))
                failures.Add(label + " requirement does not match");
        }
    }

    private static SemVer InstalledVersion(string value) => SemVer.TryParse(value, out var parsed) ? parsed! : new SemVer(0, 0, 0, null, "");

    public void Dispose() => _http.Dispose();

    private sealed record ReleaseRow(string Tag, SemVer Version, bool Prerelease, IReadOnlyList<ReleaseAsset> Assets, string Url, string Notes);
    private sealed record ReleaseAsset(long Id, string Name, long Size, string? Digest);
    private sealed class RateLimitedException(DateTimeOffset? retryAfter) : Exception { public DateTimeOffset? RetryAfter { get; } = retryAfter; }
    private sealed class PayloadLimitException : Exception;
    internal sealed class ManifestException(string code) : Exception { public string Code { get; } = code; }
}

internal sealed record ReleaseManifest(string Repository, string Version, string Tag, string CommitSha,
    IReadOnlyList<ReleaseManifestAsset> Assets)
{
    public static ReleaseManifest Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        var root = document.RootElement;
        if (HasDuplicateProperties(root))
            throw new GitHubReleaseUpdateChecker.ManifestException("release_manifest_duplicate_property");
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1
            || !TryRequired(root, "repository", 160, out var repository)
            || !TryRequired(root, "version", 120, out var version)
            || !TryRequired(root, "tag", 160, out var tag)
            || !TryRequired(root, "commitSha", 64, out var commit)
            || commit.Length != 40 || commit.Any(character => !Uri.IsHexDigit(character))
            || !root.TryGetProperty("assets", out var assetRows) || assetRows.ValueKind != JsonValueKind.Array
            || assetRows.GetArrayLength() is < 1 or > 16)
            throw new GitHubReleaseUpdateChecker.ManifestException("release_manifest_schema_invalid");
        var assets = new List<ReleaseManifestAsset>();
        foreach (var item in assetRows.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryRequired(item, "rid", 80, out var rid)
                || !TryRequired(item, "name", 240, out var name)
                || !item.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number
                || !sizeValue.TryGetInt64(out var size) || size is < 1 or > 512L * 1024 * 1024
                || !TryRequired(item, "sha256", 64, out var sha) || sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character))
                || !item.TryGetProperty("requirements", out var requirementsValue) || requirementsValue.ValueKind != JsonValueKind.Object)
                throw new GitHubReleaseUpdateChecker.ManifestException("release_manifest_asset_invalid");
            if (!TryOptional(requirementsValue, "dotnetMin", out var dotnetMin)
                || !TryOptional(requirementsValue, "dotnetMax", out var dotnetMax)
                || !TryOptional(requirementsValue, "nodeMin", out var nodeMin)
                || !TryOptional(requirementsValue, "nodeMax", out var nodeMax)
                || !TryOptional(requirementsValue, "protocolMin", out var protocolMin)
                || !TryOptional(requirementsValue, "protocolMax", out var protocolMax)
                || !TryOptional(requirementsValue, "configurationMin", out var configMin)
                || !TryOptional(requirementsValue, "configurationMax", out var configMax))
                throw new GitHubReleaseUpdateChecker.ManifestException("release_manifest_requirements_invalid");
            var requirements = new ReleaseRequirements(dotnetMin, dotnetMax, nodeMin, nodeMax,
                protocolMin, protocolMax, configMin, configMax);
            if (!ValidRange(requirements.DotNetMin, requirements.DotNetMax)
                || !ValidRange(requirements.NodeMin, requirements.NodeMax)
                || !ValidRange(requirements.ProtocolMin, requirements.ProtocolMax)
                || !ValidRange(requirements.ConfigurationMin, requirements.ConfigurationMax))
                throw new GitHubReleaseUpdateChecker.ManifestException("release_manifest_requirement_range_invalid");
            assets.Add(new(rid, name, size, sha.ToLowerInvariant(), requirements));
        }
        if (assets.Select(asset => asset.Rid).Distinct(StringComparer.Ordinal).Count() != assets.Count
        || assets.Select(asset => asset.Name).Distinct(StringComparer.Ordinal).Count() != assets.Count)
            throw new GitHubReleaseUpdateChecker.ManifestException("release_manifest_asset_duplicate");
        return new(repository, version, tag, commit.ToLowerInvariant(), assets);
    }

    private static bool TryRequired(JsonElement node, string name, int max, out string value)
    {
        value = "";
        if (!node.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > max || candidate.Any(char.IsControl)) return false;
        value = candidate; return true;
    }
    private static bool ValidRange(string? minimum, string? maximum)
    {
        if (minimum is null || maximum is null) return true;
        return RuntimeVersion.TryParse(minimum, out var min) && RuntimeVersion.TryParse(maximum, out var max)
            && min.CompareTo(max) <= 0;
    }
    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
    private static bool TryOptional(JsonElement node, string name, out string? text)
    {
        text = null;
        if (!node.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 80 } candidate
            || string.IsNullOrWhiteSpace(candidate) || candidate.Any(char.IsControl)) return false;
        text = candidate; return true;
    }
}

internal sealed record ReleaseManifestAsset(string Rid, string Name, long Size, string Sha256, ReleaseRequirements Requirements);
internal sealed record ReleaseRequirements(string? DotNetMin, string? DotNetMax, string? NodeMin, string? NodeMax,
    string? ProtocolMin, string? ProtocolMax, string? ConfigurationMin, string? ConfigurationMax);
internal sealed record ReleaseCompatibilityResult(ReleaseCompatibility Status, string Detail);

internal sealed record RuntimeVersion(int[] Parts) : IComparable<RuntimeVersion>
{
    public static bool TryParse(string value, out RuntimeVersion parsed)
    {
        parsed = new([]);
        var numbers = value.Split('.');
        if (numbers.Length is < 1 or > 4 || numbers.Any(part => part.Length == 0 || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
        parsed = new(numbers.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray()); return true;
    }
    public int CompareTo(RuntimeVersion? other)
    {
        for (var index = 0; index < Math.Max(Parts.Length, other?.Parts.Length ?? 0); index++)
        {
            var left = index < Parts.Length ? Parts[index] : 0; var right = index < (other?.Parts.Length ?? 0) ? other!.Parts[index] : 0;
            if (left != right) return left.CompareTo(right);
        }
        return 0;
    }
}

internal sealed record SemVer(int Major, int Minor, int Patch, string? Prerelease, string Build) : IComparable<SemVer>
{
    private static readonly Regex Pattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public static bool TryParseTag(string value, out SemVer? version) => TryParse(value.StartsWith('v') ? value[1..] : value, out version);
    public static bool TryParse(string value, out SemVer? version)
    {
        version = null; if (value.Length > 120) return false;
        var match = Pattern.Match(value);
        if (!match.Success) return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (prerelease?.Split('.').Any(id => id.Length > 1 && id[0] == '0' && id.All(char.IsAsciiDigit)) == true) return false;
        if (!int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch)) return false;
        version = new(major, minor, patch, prerelease, match.Groups[5].Success ? match.Groups[5].Value : ""); return true;
    }
    public int CompareTo(SemVer? other) => SemVerComparer.Instance.Compare(this, other);
    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);
    public override string ToString() => Major + "." + Minor + "." + Patch + (Prerelease is null ? "" : "-" + Prerelease)
        + (Build.Length == 0 ? "" : "+" + Build);
}

internal sealed class SemVerComparer : IComparer<SemVer>
{
    public static SemVerComparer Instance { get; } = new();
    public int Compare(SemVer? left, SemVer? right)
    {
        if (ReferenceEquals(left, right)) return 0; if (left is null) return -1; if (right is null) return 1;
        var value = left.Major.CompareTo(right.Major); if (value != 0) return value;
        value = left.Minor.CompareTo(right.Minor); if (value != 0) return value;
        value = left.Patch.CompareTo(right.Patch); if (value != 0) return value;
        if (left.Prerelease is null) return right.Prerelease is null ? 0 : 1;
        if (right.Prerelease is null) return -1;
        var leftParts = left.Prerelease.Split('.'); var rightParts = right.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var aNumeric = leftParts[index].All(char.IsAsciiDigit);
            var bNumeric = rightParts[index].All(char.IsAsciiDigit);
            value = aNumeric && bNumeric ? CompareNumericIdentifier(leftParts[index], rightParts[index]) : aNumeric ? -1 : bNumeric ? 1
                : string.CompareOrdinal(leftParts[index], rightParts[index]);
            if (value != 0) return value;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareNumericIdentifier(string left, string right) => left.Length != right.Length
        ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
}
