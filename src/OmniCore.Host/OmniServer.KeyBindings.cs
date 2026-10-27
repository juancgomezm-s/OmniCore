namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;

public sealed partial class OmniServer
{
    private SessionQueryResult QueryExtensionKeyBindings(string name)
    {
        var contributions = new List<ExtensionKeyBindingContribution>();
        var diagnostics = new List<ExtensionKeyBindingDiagnostic>();
        var workspace = _workspaceRoot;
        if (workspace is null)
            return new(name, ExtensionKeyBindingProtocol.Encode(new([], [])));

        var paths = _platformPathsFactory();
        RefreshProjectExtensionCommands();
        var ready = ProjectExtensionPackageCatalog.Ready(workspace, paths,
            new WorkspaceTrustStore(paths).IsTrusted(workspace));
        var currentCommands = _commandService.Catalog(CommandContextForCurrentRun()).Commands
            .ToDictionary(command => command.Id, StringComparer.Ordinal);
        foreach (var (extension, capabilities) in ready)
        {
            var manifest = extension.Manifest!;
            var declarations = manifest.Provides?.KeyBindings ?? [];
            if (declarations.Count == 0 || !capabilities.Contains("keybinding.register")) continue;
            var source = "Extension:" + manifest.Id + "@" + manifest.Version;
            if (source.Length > 512)
            {
                diagnostics.Add(new("source_too_long", source[..512], manifest.Id!));
                continue;
            }
            foreach (var binding in declarations)
            {
                var command = binding.Command!;
                // The manifest loader enforces this too; recheck the exact qualified identity at the authority boundary.
                if (!command.StartsWith(manifest.Id + ":", StringComparison.Ordinal)
                    || !(manifest.Provides?.Commands ?? []).Any(item => command == manifest.Id + ":" + item.Name)
                    || !currentCommands.TryGetValue(command, out var catalogCommand) || catalogCommand.Source != source)
                {
                    diagnostics.Add(new("command_not_owned", source, manifest.Id!));
                    continue;
                }
                var arguments = binding.Arguments ?? [];
                var when = binding.When ?? "true";
                var fingerprint = BindingFingerprint(extension, binding, source);
                contributions.Add(new(binding.Keys!, command, arguments, when, source,
                    extension.ManifestSha256, extension.PackageSha256, fingerprint));
            }
        }
        if (contributions.Count > 256)
            diagnostics.Insert(0, new("too_many_contributions", "Host", "extensions"));
        return new(name, ExtensionKeyBindingProtocol.Encode(new(contributions.Count > 256 ? [] : contributions.ToArray(),
            diagnostics.Take(256).ToArray())));
    }

    private static string BindingFingerprint(OmniCore.Extensions.DiscoveredExtension extension,
        OmniCore.Extensions.ExtensionKeyBindingManifest binding, string source)
    {
        static string Part(string value) => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
        var canonical = string.Concat(Part(source), Part(extension.ManifestSha256), Part(extension.PackageSha256),
            Part(binding.Keys ?? ""), Part(binding.Command ?? ""), Part(binding.When ?? "true"),
            string.Concat((binding.Arguments ?? []).Select(Part)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
