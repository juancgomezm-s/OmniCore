namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

/// <summary>Structured, offline diagnostics and explicitly invoked workspace data maintenance.</summary>
public interface IDiagnosticsPreferencesFacade
{
    DiagnosticsPreferencesSnapshot Read();
    JournalDiagnosticSummary VerifyJournal(CancellationToken cancellationToken = default);
    ArtifactMaintenancePreview PreviewWorkspaceGarbageCollection(CancellationToken cancellationToken = default);
    ArtifactMaintenancePreview CollectWorkspaceGarbage(string expectedWorkspaceRoot,
        CancellationToken cancellationToken = default);
}

public sealed class DiagnosticsPreferencesHost(IPlatformPaths paths, string workspaceRoot) : IDiagnosticsPreferencesFacade
{
    private readonly IPlatformPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));

    public static DiagnosticsPreferencesHost Create(string workspaceRoot, string? dataDirectoryOverride = null,
        string? configDirectoryOverride = null) => new(
        new DefaultPlatformPaths(dataDirectoryOverride, configDirectoryOverride), workspaceRoot);

    public DiagnosticsPreferencesSnapshot Read()
    {
        var configuration = new List<DiagnosticConfigurationEntry>();
        var trusted = false;
        string? defaultModel = null;
        string? defaultModelScope = null;
        var modelCount = 0;
        var providerCount = 0;
        var workspaceConfig = false;
        try
        {
            foreach (var file in new[] { "providers.yaml", "models.yaml", "settings.yaml", "permissions.yaml" })
                ConfigurationWriteLease.EnsureNoLinks(Path.Combine(_paths.ConfigDirectory, file));
            var loaded = OmniHost.LoadUserConfiguration(_paths);
            modelCount = loaded.Registry.Models().Count;
            providerCount = loaded.Registry.Providers().Count;
            trusted = new WorkspaceTrustStore(_paths).IsTrusted(_workspaceRoot);
            var workspace = ScopedSettingsLoader.Load(_paths, _workspaceRoot, trusted,
                alias =>
                {
                    try { _ = loaded.Registry.Resolve(alias); return true; }
                    catch (OmniCore.Models.UnknownModelException) { return false; }
                    catch (OmniCore.Models.AmbiguousModelAliasException) { return false; }
                });
            defaultModel = workspace.Settings?.DefaultModel;
            defaultModelScope = defaultModel is null ? null : workspace.Scopes?.SourceOf("defaultModel")?.ToString();
            workspaceConfig = workspace.HasConfiguration;
            foreach (var diagnostic in workspace.Diagnostics)
                configuration.Add(FromConfig(diagnostic));
        }
        catch (ConfigValidationException exception)
        {
            configuration.AddRange(exception.Diagnostics.Select(FromConfig));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or ArgumentException)
        {
            configuration.Add(new("runtime", "diagnostics.unavailable", "", null, null,
                exception.GetType().Name));
        }

        try
        {
            ConfigurationWriteLease.EnsureNoLinks(Path.Combine(_paths.ConfigDirectory, "permissions.yaml"));
            _ = UserPermissionsLoader.Load(_paths);
        }
        catch (ConfigValidationException exception) { configuration.AddRange(exception.Diagnostics.Select(FromConfig)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { configuration.Add(new("permissions.yaml", "diagnostics.unavailable", "", null, null, exception.GetType().Name)); }

        var workspaceData = WorkspaceDataPath(_paths, _workspaceRoot);
        ConfigurationWriteLease.EnsureNoLinks(workspaceData);
        var journal = Path.Combine(workspaceData, "journal.db");
        return new(_workspaceRoot, trusted, workspaceConfig, defaultModel, defaultModelScope,
            modelCount, providerCount, configuration, File.Exists(journal), journal,
            ["session_purge", "audit_retention"]);
    }

    /// <summary>Read-only journal verification; does not create a missing journal or contact a provider.</summary>
    public JournalDiagnosticSummary VerifyJournal(CancellationToken cancellationToken = default)
    {
        var workspaceData = WorkspaceDataPath(_paths, _workspaceRoot);
        var journal = Path.Combine(workspaceData, "journal.db");
        ConfigurationWriteLease.EnsureNoLinks(workspaceData);
        ConfigurationWriteLease.EnsureNoLinks(journal);
        if (!File.Exists(journal)) return new(false, 0, 0, 0, 0, false, []);
        var artifacts = new FileArtifactStore(workspaceData);
        var report = new JournalVerifier(EventCodecs.Create(), artifacts).VerifyJournal(journal, cancellationToken);
        return new(report.JournalOpened, report.SessionCount, report.EventCount, report.ArtifactRefCount,
            report.TotalIssueCount, report.IssuesTruncated, report.Issues.Select(issue => new JournalDiagnosticIssue(
                issue.Code.ToString(), issue.Sequence, issue.EventType, issue.Field)).ToArray());
    }

    /// <summary>Runs the existing garbage collector in preview mode for this workspace only.</summary>
    public ArtifactMaintenancePreview PreviewWorkspaceGarbageCollection(CancellationToken cancellationToken = default)
    {
        var workspaceData = WorkspaceDataPath(_paths, _workspaceRoot);
        ConfigurationWriteLease.EnsureNoLinks(workspaceData);
        if (!Directory.Exists(workspaceData)) return new(_workspaceRoot, 0, 0, 0, 0, true);
        var journal = Path.Combine(workspaceData, "journal.db");
        ConfigurationWriteLease.EnsureNoLinks(journal);
        ConfigurationWriteLease.EnsureNoLinks(Path.Combine(workspaceData, "blobs"));
        var result = new ArtifactGc(workspaceData).Sweep(File.Exists(journal) ? journal : null,
            ArtifactGc.DefaultGrace, dryRun: true, DateTimeOffset.UtcNow, cancellationToken);
        return new(_workspaceRoot, result.Scanned, result.LiveReferenced, result.KeptByGrace,
            result.ReclaimedBytes, true);
    }

    /// <summary>Deletes only current Workspace CAS blobs proven unreferenced and older than the grace window.</summary>
    public ArtifactMaintenancePreview CollectWorkspaceGarbage(string expectedWorkspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetFullPath(expectedWorkspaceRoot), _workspaceRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("diagnostics.selection_changed");
        var workspaceData = WorkspaceDataPath(_paths, _workspaceRoot);
        ConfigurationWriteLease.EnsureNoLinks(workspaceData);
        if (!Directory.Exists(workspaceData)) return new(_workspaceRoot, 0, 0, 0, 0, false);
        var journal = Path.Combine(workspaceData, "journal.db");
        ConfigurationWriteLease.EnsureNoLinks(journal);
        ConfigurationWriteLease.EnsureNoLinks(Path.Combine(workspaceData, "blobs"));
        var result = new ArtifactGc(workspaceData).Sweep(File.Exists(journal) ? journal : null,
            ArtifactGc.DefaultGrace, dryRun: false, DateTimeOffset.UtcNow, cancellationToken);
        return new(_workspaceRoot, result.Scanned, result.LiveReferenced, result.KeptByGrace,
            result.ReclaimedBytes, false);
    }

    private static string WorkspaceDataPath(IPlatformPaths paths, string workspaceRoot)
    {
        var canonical = ProjectIdentity.CanonicalWorkspacePath(ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot));
        var workspace = WorkspaceId.Of(canonical).ToString();
        return Path.Combine(paths.DataDirectory, "workspaces", workspace);
    }

    private static DiagnosticConfigurationEntry FromConfig(ConfigDiagnostic diagnostic) =>
        new(diagnostic.File, diagnostic.Message.Key, diagnostic.KeyPath, diagnostic.Line, diagnostic.Column, null,
            new DiagnosticLocalizedText(diagnostic.Message.Key,
                new Dictionary<string, string>(diagnostic.Message.Args, StringComparer.Ordinal)));
}

public sealed record DiagnosticConfigurationEntry(string File, string Key, string KeyPath,
    int? Line, int? Column, string? Detail, DiagnosticLocalizedText? Message = null);
public sealed record DiagnosticLocalizedText(string Key, IReadOnlyDictionary<string, string> Args);

public sealed record DiagnosticsPreferencesSnapshot(string WorkspaceRoot, bool WorkspaceTrusted,
    bool WorkspaceConfigurationPresent, string? DefaultModel, string? DefaultModelScope,
    int ModelCount, int ProviderCount, IReadOnlyList<DiagnosticConfigurationEntry> Configuration,
    bool JournalPresent, string JournalPath, IReadOnlyList<string> MaintenanceWithoutUiFacade);

public sealed record ArtifactMaintenancePreview(string WorkspaceRoot, long Scanned, long LiveReferenced,
    long KeptByGrace, long ReclaimableBytes, bool DryRun);
public sealed record JournalDiagnosticIssue(string Code, long? Sequence, string? EventType, string? Field);
public sealed record JournalDiagnosticSummary(bool JournalOpened, long SessionCount, long EventCount,
    long ArtifactRefCount, long TotalIssueCount, bool IssuesTruncated, IReadOnlyList<JournalDiagnosticIssue> Issues)
{
    public bool Ok => TotalIssueCount == 0 && JournalOpened;
}
