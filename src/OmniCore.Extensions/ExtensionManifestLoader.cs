namespace OmniCore.Extensions;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>Parses and discovers extensions without launching or initializing their processes.</summary>
public sealed class ExtensionManifestLoader
{
    public const string ManifestFileName = "omnicore-extension.yaml";
    private const string HostApiVersion = "1.0";
    public ExtensionManifest Load(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var manifest = Map(ManifestYamlAst.ReadRoot(yaml, "extension manifest"));
        Validate(manifest);
        return manifest;
    }

    private static ExtensionManifest Map(YamlMappingNode root)
    {
        ManifestYamlAst.CheckKeys(root, "extension manifest", "manifestVersion", "id", "version", "displayName",
            "publisher", "omnicoreApi", "provides", "requests", "runtime", "source", "uiResources");
        var manifest = new ExtensionManifest
        {
            ManifestVersion = ManifestYamlAst.Optional(root, "manifestVersion") is { } manifestVersion
                ? ManifestYamlAst.Int(manifestVersion, "manifestVersion") : 0,
            Id = ManifestYamlAst.OptionalString(root, "id", "id"),
            Version = ManifestYamlAst.OptionalString(root, "version", "version"),
            DisplayName = ManifestYamlAst.OptionalString(root, "displayName", "displayName"),
            Publisher = ManifestYamlAst.OptionalString(root, "publisher", "publisher"),
            OmnicoreApi = ManifestYamlAst.OptionalString(root, "omnicoreApi", "omnicoreApi"),
        };
        if (ManifestYamlAst.OptionalMap(root, "provides", "provides", "tools", "commands", "skills", "hooks",
            "contextContributors", "validators", "completionGates") is { } provides)
        {
            manifest.Provides = new ExtensionProvidesManifest
            {
                Tools = MapTools(provides), Commands = MapCommands(provides),
                Skills = ManifestYamlAst.OptionalStringList(provides, "skills", "provides.skills"),
                Hooks = MapHooks(provides),
                ContextContributors = ManifestYamlAst.OptionalStringList(provides, "contextContributors", "provides.contextContributors"),
                Validators = ManifestYamlAst.OptionalStringList(provides, "validators", "provides.validators"),
                CompletionGates = ManifestYamlAst.OptionalStringList(provides, "completionGates", "provides.completionGates"),
            };
        }
        if (ManifestYamlAst.OptionalMap(root, "requests", "requests", "capabilities", "permissions") is { } requests)
        {
            var result = new ExtensionRequestsManifest
            {
                Capabilities = ManifestYamlAst.OptionalStringList(requests, "capabilities", "requests.capabilities"),
            };
            if (ManifestYamlAst.OptionalMap(requests, "permissions", "requests.permissions", "process", "network", "paths") is { } permissions)
            {
                var permissionManifest = new ExtensionPermissionManifest
                {
                    Network = ManifestYamlAst.OptionalStringList(permissions, "network", "requests.permissions.network"),
                };
                if (ManifestYamlAst.Optional(permissions, "process") is { } processNode)
                {
                    if (processNode is not YamlSequenceNode process) throw ManifestYamlAst.WrongType("requests.permissions.process");
                    permissionManifest.Process = process.Select(item =>
                    {
                        var map = item as YamlMappingNode ?? throw ManifestYamlAst.WrongType("process permission");
                        ManifestYamlAst.CheckKeys(map, "process permission", "executable");
                        return new ExtensionProcessPermissionManifest { Executable = ManifestYamlAst.OptionalString(map, "executable", "process.executable") };
                    }).ToList();
                }
                if (ManifestYamlAst.OptionalMap(permissions, "paths", "requests.permissions.paths", "read", "write") is { } paths)
                    permissionManifest.Paths = new ExtensionPathPermissionManifest
                    {
                        Read = ManifestYamlAst.OptionalStringList(paths, "read", "requests.permissions.paths.read"),
                        Write = ManifestYamlAst.OptionalStringList(paths, "write", "requests.permissions.paths.write"),
                    };
                result.Permissions = permissionManifest;
            }
            manifest.Requests = result;
        }
        if (ManifestYamlAst.OptionalMap(root, "runtime", "runtime", "kind", "command", "args") is { } runtime)
            manifest.Runtime = new ExtensionRuntimeManifest
            {
                Kind = ManifestYamlAst.OptionalString(runtime, "kind", "runtime.kind"),
                Command = ManifestYamlAst.OptionalString(runtime, "command", "runtime.command"),
                Args = ManifestYamlAst.OptionalStringList(runtime, "args", "runtime.args"),
            };
        if (ManifestYamlAst.OptionalMap(root, "source", "source", "kind", "location", "sha256") is { } source)
            manifest.Source = new ExtensionSourceManifest
            {
                Kind = ManifestYamlAst.OptionalString(source, "kind", "source.kind"),
                Location = ManifestYamlAst.OptionalString(source, "location", "source.location"),
                Sha256 = ManifestYamlAst.OptionalString(source, "sha256", "source.sha256"),
            };
        manifest.UiResources = MapUiResources(root, manifest);
        return manifest;
    }

    private static Dictionary<string, Dictionary<string, string>>? MapUiResources(YamlMappingNode root, ExtensionManifest manifest)
    {
        if (ManifestYamlAst.OptionalMap(root, "uiResources", "uiResources", "es", "en", "fr", "it") is not { } locales) return null;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "extension.displayName", "extension.description" };
        foreach (var command in manifest.Provides?.Commands ?? []) allowed.Add("command." + command.Name + ".description");
        foreach (var tool in manifest.Provides?.Tools ?? []) allowed.Add("tool." + tool.Name + ".description");
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var locale in locales.Children)
        {
            var name = ManifestYamlAst.String(locale.Key, "uiResources locale");
            if (locale.Value is not YamlMappingNode resources || resources.Children.Count > 128)
                throw ManifestYamlAst.WrongType("uiResources." + name);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var resource in resources.Children)
            {
                var key = ManifestYamlAst.String(resource.Key, "uiResources key");
                var value = ManifestYamlAst.String(resource.Value, "uiResources text");
                if (!allowed.Contains(key) || string.IsNullOrWhiteSpace(value) || value.Length > 2048
                    || value.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
                    throw new InvalidDataException("Invalid extension UI resource: " + name + "/" + key);
                values.Add(key, value);
            }
            result.Add(name, values);
        }
        return result;
    }

    private static List<ExtensionToolManifest>? MapTools(YamlMappingNode provides)
    {
        if (ManifestYamlAst.Optional(provides, "tools") is not { } node) return null;
        if (node is not YamlSequenceNode sequence) throw ManifestYamlAst.WrongType("provides.tools");
        return sequence.Select(item =>
        {
            var map = item as YamlMappingNode ?? throw ManifestYamlAst.WrongType("extension tool");
            ManifestYamlAst.CheckKeys(map, "extension tool", "name", "schema");
            return new ExtensionToolManifest
            {
                Name = ManifestYamlAst.OptionalString(map, "name", "tool.name"),
                Schema = ManifestYamlAst.OptionalString(map, "schema", "tool.schema"),
            };
        }).ToList();
    }

    private static List<ExtensionCommandManifest>? MapCommands(YamlMappingNode provides)
    {
        if (ManifestYamlAst.Optional(provides, "commands") is not { } node) return null;
        if (node is not YamlSequenceNode sequence) throw ManifestYamlAst.WrongType("provides.commands");
        return sequence.Select(item =>
        {
            var map = item as YamlMappingNode ?? throw ManifestYamlAst.WrongType("extension command");
            ManifestYamlAst.CheckKeys(map, "extension command", "name", "kind", "tool");
            return new ExtensionCommandManifest
            {
                Name = ManifestYamlAst.OptionalString(map, "name", "command.name"),
                Kind = ManifestYamlAst.OptionalString(map, "kind", "command.kind"),
                Tool = ManifestYamlAst.OptionalString(map, "tool", "command.tool"),
            };
        }).ToList();
    }

    private static List<ExtensionHookManifest>? MapHooks(YamlMappingNode provides)
    {
        if (ManifestYamlAst.Optional(provides, "hooks") is not { } node) return null;
        if (node is not YamlSequenceNode sequence) throw ManifestYamlAst.WrongType("provides.hooks");
        return sequence.Select(item =>
        {
            var map = item as YamlMappingNode ?? throw ManifestYamlAst.WrongType("extension hook");
            ManifestYamlAst.CheckKeys(map, "extension hook", "point", "capabilities");
            return new ExtensionHookManifest
            {
                Point = ManifestYamlAst.OptionalString(map, "point", "hook.point"),
                Capabilities = ManifestYamlAst.OptionalStringList(map, "capabilities", "hook.capabilities"),
            };
        }).ToList();
    }

    public IReadOnlyList<DiscoveredExtension> Discover(string root, TrustLevel trust, bool workspaceTrusted,
        IReadOnlySet<string>? consentedCapabilities = null)
        => DiscoverCore(root, trust, workspaceTrusted, _ => consentedCapabilities);

    public IReadOnlyList<DiscoveredExtension> DiscoverWithConsents(string root, TrustLevel trust, bool workspaceTrusted,
        IReadOnlyDictionary<string, IReadOnlySet<string>> consentedByExtension) =>
        DiscoverCore(root, trust, workspaceTrusted, id => consentedByExtension.TryGetValue(id, out var set) ? set : null);

    private IReadOnlyList<DiscoveredExtension> DiscoverCore(string root, TrustLevel trust, bool workspaceTrusted,
        Func<string, IReadOnlySet<string>?> consentResolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) return [];
        if (HasReparsePointInAncestors(fullRoot)) return [];
        var result = new List<DiscoveredExtension>();
        foreach (var file in EnumerateManifestsWithoutFollowingLinks(fullRoot)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(file)!;
            var bytes = File.ReadAllBytes(file);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var packageHash = ComputePackageSha256(directory);
            try
            {
                var manifest = Load(Encoding.UTF8.GetString(bytes));
                var status = Status(manifest, trust, workspaceTrusted, consentResolver(manifest.Id!));
                result.Add(new DiscoveredExtension(directory, manifest, status,
                    status == ExtensionDiscoveryStatus.IncompatibleApi ? "The extension API range does not include " + HostApiVersion + "."
                    : status == ExtensionDiscoveryStatus.WorkspaceTrustRequired ? "The workspace is not trusted."
                    : status == ExtensionDiscoveryStatus.ConsentRequired ? "Requested capabilities have not been consented."
                    : status == ExtensionDiscoveryStatus.Untrusted ? "The extension source is unverified."
                    : null, hash, packageHash, trust));
            }
            catch (Exception ex) when (ex is InvalidDataException or YamlException or InvalidOperationException
                or FormatException or ArgumentException)
            {
                result.Add(new DiscoveredExtension(directory, null, ExtensionDiscoveryStatus.InvalidManifest,
                    ex.Message, hash, packageHash, TrustLevel.Untrusted));
            }
        }
        return result;
    }

    /// <summary>Stable hash of all regular package files except the manifest itself.</summary>
    public static string ComputePackageSha256(string packageDirectory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateManifestsAndPackageFiles(packageDirectory)
            .Where(file => !string.Equals(Path.GetFileName(file), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(packageDirectory, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = File.OpenRead(file);
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateManifestsWithoutFollowingLinks(string root) =>
        EnumerateManifestsAndPackageFiles(root).Where(path =>
            string.Equals(Path.GetFileName(path), ManifestFileName, StringComparison.OrdinalIgnoreCase));

    private static bool HasReparsePointInAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) return false;
            current = parent;
        }
    }

    private static IEnumerable<string> EnumerateManifestsAndPackageFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    private static ExtensionDiscoveryStatus Status(ExtensionManifest manifest, TrustLevel trust, bool workspaceTrusted,
        IReadOnlySet<string>? consentedCapabilities)
    {
        if (!SupportsHostApi(manifest.OmnicoreApi!)) return ExtensionDiscoveryStatus.IncompatibleApi;
        if (trust == TrustLevel.Project && !workspaceTrusted) return ExtensionDiscoveryStatus.WorkspaceTrustRequired;
        if (trust == TrustLevel.Untrusted) return ExtensionDiscoveryStatus.Untrusted;
        var requested = manifest.Requests?.Capabilities ?? [];
        if (requested.Any(capability => consentedCapabilities?.Contains(capability) != true))
            return ExtensionDiscoveryStatus.ConsentRequired;
        return ExtensionDiscoveryStatus.Ready;
    }

    private static bool SupportsHostApi(string range)
    {
        // M8 currently supports the declared major range syntax used by ADR-0023.
        if (range == "1.x" || range == "^1.0" || range == "~1.0") return true;
        if (range.StartsWith(">=1.0", StringComparison.Ordinal) && range.Contains("<2.0", StringComparison.Ordinal)) return true;
        return string.Equals(range, HostApiVersion, StringComparison.Ordinal);
    }

    private static void Validate(ExtensionManifest manifest)
    {
        if (manifest.ManifestVersion != 1 || string.IsNullOrWhiteSpace(manifest.Id)
            || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, "^[a-z0-9]+(?:[.-][a-z0-9]+)+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.DisplayName)
            || string.IsNullOrWhiteSpace(manifest.Publisher) || string.IsNullOrWhiteSpace(manifest.OmnicoreApi)
            || manifest.Runtime is not { Kind: "process", Command.Length: > 0 })
            throw new InvalidDataException("The extension manifest is missing a required field or has an unsupported version.");
        if (manifest.Requests?.Permissions is { } permissions
            && (permissions.Process?.Any(process => string.IsNullOrWhiteSpace(process.Executable)) == true
                || permissions.Network?.Any(string.IsNullOrWhiteSpace) == true
                || permissions.Paths?.Read?.Any(string.IsNullOrWhiteSpace) == true
                || permissions.Paths?.Write?.Any(string.IsNullOrWhiteSpace) == true
                || permissions.Paths?.Read?.Concat(permissions.Paths?.Write ?? []).Any(UnsafeRelativePattern) == true))
            throw new InvalidDataException("The extension permission ceiling contains an empty rule.");
        if (manifest.Provides?.Tools?.Any(tool => string.IsNullOrWhiteSpace(tool.Name)
            || string.IsNullOrWhiteSpace(tool.Schema)) == true)
            throw new InvalidDataException("Each extension tool requires a name and a schema path.");
        var declaredTools = manifest.Provides?.Tools ?? [];
        if (manifest.Provides?.Commands?.Any(command => string.IsNullOrWhiteSpace(command.Name)
            || command.Kind != "Extension" || string.IsNullOrWhiteSpace(command.Tool)) == true
            || manifest.Provides?.Commands?.Any(command => !declaredTools.Any(tool => tool.Name == command.Tool)) == true)
            throw new InvalidDataException("Each extension command must bind to a declared extension tool.");
        var requests = new HashSet<string>(manifest.Requests?.Capabilities ?? [], StringComparer.Ordinal);
        var requiredCapabilities = new List<string>();
        if (manifest.Provides?.Tools?.Count > 0) requiredCapabilities.Add("tool.register");
        if (manifest.Provides?.Commands?.Count > 0) requiredCapabilities.Add("command.register");
        if (manifest.Provides?.Skills?.Count > 0) requiredCapabilities.Add("skill.register");
        if (manifest.Provides?.Hooks?.Count > 0) requiredCapabilities.Add("hook.register");
        if (manifest.Provides?.ContextContributors?.Count > 0) requiredCapabilities.Add("context.contribute");
        foreach (var hook in manifest.Provides?.Hooks ?? [])
        {
            if (!Enum.TryParse<HookCapability>(hook.Capabilities?.FirstOrDefault(), false, out _)
                && hook.Capabilities?.Count > 0)
                throw new InvalidDataException("Hook capabilities must use the supported Observe, Annotate, Restrict, or RequireValidation names.");
            foreach (var capability in hook.Capabilities ?? [])
            {
                if (!Enum.TryParse<HookCapability>(capability, false, out var parsed))
                    throw new InvalidDataException("Hook capabilities must use the supported Observe, Annotate, Restrict, or RequireValidation names.");
                requiredCapabilities.Add("hook." + parsed.ToString().ToLowerInvariant());
            }
        }
        if (requiredCapabilities.Any(capability => !requests.Contains(capability)))
            throw new InvalidDataException("The manifest does not request a capability required by its declared components.");
        if (manifest.Provides?.Tools?.Count > 0 && manifest.Requests?.Permissions?.Process?.Count is not > 0)
            throw new InvalidDataException("A process-backed extension tool must declare its runtime executable in requests.permissions.process.");
    }

    private static bool UnsafeRelativePattern(string path)
    {
        var normalized = path.Replace('\\', '/');
        return Path.IsPathRooted(path) || normalized.StartsWith('/')
            || normalized.Split('/').Any(segment => segment is ".." or ".");
    }

}
