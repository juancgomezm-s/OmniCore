namespace OmniCore.Extensions;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>Parses and discovers extensions without launching or initializing their processes.</summary>
public sealed class ExtensionManifestLoader
{
    public const string ManifestFileName = "omnicore-extension.yaml";
    private const string HostApiVersion = "1.0";
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new ExtensionYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public ExtensionManifest Load(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ValidateKeys(yaml);
        var manifest = Deserializer.Deserialize<ExtensionManifest>(yaml)
            ?? throw new InvalidDataException("The extension manifest is empty.");
        Validate(manifest);
        return manifest;
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
                || permissions.Paths?.Write?.Any(string.IsNullOrWhiteSpace) == true))
            throw new InvalidDataException("The extension permission ceiling contains an empty rule.");
        if (manifest.Provides?.Tools?.Any(tool => string.IsNullOrWhiteSpace(tool.Name)
            || string.IsNullOrWhiteSpace(tool.Schema)) == true)
            throw new InvalidDataException("Each extension tool requires a name and a schema path.");
        var requests = new HashSet<string>(manifest.Requests?.Capabilities ?? [], StringComparer.Ordinal);
        var requiredCapabilities = new List<string>();
        if (manifest.Provides?.Tools?.Count > 0) requiredCapabilities.Add("tool.register");
        if (manifest.Provides?.Commands?.Count > 0) requiredCapabilities.Add("command.register");
        if (manifest.Provides?.Skills?.Count > 0) requiredCapabilities.Add("skill.register");
        if (manifest.Provides?.Hooks?.Count > 0) requiredCapabilities.Add("hook.register");
        if (manifest.Provides?.ContextContributors?.Count > 0) requiredCapabilities.Add("context.contribute");
        if (requiredCapabilities.Any(capability => !requests.Contains(capability)))
            throw new InvalidDataException("The manifest does not request a capability required by its declared components.");
    }

    private static void ValidateKeys(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new Parser(new StringReader(yaml)));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("The extension manifest must be a YAML mapping.");
        Check(root, ["manifestVersion", "id", "version", "displayName", "publisher", "omnicoreApi", "provides", "requests", "runtime", "source"]);
        CheckNested(root, "provides", ["tools", "commands", "skills", "hooks", "contextContributors", "validators", "completionGates"]);
        CheckNested(root, "requests", ["capabilities", "permissions"]);
        CheckNested(root, "runtime", ["kind", "command", "args"]);
        CheckNested(root, "source", ["kind", "location", "sha256"]);
    }

    private static void CheckNested(YamlMappingNode root, string key, string[] allowed)
    {
        if (root.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlMappingNode map) Check(map, allowed);
    }

    private static void Check(YamlMappingNode map, string[] allowed)
    {
        foreach (var pair in map.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value;
            if (key is null || !allowed.Contains(key, StringComparer.Ordinal))
                throw new InvalidDataException("Unknown manifest key: " + (key ?? "?"));
        }
    }
}
