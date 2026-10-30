using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class FilesystemApplyPatchToolTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async System.Threading.Tasks.Task Preview_accepts_native_decoder_optional_token_without_writes(string? token)
    {
        using var fixture = new Fixture();
        fixture.Write("a.txt", "old\n");
        var request = JsonSerializer.Serialize(new { mode = "preview", previewToken = token,
            files = new[] { fixture.File("a.txt", "old", "new") } });
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
        Assert.Null(ToolSchemaValidator.Validate(request, tool.Descriptor.InputSchema));
        var (authorized, context) = fixture.Authorize(tool, request);
        var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(EffectOutcome.None, result.EffectOutcome);
        using var preview = JsonDocument.Parse(result.Preview!);
        Assert.Equal(64, preview.RootElement.GetProperty("previewToken").GetString()!.Length);
        Assert.Equal("old\n", fixture.Read("a.txt"));
    }

    [Fact]
    public void Read_only_preview_reaches_execution_through_the_real_tool_runtime()
    {
        using var fixture = new Fixture();
        fixture.Write("a.txt", "head\nold-a\ntail\n");
        fixture.Write("b.txt", "head\nold-b\ntail\n");
        var request = fixture.Request("preview", [fixture.File("a.txt", "old-a", "new-a"),
            fixture.File("b.txt", "old-b", "new-b")]);
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
        var events = new List<DomainEventPayload>();
        var runtime = ToolRuntime.For(new FakeCatalog().Add(tool), fixture.Policy(),
            payload => { events.Add(payload); return VoidBox.Instance; });
        var result = runtime.Run(fixture.Call(tool, request),
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow),
            fixture.Context, false, CancellationToken.None);
        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(EffectOutcome.None, result.Effect);
        using var preview = JsonDocument.Parse(result.Preview!);
        Assert.Equal(64, preview.RootElement.GetProperty("previewToken").GetString()!.Length);
        Assert.Single(events.OfType<ToolCallSucceeded>());
        Assert.Empty(events.OfType<ToolCallFailed>());
        Assert.Equal("head\nold-a\ntail\n", fixture.Read("a.txt"));
        Assert.Equal("head\nold-b\ntail\n", fixture.Read("b.txt"));
    }

    [Fact]
    public void Preview_exception_does_not_allow_an_unrelated_empty_batch_to_execute()
    {
        using var fixture = new Fixture();
        var tool = new EmptyBatchResolver();
        var runtime = ToolRuntime.For(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("empty.batch", PermissionDecision.Allow), _ => VoidBox.Instance);
        var result = runtime.Run(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "empty-batch", "{}"),
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow), fixture.Context, false, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(0, tool.ExecutionCount);
    }

    private sealed class EmptyBatchResolver : ITool, IEffectiveToolAuthorizationIntents
    {
        public int ExecutionCount { get; private set; }
        public ToolDescriptor Descriptor { get; } = new(new ToolId("empty.batch"), "empty batch fixture",
            new InputSchema("{\"type\":\"object\"}"), [], true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) => new Prepared(
            new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.None,
                ResourceClaims.Empty(), ToolRisk.Low, null));
        public IReadOnlyList<ToolIntent> ResolveEffectiveAuthorizationIntents(ToolIntent intent,
            ToolPreparationContext preparation, ToolExecutionContext execution, CancellationToken cancellationToken) => [];
        public System.Threading.Tasks.Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent,
            ToolExecutionContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return System.Threading.Tasks.Task.FromResult(ToolResult.Ok("unexpected execution", null, 0, false, EffectOutcome.None));
        }
    }

    [Theory]
    [InlineData("apply", "null")]
    [InlineData("preview", "true")]
    [InlineData("preview", "\"invalid\"")]
    public void Nullable_preview_schema_never_authorizes_apply_without_a_real_token(string mode, string tokenJson)
    {
        using var fixture = new Fixture();
        fixture.Write("a.txt", "old\n");
        var files = JsonSerializer.Serialize(new[] { fixture.File("a.txt", "old", "new") });
        var request = "{\"mode\":\"" + mode + "\",\"previewToken\":" + tokenJson + ",\"files\":" + files + "}";
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
        Assert.IsType<PreparationRejected>(tool.Prepare(fixture.Call(tool, request),
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow)));
        Assert.Equal("old\n", fixture.Read("a.txt"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Preview_returns_deterministic_multi_file_diff_and_token_without_writes()
    {
        using var fixture = new Fixture();
        fixture.Write("a.txt", "head\nold-a\ntail\n");
        fixture.Write("b.txt", "start\nold-b\nend\n");
        var request = fixture.Request("preview", [
            fixture.File("a.txt", "old-a", "new-a"),
            fixture.File("b.txt", "old-b", "new-b"),
        ]);
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
        var (authorized, context) = fixture.Authorize(tool, request);

        var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(EffectOutcome.None, result.EffectOutcome);
        using var preview = JsonDocument.Parse(result.Preview!);
        Assert.Equal(64, preview.RootElement.GetProperty("previewToken").GetString()!.Length);
        Assert.Contains("a.txt", preview.RootElement.GetProperty("diff").GetString());
        Assert.Contains("b.txt", preview.RootElement.GetProperty("diff").GetString());
        Assert.Equal("head\nold-a\ntail\n", fixture.Read("a.txt"));
        Assert.Equal("start\nold-b\nend\n", fixture.Read("b.txt"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Apply_authorizes_all_files_and_records_each_durable_receipt()
    {
        using var fixture = new Fixture();
        fixture.Write("a.txt", "head\nold-a\ntail\n");
        fixture.Write("b.txt", "start\nold-b\nend\n");
        var files = new[] { fixture.File("a.txt", [new Hunk("old-a", "new-a"), new Hunk("tail", "footer")]),
            fixture.File("b.txt", "old-b", "new-b") };
        var preview = await fixture.GetPreview(files);
        var request = fixture.Request("apply", files, preview.Token);
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
        var prepared = Assert.IsType<Prepared>(tool.Prepare(fixture.Call(tool, request),
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow)));
        var policy = fixture.Policy();
        var effective = tool.ResolveEffectiveAuthorizationIntents(prepared.Intent,
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow), fixture.Context, CancellationToken.None);
        Assert.Equal(2, effective.Count);
        Assert.All(effective, intent => Assert.Equal(PermissionDecision.Allow, policy.Evaluate(intent).Final));
        var authorized = policy.Authorize(prepared.Intent);
        using var fence = fixture.History.AcquireFence(fixture.Workspace, CancellationToken.None);
        var reconciliation = tool.DescribeReconciliation(authorized, fixture.Context, CancellationToken.None)!;
        Assert.True(tool.ValidateFilesystemBatchEnvelope(prepared.Intent, effective, fixture.Context, reconciliation));

        var result = await tool.ExecuteAsync(authorized, fixture.Context, CancellationToken.None);

        Assert.False(result.IsError, result.Summary);
        Assert.Equal(EffectOutcome.Applied, result.EffectOutcome);
        Assert.Equal("head\nnew-a\nfooter\n", fixture.Read("a.txt"));
        Assert.Equal("start\nnew-b\nend\n", fixture.Read("b.txt"));
        var manifestJson = fixture.Artifacts.GetText(result.Artifact!.Hash) ?? throw new InvalidDataException("Manifest not found.");
        Assert.True(ApplyPatchMetadata.TryParse(manifestJson, out var manifest));
        Assert.Equal(2, manifest.Files.Count);
        Assert.All(manifest.Files, file =>
        {
            Assert.Equal(file.BeforeHash, FileVersion.VersionToken(FilesystemPreimage.Read(fixture.Artifacts, file.Preimage)!));
            Assert.Equal(file.AfterHash, FileVersion.VersionToken(FilesystemPreimage.Read(fixture.Artifacts, file.Postimage)!));
        });
        Assert.All(new[] { "a.txt", "b.txt" }, path =>
        {
            Assert.Equal(WorkspaceFileHistoryState.Present, fixture.History.ReadFile(fixture.Workspace, path)!.State);
            Assert.Equal(WorkspaceMutationReceiptState.Applied,
                fixture.History.ReadReceipt(fixture.Workspace, ToolCallId.Parse(prepared.Intent.ToolCallId.ToString()), path)!.State);
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task Stale_second_file_rejects_before_any_batch_effect()
    {
        using var fixture = new Fixture();
        fixture.Write("a.txt", "old-a\n");
        fixture.Write("b.txt", "old-b\n");
        var files = new[] { fixture.File("a.txt", "old-a", "new-a"), fixture.File("b.txt", "old-b", "new-b") };
        var preview = await fixture.GetPreview(files);
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
        var request = fixture.Request("apply", files, preview.Token);
        var prepared = Assert.IsType<Prepared>(tool.Prepare(fixture.Call(tool, request),
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow)));
        tool.ResolveEffectiveAuthorizationIntents(prepared.Intent,
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow), fixture.Context, CancellationToken.None);
        fixture.Write("b.txt", "someone-else\n");
        var authorized = fixture.Policy().Authorize(prepared.Intent);

        var error = Assert.Throws<ToolPreflightException>(() =>
            tool.DescribeReconciliation(authorized, fixture.Context, CancellationToken.None));

        Assert.Equal(ToolErrorCode.StaleWrite, error.ErrorCode);
        Assert.Equal("old-a\n", fixture.Read("a.txt"));
        Assert.Equal("someone-else\n", fixture.Read("b.txt"));
        Assert.Empty(fixture.History.ReadPending(fixture.Workspace));
    }

    [Fact]
    public async System.Threading.Tasks.Task Interruption_after_one_file_is_unknown_and_reconciles_as_conflict_without_rollback()
    {
        using var fixture = new Fixture();
        fixture.Write("a.txt", "old-a\n");
        fixture.Write("b.txt", "old-b\n");
        var files = new[] { fixture.File("a.txt", "old-a", "new-a"), fixture.File("b.txt", "old-b", "new-b") };
        var preview = await fixture.GetPreview(files);
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator())
        { TestAfterPublish = (index, _) => { if (index == 0) throw new IOException("injected after first file"); } };
        var request = fixture.Request("apply", files, preview.Token);
        var prepared = Assert.IsType<Prepared>(tool.Prepare(fixture.Call(tool, request),
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow)));
        var effective = tool.ResolveEffectiveAuthorizationIntents(prepared.Intent,
            new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow), fixture.Context, CancellationToken.None);
        var authorized = fixture.Policy().Authorize(prepared.Intent);
        using var fence = fixture.History.AcquireFence(fixture.Workspace, CancellationToken.None);
        var reconciliation = tool.DescribeReconciliation(authorized, fixture.Context, CancellationToken.None)!;
        Assert.True(tool.ValidateFilesystemBatchEnvelope(prepared.Intent, effective, fixture.Context, reconciliation));

        var result = await tool.ExecuteAsync(authorized, fixture.Context, CancellationToken.None);
        var recovered = new FilesystemReconciler(new PathBoundaryValidator()).Reconcile(fixture.Workspace,
            reconciliation.DurableMetadataJson!, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(EffectOutcome.Unknown, result.EffectOutcome);
        Assert.Equal(ReconciliationOutcome.Conflict, recovered.Outcome);
        Assert.Equal("new-a\n", fixture.Read("a.txt"));
        Assert.Equal("old-b\n", fixture.Read("b.txt"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".git/config")]
    public void Prepare_rejects_escape_and_protected_metadata_paths(string path)
    {
        using var fixture = new Fixture();
        var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
        var request = "{\"mode\":\"preview\",\"files\":[{\"path\":" + JsonSerializer.Serialize(path)
            + ",\"expectedVersion\":\"" + new string('a', 64)
            + "\",\"hunks\":[{\"oldText\":\"x\",\"newText\":\"y\"}]}]}";
        var result = tool.Prepare(fixture.Call(tool, request), new ToolPreparationContext(fixture.Workspace, DateTimeOffset.UtcNow));
        Assert.IsType<PreparationRejected>(result);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-apply-patch-" + Guid.NewGuid().ToString("N"));
        public string Workspace { get; }
        public string Data { get; }
        public FileArtifactStore Artifacts { get; }
        public SqliteWorkspaceMutationHistoryStore History { get; }
        public ToolExecutionContext Context { get; }

        public Fixture()
        {
            Workspace = Path.Combine(Root, "workspace"); Data = Path.Combine(Root, "data");
            Directory.CreateDirectory(Workspace); Directory.CreateDirectory(Data);
            var mutationData = Path.Combine(Data, "mutation-history");
            Artifacts = new FileArtifactStore(mutationData);
            History = new SqliteWorkspaceMutationHistoryStore(mutationData);
            Context = new ToolExecutionContext(Workspace, readRegistry: null, artifacts: Artifacts, mutationHistory: History,
                mutationRunId: Guid.NewGuid().ToString("N"), mutationTurnId: Guid.NewGuid().ToString("N"));
        }

        public void Write(string path, string text) => System.IO.File.WriteAllText(Path.Combine(Workspace, path), text, new UTF8Encoding(false));
        public string Read(string path) => System.IO.File.ReadAllText(Path.Combine(Workspace, path));
        public FileRequest File(string path, string oldText, string newText) => new(path,
            FileVersion.VersionToken(System.IO.File.ReadAllBytes(Path.Combine(Workspace, path))), [new Hunk(oldText, newText)]);
        public FileRequest File(string path, IReadOnlyList<Hunk> hunks) => new(path,
            FileVersion.VersionToken(System.IO.File.ReadAllBytes(Path.Combine(Workspace, path))), hunks);

        public string Request(string mode, IReadOnlyList<FileRequest> files, string? token = null)
        {
            var payload = new Dictionary<string, object> { ["mode"] = mode, ["files"] = files };
            if (mode == "apply") payload["previewToken"] = token ?? throw new ArgumentNullException(nameof(token));
            return JsonSerializer.Serialize(payload);
        }

        public async System.Threading.Tasks.Task<(string Token, string Diff)> GetPreview(IReadOnlyList<FileRequest> files)
        {
            var tool = new FilesystemApplyPatchTool(new PathBoundaryValidator());
            var request = Request("preview", files);
            var (authorized, context) = Authorize(tool, request);
            var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);
            using var document = JsonDocument.Parse(result.Preview!);
            return (document.RootElement.GetProperty("previewToken").GetString()!,
                document.RootElement.GetProperty("diff").GetString()!);
        }

        public (AuthorizedToolIntent Authorized, ToolExecutionContext Context) Authorize(FilesystemApplyPatchTool tool,
            string request)
        {
            var preparation = tool.Prepare(Call(tool, request), new ToolPreparationContext(Workspace, DateTimeOffset.UtcNow));
            Assert.True(preparation is Prepared, (preparation as PreparationRejected)?.Reason);
            var prepared = (Prepared)preparation;
            return (Policy().Authorize(prepared.Intent), Context);
        }

        public ValidatedToolCall Call(FilesystemApplyPatchTool tool, string request) =>
            new(ToolCallId.New(), tool.Descriptor.Id, "apply-patch-fixture", request);

        public ScriptedPermissionPolicy Policy() => new(new Dictionary<string, PermissionDecision>
        {
            [FilesystemApplyPatchTool.Id] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });

        public void Dispose()
        {
            History.Dispose();
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed record FileRequest(string path, string expectedVersion, IReadOnlyList<Hunk> hunks);
    private sealed record Hunk(string oldText, string newText);
}
