namespace OmniCore.Tests;

using System.Text;
using System.Text.Json;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

public sealed partial class WorkspaceUndoTests
{
    [Fact]
    public async System.Threading.Tasks.Task Apply_patch_batch_appears_as_one_undoable_effect_with_each_file_verified()
    {
        using var fixture = new Fixture();
        var beforeA = Encoding.UTF8.GetBytes("head\nold-a\ntail\n");
        var beforeB = Encoding.UTF8.GetBytes("start\nold-b\nend\n");
        File.WriteAllBytes(Path.Combine(fixture.Root, "a.txt"), beforeA);
        File.WriteAllBytes(Path.Combine(fixture.Root, "b.txt"), beforeB);
        using var history = new SqliteWorkspaceMutationHistoryStore(Path.Combine(fixture.Root, "cas"));
        var turn = TurnId.New();
        var context = new ToolExecutionContext(fixture.Root, readRegistry: null, artifacts: fixture.Artifacts, mutationHistory: history,
            mutationRunId: fixture.Run.RunId.ToString(), mutationTurnId: turn.ToString());
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
        var files = new[]
        {
            new { path = "a.txt", expectedVersion = FileVersion.VersionToken(beforeA), hunks = new[] { new { oldText = "old-a", newText = "new-a" } } },
            new { path = "b.txt", expectedVersion = FileVersion.VersionToken(beforeB), hunks = new[] { new { oldText = "old-b", newText = "new-b" } } },
        };
        var previewJson = JsonSerializer.Serialize(new { mode = "preview", files });
        var previewPrepared = Assert.IsType<Prepared>(tool.Prepare(new ValidatedToolCall(ToolCallId.New(),
            tool.Descriptor.Id, "fixture", previewJson), new ToolPreparationContext(fixture.Root, DateTimeOffset.UtcNow)));
        var preview = await tool.ExecuteAsync(new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        { [FilesystemApplyPatchTool.Id] = PermissionDecision.Allow }).Authorize(previewPrepared.Intent), context,
            CancellationToken.None);
        using var previewDocument = JsonDocument.Parse(preview.Preview!);
        var token = previewDocument.RootElement.GetProperty("previewToken").GetString()!;

        var call = ToolCallId.New();
        var applyJson = JsonSerializer.Serialize(new { mode = "apply", previewToken = token, files });
        var prepared = Assert.IsType<Prepared>(tool.Prepare(new ValidatedToolCall(call, tool.Descriptor.Id,
            "fixture", applyJson), new ToolPreparationContext(fixture.Root, DateTimeOffset.UtcNow)));
        tool.ResolveEffectiveAuthorizationIntents(prepared.Intent,
            new ToolPreparationContext(fixture.Root, DateTimeOffset.UtcNow), context, CancellationToken.None);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            [FilesystemApplyPatchTool.Id] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        var authorized = policy.Authorize(prepared.Intent);
        using var fence = history.AcquireFence(fixture.Root, CancellationToken.None);
        var reconciliation = tool.DescribeReconciliation(authorized, context, CancellationToken.None)!;
        var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);
        Assert.Equal(EffectOutcome.Applied, result.EffectOutcome);

        var stream = new EventStream(fixture.Store, fixture.Codecs, fixture.Session);
        using (var execution = ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId, fixture.Run.RootTask,
            fixture.Run.RootLane, turn, call, ExecutionId.New())))
        {
            stream.Append(new ToolCallRequested(call, "fixture", FilesystemApplyPatchTool.Id, applyJson));
            stream.Append(new ToolCallPrepared(call, "{}"));
            stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(call));
            stream.Append(new ToolCallStarted(call, EffectClass.Reconcilable, reconciliation.DurableMetadataJson)
            {
                Attribution = new ToolCallAttribution(FilesystemApplyPatchTool.Id, SourceKind.BuiltIn.ToString(), null,
                    "1", null),
                Reversibility = Reversibility.Reversible,
                BeforeStateRef = reconciliation.BeforeStateRef,
            }, DurabilityClass.Barrier);
            stream.Append(new ToolCallSucceeded(call, "{}") { AfterStateRef = result.Artifact });
        }

        var service = fixture.Service(PermissionDecision.Allow);
        var plan = service.Preview(fixture.Session, call);
        Assert.Equal(2, plan.Entries.Count);
        Assert.All(plan.Entries, entry => Assert.Equal("Ready", entry.State));

        var undone = service.Undo(fixture.Session, call, CancellationToken.None);

        Assert.Equal(UndoOutcome.Applied, undone.Outcome);
        Assert.Equal(beforeA, File.ReadAllBytes(Path.Combine(fixture.Root, "a.txt")));
        Assert.Equal(beforeB, File.ReadAllBytes(Path.Combine(fixture.Root, "b.txt")));
        Assert.Equal(2, undone.Files.Count);
    }
}