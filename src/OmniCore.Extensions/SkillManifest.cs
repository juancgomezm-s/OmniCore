namespace OmniCore.Extensions;

using OmniCore.Domain;
using YamlDotNet.Serialization;

/// <summary>Project skill metadata. Discovery reads only this file; instruction bodies load on explicit activation.</summary>
public sealed record SkillManifest
{
    public string? Id { get; set; }
    public string? Version { get; set; }
    public string? DisplayName { get; set; }
    public bool Sealed { get; set; }
    public int Priority { get; set; }
    public bool ModelInvocable { get; set; }
    public SkillActivationManifest? Activation { get; set; }
    public SkillExecutionManifest? Execution { get; set; }
    public List<string>? Tools { get; set; }
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
    TrustLevel Trust, string ManifestSha256, SkillLifecycleState State, string? Diagnostic);

public sealed record LoadedSkill(DiscoveredSkill Skill, string Instructions, string ContentSha256);

[YamlStaticContext]
[YamlSerializable(typeof(SkillManifest))]
[YamlSerializable(typeof(SkillActivationManifest))]
[YamlSerializable(typeof(SkillExecutionManifest))]
public sealed partial class SkillYamlStaticContext : StaticContext
{
}
