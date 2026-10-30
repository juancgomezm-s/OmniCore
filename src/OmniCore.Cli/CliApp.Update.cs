namespace OmniCore.Cli;

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed partial class CliApp
{
    private static async Task<int> RunUpdateAsync(string[] args)
    {
        if (args.Length < 2) return UpdateUsage();
        return args[1] switch
        {
            "check" => await RunUpdateCheckAsync(args).ConfigureAwait(false),
            "apply" => await RunUpdateApplyAsync(args).ConfigureAwait(false),
            "rollback" => RunUpdateRollback(args),
            "status" => RunUpdateStatus(args),
            _ => UpdateUsage(),
        };
    }

    private static int UpdateUsage()
    {
        Console.WriteLine(Loc().Resolve("updates.usage"));
        return 2;
    }

    private static async Task<int> RunUpdateCheckAsync(string[] args)
    {
        if (!TryParseUpdateArguments(args, out var version, out var channel, out _)) return UpdateUsage();
        var result = await CheckReleaseAsync(channel, version).ConfigureAwait(false);
        PrintCheckResult(result);
        return result.Status is ReleaseUpdateStatus.UpdateAvailable or ReleaseUpdateStatus.DevelopmentBuild
            or ReleaseUpdateStatus.CompatibilityUnknown or ReleaseUpdateStatus.UpToDate
            or ReleaseUpdateStatus.NoPublishedRelease ? 0 : 1;
    }

    private static async Task<int> RunUpdateApplyAsync(string[] args)
    {
        if (!TryParseUpdateArguments(args, out var version, out var channel, out var yes)) return UpdateUsage();
        var text = Loc();
        if (!yes && !IsInteractive) return ReportFailure("update_confirmation_required");
        var check = await CheckReleaseAsync(channel, version).ConfigureAwait(false);
        if (check.Status is ReleaseUpdateStatus.UpToDate or ReleaseUpdateStatus.NoPublishedRelease)
        {
            Console.WriteLine(text.Resolve(ReleaseUpdateResources.StatusKey(check.Status)));
            return 0;
        }
        if (check.Status is not (ReleaseUpdateStatus.UpdateAvailable or ReleaseUpdateStatus.DevelopmentBuild))
        {
            Console.WriteLine(text.Resolve(ReleaseUpdateResources.StatusKey(check.Status)));
            if (check.DiagnosticCode is { Length: > 0 } code) Console.WriteLine(text.Resolve("updates.diagnostic") + ": " + code);
            return 1;
        }
        if (check.AvailableVersion is not { } available || check.Asset is null)
            return ReportFailure("update_asset_unavailable");
        if (check.Compatibility != ReleaseCompatibility.Compatible)
            return ReportFailure("update_compatibility_unverified");

        Console.WriteLine(text.Resolve("updates.available") + ": " + available);
        Console.WriteLine(text.Resolve("updates.asset") + ": " + (check.AssetName ?? check.Asset.Name));
        if (check.AssetSize is { } size) Console.WriteLine(text.Resolve("updates.size") + ": " + size.ToString(CultureInfo.InvariantCulture) + " bytes");
        if (check.AssetSha256 is { } digest) Console.WriteLine(text.Resolve("updates.sha256") + ": " + digest);
        if (!yes)
        {
            if (!Confirm(text.Resolve("updates.apply.confirm", "version", available)))
            {
                Console.WriteLine(text.Resolve("updates.apply.cancelled"));
                return 1;
            }
        }

        using var downloader = new ReleaseUpdateDownloader();
        var applier = CreateApplier(downloader);
        var result = await applier.ApplyAsync(check, confirmed: true).ConfigureAwait(false);
        if (!result.Prepared) return ReportFailure(result.DiagnosticCode);
        Console.WriteLine(text.Resolve("updates.apply.prepared", "version", available));
        return 0;
    }

    private static int RunUpdateRollback(string[] args)
    {
        if (!TryParseUpdateArguments(args, out _, out _, out var yes)) return UpdateUsage();
        var text = Loc();
        if (!yes && !IsInteractive) return ReportFailure("update_confirmation_required");
        if (!yes)
        {
            if (!Confirm(text.Resolve("updates.rollback.confirm")))
            {
                Console.WriteLine(text.Resolve("updates.apply.cancelled"));
                return 1;
            }
        }
        using var downloader = new ReleaseUpdateDownloader();
        var applier = CreateApplier(downloader);
        var result = applier.Rollback(confirmed: true);
        if (!result.Prepared) return ReportFailure(result.DiagnosticCode);
        Console.WriteLine(text.Resolve("updates.rollback.prepared",
            "version", result.State?.FromVersion ?? text.Resolve("updates.unknown")));
        return 0;
    }

    private static int RunUpdateStatus(string[] args)
    {
        if (args.Length != 2) return UpdateUsage();
        var text = Loc();
        var store = ReleaseUpdateStateStore.CreateDefault();
        if (!store.TryRead(out var state, out var diagnostic)) return ReportFailure(diagnostic);
        if (state is null)
        {
            Console.WriteLine(text.Resolve("updates.status.none"));
            return 0;
        }
        Console.WriteLine(text.Resolve("updates.status.heading"));
        Console.WriteLine(text.Resolve("updates.status.operation") + ": " + state.Operation);
        Console.WriteLine(text.Resolve("updates.status.state") + ": " + state.Status);
        if (state.FromVersion is not null) Console.WriteLine(text.Resolve("updates.status.from") + ": " + state.FromVersion);
        if (state.ToVersion is not null) Console.WriteLine(text.Resolve("updates.status.to") + ": " + state.ToVersion);
        Console.WriteLine(text.Resolve("updates.status.updated") + ": " + state.UpdatedAtUtc.ToLocalTime().ToString("O", CultureInfo.InvariantCulture));
        if (state.Detail is { Length: > 0 } detail) Console.WriteLine(text.Resolve("updates.status.detail") + ": " + detail);
        return 0;
    }

    private static ReleaseUpdateApplier CreateApplier(IReleaseUpdateDownloader downloader) =>
        new(ReleaseUpdateStateStore.CreateDefault(), new ReleaseInstallationInspector(), downloader,
            new NodeReleaseUpdateHelperLauncher());


    private static bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    private static bool Confirm(string prompt)
    {
        Console.Write(prompt);
        var answer = Console.ReadLine();
        return answer is not null && answer.Trim().ToLowerInvariant() is "s" or "si" or "sí" or "y" or "yes";
    }

    private static bool TryParseUpdateArguments(string[] args, out string? version,
        out ReleaseUpdateChannel channel, out bool yes)
    {
        version = null;
        channel = ReleaseUpdateChannel.Stable;
        yes = false;
        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--yes" or "-y":
                    yes = true;
                    break;
                case "--channel":
                    if (++index >= args.Length || args[index] is not ("stable" or "preview")) return false;
                    channel = args[index] == "preview" ? ReleaseUpdateChannel.Preview : ReleaseUpdateChannel.Stable;
                    break;
                default:
                    if (version is not null || args[index].StartsWith("--", StringComparison.Ordinal)) return false;
                    version = args[index];
                    break;
            }
        }
        return true;
    }

    private static async Task<ReleaseUpdateCheckResult> CheckReleaseAsync(ReleaseUpdateChannel channel,
        string? targetVersion)
    {
        var request = CreateRequest(channel, targetVersion);
        try
        {
            if (ReleaseUpdateCheckOverride is { } injected)
                return await injected(request, CancellationToken.None).ConfigureAwait(false);
            using var checker = new GitHubReleaseUpdateChecker();
            return await checker.CheckAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new(channel, ReleaseUpdateStatus.NetworkError, ReleaseCompatibility.Unknown, request.InstalledVersion,
                DiagnosticCode: "github_check_failed");
        }
    }

    private static void PrintCheckResult(ReleaseUpdateCheckResult result)
    {
        var text = Loc();
        var environment = ReleaseUpdateRuntimeFacts.Inspect();
        Console.WriteLine(text.Resolve(ReleaseUpdateResources.StatusKey(result.Status)));
        Console.WriteLine(text.Resolve("updates.channel") + ": " + text.Resolve(result.Channel == ReleaseUpdateChannel.Preview ? "updates.preview" : "updates.stable"));
        Console.WriteLine(text.Resolve("updates.installed") + ": " + SafeOutput(result.InstalledVersion));
        Console.WriteLine(text.Resolve("updates.platform") + ": " + RuntimeInformation.RuntimeIdentifier);
        Console.WriteLine(text.Resolve("updates.runtime") + ": .NET " + Environment.Version);
        Console.WriteLine(text.Resolve("updates.node") + ": " + (environment.NodeVersion ?? text.Resolve("updates.unknown")));
        Console.WriteLine(text.Resolve("updates.protocol") + ": " + ProtocolVersions.V1);
        Console.WriteLine(text.Resolve("updates.configuration") + ": " + environment.ConfigurationVersion);
        if (result.AvailableVersion is not null) Console.WriteLine(text.Resolve("updates.available") + ": " + SafeOutput(result.AvailableVersion));
        if (result.Tag is not null) Console.WriteLine(text.Resolve("updates.tag") + ": " + SafeOutput(result.Tag));
        if (result.CommitSha is not null) Console.WriteLine(text.Resolve("updates.commit") + ": " + result.CommitSha);
        if (result.AssetName is not null) Console.WriteLine(text.Resolve("updates.asset") + ": " + SafeOutput(result.AssetName));
        if (result.AssetSize is { } size) Console.WriteLine(text.Resolve("updates.size") + ": " + size.ToString(CultureInfo.InvariantCulture) + " bytes");
        if (result.AssetSha256 is { Length: 64 } digest && digest.All(Uri.IsHexDigit))
            Console.WriteLine(text.Resolve("updates.sha256") + ": " + digest.ToLowerInvariant());
        if (result.Requirements is { } requirements)
            Console.WriteLine(text.Resolve("updates.requirements") + ": .NET " + Range(requirements.DotNetMinimum, requirements.DotNetMaximum, text)
                + " · Node.js " + Range(requirements.NodeMinimum, requirements.NodeMaximum, text)
                + " · " + text.Resolve("updates.protocol") + " " + Range(requirements.ProtocolMinimum, requirements.ProtocolMaximum, text)
                + " · " + text.Resolve("updates.configuration") + " " + Range(requirements.ConfigurationMinimum, requirements.ConfigurationMaximum, text));
        if (SafeReleaseUrl(result.ReleaseUrl) is { } releaseUrl) Console.WriteLine(text.Resolve("updates.release_url") + ": " + releaseUrl);
        Console.WriteLine(text.Resolve("updates.compat_" + (result.Compatibility switch
            { ReleaseCompatibility.Compatible => "ok", ReleaseCompatibility.Incompatible => "no", _ => "unknown" })));
        Console.WriteLine(text.Resolve("updates.metadata_pending"));
        if (result.RetryAfterUtc is { } retry) Console.WriteLine(retry.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (result.DiagnosticCode is { Length: > 0 } diagnostic)
            Console.WriteLine(text.Resolve("updates.diagnostic") + ": " + diagnostic);
        if (result.Notes is { Length: > 0 } notes)
            Console.WriteLine(text.Resolve("updates.notes") + Environment.NewLine + SafeOutput(notes));
    }

    private static int ReportFailure(string? code)
    {
        var text = Loc();
        Console.WriteLine(text.Resolve(ErrorKey(code)));
        if (code is { Length: > 0 }) Console.WriteLine(text.Resolve("updates.diagnostic") + ": " + code);
        return 1;
    }

    private static string ErrorKey(string? code) => code switch
    {
        "update_confirmation_required" => "updates.error.confirmation",
        "update_development_checkout" => "updates.error.development_checkout",
        "update_release_package_unsupported" => "updates.error.release_package_unsupported",
        "update_install_kind_unknown" => "updates.error.install_kind_unknown",
        "update_compatibility_unverified" => "updates.error.compatibility_unverified",
        "update_asset_unavailable" => "updates.error.asset_unavailable",
        "update_operation_in_progress" => "updates.error.operation_in_progress",
        "update_operation_unknown" => "updates.error.operation_unknown",
        "update_prefix_mismatch" => "updates.error.prefix_mismatch",
        "update_backup_failed" => "updates.error.backup_failed",
        "update_backup_unverified" => "updates.error.backup_unverified",
        "update_publisher_verifier_unavailable" => "updates.error.publisher_unavailable",
        "update_publisher_verifier_invalid" or "update_publisher_identity_invalid" or "update_publisher_verification_failed"
            => "updates.error.publisher_failed",
        "update_helper_start_failed" => "updates.error.helper_start_failed",
        "update_helper_missing" => "updates.error.helper_missing",
        "update_state_invalid" or "update_state_unreadable" => "updates.error.state_invalid",
        "update_toolchain_unavailable" or "npm_not_found" or "node_not_found" => "updates.error.toolchain_unavailable",
        "update_rollback_unavailable" => "updates.error.rollback_unavailable",
        "update_download_failed" or "release_asset_digest_mismatch" or "release_asset_size_mismatch"
            or "release_asset_redirect_rejected" or "release_package_identity_mismatch"
            or "release_asset_too_large" or "release_asset_not_found" or "release_asset_invalid"
            or "release_package_invalid" or "github_offline" or "github_rate_limited" => "updates.error.download_failed",
        _ => "updates.error.not_supported",
    };

    private static ReleaseUpdateCheckRequest CreateRequest(ReleaseUpdateChannel channel, string? targetVersion = null)
    {
        var info = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0+development";
        var development = info.Contains('+', StringComparison.Ordinal);
        return new(channel, info, development,
            ReleaseUpdateRuntimeFacts.Inspect(),
            targetVersion);
    }

    private static string SafeOutput(string value)
    {
        var safe = new string(value.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());
        return safe.Length > 12_000 ? safe[..12_000] : safe;
    }

    private static string Range(string? minimum, string? maximum, Localization text) => minimum is null && maximum is null ? text.Resolve("updates.unknown")
        : (minimum ?? "?") + " – " + (maximum ?? "?");

    private static string? SafeReleaseUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsolutePath.StartsWith("/juancgomezm-s/OmniCore/releases/tag/", StringComparison.Ordinal) ? uri.AbsoluteUri : null;
}
