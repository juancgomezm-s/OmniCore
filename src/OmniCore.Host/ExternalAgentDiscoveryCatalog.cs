namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;
using OmniCore.Extensions;

public enum ExternalAgentSourceKind { Skill, Instruction, Memory }

public sealed record ExternalAgentSource(ExternalAgentSourceKind Kind, string Adapter, string Scope,
    string DisplayPath, string Sha256, string State, string? Diagnostic, string? Content = null,
    DiscoveredSkill? Skill = null, bool RequiresUserConsent = false, string? ActivationSha256 = null);

/// <summary>Bounded adapter over documented local agent locations. It never walks unrelated home/project trees.</summary>
public sealed class ExternalAgentDiscoveryCatalog
{
    private const int MaximumDocuments = 256;
    private const int MaximumTextBytes = 64 * 1024;
    private readonly string _home;
    private readonly Func<string, string?> _environment;

    public ExternalAgentDiscoveryCatalog(string? homeDirectory = null, Func<string, string?>? environment = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        var configuredAgentHome = _environment("OMNICORE_AGENT_HOME");
        _home = Path.GetFullPath(homeDirectory ?? (string.IsNullOrWhiteSpace(configuredAgentHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : configuredAgentHome));
    }

    public IReadOnlyList<ExternalAgentSource> Discover(string? workspaceRoot, string? activeDirectory,
        bool workspaceTrusted, bool allowGlobalMemory, CancellationToken cancellationToken = default,
        string? consentedProjectIdentity = null, Func<string, bool>? projectTrustResolver = null)
    {
        var sources = new List<ExternalAgentSource>();
        var roots = new List<(string Path, string Adapter, string Scope, bool Trusted, string? Format)>();
        var configuredRoot = workspaceRoot is null ? null : CanonicalDirectory(workspaceRoot);
        var active = activeDirectory is null ? configuredRoot : CanonicalDirectory(activeDirectory);
        // Keep the runtime workspace unchanged. Discovery may use a verified repository root
        // above a nested cwd only when that root has its own trust decision from the caller.
        var projectRoot = DiscoveryProjectRoot(configuredRoot, active);
        var projectTrusted = projectRoot is null ? false
            : PathComparer.Equals(projectRoot, configuredRoot) ? workspaceTrusted
            : SafeProjectTrust(projectTrustResolver, projectRoot);
        if (projectRoot is not null && active is not null && IsWithin(projectRoot, active))
        {
            foreach (var directory in Ancestors(projectRoot, active))
            {
                roots.Add((Path.Combine(directory, ".agents", "skills"), "Codex", "Project", projectTrusted, "codex"));
                roots.Add((Path.Combine(directory, ".claude", "skills"), "Claude", "Project", projectTrusted, "claude"));
                roots.Add((Path.Combine(directory, ".opencode", "skills"), "OpenCode", "Project", projectTrusted, "opencode"));
            }
        }

        var codexHome = ConfigRoot("CODEX_HOME", Path.Combine(_home, ".codex"));
        var claudeHome = ConfigRoot("CLAUDE_CONFIG_DIR", Path.Combine(_home, ".claude"));
        var xdg = ConfigRoot("XDG_CONFIG_HOME", Path.Combine(_home, ".config"));
        var openCodeHome = ConfigRoot("OPENCODE_CONFIG_DIR", Path.Combine(xdg, "opencode"));
        roots.Add((Path.Combine(_home, ".agents", "skills"), "Codex", "User", true, "codex"));
        roots.Add((Path.Combine(codexHome, "skills"), "Codex", "User", true, "codex"));
        roots.Add((Path.Combine(codexHome, "skills", ".system"), "Codex", "User", true, "codex"));
        roots.Add((Path.Combine(claudeHome, "skills"), "Claude", "User", true, "claude"));
        roots.Add((Path.Combine(openCodeHome, "skills"), "OpenCode", "User", true, "opencode"));
        AddUserInstructions(codexHome, claudeHome, openCodeHome, sources, cancellationToken);
        if (projectRoot is not null && active is not null && IsWithin(projectRoot, active))
            AddInstructions(projectRoot, active, projectTrusted, sources, cancellationToken);

        if (projectRoot is not null && active is not null && IsWithin(projectRoot, active))
            AddConventionalProjectMemories(projectRoot, active, projectTrusted,
                string.Equals(consentedProjectIdentity, ProjectIdentity(projectRoot), StringComparison.Ordinal),
                sources, cancellationToken);

        var seenRoots = new HashSet<string>(PathComparer);
        var loader = new SkillManifestLoader();
        foreach (var root in roots.Take(64))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = CanonicalDirectory(root.Path);
            if (canonical is null || !seenRoots.Add(canonical)) continue;
            if (!root.Trusted)
            {
                // Expose only the root itself; never read project metadata or bodies before trust.
                if (Directory.Exists(canonical)) sources.Add(new(ExternalAgentSourceKind.Skill,
                    root.Adapter, root.Scope, SafeDisplayPath(root.Path), "", "inactive",
                    "Workspace trust is required before reading project skills."));
                continue;
            }
            foreach (var skill in loader.DiscoverPortable(canonical,
                         root.Scope == "User" ? ScopeLevel.User : ScopeLevel.Project,
                         root.Scope == "User" ? TrustLevel.Trusted : TrustLevel.Project,
                         root.Format!, workspaceTrusted: root.Trusted).Take(MaximumDocuments))
            {
                var adapterPath = Path.Combine(skill.Directory, "SKILL.md");
                sources.Add(new(ExternalAgentSourceKind.Skill, root.Adapter, root.Scope,
                    SafeDisplayPath(adapterPath), skill.PortableContentSha256 ?? "", skill.State.ToString(),
                    SafeDiagnostic(skill.Diagnostic), Skill: skill, ActivationSha256: skill.ManifestSha256));
                if (sources.Count >= MaximumDocuments) break;
            }
            if (sources.Count >= MaximumDocuments) break;
        }

        AddClaudeProjectMemories(claudeHome, projectRoot, projectTrusted, sources, allowGlobalMemory,
            consentedProjectIdentity, cancellationToken);
        AddClaudeAutoMemoryDirectory(claudeHome, sources, allowGlobalMemory, cancellationToken);
        AddCodexMemories(codexHome, sources, allowGlobalMemory, cancellationToken);
        var parallelInstructions = sources.Where(source => source.Kind == ExternalAgentSourceKind.Instruction)
            .GroupBy(source => Path.GetDirectoryName(source.DisplayPath), PathComparer)
            .Where(group => group.Select(source => source.Adapter).Distinct(StringComparer.Ordinal).Count() > 1)
            .SelectMany(group => group).ToHashSet();
        for (var index = 0; index < sources.Count; index++)
            if (parallelInstructions.Contains(sources[index]))
                sources[index] = sources[index] with { Diagnostic = "Several agent instruction files apply at this directory; they remain separate untrusted guidance and OmniCore does not silently choose one." };
        return sources.Take(MaximumDocuments).ToArray();
    }

    /// <summary>Returns only a Git project identity whose worktree metadata has been verified.</summary>
    public static string? VerifiedRepositoryRoot(string? path) => path is null ? null : GitProjectRoot(Path.GetFullPath(path));

    public static string? DiscoveryProjectRoot(string? workspaceRoot, string? activeDirectory)
    {
        var configuredRoot = workspaceRoot is null ? null : CanonicalDirectory(workspaceRoot);
        var active = activeDirectory is null ? configuredRoot : CanonicalDirectory(activeDirectory);
        var verifiedRepository = active is null ? null : GitWorktreeRoot(active);
        return configuredRoot is null ? verifiedRepository ?? active
            : verifiedRepository is not null && IsWithinOrEqual(verifiedRepository, configuredRoot)
                && IsWithinOrEqual(configuredRoot, active ?? configuredRoot)
                ? verifiedRepository : configuredRoot;
    }

    private static string? GitWorktreeRoot(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            var marker = Path.Combine(current, ".git");
            if ((Directory.Exists(marker) || File.Exists(marker)) && !HasReparsePoint(marker))
                return GitProjectRoot(current) is null ? null : current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) return null;
            current = parent;
        }
    }

    private static bool SafeProjectTrust(Func<string, bool>? resolver, string root)
    {
        try { return resolver?.Invoke(root) == true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    private void AddInstructions(string root, string active, bool trusted, List<ExternalAgentSource> output,
        CancellationToken token)
    {
        var directories = Ancestors(root, active).ToArray();
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            var agentsOverride = Path.Combine(directory, "AGENTS.override.md");
            // Match the initializer's filename on case-sensitive filesystems as well.
            // Conflicting case variants never win by directory enumeration order.
            var variants = trusted ? Directory.EnumerateFiles(directory)
                .Where(path => Path.GetFileName(path).Equals("agents.md", StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray() : [];
            var agents = variants.Length == 1 ? variants[0] : Path.Combine(directory, "AGENTS.md");
            if (variants.Length > 1)
            {
                output.Add(new(ExternalAgentSourceKind.Instruction, "Codex", "Project",
                    SafeDisplayPath(agents), "", "inactive", "Ambiguous case variants of agents.md; resolve the conflict before loading."));
                continue;
            }
            if (trusted && HasNonEmptyText(agentsOverride)) AddDocument(agentsOverride, "Codex", "Project", true, output, token,
                "override replaces AGENTS.md at this directory");
            else AddDocument(agents, "Codex", "Project", trusted, output, token, null);
            AddDocument(Path.Combine(directory, "CLAUDE.md"), "Claude", "Project", trusted, output, token, null,
                authorizedRoot: root);
            AddDocument(Path.Combine(directory, ".claude", "CLAUDE.md"), "Claude", "Project", trusted, output, token, null,
                authorizedRoot: root);
            AddDocument(Path.Combine(directory, "CLAUDE.local.md"), "Claude", "Project", trusted, output, token, null,
                authorizedRoot: root);
            AddDocument(Path.Combine(directory, ".opencode", "AGENTS.md"), "OpenCode", "Project", trusted, output, token, null);
            AddOpenCodeRulesDiagnostic(Path.Combine(directory, "opencode.json"), "Project", trusted, output, token, root);
            AddOpenCodeRulesDiagnostic(Path.Combine(directory, "opencode.jsonc"), "Project", trusted, output, token, root);
            AddOpenCodeRulesDiagnostic(Path.Combine(directory, ".opencode", "opencode.json"), "Project", trusted, output, token, root);
            AddOpenCodeRulesDiagnostic(Path.Combine(directory, ".opencode", "opencode.jsonc"), "Project", trusted, output, token, root);
        }
    }

    private void AddUserInstructions(string codex, string claude, string openCode, List<ExternalAgentSource> output,
        CancellationToken token)
    {
        var codexOverride = Path.Combine(codex, "AGENTS.override.md");
        if (HasNonEmptyText(codexOverride)) AddDocument(codexOverride, "Codex", "User", true, output, token, null);
        else AddDocument(Path.Combine(codex, "AGENTS.md"), "Codex", "User", true, output, token, null);
        AddDocument(Path.Combine(claude, "CLAUDE.md"), "Claude", "User", true, output, token, null,
            authorizedRoot: claude);
        AddDocument(Path.Combine(openCode, "AGENTS.md"), "OpenCode", "User", true, output, token, null);
        AddOpenCodeRulesDiagnostic(Path.Combine(openCode, "opencode.json"), "User", true, output, token, openCode);
        AddOpenCodeRulesDiagnostic(Path.Combine(openCode, "opencode.jsonc"), "User", true, output, token, openCode);
    }

    private void AddConventionalProjectMemories(string root, string active, bool trusted, bool allowProjectMemory,
        List<ExternalAgentSource> output, CancellationToken token)
    {
        foreach (var directory in Ancestors(root, active))
        {
            AddProjectMemory(Path.Combine(directory, "MEMORY.md"), "OmniCore convention", trusted, allowProjectMemory, output, token);
            AddProjectMemory(Path.Combine(directory, ".claude", "memory", "MEMORY.md"), "Claude convention", trusted, allowProjectMemory, output, token);
            AddProjectMemory(Path.Combine(directory, ".agents", "memory", "MEMORY.md"), "Agents convention", trusted, allowProjectMemory, output, token);
            AddProjectMemory(Path.Combine(directory, ".opencode", "MEMORY.md"), "OpenCode convention", trusted, allowProjectMemory, output, token);
        }
    }

    private void AddProjectMemory(string path, string adapter, bool trusted, bool allow,
        List<ExternalAgentSource> output, CancellationToken token, string scope = "Project")
    {
        if (!File.Exists(path)) return;
        if (!trusted)
        {
            output.Add(new(ExternalAgentSourceKind.Memory, adapter, scope, SafeDisplayPath(path), "", "inactive",
                "Workspace trust is required before reading project memory."));
            return;
        }
        if (!allow)
        {
            output.Add(new(ExternalAgentSourceKind.Memory, adapter, scope, SafeDisplayPath(path), "", "awaiting-consent",
                "External memory requires a human Ask decision.", RequiresUserConsent: true));
            return;
        }
        AddDocument(path, adapter, scope, true, output, token, null, ExternalAgentSourceKind.Memory);
    }

    private void AddClaudeAutoMemoryDirectory(string claudeHome, List<ExternalAgentSource> output,
        bool allow, CancellationToken token)
    {
        var settingsPath = Path.Combine(claudeHome, "settings.json");
        if (!File.Exists(settingsPath)) return;
        try
        {
            if (new RedactionPolicy().IsSecretPath(settingsPath) || HasReparsePoint(settingsPath)) return;
            using var settings = System.Text.Json.JsonDocument.Parse(ReadTextBytes(settingsPath),
                new System.Text.Json.JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            if (!settings.RootElement.TryGetProperty("autoMemoryDirectory", out var configured)
                || configured.ValueKind != System.Text.Json.JsonValueKind.String
                || string.IsNullOrWhiteSpace(configured.GetString())) return;
            var value = configured.GetString()!;
            if (value.Length > 1_024 || value.Any(char.IsControl)) return;
            var memoryDirectory = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(claudeHome, value));
            if (new RedactionPolicy().IsSecretPath(memoryDirectory) || HasReparsePoint(memoryDirectory)) return;
            var root = CanonicalDirectory(memoryDirectory);
            if (root is null)
            {
                if (!allow) output.Add(new(ExternalAgentSourceKind.Memory, "Claude auto memory", "User",
                    SafeDisplayPath(memoryDirectory), "", "awaiting-consent",
                    "Claude auto memory requires an explicit Ask decision.", RequiresUserConsent: true));
                return;
            }
            AddProjectMemory(Path.Combine(root, "MEMORY.md"), "Claude auto memory", true, allow, output, token, "User");
            foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly)
                         .Where(file => !string.Equals(Path.GetFileName(file), "MEMORY.md", StringComparison.OrdinalIgnoreCase))
                         .Take(32))
                AddProjectMemory(file, "Claude auto memory", true, allow, output, token, "User");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException or ArgumentException or DecoderFallbackException)
        {
            output.Add(new(ExternalAgentSourceKind.Memory, "Claude auto memory", "User",
                SafeDisplayPath(settingsPath), "", "error", "Claude auto-memory settings are malformed or unavailable."));
        }
    }

    private void AddClaudeProjectMemories(string claudeHome, string? projectRoot, bool workspaceTrusted,
        List<ExternalAgentSource> output, bool allow, string? consentedProjectIdentity, CancellationToken token)
    {
        if (projectRoot is null) return;
        // Claude encodes the project path into one exact directory name; do not enumerate sibling projects.
        var identity = ProjectIdentity(projectRoot);
        var key = ClaudeProjectKey(identity);
        // A user's native Claude project key is lossy for every path. Read only the exact
        // project mapping explicitly authorized from this active workspace.
        var projectAllowed = string.Equals(consentedProjectIdentity, identity,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var memoryRoot = Path.Combine(claudeHome, "projects", key, "memory");
        if (!workspaceTrusted)
        {
            if (Directory.Exists(memoryRoot)) output.Add(new(ExternalAgentSourceKind.Memory, "Claude", "Project",
                SafeDisplayPath(memoryRoot), "", "inactive", "Workspace trust is required before reading project memory."));
            return;
        }
        if (!projectAllowed)
        {
            if (Directory.Exists(memoryRoot)) output.Add(new(ExternalAgentSourceKind.Memory, "Claude", "Project",
                SafeDisplayPath(memoryRoot), "", "awaiting-consent", "External memory requires a human Ask decision.",
                RequiresUserConsent: true));
            return;
        }
        AddDocument(Path.Combine(memoryRoot, "MEMORY.md"), "Claude", "Project", true, output, token, null,
            ExternalAgentSourceKind.Memory);
        var canonicalMemoryRoot = CanonicalDirectory(memoryRoot);
        if (canonicalMemoryRoot is null) return;
        foreach (var file in Directory.EnumerateFiles(canonicalMemoryRoot, "*.md", SearchOption.TopDirectoryOnly)
                     .Where(path => !string.Equals(Path.GetFileName(path), "MEMORY.md", StringComparison.OrdinalIgnoreCase))
                     .Take(32))
            AddDocument(file, "Claude", "Project", true, output, token, null, ExternalAgentSourceKind.Memory);
    }

    private void AddCodexMemories(string codexHome, List<ExternalAgentSource> output, bool allow, CancellationToken token)
    {
        var root = Path.Combine(codexHome, "memories");
        var canonicalRoot = CanonicalDirectory(root);
        if (canonicalRoot is null) return;
        if (!allow)
        {
            output.Add(new(ExternalAgentSourceKind.Memory, "Codex", "Global", SafeDisplayPath(root), "",
                "awaiting-consent", "Global external memory requires a human Ask decision.", RequiresUserConsent: true));
            return;
        }
        // Only documented summary/entry categories are materialized. Unknown files are listed
        // inactive so an arbitrary Markdown file is never silently treated as memory.
        var recognizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "MEMORY.md", "memory_summary.md", "raw_memories.md" };
        var recognized = new List<string>();
        foreach (var file in Directory.EnumerateFiles(canonicalRoot, "*.md", SearchOption.TopDirectoryOnly).Take(33))
        {
            token.ThrowIfCancellationRequested();
            if (recognizedNames.Contains(Path.GetFileName(file))) recognized.Add(file);
            else output.Add(new(ExternalAgentSourceKind.Memory, "Codex", "Global", SafeDisplayPath(file), "",
                "inactive", "This Codex memory file is outside recognized MEMORY.md, memory_summary.md, raw_memories.md, or rollout_summaries formats."));
        }
        foreach (var category in new[] { "rollout_summaries" })
        {
            var categoryRoot = CanonicalDirectory(Path.Combine(canonicalRoot, category));
            if (categoryRoot is null || !IsWithinOrEqual(canonicalRoot, categoryRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(categoryRoot, "*.md", SearchOption.TopDirectoryOnly).Take(33))
            {
                token.ThrowIfCancellationRequested();
                if (IsWithinOrEqual(categoryRoot, Path.GetFullPath(file)) && !HasReparsePoint(file)) recognized.Add(file);
            }
        }
        foreach (var file in recognized.Distinct(PathComparer).Take(32))
            AddDocument(file, "Codex", "Global", true, output, token, null, ExternalAgentSourceKind.Memory);
    }

    private void AddDocument(string path, string adapter, string scope, bool trusted,
        List<ExternalAgentSource> output, CancellationToken token, string? diagnostic,
        ExternalAgentSourceKind kind = ExternalAgentSourceKind.Instruction, string? authorizedRoot = null)
    {
        token.ThrowIfCancellationRequested();
        if (output.Count >= MaximumDocuments || !File.Exists(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            if (new RedactionPolicy().IsSecretPath(full) || HasReparsePoint(full)) return;
            if (!trusted)
            {
                output.Add(new(kind, adapter, scope, SafeDisplayPath(full), "", "inactive",
                    "Workspace trust is required before reading this source."));
                return;
            }
            if (output.Any(item => PathComparer.Equals(Path.GetFullPath(item.DisplayPath.StartsWith("~" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) ? Path.Combine(_home, item.DisplayPath[2..]) : item.DisplayPath), full))) return;
            var bytes = ReadTextBytes(full);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 32_768) throw new InvalidDataException();
            var redacted = new RedactionPolicy().Redact(text);
            if (redacted != text)
            {
                output.Add(new(kind, adapter, scope, SafeDisplayPath(full), "",
                    "inactive", "Source contains protected material; content was withheld."));
                return;
            }
            if (adapter == "Claude" && System.Text.RegularExpressions.Regex.IsMatch(text,
                    @"(?m)^\s*@\S+\s*$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                if (kind != ExternalAgentSourceKind.Instruction || authorizedRoot is null
                    || !TryExpandClaudeImports(full, authorizedRoot, text, token, out text))
                {
                    output.Add(new(kind, adapter, scope, SafeDisplayPath(full), "", "inactive",
                        "Claude @-imports are unsupported or escape the authorized root; source content was withheld."));
                    return;
                }
            }
            var effectiveDiagnostic = diagnostic;
            output.Add(new(kind, adapter, scope, SafeDisplayPath(full), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                "active", effectiveDiagnostic, text));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or DecoderFallbackException or InvalidDataException or ArgumentException)
        {
            output.Add(new(kind, adapter, scope, SafeDisplayPath(path), "", "error",
                "Source is unavailable or invalid; its content was not loaded."));
        }
    }

    private bool TryExpandClaudeImports(string rootFile, string authorizedRoot, string source,
        CancellationToken token, out string expanded)
    {
        var visited = new HashSet<string>(PathComparer);
        var totalCharacters = source.Length;
        var valid = true;
        var importPattern = new System.Text.RegularExpressions.Regex(
            @"(?m)^\s*@(?<path>\S+)\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var allowedImport = new System.Text.RegularExpressions.Regex(
            @"^(?:\./)?[A-Za-z0-9._/-]+\.md$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        string Expand(string file, string text, int depth)
        {
            token.ThrowIfCancellationRequested();
            var normalizedFile = Path.GetFullPath(file);
            if (depth >= 4 || !visited.Add(normalizedFile) || visited.Count > 32) { valid = false; return ""; }
            var matches = importPattern.Matches(text);
            if (matches.Cast<System.Text.RegularExpressions.Match>().Any(match =>
                    !allowedImport.IsMatch(match.Groups["path"].Value)))
            { valid = false; return ""; }
            return importPattern.Replace(text, match =>
            {
                var reference = match.Groups["path"].Value;
                if (reference.Split('/').Any(part => part is ".." or "~") || reference.Contains(':'))
                { valid = false; return ""; }
                var importedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(normalizedFile)!,
                    reference.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithinOrEqual(authorizedRoot, importedPath) || !File.Exists(importedPath)
                    || new RedactionPolicy().IsSecretPath(importedPath) || HasReparsePoint(importedPath))
                { valid = false; return ""; }
                try
                {
                    var bytes = ReadTextBytes(importedPath);
                    var imported = new UTF8Encoding(false, true).GetString(bytes);
                    if (new RedactionPolicy().Redact(imported) != imported) { valid = false; return ""; }
                    totalCharacters = checked(totalCharacters + imported.Length);
                    if (totalCharacters > MaximumTextBytes) { valid = false; return ""; }
                    return Expand(importedPath, imported, depth + 1);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidDataException or DecoderFallbackException or OverflowException)
                { valid = false; return ""; }
            });
        }
        expanded = Expand(rootFile, source, 0);
        return valid;
    }

    private void AddOpenCodeRulesDiagnostic(string path, string scope, bool trusted,
        List<ExternalAgentSource> output, CancellationToken token, string authorizedRoot)
    {
        token.ThrowIfCancellationRequested();
        if (output.Count >= MaximumDocuments || !File.Exists(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            if (new RedactionPolicy().IsSecretPath(full) || HasReparsePoint(full)) return;
            var display = SafeDisplayPath(full);
            if (!trusted)
            {
                output.Add(new(ExternalAgentSourceKind.Instruction, "OpenCode", scope, display, "", "inactive",
                    "Workspace trust is required before reading this OpenCode configuration."));
                return;
            }
            var bytes = ReadTextBytes(full);
            using var document = System.Text.Json.JsonDocument.Parse(bytes,
                new System.Text.Json.JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            if (!document.RootElement.TryGetProperty("instructions", out var instructions)) return;
            var patterns = instructions.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => new[] { instructions.GetString() ?? "" },
                System.Text.Json.JsonValueKind.Array => instructions.EnumerateArray()
                    .Where(value => value.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(value => value.GetString() ?? "").ToArray(),
                _ => Array.Empty<string>(),
            };
            if (patterns.Length == 0)
            {
                output.Add(new(ExternalAgentSourceKind.Instruction, "OpenCode", scope, display, "", "inactive",
                    "OpenCode instructions must be local string paths or globs; no remote source is fetched."));
                return;
            }
            foreach (var pattern in patterns.Take(32))
            {
                token.ThrowIfCancellationRequested();
                if (pattern.Length is 0 or > 240 || pattern.Contains("**", StringComparison.Ordinal)
                    || pattern.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(pattern)
                    || pattern.Replace('\\', '/').Split('/').Any(part => part is ".." or "~"))
                {
                    output.Add(new(ExternalAgentSourceKind.Instruction, "OpenCode", scope, display, "", "inactive",
                        "OpenCode remote, escaping, or recursive instruction references are not followed."));
                    continue;
                }
                var baseDirectory = Path.GetDirectoryName(full)!;
                var candidatePaths = ExpandLocalPattern(baseDirectory, pattern, authorizedRoot, token).Take(32).ToArray();
                if (candidatePaths.Length == 0)
                {
                    output.Add(new(ExternalAgentSourceKind.Instruction, "OpenCode", scope, display, "", "inactive",
                        "OpenCode instruction reference matched no supported local file."));
                    continue;
                }
                foreach (var candidate in candidatePaths)
                {
                    if (!IsWithinOrEqual(authorizedRoot, candidate))
                    {
                        output.Add(new(ExternalAgentSourceKind.Instruction, "OpenCode", scope, display, "", "inactive",
                            "OpenCode instruction reference escapes its authorized root."));
                        continue;
                    }
                    AddDocument(candidate, "OpenCode", scope, trusted, output, token, null);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            output.Add(new(ExternalAgentSourceKind.Instruction, "OpenCode", scope, SafeDisplayPath(path), "",
                "error", "OpenCode configuration is malformed or unavailable; imports remain inactive."));
        }
    }

    private static IEnumerable<string> ExpandLocalPattern(string baseDirectory, string pattern, string authorizedRoot, CancellationToken token)
    {
        var segments = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > 8) yield break;
        var current = new[] { Path.GetFullPath(baseDirectory) };
        foreach (var segment in segments)
        {
            token.ThrowIfCancellationRequested();
            if (segment == ".") continue;
            var next = new List<string>();
            foreach (var directory in current.Take(32))
            {
                if (!IsWithinOrEqual(authorizedRoot, directory) || HasReparsePoint(directory)) yield break;
                if (!Directory.Exists(directory)) continue;
                if (segment.Contains('*') || segment.Contains('?'))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(257)
                                 .Where(entry => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                                     segment, Path.GetFileName(entry), OperatingSystem.IsWindows())))
                        next.Add(entry);
                }
                else next.Add(Path.Combine(directory, segment));
            }
            current = next.Take(32).ToArray();
            if (current.Length == 0) yield break;
        }
        foreach (var path in current.Where(File.Exists).Take(32)) yield return Path.GetFullPath(path);
    }

    private static byte[] ReadTextBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8_192, FileOptions.SequentialScan);
        if (stream.Length is < 1 or > MaximumTextBytes) throw new InvalidDataException("Text source exceeds limits.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Text source changed while reading.");
        return bytes;
    }

    private string ConfigRoot(string variable, string fallback)
    {
        var value = _environment(variable);
        if (string.IsNullOrWhiteSpace(value)) return Path.GetFullPath(fallback);
        try
        {
            // The explicit agent-home seam is used by isolated hosts and test harnesses. Do not
            // let inherited machine-level provider paths escape that home and expose real sources.
            var configuredAgentHome = _environment("OMNICORE_AGENT_HOME");
            if (!string.IsNullOrWhiteSpace(configuredAgentHome))
            {
                var candidatePath = Path.GetFullPath(value);
                var isolatedRoot = Path.GetFullPath(configuredAgentHome);
                if (!IsWithinOrEqual(isolatedRoot, candidatePath)) return Path.GetFullPath(fallback);
            }
            var candidate = CanonicalDirectory(value);
            return candidate ?? Path.GetFullPath(fallback);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        { return Path.GetFullPath(fallback); }
    }

    private bool HasNonEmptyText(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (HasReparsePoint(path) || new RedactionPolicy().IsSecretPath(path)) return true;
            if (new FileInfo(path).Length == 0) return false;
            var bytes = ReadTextBytes(path);
            return !string.IsNullOrWhiteSpace(new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or DecoderFallbackException or InvalidDataException or ArgumentException)
        { return true; }
    }

    private static IEnumerable<string> Ancestors(string root, string active)
    {
        var items = new Stack<string>();
        var current = active;
        var count = 0;
        while (IsWithinOrEqual(root, current) && count++ < 32)
        {
            items.Push(current);
            if (PathComparer.Equals(root, current)) break;
            current = Path.GetDirectoryName(current)!;
        }
        return items;
    }

    private static string? CanonicalDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full) || HasReparsePoint(full)) return null;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private static bool HasReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        if (File.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        var parent = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        while (parent is not null)
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return true;
            var next = Path.GetDirectoryName(parent);
            if (next == parent) break;
            parent = next;
        }
        return false;
    }

    private string SafeDisplayPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (new RedactionPolicy().IsSecretPath(full)) return "[protected path]";
        return IsWithin(_home, full) ? "~" + Path.DirectorySeparatorChar + Path.GetRelativePath(_home, full) : full;
    }

    private static string SafeDiagnostic(string? value) => string.IsNullOrWhiteSpace(value)
        ? "" : new RedactionPolicy().Redact(value);

    public static string ProjectIdentity(string root)
    {
        var full = Path.GetFullPath(root);
        return GitProjectRoot(full) ?? full;
    }

    private static string ClaudeProjectKey(string identity)
    {
        var normalized = identity.Replace('\\', '/');
        return normalized.Replace(':', '-').Replace('/', '-');
    }

    private static string? GitProjectRoot(string root)
    {
        var current = Path.GetFullPath(root);
        while (true)
        {
            var marker = Path.Combine(current, ".git");
            if (Directory.Exists(marker) && !HasReparsePoint(marker)) return current;
            if (File.Exists(marker) && !HasReparsePoint(marker))
            {
                try
                {
                    var pointer = new UTF8Encoding(false, true).GetString(ReadTextBytes(marker)).Trim();
                    if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal)) return null;
                    var gitDir = Path.GetFullPath(Path.Combine(current, pointer[8..].Trim()));
                    if (!Directory.Exists(gitDir) || HasReparsePoint(gitDir)) return null;
                    var commonFile = Path.Combine(gitDir, "commondir");
                    if (!File.Exists(commonFile)) return null;
                    if (HasReparsePoint(commonFile)) return null;
                    var common = new UTF8Encoding(false, true).GetString(ReadTextBytes(commonFile)).Trim();
                    var commonDir = Path.GetFullPath(Path.Combine(gitDir, common));
                    if (!Directory.Exists(commonDir) || HasReparsePoint(commonDir)) return null;
                    var worktrees = Path.Combine(commonDir, "worktrees");
                    if (!IsWithinOrEqual(worktrees, gitDir)) return null;
                    var backlink = Path.Combine(gitDir, "gitdir");
                    if (!File.Exists(backlink) || HasReparsePoint(backlink)) return null;
                    var backlinkPath = new UTF8Encoding(false, true).GetString(ReadTextBytes(backlink)).Trim();
                    if (!PathComparer.Equals(Path.GetFullPath(backlinkPath), Path.GetFullPath(marker))) return null;
                    var primary = Path.GetDirectoryName(commonDir);
                    return primary is not null && Directory.Exists(primary) && !HasReparsePoint(primary) ? primary : null;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                { return null; }
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) return null;
            current = parent;
        }
    }
    private static bool IsWithinOrEqual(string root, string path) => PathComparer.Equals(root, path) || IsWithin(root, path);
    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
