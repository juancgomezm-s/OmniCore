namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Applies each active skill's manifest ceiling to only the tool IDs that skill declares.
/// The boundary is restrictive; normal model, permission, extension and process policies still run.</summary>
internal sealed class SkillPermissionBoundary(Func<IReadOnlyList<LoadedSkill>> activeSkills,
    string workspaceRoot = ".") : IToolIntentBoundary
{
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public ModelCapabilityDecision Evaluate(ToolIntent intent)
    {
        var toolId = intent.ToolId.ToString();
        var requiresProjectProcessConsent = false;
        foreach (var skill in activeSkills())
        {
            if (!(skill.Skill.Manifest.Tools ?? []).Contains(toolId, StringComparer.Ordinal)) continue;
            var ceiling = skill.Skill.Manifest.Permissions;
            requiresProjectProcessConsent |= intent.Claims.Process is not null
                && skill.Skill.Trust is TrustLevel.Project or TrustLevel.ThirdParty;
            // Omitted permissions means no additional declaration. An explicit empty mapping
            // is a closed ceiling for the declared tool.
            if (ceiling is null) continue;
            if (!Allows(intent, ceiling, _workspaceRoot))
                return ModelCapabilityDecision.Reject(
                    "skill.permissions.ceiling_exceeded", null);
        }
        return requiresProjectProcessConsent
            ? ModelCapabilityDecision.Ask("skill.permissions.process_consent_required", null)
            : ModelCapabilityDecision.Allow();
    }

    private static bool Allows(ToolIntent intent, SkillPermissionManifest ceiling, string workspaceRoot)
    {
        var claims = intent.Claims;
        if (claims.Secrets.Count != 0) return false;
        var recursiveRead = intent.ToolId.ToString() is "filesystem.list" or "filesystem.search";
        if (claims.Reads.Any(path => NormalizeWorkspacePath(path, workspaceRoot) is not { } normalized
                || !IsPathClaimAllowed(ceiling.Filesystem?.Read, normalized, recursiveRead))
            || claims.Writes.Any(path => NormalizeWorkspacePath(path, workspaceRoot) is not { } normalized
                || !IsPathClaimAllowed(ceiling.Filesystem?.Write, normalized, recursive: false))) return false;
        if (claims.Network.Any(network => !MatchesAny(ceiling.Network,
                network.Host + (network.Port is { } port ? ":" + port : "")))) return false;
        if (claims.Process is { } process && (HasWildcard(process.Executable)
                || !MatchesAny(ceiling.Processes, process.Executable)
            && !MatchesAny(ceiling.Processes, Path.GetFileName(process.Executable)))) return false;
        if (claims.Process is { NetworkRequired: true } && claims.Network.Count == 0) return false;
        return true;
    }

    private static string? NormalizeWorkspacePath(string path, string workspaceRoot)
    {
        if (!Path.IsPathRooted(path))
            return path.Replace('\\', '/').Split('/').Any(part => part == "..") ? null : path;
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(workspaceRoot, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        return relative;
    }

    private static bool HasWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

    private static bool IsPathClaimAllowed(IReadOnlyList<string>? patterns, string claim, bool recursive)
    {
        claim = NormalizeSeparators(claim);
        if (!HasWildcard(claim)) return recursive
            ? IsCoveredTree(patterns, claim)
            : MatchesAny(patterns, claim);

        // Do not try to solve arbitrary glob inclusion. Exact identity and a literal recursive
        // ceiling are safe; narrower single-level globs cannot prove containment of descendants.
        return patterns?.Any(pattern =>
        {
            pattern = NormalizeSeparators(pattern);
            if (string.Equals(pattern, claim, StringComparison.OrdinalIgnoreCase))
                return !recursive || IsRecursiveClaim(claim);
            if (pattern is "**" or "**/**") return true;
            if (!pattern.EndsWith("/**", StringComparison.Ordinal)) return false;
            var allowedRoot = pattern[..^3].TrimEnd('/');
            if (HasWildcard(allowedRoot)) return false;
            var claimRoot = LiteralPrefixBeforeWildcard(claim);
            if (claimRoot is null) return false;
            if (recursive && !IsRecursiveClaim(claim)) return false;
            return PathEqualsOrDescendant(claimRoot, allowedRoot);
        }) == true;
    }

    private static bool IsRecursiveClaim(string claim) => claim.EndsWith("/**", StringComparison.Ordinal)
        || claim is "**" or "**/**";

    private static string? LiteralPrefixBeforeWildcard(string claim)
    {
        var wildcard = claim.IndexOfAny(['*', '?']);
        var prefix = wildcard < 0 ? claim : claim[..wildcard];
        var separator = prefix.LastIndexOf('/');
        var root = separator < 0 ? "." : prefix[..separator];
        if (root.Length == 0) root = ".";
        return HasWildcard(root) ? null : root;
    }

    private static bool MatchesAny(IReadOnlyList<string>? patterns, string value) => patterns?.Any(pattern =>
        !HasWildcard(value) && GlobMatches(pattern, value)) == true;

    // Listing and search tools consume entries below the claimed directory, not just the
    // directory name. Require a declared recursive subtree ceiling; a file glob such as
    // src/* must never authorize listing/searching all of src.
    private static bool IsCoveredTree(IReadOnlyList<string>? patterns, string directory)
    {
        directory = NormalizeSeparators(directory).TrimEnd('/');
        if (directory.Length == 0) directory = ".";
        return patterns?.Any(pattern =>
        {
            var normalized = NormalizeSeparators(pattern).TrimEnd('/');
            if (normalized is "**" or "**/**") return true;
            const string suffix = "/**";
            if (!normalized.EndsWith(suffix, StringComparison.Ordinal)) return false;
            var root = normalized[..^suffix.Length].TrimEnd('/');
            if (root.Length == 0) root = ".";
            if (HasWildcard(root)) return false;
            return PathEqualsOrDescendant(directory, root);
        }) == true;
    }

    private static bool PathEqualsOrDescendant(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || (root == "." ? path != ".." && !path.StartsWith("../", StringComparison.Ordinal)
            : path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));

    private static bool GlobMatches(string pattern, string value)
    {
        pattern = NormalizeSeparators(pattern);
        value = NormalizeSeparators(value);
        var patternParts = pattern.Split('/');
        var valueParts = value.Split('/');
        var matches = new bool[patternParts.Length + 1, valueParts.Length + 1];
        matches[0, 0] = true;
        for (var patternIndex = 0; patternIndex < patternParts.Length; patternIndex++)
        {
            var segment = patternParts[patternIndex];
            if (segment == "**")
            {
                for (var valueIndex = 0; valueIndex <= valueParts.Length; valueIndex++)
                    matches[patternIndex + 1, valueIndex] = matches[patternIndex, valueIndex]
                        || valueIndex > 0 && matches[patternIndex + 1, valueIndex - 1];
                continue;
            }
            for (var valueIndex = 0; valueIndex < valueParts.Length; valueIndex++)
                if (matches[patternIndex, valueIndex]
                    && SegmentMatches(segment, valueParts[valueIndex]))
                    matches[patternIndex + 1, valueIndex + 1] = true;
        }
        return matches[patternParts.Length, valueParts.Length];
    }

    private static bool SegmentMatches(string pattern, string value)
    {
        var matches = new bool[pattern.Length + 1, value.Length + 1];
        matches[0, 0] = true;
        for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
        {
            var token = pattern[patternIndex];
            if (token == '*')
            {
                for (var valueIndex = 0; valueIndex <= value.Length; valueIndex++)
                    matches[patternIndex + 1, valueIndex] = matches[patternIndex, valueIndex]
                        || valueIndex > 0 && matches[patternIndex + 1, valueIndex - 1];
            }
            else
            {
                for (var valueIndex = 0; valueIndex < value.Length; valueIndex++)
                    if (matches[patternIndex, valueIndex]
                        && (token == '?' || char.ToUpperInvariant(token) == char.ToUpperInvariant(value[valueIndex])))
                        matches[patternIndex + 1, valueIndex + 1] = true;
            }
        }
        return matches[pattern.Length, value.Length];
    }

    private static string NormalizeSeparators(string value) => value.Replace('\\', '/');
}
