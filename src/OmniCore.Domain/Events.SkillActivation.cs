namespace OmniCore.Domain;

/// <summary>Session selection lifecycle for a validated skill package identity (ADR-0026).</summary>
public sealed record SkillActivationChanged(string SkillId, ScopeLevel Scope, string ManifestSha256,
    bool Activated, string Origin) : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("skills.activation_changed");
    public int SchemaVersion() => 1;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SkillId)
            || !System.Text.RegularExpressions.Regex.IsMatch(SkillId, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || Scope is not (ScopeLevel.BuiltIn or ScopeLevel.User or ScopeLevel.Project
                or ScopeLevel.Workspace or ScopeLevel.Session)
            || ManifestSha256.Length != 64 || !ManifestSha256.All(Uri.IsHexDigit)
            || Origin is not ("User" or "Model" or "Automatic"))
            throw new InvalidDataException("The skill activation event identity is invalid.");
    }
}
