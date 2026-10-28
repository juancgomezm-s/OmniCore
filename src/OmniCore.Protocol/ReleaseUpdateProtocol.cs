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
    bool DevelopmentBuild, ReleaseUpdateEnvironment Environment, string? TargetVersion = null);

/// <summary>Published asset selected by a check. Its id is required for the verified download.</summary>
public sealed record ReleaseUpdateAsset(long Id, string Name, long Size, string Sha256, string Rid);

/// <summary>Manual metadata result. Metadata/digests alone never verify archive publisher identity.</summary>
public sealed record ReleaseUpdateCheckResult(ReleaseUpdateChannel Channel, ReleaseUpdateStatus Status,
    ReleaseCompatibility Compatibility, string InstalledVersion, string? AvailableVersion = null,
    string? Tag = null, string? CommitSha = null, string? ReleaseUrl = null, string? Notes = null,
    string? Rid = null, string? AssetName = null, long? AssetSize = null, string? AssetSha256 = null,
    string? CompatibilityDetail = null, string? DiagnosticCode = null, DateTimeOffset? RetryAfterUtc = null,
    bool CatalogComplete = true, int ReleasesExamined = 0,
    bool MetadataOnly = true, bool ArchiveVerificationPending = true, bool PublisherVerificationPending = true,
    ReleaseUpdateRequirements? Requirements = null, ReleaseUpdateAsset? Asset = null);

public sealed record ReleaseUpdateRequirements(string? DotNetMinimum, string? DotNetMaximum,
    string? NodeMinimum, string? NodeMaximum, string? ProtocolMinimum, string? ProtocolMaximum,
    string? ConfigurationMinimum, string? ConfigurationMaximum);

public enum ReleaseUpdateDownloadStatus
{
    Downloaded,
    AssetUnavailable,
    RateLimited,
    Offline,
    NetworkError,
    RedirectRejected,
    PayloadTooLarge,
    SizeMismatch,
    DigestMismatch,
    PackageIdentityMismatch,
    StagingFailed,
    Cancelled,
}

public sealed record ReleaseUpdateDownloadResult(ReleaseUpdateDownloadStatus Status,
    string? DiagnosticCode = null, string? Path = null, string? Sha256 = null, long? Size = null,
    DateTimeOffset? RetryAfterUtc = null);

public enum ReleaseInstallKind { Unknown, NpmGlobal, DevelopmentCheckout, ReleasePackage }

public sealed record ReleaseInstallation(ReleaseInstallKind Kind, string? PackageDirectory = null,
    string? NpmPath = null, string? NodePath = null, string? InstalledVersion = null, string? Detail = null,
    string? NpmPrefix = null);

public enum ReleaseUpdateOperation { Apply, Rollback }

public enum ReleaseUpdateOperationStatus { Pending, Running, Succeeded, Failed, Unknown }

public sealed record ReleaseUpdateState(int SchemaVersion, ReleaseUpdateOperation Operation,
    ReleaseUpdateOperationStatus Status, string? FromVersion, string? ToVersion, string? AssetPath,
    string? BackupPath, int? ParentPid, int? HelperPid, DateTimeOffset UpdatedAtUtc, string? Detail,
    string? OperationId = null, string? ExpectedSha256 = null, long? ExpectedSize = null, string? NpmPrefix = null);

public sealed record ReleaseUpdateApplyResult(bool Prepared, string? DiagnosticCode = null,
    ReleaseUpdateState? State = null);

public sealed record ReleaseUpdateRollbackResult(bool Prepared, string? DiagnosticCode = null,
    ReleaseUpdateState? State = null);
