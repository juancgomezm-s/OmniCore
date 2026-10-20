using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Domain;

namespace OmniCore.Host;

public sealed record WorkspacePanelRequest(string Kind, string Path = "", string Search = "", string? CommandId = null, string? CheckpointId = null);
public sealed record WorkspaceCheckpoint(string Id, string SessionId, long Sequence, string Kind,
    DateTimeOffset CreatedAt, WorkspaceSnapshot? Snapshot, string? UnavailableReason);
public sealed record WorkspaceActivity(long Sequence, string Kind, string Text, string? RunId, string? ExecutionId,
    string? Agent, IReadOnlyList<string> Files, string Activity, WorkspaceCheckpoint? Checkpoint);
public sealed record WorkspaceAlternative(string CommandId, string Workspace, string Branch, string SessionId,
    string SourceWorkspace, string SourceSessionId, long ThroughSequence, string CheckpointId, string State);
public sealed record WorkspacePanelResult(int Version, string? SessionId, long Revision, string Kind,
    IReadOnlyList<WorkspaceFile>? Files = null, IReadOnlyList<string>? Matches = null, WorkspaceGitView? Git = null,
    WorkspaceFileView? File = null, IReadOnlyList<WorkspaceActivity>? Timeline = null,
    ConversationHistoryImported? Origin = null, WorkspaceAlternative? Alternative = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspacePanelRequest))]
[JsonSerializable(typeof(WorkspacePanelResult))]
[JsonSerializable(typeof(WorkspaceCheckpoint))]
[JsonSerializable(typeof(WorkspaceAlternative))]
[JsonSerializable(typeof(ConversationHistoryImported))]
internal partial class WorkspacePanelJson : JsonSerializerContext;
