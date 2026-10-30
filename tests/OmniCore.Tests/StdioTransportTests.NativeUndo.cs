namespace OmniCore.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Tools;

using Task = System.Threading.Tasks.Task;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Native_undo_requires_signed_exact_review_and_replays_the_durable_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-native-undo-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace"); var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace); Directory.CreateDirectory(data);
        var oldDirectory = Environment.CurrentDirectory;
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var key = RandomNumberGenerator.GetBytes(32); OmniServer? server = null; Task<int>? host = null;
        using var duplex = new RecoveryDuplex();
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
            var journal = Path.Combine(workspaceData, "journal.db");
            var session = SessionId.New(); var canonical = ProjectIdentity.CanonicalWorkspacePath(workspace);
            var before = Encoding.UTF8.GetBytes("before\n"); var after = Encoding.UTF8.GetBytes("after\n");
            var file = Path.Combine(workspace, "target.txt"); File.WriteAllBytes(file, after);
            var call = ToolCallId.New(); TestRun.Opened run;
            using (var store = new SqliteEventStore(journal))
            {
                var stream = new EventStream(store, EventCodecs.Create(), session);
                stream.Append(new SessionCreated(session, WorkspaceId.Of(canonical).ToString(), workspace, ProfileId.New(), DateTimeOffset.UtcNow));
                stream.Append(new WorkspaceRootEstablished(session, canonical, DateTimeOffset.UtcNow, WorkspaceRootIdentity.Establish(workspace)));
                run = TestRun.Open(stream, session);
                using var scope = ExecutionScope.Begin(new(run.RunId, run.RootTask, run.RootLane, TurnId.New(), call, ExecutionId.New()));
                var artifacts = new FileArtifactStore(workspaceData);
                var pre = FilesystemPreimage.Capture(new(FileVersion.VersionToken(before), FileVersion.VersionToken(after), null), artifacts, before).BeforeStateRef;
                var post = FilesystemPreimage.CaptureAfter(artifacts, after);
                stream.Append(new ToolCallRequested(call, "fixture", "filesystem.patch", "{}"));
                stream.Append(new ToolCallPrepared(call, "{}")); stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
                stream.Append(new ToolCallAuthorized(call));
                stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent,
                    FilesystemReconciliationMetadata.Encode("target.txt", FileVersion.VersionToken(before), FileVersion.VersionToken(after)))
                { TargetRef = "target.txt", BeforeStateRef = pre, Reversibility = Reversibility.Reversible }, DurabilityClass.Barrier);
                stream.Append(new ToolCallSucceeded(call, "{}") { AfterStateRef = post });
            }
            File.WriteAllText(Path.Combine(workspaceData, "lastsession.txt"), session + "\n" + run.RunId);
            server = OmniHost.OpenPersistentServer(journal, data); server.ConfigureWorkspaceRoot(workspace);
            host = Task.Run(() => StdioProtocolHost.RunWithTrustedActions(server, duplex.HostInput, duplex.HostOutput, TextWriter.Null, key));
            WireEnvelope Exchange(WireEnvelope request)
            {
                WireFrame.Write(duplex.ClientInput, request.ToJson());
                var response = WireEnvelope.FromJson(WireFrame.Read(duplex.ClientOutput) ?? throw new EndOfStreamException());
                Assert.Equal(request.MessageId, response.MessageId); return response;
            }
            using var hello = JsonDocument.Parse(Exchange(WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"undo-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"],\"client\":\"undo-test\",\"capabilities\":[\"query.workspaceUndo\",\"command.workspace.undo.apply.user\"]}}")).PayloadJson);
            var nonce = hello.RootElement.GetProperty("trustedActionNonce").GetString()!;
            UndoReview Review()
            {
                using var response = JsonDocument.Parse(Exchange(WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"query\",\"name\":\"" + UndoProtocol.QueryFor(call.ToString()) + "\"}")).PayloadJson);
                return UndoProtocol.Decode(response.RootElement.GetProperty("json").GetString()!)!;
            }
            var review = Review(); Assert.Null(review.Error); Assert.Single(review.Files);
            string Payload(UndoReview current) => "{" + JsonObj.Field("cmd", UndoProtocol.Apply) + "," + JsonObj.Field("sessionId", session.ToString())
                + "," + JsonObj.Field("effectId", call.ToString()) + "," + JsonObj.Field("revision", current.Revision) + "," + JsonObj.Field("confirmed", "true")
                + "," + JsonObj.Field("expectedRunId", run.RunId.ToString()) + "}";
            var command = WireEnvelope.Command(Ids.NewV7(), Payload(review));
            var unsigned = Ack(Exchange(WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"command\",\"command\":" + command.ToJson() + "}")));
            Assert.Equal("trusted_user_action_required", unsigned.Error);
            File.WriteAllText(file, "foreign\n");
            var stale = Ack(Exchange(TrustedActionEnvelope(command, key, nonce, workspace, session.ToString(), run.RunId.ToString())));
            Assert.Equal("undo.stale", stale.Error); Assert.Equal("foreign\n", File.ReadAllText(file));
            File.WriteAllBytes(file, after); review = Review(); command = WireEnvelope.Command(Ids.NewV7(), Payload(review));
            var proof = TrustedActionEnvelope(command, key, nonce, workspace, session.ToString(), run.RunId.ToString());
            var applied = Ack(Exchange(proof)); Assert.Equal("ok", applied.Status); Assert.Equal(before, File.ReadAllBytes(file));
            var eventCount = server.AcquireStore().ReadFrom(session, 1).Count;
            var replay = Ack(Exchange(proof)); Assert.Equal(applied.Status, replay.Status);
            Assert.Equal(eventCount, server.AcquireStore().ReadFrom(session, 1).Count);
            Assert.Equal(CommandReceiptState.Completed, server.ReceiptStore!.ReadCommandReceipt(command.MessageId)!.State);
        }
        finally
        {
            try
            {
                duplex.CompleteClientInput();
                if (host is not null) await host.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            finally
            {
                if (server?.AcquireStore() is IDisposable eventStore) eventStore.Dispose();
                CryptographicOperations.ZeroMemory(key);
                Environment.CurrentDirectory = oldDirectory;
                Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
                Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
                try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}