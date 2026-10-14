namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Loads only workspace-trusted, hash-consented project hooks through the Strong RPC bridge.</summary>
public static class ProjectExtensionHooks
{
    public static IReadOnlyList<IHook> Load(string workspaceRoot, IPlatformPaths paths, bool workspaceTrusted,
        IExtensionRpcInvoker invoker)
    {
        if (!workspaceTrusted) return [];
        ArgumentNullException.ThrowIfNull(invoker);
        var root = Path.Combine(Path.GetFullPath(workspaceRoot), ".omnicore", "extensions");
        var loader = new ExtensionManifestLoader();
        var store = new ExtensionConsentStore(paths, workspaceRoot);
        var candidates = loader.Discover(root, TrustLevel.Project, workspaceTrusted);
        var grants = candidates.Where(candidate => candidate.Manifest?.Id is not null)
            .ToDictionary(candidate => candidate.Manifest!.Id!, store.ConsentedCapabilities, StringComparer.Ordinal);
        var ready = loader.DiscoverWithConsents(root, TrustLevel.Project, workspaceTrusted, grants)
            .Where(candidate => candidate.Status == ExtensionDiscoveryStatus.Ready && candidate.Manifest is not null)
            .ToArray();
        var hooks = new List<IHook>();
        foreach (var extension in ready)
        {
            var consented = store.ConsentedCapabilities(extension);
            if (!consented.Contains("hook.register")) continue;
            var declarations = extension.Manifest!.Provides?.Hooks ?? [];
            for (var index = 0; index < declarations.Count; index++)
            {
                // Consenting to hook.register is the user's explicit activation of declared hooks;
                // each behavior capability still requires its own hash-bound grant.
                hooks.Add(new ExtensionLifecycleHook(extension, index, workspaceRoot, invoker,
                    consented, explicitlyActivated: true));
            }
        }
        var registry = new LifecycleHookRegistry();
        foreach (var hook in hooks)
            registry.Register(hook, explicitlyActivated: hook.Registration.Enabled,
                consented: hook.Registration.GrantedCapabilities);
        return registry.Activated();
    }
}
