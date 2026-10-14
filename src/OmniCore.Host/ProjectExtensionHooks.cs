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
        ArgumentNullException.ThrowIfNull(invoker);
        var ready = ProjectExtensionPackageCatalog.Ready(workspaceRoot, paths, workspaceTrusted);
        var hooks = new List<IHook>();
        foreach (var package in ready)
        {
            var extension = package.Extension;
            var consented = package.Capabilities;
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
