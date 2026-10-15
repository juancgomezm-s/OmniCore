namespace OmniCore.Extensions;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>Project-scope skill discovery and explicit loading. Discovery never reads instructions or executes scripts.</summary>
public sealed class SkillManifestLoader
{
    public const string ManifestFileName = "skill.yaml";
    public const string InstructionsFileName = "instructions.md";
    private const int MaximumInstructionsCharacters = 65_536;
    private const int MaximumPortableSkillBytes = 262_144;
    private const int MaximumResourceCharacters = 65_536;
    private const int MaximumTotalResourceCharacters = 262_144;
    public IReadOnlyList<DiscoveredSkill> Discover(string root, ScopeLevel scope = ScopeLevel.Project,
        TrustLevel trust = TrustLevel.Project, bool workspaceTrusted = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (scope is not (ScopeLevel.BuiltIn or ScopeLevel.User or ScopeLevel.Project
                or ScopeLevel.Workspace or ScopeLevel.Session)
            || trust == TrustLevel.Untrusted
            || ((scope is ScopeLevel.Project or ScopeLevel.Workspace) && !workspaceTrusted)
            || (scope == ScopeLevel.BuiltIn && trust != TrustLevel.Core))
            return [];
        var fullRoot = Path.GetFullPath(root);
        if (((scope is ScopeLevel.Project or ScopeLevel.Workspace) && !workspaceTrusted)
            || !Directory.Exists(fullRoot) || HasReparsePointToVolumeRoot(fullRoot)) return [];
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
                skills.Add(new(directory, manifest, scope, trust, hash, SkillLifecycleState.Discovered,
                    ExecutionMetadataDiagnostic(manifest)));
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

    /// <summary>Discovers portable SKILL.md packages without activating their body or metadata capabilities.</summary>
    public IReadOnlyList<DiscoveredSkill> DiscoverPortable(string root, ScopeLevel scope, TrustLevel trust,
        string sourceFormat, bool workspaceTrusted = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (sourceFormat is not ("codex" or "claude" or "opencode")) throw new ArgumentOutOfRangeException(nameof(sourceFormat));
        if (scope is not (ScopeLevel.User or ScopeLevel.Project or ScopeLevel.Workspace)
            || trust is TrustLevel.Untrusted or TrustLevel.ThirdParty
            || scope == ScopeLevel.Project && !workspaceTrusted) return [];
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot) || HasReparsePointToVolumeRoot(fullRoot)) return [];
        var result = new List<DiscoveredSkill>();
        foreach (var directory in Directory.EnumerateDirectories(fullRoot).Order(StringComparer.OrdinalIgnoreCase).Take(128))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var path = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(path) || HasReparsePoint(directory, path)) continue;
            try
            {
                var bytes = ReadBounded(path, MaximumPortableSkillBytes);
                var decoded = new UTF8Encoding(false, true).GetString(bytes);
                if (new OmniCore.Domain.RedactionPolicy().Redact(decoded) != decoded)
                    throw new InvalidDataException("Portable skill contains protected material and was withheld.");
                var portable = ParsePortable(decoded, sourceFormat);
                if (sourceFormat == "codex") portable = portable with { Restrictions = ApplyCodexInvocationPolicy(directory, portable.Restrictions) };
                var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var hash = PortableActivationHash(directory, sourceFormat, bytes);
                var portableState = portable.Diagnostic is null ? SkillLifecycleState.Discovered : SkillLifecycleState.Conflict;
                var manifest = new SkillManifest
                {
                    Id = portable.Name,
                    Version = "1",
                    DisplayName = portable.Name,
                    BudgetTokens = Math.Clamp((portable.Body.Length + 3) / 4, 1, 16_384),
                    ModelInvocable = portable.Restrictions.AllowModelInvocation,
                    Activation = new SkillActivationManifest { Explicit = portable.Restrictions.AllowUserInvocation },
                };
                result.Add(new(directory, manifest, scope,
                    portableState == SkillLifecycleState.Conflict ? TrustLevel.Untrusted : trust, hash, portableState,
                    portable.Diagnostic, InvocationRestrictions: portable.Restrictions, Description: portable.Description,
                    PortableContentSha256: contentHash));
            }
            catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException
                or YamlException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                var safeId = Path.GetFileName(directory).ToLowerInvariant().Replace('_', '-');
                if (!System.Text.RegularExpressions.Regex.IsMatch(safeId, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant)) safeId = "invalid-portable-skill";
                result.Add(new(directory, new SkillManifest { Id = safeId, Version = "invalid" }, scope,
                    TrustLevel.Untrusted, "", SkillLifecycleState.Conflict,
                    "Portable skill is inactive because its bounded metadata could not be validated."));
            }
        }
        return result;
    }

    /// <summary>Discovers one explicitly declared package directory without enumerating siblings.</summary>
    public DiscoveredSkill? DiscoverSingle(string directory, ScopeLevel scope, TrustLevel trust,
        bool workspaceTrusted = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (scope is not (ScopeLevel.BuiltIn or ScopeLevel.User or ScopeLevel.Project
                or ScopeLevel.Workspace or ScopeLevel.Session)
            || trust == TrustLevel.Untrusted
            || ((scope is ScopeLevel.Project or ScopeLevel.Workspace) && !workspaceTrusted)
            || (scope == ScopeLevel.BuiltIn && trust != TrustLevel.Core)) return null;
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full) || HasReparsePointToVolumeRoot(full)) return null;
        var manifestPath = Path.Combine(full, ManifestFileName);
        if (!File.Exists(manifestPath) || (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
            return null;
        var bytes = File.ReadAllBytes(manifestPath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            var manifest = Load(Encoding.UTF8.GetString(bytes));
            return new(full, manifest, scope, trust, hash, SkillLifecycleState.Discovered,
                ExecutionMetadataDiagnostic(manifest));
        }
        catch (Exception exception) when (exception is InvalidDataException or YamlException
            or InvalidOperationException or FormatException or ArgumentException)
        {
            return new(full, new SkillManifest { Id = Path.GetFileName(full), Version = "invalid" }, scope,
                TrustLevel.Untrusted, hash, SkillLifecycleState.Conflict, "Invalid skill manifest: " + exception.Message);
        }
    }

    public SkillManifest Load(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        SkillManifest manifest;
        try
        {
            var root = ManifestYamlAst.ReadRoot(yaml, "skill manifest");
            ManifestYamlAst.CheckKeys(root, "skill manifest", "id", "version", "displayName", "sealed", "priority",
                "modelInvocable", "budgetTokens", "activation", "execution", "permissions", "tools", "contributions");
            manifest = new SkillManifest
            {
                Id = ManifestYamlAst.OptionalString(root, "id", "id"),
                Version = ManifestYamlAst.OptionalString(root, "version", "version"),
                DisplayName = ManifestYamlAst.OptionalString(root, "displayName", "displayName"),
                Sealed = ManifestYamlAst.Optional(root, "sealed") is { } sealedNode && ManifestYamlAst.Bool(sealedNode, "sealed"),
                Priority = ManifestYamlAst.Optional(root, "priority") is { } priorityNode
                    ? ManifestYamlAst.Int(priorityNode, "priority") : 0,
                ModelInvocable = ManifestYamlAst.Optional(root, "modelInvocable") is { } modelInvocableNode
                    && ManifestYamlAst.Bool(modelInvocableNode, "modelInvocable"),
                BudgetTokens = ManifestYamlAst.Optional(root, "budgetTokens") is { } budgetNode
                    ? ManifestYamlAst.Int(budgetNode, "budgetTokens") : 2048,
                Tools = ManifestYamlAst.OptionalStringList(root, "tools", "tools"),
            };
            if (ManifestYamlAst.OptionalMap(root, "activation", "activation", "paths", "extensions", "languages",
                "frameworks", "taskTypes", "explicit") is { } activation)
                manifest.Activation = new SkillActivationManifest
                {
                    Paths = ManifestYamlAst.OptionalStringList(activation, "paths", "activation.paths"),
                    Extensions = ManifestYamlAst.OptionalStringList(activation, "extensions", "activation.extensions"),
                    Languages = ManifestYamlAst.OptionalStringList(activation, "languages", "activation.languages"),
                    Frameworks = ManifestYamlAst.OptionalStringList(activation, "frameworks", "activation.frameworks"),
                    TaskTypes = ManifestYamlAst.OptionalStringList(activation, "taskTypes", "activation.taskTypes"),
                    Explicit = ManifestYamlAst.Optional(activation, "explicit") is { } explicitNode
                        && ManifestYamlAst.Bool(explicitNode, "activation.explicit"),
                };
            if (ManifestYamlAst.OptionalMap(root, "execution", "execution", "context", "preferredModelAlias") is { } execution)
                manifest.Execution = new SkillExecutionManifest
                {
                    Context = ManifestYamlAst.OptionalString(execution, "context", "execution.context"),
                    PreferredModelAlias = ManifestYamlAst.OptionalString(execution, "preferredModelAlias", "execution.preferredModelAlias"),
                };
            if (ManifestYamlAst.OptionalMap(root, "permissions", "permissions", "filesystem", "processes", "network") is { } permissions)
            {
                var result = new SkillPermissionManifest
                {
                    Processes = ManifestYamlAst.OptionalStringList(permissions, "processes", "permissions.processes"),
                    Network = ManifestYamlAst.OptionalStringList(permissions, "network", "permissions.network"),
                };
                if (ManifestYamlAst.OptionalMap(permissions, "filesystem", "permissions.filesystem", "read", "write") is { } filesystem)
                    result.Filesystem = new SkillPathPermissionManifest
                    {
                        Read = ManifestYamlAst.OptionalStringList(filesystem, "read", "permissions.filesystem.read"),
                        Write = ManifestYamlAst.OptionalStringList(filesystem, "write", "permissions.filesystem.write"),
                    };
                manifest.Permissions = result;
            }
            if (ManifestYamlAst.OptionalMap(root, "contributions", "contributions", "tools", "workflows",
                "validators", "contributors", "knowledge") is { } contributions)
                manifest.Contributions = new SkillContributionsManifest
                {
                    Tools = ManifestYamlAst.OptionalStringList(contributions, "tools", "contributions.tools"),
                    Workflows = ManifestYamlAst.OptionalStringList(contributions, "workflows", "contributions.workflows"),
                    Validators = ManifestYamlAst.OptionalStringList(contributions, "validators", "contributions.validators"),
                    Contributors = ManifestYamlAst.OptionalStringList(contributions, "contributors", "contributions.contributors"),
                    Knowledge = ManifestYamlAst.OptionalStringList(contributions, "knowledge", "contributions.knowledge"),
                };
            if (manifest.Contributions?.Tools is { Count: > 0 } declaredTools)
                manifest.Tools = (manifest.Tools ?? []).Concat(declaredTools).Distinct(StringComparer.Ordinal).ToList();
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
            || manifest.BudgetTokens is < 1 or > 16_384
            || manifest.Tools?.Count > 32
            || manifest.Tools?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 160
                || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9._:-]*$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)) == true
            || manifest.Permissions is { } permissionsCeiling && PermissionPatterns(permissionsCeiling).Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 240 || value.Any(char.IsControl)
                || value.Replace('\\', '/').Split('/').Any(part => part == ".."))
            || manifest.Permissions is { } permissionLists && PermissionPatterns(permissionLists).Count() > 64
            || manifest.Permissions?.Processes?.Count > 32 || manifest.Permissions?.Network?.Count > 32
            || manifest.Permissions?.Filesystem?.Read?.Count > 32 || manifest.Permissions?.Filesystem?.Write?.Count > 32
            || manifest.Execution?.Context is { } executionContext && executionContext != "fork"
            || manifest.Execution?.PreferredModelAlias is { } modelAlias && (modelAlias.Length > 64
                || !System.Text.RegularExpressions.Regex.IsMatch(modelAlias, "^[a-zA-Z0-9][a-zA-Z0-9._-]*$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            || manifest.Contributions is { } refs && ResourceLists(refs).Any(pair =>
                pair.List is { } list && (list.Count > 16 || list.Any(value => !SafeAssetPath(value))))
            || manifest.Activation?.Paths?.Any(UnsafePattern) == true
            || manifest.Activation?.Extensions?.Any(value => string.IsNullOrWhiteSpace(value) || !value.StartsWith('.')) == true)
            throw new InvalidDataException("The skill manifest is missing required metadata or contains an invalid activation rule.");
        return manifest;
    }

    private static string? ExecutionMetadataDiagnostic(SkillManifest manifest) =>
        manifest.Execution is { Context: not null } or { PreferredModelAlias: not null }
            ? "Skill execution metadata is parsed for compatibility but has no runtime consumer yet."
            : null;

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
            || skill.Trust is TrustLevel.Untrusted or TrustLevel.ThirdParty)
            throw new InvalidOperationException("The skill source cannot be activated.");
        var directory = Path.GetFullPath(skill.Directory);
        if (HasReparsePointToVolumeRoot(directory))
            throw new InvalidDataException("The skill package scope contains a reparse point.");
        var portablePath = Path.Combine(directory, "SKILL.md");
        if (skill.InvocationRestrictions is { } restrictions)
        {
            var portableBytes = ReadBounded(portablePath, MaximumPortableSkillBytes);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(portableBytes)), skill.PortableContentSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException("The portable skill changed after discovery.");
            var portable = ParsePortable(new UTF8Encoding(false, true).GetString(portableBytes), restrictions.SourceFormat);
            if (restrictions.SourceFormat == "codex")
                portable = portable with { Restrictions = ApplyCodexInvocationPolicy(directory, portable.Restrictions) };
            if (portable.Restrictions != restrictions || portable.Name != skill.Manifest.Id
                || !string.Equals(PortableActivationHash(directory, restrictions.SourceFormat, portableBytes),
                    skill.ManifestSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The portable skill policy changed after discovery.");
            var body = new RedactionPolicy().Redact(portable.Body);
            if (!string.Equals(body, portable.Body, StringComparison.Ordinal))
                throw new InvalidDataException("The portable skill contains protected secret material and was not loaded.");
            if (Math.Max(1, (body.Length + 3) / 4) > skill.Manifest.BudgetTokens)
                throw new InvalidDataException("Portable skill instructions exceed the declared context budget.");
            return new(skill with { State = SkillLifecycleState.Loaded }, body,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), [], skill.ManifestSha256);
        }
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
        var estimatedTokens = Math.Max(1, (instructions.Length + 3) / 4);
        if (estimatedTokens > manifest.BudgetTokens)
            throw new InvalidDataException("The skill instructions exceed their declared context token budget.");
        var resources = new List<LoadedSkillResource>();
        var totalCharacters = 0;
        foreach (var (kind, paths) in ResourceLists(manifest.Contributions ?? new()))
        foreach (var relativePath in paths ?? [])
        {
            var fullPath = Path.GetFullPath(Path.Combine(directory,
                relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !File.Exists(fullPath) || HasReparsePoint(directory, fullPath))
                throw new InvalidDataException("A skill contribution is missing or linked outside its package.");
            var bytes = File.ReadAllBytes(fullPath);
            string content;
            try { content = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException exception)
            { throw new InvalidDataException("A skill contribution is not valid UTF-8.", exception); }
            if (content.Length > MaximumResourceCharacters
                || totalCharacters > MaximumTotalResourceCharacters - content.Length)
                throw new InvalidDataException("Skill contributions exceed their aggregate size limit.");
            totalCharacters += content.Length;
            resources.Add(new(kind, relativePath, content, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        var loadedTokens = resources.Where(resource => resource.Kind is not SkillResourceKind.Validator)
            .Sum(resource => Math.Max(1, (resource.Content.Length + 3) / 4));
        if (loadedTokens > manifest.BudgetTokens - estimatedTokens)
            throw new InvalidDataException("Loaded skill contributions exceed their declared context token budget.");
        var instructionsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)));
        var identity = "instructions:" + instructionsHash + "\n" + string.Join("\n", resources
            .OrderBy(resource => resource.Kind).ThenBy(resource => resource.RelativePath, StringComparer.Ordinal)
            .Select(resource => resource.Kind + ":" + resource.RelativePath + ":" + resource.Sha256));
        var packageContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        if (skill.ParentPackageSha256 is { Length: > 0 } parentHash)
            packageContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                "parent:" + skill.ParentPackageId + ":" + parentHash + ":" + packageContentHash)));
        return new(skill with { Manifest = manifest, State = SkillLifecycleState.Loaded }, instructions,
            instructionsHash, resources.AsReadOnly(), packageContentHash);
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 8_192, FileOptions.SequentialScan);
        if (stream.Length is < 1 || stream.Length > maximumBytes)
            throw new InvalidDataException("Portable skill metadata exceeds its byte limit.");
        var bytes = new byte[checked((int)stream.Length)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0) throw new InvalidDataException("Portable skill changed while being read.");
            read += count;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("Portable skill changed while being read.");
        return bytes;
    }

    private static PortableSkill ParsePortable(string text, string sourceFormat)
    {
        if (text.Length > MaximumPortableSkillBytes || !text.StartsWith("---\n", StringComparison.Ordinal)
            && !text.StartsWith("---\r\n", StringComparison.Ordinal))
            throw new InvalidDataException("Portable SKILL.md requires bounded YAML frontmatter.");
        var lines = text.Split('\n');
        var closeLine = -1;
        var frontmatterCharacters = 0;
        for (var index = 1; index < lines.Length; index++)
        {
            frontmatterCharacters += lines[index].Length + 1;
            if (frontmatterCharacters > 16_384) break;
            if (lines[index].TrimEnd('\r') == "---") { closeLine = index; break; }
        }
        if (closeLine < 0) throw new InvalidDataException("Portable skill frontmatter is missing or too large.");
        var yaml = string.Join('\n', lines[1..closeLine]).TrimEnd('\r');
        var body = string.Join('\n', lines[(closeLine + 1)..]);
        var root = ManifestYamlAst.ReadRoot(yaml, "portable skill frontmatter");
        var name = ManifestYamlAst.OptionalString(root, "name", "name");
        var description = ManifestYamlAst.OptionalString(root, "description", "description");
        if (string.IsNullOrWhiteSpace(name)) name = "";
        if (name.Length > 64
            || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || string.IsNullOrWhiteSpace(description) || description.Length > 1_024 || body.Length > MaximumInstructionsCharacters)
            throw new InvalidDataException("Portable skill name, description, or body is invalid.");
        var redaction = new OmniCore.Domain.RedactionPolicy();
        if (redaction.Redact(name) != name || redaction.Redact(description) != description)
            throw new InvalidDataException("Portable skill metadata contains protected material and was withheld.");
        var allowUser = true;
        var allowModel = true;
        var allowAutomatic = true;
        string? diagnostic = null;
        if (sourceFormat == "claude")
        {
            if (name.Length == 0) throw new InvalidDataException("Claude SKILL.md without a name is not supported by this bounded adapter.");
            allowModel = ManifestYamlAst.Optional(root, "disable-model-invocation") is not { } disabled
                || !ManifestYamlAst.Bool(disabled, "disable-model-invocation");
            allowAutomatic = allowModel;
            allowUser = ManifestYamlAst.Optional(root, "user-invocable") is not { } user
                || ManifestYamlAst.Bool(user, "user-invocable");
            if (ManifestYamlAst.Optional(root, "context") is not null || ManifestYamlAst.Optional(root, "hooks") is not null
                || ManifestYamlAst.Optional(root, "paths") is not null
                || ManifestYamlAst.Optional(root, "model") is not null || body.Contains("!`", StringComparison.Ordinal))
            {
                allowAutomatic = false;
                allowModel = false;
                allowUser = false;
                diagnostic = "Unsupported Claude execution features make this skill inactive; paths, context forks, scripts, hooks, model overrides, and shell substitutions are never executed.";
            }
        }
        else if (sourceFormat == "codex")
        {
            allowAutomatic = true;
        }
        else
        {
            if (ManifestYamlAst.Optional(root, "metadata") is { } metadataNode)
            {
                if (metadataNode is not YamlMappingNode metadata) throw ManifestYamlAst.WrongType("metadata");
                if (ManifestYamlAst.Optional(metadata, "opencode/autoinvoke") is { } auto)
                {
                    var enabled = PortableBoolean(auto, "metadata.opencode/autoinvoke");
                    allowAutomatic = enabled;
                    allowModel = enabled;
                }
                if (ManifestYamlAst.Optional(metadata, "opencode/slash") is { } slash)
                    allowUser = PortableBoolean(slash, "metadata.opencode/slash");
            }
        }
        if (name.Length == 0) throw new InvalidDataException("Portable skills without a valid name are not supported.");
        return new(name, description ?? "", body, new(allowAutomatic, allowModel, allowUser, sourceFormat), diagnostic);
    }

    private static bool PortableBoolean(YamlNode node, string description) => node is YamlScalarNode scalar
        && (bool.TryParse(scalar.Value, out var boolean) ? boolean
            : string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase)
                ? true : string.Equals(scalar.Value, "false", StringComparison.OrdinalIgnoreCase)
                    ? false : throw ManifestYamlAst.WrongType(description));

    private sealed record PortableSkill(string Name, string Description, string Body, SkillInvocationRestrictions Restrictions,
        string? Diagnostic);

    private static SkillInvocationRestrictions ApplyCodexInvocationPolicy(string directory,
        SkillInvocationRestrictions restrictions)
    {
        var policyPath = Path.Combine(directory, "agents", "openai.yaml");
        if (!File.Exists(policyPath)) return restrictions;
        if (HasReparsePoint(directory, policyPath))
            return restrictions with { AllowAutomatic = false, AllowModelInvocation = false };
        try
        {
            var bytes = ReadBounded(policyPath, 8_192);
            var yaml = new UTF8Encoding(false, true).GetString(bytes);
            var root = ManifestYamlAst.ReadRoot(yaml, "Codex openai policy");
            if (ManifestYamlAst.Optional(root, "policy") is not { } policyNode) return restrictions;
            if (policyNode is not YamlMappingNode policy) throw ManifestYamlAst.WrongType("Codex policy");
            if (ManifestYamlAst.Optional(policy, "allow_implicit_invocation") is not { } allow) return restrictions;
            var enabled = ManifestYamlAst.Bool(allow, "allow_implicit_invocation");
            return restrictions with { AllowAutomatic = enabled, AllowModelInvocation = enabled };
        }
        catch (Exception exception) when (exception is InvalidDataException or YamlException or DecoderFallbackException)
        {
            return restrictions with { AllowAutomatic = false, AllowModelInvocation = false };
        }
    }

    private static string PortableActivationHash(string directory, string sourceFormat, byte[] skillBytes)
    {
        var canonicalDirectory = Path.GetFullPath(directory).Replace(Path.DirectorySeparatorChar, '/');
        if (OperatingSystem.IsWindows()) canonicalDirectory = canonicalDirectory.ToUpperInvariant();
        var policyPath = Path.Combine(directory, "agents", "openai.yaml");
        var policyBytes = Array.Empty<byte>();
        if (sourceFormat == "codex" && File.Exists(policyPath))
        {
            if (HasReparsePoint(directory, policyPath)) throw new InvalidDataException("Codex skill policy is linked outside its package.");
            policyBytes = ReadBounded(policyPath, 8_192);
            var policyText = new UTF8Encoding(false, true).GetString(policyBytes);
            if (new RedactionPolicy().Redact(policyText) != policyText)
                throw new InvalidDataException("Codex skill policy contains protected material and was withheld.");
        }
        var policyHash = policyBytes.Length == 0 ? "absent" : Convert.ToHexStringLower(SHA256.HashData(policyBytes));
        var material = "portable-skill-v2\n" + sourceFormat + "\n" + canonicalDirectory + "\n"
            + Convert.ToHexStringLower(SHA256.HashData(skillBytes)) + "\n" + policyHash;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
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

    private static bool SafeAssetPath(string value)
    {
        var normalized = value.Replace('\\', '/');
        var extension = Path.GetExtension(normalized);
        return value.Length is > 0 and <= 240 && !Path.IsPathRooted(value)
            && !value.Any(char.IsControl)
            && !normalized.Split('/').Any(part => part is ".." or "." or "")
            && extension is ".md" or ".yaml" or ".yml" or ".json";
    }

    private static IEnumerable<(SkillResourceKind Kind, List<string>? List)> ResourceLists(SkillContributionsManifest manifest)
    {
        yield return (SkillResourceKind.Workflow, manifest.Workflows);
        yield return (SkillResourceKind.Validator, manifest.Validators);
        yield return (SkillResourceKind.Contributor, manifest.Contributors);
        yield return (SkillResourceKind.Knowledge, manifest.Knowledge);
    }

    private static IEnumerable<string> PermissionPatterns(SkillPermissionManifest permissions) =>
        (permissions.Filesystem?.Read ?? []).Concat(permissions.Filesystem?.Write ?? [])
            .Concat(permissions.Processes ?? []).Concat(permissions.Network ?? []);

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

}
