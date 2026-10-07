namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Reusable, versioned agent configuration (ADR-0037 §8, ADR-0046 §2).
/// Identity belongs to configuration, never to a Lane or AgentExecution. The ceiling
/// is not a grant and preferred tools are not an allowlist.
/// </summary>
public sealed class AgentProfile
{
    public ProfileId Id { get; }
    public string Name { get; }
    public long Revision { get; }
    public PermissionScope PermissionCeiling { get; }
    public IReadOnlyList<ToolId> PreferredTools { get; }

    public AgentProfile(ProfileId id, string name, long revision, PermissionScope permissionCeiling,
        IReadOnlyList<ToolId> preferredTools)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(permissionCeiling);
        ArgumentNullException.ThrowIfNull(preferredTools);
        if (id.Value == Guid.Empty) throw new ArgumentException("Profile identity must be explicit.", nameof(id));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (preferredTools.Any(tool => tool is null || string.IsNullOrWhiteSpace(tool.ToString()))
            || preferredTools.Distinct().Count() != preferredTools.Count)
            throw new ArgumentException("Preferred tools must have complete, unique canonical identities.", nameof(preferredTools));

        Id = id;
        Name = name;
        Revision = revision;
        PermissionCeiling = Freeze(permissionCeiling);
        PreferredTools = Array.AsReadOnly(preferredTools.ToArray());
    }

    private static PermissionScope Freeze(PermissionScope scope)
    {
        static IReadOnlyList<string> Strings(IReadOnlyList<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Permission rules cannot contain empty values.", nameof(scope));
            return Array.AsReadOnly(values.ToArray());
        }

        ArgumentNullException.ThrowIfNull(scope.Process);
        ArgumentNullException.ThrowIfNull(scope.Network);
        var processes = scope.Process.Select(rule =>
        {
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.ExecutablePattern);
            if (!Enum.IsDefined(rule.Decision)) throw new ArgumentException("Invalid process decision.", nameof(scope));
            return rule with { ArgvPatterns = Strings(rule.ArgvPatterns) };
        }).ToArray();
        var networks = scope.Network.Select(rule =>
        {
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.HostPattern);
            if (!Enum.IsDefined(rule.Decision)) throw new ArgumentException("Invalid network decision.", nameof(scope));
            return rule with { };
        }).ToArray();
        return PermissionScope.With(Strings(scope.Reads), Strings(scope.Writes),
            Array.AsReadOnly(processes), Array.AsReadOnly(networks), Strings(scope.Secrets), scope.AllowShell);
    }
}

/// <summary>Pure explicit selection; a missing configuration never fabricates a profile or authority.</summary>
public sealed class AgentProfileRegistry
{
    private readonly IReadOnlyDictionary<ProfileId, AgentProfile> _profiles;

    public AgentProfileRegistry(IEnumerable<AgentProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var values = new Dictionary<ProfileId, AgentProfile>();
        foreach (var profile in profiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (!values.TryAdd(profile.Id, profile))
                throw new ArgumentException("Each profile identity must resolve to exactly one revision.", nameof(profiles));
        }
        _profiles = values;
    }

    public AgentProfile? Find(ProfileId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _profiles.GetValueOrDefault(id);
    }
}
