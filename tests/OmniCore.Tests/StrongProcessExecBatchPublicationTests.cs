using System.Diagnostics;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class StrongProcessExecBatchPublicationTests
{
    [Fact]
    public async Task Strong_process_exec_publishes_one_private_batch_with_durable_file_history()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Strong process writer integration test requires Windows AppContainer.");
            return;
        }
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer is unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "OmniStrongProcessBatch" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "runtime");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        try
        {
            CopyHelper(workspace);
            File.WriteAllText(Path.Combine(workspace, "tracked.txt"), "baseline\n");
            RunGit(workspace, "init");
            RunGit(workspace, "config", "user.name", "OmniCore test");
            RunGit(workspace, "config", "user.email", "omnicore-test@localhost");
            RunGit(workspace, "add", "tracked.txt", "OmniCore.SandboxTestProcess.exe",
                "OmniCore.SandboxTestProcess.dll", "OmniCore.SandboxTestProcess.deps.json",
                "OmniCore.SandboxTestProcess.runtimeconfig.json", "OmniCore.Infrastructure.dll",
                "OmniCore.Abstractions.dll", "OmniCore.Domain.dll");
            RunGit(workspace, "commit", "-m", "fixture baseline");

            var artifacts = new FileArtifactStore(data);
            using var history = new SqliteWorkspaceMutationHistoryStore(data);
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var runId = run.RunId.ToString();
            var laneId = run.RootLane.ToString();
            var turnId = TurnId.New().ToString();
            var manager = new PersistentProcessSessionManager(session, store, codecs,
                Guid.NewGuid().ToString("N"), workspace, artifacts, () => true);
            var coordinator = new WorktreeIntegrationHostCoordinator(workspace, data, artifacts,
                new GitWorktreeStore(SystemProcessRuntime.Instance()));
            var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(),
                includeSimulationTools: false, includeMutationTools: true, includeProcessTools: true,
                processSandboxStrength: SandboxStrength.Strong, includeShellTool: true,
                worktreeIntegrationCoordinator: coordinator, processSessionManager: manager);
            var catalog = hostTools.Catalog();
            var key = ModelPolicyKey.For("process-exec-fixture", "process-exec-fixture");
            var fullAgent = ModelPolicyPresets.FullAgent();
            var effective = EffectiveModelPolicy.Resolve(key,
                new StoredModelPolicy(key, 1, fullAgent, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 16, GuidanceLevel.Full, 3,
                    PlanControl.Assisted, 8));
            var boundary = OmniCliRuntime.CreateBoundary(effective, workspace, catalog);
            var events = new List<DomainEventPayload>();
            var permissions = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>())
                .WithModeDefaults(RunMode.Act).WithProfile(PermissionProfile.Balanced)
                .WithUserPolicyTool("process.exec", PermissionDecision.Allow)
                .WithUserPolicyTool("extension.batch.apply", PermissionDecision.Allow)
                .WithUserPolicyTool("filesystem.write", PermissionDecision.Allow);
            var runtime = new ToolRuntime(catalog, permissions,
                payload => { events.Add(payload); return VoidBox.Instance; }, boundary);
            var context = new ToolExecutionContext(workspace, boundary.ReadRegistry(), emitEvent: events.Add,
                artifacts: artifacts, workspaceWriteLeases: HostWorkspaceWriteLeases.Shared,
                mutationHistory: history, mutationRunId: runId, mutationTurnId: turnId,
                mutationLaneId: laneId);
            var readPaths = new[]
            {
                "OmniCore.SandboxTestProcess.exe", "OmniCore.SandboxTestProcess.dll",
                "OmniCore.SandboxTestProcess.deps.json", "OmniCore.SandboxTestProcess.runtimeconfig.json",
                "OmniCore.Infrastructure.dll", "OmniCore.Abstractions.dll", "OmniCore.Domain.dll",
            };
            var stageCall = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"),
                "model-private-process-writer", JsonSerializer.Serialize(new
                {
                    executable = "./OmniCore.SandboxTestProcess.exe",
                    argv = new[] { "private-process-exec", "m9-process-marker.txt" },
                    cwd = workspace,
                    timeoutSeconds = 45,
                    readPaths,
                    writePaths = new[] { "m9-process-marker.txt" },
                }));
            var preparationContext = new ToolPreparationContext(workspace, DateTimeOffset.UtcNow);
            var staged = runtime.Run(stageCall, preparationContext, context,
                userApprovesAsk: false, TestContext.Current.CancellationToken);
            Assert.True(staged.Succeeded, "process.exec did not produce a protected proposal: " + staged.Summary);
            Assert.NotNull(staged.Artifact);
            Assert.NotNull(staged.Preview);
            using var preview = JsonDocument.Parse(staged.Preview!);
            var proposalId = preview.RootElement.GetProperty("proposalId").GetString();
            var artifactHash = preview.RootElement.GetProperty("artifactHash").GetString();
            var artifactSize = preview.RootElement.GetProperty("artifactSize").GetInt64();
            Assert.False(File.Exists(Path.Combine(workspace, "m9-process-marker.txt")),
                "The process must write only to its private copy before the explicit batch apply.");

            var applyCall = new ValidatedToolCall(ToolCallId.New(), new ToolId("extension.batch.apply"),
                "model-apply-private-process-batch", JsonSerializer.Serialize(new
                { proposalId, artifactHash, artifactSize }));
            var applied = runtime.Run(applyCall, preparationContext, context,
                userApprovesAsk: false, TestContext.Current.CancellationToken);
            Assert.True(applied.Succeeded, "The process batch was not applied: " + applied.Summary);
            Assert.Equal("process-exec-batch-published", File.ReadAllText(Path.Combine(workspace, "m9-process-marker.txt")));
            var mutation = history.ReadFile(workspace, "m9-process-marker.txt");
            Assert.NotNull(mutation);
            Assert.Equal(WorkspaceFileHistoryState.Present, mutation.State);
            Assert.NotNull(mutation.ExpectedHash);
            Assert.Equal(WorkspaceMutationReceiptState.Applied,
                history.ReadReceipt(workspace, applyCall.ToolCallId, "m9-process-marker.txt")?.State);
            Assert.False(manager.HasUnresolvedForRun(runId), "A published process proposal must be released after durable apply.");
            var startedCalls = events.OfType<ToolCallStarted>().ToArray();
            Assert.Equal(2, startedCalls.Length);
            Assert.Contains(startedCalls, started => started.Attribution?.ToolId == "process.exec");
            Assert.Contains(startedCalls, started => started.Attribution?.ToolId == "extension.batch.apply");
            Assert.Equal(2, events.OfType<ToolCallSucceeded>().Count());
            Assert.Empty(events.OfType<PermissionRequested>());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void CopyHelper(string workspace)
    {
        foreach (var name in new[]
        {
            "OmniCore.SandboxTestProcess.exe", "OmniCore.SandboxTestProcess.dll",
            "OmniCore.SandboxTestProcess.deps.json", "OmniCore.SandboxTestProcess.runtimeconfig.json",
            "OmniCore.Infrastructure.dll", "OmniCore.Abstractions.dll", "OmniCore.Domain.dll",
        })
        {
            var source = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(source)) throw new FileNotFoundException("Fresh process helper file is missing.", source);
            File.Copy(source, Path.Combine(workspace, name));
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git fixture command.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture command failed: "
            + string.Join(" ", arguments) + "; " + stdout + stderr);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
