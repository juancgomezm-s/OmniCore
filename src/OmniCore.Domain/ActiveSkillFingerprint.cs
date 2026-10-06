namespace OmniCore.Domain;

/// <summary>Identity of an already resolved active skill (ADR-0017). Does not load or activate skills.</summary>
public sealed record ActiveSkillFingerprint(string Id, string Version, ContentHash ContentHash);
