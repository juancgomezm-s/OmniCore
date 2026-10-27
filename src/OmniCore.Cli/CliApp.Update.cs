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
        if (args.Length is < 2 or > 4 || args[1] != "check")
        { Console.WriteLine(Loc().Resolve("updates.usage")); return 2; }
        var channel = ReleaseUpdateChannel.Stable;
        if (args.Length > 2)
        {
            if (args.Length != 4 || args[2] != "--channel" || args[3] is not ("stable" or "preview"))
            { Console.WriteLine(Loc().Resolve("updates.usage")); return 2; }
            channel = args[3] == "preview" ? ReleaseUpdateChannel.Preview : ReleaseUpdateChannel.Stable;
        }

        var request = CreateRequest(channel);
        ReleaseUpdateCheckResult result;
        try
        {
            if (ReleaseUpdateCheckOverride is { } injected)
                result = await injected(request, CancellationToken.None).ConfigureAwait(false);
            else
            {
                using var checker = new GitHubReleaseUpdateChecker();
                result = await checker.CheckAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            result = new(channel, ReleaseUpdateStatus.NetworkError, ReleaseCompatibility.Unknown, request.InstalledVersion,
                DiagnosticCode: "github_check_failed");
        }

        var text = Loc();
        Console.WriteLine(text.Resolve(ReleaseUpdateResources.StatusKey(result.Status)));
        Console.WriteLine(text.Resolve("updates.channel") + ": " + text.Resolve(result.Channel == ReleaseUpdateChannel.Preview ? "updates.preview" : "updates.stable"));
        Console.WriteLine(text.Resolve("updates.installed") + ": " + SafeOutput(result.InstalledVersion));
        Console.WriteLine(text.Resolve("updates.platform") + ": " + RuntimeInformation.RuntimeIdentifier);
        Console.WriteLine(text.Resolve("updates.runtime") + ": .NET " + Environment.Version);
        Console.WriteLine(text.Resolve("updates.node") + ": " + text.Resolve("updates.unknown"));
        Console.WriteLine(text.Resolve("updates.protocol") + ": " + ProtocolVersions.V1);
        Console.WriteLine(text.Resolve("updates.configuration") + ": " + text.Resolve("updates.unknown"));
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
        return result.Status is ReleaseUpdateStatus.UpdateAvailable or ReleaseUpdateStatus.DevelopmentBuild
            or ReleaseUpdateStatus.CompatibilityUnknown ? 0 : result.Status is ReleaseUpdateStatus.UpToDate or ReleaseUpdateStatus.NoPublishedRelease ? 0 : 1;
    }

    private static ReleaseUpdateCheckRequest CreateRequest(ReleaseUpdateChannel channel)
    {
        var info = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0+development";
        var development = info.Contains('+', StringComparison.Ordinal);
        return new(channel, info, development,
            new(RuntimeInformation.RuntimeIdentifier, Environment.Version.ToString(), ProtocolVersion: ProtocolVersions.V1));
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
