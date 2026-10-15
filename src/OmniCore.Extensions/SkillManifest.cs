namespace OmniCore.Extensions;

using OmniCore.Domain;

/// <summary>Project skill metadata. Discovery reads only this file; instruction bodies load on explicit activation.</summary>
public sealed record SkillManifest
{
    public string? Id { get; set; }
    public string? Version { get; set; }
    public string? DisplayName { get; set; }
    public bool Sealed { get; set; }
    public int Priority { get; set; }
    public bool ModelInvocable { get; set; }
    /// <summary>Maximum context tokens this skill may contribute in a single materialization.</summary>
    public int BudgetTokens { get; set; } = 2048;
    public SkillActivationManifest? Activation { get; set; }
    public SkillExecutionManifest? Execution { get; set; }
    public SkillPermissionManifest? Permissions { get; set; }
    public List<string>? Tools { get; set; }
    public SkillContributionsManifest? Contributions { get; set; }
}

/// <summary>Restrictive claims ceiling for tool IDs declared by this skill; never grants permission.</summary>
public sealed class SkillPermissionManifest
{
    public SkillPathPermissionManifest? Filesystem { get; set; }
    public List<string>? Processes { get; set; }
    public List<string>? Network { get; set; }
}

public sealed class SkillPathPermissionManifest
{
    public List<string>? Read { get; set; }
    public List<string>? Write { get; set; }
}

public sealed class SkillActivationManifest
{
    public List<string>? Paths { get; set; }
    public List<string>? Extensions { get; set; }
    public List<string>? Languages { get; set; }
    public List<string>? Frameworks { get; set; }
    public List<string>? TaskTypes { get; set; }
    public bool Explicit { get; set; }
}

public sealed class SkillExecutionManifest
{
    public string? Context { get; set; }
    public string? PreferredModelAlias { get; set; }
}

/// <summary>Static package contributions; paths are read-only data, never executable code.</summary>
public sealed class SkillContributionsManifest
{
    public List<string>? Tools { get; set; }
    public List<string>? Workflows { get; set; }
    public List<string>? Validators { get; set; }
    public List<string>? Contributors { get; set; }
    public List<string>? Knowledge { get; set; }
}

public enum SkillResourceKind { Workflow, Validator, Contributor, Knowledge }

public sealed record LoadedSkillResource(SkillResourceKind Kind, string RelativePath, string Content, string Sha256);

public enum SkillLifecycleState
{
    Discovered,
    Eligible,
    Activated,
    Loaded,
    Released,
    Conflict,
}

public sealed record DiscoveredSkill(string Directory, SkillManifest Manifest, ScopeLevel Scope,
    TrustLevel Trust, string ManifestSha256, SkillLifecycleState State, string? Diagnostic,
    string? ParentPackageSha256 = null, string? ParentPackageId = null,
    SkillInvocationRestrictions? InvocationRestrictions = null, string? Description = null,
    string? PortableContentSha256 = null);

/// <summary>Additional portable-format restrictions; a source may narrow, never widen, OmniCore policy.</summary>
public sealed record SkillInvocationRestrictions(bool AllowAutomatic, bool AllowModelInvocation,
    bool AllowUserInvocation, string SourceFormat);

public sealed record LoadedSkill(DiscoveredSkill Skill, string Instructions, string ContentSha256,
    IReadOnlyList<LoadedSkillResource>? Resources = null, string? PackageContentSha256 = null);
