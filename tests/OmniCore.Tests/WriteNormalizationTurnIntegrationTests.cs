using System.Text.Json;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class WriteNormalizationTurnIntegrationTests
{
    [Fact]
    public void Model_read_then_existing_write_runs_through_boundary_ask_and_publishes_one_durable_patch()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-write-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = "first\nsecond\nthird\n";
        var path = Path.Combine(root, "doc.txt");
        File.WriteAllText(path, original);
        try
        {
            var key = ModelPolicyKey.For("fixture", "write-turn");
            var mutation = new FileMutationPolicy(FileMutationMode.PatchAndCreate,
                DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny, 4, 20, 1.0,
                requirePriorRead: true, requireExpectedVersionToken: true,
                requirePostEditValidation: false, allowParallelMutations: false);
            var caps = new HashSet<ModelToolCapability>
            {
                ModelToolCapability.WorkspaceRead, ModelToolCapability.PatchExisting,
                ModelToolCapability.CreateFile, ModelToolCapability.ReplaceFile,
            };
            var userPolicy = new UserModelPolicy(ModelPolicyCategory.Custom,
                new ModelToolPolicy(ToolMode.Direct, 8, false, caps), mutation, "fixture", null);
            var effective = EffectiveModelPolicy.Resolve(key,
                new StoredModelPolicy(key, 1, userPolicy, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                    PlanControl.ModelDriven, 8));
            var boundary = new ModelCapabilityBoundary(effective);
            var tools = OmniHost.CreateActTools();
            var catalog = tools.Catalog();
            var permission = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
            {
                ["filesystem.read"] = PermissionDecision.Allow,
                ["filesystem.write"] = PermissionDecision.Allow,
                ["filesystem.patch"] = PermissionDecision.Ask,
            });
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session, "normalize existing write");
            var artifacts = new FileArtifactStore(Path.Combine(root, "artifacts"));
            var executor = new ScriptedToolExecutor(catalog, permission, root, boundary,
                new InMemoryAuditSink(), _ => "allow_once", isInteractive: true, artifacts: artifacts);
            var readId = ToolCallId.New();
            var writeId = ToolCallId.New();
            var requests = new List<ModelRequest>();
            var calls = 0;
            var desired = "first\nsecond-edited\nthird\n";
            var version = FileVersion.VersionToken(File.ReadAllBytes(path));
            ModelResponse Respond(ModelRequest request, CancellationToken _)
            {
                requests.Add(request);
                calls++;
                if (calls == 1)
                    return new ModelResponse([new ToolCallBlock(readId, "read", "filesystem.read",
                        "{\"path\":\"doc.txt\"}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "model", null));
                if (calls == 2)
                    return new ModelResponse([new ToolCallBlock(writeId, "write", "filesystem.write",
                        JsonSerializer.Serialize(new { path = "doc.txt", content = desired, expectedVersion = version }))],
                        StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "model", null));
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "model", null));
            }
            var turn = new ExplorerTurn(Respond, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "write-turn"),
                new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                boundary: boundary);

            var result = turn.Ask("edit second line", "edit only the requested line", session, run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(3, requests.Count);
            Assert.Contains(requests[1].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(),
                block => block.Id == readId && !block.IsError);
            Assert.Equal(desired, File.ReadAllText(path));
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Contains(events.OfType<ToolCallRequested>(), item => item.ToolCallId == writeId
                && item.ToolName == "filesystem.write");
            Assert.Single(events.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.Permission);
            Assert.Single(events.OfType<PermissionGranted>(), item => item.ToolCallId == writeId
                && item.Lifetime == GrantLifetime.Once);
            Assert.Single(events.OfType<ToolCallAuthorized>(), item => item.ToolCallId == writeId);
            Assert.Single(events.OfType<MutationBudgetCharged>(), item => item.ToolCallId == writeId
                && item.ChangedLines == 2);
            var started = Assert.Single(events.OfType<ToolCallStarted>(), item => item.ToolCallId == writeId);
            Assert.Equal("doc.txt", started.TargetRef);
            Assert.NotNull(started.BeforeStateRef);
            Assert.Equal(Encoding.UTF8.GetBytes(original), FilesystemPreimage.Read(artifacts, started.BeforeStateRef));
            var succeeded = Assert.Single(events.OfType<ToolCallSucceeded>(), item => item.ToolCallId == writeId);
            Assert.NotNull(succeeded.AfterStateRef);
            Assert.Equal(Encoding.UTF8.GetBytes(desired), FilesystemPreimage.Read(artifacts, succeeded.AfterStateRef));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
