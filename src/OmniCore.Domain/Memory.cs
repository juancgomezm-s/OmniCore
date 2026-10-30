namespace OmniCore.Domain;

/// <summary>Identity and scope for a memory store partition (ADR-0028).</summary>
public sealed record MemoryScope(ScopeLevel Level, string Id)
{
    public static MemoryScope ForSession(SessionId session) => new(ScopeLevel.Session, session.ToString());
    public static MemoryScope ForProject(ProjectId project) => new(ScopeLevel.Project, project.ToString());
    public static MemoryScope ForWorkspace(WorkspaceId workspace) => new(ScopeLevel.Workspace, workspace.ToString());
    public static MemoryScope ForUser() => new(ScopeLevel.User, "user");

    public void Validate()
    {
        var valid = Level switch
        {
            ScopeLevel.Session => Guid.TryParseExact(Id, "D", out _),
            ScopeLevel.Project or ScopeLevel.Workspace => Id is { Length: 16 }
                && Id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            ScopeLevel.User => string.Equals(Id, "user", StringComparison.Ordinal),
            _ => false,
        };
        if (!valid) throw new ArgumentException("The memory scope identity is invalid or unsupported.");
    }
}

public enum MemoryKind { Fact, Preference, Decision, Convention, Procedure, Pitfall, Architecture, Environment, Summary }
public enum MemorySource { Observed, UserStated, Imported, Promoted }
public enum MemoryExpirationKind { Never, AfterUnused, At, WithScope }

/// <summary>Durable fact with source, time and evidence; content is redacted before storage by the Host.</summary>
public sealed record MemoryRecord(string Id, MemoryScope Scope, MemoryKind Kind, string Content,
    double Importance, double Confidence, MemorySource Source, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, MemoryExpirationKind Expiration, IReadOnlyList<string> Tags,
    IReadOnlyList<string> EvidenceRefs, string? Supersedes, DateTimeOffset? ExpiresAt = null,
    int? ExpireAfterUnusedDays = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentNullException.ThrowIfNull(Scope);
        Scope.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(Content);
        ArgumentNullException.ThrowIfNull(Tags);
        ArgumentNullException.ThrowIfNull(EvidenceRefs);
        if (Id.Length > 128 || Content.Length > 4096 || !double.IsFinite(Importance) || !double.IsFinite(Confidence)
            || Importance is < 0 or > 1 || Confidence is < 0 or > 1
            || Tags.Count > 32 || Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag.Any(char.IsControl))
            || EvidenceRefs.Count > 32 || EvidenceRefs.Any(reference => string.IsNullOrWhiteSpace(reference) || reference.Length > 512)
            || Supersedes is { Length: > 128 }
            || (Expiration == MemoryExpirationKind.At && ExpiresAt is null)
            || (Expiration == MemoryExpirationKind.AfterUnused && ExpireAfterUnusedDays is null or <= 0 or > 3650)
            || !Enum.IsDefined(Kind) || !Enum.IsDefined(Source) || !Enum.IsDefined(Expiration))
            throw new ArgumentException("The memory record is outside its supported bounds.");
    }
}
