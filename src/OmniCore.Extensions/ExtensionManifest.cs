namespace OmniCore.Extensions;

using OmniCore.Abstractions;
using OmniCore.Domain;

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
    public List<string>? ActivationEvents { get; set; }
    public List<string>? Platforms { get; set; }
    public List<string>? Dependencies { get; set; }
    public List<string>? Configuration { get; set; }
    public string? ExecutionMode { get; set; }
    /// <summary>Optional literal UI copy by locale and stable key; never used as model instructions.</summary>
    public Dictionary<string, Dictionary<string, string>>? UiResources { get; set; }
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
    public List<ExtensionViewManifest>? Views { get; set; }
}

public sealed class ExtensionViewManifest
{
    public string? Id { get; set; }
    public string? Kind { get; set; }
    public string? Source { get; set; }
}

public sealed class ExtensionToolManifest
{
    public string? Name { get; set; }
    public string? Schema { get; set; }
    /// <summary>Search/planning tags only. Risk, effect and permission ceilings remain Host-derived.</summary>
    public List<string>? Tags { get; set; }
}

public sealed class ExtensionCommandManifest
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? Tool { get; set; }
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
        ResourceClaims authorizedClaims, CancellationToken cancellationToken);
}

/// <summary>
/// ExtensionBoundary is an intersection ceiling. It verifies the requests declared by an
/// extension; it never creates a permission grant or turns an Ask into Allow.
/// </summary>
public sealed class ExtensionBoundary : IToolIntentBoundary
{
    private readonly ExtensionPermissionManifest _ceiling;
    private readonly string _extensionId;
    private readonly string _workspaceRoot;
    private readonly ComponentSource _source;

    public ExtensionBoundary(ExtensionPermissionManifest ceiling, string extensionId = "", string workspaceRoot = "",
        ComponentSource? source = null)
    {
        _ceiling = ceiling ?? throw new ArgumentNullException(nameof(ceiling));
        _extensionId = extensionId;
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? "" : Path.GetFullPath(workspaceRoot);
        _source = source ?? new ComponentSource(SourceKind.Extension, ScopeLevel.Project, TrustLevel.Project,
            extensionId, "1");
    }

    public bool AllowsProcess(string executable)
    {
        var normalized = executable.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        return _ceiling.Process?.Any(rule => rule.Executable is { Length: > 0 } pattern
            && (ExtensionPathPattern.IsMatch(pattern, normalized)
                || ExtensionPathPattern.IsMatch(pattern, fileName))) == true;
    }

    public bool AllowsNetwork(string host)
    {
        var separator = host.LastIndexOf(':');
        var hostname = separator > 0 && int.TryParse(host[(separator + 1)..], out _) ? host[..separator] : host;
        return _ceiling.Network?.Any(pattern => ExtensionPathPattern.IsMatch(pattern, host)
            || ExtensionPathPattern.IsMatch(pattern, hostname)) == true;
    }

    public bool AllowsPath(string path, bool write)
    {
        var patterns = write ? _ceiling.Paths?.Write : _ceiling.Paths?.Read;
        if (patterns is null) return false;
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is ".." or ".")) return false;
        if (_workspaceRoot.Length > 0)
        {
            var target = Path.GetFullPath(Path.Combine(_workspaceRoot, normalized));
            var relative = Path.GetRelativePath(_workspaceRoot, target);
            if (Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        }
        return patterns.Any(pattern => ExtensionPathPattern.IsMatch(pattern, normalized));
    }

    /// <summary>Per-tool ceiling checked by ToolRuntime both before permissions and before Execute.</summary>
    public ModelCapabilityDecision Evaluate(ToolIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!intent.ToolId.ToString().StartsWith("ext." + _source.Owner + ".", StringComparison.Ordinal))
            return ModelCapabilityDecision.Allow();
        var sourceTrusted = (_source.Scope == ScopeLevel.Project && _source.Trust == TrustLevel.Project)
            || (_source.Scope == ScopeLevel.User && _source.Trust == TrustLevel.Trusted);
        if (_source.Kind != SourceKind.Extension || !sourceTrusted
            || (_extensionId.Length > 0 && !string.Equals(_source.Owner, _extensionId, StringComparison.Ordinal))
            || !intent.ToolId.ToString().StartsWith("ext." + _source.Owner + ".", StringComparison.Ordinal))
            return ModelCapabilityDecision.Reject("extension source or scope is outside its declared boundary", null);
        if (!AllowsClaims(intent.Claims))
            return ModelCapabilityDecision.Reject("extension intent exceeds requests.permissions", null);
        return ModelCapabilityDecision.Allow();
    }

    public bool AllowsClaims(ResourceClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return claims.Secrets.Count == 0
            && claims.Process is { } process && AllowsProcess(process.Executable)
                && (_workspaceRoot.Length == 0 || process.WorkingDirectory is { Length: > 0 } cwd
                    && string.Equals(Path.GetFullPath(cwd), _workspaceRoot,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            && claims.Network.All(network => AllowsNetwork(network.Host + (network.Port is { } port ? ":" + port : "")))
            && claims.Reads.All(path => AllowsPath(path, write: false))
            && claims.Writes.All(path => AllowsPath(path, write: true));
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
