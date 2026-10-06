namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Read-only evidence from other workspace journals in one User data directory.
/// This is a replay snapshot, not a billing invoice or concurrent spending reservation.</summary>
public sealed class UserWorkspaceSpendReader
{
    private readonly string _workspaces;
    private readonly string _currentWorkspace;
    private static readonly string[] Types = ["model_step.started", "model_step.completed", "model.completed",
        "meta_model.invocation_started", "meta_model.invocation_completed", "meta_model.invocation_failed",
        "interaction.requested", "interaction.resolved", "interaction.expired"];

    public UserWorkspaceSpendReader(string userDataDirectory, string currentWorkspaceDataDirectory)
    {
        _workspaces = Path.GetFullPath(Path.Combine(userDataDirectory, "workspaces"));
        _currentWorkspace = Path.GetFullPath(currentWorkspaceDataDirectory);
    }

    public sealed record WorkspaceEvidence(IReadOnlyList<DomainEvent> Events, IArtifactStore Artifacts);

    /// <summary>Materializes and closes each readonly connection; failures propagate, never become zero.</summary>
    public IReadOnlyList<WorkspaceEvidence> ReadOtherWorkspaces()
    {
        if (!Directory.Exists(_workspaces)) return [];
        RejectLink(_workspaces);
        var result = new List<WorkspaceEvidence>();
        foreach (var directory in Directory.GetDirectories(_workspaces).Order(StringComparer.Ordinal))
        {
            if (string.Equals(directory, _currentWorkspace, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            RejectLink(directory);
            var journal = Path.Combine(directory, "journal.db");
            if (!File.Exists(journal)) continue; // Registered data directory may not yet have any session.
            RejectLink(journal);
            var store = new SqliteEventStore(journal, readOnly: true);
            try
            {
                result.Add(new WorkspaceEvidence(store.ReadEventsSnapshot(Types.Select(EventType.Of)),
                    new FileArtifactStore(directory)));
            }
            finally { store.Close(); }
        }
        return result.ToArray();
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("User spend evidence cannot traverse a linked journal directory.");
    }
}
