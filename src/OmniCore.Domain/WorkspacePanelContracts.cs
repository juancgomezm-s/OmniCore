namespace OmniCore.Domain;

// Transport-neutral, bounded workspace read models. Paths are always relative to the bound root.
public sealed record WorkspaceFile(string Path, bool Directory, string Status, int Added, int Deleted);
public sealed record WorkspaceGitFile(string Path, string Status, int Added, int Deleted);
public sealed record WorkspaceBranch(string Name, string Upstream, string Tracking);
public sealed record WorkspaceWorktree(string Path, string Head, string? Branch);
public sealed record WorkspaceGitView(bool IsRepository, string Branch, string Upstream, int Ahead, int Behind,
    IReadOnlyList<WorkspaceGitFile> Files, IReadOnlyList<WorkspaceBranch> Branches, IReadOnlyList<WorkspaceWorktree> Worktrees)
{
    public string? LastCommit { get; init; }
    public IReadOnlyList<string> CommittedFiles { get; init; } = [];
}
public sealed record WorkspaceFileView(string Path, string Text, bool Binary);
public sealed record WorkspaceSnapshot(string Commit, string Tree, string Reference, string SourceHead);
public sealed record ImportedConversationMessage(string Role, string Text, string? CallId = null,
    string? ToolName = null, string? Arguments = null, bool IsError = false);
public sealed record ConversationHistoryImported(string SourceWorkspace, string SourceSessionId, long ThroughSequence,
    string CheckpointId, IReadOnlyList<ImportedConversationMessage> Messages, string? Instruction) : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("conversation.history_imported");
    public int SchemaVersion() => 1;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceWorkspace) || SourceWorkspace.Length > 4096
            || !Guid.TryParseExact(SourceSessionId, "D", out _) || ThroughSequence < 1
            || string.IsNullOrEmpty(CheckpointId) || CheckpointId.Length > 160
            || !CheckpointId.StartsWith(SourceSessionId + "-", StringComparison.Ordinal)
            || CheckpointId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || Instruction?.Length > 16000 || Messages is null || Messages.Count > 10000)
            throw new InvalidDataException("Invalid imported conversation identity.");
        var calls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in Messages)
        {
            if (message.Role is not ("user" or "assistant" or "tool") || message.Text is null || message.Text.Length > 393216)
                throw new InvalidDataException("Invalid imported conversation message.");
            if (message.Role == "tool")
            {
                if (!Guid.TryParseExact(message.CallId, "D", out _) || !calls.Add(message.CallId!)
                    || string.IsNullOrWhiteSpace(message.ToolName) || message.ToolName.Length > 160
                    || message.Arguments is null || message.Arguments.Length > 393216)
                    throw new InvalidDataException("Invalid imported tool pair.");
            }
            else if (message.CallId is not null || message.ToolName is not null || message.Arguments is not null || message.IsError)
                throw new InvalidDataException("Non-tool imported message contains a tool envelope.");
        }
    }
}
