namespace OmniCore.Tests;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

public sealed class WindowsAppContainerSandboxTests
{
    [Fact]
    public async Task Strong_terminal_uses_ConPTY_for_real_duplex_input_resize_and_output()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("AppContainer integration tests run only on Windows."); return; }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        { Assert.Skip("ConPTY requires Windows 10 version 1809 or newer."); return; }
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable) { Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason); return; }

        var root = Path.Combine(Path.GetTempPath(), "OmniConPty" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe");
        Assert.True(File.Exists(executable), "The built Strong test helper must be present beside the test assembly.");
        ISandboxPersistentProcessControl? process = null;
        Task<string>? outputTask = null;
        try
        {
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, executable, ["terminal-echo"], root,
                new Dictionary<string, string>(),
                [new SandboxAllowedPath(root, SandboxPathAccess.Read | SandboxPathAccess.Write)],
                new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(10))
            { Duplex = true, Terminal = true };
            process = Assert.IsAssignableFrom<ISandboxPersistentProcessControl>(
                await new WindowsAppContainerProcessLauncher().StartAsync(launch, TestContext.Current.CancellationToken));
            outputTask = new StreamReader(process.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.True(process.IsTerminal);
            process.ResizeTerminal(100, 32);
            await process.StandardInput.WriteAsync(System.Text.Encoding.UTF8.GetBytes("terminal-test\r\n"), TestContext.Current.CancellationToken);
            await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            var exitCode = await process.WaitForExitCodeAsync(TestContext.Current.CancellationToken);
            var output = await outputTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await process.CloseInputAsync();
            await process.DisposeAsync();
            Assert.Equal(0, exitCode);
            Assert.Contains("ACK:terminal-test", output, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (process is not null) await process.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (outputTask is not null)
                    {
                        try { _ = await outputTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
                    }
                }
                finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
            }
        }
    }

    [Theory]
    [InlineData(PermissionProfile.Balanced, false, PermissionDecision.Ask, false, false, false, false, false)]
    [InlineData(PermissionProfile.Autonomous, true, PermissionDecision.Allow, false, false, false, false, false)]
    [InlineData(PermissionProfile.Balanced, false, PermissionDecision.Ask, true, false, false, false, false)]
    [InlineData(PermissionProfile.Balanced, false, PermissionDecision.Deny, true, false, false, false, false)]
    [InlineData(PermissionProfile.Balanced, false, PermissionDecision.Allow, false, true, false, false, false)]
    [InlineData(PermissionProfile.Balanced, false, PermissionDecision.Allow, false, false, true, false, false)]
    [InlineData(PermissionProfile.Autonomous, true, PermissionDecision.Allow, false, false, false, true, false)]
    [InlineData(PermissionProfile.Autonomous, true, PermissionDecision.Allow, false, false, false, true, true)]
    public async Task Strong_extension_batch_is_published_as_one_protected_host_action(
        PermissionProfile profile, bool automatic, PermissionDecision applyDecision, bool blockApplySilently,
        bool staleBeforeApply, bool rejectByBudget, bool seedTombstone, bool retryBlockedProposal)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("AppContainer integration tests run only on Windows."); return; }
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable) { Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason); return; }

        var root = Path.Combine(Path.GetTempPath(), "OmniExtensionBatchE2E" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(root, "package");
        var data = Path.Combine(root, "runtime");
        Directory.CreateDirectory(workspace); Directory.CreateDirectory(package);
        try
        {
            File.WriteAllText(Path.Combine(workspace, ".gitignore"), "m9-extension-marker.txt\n");
            File.WriteAllText(Path.Combine(workspace, "tracked.txt"), "baseline\n");
            RunGit(workspace, "init"); RunGit(workspace, "config", "user.name", "Test");
            RunGit(workspace, "config", "user.email", "test@localhost");
            RunGit(workspace, "add", ".gitignore", "tracked.txt"); RunGit(workspace, "commit", "-m", "base");
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess*")
                .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "OmniCore.*.dll")))
                File.Copy(file, Path.Combine(package, Path.GetFileName(file)), overwrite: true);
            File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
                manifestVersion: 1
                id: fixture.batch
                version: 1.0.0
                displayName: Batch fixture
                publisher: tests
                omnicoreApi: '1.0'
                provides:
                  tools:
                    - name: mutate
                      schema: schema.json
                requests:
                  capabilities: [tool.register]
                  permissions:
                    process:
                      - executable: OmniCore.SandboxTestProcess.exe
                    paths:
                      write: [m9-extension-marker.txt]
                runtime:
                  kind: process
                  command: OmniCore.SandboxTestProcess.exe
                  args: [extension-rpc-write-marker, ., m9-extension-marker.txt]
                """);
            File.WriteAllText(Path.Combine(package, "schema.json"), "{\"type\":\"object\"}");
            var discovered = Assert.Single(new ExtensionManifestLoader().Discover(package, TrustLevel.Project,
                workspaceTrusted: true, consentedCapabilities: new HashSet<string>(["tool.register"], StringComparer.Ordinal)));
            var extensionTool = new ExtensionTool(discovered, Assert.Single(discovered.Manifest!.Provides!.Tools!),
                File.ReadAllText(Path.Combine(package, "schema.json")),
                new ExtensionRpcProcessInvoker(new WindowsAppContainerProcessLauncher()), workspace,
                stillConsented: () => true);

            var artifacts = new FileArtifactStore(data);
            using var history = new SqliteWorkspaceMutationHistoryStore(data);
            string? tombstoneHashForRead = null;
            if (seedTombstone)
            {
                var oldImage = System.Text.Encoding.UTF8.GetBytes("old logical image\n");
                var oldHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(oldImage));
                tombstoneHashForRead = oldHash;
                history.RecordObservation(workspace, "m9-extension-marker.txt", oldHash,
                    TestContext.Current.CancellationToken);
                var deletion = new WorkspaceMutationReceipt(history.GetWorkspaceId(workspace),
                    "m9-extension-marker.txt", ToolCallId.New(), "seed-run", "seed-turn", "DeleteFile",
                    oldHash, null, FilesystemPreimage.CaptureHistoryImage(artifacts, oldImage), null,
                    1, 0, WorkspaceMutationReceiptState.Prepared);
                history.Prepare([deletion], TestContext.Current.CancellationToken);
                history.Complete([deletion with { State = WorkspaceMutationReceiptState.Applied }],
                    [new WorkspaceFileHistoryEntry(deletion.WorkspaceId, deletion.RelativePath,
                        WorkspaceFileHistoryState.Tombstone, oldHash, deletion.PreimageRef, 2, deletion.ToolCallId)],
                    TestContext.Current.CancellationToken);
            }
            var coordinator = new WorktreeIntegrationHostCoordinator(workspace, data, artifacts,
                new GitWorktreeStore(SystemProcessRuntime.Instance()));
            var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
                includeMutationTools: true, includeProcessTools: false, worktreeIntegrationCoordinator: coordinator);
            hostTools.RegisterExtensionTool(extensionTool);
            var catalog = hostTools.Catalog();
            var key = ModelPolicyKey.For("extension-batch-fixture", "extension-batch-fixture");
            var fullAgent = ModelPolicyPresets.FullAgent();
            var selectedPolicy = rejectByBudget
                ? new UserModelPolicy(fullAgent.Category, fullAgent.ToolPolicy,
                    new FileMutationPolicy(FileMutationMode.Full, DestructiveActionPolicy.Ask,
                        DestructiveActionPolicy.Ask, 8, 0, 1.0, requirePriorRead: true,
                        requireExpectedVersionToken: true, requirePostEditValidation: true,
                        allowParallelMutations: false), fullAgent.Source, fullAgent.Note,
                    fullAgent.MaxModelStepsPerTurn, fullAgent.MaxToolCallsPerTurn,
                    fullAgent.AllowOrchestration, fullAgent.MaxGenerationSeconds)
                : fullAgent;
            var modelPolicy = EffectiveModelPolicy.Resolve(key,
                new StoredModelPolicy(key, 1, selectedPolicy, DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 16, GuidanceLevel.Full, 3,
                    PlanControl.Assisted, 8));
            var boundary = OmniCliRuntime.CreateBoundary(modelPolicy, workspace, catalog);
            var events = new List<DomainEventPayload>();
            var permissions = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>())
                .WithModeDefaults(RunMode.Act).WithProfile(profile);
            if (!automatic)
                permissions.WithUserPolicyTool("extension.batch.apply", PermissionDecision.Ask)
                    .WithUserPolicyTool("filesystem.write", PermissionDecision.Ask);
            if (automatic && retryBlockedProposal)
                permissions.WithUserPolicyTool("filesystem.write", PermissionDecision.Deny);
            var runtime = new ToolRuntime(catalog, permissions, payload => { events.Add(payload); return VoidBox.Instance; },
                boundary, executableResolver: null, additionalBoundaries: [extensionTool.Boundary]);
            var runId = RunId.New().ToString();
            var turnId = TurnId.New().ToString();
            var expectedRunId = runId;
            var readRegistry = boundary.ReadRegistry();
            if (tombstoneHashForRead is not null)
                readRegistry.RecordRead("m9-extension-marker.txt", tombstoneHashForRead);
            var context = new ToolExecutionContext(workspace, readRegistry, emitEvent: events.Add,
                artifacts: artifacts, workspaceWriteLeases: HostWorkspaceWriteLeases.Shared,
                mutationHistory: history, mutationRunId: runId, mutationTurnId: turnId,
                mutationLaneId: LaneId.New().ToString());
            var originalCall = new ValidatedToolCall(ToolCallId.New(), extensionTool.Descriptor.Id,
                "model-extension-call", "{\"name\":\"mutate\",\"arguments\":{}}");
            if (automatic)
            {
                var store = new InMemoryEventStore();
                var codecs = EventCodecs.Create();
                var session = SessionId.New();
                var run = TestRun.Open(store, session);
                expectedRunId = run.RunId.ToString();
                var requests = new List<ModelRequest>();
                var providerCalls = 0;
                ToolCallId? explicitApplyCallId = null;
                string? retainedProposalId = null;
                string? retainedArtifactHash = null;
                long? retainedArtifactSize = null;
                var providerCallId = originalCall.ToolCallId;
                var turn = new ExplorerTurn((request, _) =>
                {
                    requests.Add(request);
                    var providerCall = providerCalls++;
                    if (providerCall == 0)
                        return new ModelResponse([new ToolCallBlock(providerCallId, originalCall.ProviderCallId,
                            originalCall.ToolId.ToString(), originalCall.NormalizedArgumentsJson)], StopReason.ToolUse,
                            new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                    if (retryBlockedProposal && providerCall == 1)
                    {
                        var retainedResult = Assert.Single(request.Messages.SelectMany(message => message.Content)
                            .OfType<ToolResultBlock>(), result => result.Id == originalCall.ToolCallId);
                        var retainedJson = Assert.Single(retainedResult.Content.OfType<TextBlock>()).Text;
                        using var retained = System.Text.Json.JsonDocument.Parse(retainedJson);
                        var retainedRoot = retained.RootElement;
                        Assert.Equal("blocked", retainedRoot.GetProperty("publication").GetString());
                        retainedProposalId = retainedRoot.GetProperty("proposalId").GetString();
                        retainedArtifactHash = retainedRoot.GetProperty("artifactHash").GetString();
                        retainedArtifactSize = retainedRoot.GetProperty("artifactSize").GetInt64();
                        var protectedArtifact = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                            .OfType<ToolCallSucceeded>(), succeeded => succeeded.ToolCallId == originalCall.ToolCallId)
                            .ResultArtifact;
                        Assert.NotNull(protectedArtifact);
                        Assert.Equal(retainedArtifactHash, protectedArtifact.Hash.Value);
                        Assert.Equal(retainedArtifactSize, protectedArtifact.Size);
                        Assert.True(artifacts.Verify(protectedArtifact.Hash, protectedArtifact.Size));
                        Assert.Equal(new[] { "publication", "proposalId", "artifactHash", "artifactSize", "files" },
                            retainedRoot.EnumerateObject().Select(property => property.Name));

                        // Resolve only the user permission that blocked publication. The read
                        // registry already contains the tombstone preimage, so the same CAS proposal
                        // can now be applied without invoking the extension again.
                        permissions.WithUserPolicyTool("filesystem.write", PermissionDecision.Allow);
                        explicitApplyCallId = ToolCallId.New();
                        var applyArgs = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            proposalId = retainedProposalId,
                            artifactHash = retainedArtifactHash,
                            artifactSize = retainedArtifactSize,
                        });
                        return new ModelResponse([new ToolCallBlock(explicitApplyCallId!,
                            "fixture-explicit-batch-apply", "extension.batch.apply", applyArgs)], StopReason.ToolUse,
                            new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                    }
                    if (retryBlockedProposal && providerCall == 2)
                    {
                        var appliedResult = Assert.Single(request.Messages.SelectMany(message => message.Content)
                            .OfType<ToolResultBlock>(), result => result.Id == explicitApplyCallId);
                        Assert.Contains("Extension batch applied.",
                            Assert.Single(appliedResult.Content.OfType<TextBlock>()).Text, StringComparison.Ordinal);
                    }
                    return new ModelResponse([new TextBlock("published")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                }, new ScriptedToolExecutor(catalog, permissions, workspace, boundary, null, null, false,
                        artifacts: artifacts, workspaceWriteLeases: HostWorkspaceWriteLeases.Shared,
                        additionalBoundaries: [extensionTool.Boundary], mutationHistory: history),
                    catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                    new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                    new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                    store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), boundary: boundary);
                var turnResult = turn.Ask("run extension", "system", session, run.RunId,
                    run.RootLane, "", TestContext.Current.CancellationToken);
                Assert.True(turnResult.StopReason == StopReason.EndTurn,
                    "turn=" + turnResult.StopReason + " failure=" + turnResult.FailureDiagnostic);
                var allEvents = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
                Assert.Empty(allEvents.OfType<PermissionRequested>());
                var child = Assert.Single(allEvents.OfType<ToolCallRequested>(), request => request.ParentToolCallId == originalCall.ToolCallId);
                Assert.Equal("extension.batch.apply", child.ToolName);
                var nextRequest = requests[^1];
                var visibleResult = Assert.Single(nextRequest.Messages.SelectMany(message => message.Content)
                    .OfType<ToolResultBlock>(), result => result.Id == originalCall.ToolCallId);
                var resultJson = Assert.Single(visibleResult.Content.OfType<TextBlock>()).Text;
                using var status = System.Text.Json.JsonDocument.Parse(resultJson);
                Assert.True(status.RootElement.GetProperty("publication").GetString() == (retryBlockedProposal ? "blocked" : "applied"),
                    "result=" + resultJson + " permissions=" + string.Join(",", allEvents.OfType<PermissionEvaluated>().Select(evt => evt.Decision))
                    + " rejected=" + string.Join("|", allEvents.OfType<ToolCallRejected>().Select(evt => evt.Reason))
                    + " denied=" + string.Join("|", allEvents.OfType<PermissionDenied>().Select(evt => evt.Cause))
                    + " failed=" + string.Join("|", allEvents.OfType<ToolCallFailed>().Select(evt => evt.Cause)));
                Assert.Equal("m9-extension-marker.txt", status.RootElement.GetProperty("files")[0].GetString());
                Assert.Equal("extension-applied", File.ReadAllText(Path.Combine(workspace, "m9-extension-marker.txt")));
                Assert.Equal(2, allEvents.OfType<ToolCallStarted>().Count());
                Assert.Single(allEvents.OfType<ToolCallStarted>(), started =>
                    started.Attribution?.ToolId == extensionTool.Descriptor.Id.ToString());
                Assert.DoesNotContain(nextRequest.Messages.SelectMany(message => message.Content)
                    .OfType<ToolCallBlock>(), call => call.Id == child.ToolCallId);
                Assert.DoesNotContain(nextRequest.Messages.SelectMany(message => message.Content)
                    .OfType<ToolResultBlock>(), result => result.Id == child.ToolCallId);
            }
            else
            {
                var staged = runtime.Run(originalCall, new ToolPreparationContext(workspace, DateTimeOffset.UtcNow),
                    context, userApprovesAsk: true, TestContext.Current.CancellationToken);
                Assert.True(staged.Succeeded, "extension-stage-failed:" + staged.Summary);
                using var preview = System.Text.Json.JsonDocument.Parse(staged.Preview!);
                var previewRoot = preview.RootElement;
                Assert.Equal("m9-extension-marker.txt", previewRoot.GetProperty("files")[0].GetString());
                Assert.False(previewRoot.TryGetProperty("afterBytes", out _));
                Assert.False(File.Exists(Path.Combine(workspace, "m9-extension-marker.txt")),
                    "Strong staging must not publish to the checkout.");
                Assert.False(previewRoot.TryGetProperty("publication", out _));
                Assert.False(File.Exists(Path.Combine(workspace, "m9-extension-marker.txt")));
                var applyArgs = System.Text.Json.JsonSerializer.Serialize(new
                {
                    proposalId = previewRoot.GetProperty("proposalId").GetString(),
                    artifactHash = previewRoot.GetProperty("artifactHash").GetString(),
                    artifactSize = previewRoot.GetProperty("artifactSize").GetInt64(),
                });
                var apply = new ValidatedToolCall(ToolCallId.New(), new ToolId("extension.batch.apply"),
                    "model-extension-batch-apply", applyArgs);
                if (blockApplySilently || staleBeforeApply || rejectByBudget)
                {
                    if (blockApplySilently)
                        permissions.WithProfile(PermissionProfile.Autonomous)
                            .WithUserPolicyTool("filesystem.write", applyDecision);
                    if (staleBeforeApply)
                        File.WriteAllText(Path.Combine(workspace, "m9-extension-marker.txt"), "concurrent editor");
                    var requestsBeforeBlock = events.OfType<InteractionRequested>().Count();
                    var startedBeforeBlock = events.OfType<ToolCallStarted>().Count();
                    var blocked = runtime.Run(apply, new ToolPreparationContext(workspace, DateTimeOffset.UtcNow),
                        context, userApprovesAsk: false, TestContext.Current.CancellationToken);
                    Assert.False(blocked.Succeeded);
                    if (staleBeforeApply)
                        Assert.Equal("concurrent editor", File.ReadAllText(Path.Combine(workspace, "m9-extension-marker.txt")));
                    else
                        Assert.False(File.Exists(Path.Combine(workspace, "m9-extension-marker.txt")));
                    Assert.Equal(requestsBeforeBlock, events.OfType<InteractionRequested>().Count());
                    Assert.Equal(startedBeforeBlock, events.OfType<ToolCallStarted>().Count());
                    return;
                }
                var published = runtime.Run(apply, new ToolPreparationContext(workspace, DateTimeOffset.UtcNow),
                    context, userApprovesAsk: true, TestContext.Current.CancellationToken);
                Assert.True(published.Succeeded, "batch-apply-failed:" + published.Summary);
                Assert.Equal("extension-applied", File.ReadAllText(Path.Combine(workspace, "m9-extension-marker.txt")));
                Assert.Equal(2, events.OfType<InteractionRequested>().Count());
            }
            if (seedTombstone)
            {
                var current = Assert.IsType<WorkspaceFileHistoryEntry>(history.ReadFile(workspace, "m9-extension-marker.txt"));
                Assert.Equal(WorkspaceFileHistoryState.Present, current.State);
                var charge = Assert.Single(history.ReadBudgetChargesForRun(workspace, expectedRunId));
                Assert.Equal("ReplaceFile", charge.Operation);
            }
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Strong_extension_writer_runs_in_private_copy_and_captures_ignored_output()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("AppContainer integration tests run only on Windows."); return; }
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable) { Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason); return; }

        var root = Path.Combine(Path.GetTempPath(), "OmniExtensionStaged" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(workspace); Directory.CreateDirectory(package);
        try
        {
            File.WriteAllText(Path.Combine(workspace, ".gitignore"), "m9-extension-marker.txt\n");
            File.WriteAllText(Path.Combine(workspace, "tracked.txt"), "base\n");
            File.WriteAllText(Path.Combine(workspace, "m9-extension-marker.txt"), "original\n");
            RunGit(workspace, "init"); RunGit(workspace, "config", "user.name", "Test");
            RunGit(workspace, "config", "user.email", "test@localhost");
            RunGit(workspace, "add", ".gitignore", "tracked.txt"); RunGit(workspace, "commit", "-m", "base");
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.*")
                .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "OmniCore.*.dll")))
                File.Copy(file, Path.Combine(package, Path.GetFileName(file)), overwrite: true);
            File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
                manifestVersion: 1
                id: fixture.staged
                version: 1.0.0
                displayName: Staged fixture
                publisher: tests
                omnicoreApi: '1.0'
                requests:
                  capabilities: [tool.register]
                  permissions:
                    process:
                      - executable: OmniCore.SandboxTestProcess.exe
                    paths:
                      write: [m9-extension-marker.txt]
                runtime:
                  kind: process
                  command: OmniCore.SandboxTestProcess.exe
                  args: [extension-rpc-write-marker, ., m9-extension-marker.txt]
                """);
            var extension = Assert.Single(new ExtensionManifestLoader().Discover(package, TrustLevel.Trusted,
                workspaceTrusted: true, consentedCapabilities: new HashSet<string>(["tool.register"], StringComparer.Ordinal)));
            var process = new OmniCore.Domain.ProcessClaim(Path.Combine(package, "OmniCore.SandboxTestProcess.exe"),
                ["extension-rpc-write-marker", ".", "m9-extension-marker.txt"], "ExtensionTool",
                WorkingDirectory: workspace, Isolation: OmniCore.Domain.ProcessIsolation.PrivateExtensionWorkspace);
            var invocation = await new ExtensionRpcProcessInvoker(new WindowsAppContainerProcessLauncher())
                .InvokeStagedAsync(extension, "tools/call", "{\"name\":\"mutate\"}",
                    new OmniCore.Domain.ResourceClaims([], ["m9-extension-marker.txt"], [], process, []), workspace,
                    TestContext.Current.CancellationToken);
            Assert.Contains("extension-applied", invocation.Response);
            var output = Assert.Single(invocation.Changes, change => change.RelativePath == "m9-extension-marker.txt");
            Assert.Null(output.BeforeSha256);
            Assert.Equal("extension-applied", System.Text.Encoding.UTF8.GetString(output.AfterBytes!));
            Assert.Equal("original\n", File.ReadAllText(Path.Combine(workspace, "m9-extension-marker.txt")));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Strong_read_only_v1_extension_receives_no_checkout_wide_write_access()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("AppContainer integration tests run only on Windows."); return; }
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable) { Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason); return; }

        var root = Path.Combine(Path.GetTempPath(), "OmniExtensionReadonly" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(workspace); Directory.CreateDirectory(package);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "marker.txt"), "original");
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.*")
                .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "OmniCore.*.dll")))
                File.Copy(file, Path.Combine(package, Path.GetFileName(file)), overwrite: true);
            File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), $$"""
                manifestVersion: 1
                id: fixture.readonly
                version: 1.0.0
                displayName: Read only fixture
                publisher: tests
                omnicoreApi: '1.0'
                requests:
                  capabilities: [tool.register]
                  permissions:
                    process:
                      - executable: OmniCore.SandboxTestProcess.exe
                runtime:
                  kind: process
                  command: OmniCore.SandboxTestProcess.exe
                  args: [extension-rpc-write-marker, '{{workspace}}', marker.txt]
                """);
            var extension = Assert.Single(new ExtensionManifestLoader().Discover(package, TrustLevel.Trusted,
                workspaceTrusted: true, consentedCapabilities: new HashSet<string>(["tool.register"], StringComparer.Ordinal)));
            var process = new OmniCore.Domain.ProcessClaim(Path.Combine(package, "OmniCore.SandboxTestProcess.exe"),
                ["extension-rpc-write-marker", workspace, "marker.txt"], "ExtensionTool", WorkingDirectory: workspace);
            InvalidDataException? denied = null;
            try
            {
                await new ExtensionRpcProcessInvoker(new WindowsAppContainerProcessLauncher())
                    .InvokeAsync(extension, "tools/call", "{\"name\":\"mutate\"}",
                        new OmniCore.Domain.ResourceClaims([], [], [], process, []), TestContext.Current.CancellationToken);
            }
            catch (InvalidDataException ex) { denied = ex; }
            Assert.NotNull(denied);
            Assert.Equal("original", File.ReadAllText(Path.Combine(workspace, "marker.txt")));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Extension_JSON_RPC_runs_as_a_real_Strong_stdio_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        var package = Path.Combine(AppContext.BaseDirectory, "extension-rpc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.*"))
            File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
        try
        {
            File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
                manifestVersion: 1
                id: fixture.rpc
                version: 1.0.0
                displayName: RPC fixture
                publisher: tests
                omnicoreApi: '1.0'
                requests:
                  capabilities: [tool.register]
                  permissions:
                    process:
                      - executable: OmniCore.SandboxTestProcess.exe
                provides:
                  tools:
                    - name: fixture
                      schema: schema.json
                runtime:
                  kind: process
                  command: OmniCore.SandboxTestProcess.exe
                  args: [extension-rpc]
                """);
            File.WriteAllText(Path.Combine(package, "schema.json"), "{\"type\":\"object\"}");
            var extension = Assert.Single(new ExtensionManifestLoader().Discover(package, TrustLevel.Trusted,
                workspaceTrusted: true, consentedCapabilities: new HashSet<string>(["tool.register"], StringComparer.Ordinal)));
            var invoker = new ExtensionRpcProcessInvoker(new WindowsAppContainerProcessLauncher());
            var process = new OmniCore.Domain.ProcessClaim(Path.Combine(package, "OmniCore.SandboxTestProcess.exe"),
                ["extension-rpc"], "ExtensionTool", WorkingDirectory: package);
            var response = await invoker.InvokeAsync(extension, "tools/call", "{\"name\":\"fixture\"}",
                new OmniCore.Domain.ResourceClaims([], [], [], process, []), TestContext.Current.CancellationToken);
            Assert.Contains("fixture-ok", response);
        }
        finally { TryDelete(package); }
    }

    [Fact]
    public void Process_runtime_writes_and_closes_one_shot_stdin_without_changing_null_input_callers()
    {
        var helper = HelperExecutable();
        var runtime = new SystemProcessRuntime();
        var handle = runtime.Launch(new ProcessLaunch(helper, ["stdin-echo"], AppContext.BaseDirectory,
            new Dictionary<string, string>(), captureOutput: true) { InitialStandardInput = "hello-from-host" },
            TestContext.Current.CancellationToken);
        var result = runtime.Wait(handle, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello-from-host", result.Stdout);

        var noInput = runtime.Launch(new ProcessLaunch(helper, ["claude-stream"], AppContext.BaseDirectory,
            new Dictionary<string, string>(), captureOutput: true), TestContext.Current.CancellationToken);
        Assert.Equal(0, runtime.Wait(noInput, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ExitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hello-from-host")]
    public async Task Strong_process_receives_a_valid_stdin_pipe_and_eof(string? input)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }
        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxInput-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, HelperExecutable(), ["stdin-state"],
                workspace, new Dictionary<string, string>(),
                [new SandboxAllowedPath(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)],
                new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(10), input);
            await using var process = await new WindowsAppContainerProcessLauncher()
                .StartAsync(launch, TestContext.Current.CancellationToken);
            var result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(input is null ? "PIPE:" : "PIPE:" + input + "\n", OutputText(result));
        }
        finally { TryDelete(workspace); }
    }

    [Fact]
    public async Task Strong_process_reads_allowed_workspace_but_not_outside_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }

        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxTest-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxOutside-" + Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(workspace);
        const string insideSecret = "inside-readable";
        const string outsideSecret = "outside-denied";
        var inside = Path.Combine(workspace, "inside.txt");
        await File.WriteAllTextAsync(inside, insideSecret, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(outside, outsideSecret, TestContext.Current.CancellationToken);
        try
        {
            var helper = HelperExecutable();
            var launcher = new WindowsAppContainerProcessLauncher();
            var insideResult = await RunReadAsync(launcher, helper, workspace, inside);
            Assert.Equal(0, insideResult.ExitCode);
            Assert.Contains(insideSecret, OutputText(insideResult));

            var outsideResult = await RunReadAsync(launcher, helper, workspace, outside);
            Assert.Equal(23, outsideResult.ExitCode);
            Assert.Contains("DENIED", OutputText(outsideResult));
        }
        finally
        {
            TryDelete(workspace);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task Network_is_denied_when_the_launch_spec_has_no_network_capability()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }

        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxNetworkTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, HelperExecutable(),
                new[] { "connect", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                workspace, new Dictionary<string, string>(), new[]
                {
                    new SandboxAllowedPath(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)
                }, new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()), TimeSpan.FromSeconds(5));
            await using var process = await new WindowsAppContainerProcessLauncher()
                .StartAsync(launch, TestContext.Current.CancellationToken);
            var result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(24, result.ExitCode);
            Assert.Contains("DENIED", OutputText(result));
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public async Task Timeout_terminates_the_entire_AppContainer_job_tree()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }

        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxJobTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var pidFile = Path.Combine(workspace, "child.pid");
        try
        {
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, HelperExecutable(), new[] { "spawn", pidFile },
                workspace, new Dictionary<string, string>(), new[]
                {
                    new SandboxAllowedPath(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)
                }, new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()), TimeSpan.FromSeconds(2));
            await using var process = await new WindowsAppContainerProcessLauncher()
                .StartAsync(launch, TestContext.Current.CancellationToken);
            var result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(result.TimedOut, "The configured timeout should have killed the process.");
            Assert.True(File.Exists(pidFile), "The parent should have written its child process id before timeout.");
            var childPid = int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken));
            Assert.True(WaitUntilExited(childPid), "The Job Object must terminate descendants, not only the parent.");
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public async Task AppContainer_waits_for_descendants_to_release_files_before_returning()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer integration tests run only on Windows.");
            return;
        }

        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreSandboxJobReleaseTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var heldFile = Path.Combine(workspace, "held.txt");
        var readyFile = Path.Combine(workspace, "ready.txt");
        var parentPidFile = Path.Combine(workspace, "parent.pid");
        try
        {
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, HelperExecutable(),
                ["spawn-file-holder", heldFile, readyFile, parentPidFile], workspace, new Dictionary<string, string>(),
                [new SandboxAllowedPath(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)],
                new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(10));
            await using var process = await new WindowsAppContainerProcessLauncher()
                .StartAsync(launch, TestContext.Current.CancellationToken);

            Assert.True(SpinWait.SpinUntil(() => File.Exists(parentPidFile), TimeSpan.FromSeconds(10)), "The child should lock the file and the root should publish its pid before exiting.");
            var parentPid = int.Parse(await File.ReadAllTextAsync(parentPidFile, TestContext.Current.CancellationToken));
            Assert.True(WaitUntilExited(parentPid), "The root process should exit while its child still holds the file.");
            Assert.Equal("locked", await File.ReadAllTextAsync(readyFile, TestContext.Current.CancellationToken));
            await process.TerminateAsync(TestContext.Current.CancellationToken);
            await process.DisposeAsync();
            // TerminateAsync completion means every process in the Job Object has exited.
            // The child held this file without FILE_SHARE_DELETE, so immediate deletion proves
            // teardown waited for descendant process handles, not only the root process handle.
            File.Delete(heldFile);
            Assert.False(File.Exists(heldFile));
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void AppContainer_profile_name_is_stable_per_workspace_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("AppContainer profile naming is Windows-specific.");
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "OmniCoreProfileName");
        Assert.Equal(WindowsAppContainerProcessLauncher.GetProfileName(workspace),
            WindowsAppContainerProcessLauncher.GetProfileName(Path.Combine(workspace, ".")));
        Assert.NotEqual(WindowsAppContainerProcessLauncher.GetProfileName(workspace),
            WindowsAppContainerProcessLauncher.GetProfileName(workspace + "-other"));
    }

    [Fact]
    public async Task Strong_request_selects_the_available_launcher_without_starting_the_weak_runtime()
    {
        var runtime = new FakeProcessRuntime();
        var strong = new FakeSandboxLauncher();
        var selector = new PlatformSandboxProcessLauncher(runtime,
            new FixedCapabilitiesProbe(new SandboxCapabilities(true, true, true, null)), strong);
        var result = await selector.StartAsync(Spec(SandboxStrength.Strong), CancellationToken.None);
        Assert.Equal(1, strong.Starts);
        Assert.Equal(0, runtime.Starts);
        await result.DisposeAsync();
    }

    [Fact]
    public async Task Strong_request_is_not_silently_downgraded_when_unavailable()
    {
        var runtime = new FakeProcessRuntime();
        var selector = new PlatformSandboxProcessLauncher(runtime,
            new FixedCapabilitiesProbe(new SandboxCapabilities(false, true, true, "unavailable")),
            new FakeSandboxLauncher());
        await Assert.ThrowsAsync<StrongSandboxUnavailableBeforeLaunchException>(async () =>
            await selector.StartAsync(Spec(SandboxStrength.Strong), CancellationToken.None));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task Weak_request_uses_the_runtime_launcher()
    {
        var runtime = new FakeProcessRuntime();
        var selector = new PlatformSandboxProcessLauncher(runtime,
            new FixedCapabilitiesProbe(new SandboxCapabilities(false, true, true, "unavailable")),
            new FakeSandboxLauncher());
        var process = await selector.StartAsync(Spec(SandboxStrength.Weak), CancellationToken.None);
        Assert.Equal(1, runtime.Starts);
        await process.DisposeAsync();
    }

    [Fact]
    public void Probe_reports_unavailability_with_a_reason_or_availability()
    {
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(capabilities.StrongAvailable);
            Assert.False(string.IsNullOrWhiteSpace(capabilities.StrongUnavailableReason));
            return;
        }

        if (!capabilities.StrongAvailable)
            Assert.False(string.IsNullOrWhiteSpace(capabilities.StrongUnavailableReason));
        else
            Assert.Null(capabilities.StrongUnavailableReason);
    }

    private static SandboxLaunchSpec Spec(SandboxStrength strength) => new(strength, "test", Array.Empty<string>(),
        Environment.CurrentDirectory, new Dictionary<string, string>(), Array.Empty<SandboxAllowedPath>(),
        new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()));

    private static async Task<SandboxProcessOutput> RunReadAsync(ISandboxProcessLauncher launcher,
        string helper, string workspace, string path)
    {
        var launch = new SandboxLaunchSpec(SandboxStrength.Strong, helper, new[] { "read", path }, workspace,
            new Dictionary<string, string>(), new[]
            {
                new SandboxAllowedPath(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)
            }, new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()), TimeSpan.FromSeconds(10));
        await using var process = await launcher.StartAsync(launch, TestContext.Current.CancellationToken);
        return await process.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    private static string HelperExecutable() => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
        ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");

    private static void RunGit(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }

    private static string OutputText(SandboxProcessOutput output) => string.Concat(output.Chunks.Select(chunk => chunk.Text));

    private static bool WaitUntilExited(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            Thread.Sleep(50);
        }

        return false;
    }

    private sealed class FixedCapabilitiesProbe(SandboxCapabilities value) : ISandboxCapabilitiesProbe
    {
        public SandboxCapabilities Probe() => value;
    }

    private sealed class FakeSandboxLauncher : ISandboxProcessLauncher
    {
        public int Starts { get; private set; }
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
        {
            Starts++;
            return ValueTask.FromResult<ISandboxProcessControl>(new FakeSandboxProcess());
        }
    }

    private sealed class FakeSandboxProcess : ISandboxProcessControl
    {
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SandboxProcessOutput(0, Array.Empty<SandboxOutputChunk>()));
        public ValueTask TerminateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProcessRuntime : IProcessRuntime
    {
        public int Starts { get; private set; }
        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            Starts++;
            return new ProcessHandle(1, this);
        }
        public void CancelTree(ProcessHandle handle) { }
        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(0, null, null, false);
        public bool IsAlive(ProcessHandle handle) => true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
