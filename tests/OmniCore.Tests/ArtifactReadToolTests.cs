using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ArtifactReadToolTests
{
    [Fact]
    public void Reads_externalized_artifact_in_pages_when_session_references_hash()
    {
        var data = TempDir();
        try
        {
            var artifacts = new FileArtifactStore(data);
            var artifact = artifacts.PutText("0123456789 café 🚀", "text/plain", ArtifactKind.ToolOutput,
                Sensitivity.Normal);
            var executor = Executor(artifacts, hash => hash == artifact.Hash.Value, data);

            var first = executor.ExecuteToolWithoutJournal(Call(artifact.Hash.Value, offset: 0, limit: 10), false,
                CancellationToken.None);
            var second = executor.ExecuteToolWithoutJournal(Call(artifact.Hash.Value, offset: 10, limit: 8), false,
                CancellationToken.None);

            Assert.True(first.Succeeded, first.Summary);
            Assert.Equal("0123456789", first.Preview);
            Assert.True(second.Succeeded, second.Summary);
            Assert.Equal(" café 🚀", second.Preview);
            Assert.Contains("end=true", second.Summary);
        }
        finally { Delete(data); }
    }

    [Fact]
    public void Re_read_returns_the_redacted_artifact_content_not_the_original_secret()
    {
        var data = TempDir();
        try
        {
            var artifacts = new FileArtifactStore(data);
            const string secret = "VOtOkEnSecret123";
            var artifact = artifacts.PutText("Bearer " + secret, "text/plain", ArtifactKind.ToolOutput,
                Sensitivity.Sensitive);
            Assert.True(artifact.Redacted);
            var executor = Executor(artifacts, hash => hash == artifact.Hash.Value, data);

            var result = executor.ExecuteToolWithoutJournal(Call(artifact.Hash.Value), false, CancellationToken.None);

            Assert.True(result.Succeeded, result.Summary);
            Assert.DoesNotContain(secret, result.Preview);
        }
        finally { Delete(data); }
    }

    [Fact]
    public void Refuses_to_read_blob_not_referenced_by_current_session()
    {
        var data = TempDir();
        try
        {
            var artifacts = new FileArtifactStore(data);
            var artifact = artifacts.PutText("private CAS content", "text/plain", ArtifactKind.ToolOutput,
                Sensitivity.Normal);
            var executor = Executor(artifacts, _ => false, data);

            var result = executor.ExecuteToolWithoutJournal(Call(artifact.Hash.Value), false, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("not referenced", result.Summary);
        }
        finally { Delete(data); }
    }

    [Fact]
    public void Session_authorizer_follows_snapshot_stub_refs_but_not_other_CAS_blobs()
    {
        var data = TempDir();
        try
        {
            var artifacts = new FileArtifactStore(data);
            var referencedOutput = artifacts.PutText("externalized output", "text/plain", ArtifactKind.ToolOutput,
                Sensitivity.Normal);
            var unrelated = artifacts.PutText("unreferenced CAS content", "text/plain", ArtifactKind.Other,
                Sensitivity.Normal);
            var snapshot = artifacts.PutText(
                "[tool output externalized; re-read with artifact.read using hash=\"" + referencedOutput.Hash.Value + "\"]",
                "application/json", ArtifactKind.ContextSnapshot, Sensitivity.Normal);
            var store = new InMemoryEventStore();
            var session = SessionId.New();
            var otherSession = SessionId.New();
            var snapshotEvent = DomainEvent.Create(session, EventType.Of("turn.started"), 2, null, null, null,
                null, null, TurnId.New(), null, null, new[] { snapshot }, "{}");
            store.Append(session, snapshotEvent, DurabilityClass.Standard, CancellationToken.None);
            var otherSessionEvent = DomainEvent.Create(otherSession, EventType.Of("turn.started"), 2, null, null,
                null, null, null, TurnId.New(), null, null, new[] { unrelated }, "{}");
            store.Append(otherSession, otherSessionEvent, DurabilityClass.Standard, CancellationToken.None);
            var authorization = new SessionArtifactReadAuthorization(store, () => session, artifacts);

            Assert.True(authorization.IsReferenced(referencedOutput.Hash.Value));
            Assert.False(authorization.IsReferenced(unrelated.Hash.Value));
        }
        finally { Delete(data); }
    }

    [Fact]
    public void Rejects_invalid_hash_and_unbounded_page_sizes_during_prepare()
    {
        var data = TempDir();
        try
        {
            var artifacts = new FileArtifactStore(data);
            var executor = Executor(artifacts, _ => true, data);
            var malformed = executor.ExecuteToolWithoutJournal(Call("../secret"), false, CancellationToken.None);
            var oversized = executor.ExecuteToolWithoutJournal(Call(new string('a', 64), limit: 20000), false,
                CancellationToken.None);

            Assert.False(malformed.Succeeded);
            Assert.False(oversized.Succeeded);
        }
        finally { Delete(data); }
    }

    private static ScriptedToolExecutor Executor(IArtifactStore artifacts, Func<string, bool> isReferenced,
        string workspace)
    {
        var tool = new ArtifactReadTool(artifacts, isReferenced);
        var tools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            includeMutationTools: false, artifactReadTool: tool);
        return ScriptedToolExecutor.WithWorkspace(tools.Catalog(),
            ScriptedPermissionPolicy.WithTool("artifact.read", PermissionDecision.Allow), workspace);
    }

    private static ValidatedToolCall Call(string hash, int? offset = null, int? limit = null)
    {
        var arguments = new Dictionary<string, object> { ["hash"] = hash };
        if (offset is not null) arguments["offset"] = offset.Value;
        if (limit is not null) arguments["limit"] = limit.Value;
        return new ValidatedToolCall(ToolCallId.New(), new ToolId("artifact.read"), "artifact-read-test",
            JsonSerializer.Serialize(arguments));
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-artifact-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
    }
}
