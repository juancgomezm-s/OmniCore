namespace OmniCore.Protocol;

/// <summary>Manual release-check channel. Preview is never selected implicitly.</summary>
public enum ReleaseUpdateChannel { Stable, Preview }

public enum ReleaseUpdateStatus
{
    NoPublishedRelease,
    RepositoryUnavailable,
    RateLimited,
    Offline,
    NetworkError,
    CatalogIncomplete,
    ManifestMissing,
    ManifestInvalid,
    NoCompatibleAsset,
    CompatibilityUnknown,
    UpToDate,
    DevelopmentBuild,
    UpdateAvailable,
}

public enum ReleaseCompatibility { Unknown, Compatible, Incompatible }

/// <summary>Runtime facts the caller actually knows; null means unknown and is never inferred.</summary>
public sealed record ReleaseUpdateEnvironment(string Rid, string DotNetVersion,
    string? NodeVersion = null, string? ProtocolVersion = null, string? ConfigurationVersion = null);

public sealed record ReleaseUpdateCheckRequest(ReleaseUpdateChannel Channel, string InstalledVersion,
    bool DevelopmentBuild, ReleaseUpdateEnvironment Environment);

/// <summary>Manual metadata result. Metadata/digests alone never verify archive publisher identity.</summary>
public sealed record ReleaseUpdateCheckResult(ReleaseUpdateChannel Channel, ReleaseUpdateStatus Status,
    ReleaseCompatibility Compatibility, string InstalledVersion, string? AvailableVersion = null,
    string? Tag = null, string? CommitSha = null, string? ReleaseUrl = null, string? Notes = null,
    string? Rid = null, string? AssetName = null, long? AssetSize = null, string? AssetSha256 = null,
    string? CompatibilityDetail = null, string? DiagnosticCode = null, DateTimeOffset? RetryAfterUtc = null,
    bool CatalogComplete = true, int ReleasesExamined = 0,
    bool MetadataOnly = true, bool ArchiveVerificationPending = true, bool PublisherVerificationPending = true,
    ReleaseUpdateRequirements? Requirements = null);

public sealed record ReleaseUpdateRequirements(string? DotNetMinimum, string? DotNetMaximum,
    string? NodeMinimum, string? NodeMaximum, string? ProtocolMinimum, string? ProtocolMaximum,
    string? ConfigurationMinimum, string? ConfigurationMaximum);
