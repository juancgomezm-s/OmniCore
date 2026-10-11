namespace OmniCore.Extensions;

using OmniCore.Domain;
using YamlDotNet.Serialization;

/// <summary>Typed, versioned representation of omnicore-extension.yaml (ADR-0023/0039).</summary>
public sealed class ExtensionManifest
{
    public int ManifestVersion { get; set; }
    public string? Id { get; set; }
    public string? Version { get; set; }
    public string? DisplayName { get; set; }
    public string? Publisher { get; set; }
    public string? OmnicoreApi { get; set; }
    public ExtensionProvidesManifest? Provides { get; set; }
    public ExtensionRequestsManifest? Requests { get; set; }
    public ExtensionRuntimeManifest? Runtime { get; set; }
    public ExtensionSourceManifest? Source { get; set; }
}

public sealed class ExtensionProvidesManifest
{
    public List<ExtensionToolManifest>? Tools { get; set; }
    public List<ExtensionCommandManifest>? Commands { get; set; }
    public List<string>? Skills { get; set; }
    public List<ExtensionHookManifest>? Hooks { get; set; }
    public List<string>? ContextContributors { get; set; }
    public List<string>? Validators { get; set; }
    public List<string>? CompletionGates { get; set; }
}

public sealed class ExtensionToolManifest
{
    public string? Name { get; set; }
    public string? Schema { get; set; }
}

public sealed class ExtensionCommandManifest
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
}

public sealed class ExtensionHookManifest
{
    public string? Point { get; set; }
    public List<string>? Capabilities { get; set; }
}

public sealed class ExtensionRequestsManifest
{
    public List<string>? Capabilities { get; set; }
    public ExtensionPermissionManifest? Permissions { get; set; }
}

public sealed class ExtensionPermissionManifest
{
    public List<ExtensionProcessPermissionManifest>? Process { get; set; }
    public List<string>? Network { get; set; }
    public ExtensionPathPermissionManifest? Paths { get; set; }
}

public sealed class ExtensionProcessPermissionManifest
{
    public string? Executable { get; set; }
}

public sealed class ExtensionPathPermissionManifest
{
    public List<string>? Read { get; set; }
    public List<string>? Write { get; set; }
}

public sealed class ExtensionRuntimeManifest
{
    public string? Kind { get; set; }
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
}

public sealed class ExtensionSourceManifest
{
    public string? Kind { get; set; }
    public string? Location { get; set; }
    public string? Sha256 { get; set; }
}

[YamlStaticContext]
[YamlSerializable(typeof(ExtensionManifest))]
[YamlSerializable(typeof(ExtensionProvidesManifest))]
[YamlSerializable(typeof(ExtensionToolManifest))]
[YamlSerializable(typeof(ExtensionCommandManifest))]
[YamlSerializable(typeof(ExtensionHookManifest))]
[YamlSerializable(typeof(ExtensionRequestsManifest))]
[YamlSerializable(typeof(ExtensionPermissionManifest))]
[YamlSerializable(typeof(ExtensionProcessPermissionManifest))]
[YamlSerializable(typeof(ExtensionPathPermissionManifest))]
[YamlSerializable(typeof(ExtensionRuntimeManifest))]
[YamlSerializable(typeof(ExtensionSourceManifest))]
public sealed partial class ExtensionYamlStaticContext : StaticContext
{
}

public enum ExtensionDiscoveryStatus
{
    Ready,
    WorkspaceTrustRequired,
    ConsentRequired,
    IncompatibleApi,
    InvalidManifest,
    Untrusted,
}

public sealed record DiscoveredExtension(string Directory, ExtensionManifest? Manifest,
    ExtensionDiscoveryStatus Status, string? Diagnostic, string ManifestSha256, string PackageSha256, TrustLevel Trust)
{
    public string ConsentHash => ManifestSha256 + PackageSha256;
}

public interface IExtensionRpcInvoker
{
    Task<string> InvokeAsync(DiscoveredExtension extension, string method, string parametersJson,
        CancellationToken cancellationToken);
}

/// <summary>
/// ExtensionBoundary is an intersection ceiling. It verifies the requests declared by an
/// extension; it never creates a permission grant or turns an Ask into Allow.
/// </summary>
public sealed class ExtensionBoundary
{
    private readonly ExtensionPermissionManifest _ceiling;

    public ExtensionBoundary(ExtensionPermissionManifest ceiling) =>
        _ceiling = ceiling ?? throw new ArgumentNullException(nameof(ceiling));

    public bool AllowsProcess(string executable) => _ceiling.Process?.Any(rule =>
        string.Equals(rule.Executable, executable, StringComparison.OrdinalIgnoreCase)) == true;

    public bool AllowsNetwork(string host) => _ceiling.Network?.Any(pattern =>
        string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase)) == true;

    public bool AllowsPath(string path, bool write)
    {
        var patterns = write ? _ceiling.Paths?.Write : _ceiling.Paths?.Read;
        if (patterns is null) return false;
        var normalized = path.Replace('\\', '/');
        return patterns.Any(pattern => ExtensionPathPattern.IsMatch(pattern, normalized));
    }
}

internal static class ExtensionPathPattern
{
    public static bool IsMatch(string pattern, string path)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Replace('\\', '/'))
            .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(path, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
