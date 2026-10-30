namespace OmniCore.Host;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Protocol;

public sealed record ReleasePublisherVerification(bool Verified, string DiagnosticCode);

public interface IReleasePublisherVerifier
{
    Task<ReleasePublisherVerification> VerifyAsync(string archivePath, ReleaseUpdateCheckResult release,
        CancellationToken cancellationToken);
}

/// <summary>Uses a pinned official GitHub verifier, not release-controlled executable code.
/// The downloaded bundle is untrusted until Sigstore verification and repository policy succeed.</summary>
public sealed class GitHubReleasePublisherVerifier : IReleasePublisherVerifier, IDisposable
{
    public const string VerifierVersion = "2.103.3";
    public const string VerifierSha256 = "d303d8ac3f5e9438bf7c977b64dd6425513eae46218f8881d30694f28c1d0dfd";
    public const string RepositoryId = "1386758159";
    public const string Workflow = "juancgomezm-s/OmniCore/.github/workflows/release.yml";
    private const int MaximumBundleBytes = 8 * 1024 * 1024;
    private readonly string _verifier;
    private readonly HttpClient _http;

    public GitHubReleasePublisherVerifier(string? verifierPath = null, HttpMessageHandler? handler = null)
    {
        _verifier = verifierPath ?? Path.Combine(AppContext.BaseDirectory, "verifier", "gh.exe");
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ReleasePublisherVerification> VerifyAsync(string archivePath, ReleaseUpdateCheckResult release,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return new(false, "update_publisher_verifier_unavailable");
        if (release.Asset is not { } asset || release.Tag is not { Length: > 0 } tag
            || release.CommitSha is not { Length: 40 } commit || !commit.All(Uri.IsHexDigit)
            || tag.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+'))
            || asset.Name != Path.GetFileName(asset.Name) || asset.Name.Contains('\\')
            || string.IsNullOrWhiteSpace(asset.Name))
            return new(false, "update_publisher_identity_invalid");
        string verifierDigest;
        FileStream? executable = null;
        try
        {
            if (!File.Exists(_verifier) || HasReparseAncestor(_verifier))
                return new(false, "update_publisher_verifier_unavailable");
            executable = new FileStream(_verifier, FileMode.Open, FileAccess.Read, FileShare.Read);
            verifierDigest = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(executable, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { executable?.Dispose(); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            executable?.Dispose();
            return new(false, "update_publisher_verifier_unavailable");
        }
        // Keep the verified executable locked against replacement until both checks finish.
        using var executableLease = executable;
        if (verifierDigest != VerifierSha256)
            return new(false, "update_publisher_verifier_invalid");

        var folder = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        var bundlePath = Path.Combine(folder, "publisher-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var manifestPath = bundlePath + ".manifest.json";
        var manifestBundle = bundlePath + ".manifest.jsonl";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await DownloadBundle(tag, asset.Name + ".sigstore.jsonl", bundlePath, linked.Token).ConfigureAwait(false);
            if (!await VerifyFile(archivePath, bundlePath, commit, tag, asset.Sha256, folder, linked.Token).ConfigureAwait(false))
                return new(false, "update_publisher_verification_failed");
            await DownloadBundle(tag, GitHubReleaseUpdateChecker.ManifestAssetName, manifestPath, linked.Token, 64 * 1024).ConfigureAwait(false);
            await DownloadBundle(tag, GitHubReleaseUpdateChecker.ManifestAssetName + ".sigstore.jsonl", manifestBundle, linked.Token).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(manifestPath, linked.Token).ConfigureAwait(false);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!await VerifyFile(manifestPath, manifestBundle, commit, tag, digest, folder, linked.Token).ConfigureAwait(false))
                return new(false, "update_publisher_verification_failed");
            var manifest = ReleaseManifest.Parse(bytes);
            var selected = manifest.Assets.SingleOrDefault(item => item.Rid == asset.Rid);
            if (manifest.Repository != GitHubReleaseUpdateChecker.Repository || manifest.Tag != tag
                || manifest.CommitSha != commit || manifest.Version != release.AvailableVersion
                || selected is null || selected.Name != asset.Name || selected.Size != asset.Size || selected.Sha256 != asset.Sha256
                || release.Requirements != new ReleaseUpdateRequirements(selected.Requirements.DotNetMin, selected.Requirements.DotNetMax,
                    selected.Requirements.NodeMin, selected.Requirements.NodeMax, selected.Requirements.ProtocolMin, selected.Requirements.ProtocolMax,
                    selected.Requirements.ConfigurationMin, selected.Requirements.ConfigurationMax))
                return new(false, "update_publisher_identity_invalid");
            return new(true, "update_publisher_verified");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException
            or JsonException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception
            or GitHubReleaseUpdateChecker.ManifestException)
        {
            return new(false, "update_publisher_verification_failed");
        }
        finally
        {
            foreach (var file in new[] { bundlePath, manifestPath, manifestBundle })
                try { if (File.Exists(file)) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<bool> VerifyFile(string archivePath, string bundlePath, string commit, string tag,
        string expectedDigest, string folder, CancellationToken token)
    {
            var start = new ProcessStartInfo(_verifier)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var value in new[] { "attestation", "verify", archivePath, "--bundle", bundlePath,
                "--repo", GitHubReleaseUpdateChecker.Repository, "--signer-workflow", Workflow,
                "--signer-digest", commit, "--source-digest", commit, "--source-ref", "refs/tags/" + tag,
                "--deny-self-hosted-runners", "--predicate-type", "https://slsa.dev/provenance/v1", "--format", "json" })
                start.ArgumentList.Add(value);
            // Public verification must not receive model credentials or use the user's gh login.
            start.Environment.Clear();
            foreach (var key in new[] { "SystemRoot", "WINDIR", "USERPROFILE", "HOME", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP",
                "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment["GH_CONFIG_DIR"] = Path.Combine(folder, "publisher-verifier-config");
            start.Environment["GH_PROMPT_DISABLED"] = "1";
            start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
            start.Environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1";
            using var process = Process.Start(start);
            if (process is null) return false;
            try
            {
                var output = ReadBounded(process.StandardOutput, 1024 * 1024, token);
                var errors = ReadBounded(process.StandardError, 64 * 1024, token);
                await Task.WhenAll(output, errors, process.WaitForExitAsync(token)).ConfigureAwait(false);
                return process.ExitCode == 0 && VerifiedRepository(output.Result, expectedDigest);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
    }

    public static bool VerifiedRepository(string verifiedOutput, string expectedDigest)
    {
        using var document = JsonDocument.Parse(verifiedOutput, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
        foreach (var result in document.RootElement.EnumerateArray())
        {
            if (!result.TryGetProperty("verificationResult", out var verification)
                || !verification.TryGetProperty("signature", out var signature)
                || !signature.TryGetProperty("certificate", out var certificate)
                || !certificate.TryGetProperty("sourceRepositoryIdentifier", out var identity)
                || identity.GetString() != RepositoryId
                || !verification.TryGetProperty("statement", out var statement)
                || !statement.TryGetProperty("subject", out var subjects) || subjects.ValueKind != JsonValueKind.Array) continue;
            if (subjects.EnumerateArray().Any(subject => subject.TryGetProperty("digest", out var digest)
                && digest.TryGetProperty("sha256", out var sha) && sha.GetString() == expectedDigest)) return true;
        }
        return false;
    }

    private async Task DownloadBundle(string tag, string name, string destination, CancellationToken token, int maximumBytes = MaximumBundleBytes)
    {
        var uri = new Uri("https://github.com/" + GitHubReleaseUpdateChecker.Repository + "/releases/download/"
            + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("OmniCore-Updater/1.0");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is not { } location) throw new InvalidDataException("Missing attestation redirect.");
                var next = new Uri(uri, location);
                if (next.Scheme != "https" || next.Port != 443 || next.UserInfo.Length != 0
                    || next.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("Invalid attestation redirect.");
                uri = next; continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Attestation too large.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[8192]; var total = 0; int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += count; if (total > maximumBytes) throw new InvalidDataException("Attestation too large.");
                await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            return;
        }
        throw new InvalidDataException("Too many attestation redirects.");
    }

    private static async Task<string> ReadBounded(StreamReader reader, int maximum, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > maximum) throw new InvalidDataException("Verifier output exceeds its bound.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static bool HasReparseAncestor(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    public void Dispose() => _http.Dispose();
}
