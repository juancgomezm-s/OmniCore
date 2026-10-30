namespace OmniCore.Host;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OmniCore.Protocol;

/// <summary>Bounded download of a published asset with size, digest and package identity checks.</summary>
public sealed class ReleaseUpdateDownloader : IDisposable, IReleaseUpdateDownloader
{
    public const long MaximumAssetBytes = 512L * 1024 * 1024;
    public const int MaximumRedirects = 3;
    private const int MaximumPackageManifestBytes = 1024 * 1024;
    private static readonly Uri ApiRoot = new("https://api.github.com/");
    private static readonly HashSet<string> AssetRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    { "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github.com" };
    private readonly HttpClient _http;

    public ReleaseUpdateDownloader(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ReleaseUpdateDownloadResult> DownloadAsync(ReleaseUpdateAsset asset, string tag,
        string destinationDirectory, string expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        if (asset.Id <= 0 || asset.Size is < 1 or > MaximumAssetBytes || !ValidSha(asset.Sha256)
            || string.IsNullOrWhiteSpace(asset.Rid) || asset.Rid.Length > 80
            || string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 240
            || Path.GetFileName(asset.Name) != asset.Name || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || tag.Length > 160 || tag.Any(char.IsControl))
            return new(ReleaseUpdateDownloadStatus.AssetUnavailable, "release_asset_invalid");
        try
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(ReleaseUpdateDownloadStatus.StagingFailed, "update_staging_unavailable");
        }

        var finalPath = Path.Combine(destinationDirectory, asset.Name);
        var temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var current = new Uri(ApiRoot, $"repos/{GitHubReleaseUpdateChecker.Repository}/releases/assets/{asset.Id}");
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = CreateRequest(current);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (RateLimit(response) is { } retry)
                    return new(ReleaseUpdateDownloadStatus.RateLimited, "github_rate_limited", RetryAfterUtc: retry);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (redirect == MaximumRedirects || response.Headers.Location is not { } location
                        || !Uri.TryCreate(current, location, out var next) || !AllowedAssetRedirect(next, tag))
                        return new(ReleaseUpdateDownloadStatus.RedirectRejected, "release_asset_redirect_rejected");
                    current = next;
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new(ReleaseUpdateDownloadStatus.AssetUnavailable, "release_asset_not_found");
                if (!response.IsSuccessStatusCode)
                    return new(ReleaseUpdateDownloadStatus.NetworkError, "release_asset_unavailable");
                if (response.Content.Headers.ContentLength is { } declared && declared != asset.Size)
                    return new(ReleaseUpdateDownloadStatus.SizeMismatch, "release_asset_size_mismatch");
                var (failure, digest) = await WriteVerifiedAsync(response.Content, temporaryPath, asset, cancellationToken)
                    .ConfigureAwait(false);
                if (failure is not null) return failure;
                if (!string.Equals(digest, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(ReleaseUpdateDownloadStatus.DigestMismatch, "release_asset_digest_mismatch");
                if (!await PackageIdentityMatchesAsync(temporaryPath, expectedVersion, cancellationToken).ConfigureAwait(false))
                    return new(ReleaseUpdateDownloadStatus.PackageIdentityMismatch, "release_package_identity_mismatch");
                File.Move(temporaryPath, finalPath, overwrite: true);
                return new(ReleaseUpdateDownloadStatus.Downloaded, "release_asset_downloaded",
                    finalPath, digest, asset.Size);
            }
            return new(ReleaseUpdateDownloadStatus.RedirectRejected, "release_asset_redirect_rejected");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(ReleaseUpdateDownloadStatus.Cancelled, "release_download_cancelled"); }
        catch (HttpRequestException) { return new(ReleaseUpdateDownloadStatus.Offline, "github_offline"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(ReleaseUpdateDownloadStatus.StagingFailed, "update_staging_failed");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return new(ReleaseUpdateDownloadStatus.PackageIdentityMismatch, "release_package_invalid");
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<(ReleaseUpdateDownloadResult? Result, string? Sha256)> WriteVerifiedAsync(
        HttpContent content, string path, ReleaseUpdateAsset asset, CancellationToken token)
    {
        await using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumAssetBytes)
                return (new(ReleaseUpdateDownloadStatus.PayloadTooLarge, "release_asset_too_large"), null);
            if (total > asset.Size)
                return (new(ReleaseUpdateDownloadStatus.SizeMismatch, "release_asset_size_mismatch"), null);
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        await target.FlushAsync(token).ConfigureAwait(false);
        if (total != asset.Size)
            return (new(ReleaseUpdateDownloadStatus.SizeMismatch, "release_asset_size_mismatch"), null);
        return (null, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<bool> PackageIdentityMatchesAsync(string path, string expectedVersion,
        CancellationToken token)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: token).ConfigureAwait(false) is { } entry)
        {
            if (!SafeEntryName(entry.Name)) return false;
            if (entry.Name is not "package/package.json" || entry.DataStream is null) continue;
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await entry.DataStream.ReadAsync(chunk, token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumPackageManifestBytes) return false;
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || name.GetString() != "omnicore-cli"
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || version.GetString() is not { } versionText)
                return false;
            return SemVer.TryParse(versionText, out var packageVersion)
                && SemVer.TryParse(expectedVersion, out var wanted)
                && SemVerComparer.Instance.Compare(packageVersion, wanted) == 0;
        }
        return false;
    }

    private static bool SafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512 || name.Contains('\0')) return false;
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) return false;
        return normalized.Split('/').All(part => part is not "..");
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("api.github.com" or "release-assets.githubusercontent.com"
            or "objects.githubusercontent.com" or "github.com"))
            throw new HttpRequestException("release_asset_origin_invalid");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.UserAgent.ParseAdd("OmniCore-Update/1.0 (+https://github.com/juancgomezm-s/OmniCore)");
        if (uri.Host == "api.github.com") request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static bool AllowedAssetRedirect(Uri uri, string tag) => uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo) && uri.Port == 443
        && string.IsNullOrEmpty(uri.Fragment)
        && (AssetRedirectHosts.Contains(uri.Host) && !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(uri.Query)
                && uri.AbsolutePath.StartsWith("/" + GitHubReleaseUpdateChecker.Repository + "/releases/download/"
                    + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal));

    private static DateTimeOffset? RateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
        {
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
                && long.TryParse(reset.FirstOrDefault(), out var unix))
                return DateTimeOffset.FromUnixTimeSeconds(unix);
            return DateTimeOffset.UtcNow.AddMinutes(1);
        }
        if (response.Headers.RetryAfter?.Date is { } date) return date;
        if (response.Headers.RetryAfter?.Delta is { } delta) return DateTimeOffset.UtcNow + delta;
        return (int)response.StatusCode == 429 ? DateTimeOffset.UtcNow.AddMinutes(1) : null;
    }

    private static bool ValidSha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public void Dispose() => _http.Dispose();
}
