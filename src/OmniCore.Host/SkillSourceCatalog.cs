namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Maps each supported skill scope to an application-owned package root and trust boundary.</summary>
public sealed class SkillSourceCatalog
{
    public IReadOnlyList<DiscoveredSkill> DiscoverWithExtensions(string? workspaceRoot, string userDataDirectory,
        string? workspaceDataDirectory, SessionId? session, bool workspaceTrusted,
        IEnumerable<(DiscoveredExtension Extension, IReadOnlySet<string> Capabilities)> packages,
        SkillActivationContext? activationContext = null)
    {
        var discovered = Discover(workspaceRoot, userDataDirectory, workspaceDataDirectory, session,
            workspaceTrusted, activationContext);
        var combined = AddConsentedExtensionSkills(discovered, packages);
        return activationContext is null ? combined : SkillManifestLoader.ResolvePrecedence(
            combined.Select(skill => activationContext.Evaluate(new SkillManifestLoader(), skill)));
    }

    public IReadOnlyList<DiscoveredSkill> AddConsentedExtensionSkills(IEnumerable<DiscoveredSkill> skills,
        IEnumerable<(DiscoveredExtension Extension, IReadOnlySet<string> Capabilities)> packages)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(packages);
        var loader = new SkillManifestLoader();
        var result = skills.ToList();
        foreach (var (extension, capabilities) in packages)
        {
            if (!capabilities.Contains("skill.register") || extension.Manifest is not { } manifest) continue;
            var packageRoot = Path.GetFullPath(extension.Directory);
            foreach (var declaredPath in manifest.Provides?.Skills ?? [])
            {
                if (!SafePackageRelativePath(declaredPath)) continue;
                var skillDirectory = Path.GetFullPath(Path.Combine(packageRoot,
                    declaredPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
                if (!Within(packageRoot, skillDirectory) || !Directory.Exists(skillDirectory)
                    || HasReparsePoint(packageRoot, skillDirectory)) continue;
                var scope = extension.Trust == TrustLevel.Trusted ? ScopeLevel.User : ScopeLevel.Project;
                var discovered = loader.DiscoverSingle(skillDirectory, scope, extension.Trust, workspaceTrusted: true);
                if (discovered is null) continue;
                result.Add(discovered with { ParentPackageId = manifest.Id,
                    ParentPackageSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(extension.ConsentHash + ":" + extension.PackageSha256))) });
            }
        }
        return SkillManifestLoader.ResolvePrecedence(result);
    }

    private static bool SafePackageRelativePath(string value) => value.Length is > 0 and <= 240
        && !Path.IsPathRooted(value) && !value.Any(char.IsControl)
        && !value.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..");

    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool HasReparsePoint(string root, string path)
    {
        var current = Path.GetFullPath(path);
        while (Within(root, current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            current = Path.GetDirectoryName(current)!;
        }
        return false;
    }

    public IReadOnlyList<DiscoveredSkill> Discover(string? workspaceRoot, string userDataDirectory,
        string? workspaceDataDirectory, SessionId? session, bool workspaceTrusted,
        SkillActivationContext? activationContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        var loader = new SkillManifestLoader();
        var roots = new List<(string Path, ScopeLevel Scope, TrustLevel Trust, bool Trusted)>();
        roots.Add((Path.Combine(AppContext.BaseDirectory, "skills"), ScopeLevel.BuiltIn, TrustLevel.Core, false));
        roots.Add((Path.Combine(Path.GetFullPath(userDataDirectory), "skills"), ScopeLevel.User, TrustLevel.Trusted, false));
        if (workspaceRoot is not null)
        {
            var fullWorkspace = Path.GetFullPath(workspaceRoot);
            roots.Add((Path.Combine(fullWorkspace, ".omnicore", "skills"), ScopeLevel.Project,
                TrustLevel.Project, workspaceTrusted));
        }
        if (workspaceDataDirectory is not null)
        {
            var dataRoot = Path.GetFullPath(workspaceDataDirectory);
            // Workspace scope is in user-owned runtime data, not the potentially untrusted
            // project .omnicore tree. Its trust decision must not be inherited from that repo.
            roots.Add((Path.Combine(dataRoot, "skills"), ScopeLevel.Workspace, TrustLevel.Trusted, true));
            if (session is { } sessionId)
                roots.Add((Path.Combine(dataRoot, "sessions", sessionId.ToString(), "skills"),
                    ScopeLevel.Session, TrustLevel.Trusted, false));
        }
        var all = roots.SelectMany(root => loader.Discover(root.Path, root.Scope, root.Trust,
            workspaceTrusted: root.Trusted));
        if (activationContext is not null)
            all = all.Select(skill => activationContext.Evaluate(loader, skill));
        return SkillManifestLoader.ResolvePrecedence(all);
    }
}

/// <summary>Bounded, local activation signals taken from the current human objective and WorkingState.</summary>
public sealed record SkillActivationContext(IReadOnlyList<string> RelativePaths, string? Language,
    string? Framework, string? TaskType)
{
    private static readonly System.Text.RegularExpressions.Regex PathPattern = new(
        @"(?<![A-Za-z0-9_.-])(?<path>(?:[A-Za-z0-9_.-]+[\\/])*[A-Za-z0-9_.-]+\.[A-Za-z0-9]{1,12})(?![A-Za-z0-9_])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static SkillActivationContext FromInput(string? objective, string? workingState, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var text = ((objective ?? "") + "\n" + (workingState ?? "")).Normalize();
        var root = Path.GetFullPath(workspaceRoot);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in PathPattern.Matches(text).Take(64))
        {
            var token = match.Groups["path"].Value.Replace('\\', '/');
            if (Path.IsPathRooted(token) || token.Split('/').Any(part => part is "" or "." or "..")) continue;
            try
            {
                var full = Path.GetFullPath(Path.Combine(root, token.Replace('/', Path.DirectorySeparatorChar)));
                if (full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    paths.Add(token);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        var language = paths.Select(path => LanguageFor(Path.GetExtension(path)))
            .FirstOrDefault(value => value is not null);
        string? framework = null;
        foreach (var known in new[] { "aspnetcore", "asp.net", "blazor", "react", "angular", "node", "dotnet" })
            if (text.Contains(known, StringComparison.OrdinalIgnoreCase)) { framework = known; break; }
        string? taskType = text.Contains("build failure", StringComparison.OrdinalIgnoreCase)
            || text.Contains("build failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("compilation failed", StringComparison.OrdinalIgnoreCase)
            ? "build-failure"
            : text.Contains("test failure", StringComparison.OrdinalIgnoreCase)
                || text.Contains("failing test", StringComparison.OrdinalIgnoreCase) ? "test-failure"
                : text.Contains("refactor", StringComparison.OrdinalIgnoreCase) ? "refactor" : null;
        return new(paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), language, framework, taskType);
    }

    internal DiscoveredSkill Evaluate(SkillManifestLoader loader, DiscoveredSkill skill)
    {
        ArgumentNullException.ThrowIfNull(loader);
        var signals = RelativePaths.Count == 0 ? new string?[] { null } : RelativePaths.Cast<string?>().ToArray();
        foreach (var path in signals)
        {
            var evaluated = loader.EvaluateEligibility(skill, path, Language, Framework, TaskType);
            if (evaluated.State == SkillLifecycleState.Eligible) return evaluated;
        }
        return skill;
    }

    private static string? LanguageFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" or ".csproj" or ".sln" => "csharp",
        ".p" or ".w" => "abl",
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript",
        ".py" => "python",
        ".go" => "go",
        ".rs" => "rust",
        ".java" => "java",
        ".kt" => "kotlin",
        _ => null,
    };
}
