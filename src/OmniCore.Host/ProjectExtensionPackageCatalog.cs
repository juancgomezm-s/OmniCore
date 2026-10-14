namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Resolves only current, workspace-trusted, hash-consented Project extensions.</summary>
internal static class ProjectExtensionPackageCatalog
{
    public static IReadOnlyList<(DiscoveredExtension Extension, IReadOnlySet<string> Capabilities)> Ready(
        string workspaceRoot, IPlatformPaths paths, bool workspaceTrusted)
    {
        var loader = new ExtensionManifestLoader();
        var result = new List<(DiscoveredExtension Extension, IReadOnlySet<string> Capabilities)>();
        Add(Path.Combine(Path.GetFullPath(paths.DataDirectory), "extensions"), TrustLevel.Trusted,
            trusted: true, new ExtensionConsentStore(paths, ScopeLevel.User));
        if (workspaceTrusted)
            Add(Path.Combine(Path.GetFullPath(workspaceRoot), ".omnicore", "extensions"), TrustLevel.Project,
                trusted: true, new ExtensionConsentStore(paths, workspaceRoot));
        return result.GroupBy(item => item.Extension.Manifest?.Id ?? "", StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();

        void Add(string root, TrustLevel trust, bool trusted, ExtensionConsentStore store)
        {
            var candidates = loader.Discover(root, trust, workspaceTrusted: trusted);
            var grants = candidates.Where(candidate => candidate.Manifest?.Id is not null)
                .ToDictionary(candidate => candidate.Manifest!.Id!, store.ConsentedCapabilities, StringComparer.Ordinal);
            result.AddRange(loader.DiscoverWithConsents(root, trust, workspaceTrusted: trusted, grants)
                .Where(candidate => candidate.Status == ExtensionDiscoveryStatus.Ready && candidate.Manifest is not null)
                .Select(candidate => (candidate, (IReadOnlySet<string>)store.ConsentedCapabilities(candidate))));
        }
    }
}
