using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Sandbox;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>Smoke tests through the real CLI dispatcher, persistent Host and provider HTTP adapter.</summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CliEndToEndTests
{
    [Fact]
    public async Task Tui_dispatches_managed_read_only_child_through_actual_HTTP_context_profile_and_result_boundaries()
    {
        await InIsolatedCli(async (workspace, config, _, provider) =>
        {
            const string profile = "0199a000-0000-7000-8000-000000000001";
            File.AppendAllText(Path.Combine(config, "models.yaml"), "    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n");
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), """
                defaultProfile: 0199a000-0000-7000-8000-000000000001
                agentProfiles:
                  reader:
                    id: 0199a000-0000-7000-8000-000000000001
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: []
                      process: []
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read]
                """);
            string? childRequest = null;
            static string WithUsage(string response) => response.Replace("data: [DONE]",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\ndata: [DONE]", StringComparison.Ordinal);
            provider.RespondWith((index, body) => {
                if (index == 0) return WithUsage(TextResponse("Contexto visible del principal"));
                childRequest ??= body;
                return WithUsage(index == 1 ? ToolCallResponse("denied-write", "filesystem.write", """{"path":"forbidden.txt","content":"no"}""")
                    : TextResponse("Resultado de lectura; no modifiqué archivos."));
            });
            var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            var diagnostics = new List<string>();
            Assert.Equal(0, await host.ExecuteAsync("CONTEXTO SELECCIONADO: acción española", diagnostics.Add, TestContext.Current.CancellationToken));
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
            var grant = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
                {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,"allowedModes":"plan,act,orq",
                "maxAgents":2,"maxDepth":1,"maxTurns":20,"maxToolCalls":20,"maxElapsedSeconds":3600,"maxSpendUsd":1}
                """), TestContext.Current.CancellationToken);
            Assert.Equal("ok", grant.Status);
            var root = Assert.Single(AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes);
            var request = new DelegationCreateRequest(profile, root.LastContextEventId!, "Inspeccionar sin escribir",
                root.SelectableContextItemIds!, 8192, 2, 4, 200000, 0m);
            var queue = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()), TestContext.Current.CancellationToken);
            Assert.True(queue.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted, queue.Error ?? queue.Outcome?.Reason);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, queue.Outcome?.Kind);
            var child = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes.Single(e => e.ParentTaskId is not null);
            diagnostics.Clear();
            Assert.Equal(0, await host.ExecuteDelegationAsync(child.DelegationId!, diagnostics.Add, TestContext.Current.CancellationToken));
            Assert.Equal(3, provider.RequestCount);
            Assert.Contains("CONTEXTO SELECCIONADO", childRequest!);
            Assert.DoesNotContain("filesystem.write\"", childRequest!);
            Assert.DoesNotContain("plan.propose", childRequest!);
            Assert.DoesNotContain("mode.propose", childRequest!);
            Assert.DoesNotContain("user.ask", childRequest!);
            Assert.False(File.Exists(Path.Combine(workspace, "forbidden.txt")));
            var events = ReadCurrentSessionEvents(workspace);
            var started = Assert.Single(events.OfType<AgentExecutionStarted>(), e => e.ParentExecutionId is not null);
            var produced = Assert.Single(events.OfType<AgentResultProduced>());
            Assert.Equal(started.ExecutionId, produced.ExecutionId);
            Assert.Single(events.OfType<DelegationReturned>());
            Assert.Empty(events.OfType<ResultDispositionRecorded>());
            Assert.DoesNotContain(events.OfType<TaskCompleted>(), e => e.TaskId.ToString() == child.TaskId);
            var snapshot = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
            var returned = snapshot.Lanes.Single(e => e.DelegationId == child.DelegationId);
            Assert.Equal(produced.ResultRef.Id.ToString(), returned.ResultId);
            Assert.Contains("Resultado de lectura", returned.ResultSummary!);
            Assert.Equal(1, returned.ResultIssueCount);
            Assert.Null(returned.ResultDisposition);
            var accept = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"delegation.disposition\",\"delegationId\":\""
                + child.DelegationId + "\",\"resultId\":\"" + returned.ResultId + "\",\"outcome\":\"Accepted\",\"reason\":\"Leído y aceptado\"}"), TestContext.Current.CancellationToken);
            Assert.Equal("error", accept.Status);
            var rework = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"delegation.disposition\",\"delegationId\":\""
                + child.DelegationId + "\",\"resultId\":\"" + returned.ResultId + "\",\"outcome\":\"ReworkRequested\",\"reason\":\"Tool scope lacked read-only compatibility\"}"), TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, rework.Outcome?.Kind);
            Assert.Equal("Blocked", AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes.Single(e => e.DelegationId == child.DelegationId).TaskState);
        });
    }

    [Fact]
    public async Task Runtime_runs_two_delegations_on_one_host_and_defers_root_writer_behind_live_readers()
    {
        await InIsolatedCli(async (workspace, config, _, provider) =>
        {
            const string profile = "0199a000-0000-7000-8000-000000000021";
            File.AppendAllText(Path.Combine(config, "models.yaml"),
                "    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n");
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), """
                defaultProfile: 0199a000-0000-7000-8000-000000000021
                agentProfiles:
                  reader:
                    id: 0199a000-0000-7000-8000-000000000021
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: []
                      process: []
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read]
                """);

            var readersEntered = new CountdownEvent(2);
            using var releaseFirstReader = new ManualResetEventSlim(false);
            using var releaseSecondReader = new ManualResetEventSlim(false);
            var bothReadersAtProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReaderAtProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var principalReachedProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            static string WithUsage(string response) => response.Replace("data: [DONE]",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\ndata: [DONE]", StringComparison.Ordinal);
            provider.RespondWith((index, _) =>
            {
                if (index == 0) return WithUsage(TextResponse("stable root context"));
                if (index > 2)
                {
                    principalReachedProvider.TrySetResult(true);
                    return WithUsage(TextResponse("root writer reached provider"));
                }
                readersEntered.Signal();
                if (index == 1) firstReaderAtProvider.TrySetResult(true);
                if (readersEntered.CurrentCount == 0) bothReadersAtProvider.TrySetResult(true);
                var release = index == 1 ? releaseFirstReader : releaseSecondReader;
                if (!release.Wait(TimeSpan.FromSeconds(25)))
                    return WithUsage(TextResponse("reader fixture timeout"));
                return WithUsage(TextResponse("reader completed " + index));
            });

            var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            var diagnostics = new List<string>();
            Assert.Equal(0, await host.ExecuteAsync("ROOT CONTEXT: stable", diagnostics.Add,
                TestContext.Current.CancellationToken));
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
            Assert.Equal("ok", trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
                {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,
                "allowedModes":"plan,act,orq","maxAgents":3,"maxDepth":1,"maxTurns":20,
                "maxToolCalls":20,"maxElapsedSeconds":3600,"maxSpendUsd":1}
                """), TestContext.Current.CancellationToken).Status);
            var before = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
            var rootBefore = Assert.Single(before.Lanes);
            var rootRun = before.RunId ?? throw new InvalidDataException("Root Run was not projected.");
            var rootContext = rootBefore.LastContextEventId
                ?? throw new InvalidDataException("Root context was not available for reader admission.");
            var selectable = rootBefore.SelectableContextItemIds
                ?? throw new InvalidDataException("Root selected context was not available.");

            string CreateReader(string objective)
            {
                var request = new DelegationCreateRequest(profile, rootContext, objective, selectable,
                    8192, 2, 4, 200_000, 0m);
                var ack = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()),
                    TestContext.Current.CancellationToken);
                Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
                return AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!
                    .Lanes.Single(item => item.ParentTaskId is not null && item.DelegationId is not null
                        && item.Objective == objective).DelegationId!;
            }

            var firstId = CreateReader("first live reader");
            var secondId = CreateReader("second live reader");
            using var firstCancellation = new CancellationTokenSource();
            using var secondCancellation = new CancellationTokenSource();
            var firstDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var secondDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            Task<int>? first = null;
            Task<int>? second = null;

            try
            {
                first = Task.Run(() => runtime.DelegationAsync(firstId, firstDiagnostics.Enqueue, firstCancellation.Token));
                await firstReaderAtProvider.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                second = Task.Run(() => runtime.DelegationAsync(secondId, secondDiagnostics.Enqueue, secondCancellation.Token));
                await bothReadersAtProvider.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                var live = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
                Assert.Equal(2, live.Capacity!.Active);
                Assert.Equal(3, live.Capacity.Maximum);
                Assert.False(live.Capacity.WriterActive);
                Assert.Equal(rootRun, live.RunId);
                Assert.Equal(rootContext, Assert.Single(live.Lanes, lane => lane.ParentTaskId is null).LastContextEventId);

                // The same Host refuses a principal writer without waiting or invoking its
                // callback while both production DelegationAsync readers own their leases.
                var server = Assert.IsType<OmniServer>(client);
                var rootWriterCalls = 0;
                var rootWrite = server.ExecuteExplorerTurn(server.LastSessionId()!, server.LastRunId()!,
                    _ =>
                    {
                        Interlocked.Increment(ref rootWriterCalls);
                        throw new InvalidOperationException("Root writer must remain deferred while readers live.");
                    }, CancellationToken.None, readOnlyLane: false);
                Assert.Equal("WaitingForCapacity", rootWrite.Ack.Outcome?.Reason);
                Assert.Equal(0, rootWriterCalls);
                Assert.False(principalReachedProvider.Task.IsCompleted);
                Assert.Equal(3, provider.RequestCount); // root context + exactly two overlapping child requests

                // Settle the sibling's known provider receipt before cancelling the first
                // invocation. Its later cancellation must not poison the already-settled Lane.
                releaseSecondReader.Set();
                var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                Assert.True(secondResult == 0, "Second reader failed before independent cancellation: "
                    + string.Join(" | ", secondDiagnostics));
                Assert.Equal(1, AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!
                    .Capacity!.Active);

                firstCancellation.Cancel();
                releaseFirstReader.Set();
                var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                Assert.NotEqual(0, firstResult);
                var afterReaders = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
                Assert.Contains(afterReaders.Lanes, item => item.DelegationId == firstId
                    && item.ExecutionState == "Started" && item.DelegationState == "Accepted"
                    && item.Heartbeat?.State == "Cancelled");
                Assert.False(principalReachedProvider.Task.IsCompleted);
                Assert.Equal(3, provider.RequestCount);
                var final = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
                Assert.Equal(rootRun, final.RunId);
                Assert.Equal(rootContext, Assert.Single(final.Lanes, lane => lane.ParentTaskId is null).LastContextEventId);
            }
            finally
            {
                firstCancellation.Cancel();
                secondCancellation.Cancel();
                releaseFirstReader.Set();
                releaseSecondReader.Set();
                var workers = new[] { first, second }.Where(task => task is not null).Cast<Task<int>>().ToArray();
                try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception) { }
            }
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Typed_workflow_runs_real_filesystem_and_verifier_tools_and_reworks_unconnected_module(
        bool connectModule, bool injectPostVerificationMutation)
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            const string profileId = "0199a000-0000-7000-8000-000000000011";
            // Keep the physical invocation envelope below the workflow's finite per-stage
            // reservation. This fixture does not increase Run/task budgets to accommodate the
            // default 8192+2048 model multiplied by the provider's retry bound.
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  scripted-model:
                    provider: scripted
                    context: 4096
                    maxOutput: 256
                    inputPricePerMillionUsd: 0
                    outputPricePerMillionUsd: 0
                    reasoning:
                      supported: true
                      effortLevels: [high, adaptive]
                      replayPolicy: RequiredWithTools
                """);
            File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
                providers:
                  scripted:
                    family: OpenAIResponses
                    profile: api
                    baseUrl: {{provider.BaseUrl}}
                    auth: none
                    billingMode: Local
                """);
            var executable = new SystemExecutableResolver().Resolve("dotnet", workspace).ResolvedPath;
            var projectDirectory = Path.Combine(workspace, "src", "FeatureApp");
            Directory.CreateDirectory(projectDirectory);
            const string originalProgram = "public static class Program { public static int Main() { /* FEATURE_MODULE_HOOK */ return FeatureModule.IsInitialized ? 0 : 1; } }\n";
            File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), originalProgram);
            File.WriteAllText(Path.Combine(projectDirectory, "FeatureApp.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <RestorePackagesPath>$(MSBuildThisFileDirectory).packages</RestorePackagesPath>
                    <NuGetAudit>false</NuGetAudit>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "NuGet.Config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration><packageSources><clear /></packageSources></configuration>
                """);
            var restore = new System.Diagnostics.ProcessStartInfo(executable)
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            restore.ArgumentList.Add("restore");
            restore.ArgumentList.Add(Path.Combine("src", "FeatureApp", "FeatureApp.csproj"));
            restore.ArgumentList.Add("--configfile");
            restore.ArgumentList.Add(Path.Combine("src", "FeatureApp", "NuGet.Config"));
            restore.ArgumentList.Add("--packages");
            restore.ArgumentList.Add(Path.Combine("src", "FeatureApp", ".packages"));
            restore.ArgumentList.Add("--nologo");
            restore.ArgumentList.Add("-p:NuGetAudit=false");
            using (var process = System.Diagnostics.Process.Start(restore)
                ?? throw new InvalidOperationException("Could not start dotnet restore for the temporary workflow project."))
            {
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.True(process.ExitCode == 0,
                    "Temporary workflow project restore failed: " + await process.StandardOutput.ReadToEndAsync()
                    + await process.StandardError.ReadToEndAsync());
            }
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), $$"""
                defaultProfile: {{profileId}}
                agentProfiles:
                  workflow:
                    id: {{profileId}}
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: ["**"]
                      process:
                        - executablePattern: '{{executable.Replace("\\", "/", StringComparison.Ordinal)}}'
                          argvPatterns: ["run", "--project", "src/FeatureApp/FeatureApp.csproj", "--configuration", "Release", "--no-restore"]
                          decision: Allow
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read, filesystem.write, filesystem.patch]
                """);

            using (var modelPolicies = OmniHost.CreateModelPolicyService(data))
                modelPolicies.Set(ModelPolicyKey.For("scripted", "scripted-model"), 0,
                    ModelPolicyPresets.FullAgent(), CancellationToken.None);

            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalProgram))).ToLowerInvariant();
            var implementation = "public static class FeatureModule { public static bool IsInitialized { get; private set; } public static void Initialize() => IsInitialized = true; }\n";
            var patchArguments = System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "src/FeatureApp/Program.cs",
                expectedVersion = version,
                oldText = "/* FEATURE_MODULE_HOOK */",
                newText = connectModule ? "FeatureModule.Initialize();" : "/* FEATURE_MODULE_HOOK */",
            });
            var requests = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
            provider.RespondWith((index, body) =>
            {
                requests[index] = body;
                return index switch
                {
                    0 => ResponsesTextResponse("principal workflow context"),
                    1 => ResponsesToolCallResponse("explore-entrypoint", "filesystem.read", "{\"path\":\"src/FeatureApp/Program.cs\"}"),
                    2 => ResponsesTextResponse("EXPLORE_RESULT_EVIDENCE: src/FeatureApp/Program.cs is the executable entry point and contains FEATURE_MODULE_HOOK."),
                    3 => ResponsesToolCallResponse("implement-read-entrypoint", "filesystem.read", "{\"path\":\"src/FeatureApp/Program.cs\"}"),
                    4 => ResponsesToolCallResponse("implement-module", "filesystem.write", System.Text.Json.JsonSerializer.Serialize(new
                    {
                        path = "src/FeatureApp/FeatureModule.cs", content = implementation,
                    })),
                    5 when connectModule => ResponsesToolCallResponse("implement-connect", "filesystem.patch", patchArguments),
                    5 => ResponsesToolCallResponse("implement-review", "filesystem.read", "{\"path\":\"src/FeatureApp/FeatureModule.cs\"}"),
                    6 when connectModule => ResponsesToolCallResponse("implement-review", "filesystem.read", "{\"path\":\"src/FeatureApp/Program.cs\"}"),
                    6 when !connectModule => ResponsesTextResponse("IMPLEMENT_RESULT_EVIDENCE: src/FeatureApp/FeatureModule.cs exists; Program.Main does not invoke FeatureModule.Initialize."),
                    7 when connectModule => ResponsesTextResponse("IMPLEMENT_RESULT_EVIDENCE: FeatureModule.Initialize is connected from the application entry point."),
                    7 when !connectModule => ResponsesToolCallResponse("verify-integration", "dyn.core.verify_integration",
                        VerifierArguments(executable, workspace)),
                    8 when connectModule => ResponsesToolCallResponse("verify-integration", "dyn.core.verify_integration",
                        VerifierArguments(executable, workspace)),
                    9 when connectModule => ResponsesTextResponse("VERIFY_RESULT_EVIDENCE: the declared integration check passed."),
                    _ => ResponsesTextResponse("VERIFY_RESULT_EVIDENCE: integration check completed."),
                };
            });

            var runtime = OmniCliRuntime.Create(workspace);
            runtime.ProcessSandboxStrengthForTests = SandboxStrength.Weak;
            var injectedMutation = 0;
            if (injectPostVerificationMutation)
            {
                runtime.BeforeWorkflowCompletionGateForTests = (server, authorization) =>
                {
                    if (Interlocked.Exchange(ref injectedMutation, 1) != 0) return;
                    var store = server.AcquireStore();
                    var codecs = server.AcquireCodecs();
                    var session = authorization.Session;
                    var journal = store.ReadFrom(session, 1);
                    var own = journal.Where(evt => evt.RunId == authorization.Run).ToArray();
                    var rootTask = Assert.Single(own.Select(codecs.Decode).OfType<TaskCreated>(),
                        task => task.ParentTaskId is null);
                    var rootLane = Assert.Single(own.Select(codecs.Decode).OfType<LaneCreated>(),
                        lane => lane.TaskId == rootTask.TaskId);
                    var rootExecution = Assert.Single(own.Select(codecs.Decode).OfType<AgentExecutionStarted>(),
                        execution => execution.LaneId == rootLane.LaneId);
                    var scope = new ExecutionScopeState(authorization.Run, rootTask.TaskId, rootLane.LaneId,
                        ExecutionId: rootExecution.ExecutionId);
                    new EventStream(store, codecs, session).AppendBatch(
                        [new ToolCallRequested(ToolCallId.New(), "late-sibling", "filesystem.patch", "{}")],
                        DurabilityClass.Barrier, [scope]);

                    var afterMutation = store.CurrentSequence(session);
                    Assert.Throws<InvalidOperationException>(() => server.CompleteWorkflowCompletionGate(authorization));
                    var afterFirstDebt = store.CurrentSequence(session);
                    Assert.True(afterFirstDebt > afterMutation, "The rejected completion gate must persist validation debt.");
                    Assert.Throws<InvalidOperationException>(() => server.CompleteWorkflowCompletionGate(authorization));
                    Assert.Equal(afterFirstDebt, store.CurrentSequence(session));
                };
            }
            var processEnvironmentRoot = Path.Combine(workspace, ".workflow-process-env");
            var testAppData = Path.Combine(processEnvironmentRoot, "roaming");
            var testLocalAppData = Path.Combine(processEnvironmentRoot, "local");
            Directory.CreateDirectory(testAppData);
            Directory.CreateDirectory(testLocalAppData);
            var previousAppData = Environment.GetEnvironmentVariable("APPDATA");
            var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            runtime.ProcessRuntimeFactoryForTests = () => new OmniCore.Execution.SystemProcessRuntime(
                new[] { "APPDATA", "LOCALAPPDATA" });
            runtime.UseConsoleInput = false;
            var previousRuntime = CliApp.UseRuntimeForTests(runtime);
            try
            {
                var host = new TuiTurnHost(runtime);
                var diagnostics = new List<string>();
                Assert.Equal(0, await host.ExecuteAsync("Start an authorized workflow session", diagnostics.Add,
                    TestContext.Current.CancellationToken));

                var client = runtime.Connect(TestContext.Current.CancellationToken);
                var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
                var authority = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
                    {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,
                     "allowedModes":"plan,act,orq","maxAgents":4,"maxDepth":3,"maxTurns":12,
                     "maxToolCalls":36,"maxElapsedSeconds":300,"maxSpendUsd":5}
                    """), TestContext.Current.CancellationToken);
                Assert.True(authority.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                    authority.Error ?? authority.Outcome?.Reason ?? "Run mode authority was not accepted.");

                var rootRun = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<RunCreated>());
                var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
                    ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspace)));
                var verifyArguments = VerifierArguments(executable, workspace);
                var verifyArgv = new[] { "run", "--project", "src/FeatureApp/FeatureApp.csproj",
                    "--configuration", "Release", "--no-restore" };
                var verifyIntent = new ToolIntent(ToolCallId.New(), new ToolId("dyn.core.verify_integration"),
                    verifyArguments, EffectClass.NonIdempotent,
                    new ResourceClaims(Array.Empty<string>(), new[] { workspace }, Array.Empty<NetworkGrant>(),
                    new ProcessClaim(executable, verifyArgv, "External", NetworkRequired: false,
                            WorkingDirectory: workspace), Array.Empty<string>()),
                    ToolRisk.High, null);
                var permissionPolicy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>())
                    .WithModeDefaults(RunMode.Orchestrate)
                    .WithGrantStore(OmniHost.CreatePermissionGrantStore(workspace), workspaceId, rootRun.RunId);
                _ = permissionPolicy.RecordApprovedGrant(verifyIntent, GrantLifetime.Run, CancellationToken.None);
                OmniHost.GetWeakSandboxConsentState(rootRun.RunId).GrantForRun();

                var command = new[]
                {
                    "/orq-auth", "connect FeatureModule to the application entry point",
                    "--verify-executable", executable,
                    "--verify-argv-json", "[\"run\", \"--project\", \"src/FeatureApp/FeatureApp.csproj\", "
                        + "\"--configuration\", \"Release\", \"--no-restore\"]",
                };
                (int Code, string Output) result;
                try
                {
                    Environment.SetEnvironmentVariable("APPDATA", testAppData);
                    Environment.SetEnvironmentVariable("LOCALAPPDATA", testLocalAppData);
                    result = await Run(command);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("APPDATA", previousAppData);
                    Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
                }
                var expectedCode = connectModule && !injectPostVerificationMutation ? 0 : 1;
                Assert.True(result.Code == expectedCode,
                    $"Workflow returned {result.Code}; output: {result.Output}\nDiagnostics: {string.Join(" | ", diagnostics)}"
                    + $"\nProvider requests={provider.RequestCount}; keys={string.Join(",", requests.Keys.Order())}"
                    + $"\nJournal tail={string.Join(" | ", ReadCurrentSessionEvents(workspace).TakeLast(12).Select(item => item.GetType().Name))}"
                    + $"\nTask failures={string.Join(" | ", ReadCurrentSessionEvents(workspace).OfType<TaskFailed>().Select(item => item.Cause))}"
                    + $"\nProgram={File.ReadAllText(Path.Combine(projectDirectory, "Program.cs"))}"
                    + $"\nModule={(File.Exists(Path.Combine(projectDirectory, "FeatureModule.cs")) ? File.ReadAllText(Path.Combine(projectDirectory, "FeatureModule.cs")) : "<missing>")}"
                    + $"\nFinal provider request={requests.OrderBy(pair => pair.Key).LastOrDefault().Value}"
                    + $"\nEvent details={string.Join(" | ", ReadCurrentSessionJournalEvents(workspace).TakeLast(18).Select(evt => evt.Sequence + ":" + EventCodecs.Create().Decode(evt)))}");
                var expectedOutput = connectModule && !injectPostVerificationMutation
                    ? "Workflow completed" : !connectModule ? "ReworkRequested" : "Workflow gate remains open";
                Assert.Contains(expectedOutput, result.Output,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(connectModule, File.ReadAllText(Path.Combine(projectDirectory, "Program.cs"))
                    .Contains("FeatureModule.Initialize", StringComparison.Ordinal));
                Assert.Equal(implementation, File.ReadAllText(Path.Combine(projectDirectory, "FeatureModule.cs")));
                Assert.Contains(requests.OrderBy(pair => pair.Key).Select(pair => pair.Value), body =>
                    body.Contains("EXPLORE_RESULT_EVIDENCE", StringComparison.Ordinal));
                Assert.Contains(requests.OrderBy(pair => pair.Key).Select(pair => pair.Value), body =>
                    body.Contains("IMPLEMENT_RESULT_EVIDENCE", StringComparison.Ordinal));

                var journal = ReadCurrentSessionJournalEvents(workspace).ToArray();
                var codecs = EventCodecs.Create();
                var events = journal.Select(codecs.Decode).ToArray();
                var plan = PlanProjection.Replay(codecs, journal);
                var stageItems = plan.Items().Where(item => item.Metadata.ContainsKey(WorkflowStageContract.WorkflowIdKey))
                    .OrderBy(item => item.Metadata[WorkflowStageContract.StageKey], StringComparer.Ordinal).ToArray();
                Assert.Equal(3, stageItems.Length);
                Assert.Equal(connectModule ? 3 : 2,
                    stageItems.Count(item => item.State == PlanItemState.Completed));
                Assert.Equal(connectModule ? PlanItemState.Completed : PlanItemState.Blocked, stageItems[^1].State);
                var completionGate = Assert.Single(plan.Items(), item =>
                    item.Metadata.ContainsKey("workflow.gate.workflow"));
                if (connectModule && !injectPostVerificationMutation) Assert.Equal(PlanItemState.Completed, completionGate.State);
                else Assert.NotEqual(PlanItemState.Completed, completionGate.State);
                var produced = events.OfType<AgentResultProduced>().ToArray();
                Assert.Equal(3, produced.Length);
                var dispositions = events.OfType<ResultDispositionRecorded>().ToArray();
                Assert.Equal(3, dispositions.Length);
                Assert.Equal(connectModule ? ResultDispositionOutcome.Accepted : ResultDispositionOutcome.ReworkRequested,
                    dispositions[^1].Disposition.Outcome);
                Assert.Contains(events.OfType<ToolCallRequested>(), item => item.ToolName == "filesystem.write");
                var verifier = Assert.Single(events.OfType<ToolCallRequested>(), item =>
                    item.ToolName == "dyn.core.verify_integration");
                Assert.Equal(connectModule,
                    events.OfType<ToolCallSucceeded>().Any(item => item.ToolCallId == verifier.ToolCallId));
                Assert.Equal(!connectModule,
                    events.OfType<ToolCallFailed>().Any(item => item.ToolCallId == verifier.ToolCallId));
                Assert.True(requests.Count >= (connectModule ? 10 : 9));
                if (connectModule && !injectPostVerificationMutation)
                {
                    var providerCalls = provider.RequestCount;
                    var modelSteps = events.OfType<ModelStepStarted>().Count();
                    var toolCalls = events.OfType<ToolCallRequested>().Count();
                    var retry = await Run(command);
                    Assert.Equal(0, retry.Code);
                    Assert.Contains("Workflow completed", retry.Output, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(providerCalls, provider.RequestCount);
                    var retryEvents = ReadCurrentSessionEvents(workspace);
                    Assert.Equal(modelSteps, retryEvents.OfType<ModelStepStarted>().Count());
                    Assert.Equal(toolCalls, retryEvents.OfType<ToolCallRequested>().Count());
                }
                if (injectPostVerificationMutation)
                {
                    var debts = events.OfType<ValidationDebtCreated>().Where(item =>
                        item.Debt.Scope.RunId == rootRun.RunId
                        && item.Debt.RequiredLevel == ValidationLevel.Integration).ToArray();
                    var debt = Assert.Single(debts);
                    Assert.Contains("stale", debt.Debt.MissingChecks.Single(), StringComparison.OrdinalIgnoreCase);
                    Assert.Single(events.OfType<ValidationStateRecorded>(), item =>
                        item.ExecutionId == debt.ExecutionId && item.State.Status == ValidationStatus.Failed
                        && item.State.Level == ValidationLevel.Integration);
                    var snapshot = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
                    var verifyLane = Assert.Single(snapshot.Lanes, lane => lane.WorkflowStage == "Verify");
                    Assert.Single(verifyLane.ValidationDebts!);
                    Assert.Contains("stale", verifyLane.ValidationDebts![0].MissingChecks.Single(),
                        StringComparison.OrdinalIgnoreCase);
                    Assert.False(verifyLane.IntegrationVerified);
                    Assert.False(verifyLane.IntegrationValidationPassed);
                    Assert.Equal(1, injectedMutation);
                    var providerCalls = provider.RequestCount;
                    var modelSteps = events.OfType<ModelStepStarted>().Count();
                    var toolCalls = events.OfType<ToolCallRequested>().Count();
                    var retry = await Run(command);
                    Assert.Equal(1, retry.Code);
                    Assert.Equal(providerCalls, provider.RequestCount);
                    var retryEvents = ReadCurrentSessionEvents(workspace);
                    Assert.Equal(modelSteps, retryEvents.OfType<ModelStepStarted>().Count());
                    Assert.Equal(toolCalls, retryEvents.OfType<ToolCallRequested>().Count());
                    Assert.Single(retryEvents.OfType<ValidationDebtCreated>());
                    Assert.Single(retryEvents.OfType<ValidationStateRecorded>(), item =>
                        item.State.Status == ValidationStatus.Failed
                        && item.State.Level == ValidationLevel.Integration);
                }
            }
            finally { CliApp.UseRuntimeForTests(previousRuntime); }
        });
    }

    private static string VerifierArguments(string executable, string workspace) => System.Text.Json.JsonSerializer.Serialize(new
    {
        executable,
        argv = new[] { "run", "--project", "src/FeatureApp/FeatureApp.csproj",
            "--configuration", "Release", "--no-restore" },
        cwd = workspace,
        timeoutSeconds = 120,
    });

    [Fact]
    public async Task Normal_PLAN_chat_can_recommend_ORQ_without_granting_mode_or_creating_workers()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            using (var preferences = new RunModePreferenceStore(OmniHost.CreatePlatformPaths().UserDatabasePath))
                preferences.Set(RunMode.Plan, 0);
            string? request = null;
            provider.RespondWith((index, body) =>
            {
                request ??= body;
                return index == 0 ? ToolCallResponse("recommend-mode", "mode.propose",
                    """{"mode":"orq","reason":"Independent review would help"}""")
                    : TextResponse("Recomiendo ORQ; seguimos en PLAN hasta que lo autorices.");
            });
            var output = new List<string>();
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            Assert.Equal(0, await host.ExecuteAsync("¿Conviene otra forma de trabajo?", output.Add,
                TestContext.Current.CancellationToken));
            Assert.Equal(2, provider.RequestCount);
            Assert.Contains("mode.propose", request!);
            var journal = ReadCurrentSessionJournalEvents(workspace);
            var codecs = EventCodecs.Create();
            var created = Assert.Single(journal.Select(codecs.Decode).OfType<RunCreated>());
            var proposed = Assert.Single(ModeProposalProjection.Replay(created.SessionId, created.RunId, codecs, journal));
            Assert.Equal(RunMode.Plan, proposed.From);
            Assert.Equal(RunMode.Orchestrate, proposed.To);
            var projection = RunProjection.Replay(created.SessionId, created.RunId, codecs, journal);
            Assert.Equal(RunMode.Plan, projection.Mode);
            Assert.False(projection.ModeAuthority!.AutoModeSwitch);
            Assert.DoesNotContain(journal.Select(codecs.Decode), e => e is RunModeChanged
                or RunModeTransitionAuthorized or DelegationCreated or DelegationAccepted);
            var rootExecutor = Assert.Single(journal.Select(codecs.Decode).OfType<AgentExecutionStarted>());
            Assert.Null(rootExecutor.ParentExecutionId);
            var rootLane = Assert.Single(journal.Select(codecs.Decode).OfType<LaneCreated>());
            Assert.Equal(created.RootTask, rootLane.TaskId);
            Assert.Equal(rootLane.LaneId, rootExecutor.LaneId);
            Assert.Equal(rootLane.AgentProfile, rootExecutor.ProfileId);
            Assert.All(journal.Where(e => codecs.Decode(e) is ModelStepStarted), e => Assert.Equal(rootExecutor.ExecutionId, e.ExecutionId));
            Assert.Single(journal.Select(codecs.Decode).OfType<TaskCreated>());
            Assert.Single(journal.Select(codecs.Decode).OfType<LaneCreated>());
        });
    }

    [Theory]
    [InlineData(RunMode.Plan, "PLAN")]
    [InlineData(RunMode.Act, "ACT")]
    [InlineData(RunMode.Orchestrate, "ORQ")]
    public async Task Direct_explanation_preserves_selected_mode_without_plan_tools_or_children(RunMode mode, string instructionMode)
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            using (var preferences = new RunModePreferenceStore(OmniHost.CreatePlatformPaths().UserDatabasePath))
                preferences.Set(mode, 0);
            string? submitted = null;
            provider.RespondWith((_, body) => { submitted = body; return TextResponse("La función suma sus dos argumentos."); });
            var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            var diagnostics = new List<string>();
            Assert.Equal(0, await host.ExecuteAsync("¿Qué hace esta función? int Sum(int a, int b) => a + b;",
                diagnostics.Add, TestContext.Current.CancellationToken));
            Assert.Equal(1, provider.RequestCount);
            Assert.Contains("You are in " + instructionMode + " mode", submitted!);
            // Read helpers open a new SQLite connection: assertions inspect durable state, not host cache.
            var events = ReadCurrentSessionEvents(workspace);
            var created = Assert.Single(events.OfType<RunCreated>());
            Assert.Equal(mode, created.Mode);
            Assert.Single(events.OfType<TaskCreated>());
            Assert.Single(events.OfType<LaneCreated>());
            Assert.Single(events.OfType<TurnStarted>());
            Assert.Single(events.OfType<TurnCompleted>());
            Assert.DoesNotContain(events, item => item is ToolCallRequested or ToolCallStarted
                or RunModeChanged or RunModeTransitionAuthorized or DelegationCreated or DelegationAccepted);
            var rootExecutor = Assert.Single(events.OfType<AgentExecutionStarted>());
            Assert.Null(rootExecutor.ParentExecutionId);
            var rootLane = Assert.Single(events.OfType<LaneCreated>());
            Assert.Equal(created.RootTask, rootLane.TaskId);
            Assert.Equal(rootLane.LaneId, rootExecutor.LaneId);
            Assert.Equal(rootLane.AgentProfile, rootExecutor.ProfileId);
            Assert.DoesNotContain(events.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.PlanApproval);
            Assert.Empty(Directory.GetFiles(workspace, "*", SearchOption.AllDirectories));
            var journal = ReadCurrentSessionJournalEvents(workspace);
            var replay = RunProjection.Replay(created.SessionId, created.RunId, EventCodecs.Create(), journal);
            Assert.Equal(mode, replay.Mode);
            Assert.False(replay.ModeAuthority!.AutoModeSwitch);
        });
    }

    [Fact]
    public async Task Tui_reasoning_budget_boost_requires_an_explicit_nontrivial_budget()
    {
        await InIsolatedCli((workspace, _, _, _) =>
        {
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            Assert.Throws<ArgumentException>(() => host.SetNextTurnReasoningBoost("budget", null));
            Assert.Throws<ArgumentException>(() => host.SetNextTurnReasoningBoost("budget", 1023));
            Assert.True(host.SetNextTurnReasoningBoost("budget", 1024));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task User_profile_is_applied_by_normal_CLI_and_changed_content_blocks_followup_before_HTTP()
    {
        await InIsolatedCli(async (workspace, config, _, provider) =>
        {
            // This is a loopback HTTP adapter fixture, not an authenticated provider call.
            var profileFile = Path.Combine(config, "agent-profiles.yaml");
            const string yaml = """
                defaultProfile: 0199a000-0000-7000-8000-000000000001
                agentProfiles:
                  explorer:
                    id: 0199a000-0000-7000-8000-000000000001
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: []
                      process: []
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read]
                """;
            File.WriteAllText(profileFile, yaml);
            provider.RespondWith((_, _) => TextResponse("profile fixture"));
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            var output = new List<string>();
            Assert.Equal(0, await host.ExecuteAsync("inspect fixture", output.Add, TestContext.Current.CancellationToken));
            Assert.Equal(1, provider.RequestCount);
            var events = ReadCurrentSessionEvents(workspace);
            var lane = Assert.Single(events.OfType<LaneCreated>());
            var turn = Assert.Single(events.OfType<TurnStarted>());
            var profileComponent = Assert.Single(turn.Fingerprint!.Components, component => component.Name == "agent.profile");
            Assert.Equal("2", profileComponent.Version);
            Assert.Equal(lane.AgentProfileHash, profileComponent.Hash);
            Assert.Equal(1, lane.AgentProfileRevision);
            var artifacts = OmniHost.CreateArtifactStore(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace));
            using var content = JsonDocument.Parse(artifacts.GetText(profileComponent.Hash)!);
            Assert.Equal("resolved.configuration", content.RootElement.GetProperty("source").GetString());
            Assert.Empty(content.RootElement.GetProperty("permissionCeiling").GetProperty("writes").EnumerateArray());
            File.WriteAllText(profileFile, yaml.Replace("reads: [\"**\"]", "reads: [\"src/**\"]", StringComparison.Ordinal));
            output.Clear();
            Assert.Equal(1, await host.ExecuteAsync("inspect again", output.Add, TestContext.Current.CancellationToken));
            Assert.Equal(1, provider.RequestCount);
            Assert.Single(ReadCurrentSessionEvents(workspace).OfType<TurnStarted>());
            Assert.Contains(output, line => line.Contains("AgentProfile", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Routed_override_invokes_logical_model_and_persists_the_selected_physical_route()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "providers.yaml"), """
                providers:
                  scripted: { family: OpenAiChatCompatible, baseUrl: http://127.0.0.1:1/v1, auth: none, billingMode: Local }
                """);
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  scripted-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [worker] }
                routing:
                  exploration: [worker]
                """);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", provider.BaseUrl);
            string? requestBody = null;
            provider.RespondWith((_, request) => { requestBody = request; return TextResponse("ruta física seleccionada"); });
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            var output = new List<string>();
            Assert.True(0 == await host.ExecuteAsync("hola por ruta override", output.Add, TestContext.Current.CancellationToken),
                string.Join("\n", output));
            Assert.Equal(1, provider.RequestCount);
            Assert.Contains("\"model\":\"scripted-model\"", requestBody);
            var expected = new ModelRoute("scripted", provider.BaseUrl, ProviderFamily.OpenAiChatCompatible, null, "scripted-model");
            var started = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<ModelStepStarted>());
            Assert.Equal("scripted-model", started.ModelId);
            Assert.Equal(expected.Id, started.RouteId);
            Assert.NotEqual(started.ModelId, started.RouteId!.Value);
            var turn = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<TurnStarted>());
            Assert.NotNull(turn.Fingerprint);
            Assert.Equal(RuntimeBuildIdentity.ForAssembly(typeof(OmniCliRuntime).Assembly), turn.Fingerprint.Build);
            Assert.Equal(new[] { "agent.profile", "context.policy", "model.descriptor", "model.harness", "model.profile",
                "model.reasoning.resolved", "plan.revision", "prompt.template", "provider.adapter", "run.mode_authority",
                "runtime.build", "skills.active", "tools.plan", "turn.instruction" },
                turn.Fingerprint.Components.Select(component => component.Name));
            Assert.NotEqual("core-tools-1", turn.Fingerprint.ToolkitHash);
            var adapter = Assert.Single(turn.Fingerprint.Components, component => component.Name == "provider.adapter");
            Assert.Equal("2", adapter.Version);
            using var adapterMetadata = new MemoryStream();
            using (var writer = new Utf8JsonWriter(adapterMetadata))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("route");
                using (var routeJson = JsonDocument.Parse(expected.CanonicalJson()))
                    routeJson.RootElement.WriteTo(writer);
                writer.WriteString("providerType", typeof(OpenAiChatCompatibleProvider).FullName);
                writer.WriteString("providerBuild", RuntimeBuildIdentity.ForAssembly(typeof(OpenAiChatCompatibleProvider).Assembly));
                writer.WriteEndObject();
            }
            Assert.Equal(ContentHash.Sha256(Convert.ToHexStringLower(SHA256.HashData(
                adapterMetadata.ToArray()))), adapter.Hash);
            Assert.NotNull(adapter.Content); // Exact sensitive fixture content, subject to store redaction.
            var artifacts = OmniHost.CreateArtifactStore(OmniHost.WorkspaceDataDirectory(
                OmniHost.CreatePlatformPaths(), workspace));
            Assert.Equal(Encoding.UTF8.GetString(adapterMetadata.ToArray()), artifacts.GetText(adapter.Hash));
            var lane = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<LaneCreated>(),
                created => created.LaneId == turn.LaneId);
            var agent = Assert.Single(turn.Fingerprint.Components, component => component.Name == "agent.profile");
            using var agentMetadata = JsonDocument.Parse(artifacts.GetText(agent.Hash)!);
            Assert.Equal(lane.AgentProfile.ToString(), agentMetadata.RootElement.GetProperty("profileId").GetString());
            Assert.Equal("lane.created", agentMetadata.RootElement.GetProperty("source").GetString());
            var skills = Assert.Single(turn.Fingerprint.Components, component => component.Name == "skills.active");
            using var skillMetadata = JsonDocument.Parse(artifacts.GetText(skills.Hash)!);
            Assert.Equal("provided", skillMetadata.RootElement.GetProperty("source").GetString());
            Assert.Empty(skillMetadata.RootElement.GetProperty("skills").EnumerateArray());
            Assert.All(turn.Fingerprint.Components, component =>
            {
                Assert.NotNull(component.Content);
                Assert.Equal(component.Hash, component.Content.Hash);
                Assert.False(component.Content.Redacted);
                Assert.True(artifacts.Verify(component.Hash, component.Content.Size));
                using var content = JsonDocument.Parse(artifacts.GetText(component.Hash)!);
                Assert.Equal(JsonValueKind.Object, content.RootElement.ValueKind);
                if (component.Name == "provider.adapter")
                    Assert.Equal(provider.BaseUrl, content.RootElement.GetProperty("route").GetProperty("endpoint").GetString());
                else Assert.DoesNotContain(provider.BaseUrl, content.RootElement.GetRawText());
            });
        });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Turn_fingerprints_only_qualification_for_its_effective_endpoint(
        bool endpointOverride, bool qualifyEffectiveEndpoint)
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            if (endpointOverride)
            {
                File.WriteAllText(Path.Combine(config, "providers.yaml"), """
                    providers:
                      scripted: { family: OpenAiChatCompatible, baseUrl: http://127.0.0.1:1/v1, auth: none, billingMode: Local }
                    """);
                Environment.SetEnvironmentVariable("OMNI_BASE_URL", provider.BaseUrl);
            }
            var registry = OmniHost.LoadUserConfiguration(config).Registry;
            var model = registry.Model("scripted-model")!;
            var descriptor = registry.Provider("scripted")!;
            var key = ModelQualificationHost.QualificationKeyFor(model, descriptor,
                qualifyEffectiveEndpoint ? provider.BaseUrl : descriptor.BaseUrl);
            var traits = new Dictionary<string, double> { ["InstructionFollowing"] = .123 };
            ModelQualificationProfile persisted;
            using (var store = OmniHost.CreateModelQualificationStore(data))
            {
                persisted = store.Upsert(key, 0, ModelQualificationState.Qualified,
                    "fixture-quick", "1.0.0", TestContext.Current.CancellationToken);
                store.SaveTraits(key, persisted.ProfileRevision, new[]
                {
                    new ModelTraitRecord(key.QualificationKeyHash(), persisted.ProfileRevision,
                        "InstructionFollowing", .123, .9, 5, "fixture")
                }, TestContext.Current.CancellationToken);
            }
            // Close/reopen the real User SQLite store before the runtime resolves its profile.
            var expectedQualification = qualifyEffectiveEndpoint
                ? new ModelQualificationSnapshot(key, key.QualificationKeyHash(), persisted.ProfileRevision,
                    persisted.State, traits) : null;
            var route = ModelRoutingHost.RouteFor(model, descriptor, provider.BaseUrl);
            var effective = new ModelProfileResolver().Resolve(model, descriptor,
                empiricalTraits: expectedQualification?.Traits, route: route);
            var harness = new HarnessPolicyResolver().Resolve(effective);
            var expected = RuntimeFingerprintFactory.Create(model, effective, harness,
                new ModelSelection(new ModelIdValue(model.Id), model.ContextWindow, ToolMode.Direct, null,
                    route.Id, route), "unused", "unused", "unused", "unused",
                qualification: expectedQualification);
            provider.RespondWith((_, _) => TextResponse("cualificación aplicada"));
            var output = new List<string>();
            Assert.True(0 == await new TuiTurnHost(OmniCliRuntime.Create(workspace)).ExecuteAsync(
                "hola con perfil", output.Add, TestContext.Current.CancellationToken), string.Join("\n", output));
            Assert.Equal(1, provider.RequestCount);
            var turn = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<TurnStarted>());
            Assert.NotNull(turn.Fingerprint);
            var actualProfile = Assert.Single(turn.Fingerprint.Components, part => part.Name == "model.profile");
            Assert.Equal("2", actualProfile.Version);
            Assert.Equal(Assert.Single(expected.Components, part => part.Name == "model.profile"),
                actualProfile with { Content = null });
            Assert.NotNull(actualProfile.Content);
            var profileArtifacts = OmniHost.CreateArtifactStore(OmniHost.WorkspaceDataDirectory(
                OmniHost.CreatePlatformPaths(), workspace));
            Assert.Equal(actualProfile.Hash, actualProfile.Content.Hash);
            Assert.False(actualProfile.Content.Redacted);
            Assert.True(profileArtifacts.Verify(actualProfile.Hash, actualProfile.Content.Size));
            if (!qualifyEffectiveEndpoint)
            {
                var wrongProfile = new ModelProfileResolver().Resolve(model, descriptor, empiricalTraits: traits, route: route);
                var wrong = RuntimeFingerprintFactory.Create(model, wrongProfile, harness,
                    new ModelSelection(new ModelIdValue(model.Id), model.ContextWindow, ToolMode.Direct, null,
                        route.Id, route), "unused", "unused", "unused", "unused",
                    qualification: new ModelQualificationSnapshot(key, key.QualificationKeyHash(),
                        persisted.ProfileRevision, persisted.State, traits));
                Assert.NotEqual(Assert.Single(wrong.Components, part => part.Name == "model.profile"),
                    actualProfile with { Content = null });
            }
        });
    }

    [Fact]
    public async Task Authorized_auto_escalation_reuses_run_and_marks_completed_only_after_target_response()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  tiny-model: { provider: scripted, context: 4, recommendedUsableContext: 4, maxOutput: 1, aliases: [tiny] }
                  target-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [target] }
                routing:
                  exploration: [tiny]
                  escalation: { mode: auto, chain: [tiny, target] }
                """);
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            var diagnostics = new List<string>();
            Assert.True(0 == await host.ExecuteAsync("intención auto original", diagnostics.Add, TestContext.Current.CancellationToken),
                string.Join("\n", diagnostics));
            Assert.Equal(1, provider.RequestCount);
            var events = ReadCurrentSessionEvents(workspace);
            var run = Assert.Single(events.OfType<RunCreated>());
            var requested = Assert.Single(events.OfType<ModelEscalationRequested>());
            var approved = Assert.Single(events.OfType<ModelEscalationApproved>());
            var completed = Assert.Single(events.OfType<ModelEscalationCompleted>());
            Assert.Equal(run.RunId, requested.RunId);
            Assert.Equal(run.RunId, approved.RunId);
            Assert.Equal(run.RunId, completed.RunId);
            Assert.Equal(requested.TurnId, approved.TurnId);
            Assert.Equal(requested.TurnId, completed.TurnId);
            Assert.Equal("target-model", Assert.Single(events.OfType<ModelStepStarted>()).ModelId);
            Assert.DoesNotContain(events.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            Assert.True(events.ToList().FindIndex(item => item is ModelStepCompleted)
                < events.ToList().FindIndex(item => item is ModelEscalationCompleted));
        });
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("endpoint")]
    [InlineData("billing")]
    public async Task Escalation_resume_denies_rejected_or_changed_route_before_http(string change)
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  tiny-model: { provider: scripted, context: 4, recommendedUsableContext: 4, maxOutput: 1, aliases: [tiny] }
                  target-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [target] }
                routing:
                  exploration: [tiny]
                  escalation: { mode: ask, chain: [tiny, target] }
                """);
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            Assert.Equal(3, await host.ExecuteAsync("original", _ => { }, TestContext.Current.CancellationToken));
            var consent = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<InteractionRequested>(),
                item => item.Kind == InteractionKind.ModelRouteConsent);
            var option = change == "deny" ? "deny" : "allow_route";
            Assert.Equal("ok", client.Send(OmniCore.Protocol.WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"interaction.respond\",\"interactionId\":\"" + consent.InteractionId
                + "\",\"optionId\":\"" + option + "\"}"), CancellationToken.None).Status);
            if (change == "endpoint") Environment.SetEnvironmentVariable("OMNI_BASE_URL", "http://127.0.0.1:1/v1");
            if (change == "billing")
                File.WriteAllText(Path.Combine(config, "providers.yaml"),
                    "providers:\n  scripted: { family: OpenAiChatCompatible, baseUrl: '" + provider.BaseUrl + "', auth: none, billingMode: Unknown }\n");
            Assert.Equal(1, await host.ResumeEscalationAsync(consent.InteractionId.ToString(), _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(0, provider.RequestCount);
            var events = ReadCurrentSessionEvents(workspace);
            Assert.DoesNotContain(events, item => item is ModelStepStarted or ModelEscalationApproved or ModelEscalationCompleted);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task User_consented_ask_escalation_resumes_exact_target_and_original_input_once(bool reopenRuntime)
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  tiny-model: { provider: scripted, context: 4, recommendedUsableContext: 4, maxOutput: 1, aliases: [tiny] }
                  target-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [target] }
                routing:
                  exploration: [tiny]
                  escalation: { mode: ask, chain: [tiny, target] }
                """);
            string? received = null;
            provider.RespondWith((_, request) => { received = request; return TextResponse("respuesta del destino autorizado"); });
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            Assert.Equal(3, await host.ExecuteAsync("intención original durable", _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(0, provider.RequestCount);
            var before = ReadCurrentSessionEvents(workspace);
            var requested = Assert.Single(before.OfType<ModelEscalationRequested>());
            Assert.NotNull(requested.TurnId);
            Assert.NotNull(requested.LaneId);
            var consent = Assert.Single(before.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            var inputCount = before.OfType<UserInputReceived>().Count();
            Assert.Equal(1, inputCount);
            Assert.Equal("ok", client.Send(OmniCore.Protocol.WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"follow-up reservado para después\"}"), CancellationToken.None).Status);
            Assert.Equal("ok", client.Send(OmniCore.Protocol.WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"interaction.respond\",\"interactionId\":\"" + consent.InteractionId
                + "\",\"optionId\":\"allow_route\"}"), CancellationToken.None).Status);
            var afterConsent = ReadCurrentSessionJournalEvents(workspace);
            var codecs = EventCodecs.Create();
            var resumedEnvelope = Assert.Single(afterConsent, item =>
                codecs.Decode(item) is RunInteractionResumed resumed && resumed.InteractionId == consent.InteractionId);
            var resumedMarker = Assert.IsType<RunInteractionResumed>(codecs.Decode(resumedEnvelope));
            Assert.Equal(requested.RunId, resumedMarker.RunId);
            Assert.Equal(RunState.Running,
                RunProjection.Replay(resumedEnvelope.SessionId, requested.RunId, codecs, afterConsent).State);
            var resolutionEnvelope = Assert.Single(afterConsent, item =>
                codecs.Decode(item) is InteractionResolved resolved && resolved.InteractionId == consent.InteractionId);
            Assert.Equal(requested.RunId, resolutionEnvelope.RunId);
            Assert.Equal(resumedEnvelope.Causation, resolutionEnvelope.Causation);
            if (reopenRuntime)
            {
                runtime = OmniCliRuntime.Create(workspace);
                client = runtime.Connect(CancellationToken.None);
                host = new TuiTurnHost(runtime);
            }
            var diagnostics = new List<string>();
            var code = await host.ResumeEscalationAsync(consent.InteractionId.ToString(), diagnostics.Add, TestContext.Current.CancellationToken);
            Assert.True(code == 0, string.Join("\n", diagnostics));
            Assert.Equal(1, provider.RequestCount);
            Assert.NotNull(received);
            Assert.Contains("target-model", received);
            Assert.Contains("intención original durable", Regex.Unescape(received));
            Assert.DoesNotContain("follow-up reservado", Regex.Unescape(received));
            var after = ReadCurrentSessionEvents(workspace);
            Assert.Single(after.OfType<ModelEscalationApproved>());
            Assert.Single(after.OfType<ModelEscalationCompleted>());
            var resumed = Assert.Single(after.OfType<RunInteractionResumed>());
            Assert.Equal(requested.RunId, resumed.RunId);
            Assert.Equal(consent.InteractionId, resumed.InteractionId);
            Assert.NotEqual("", resumed.CommandId);
            Assert.Single(after.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            Assert.All(after.OfType<ModelStepStarted>(), step => Assert.Equal("target-model", step.ModelId));
            Assert.Equal(inputCount, after.OfType<UserInputReceived>().Count());
            var queued = Assert.Single(after.OfType<FollowUpQueued>());
            Assert.DoesNotContain(after.OfType<FollowUpPromoted>(), item => item.FollowUpId == queued.FollowUpId);
            Assert.Equal(1, await host.ResumeEscalationAsync(consent.InteractionId.ToString(), _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(1, provider.RequestCount);
        });
    }

    [Fact]
    public async Task Tui_chat_rejects_file_writes_at_the_tool_boundary()
    {
        await InIsolatedCli(async (workspace, data, config, provider) =>
        {
            provider.RespondWith((index, _) => index == 0
                ? ToolCallResponse("unrequested-write", "filesystem.write", "{\"path\":\"unrequested.txt\",\"content\":\"must not exist\"}")
                : TextResponse("Ejemplo entregado aquí en la conversación"));
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            var diagnostics = new List<string>();
            var exit = await host.ExecuteAsync("Dame un ejemplo aquí, no archivos", diagnostics.Add, TestContext.Current.CancellationToken);
            Assert.Equal(0, exit);
            Assert.False(File.Exists(Path.Combine(workspace, "unrequested.txt")));
            var events = client.SubscribeSince(1).ToArray();
            Assert.Contains(events, evt => evt.PayloadJson.Contains("toolcall.rejected", StringComparison.Ordinal));
            Assert.Contains(events, evt => evt.PayloadJson.Contains("assistant_message.recorded", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Tui_follow_up_sends_previous_run_user_and_assistant_text_to_the_provider()
    {
        await InIsolatedCli(async (workspace, data, config, provider) =>
        {
            provider.RespondWith((_, _) => TextResponse("respuesta anterior única"));
            var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            Assert.Equal(0, await host.ExecuteActAsync("pregunta anterior única", _ => { }, TestContext.Current.CancellationToken));
            string? received = null;
            provider.RespondWith((_, request) => { received = request; return TextResponse("continuación con contexto"); });
            var messages = new List<string>();
            Assert.True(0 == await host.ExecuteAsync("continúa el ejemplo anterior", messages.Add, TestContext.Current.CancellationToken), string.Join("\n", messages));
            Assert.NotNull(received);
            var decoded = System.Text.RegularExpressions.Regex.Unescape(received!);
            Assert.Contains("pregunta anterior", decoded);
            Assert.Contains("respuesta anterior", decoded);
            Assert.Contains("continúa el ejemplo anterior", decoded);
        });
    }

    [Fact]
    public async Task Act_completion_publishes_one_structured_summary_without_an_extra_provider_call()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            provider.RespondWith((_, _) => TextResponse("Resumen final para el usuario."));
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            Assert.Equal(0, await host.ExecuteActAsync("explica el ejemplo", _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(1, provider.RequestCount);
            var events = ReadCurrentSessionJournalEvents(workspace);
            var codecs = EventCodecs.Create();
            var completed = Assert.Single(events, evt => codecs.Decode(evt) is RunCompleted);
            var root = Assert.Single(events.Select(codecs.Decode).OfType<RunSummaryRecorded>());
            Assert.Equal(completed.RunId, root.RunId);
            Assert.Equal(completed.Sequence, root.ThroughEventSequence);
            Assert.True(events.Last().Sequence > completed.Sequence);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_chat_followup_preserves_ordered_messages_once_after_runtime_reopen(bool reopen)
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            provider.RespondWith((_, _) => TextResponse("Usaremos PostgreSQL y el puerto 5432."));
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            Assert.Equal(0, await host.ExecuteAsync("Mi proyecto se llama Brújula; usa PostgreSQL.",
                _ => { }, TestContext.Current.CancellationToken));
            var original = ReadCurrentSessionEvents(workspace).OfType<RunCreated>().Single().SessionId;
            if (reopen) host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            string? received = null;
            provider.RespondWith((_, body) => { received = body; return TextResponse("Continuación."); });
            Assert.Equal(0, await host.ExecuteAsync("¿Qué base y puerto acordamos?", _ => { },
                TestContext.Current.CancellationToken));
            Assert.Equal(2, provider.RequestCount);
            using var request = JsonDocument.Parse(Assert.IsType<string>(received));
            var messages = request.RootElement.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() != "system").ToArray();
            Assert.Equal(new[] { "user", "assistant", "user" },
                messages.Select(message => message.GetProperty("role").GetString()));
            Assert.Equal(new[] { "Mi proyecto se llama Brújula; usa PostgreSQL.",
                "Usaremos PostgreSQL y el puerto 5432.", "¿Qué base y puerto acordamos?" },
                messages.Select(message => message.GetProperty("content").GetString()));
            Assert.All(ReadCurrentSessionEvents(workspace).OfType<RunCreated>(),
                run => Assert.Equal(original, run.SessionId));
        });
    }

    [Fact]
    public async Task Tui_turn_host_executes_real_runtime_http_and_journals_assistant_response()
    {
        await InIsolatedCli(async (workspace, data, config, provider) =>
        {
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            var messages = new List<string>();
            var code = await host.ExecuteAsync("hola", messages.Add, CancellationToken.None);
            Assert.True(code == 0, string.Join("\n", messages));
            Assert.True(provider.RequestCount > 0, "TUI Host debe consultar realmente el proveedor HTTP");
            var identity = client.Query("sessionIdentity", CancellationToken.None);
            Assert.NotNull(identity);
            Assert.Contains("respuesta-scripted", string.Join("\n", messages));
            Assert.Contains(client.SubscribeSince(1), envelope => envelope.PayloadJson.Contains("assistant_message.recorded"));
            var user = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<UserInputReceived>());
            Assert.Contains("hola", user.InputPartsJson);
        });
    }
    [Fact]
    public async Task M1_to_M4_cli_surface_runs_in_isolated_directories()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), "omnicore-cli-e2e-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);

        var previousDirectory = Environment.CurrentDirectory;
        var priorData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        var priorModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        OmniCliRuntime? previousRuntime = null;
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", "es");
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);

            await using var provider = new ScriptedHttpProvider();
            File.WriteAllText(Path.Combine(config, "providers.yaml"), string.Join(Environment.NewLine,
                "providers:", "  scripted:", "    family: OpenAiChatCompatible", "    baseUrl: " + provider.BaseUrl,
                "    auth: none", "    billingMode: Local", ""));
            File.WriteAllText(Path.Combine(config, "models.yaml"), string.Join(Environment.NewLine,
                "models:", "  scripted-model:", "    provider: scripted", "    context: 8192",
                "    maxOutput: 2048", ""));
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
            previousRuntime = CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace));

            // M1: default and JSON renderers, all checked-in scenario files, and crash/recovery.
            var sim = await Run("sim");
            Assert.Equal(0, sim.Code);
            Assert.Contains("Run iniciado:", sim.Output);
            AssertNoLeaks(sim.Output);

            var simJson = await Run("sim", "--json");
            Assert.Equal(0, simJson.Code);
            Assert.Contains("\"type\"", simJson.Output);
            Assert.Contains("\"outcome\"", simJson.Output);
            AssertNoLeaks(simJson.Output, machineReadable: true);

            var scenarios = Path.Combine(repositoryRoot, "docs", "sim");
            foreach (var file in Directory.GetFiles(scenarios, "*.yaml").OrderBy(path => path, StringComparer.Ordinal))
            {
                var result = await Run("sim", file, "--json");
                // crash-resume.yaml models a successful interrupted state; the explicit --crash
                // flag below is the command-level interrupted exit path.
                const int expectedExit = 0;
                Assert.True(result.Code == expectedExit,
                    $"{Path.GetFileName(file)} expected exit={expectedExit}, actual={result.Code}: {result.Output}");
                Assert.Contains("\"outcome\"", result.Output);
                AssertNoLeaks(result.Output, machineReadable: true);
            }

            var crash = await Run("sim", "--crash", "--json");
            Assert.Equal(1, crash.Code);
            Assert.Contains("Interrupted", crash.Output);
            AssertNoLeaks(crash.Output, machineReadable: true);
            var resumed = await Run("sim", "--resume", "--json");
            Assert.Equal(0, resumed.Code);
            Assert.Contains("sim.resumed", resumed.Output);
            Assert.Contains("\"outcome\"", resumed.Output);
            AssertNoLeaks(resumed.Output, machineReadable: true);

            // M1 scenarios can deliberately leave a live Question interaction. Ordinary input must
            // not resolve it (FollowUp contract). Start the independent HTTP smoke in its own
            // workspace instead of implicitly relying on ask to bypass that pending interaction.
            workspace = Path.Combine(root, "workspace-http");
            Directory.CreateDirectory(workspace);
            Environment.CurrentDirectory = workspace;
            CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace));

            // M2: actual HTTP Chat Completions provider, plus diagnostics and typed client commands.
            var doctor = await Run("doctor");
            Assert.Equal(0, doctor.Code);
            Assert.Contains("omni doctor — diagnóstico de M2", doctor.Output);
            Assert.Contains("Modelos disponibles:", doctor.Output);
            Assert.Contains("Estado: modelo configurado", doctor.Output);
            Assert.DoesNotContain("doctor.", doctor.Output);
            AssertNoLeaks(doctor.Output);
            var ask = await Run("ask", "explica el estado");
            Assert.True(ask.Code == 0, $"ask expected exit=0, actual={ask.Code}: {ask.Output}");
            Assert.Contains("respuesta-scripted", ask.Output);
            AssertNoLeaks(ask.Output);

            // Verify the new workspace's journal after ask has actually created it.
            var doctorJournal = await Run("doctor", "--verify-journal");
            Assert.Equal(0, doctorJournal.Code);
            Assert.Contains("verify-journal:", doctorJournal.Output);
            AssertNoLeaks(doctorJournal.Output);

            var explain = await Run("explain", "estado del plan");
            Assert.Equal(0, explain.Code);
            Assert.Contains("Pregunta: estado del plan", explain.Output);
            AssertNoLeaks(explain.Output);

            var context = await Run("/context");
            Assert.Equal(0, context.Code);
            Assert.Contains("Snapshot de contexto", context.Output);
            AssertNoLeaks(context.Output);

            var tools = await Run("/tools");
            Assert.Equal(0, tools.Code);
            Assert.Contains("Tools efectivas", tools.Output);
            AssertNoLeaks(tools.Output);

            var typedExplain = await Run("/explain");
            Assert.Equal(0, typedExplain.Code);
            Assert.Contains("respuesta-scripted", typedExplain.Output);
            AssertNoLeaks(typedExplain.Output);

            // M3: exercise the real Act path and verify the scripted provider response is rendered.
            var act = await Run("act", "implementa el objetivo");
            Assert.Equal(0, act.Code);
            Assert.Contains("respuesta-scripted", act.Output);
            AssertNoLeaks(act.Output);

            var policyList = await Run("model", "list");
            Assert.Equal(0, policyList.Code);
            Assert.Contains("scripted-model", policyList.Output);
            AssertNoLeaks(policyList.Output);

            var policyShowMissing = await Run("model", "policy", "show", "scripted-model");
            Assert.Equal(1, policyShowMissing.Code);
            AssertNoLeaks(policyShowMissing.Output);

            var policySet = await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "e2e");
            Assert.Equal(0, policySet.Code);
            Assert.Contains("PatchOnly", policySet.Output);
            AssertNoLeaks(policySet.Output);

            var policyShow = await Run("model", "policy", "show", "scripted-model");
            Assert.Equal(0, policyShow.Code);
            Assert.Contains("PatchOnly", policyShow.Output);
            AssertNoLeaks(policyShow.Output);

            var select = await Run("model", "select", "scripted-model");
            Assert.Equal(0, select.Code);
            Assert.Contains("scripted-model", select.Output);
            AssertNoLeaks(select.Output);

            var policyHistory = await Run("model", "policy", "history", "scripted-model");
            Assert.Equal(0, policyHistory.Code);
            Assert.Contains("rev=", policyHistory.Output);
            AssertNoLeaks(policyHistory.Output);

            var policyDelete = await Run("model", "policy", "delete", "scripted-model");
            Assert.Equal(0, policyDelete.Code);
            Assert.Contains("eliminada", policyDelete.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(policyDelete.Output);

            // M4: trust, permission grants, interaction resolution, integrity and maintenance.
            var trust = await Run("trust");
            Assert.Equal(0, trust.Code);
            Assert.Contains("confiable", trust.Output);
            AssertNoLeaks(trust.Output);
            var untrust = await Run("trust", "revoke");
            Assert.Equal(0, untrust.Code);
            Assert.Contains("no confiable", untrust.Output);
            AssertNoLeaks(untrust.Output);

            var paths = OmniHost.CreatePlatformPaths();
            var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
                ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspace)));
            var workspaceData = OmniHost.WorkspaceDataDirectory(paths, workspace);
            var grantStore = new FilePermissionGrantStore(workspaceData, new FileAuditSink(data));
            var grant = new PermissionGrantRecord(GrantId.New(), "filesystem.read", new string('a', 64),
                GrantLifetime.Workspace, workspaceId, null, DateTimeOffset.UtcNow);
            grantStore.Add(grant, CancellationToken.None);

            var permissions = await Run("permissions", "list");
            Assert.Equal(0, permissions.Code);
            Assert.Contains(grant.Id.ToString(), permissions.Output);
            AssertNoLeaks(permissions.Output);
            var revoke = await Run("permissions", "revoke", grant.Id.ToString());
            Assert.Equal(0, revoke.Code);
            Assert.Contains("revocado", revoke.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(revoke.Output);
            var revokeMissing = await Run("permissions", "revoke", Guid.NewGuid().ToString());
            Assert.Equal(1, revokeMissing.Code);
            AssertNoLeaks(revokeMissing.Output);

            var resolve = await Run("resolve");
            Assert.Equal(1, resolve.Code);
            Assert.Contains("No hay", resolve.Output);
            AssertNoLeaks(resolve.Output);
            var resolveUnknown = await Run("resolve", "missing-interaction", "allow_once");
            Assert.Equal(1, resolveUnknown.Code);
            AssertNoLeaks(resolveUnknown.Output);

            // An explicit bad path is a stable user-facing failure mode, independent of internal storage layout.
            var verifyOk = await Run("verify-journal");
            Assert.True(verifyOk.Code == 0, $"verify-journal exited {verifyOk.Code}: {verifyOk.Output}");
            Assert.Contains("— OK", verifyOk.Output);
            AssertNoLeaks(verifyOk.Output);

            var verify = await Run("verify-journal", Path.Combine(root, "missing-journal.db"));
            Assert.Equal(1, verify.Code);
            Assert.Contains("JournalUnreadable", verify.Output);
            AssertNoLeaks(verify.Output);

            var sessionPurge = await Run("session", "purge", "not-a-guid");
            Assert.Equal(2, sessionPurge.Code);
            Assert.Contains("no válido", sessionPurge.Output);
            AssertNoLeaks(sessionPurge.Output);

            var gc = await Run("gc", "--dry-run");
            Assert.Equal(0, gc.Code);
            Assert.Contains("GC", gc.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(gc.Output);

            var auditPurge = await Run("audit", "purge", "--dry-run", "--before", "2026-01-01");
            Assert.Equal(0, auditPurge.Code);
            Assert.Contains("auditoría", auditPurge.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(auditPurge.Output);

            // Headless only: must return without initializing an interactive terminal driver.
            var tui = await Run("tui", "--sim");
            Assert.True(tui.Code == 0, $"tui --sim exited {tui.Code}: {tui.Output}");
            Assert.Contains("TUI (sim)", tui.Output);
            Assert.Contains("OmniCore", tui.Output);
            AssertNoLeaks(tui.Output);

            var journal = Path.Combine(workspaceData, "journal.db");
            string sessionId;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journal }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT session_id FROM events ORDER BY seq LIMIT 1";
                sessionId = Convert.ToString(command.ExecuteScalar()) ?? "";
            }
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            var purge = await Run("session", "purge", sessionId);
            Assert.Equal(0, purge.Code);
            Assert.Contains("sesión", purge.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(purge.Output);

            Environment.SetEnvironmentVariable("OMNI_LOCALE", "en");
            var englishDoctor = await Run("doctor");
            Assert.Equal(0, englishDoctor.Code);
            Assert.Contains("omni doctor — M2 diagnostics", englishDoctor.Output);
            Assert.Contains("Available models:", englishDoctor.Output);
            Assert.Contains("Status: model configured", englishDoctor.Output);
            Assert.DoesNotContain("doctor.", englishDoctor.Output);
            AssertNoLeaks(englishDoctor.Output);

            Environment.SetEnvironmentVariable("OMNI_LOCALE", "es");
            var explicitEnglishDoctor = await Run("doctor", "--locale", "en");
            Assert.Equal(0, explicitEnglishDoctor.Code);
            Assert.Contains("Available models:", explicitEnglishDoctor.Output);
            Assert.DoesNotContain("doctor.", explicitEnglishDoctor.Output);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", "en");

            var help = await Run("--help");
            Assert.Equal(0, help.Code);
            Assert.Contains("Usage:", help.Output);
            AssertNoLeaks(help.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", priorData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", priorConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
            Environment.SetEnvironmentVariable("OMNI_MODEL", priorModel);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task Act_executes_read_and_approved_patch_before_completing_gates()
    {
        await InIsolatedCli(async (workspace, _, data, provider) =>
        {
            const string original = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n";
            File.WriteAllText(Path.Combine(workspace, "doc.txt"), original);
            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
            provider.RespondWith((requestNumber, _) => requestNumber switch
            {
                0 => ToolCallResponse("call-read", "filesystem.read", """{"path":"doc.txt"}"""),
                1 => ToolCallResponse("call-patch", "filesystem.patch",
                    PatchArguments(version)),
                _ => TextResponse("cambio aplicado"),
            });

            var policy = await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "cli-e2e");
            Assert.Equal(0, policy.Code);
            var policyService = OmniHost.CreateModelPolicyService(data);
            var policyKey = ModelPolicyKey.For("scripted", "scripted-model");
            var stored = Assert.IsType<StoredModelPolicy>(policyService.Get(policyKey, CancellationToken.None));
            var oldMutation = stored.Policy.MutationPolicy;
            var noPostEditGate = new FileMutationPolicy(oldMutation.Mode, oldMutation.Delete, oldMutation.MoveOrRename,
                oldMutation.MaxFilesPerTurn, oldMutation.MaxChangedLinesPerTurn, oldMutation.MaxRewriteRatio,
                oldMutation.RequirePriorRead, oldMutation.RequireExpectedVersionToken,
                requirePostEditValidation: false, allowParallelMutations: oldMutation.AllowParallelMutations);
            policyService.Set(policyKey, stored.Revision,
                new UserModelPolicy(stored.Policy.Category, stored.Policy.ToolPolicy, noPostEditGate,
                    stored.Policy.Source, "cli-e2e-approved-patch"), CancellationToken.None);
            var act = await Run("act", "actualiza doc.txt");
            Assert.Equal(0, act.Code);
            Assert.Equal("one\ntwo-X\nthree\nfour\nfive\nsix\nseven\neight\n",
                File.ReadAllText(Path.Combine(workspace, "doc.txt")));
            Assert.Contains("[herramienta] filesystem.read", act.Output);
            Assert.Contains("[herramienta] filesystem.patch", act.Output);
            AssertNoLeaks(act.Output);

            var payloads = ReadCurrentSessionEvents(workspace);
            var patchCall = Assert.Single(payloads.OfType<ToolCallRequested>(),
                call => call.ToolName == "filesystem.patch");
            var started = Assert.Single(payloads.OfType<ToolCallStarted>(),
                evt => evt.ToolCallId == patchCall.ToolCallId);
            var succeeded = Assert.Single(payloads.OfType<ToolCallSucceeded>(),
                evt => evt.ToolCallId == patchCall.ToolCallId);
            Assert.Equal(EffectClass.NonIdempotent, started.EffectClass);
            var ordered = payloads.ToList();
            Assert.True(ordered.IndexOf(started) < ordered.IndexOf(succeeded),
                "Barrier ToolCallStarted must precede successful effect outcome");
            var validation = Assert.Single(payloads.OfType<RunValidationStarted>());
            var completed = Assert.Single(payloads.OfType<RunCompleted>());
            Assert.True(payloads.ToList().IndexOf(validation) < payloads.ToList().IndexOf(completed));
            Assert.Equal(3, provider.RequestCount);
        });
    }

    [Fact]
    public async Task Act_permission_ask_without_tty_stays_awaiting_input_without_inventing_answer()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            const string original = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n";
            File.WriteAllText(Path.Combine(workspace, "doc.txt"), original);
            Assert.Equal(0, (await Run("trust")).Code);
            var settings = Path.Combine(workspace, ".omnicore");
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "settings.yaml"), string.Join(Environment.NewLine,
                "permissionRestrictions:", "  filesystem.patch: ask", ""));
            provider.RespondWith((requestNumber, _) => requestNumber == 0
                ? ToolCallResponse("call-read", "filesystem.read", """{"path":"doc.txt"}""")
                : ToolCallResponse("call-patch", "filesystem.patch",
                    PatchArguments(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant())));

            Assert.Equal(0, (await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "cli-e2e-ask")).Code);
            var result = await Run("act", "actualiza doc.txt");
            Assert.Equal(3, result.Code);
            Assert.Contains("InputRequired", result.Output);
            Assert.Contains("No se asumió una respuesta", result.Output);
            Assert.Equal(original, File.ReadAllText(Path.Combine(workspace, "doc.txt")));
            var payloads = ReadCurrentSessionEvents(workspace);
            Assert.Contains(payloads, evt => evt is InteractionRequested request && request.Kind == InteractionKind.Permission);
            Assert.Contains(payloads, evt => evt is RunAwaitingInput);
            Assert.DoesNotContain(payloads, evt => evt is InteractionResolved);
            Assert.DoesNotContain(payloads, evt => evt is ToolCallStarted started && started.EffectClass != EffectClass.None);
            AssertNoLeaks(result.Output);
        });
    }

    [Fact]
    public async Task Resolve_lists_and_answers_live_effect_recovery_interaction_then_allows_new_run()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
            Directory.CreateDirectory(workspaceData);
            var journalPath = Path.Combine(workspaceData, "journal.db");
            var statePath = Path.Combine(workspaceData, "lastsession.txt");
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var store = new SqliteEventStore(journalPath);
            var stream = new EventStream(store, codecs, session);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(workspace).ToString(), workspace,
                ProfileId.New(), DateTimeOffset.UtcNow));
            var run = TestRun.Open(stream, session, "efecto interrumpido", RunMode.Act);
            var call = ToolCallId.New();
            stream.Append(new ToolCallRequested(call, "pc-recovery", "filesystem.patch", "{}"));
            stream.Append(new ToolCallPrepared(call, "{}"));
            stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(call));
            stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent, null), DurabilityClass.Barrier);
            stream.Append(new ToolCallEffectUnknown(call, EffectClass.NonIdempotent));
            stream.Append(new RunFailed(run.RunId, "interrupted after barrier"));
            File.WriteAllText(statePath, session + Environment.NewLine + run.RunId);
            store.Close();

            var list = await Run("resolve");
            Assert.Equal(0, list.Code);
            Assert.Contains("resolution_applied", list.Output);
            var match = Regex.Match(list.Output, @"#(?<id>[0-9a-fA-F-]{36})");
            Assert.True(match.Success, "resolve should print the live interaction id: " + list.Output);

            var response = await Run("resolve", match.Groups["id"].Value, "resolution_applied");
            Assert.Equal(0, response.Code);
            Assert.Contains("resuelta", response.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(list.Output);
            AssertNoLeaks(response.Output);

            provider.RespondWith((_, _) => TextResponse("nuevo run completado"));
            var act = await Run("act", "inicia trabajo nuevo");
            Assert.Equal(0, act.Code);
            Assert.Contains("nuevo run completado", act.Output);
            var payloads = ReadAllSessionEvents(workspace);
            Assert.True(payloads.OfType<RunCreated>().Count() >= 2);
            Assert.Contains(payloads, evt => evt is RunCompleted);
            Assert.Contains(payloads, evt => evt is InteractionResolved resolved
                && resolved.InteractionId.ToString() == match.Groups["id"].Value);
        });
    }

    private static IReadOnlyList<DomainEventPayload> ReadCurrentSessionEvents(string workspace)
    {
        var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
        var sessionId = SessionId.Parse(File.ReadAllLines(Path.Combine(workspaceData, "lastsession.txt"))[0]);
        var store = new SqliteEventStore(Path.Combine(workspaceData, "journal.db"));
        try { return store.ReadFrom(sessionId, 1).Select(EventCodecs.Create().Decode).ToArray(); }
        finally { store.Close(); }
    }

    private static IReadOnlyList<DomainEvent> ReadCurrentSessionJournalEvents(string workspace)
    {
        var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
        var sessionId = SessionId.Parse(File.ReadAllLines(Path.Combine(workspaceData, "lastsession.txt"))[0]);
        var store = new SqliteEventStore(Path.Combine(workspaceData, "journal.db"));
        try { return store.ReadFrom(sessionId, 1); }
        finally { store.Close(); }
    }

    private static IReadOnlyList<DomainEventPayload> ReadAllSessionEvents(string workspace)
    {
        var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
        var journal = Path.Combine(workspaceData, "journal.db");
        var sessions = new List<SessionId>();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journal }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT session_id FROM events";
            using var reader = command.ExecuteReader();
            while (reader.Read()) sessions.Add(SessionId.Parse(reader.GetString(0)));
        }
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            return sessions.SelectMany(session => store.ReadFrom(session, 1))
                .Select(codecs.Decode).ToArray();
        }
        finally { store.Close(); }
    }

    private static async Task InIsolatedCli(Func<string, string, string, ScriptedHttpProvider, Task> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-cli-e2e-gap-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        var previousDirectory = Environment.CurrentDirectory;
        var priorData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        var priorModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        OmniCliRuntime? previousRuntime = null;
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", null);
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
            await using var provider = new ScriptedHttpProvider();
            File.WriteAllText(Path.Combine(config, "providers.yaml"), string.Join(Environment.NewLine,
                "providers:", "  scripted:", "    family: OpenAiChatCompatible", "    baseUrl: " + provider.BaseUrl,
                "    auth: none", "    billingMode: Local", ""));
            File.WriteAllText(Path.Combine(config, "models.yaml"), string.Join(Environment.NewLine,
                "models:", "  scripted-model:", "    provider: scripted", "    context: 8192",
                "    maxOutput: 2048", ""));
            var runtime = OmniCliRuntime.Create(workspace);
            runtime.Localize = (key, values) => Localization.Spanish().Resolve(key, values);
            previousRuntime = CliApp.UseRuntimeForTests(runtime);
            await run(workspace, config, data, provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, priorData);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, priorConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
            Environment.SetEnvironmentVariable("OMNI_MODEL", priorModel);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime);
            Environment.CurrentDirectory = previousDirectory;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static string ToolCallResponse(string id, string name, string arguments)
    {
        var eventBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { tool_calls = new[] { new
                { index = 0, id, type = "function", function = new { name, arguments } } } }, finish_reason = (string?)null } },
        });
        return string.Join("\n", "data: " + eventBody, "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}", "",
            "data: [DONE]", "");
    }

    private static string ResponsesToolCallResponse(string id, string name, string arguments)
    {
        var wireName = name.Length is > 0 and <= 64 && !name.StartsWith("omni_", StringComparison.Ordinal)
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? name
            : "omni_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..48];
        var encodedArguments = System.Text.Json.JsonSerializer.Serialize(arguments);
        var encodedName = System.Text.Json.JsonSerializer.Serialize(wireName);
        var encodedId = System.Text.Json.JsonSerializer.Serialize(id);
        return string.Join("\n", new[]
        {
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"id\":\"fc_" + id + "\",\"call_id\":" + encodedId + ",\"name\":" + encodedName + "}}",
            "",
            "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":" + encodedArguments + "}",
            "",
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"id\":\"fc_" + id + "\",\"call_id\":" + encodedId + ",\"name\":" + encodedName + ",\"arguments\":" + encodedArguments + "}}",
            "",
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_" + id + "\",\"status\":\"completed\",\"usage\":{\"input_tokens\":64,\"output_tokens\":16}}}",
            "",
        });
    }

    private static string ResponsesTextResponse(string text)
    {
        var encoded = System.Text.Json.JsonSerializer.Serialize(text);
        return string.Join("\n", new[]
        {
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"role\":\"assistant\"}}",
            "",
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":" + encoded + "}",
            "",
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":" + encoded + "}]}}",
            "",
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":64,\"output_tokens\":16}}}",
            "",
        });
    }

    private static string PatchArguments(string version) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            path = "doc.txt", expectedVersion = version, oldText = "two\n", newText = "two-X\n",
        });

    private static string TextResponse(string text)
    {
        var eventBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = text }, finish_reason = (string?)null } },
        });
        return string.Join("\n", "data: " + eventBody, "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}", "",
            "data: [DONE]", "");
    }

    private static async Task<(int Code, string Output)> Run(params string[] args)
    {
        var prior = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var code = await CliApp.RunAsync(args);
            return (code, output.ToString());
        }
        finally { Console.SetOut(prior); }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs", "sim")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró docs/sim.");
    }

    private static void AssertNoLeaks(string output, bool machineReadable = false)
    {
        Assert.False(Regex.IsMatch(output, @"(?m)^\s*at [A-Za-z0-9_.+`]+\("), "Se filtró una traza: " + output);
        Assert.DoesNotContain("frames:", output, StringComparison.OrdinalIgnoreCase);
        if (machineReadable) return; // JSON conserva identificadores de eventos, que no son texto localizado.
        var localizationKey = Regex.Match(output, @"\b[a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*)*\.[a-z][a-z0-9]*_[a-z0-9_]+\b",
            RegexOptions.IgnoreCase);
        Assert.False(localizationKey.Success, "Se filtró una clave de localización: " + localizationKey.Value);
    }

    /// <summary>A tiny real HTTP server returning deterministic OpenAI-compatible SSE responses.</summary>
    private sealed class ScriptedHttpProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private Func<int, string, string>? _response;
        private int _requestCount;

        public ScriptedHttpProvider()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public void RespondWith(Func<int, string, string> response) => _response = response;

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = HandleAsync(client, _stop.Token);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                var body = new char[contentLength];
                var read = 0;
                while (read < body.Length)
                {
                    var count = await reader.ReadAsync(body.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }

                var requestNumber = Interlocked.Increment(ref _requestCount) - 1;
                var request = new string(body, 0, read);
                var eventText = _response?.Invoke(requestNumber, request) ?? TextResponse("respuesta-scripted");
                var bytes = Encoding.UTF8.GetBytes(eventText);
                var response = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + bytes.Length) + "\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _serve; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
