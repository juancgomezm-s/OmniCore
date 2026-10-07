namespace OmniCore.Security;

using System.Text;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Intersects the existing policy with a resolved AgentProfile ceiling. Preferences never
/// authorize tools. Neither stored grants nor an approved interaction can lift this ceiling.
/// Path rules are workspace-relative globs or explicitly absolute physical-path rules;
/// process argv patterns match the complete ordered argv, not a permissive prefix.
/// </summary>
public sealed class AgentProfilePermissionPolicy : IPermissionPolicy, IGrantablePermissionPolicy
{
    private readonly IPermissionPolicy _inner;
    private readonly AgentProfile _profile;
    private readonly IPathBoundaryValidator _paths;
    private readonly string _workspaceRoot;

    public AgentProfile Profile => _profile;

    public AgentProfilePermissionPolicy(IPermissionPolicy inner, AgentProfile profile,
        IPathBoundaryValidator paths, string workspaceRoot)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = paths.ResolvePhysical(Path.GetFullPath(workspaceRoot))
            ?? throw new ArgumentException("Workspace physical identity is unavailable.", nameof(workspaceRoot));
    }

    public PermissionDecisionRecord Evaluate(ToolIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var inner = _inner.Evaluate(intent);
        var ceiling = Ceiling(intent);
        var final = Enum.IsDefined(inner.Final) ? Min(inner.Final, ceiling) : PermissionDecision.Deny;
        return new PermissionDecisionRecord(final,
            inner.Layers.Append(new LayerDecision("AgentProfile", ceiling,
                _profile.Id + "@" + _profile.Revision)).ToArray(), inner.AppliedGrant);
    }

    public AuthorizedToolIntent Authorize(ToolIntent intent)
    {
        var decision = Evaluate(intent);
        if (decision.Final != PermissionDecision.Allow)
            throw new PermissionDeniedException(intent.ToolId.ToString(), "AgentProfile ceiling or underlying policy: " + decision.Final);
        return new AuthorizedToolIntent(intent, decision, "omnicore.security:agent-profile");
    }

    public AuthorizedToolIntent AuthorizeApproved(ToolIntent intent, GrantId? approvedGrant)
    {
        // Only UserPolicy/mode Ask can be granted. An AgentProfile Ask is still a ceiling.
        if (Ceiling(intent) != PermissionDecision.Allow)
            throw new PermissionDeniedException(intent.ToolId.ToString(), "Approval cannot lift the AgentProfile ceiling.");
        var approved = _inner.AuthorizeApproved(intent, approvedGrant);
        return new AuthorizedToolIntent(intent,
            new PermissionDecisionRecord(approved.Decision.Final,
                approved.Decision.Layers.Append(new LayerDecision("AgentProfile", PermissionDecision.Allow,
                    _profile.Id + "@" + _profile.Revision)).ToArray(), approved.Decision.AppliedGrant),
            "omnicore.security:agent-profile-approved");
    }

    public bool CanCreatePersistentGrants => _inner is IGrantablePermissionPolicy { CanCreatePersistentGrants: true };

    public GrantId? RecordApprovedGrant(ToolIntent intent, GrantLifetime lifetime, CancellationToken cancellationToken)
    {
        if (Ceiling(intent) != PermissionDecision.Allow)
            throw new PermissionDeniedException(intent.ToolId.ToString(), "A grant cannot lift the AgentProfile ceiling.");
        if (_inner is not IGrantablePermissionPolicy grantable)
            throw new InvalidOperationException("Underlying policy cannot record grants.");
        return grantable.RecordApprovedGrant(intent, lifetime, cancellationToken);
    }

    private PermissionDecision Ceiling(ToolIntent intent)
    {
        var scope = _profile.PermissionCeiling;
        var claims = intent.Claims;
        if (claims is null || claims.Reads is null || claims.Writes is null || claims.Network is null || claims.Secrets is null)
            return PermissionDecision.Deny;
        if (intent.ToolId.ToString() == "shell.exec" && !scope.AllowShell) return PermissionDecision.Deny;
        if (claims.Reads.Any(path => !PathAllowed(path, scope.Reads))
            || claims.Writes.Any(path => !PathAllowed(path, scope.Writes))
            || claims.Secrets.Any(secret => !scope.Secrets.Contains(secret, StringComparer.Ordinal)))
            return PermissionDecision.Deny;
        var result = PermissionDecision.Allow;
        if (claims.Process is { } process)
        {
            if (process.Args is null || string.IsNullOrWhiteSpace(process.Executable)) return PermissionDecision.Deny;
            var rules = scope.Process.Where(rule =>
                Match(rule.ExecutablePattern.Replace('\\', '/'), process.Executable.Replace('\\', '/'), path: false)
                && rule.ArgvPatterns.Count == process.Args.Count
                && rule.ArgvPatterns.Zip(process.Args).All(pair => Match(pair.First, pair.Second, path: false))).ToArray();
            if (rules.Length == 0) return PermissionDecision.Deny;
            foreach (var rule in rules) result = Min(result, rule.Decision);
        }
        foreach (var network in claims.Network)
        {
            if (network is null || string.IsNullOrWhiteSpace(network.Host) || network.Port is < 1 or > 65535)
                return PermissionDecision.Deny;
            var address = network.Port is { } port ? network.Host + ":" + port : network.Host;
            var rules = scope.Network.Where(rule => rule.HostPattern == "build"
                ? claims.Process is { NetworkRequired: true, EffectClass: "Rerunnable" or "WorkspaceEffect" }
                : Match(rule.HostPattern, address, path: false)
                    || (!rule.HostPattern.Contains(':') && Match(rule.HostPattern, network.Host, path: false))).ToArray();
            if (rules.Length == 0) return PermissionDecision.Deny;
            foreach (var rule in rules) result = Min(result, rule.Decision);
        }
        if (claims.Process is { NetworkRequired: true } && claims.Network.Count == 0)
        {
            // The reserved build category applies only to explicitly permitted structured build effects.
            var rules = scope.Network.Where(rule => rule.HostPattern == "build"
                && claims.Process.EffectClass is "Rerunnable" or "WorkspaceEffect").ToArray();
            if (rules.Length == 0) return PermissionDecision.Deny;
            foreach (var rule in rules) result = Min(result, rule.Decision);
        }
        return result;
    }

    private bool PathAllowed(string path, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path, _workspaceRoot);
            var physical = _paths.ResolvePhysical(full);
            if (physical is null) return false;
            foreach (var rule in patterns)
            {
                if (Path.IsPathFullyQualified(rule))
                {
                    var physicalRule = _paths.ResolvePhysical(rule);
                    if (physicalRule is not null && Match(physicalRule.Replace('\\', '/'), physical.Replace('\\', '/'), path: true))
                        return true;
                }
                else if (_paths.IsWithin(physical, _workspaceRoot)
                    && Match(rule.Replace('\\', '/'), Path.GetRelativePath(_workspaceRoot, physical).Replace('\\', '/'), path: true))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static PermissionDecision Min(PermissionDecision first, PermissionDecision second) =>
        (PermissionDecision)Math.Min((int)first, (int)second);

    private static bool Match(string pattern, string value, bool path)
    {
        if (pattern is null || value is null) return false;
        var expression = new StringBuilder("\\A");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*')
            {
                if (path && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; expression.Append("(?:.*/)?"); }
                    else expression.Append(".*");
                }
                else expression.Append(path ? "[^/]*" : ".*");
            }
            else if (pattern[i] == '?') expression.Append(path ? "[^/]" : ".");
            else expression.Append(Regex.Escape(pattern[i].ToString()));
        }
        expression.Append("\\z");
        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (path && OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
        try { return Regex.IsMatch(value, expression.ToString(), options, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
