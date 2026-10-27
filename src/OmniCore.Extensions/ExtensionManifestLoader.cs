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
            "publisher", "omnicoreApi", "provides", "requests", "runtime", "source", "uiResources",
            "activationEvents", "platforms", "dependencies", "configuration", "executionMode");
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
            "contextContributors", "validators", "completionGates", "views", "keyBindings") is { } provides)
        {
            manifest.Provides = new ExtensionProvidesManifest
            {
                Tools = MapTools(provides), Commands = MapCommands(provides),
                Skills = ManifestYamlAst.OptionalStringList(provides, "skills", "provides.skills"),
                Hooks = MapHooks(provides),
                ContextContributors = ManifestYamlAst.OptionalStringList(provides, "contextContributors", "provides.contextContributors"),
                Validators = ManifestYamlAst.OptionalStringList(provides, "validators", "provides.validators"),
                CompletionGates = ManifestYamlAst.OptionalStringList(provides, "completionGates", "provides.completionGates"),
                KeyBindings = MapKeyBindings(provides),
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
        manifest.ActivationEvents = ManifestYamlAst.OptionalStringList(root, "activationEvents", "activationEvents");
        manifest.Platforms = ManifestYamlAst.OptionalStringList(root, "platforms", "platforms");
        manifest.Dependencies = ManifestYamlAst.OptionalStringList(root, "dependencies", "dependencies");
        manifest.Configuration = ManifestYamlAst.OptionalStringList(root, "configuration", "configuration");
        manifest.ExecutionMode = ManifestYamlAst.OptionalString(root, "executionMode", "executionMode");
        if (manifest.ManifestVersion == 2 && ManifestYamlAst.OptionalMap(root, "provides", "provides",
            "tools", "commands", "skills", "hooks", "contextContributors", "validators", "completionGates", "views", "keyBindings") is { } viewProvides
            && ManifestYamlAst.Optional(viewProvides, "views") is { } viewNode)
        {
            if (viewNode is not YamlSequenceNode views) throw ManifestYamlAst.WrongType("provides.views");
            manifest.Provides ??= new();
            manifest.Provides.Views = views.Select(item =>
            {
                var map = item as YamlMappingNode ?? throw ManifestYamlAst.WrongType("view");
                ManifestYamlAst.CheckKeys(map, "view", "id", "kind", "source");
                return new ExtensionViewManifest { Id = ManifestYamlAst.OptionalString(map, "id", "view.id"),
                    Kind = ManifestYamlAst.OptionalString(map, "kind", "view.kind"),
                    Source = ManifestYamlAst.OptionalString(map, "source", "view.source") };
            }).ToList();
        }
        manifest.UiResources = MapUiResources(root, manifest);
        return manifest;
    }

    private static List<ExtensionKeyBindingManifest>? MapKeyBindings(YamlMappingNode provides)
    {
        if (ManifestYamlAst.Optional(provides, "keyBindings") is not { } node) return null;
        if (node is not YamlSequenceNode sequence) throw ManifestYamlAst.WrongType("provides.keyBindings");
        return sequence.Select(item =>
        {
            var map = item as YamlMappingNode ?? throw ManifestYamlAst.WrongType("extension key binding");
            ManifestYamlAst.CheckKeys(map, "extension key binding", "keys", "command", "arguments", "when");
            return new ExtensionKeyBindingManifest
            {
                Keys = ManifestYamlAst.OptionalString(map, "keys", "keyBinding.keys"),
                Command = ManifestYamlAst.OptionalString(map, "command", "keyBinding.command"),
                Arguments = ManifestYamlAst.OptionalStringList(map, "arguments", "keyBinding.arguments"),
                When = ManifestYamlAst.OptionalString(map, "when", "keyBinding.when"),
            };
        }).ToList();
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
            ManifestYamlAst.CheckKeys(map, "extension tool", "name", "schema", "tags");
            return new ExtensionToolManifest
            {
                Name = ManifestYamlAst.OptionalString(map, "name", "tool.name"),
                Schema = ManifestYamlAst.OptionalString(map, "schema", "tool.schema"),
                Tags = ManifestYamlAst.OptionalStringList(map, "tags", "tool.tags"),
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
        if (!(manifest.ManifestVersion == 2 ? SupportsVersionTwoApi(manifest.OmnicoreApi!) : SupportsHostApi(manifest.OmnicoreApi!)))
            return ExtensionDiscoveryStatus.IncompatibleApi;
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

    private static bool SupportsVersionTwoApi(string range) => range.Trim() is "1.0" or "1.0.0" or "1.x"
        or "^1.0" or "^1.0.0" or "~1.0" or "~1.0.0" or ">=1.0 <2.0" or ">=1.0.0 <2.0.0";

    private static void Validate(ExtensionManifest manifest)
    {
        if (manifest.ManifestVersion is not (1 or 2) || string.IsNullOrWhiteSpace(manifest.Id)
            || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, "^[a-z0-9]+(?:[.-][a-z0-9]+)+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.DisplayName)
            || string.IsNullOrWhiteSpace(manifest.Publisher) || string.IsNullOrWhiteSpace(manifest.OmnicoreApi)
            || manifest.ManifestVersion == 1 && manifest.Runtime is not { Kind: "process", Command.Length: > 0 }
            || manifest.ManifestVersion == 2 && manifest.Runtime is not null
                && manifest.Runtime is not { Kind: "process" or "node", Command.Length: > 0 })
            throw new InvalidDataException("The extension manifest is missing a required field or has an unsupported version.");
        if (manifest.ManifestVersion == 2) ValidateVersionTwo(manifest);
        if (manifest.Requests?.Permissions is { } permissions
            && (permissions.Process?.Any(process => string.IsNullOrWhiteSpace(process.Executable)) == true
                || permissions.Network?.Any(string.IsNullOrWhiteSpace) == true
                || permissions.Paths?.Read?.Any(string.IsNullOrWhiteSpace) == true
                || permissions.Paths?.Write?.Any(string.IsNullOrWhiteSpace) == true
                || permissions.Paths?.Read?.Concat(permissions.Paths?.Write ?? []).Any(UnsafeRelativePattern) == true))
            throw new InvalidDataException("The extension permission ceiling contains an empty rule.");
        if (manifest.Provides?.Tools?.Any(tool => string.IsNullOrWhiteSpace(tool.Name)
            || string.IsNullOrWhiteSpace(tool.Schema)
            || tool.Tags?.Count > 16
            || tool.Tags?.Any(tag => string.IsNullOrWhiteSpace(tag)
                || !System.Text.RegularExpressions.Regex.IsMatch(tag, "^[a-z0-9][a-z0-9._-]{0,47}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)) == true
            || tool.Tags?.Distinct(StringComparer.Ordinal).Count() != tool.Tags?.Count) == true)
            throw new InvalidDataException("Each extension tool requires a name and a schema path.");
        var declaredTools = manifest.Provides?.Tools ?? [];
        if (manifest.Provides?.Commands?.Any(command => string.IsNullOrWhiteSpace(command.Name)
            || command.Kind != "Extension" || string.IsNullOrWhiteSpace(command.Tool)) == true
            || manifest.Provides?.Commands?.Any(command => !declaredTools.Any(tool => tool.Name == command.Tool)) == true)
            throw new InvalidDataException("Each extension command must bind to a declared extension tool.");
        var keyBindings = manifest.Provides?.KeyBindings ?? [];
        if (keyBindings.Count > 16 || keyBindings.Any(binding => string.IsNullOrWhiteSpace(binding.Keys)
            || binding.Keys.Length > 256 || string.IsNullOrWhiteSpace(binding.Command) || binding.Command.Length > 160
            || binding.Arguments?.Count > 16 || binding.Arguments?.Any(argument => argument is null || argument.Length > 4096) == true
            || binding.When?.Length > 512)
            || keyBindings.Select(binding => (binding.Keys, binding.Command, string.Join("\0", binding.Arguments ?? []), binding.When ?? "true"))
                .Distinct().Count() != keyBindings.Count
            || keyBindings.Any(binding => !(manifest.Provides?.Commands ?? []).Any(command =>
                binding.Command == manifest.Id + ":" + command.Name)))
            throw new InvalidDataException("Extension key bindings must be bounded and target unique commands owned by the extension.");
        if (manifest.Provides?.Skills is { } skills && (skills.Count > 16
            || skills.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 240 || UnsafeRelativePattern(path))
            || skills.Distinct(StringComparer.Ordinal).Count() != skills.Count))
            throw new InvalidDataException("Extension skill paths must be unique, bounded package-relative directories.");
        foreach (var declarations in new[] { manifest.Provides?.ContextContributors,
                     manifest.Provides?.Validators, manifest.Provides?.CompletionGates })
            if (declarations is { } names && (names.Count > 16 || names.Any(name => string.IsNullOrWhiteSpace(name)
                || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9._-]{0,63}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                || names.Distinct(StringComparer.Ordinal).Count() != names.Count))
                throw new InvalidDataException("Extension contribution identifiers must be unique bounded slugs.");
        var requests = new HashSet<string>(manifest.Requests?.Capabilities ?? [], StringComparer.Ordinal);
        var requiredCapabilities = new List<string>();
        if (manifest.Provides?.Tools?.Count > 0) requiredCapabilities.Add("tool.register");
        if (manifest.Provides?.Commands?.Count > 0) requiredCapabilities.Add("command.register");
        if (keyBindings.Count > 0) requiredCapabilities.Add("keybinding.register");
        if (manifest.Provides?.Skills?.Count > 0) requiredCapabilities.Add("skill.register");
        if (manifest.Provides?.Hooks?.Count > 0) requiredCapabilities.Add("hook.register");
        if (manifest.Provides?.ContextContributors?.Count > 0) requiredCapabilities.Add("context.contribute");
        if (manifest.Provides?.Validators?.Count > 0) requiredCapabilities.Add("completion.validate");
        if (manifest.Provides?.CompletionGates?.Count > 0) requiredCapabilities.Add("completion.gate");
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

    private static void ValidateVersionTwo(ExtensionManifest manifest)
    {
        foreach (var names in new[] { manifest.Provides?.Tools?.Select(tool => tool.Name).ToArray(),
                     manifest.Provides?.Commands?.Select(command => command.Name).ToArray() })
            if (names is not null && (names.Length > 64 || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 80
                    || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))))
                throw new InvalidDataException("extension_contribution_id_invalid");
        if (manifest.Requests?.Capabilities is { } capabilities && (capabilities.Count > 64
            || capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Count
            || capabilities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))))
            throw new InvalidDataException("extension_capabilities_invalid");
        if (!manifest.Id!.StartsWith(manifest.Publisher + ".", StringComparison.Ordinal)
            || !System.Version.TryParse(manifest.Version!.Split('-')[0], out _)
            || manifest.ExecutionMode is not (null or "trusted" or "restricted")
            || manifest.Runtime is not null && manifest.ExecutionMode is null
            || manifest.Runtime is { } runtime && (UnsafeRelativePattern(runtime.Command!)
                || runtime.Command!.Length > 240 || runtime.Args?.Count > 32)
            || manifest.Platforms?.Any(value => value is not ("windows" or "linux" or "osx")) == true)
            throw new InvalidDataException("extension_manifest_v2_invalid");
        foreach (var list in new[] { manifest.ActivationEvents, manifest.Dependencies, manifest.Configuration })
            if (list is not null && (list.Count > 64 || list.Distinct(StringComparer.Ordinal).Count() != list.Count
                || list.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl))))
                throw new InvalidDataException("extension_declarations_invalid");
        var views = manifest.Provides?.Views ?? [];
        if (views.Count > 16 || views.Select(view => view.Id).Distinct(StringComparer.Ordinal).Count() != views.Count
            || views.Any(view => string.IsNullOrWhiteSpace(view.Id) || view.Id.Length > 80
                || !view.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
                || view.Kind is not ("tree" or "list" or "table" or "form" or "indicator" or "details")
                || view.Source is not ("workspace" or "agents" or "history" or "extension")))
            throw new InvalidDataException("extension_views_invalid");
        if (views.Count > 0 && manifest.Requests?.Capabilities?.Contains("presentation.read", StringComparer.Ordinal) != true)
            throw new InvalidDataException("extension_view_capability_required");
        if (manifest.Runtime is null && (manifest.Provides?.Tools?.Count > 0 || manifest.Provides?.Hooks?.Count > 0
            || manifest.Provides?.ContextContributors?.Count > 0 || manifest.Provides?.Validators?.Count > 0
            || manifest.Provides?.CompletionGates?.Count > 0 || views.Any(view => view.Source == "extension")))
            throw new InvalidDataException("extension_executable_required");
        if (manifest.ActivationEvents?.Any(value => value != "onCommand" && value != "onView"
            && value != "onSession") == true) throw new InvalidDataException("extension_activation_event_invalid");
    }

    private static bool UnsafeRelativePattern(string path)
    {
        var normalized = path.Replace('\\', '/');
        return Path.IsPathRooted(path) || normalized.StartsWith('/')
            || normalized.Split('/').Any(segment => segment is ".." or ".");
    }

}
