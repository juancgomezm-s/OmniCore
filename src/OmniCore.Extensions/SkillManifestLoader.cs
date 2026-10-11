namespace OmniCore.Extensions;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>Project-scope skill discovery and explicit loading. Discovery never reads instructions or executes scripts.</summary>
public sealed class SkillManifestLoader
{
    public const string ManifestFileName = "skill.yaml";
    public const string InstructionsFileName = "instructions.md";
    private const int MaximumInstructionsCharacters = 65_536;
    // Fixed manifest DTO graph; see ExtensionManifestLoader and upstream issue #1124. M8 has no AOT binary.
#pragma warning disable IL3050
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
#pragma warning restore IL3050

    public IReadOnlyList<DiscoveredSkill> Discover(string root, ScopeLevel scope = ScopeLevel.Project,
        TrustLevel trust = TrustLevel.Project, bool workspaceTrusted = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (scope != ScopeLevel.Project || trust is TrustLevel.Untrusted or TrustLevel.ThirdParty)
            return [];
        var fullRoot = Path.GetFullPath(root);
        if (!workspaceTrusted || !Directory.Exists(fullRoot) || HasReparsePointToVolumeRoot(fullRoot)) return [];
        var skills = new List<DiscoveredSkill>();
        foreach (var directory in Directory.EnumerateDirectories(fullRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath) || (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0) continue;
            var bytes = File.ReadAllBytes(manifestPath);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            try
            {
                var manifest = Load(Encoding.UTF8.GetString(bytes));
                skills.Add(new(directory, manifest, scope, trust, hash, SkillLifecycleState.Discovered, null));
            }
            catch (Exception exception) when (exception is InvalidDataException or YamlException
                or InvalidOperationException or FormatException or ArgumentException)
            {
                // Invalid metadata is visible to diagnostics but never eligible for loading.
                skills.Add(new(directory, new SkillManifest { Id = Path.GetFileName(directory), Version = "invalid" },
                    scope, TrustLevel.Untrusted, hash, SkillLifecycleState.Conflict, "Invalid skill manifest: " + exception.Message));
            }
        }
        return skills;
    }

    public SkillManifest Load(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        SkillManifest manifest;
        try
        {
            ValidateKeys(yaml);
            manifest = Deserializer.Deserialize<SkillManifest>(yaml)
                ?? throw new InvalidDataException("The skill manifest is empty.");
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException("The skill manifest YAML is malformed.", exception);
        }
        if (string.IsNullOrWhiteSpace(manifest.Id)
            || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.Priority is < -100 or > 100
            || manifest.Tools?.Any(string.IsNullOrWhiteSpace) == true
            || manifest.Activation?.Paths?.Any(UnsafePattern) == true
            || manifest.Activation?.Extensions?.Any(value => string.IsNullOrWhiteSpace(value) || !value.StartsWith('.')) == true)
            throw new InvalidDataException("The skill manifest is missing required metadata or contains an invalid activation rule.");
        return manifest;
    }

    public DiscoveredSkill EvaluateEligibility(DiscoveredSkill skill, string? relativePath = null,
        string? language = null, string? framework = null, string? taskType = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.State == SkillLifecycleState.Conflict) return skill;
        var activation = skill.Manifest.Activation;
        if (activation is null) return skill;
        var hasContextRules = activation.Paths?.Count > 0 || activation.Extensions?.Count > 0
            || activation.Languages?.Count > 0 || activation.Frameworks?.Count > 0 || activation.TaskTypes?.Count > 0;
        if (!hasContextRules) return skill;
        var eligible = activation.Paths?.Any(pattern => relativePath is not null && Glob(pattern, relativePath)) == true
            || activation.Extensions?.Any(value => string.Equals(value, Path.GetExtension(relativePath ?? ""), StringComparison.OrdinalIgnoreCase)) == true
            || activation.Languages?.Any(value => string.Equals(value, language, StringComparison.OrdinalIgnoreCase)) == true
            || activation.Frameworks?.Any(value => string.Equals(value, framework, StringComparison.OrdinalIgnoreCase)) == true
            || activation.TaskTypes?.Any(value => string.Equals(value, taskType, StringComparison.OrdinalIgnoreCase)) == true;
        return eligible ? skill with { State = SkillLifecycleState.Eligible } : skill;
    }

    /// <summary>Applies scope and trust precedence without allowing a less trusted source to hide another.</summary>
    public static IReadOnlyList<DiscoveredSkill> ResolvePrecedence(IEnumerable<DiscoveredSkill> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var resolved = new List<DiscoveredSkill>();
        foreach (var group in candidates.GroupBy(candidate => candidate.Manifest.Id, StringComparer.Ordinal))
        {
            var items = group.ToArray();
            var sealedSource = items.FirstOrDefault(item => item.Manifest.Sealed
                && item.Scope == ScopeLevel.BuiltIn);
            if (sealedSource is not null)
            {
                resolved.Add(sealedSource);
                resolved.AddRange(items.Where(item => !ReferenceEquals(item, sealedSource))
                    .Select(item => item with { State = SkillLifecycleState.Conflict,
                        Diagnostic = "A sealed BuiltIn skill cannot be hidden." }));
                continue;
            }
            var mostSpecificScope = items.Max(item => item.Scope);
            var mostSpecific = items.Where(item => item.Scope == mostSpecificScope).ToArray();
            if (mostSpecific.Length != 1 || items.Any(item => item.Trust < mostSpecific[0].Trust))
            {
                resolved.AddRange(items.Select(item => item with { State = SkillLifecycleState.Conflict,
                    Diagnostic = "Skill scope and trust precedence is ambiguous." }));
                continue;
            }
            resolved.Add(mostSpecific[0]);
            resolved.AddRange(items.Where(item => !ReferenceEquals(item, mostSpecific[0]))
                .Select(item => item with { State = SkillLifecycleState.Released,
                    Diagnostic = "A more specific skill with compatible trust takes precedence." }));
        }
        return resolved.OrderBy(item => item.Scope).ThenBy(item => item.Manifest.Priority)
            .ThenBy(item => item.Manifest.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Explicitly materializes the bounded instruction body after trust and activation checks.</summary>
    public LoadedSkill Activate(DiscoveredSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.State is SkillLifecycleState.Conflict or SkillLifecycleState.Released
            || skill.Trust is TrustLevel.Untrusted or TrustLevel.ThirdParty
            || skill.Scope != ScopeLevel.Project)
            throw new InvalidOperationException("The skill source cannot be activated.");
        var directory = Path.GetFullPath(skill.Directory);
        if (HasReparsePointToVolumeRoot(directory))
            throw new InvalidDataException("The skill package scope contains a reparse point.");
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), skill.ManifestSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("The skill manifest changed after discovery.");
        var instructionsPath = Path.Combine(directory, InstructionsFileName);
        if (!File.Exists(instructionsPath) || HasReparsePoint(directory, instructionsPath))
            throw new InvalidDataException("The skill instructions are missing or linked outside the package.");
        var instructions = File.ReadAllText(instructionsPath, Encoding.UTF8);
        if (instructions.Length > MaximumInstructionsCharacters)
            throw new InvalidDataException("The skill instructions exceed the size limit.");
        var manifest = Load(Encoding.UTF8.GetString(manifestBytes));
        return new(skill with { Manifest = manifest, State = SkillLifecycleState.Loaded }, instructions,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions))));
    }

    private static bool HasReparsePoint(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(root);
        while (!string.Equals(current, boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("The skill package path is invalid.");
        }
        return (File.GetAttributes(boundary) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool HasReparsePointToVolumeRoot(string path)
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

    private static bool UnsafePattern(string value)
    {
        var normalized = value.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)
            || normalized.Split('/').Any(part => part is ".." or ".");
    }

    private static bool Glob(string pattern, string path)
    {
        var normalizedPattern = pattern.Replace('\\', '/');
        var normalizedPath = path.Replace('\\', '/');
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(normalizedPattern)
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(normalizedPath, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static void ValidateKeys(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new Parser(new StringReader(yaml)));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("The skill manifest must be a YAML mapping.");
        Check(root, ["id", "version", "displayName", "sealed", "priority", "modelInvocable", "activation", "execution", "tools"]);
        if (root.Children.TryGetValue(new YamlScalarNode("activation"), out var activation)
            && activation is YamlMappingNode activationNode)
            Check(activationNode, ["paths", "extensions", "languages", "frameworks", "taskTypes", "explicit"]);
        if (root.Children.TryGetValue(new YamlScalarNode("execution"), out var execution)
            && execution is YamlMappingNode executionNode)
            Check(executionNode, ["context", "preferredModelAlias"]);
    }

    private static void Check(YamlMappingNode node, IReadOnlyList<string> allowed)
    {
        foreach (var pair in node.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value;
            if (key is null || !allowed.Contains(key))
                throw new InvalidDataException("Unknown skill manifest field: " + (key ?? "<non-scalar>"));
        }
    }
}
