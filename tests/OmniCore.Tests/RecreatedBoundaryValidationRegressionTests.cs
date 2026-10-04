using Microsoft.Data.Sqlite;
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

public sealed class RecreatedBoundaryValidationRegressionTests
{
    // Desired invariant: no successful completion before validation, even with a recreated
    // capability boundary. Recreating in this process is NOT an OS-restart test.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completion_without_validation_rejects_pending_edit_after_boundary_recreation(bool recreateBoundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-recreated-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            const string original = "class Program { BROKEN }\n// 2\n// 3\n// 4\n// 5\n// 6\n// 7\n// 8\n";
            File.WriteAllText(Path.Combine(root, "Program.cs"), original);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var key = ModelPolicyKey.For("scripted", "coder");
            var preset = ModelPolicyPresets.PatchOnly();
            var stored = new StoredModelPolicy(key, 1,
                new UserModelPolicy(preset.Category, preset.ToolPolicy, preset.MutationPolicy, "test", null),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8,
                GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8);
            var policy = EffectiveModelPolicy.Resolve(key, stored, harness);
            var catalog = OmniHost.CreateActTools().Catalog();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete,
                out ModelCapabilityBoundary boundary)
            {
                boundary = OmniCliRuntime.CreateBoundary(policy, root);
                return new ExplorerTurn(complete, OmniHost.CreateActExecutor(catalog, root, boundary), catalog,
                    new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                    new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3", policy.Fingerprint()),
                    new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                    store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), boundary: boundary);
            }
            var step = 0;
            ModelResponse Complete(ModelRequest request, CancellationToken token)
            {
                var content = step++ switch
                {
                    0 => new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "read", "filesystem.read", "{\"path\":\"Program.cs\"}") },
                    1 => new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "patch", "filesystem.patch",
                        "{\"path\":\"Program.cs\",\"expectedVersion\":\""
                        + FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes(original))
                        + "\",\"oldText\":\"BROKEN\",\"newText\":\"FIXED\"}") },
                    _ => new ContentBlock[] { new TextBlock("done") },
                };
                return new ModelResponse(content, step <= 2 ? StopReason.ToolUse : StopReason.EndTurn,
                    new TokenUsage(2, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }
            var turn = MakeTurn(Complete, out var originalBoundary);
            var result = turn.Ask("fix", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Contains(result.ToolCalls, call => call.ToolName == "filesystem.patch" && call.Succeeded);
            Assert.Equal(original.Replace("BROKEN", "FIXED", StringComparison.Ordinal), File.ReadAllText(Path.Combine(root, "Program.cs")));
            Assert.Single(originalBoundary.ReadRegistry().Ledger.PendingValidations());
            var editEnvelope = Assert.Single(store.ReadFrom(session, 1),
                evt => codecs.Decode(evt) is PostEditValidationPending);
            var edit = Assert.IsType<PostEditValidationPending>(codecs.Decode(editEnvelope));
            Assert.Equal(run.RunId, edit.RunId);
            Assert.Equal("Program.cs", Assert.Single(edit.Paths));
            var startedEnvelope = Assert.Single(store.ReadFrom(session, 1),
                evt => codecs.Decode(evt) is ToolCallStarted started && started.ToolCallId == edit.ToolCallId);
            Assert.True(editEnvelope.Sequence < startedEnvelope.Sequence);
            if (recreateBoundary)
            {
                store.Close();
                store = new SqliteEventStore(journal);
                turn = MakeTurn(Complete, out _);
                Assert.Empty(turn.MutationLedger!.PendingValidations());
            }
            var events = store.ReadFrom(session, 1);
            var projection = RunProjection.Replay(session, run.RunId, codecs, events);
            var completed = new RunCoupon(projection, TaskGraphProjection.Replay(codecs, events),
                PlanProjection.Replay(codecs, events)).CheckCompletionAndGate(new PlanService(),
                    new ProgressReconciler(), store, codecs, session, new EventStream(store, codecs, session),
                    mutationLedger: turn.MutationLedger);
            Assert.False(completed);
            var rejected = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<RunValidationRejected>().Last();
            Assert.Contains("post-edit-validation", rejected.Gates);
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
