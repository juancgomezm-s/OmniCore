using System.Diagnostics;
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
public sealed partial class CliEndToEndTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public async Task Normal_model_switch_consent_resumes_original_conversation_once(bool reopenRuntime,
        bool existingActRun, bool includedQuota)
    {
        await InIsolatedCli(async (workspace, config, _, provider) =>
        {
            var reasoningBoost = existingActRun && !reopenRuntime;
            if (includedQuota)
            {
                var file = Path.Combine(config, "providers.yaml");
                File.WriteAllText(file, File.ReadAllText(file).Replace("billingMode: Local", "billingMode: IncludedQuota"));
            }
            if (reasoningBoost)
            {
                var file = Path.Combine(config, "providers.yaml");
                File.WriteAllText(file, File.ReadAllText(file).Replace("family: OpenAiChatCompatible", "family: OpenAIResponses\n    profile: api"));
            }
            string? received = null;
            provider.RespondWith((_, request) => { received = request; return reasoningBoost
                ? ResponsesTextResponse("respuesta conservada") : TextResponse("respuesta conservada"); });
            using (var preferences = new RunModePreferenceStore(OmniHost.CreatePlatformPaths().UserDatabasePath))
                preferences.Set(existingActRun ? RunMode.Act : RunMode.Plan, 0);
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            Assert.Equal(0, await host.ExecuteAsync("primer mensaje de la conversación", _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(existingActRun ? RunMode.Act : RunMode.Plan,
                Assert.Single(ReadCurrentSessionEvents(workspace).OfType<RunCreated>()).Mode);
            File.AppendAllText(Path.Combine(config, "models.yaml"),
                "  target-model: { provider: scripted, context: 8192, maxOutput: 2048, reasoning: { supported: true, effortLevels: [high] } }\n");
            Environment.SetEnvironmentVariable("OMNI_MODEL", "target-model");
            if (reasoningBoost) Assert.True(host.SetNextTurnReasoningBoost("high", null));
            Assert.Equal(3, await host.ExecuteAsync("petición original después del cambio", _ => { }, TestContext.Current.CancellationToken));
            var before = ReadCurrentSessionEvents(workspace);
            Assert.Empty(before.OfType<ModelEscalationRequested>());
            var consent = Assert.Single(before.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            Assert.Contains("continuation", consent.SubjectJson);
            Assert.Equal(1, provider.RequestCount);
            Assert.Equal("ok", client.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"interaction.respond\",\"interactionId\":\"" + consent.InteractionId
                + "\",\"optionId\":\"allow_route\"}"), CancellationToken.None).Status);
            if (reopenRuntime)
            {
                runtime.Dispose();
                runtime = OmniCliRuntime.Create(workspace);
                client = runtime.Connect(CancellationToken.None);
                host = new TuiTurnHost(runtime);
            }
            using (runtime)
            {
                var diagnostics = new List<string>();
                Assert.True(await host.ResumeModelRouteAsync(consent.InteractionId.ToString(), diagnostics.Add,
                    TestContext.Current.CancellationToken) == 0, string.Join("\n", diagnostics));
                Assert.Equal(2, provider.RequestCount);
                Assert.Contains("target-model", received!);
                Assert.Contains("primer mensaje de la conversación", Regex.Unescape(received!));
                Assert.Contains("petición original después del cambio", Regex.Unescape(received!));
                var after = ReadCurrentSessionEvents(workspace);
                Assert.Equal(before.OfType<UserInputReceived>().Count(), after.OfType<UserInputReceived>().Count());
                Assert.Equal(before.OfType<RunCreated>().Count(), after.OfType<RunCreated>().Count());
                Assert.Equal("target-model", after.OfType<ModelStepStarted>().Last().ModelId);
                Assert.True(after.OfType<TurnStarted>().Last().InstructionSnapshot!.ConversationOnly);
                Assert.Empty(after.OfType<ModelEscalationApproved>());
                Assert.Equal(1, await host.ResumeModelRouteAsync(consent.InteractionId.ToString(), _ => { },
                    TestContext.Current.CancellationToken));
                Assert.Equal(2, provider.RequestCount);
                if (reasoningBoost)
                {
                    Assert.Equal(ReasoningSelectionSource.TurnBoost, after.OfType<TurnStarted>().Last().ReasoningResolution!.Source);
                    Assert.Equal(0, await host.ExecuteAsync("siguiente mensaje sin refuerzo", diagnostics.Add,
                        TestContext.Current.CancellationToken));
                    Assert.Equal(3, provider.RequestCount);
                    Assert.Null(ReadCurrentSessionEvents(workspace).OfType<TurnStarted>().Last().ReasoningResolution!.TurnBoostId);
                }
            }
        });
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("endpoint")]
    [InlineData("billing")]
    [InlineData("new-input")]
    public async Task Normal_route_resume_rejects_denied_changed_or_superseded_intent(string change)
    {
        await InIsolatedCli(async (workspace, config, _, provider) =>
        {
            using var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            Assert.Equal(0, await host.ExecuteAsync("primer mensaje", _ => { }, TestContext.Current.CancellationToken));
            File.AppendAllText(Path.Combine(config, "models.yaml"),
                "  target-model: { provider: scripted, context: 8192, maxOutput: 2048 }\n");
            Environment.SetEnvironmentVariable("OMNI_MODEL", "target-model");
            Assert.Equal(3, await host.ExecuteAsync("mensaje pendiente", _ => { }, TestContext.Current.CancellationToken));
            var consent = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<InteractionRequested>(),
                item => item.Kind == InteractionKind.ModelRouteConsent);
            Assert.Equal("ok", client.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"interaction.respond\",\"interactionId\":\"" + consent.InteractionId
                + "\",\"optionId\":\"" + (change == "deny" ? "deny" : "allow_route") + "\"}"), CancellationToken.None).Status);
            if (change is "endpoint" or "billing")
            {
                var file = Path.Combine(config, "providers.yaml");
                File.WriteAllText(file, File.ReadAllText(file).Replace(
                    change == "endpoint" ? provider.BaseUrl : "billingMode: Local",
                    change == "endpoint" ? "http://127.0.0.1:1/v1" : "billingMode: IncludedQuota"));
            }
            if (change == "new-input")
                Assert.Equal("ok", client.Send(WireEnvelope.Command(Ids.NewV7(),
                    "{\"cmd\":\"session.input\",\"text\":\"nueva intención\"}"), CancellationToken.None).Status);
            var count = ReadCurrentSessionEvents(workspace).OfType<UserInputReceived>().Count();
            Assert.Equal(1, await host.ResumeModelRouteAsync(consent.InteractionId.ToString(), _ => { },
                TestContext.Current.CancellationToken));
            Assert.Equal(1, provider.RequestCount);
            Assert.Equal(count, ReadCurrentSessionEvents(workspace).OfType<UserInputReceived>().Count());
        });
    }

    [Fact]
    public async Task Occupied_session_rejects_input_without_provider_work_and_same_window_can_retry_after_owner_closes()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            Assert.Equal(0, (await Run("ask", "seed writer ownership")).Code);
            var original = ReadCurrentSessionEvents(workspace);
            var requests = provider.RequestCount;
            using var contender = OmniCliRuntime.Create(workspace);
            contender.Localize = (key, values) => Localization.Spanish().Resolve(key, values);
            _ = contender.Connect(TestContext.Current.CancellationToken);
            var host = new TuiTurnHost(contender);
            var diagnostics = new List<string>();
            const string prompt = "mensaje conservado para reintentar";
            var blocked = await host.ExecuteAsync(prompt, diagnostics.Add, TestContext.Current.CancellationToken);
            Assert.True(blocked == TurnExitCodes.SessionBusy, string.Join("\n", diagnostics));
            Assert.Equal(requests, provider.RequestCount);
            Assert.Equal(original.Count, ReadCurrentSessionEvents(workspace).Count);
            Assert.DoesNotContain("InvalidOperationException", string.Join("\n", diagnostics));
            Assert.Contains("otra ventana", string.Join("\n", diagnostics), StringComparison.OrdinalIgnoreCase);

            // Release the actual owner, not its lock file; keep the contender runtime alive.
            CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace)).Dispose();
            diagnostics.Clear();
            Assert.True(0 == await host.ExecuteAsync(prompt, diagnostics.Add, TestContext.Current.CancellationToken),
                string.Join("\n", diagnostics));
            Assert.Equal(requests + 1, provider.RequestCount);
            var after = ReadCurrentSessionEvents(workspace);
            Assert.Single(after.OfType<UserInputReceived>(), input => input.InputPartsJson.Contains(prompt, StringComparison.Ordinal));
            Assert.Equal(original.OfType<SessionCreated>().Single().SessionId, after.OfType<SessionCreated>().Single().SessionId);
        });
    }

    [Fact]
    public async Task Explicit_project_skill_read_ceiling_is_applied_by_the_real_cli_runtime()
    {
        await InIsolatedCli(async (workspace, _, data, provider) =>
        {
            var allowedPath = Path.Combine(workspace, "src", "allowed.txt");
            var deniedPath = Path.Combine(workspace, "other", "denied.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(allowedPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(deniedPath)!);
            File.WriteAllText(allowedPath, "allowed fixture data");
            File.WriteAllText(deniedPath, "must remain outside the skill ceiling");
            var skillDirectory = Path.Combine(workspace, ".omnicore", "skills", "read.cap");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "skill.yaml"), """
                id: read.cap
                version: 1
                budgetTokens: 64
                tools: [filesystem.read]
                permissions:
                  filesystem:
                    read: ["src/allowed.txt"]
                """);
            File.WriteAllText(Path.Combine(skillDirectory, "instructions.md"), "Read only the declared fixture file.");

            var runtime = OmniCliRuntime.Create(workspace);
            runtime.SetWorkspaceTrusted(true);
            CliApp.UseRuntimeForTests(runtime).Dispose();
            string? requestBody = null;
            provider.RespondWith((index, request) => index switch
            {
                0 => TextResponse("session ready"),
                1 => RecordAndCall(request, "allowed-read", "src/allowed.txt"),
                2 => RecordAndCall(request, "denied-read", "other/denied.txt"),
                _ => TextResponse("read plan finished"),
            });

            var started = await Run("ask", "start the workspace session");
            Assert.Equal(0, started.Code);
            var activation = await Run("/skills", "load", "read.cap", "Project");
            Assert.Equal(0, activation.Code);
            Assert.Contains("activada", activation.Output, StringComparison.OrdinalIgnoreCase);
            var inspected = await Run("/skills");
            Assert.Contains("read.cap", inspected.Output, StringComparison.Ordinal);
            Assert.Contains("activada", inspected.Output, StringComparison.OrdinalIgnoreCase);
            var result = await Run("ask", "read the approved and unapproved files");
            Assert.Equal(0, result.Code);
            Assert.Contains("filesystem.read", requestBody!, StringComparison.Ordinal);
            var events = ReadCurrentSessionEvents(workspace);
            Assert.Contains(events.OfType<SkillActivationChanged>(), item => item.Activated);
            var activeFingerprint = Assert.Single(events.OfType<TurnStarted>().Last().Fingerprint!.Components,
                item => item.Name == "skills.active");
            var artifacts = OmniHost.CreateArtifactStore(OmniHost.WorkspaceDataDirectory(
                OmniHost.CreatePlatformPaths(), workspace));
            var activeSkillsJson = artifacts.GetText(activeFingerprint.Hash)!;
            Assert.Contains("read.cap", activeSkillsJson, StringComparison.Ordinal);
            Assert.Contains(events, item => item is ToolCallSucceeded);
            Assert.True(events.OfType<ToolCallRejected>().Any(),
                $"provider requests={provider.RequestCount}; tool requests={string.Join(" | ", events.OfType<ToolCallRequested>().Select(item => item.ToolName + " " + item.ArgumentsJson))}; completed={string.Join(" | ", events.OfType<ToolCallSucceeded>().Select(item => item.ToolCallId))}");
            var rejected = Assert.Single(events.OfType<ToolCallRejected>());
            Assert.Contains("skill.permissions.ceiling_exceeded", rejected.Reason, StringComparison.Ordinal);

            string RecordAndCall(string request, string id, string path)
            {
                requestBody = request;
                return ToolCallResponse(id, "filesystem.read", System.Text.Json.JsonSerializer.Serialize(new { path }));
            }
        });
    }

    [Fact]
    public async Task Claude_lane_runs_through_admission_owned_worktree_and_durable_session_barrier()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            const string profile = "0199a000-0000-7000-8000-000000000031";
            const string model = "claude-haiku-5-5";
            var liveClaudeAcceptance = string.Equals(
                Environment.GetEnvironmentVariable("OMNI_M7_LIVE_CLAUDE_ACCEPTANCE"), "1", StringComparison.Ordinal);
            InitializeGitRepository(workspace);
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), $$"""
                defaultProfile: {{profile}}
                agentProfiles:
                  claude-writer:
                    id: {{profile}}
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: ["**"]
                      process: []
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read, filesystem.write, filesystem.patch]
                    executor: claude-code
                    externalModel: {{model}}
                """);
            using (var policies = OmniHost.CreateModelPolicyService(data))
                policies.Set(ModelPolicyKey.For("claude-code", model), 0, ModelPolicyPresets.FullAgent(),
                    TestContext.Current.CancellationToken);
            SetSourceModelFullAgent(data);

            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  scripted-model:
                    provider: scripted
                    context: 8192
                    maxOutput: 2048
                    inputPricePerMillionUsd: 0
                    outputPricePerMillionUsd: 0
                    reasoning:
                      supported: true
                      effortLevels: [high, adaptive]
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
            static string WithUsage(string response) => response
                .Replace("\"input_tokens\":64,\"output_tokens\":16",
                    "\"input_tokens\":3,\"output_tokens\":2", StringComparison.Ordinal)
                .Replace("data: [DONE]",
                    "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\ndata: [DONE]",
                    StringComparison.Ordinal);
            provider.RespondWith((_, _) => WithUsage(ResponsesTextResponse("Principal context is ready.")));
            IProcessRuntime process = liveClaudeAcceptance
                ? new SystemProcessRuntime()
                : new FakeClaudeLaneRuntime(model, includeToolActivity: true);
            var runtime = OmniCliRuntime.Create(workspace);
            runtime.ProcessRuntimeFactoryForTests = () => process;
            runtime.ClaudeExecutableLocatorForTests = () => liveClaudeAcceptance
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe")
                : Path.Combine(workspace, "claude-fixture.exe");
            var host = new TuiTurnHost(runtime);
            var diagnostics = new List<string>();
            Assert.Equal(0, await host.ExecuteAsync("Prepare a delegated task", diagnostics.Add,
                TestContext.Current.CancellationToken));
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
            var modeAck = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
                {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,
                 "allowedModes":"plan,act,orq","maxAgents":2,"maxDepth":1,"maxTurns":8,
                 "maxToolCalls":12,"maxElapsedSeconds":900}
                """), TestContext.Current.CancellationToken);
            Assert.True(modeAck.Status == "ok", modeAck.Error ?? "Run mode selection was rejected.");
            var authorityRun = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<RunCreated>());
            var authority = RunProjection.Replay(authorityRun.SessionId, authorityRun.RunId, EventCodecs.Create(),
                ReadCurrentSessionJournalEvents(workspace)).ModeAuthority;
            Assert.Null(authority?.Authorization?.Limits.MaxSpendUsd);
            var root = Assert.Single(AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes);
            var request = new DelegationCreateRequest(profile, root.LastContextEventId!,
                liveClaudeAcceptance
                    ? "Create m7-lane-proof.txt in this worktree with exactly one line: isolated lane proof. Then report the result."
                    : "Inspect the owned lane and report a result",
                root.SelectableContextItemIds!, 8192, liveClaudeAcceptance ? 4 : 2,
                liveClaudeAcceptance ? 8 : 4, liveClaudeAcceptance ? 40_000 : 20_000, 1m);
            var queued = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()),
                TestContext.Current.CancellationToken);
            Assert.True(queued.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                queued.Error ?? queued.Outcome?.Reason ?? "Claude lane admission was not accepted.");
            var child = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes
                .Single(lane => lane.ParentTaskId is not null && lane.DelegationId is not null);
            var providerCalls = provider.RequestCount;
            var childExit = await host.ExecuteDelegationAsync(child.DelegationId!, diagnostics.Add,
                TestContext.Current.CancellationToken);
            Assert.True(childExit == 0, "Claude lane failed: " + string.Join(" | ", diagnostics)
                + "; process=" + (liveClaudeAcceptance ? "real" : "fake")
                + "; events=" + string.Join(",", ReadCurrentSessionEvents(workspace).Select(item => item.GetType().Name)));
            Assert.Equal(providerCalls + 1, provider.RequestCount); // one managed root continuation after the child returns
            var fakeLaunch = process is FakeClaudeLaneRuntime fakeProcess ? fakeProcess.LastLaunch : null;

            var events = ReadCurrentSessionEvents(workspace);
            var wakeEnvelopes = ReadCurrentSessionJournalEvents(workspace);
            var returnEnvelope = Assert.Single(wakeEnvelopes, envelope => EventCodecs.Create().Decode(envelope)
                is DelegationReturned item && item.DelegationId.ToString() == child.DelegationId);
            var wake = Assert.Single(events.OfType<WakeRequestCreated>(), item => item.Request.SourceEvent.EventId == returnEnvelope.EventId);
            var acceptedWake = Assert.Single(events.OfType<WakeRequestAccepted>(), item => item.WakeRequestId == wake.Request.WakeRequestId);
            var resolvedWake = Assert.Single(events.OfType<WakeRequestResolved>(), item => item.WakeRequestId == wake.Request.WakeRequestId);
            Assert.Equal(wake.Request.TargetExecutionId, acceptedWake.ExecutionId);
            Assert.Equal(wake.Request.TargetExecutionId, resolvedWake.ExecutionId);
            Assert.DoesNotContain(events.OfType<WakeRequestFailed>(), item => item.WakeRequestId == wake.Request.WakeRequestId);
            var worktree = Assert.Single(events.OfType<LaneWorktreeCreated>(), item => item.TaskId.ToString() == child.TaskId);
            var execution = Assert.Single(events.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is not null);
            var session = Assert.Single(events.OfType<ExternalLaneSessionBound>());
            var usage = Assert.Single(events.OfType<ExternalLaneUsageRecorded>());
            var result = Assert.Single(events.OfType<AgentResultProduced>(), item => item.ExecutionId == execution.ExecutionId);
            Assert.True(usage.UsageCoverageComplete);
            var resultArtifacts = OmniHost.CreateArtifactStore(
                OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace));
            Assert.True(resultArtifacts.Verify(result.ResultRef.Hash, result.ResultRef.Size));
            var resultText = resultArtifacts.GetText(result.ResultRef.Hash);
            Assert.False(string.IsNullOrWhiteSpace(resultText));
            Assert.NotEmpty(session.SessionId);
            if (liveClaudeAcceptance)
            {
                Assert.Equal(model, session.Model);
                var reservation = Assert.Single(events.OfType<ExternalLaneInvocationReserved>());
                Assert.Equal(1, reservation.AttemptNumber);
                Assert.Null(reservation.SessionId);
                Assert.Equal(0.5m, reservation.ReservedCostUsd);
                Assert.Equal(20_000L, reservation.ReservedTokens);
                Assert.Equal(2, reservation.ReservedTurns);
                Assert.Equal(4, reservation.ReservedToolCalls);
                Assert.InRange(usage.TotalCostUsd ?? decimal.MaxValue, 0m, reservation.ReservedCostUsd!.Value);
                Assert.InRange((usage.InputTokens ?? long.MaxValue) + (usage.OutputTokens ?? long.MaxValue),
                    0L, reservation.ReservedTokens!.Value);
                Assert.InRange(usage.ToolUseCount, 0, reservation.ReservedToolCalls);
                Assert.Equal("isolated lane proof\n", File.ReadAllText(Path.Combine(worktree.WorktreePath,
                    "m7-lane-proof.txt")));
                Assert.False(File.Exists(Path.Combine(workspace, "m7-lane-proof.txt")));
            }
            else
            {
                var launch = fakeLaunch ?? throw new InvalidOperationException("Fake process launch missing.");
                Assert.Equal(Path.GetFullPath(worktree.WorktreePath), Path.GetFullPath(launch.WorkingDirectory));
                Assert.Contains("Fixture lane completed.", resultText);
                Assert.Contains("--strict-mcp-config", launch.Args);
                Assert.Contains("--setting-sources", launch.Args);
                Assert.Contains("--safe-mode", launch.Args);
                Assert.Contains("--restricted", launch.Args);
                Assert.Contains("--permission-mode", launch.Args);
                Assert.Contains("dontAsk", launch.Args);
                Assert.Contains("--json-schema", launch.Args);
                Assert.Equal("claude-fixture-session", session.SessionId);
                var reservation = Assert.Single(events.OfType<ExternalLaneInvocationReserved>());
                Assert.Equal((0.5m, 10_000L, 1, 2), (reservation.ReservedCostUsd,
                    reservation.ReservedTokens, reservation.ReservedTurns, reservation.ReservedToolCalls));
                Assert.Equal(1, ExternalLaneAccounting.ToolUseCount([usage]));
                var runCreated = Assert.Single(events.OfType<RunCreated>());
                var runBudget = RunTokenBudgetReader.Read(ReadCurrentSessionJournalEvents(workspace), EventCodecs.Create(),
                    runCreated.RunId, 8192);
                Assert.Equal(26L, runBudget.ObservedSettledTokens); // includes the root wake generation
                Assert.Equal(8166L, runBudget.Remaining);
                Assert.Null(runBudget.Limitation);
            }
            Assert.Equal(session.ExecutionId, execution.ExecutionId);
            Assert.Equal(session.ExecutionId, usage.ExecutionId);
            Assert.Equal(session.ExecutionId, result.ExecutionId);
            Assert.Equal(ClaudeLaneAgentResultValidator.SchemaId, result.ResultSchemaId);
            var envelopes = ReadCurrentSessionJournalEvents(workspace).ToArray();
            Assert.True(envelopes.Single(item => EventCodecs.Create().Decode(item) is ExternalLaneSessionBound).Sequence
                < envelopes.Single(item => EventCodecs.Create().Decode(item) is ExternalLaneUsageRecorded).Sequence);
            Assert.True(envelopes.Single(item => EventCodecs.Create().Decode(item) is ExternalLaneUsageRecorded).Sequence
                < envelopes.Single(item => EventCodecs.Create().Decode(item) is AgentResultProduced).Sequence);
            Assert.Equal("external lane fixture\n", File.ReadAllText(Path.Combine(workspace, "README.md")));
            Assert.True(Directory.Exists(worktree.WorktreePath));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Claude_dispatcher_resumes_bound_session_without_claiming_uncertain_usage(bool resumeCompletes)
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            const string profile = "0199a000-0000-7000-8000-000000000033";
            const string model = "claude-haiku-5-5";
            InitializeGitRepository(workspace);
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), $$"""
                defaultProfile: {{profile}}
                agentProfiles:
                  claude-writer:
                    id: {{profile}}
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: ["**"]
                      process: []
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read, filesystem.write, filesystem.patch]
                    executor: claude-code
                    externalModel: {{model}}
                """);
            using (var policies = OmniHost.CreateModelPolicyService(data))
                policies.Set(ModelPolicyKey.For("claude-code", model), 0, ModelPolicyPresets.FullAgent(),
                    TestContext.Current.CancellationToken);
            SetSourceModelFullAgent(data);
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  scripted-model:
                    provider: scripted
                    context: 8192
                    maxOutput: 2048
                    inputPricePerMillionUsd: 0
                    outputPricePerMillionUsd: 0
                    reasoning:
                      supported: true
                      effortLevels: [high, adaptive]
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
            provider.RespondWith((_, _) => ResponsesTextResponse("Root context ready."));
            var firstProcess = new FakeClaudeLaneRuntime(model, failAfterInit: true);
            var runtime = OmniCliRuntime.Create(workspace);
            runtime.ProcessRuntimeFactoryForTests = () => firstProcess;
            runtime.ClaudeExecutableLocatorForTests = () => Path.Combine(workspace, "claude-fixture.exe");
            var host = new TuiTurnHost(runtime);
            Assert.Equal(0, await host.ExecuteAsync("Prepare resumable lane", _ => { },
                TestContext.Current.CancellationToken));
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
            var modeAck = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
                {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,
                 "allowedModes":"plan,act,orq","maxAgents":2,"maxDepth":1,"maxTurns":8,
                 "maxToolCalls":12,"maxElapsedSeconds":900}
                """), TestContext.Current.CancellationToken);
            Assert.True(modeAck.Status == "ok", modeAck.Error ?? "Run mode selection was rejected.");
            var rootLane = Assert.Single(AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes);
            var runCreated = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<RunCreated>());
            var authority = RunProjection.Replay(runCreated.SessionId, runCreated.RunId, EventCodecs.Create(),
                ReadCurrentSessionJournalEvents(workspace)).ModeAuthority;
            Assert.Null(authority?.Authorization?.Limits.MaxSpendUsd);
            var request = new DelegationCreateRequest(profile, rootLane.LastContextEventId!,
                "Resume the owned lane and return a result", rootLane.SelectableContextItemIds!, 8192, 2, 4, 20_000, 1m);
            var queued = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, queued.Outcome?.Kind);
            var child = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes
                .Single(item => item.ParentTaskId is not null && item.DelegationId is not null);

            Assert.NotEqual(0, await host.ExecuteDelegationAsync(child.DelegationId!, _ => { },
                TestContext.Current.CancellationToken));
            var firstLaunch = Assert.IsType<ProcessLaunch>(firstProcess.LastLaunch);
            Assert.DoesNotContain("--resume", firstLaunch.Args);
            var firstEvents = ReadCurrentSessionEvents(workspace);
            var bound = Assert.Single(firstEvents.OfType<ExternalLaneSessionBound>());
            Assert.Equal("claude-fixture-session", bound.SessionId);
            var firstReservation = Assert.Single(firstEvents.OfType<ExternalLaneInvocationReserved>());
            Assert.Equal((1, (string?)null, 0.5m, 10_000L, 1, 2),
                (firstReservation.AttemptNumber, firstReservation.SessionId, firstReservation.ReservedCostUsd,
                    firstReservation.ReservedTokens, firstReservation.ReservedTurns, firstReservation.ReservedToolCalls));
            var childStartedEnvelopes = ReadCurrentSessionJournalEvents(workspace)
                .Where(item => item.LaneId == bound.LaneId && item.ExecutionId == bound.ExecutionId).ToArray();
            Assert.Single(childStartedEnvelopes.Select(EventCodecs.Create().Decode).OfType<ModelStepStarted>());
            Assert.Empty(childStartedEnvelopes.Select(EventCodecs.Create().Decode).OfType<ModelStepCompleted>());
            runtime.Dispose();

            var resumedProcess = new FakeClaudeLaneRuntime(model, failAfterInit: !resumeCompletes);
            var reopened = OmniCliRuntime.Create(workspace);
            reopened.ProcessRuntimeFactoryForTests = () => resumedProcess;
            reopened.ClaudeExecutableLocatorForTests = () => Path.Combine(workspace, "claude-fixture.exe");
            var reopenedHost = new TuiTurnHost(reopened);
            var resumedDiagnostics = new List<string>();
            var resumedExit = await reopenedHost.ExecuteDelegationAsync(child.DelegationId!, resumedDiagnostics.Add,
                TestContext.Current.CancellationToken);
            var resumedLaunch = Assert.IsType<ProcessLaunch>(resumedProcess.LastLaunch);
            Assert.Contains("--resume", resumedLaunch.Args);
            var resumedArgs = resumedLaunch.Args.ToList();
            Assert.Equal("claude-fixture-session", resumedArgs[resumedArgs.IndexOf("--resume") + 1]);
            Assert.Equal("0.5", resumedArgs[resumedArgs.IndexOf("--max-budget-usd") + 1]);
            Assert.Equal("1", resumedArgs[resumedArgs.IndexOf("--max-turns") + 1]);
            var events = ReadCurrentSessionEvents(workspace);
            Assert.Collection(events.OfType<ExternalLaneInvocationReserved>().OrderBy(item => item.AttemptNumber),
                first => Assert.Equal(1, first.AttemptNumber), second =>
                {
                    Assert.Equal(2, second.AttemptNumber);
                    Assert.Equal(bound.SessionId, second.SessionId);
                    Assert.Equal((0.5m, 10_000L, 1, 2), (second.ReservedCostUsd, second.ReservedTokens,
                        second.ReservedTurns, second.ReservedToolCalls));
                });
            if (!resumeCompletes)
            {
                Assert.NotEqual(0, resumedExit);
                Assert.Empty(events.OfType<ExternalLaneUsageRecorded>());
                Assert.Empty(ReadCurrentSessionJournalEvents(workspace)
                    .Where(item => item.LaneId == bound.LaneId && item.ExecutionId == bound.ExecutionId)
                    .Select(EventCodecs.Create().Decode).OfType<ModelStepCompleted>());
                reopened.Dispose();
                var thirdProcess = new FakeClaudeLaneRuntime(model);
                var thirdRuntime = OmniCliRuntime.Create(workspace);
                thirdRuntime.ProcessRuntimeFactoryForTests = () => thirdProcess;
                thirdRuntime.ClaudeExecutableLocatorForTests = () => Path.Combine(workspace, "claude-fixture.exe");
                var thirdHost = new TuiTurnHost(thirdRuntime);
                Assert.NotEqual(0, await thirdHost.ExecuteDelegationAsync(child.DelegationId!, _ => { },
                    TestContext.Current.CancellationToken));
                Assert.Null(thirdProcess.LastLaunch);
                Assert.Equal(2, ReadCurrentSessionEvents(workspace).OfType<ExternalLaneInvocationReserved>().Count());
                thirdRuntime.Dispose();
                return;
            }
            Assert.True(resumedExit == 0, string.Join(" | ", resumedDiagnostics)
                + "; wakes=" + string.Join(",", ReadCurrentSessionEvents(workspace)
                    .Where(item => item is WakeRequestCreated or WakeRequestAccepted or WakeRequestResolved or WakeRequestFailed)
                    .Select(item => item.GetType().Name)));
            Assert.True(provider.RequestCount == 2, "Expected initial root plus one continuation; calls="
                + provider.RequestCount + "; wake events=" + string.Join(",", events
                    .Where(item => item is WakeRequestCreated or WakeRequestAccepted or WakeRequestResolved or WakeRequestFailed)
                    .Select(item => item.ToString())) + "; run events=" + string.Join(",", events
                    .Where(item => item is RunCompleted or RunFailed or TurnStarted or TurnCompleted or TurnAbandoned)
                    .Select(item => item.ToString())));
            var returnedEnvelope = Assert.Single(ReadCurrentSessionJournalEvents(workspace), envelope =>
                EventCodecs.Create().Decode(envelope) is DelegationReturned returned
                    && returned.DelegationId.ToString() == child.DelegationId);
            var wake = Assert.Single(events.OfType<WakeRequestCreated>(), item =>
                item.Request.SourceEvent.EventId == returnedEnvelope.EventId);
            var acceptedWake = Assert.Single(events.OfType<WakeRequestAccepted>(), item =>
                item.WakeRequestId == wake.Request.WakeRequestId);
            var resolvedWake = Assert.Single(events.OfType<WakeRequestResolved>(), item =>
                item.WakeRequestId == wake.Request.WakeRequestId);
            Assert.Equal(wake.Request.TargetExecutionId, acceptedWake.ExecutionId);
            Assert.Equal(wake.Request.TargetExecutionId, resolvedWake.ExecutionId);
            Assert.DoesNotContain(events.OfType<WakeRequestFailed>(), item =>
                item.WakeRequestId == wake.Request.WakeRequestId);
            var usage = Assert.Single(events.OfType<ExternalLaneUsageRecorded>());
            Assert.Equal(bound.SessionId, usage.SessionId);
            Assert.False(usage.UsageCoverageComplete);
            var childCompletedEnvelopes = ReadCurrentSessionJournalEvents(workspace)
                .Where(item => item.LaneId == bound.LaneId && item.ExecutionId == bound.ExecutionId).ToArray();
            Assert.Single(childCompletedEnvelopes.Select(EventCodecs.Create().Decode).OfType<TurnCompleted>());
            Assert.Single(childCompletedEnvelopes.Select(EventCodecs.Create().Decode).OfType<ModelStepCompleted>());

            var envelopes = ReadCurrentSessionJournalEvents(workspace);
            var run = Assert.Single(events.OfType<RunCreated>()).RunId;
            var budget = RunTokenBudgetReader.Read(envelopes, EventCodecs.Create(), run, 8192);
            Assert.Null(budget.Remaining);
            Assert.Equal("generation attempt usage coverage unknown", budget.Limitation);
            Assert.Null(budget.ObservedSettledTokens);
            var duplicateDiagnostics = new List<string>();
            var duplicateExit = await reopenedHost.ExecuteDelegationAsync(child.DelegationId!, duplicateDiagnostics.Add,
                TestContext.Current.CancellationToken);
            Assert.True(duplicateExit == 0, string.Join(" | ", duplicateDiagnostics));
            Assert.Equal(resumedLaunch, resumedProcess.LastLaunch);
            reopened.Dispose();
        });
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("revise")]
    public async Task Lane_acceptance_after_reopen_revalidates_tree_and_never_relaunches_executor(string option)
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            const string profile = "0199a000-0000-7000-8000-000000000032";
            const string model = "claude-haiku-5-5";
            InitializeGitRepository(workspace);
            Directory.CreateDirectory(Path.Combine(workspace, ".omnicore"));
            File.WriteAllText(Path.Combine(workspace, ".omnicore", "settings.yaml"),
                "gates:\n  acceptance: true\n");
            var workspaceTrust = await Run("trust");
            Assert.Equal(0, workspaceTrust.Code);
            Assert.Contains("confiable", workspaceTrust.Output, StringComparison.OrdinalIgnoreCase);
            Assert.True(new WorkspaceTrustStore(OmniHost.CreatePlatformPaths()).IsTrusted(workspace));
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), $$"""
                defaultProfile: {{profile}}
                agentProfiles:
                  claude-writer:
                    id: {{profile}}
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: ["**"]
                      process: []
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read, filesystem.write, filesystem.patch]
                    executor: claude-code
                    externalModel: {{model}}
                """);
            using (var policies = OmniHost.CreateModelPolicyService(data))
                policies.Set(ModelPolicyKey.For("claude-code", model), 0, ModelPolicyPresets.FullAgent(),
                    TestContext.Current.CancellationToken);
            SetSourceModelFullAgent(data);
            File.AppendAllText(Path.Combine(config, "models.yaml"),
                "    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n");
            provider.RespondWith((_, _) => UsageTextResponse("Parent is ready."));
            var firstProcess = new FakeClaudeLaneRuntime(model);
            var runtime = OmniCliRuntime.Create(workspace);
            runtime.ProcessRuntimeFactoryForTests = () => firstProcess;
            runtime.ClaudeExecutableLocatorForTests = () => Path.Combine(workspace, "claude-fixture.exe");
            var host = new TuiTurnHost(runtime);
            Assert.Equal(0, await host.ExecuteAsync("Prepare acceptance lane", _ => { }, TestContext.Current.CancellationToken));
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
            var modeAck = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
                {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,
                 "allowedModes":"plan,act,orq","maxAgents":2,"maxDepth":1,"maxTurns":8,
                 "maxToolCalls":12,"maxElapsedSeconds":900,"maxSpendUsd":3}
                """), TestContext.Current.CancellationToken);
            Assert.True(modeAck.Status == "ok", modeAck.Error ?? "Run mode selection was rejected.");
            var rootLane = Assert.Single(AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes);
            var request = new DelegationCreateRequest(profile, rootLane.LastContextEventId!,
                "Inspect the lane and return a short result", rootLane.SelectableContextItemIds!,
                8192, 2, 4, 20_000, 1m);
            var queued = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()), TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, queued.Outcome?.Kind);
            var child = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes
                .Single(item => item.ParentTaskId is not null && item.DelegationId is not null);
            var firstExit = await host.ExecuteDelegationAsync(child.DelegationId!, _ => { }, TestContext.Current.CancellationToken);
            Assert.Equal(3, firstExit);
            var firstEvents = ReadCurrentSessionEvents(workspace);
            var pending = Assert.Single(firstEvents.OfType<LaneAcceptancePending>());
            var worktree = Assert.Single(firstEvents.OfType<LaneWorktreeCreated>(), item => item.LaneId == pending.LaneId);
            Assert.NotNull(firstProcess.LastLaunch);

            runtime.Dispose();
            var resumedProcess = new FakeClaudeLaneRuntime(model);
            var reopened = OmniCliRuntime.Create(workspace);
            reopened.ProcessRuntimeFactoryForTests = () => resumedProcess;
            reopened.ClaudeExecutableLocatorForTests = () => Path.Combine(workspace, "claude-fixture.exe");
            var reopenedHost = new TuiTurnHost(reopened);
            var reopenedClient = reopened.Connect(TestContext.Current.CancellationToken);
            var reopenedTrusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(reopenedClient);

            var laneOutput = Path.Combine(worktree.WorktreePath, "later-edit.txt");
            File.WriteAllText(laneOutput, "changed after acceptance prompt\n");
            var staleAccept = reopenedTrusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{"
                + "\"cmd\":\"interaction.respond\",\"interactionId\":\"" + pending.InteractionId
                + "\",\"optionId\":\"accept\"}"), TestContext.Current.CancellationToken);
            Assert.Equal("error", staleAccept.Status);
            Assert.DoesNotContain(ReadCurrentSessionEvents(workspace), item => item is InteractionResolved resolved
                && resolved.InteractionId == pending.InteractionId);

            if (option == "revise")
            {
                var revise = reopenedTrusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{"
                    + "\"cmd\":\"interaction.respond\",\"interactionId\":\"" + pending.InteractionId
                    + "\",\"optionId\":\"revise\"}"), TestContext.Current.CancellationToken);
                Assert.Equal("ok", revise.Status);
                Assert.Equal("revise", Assert.Single(ReadCurrentSessionEvents(workspace).OfType<InteractionResolved>(),
                    item => item.InteractionId == pending.InteractionId).OptionId);
                Assert.NotEqual(0, await reopenedHost.ExecuteDelegationAsync(child.DelegationId!, _ => { },
                    TestContext.Current.CancellationToken));
                Assert.Null(resumedProcess.LastLaunch);
                reopened.Dispose();
                return;
            }

            File.Delete(laneOutput);

            var accept = reopenedTrusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{"
                + "\"cmd\":\"interaction.respond\",\"interactionId\":\"" + pending.InteractionId
                + "\",\"optionId\":\"accept\"}"), TestContext.Current.CancellationToken);
            Assert.Equal("ok", accept.Status);
            Assert.Equal("accept", Assert.Single(ReadCurrentSessionEvents(workspace).OfType<InteractionResolved>(),
                item => item.InteractionId == pending.InteractionId).OptionId);

            Assert.Equal(0, await reopenedHost.ExecuteDelegationAsync(child.DelegationId!, _ => { },
                TestContext.Current.CancellationToken));
            Assert.Null(resumedProcess.LastLaunch);
            reopened.Dispose();
        });
    }

    [Fact]
    public async Task User_filesystem_write_deny_blocks_external_lane_before_process_launch()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            const string profile = "0199a000-0000-7000-8000-000000000041";
            const string model = "claude-haiku-5-5";
            InitializeGitRepository(workspace);
            File.WriteAllText(Path.Combine(config, "permissions.yaml"), "profile: autonomous\nrules:\n  filesystem.write: deny\n");
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), $$"""
                defaultProfile: {{profile}}
                agentProfiles:
                  claude-writer:
                    id: {{profile}}
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: ["**"]
                      process: []
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read, filesystem.write, filesystem.patch]
                    executor: claude-code
                    externalModel: {{model}}
                """);
            using (var policies = OmniHost.CreateModelPolicyService(data))
                policies.Set(ModelPolicyKey.For("claude-code", model), 0, ModelPolicyPresets.FullAgent(),
                    TestContext.Current.CancellationToken);
            SetSourceModelFullAgent(data);
            File.AppendAllText(Path.Combine(config, "models.yaml"),
                "    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n");
            provider.RespondWith((_, _) => UsageTextResponse("root context"));
            var process = new FakeClaudeLaneRuntime(model);
            var runtime = OmniCliRuntime.Create(workspace);
            runtime.ProcessRuntimeFactoryForTests = () => process;
            runtime.ClaudeExecutableLocatorForTests = () => Path.Combine(workspace, "claude-fixture.exe");
            var host = new TuiTurnHost(runtime);
            var diagnostics = new List<string>();
            Assert.Equal(0, await host.ExecuteAsync("Prepare a delegated task", diagnostics.Add,
                TestContext.Current.CancellationToken));
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
            var modeAck = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
                {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,
                 "allowedModes":"plan,act,orq","maxAgents":2,"maxDepth":1,"maxTurns":8,
                 "maxToolCalls":12,"maxElapsedSeconds":900,"maxSpendUsd":3}
                """), TestContext.Current.CancellationToken);
            Assert.True(modeAck.Status == "ok", modeAck.Error ?? "Run mode selection was rejected.");
            var root = Assert.Single(AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes);
            var request = new DelegationCreateRequest(profile, root.LastContextEventId!,
                "write one file", root.SelectableContextItemIds!, 8192, 2, 4, 20_000, 1m);
            var queued = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()),
                TestContext.Current.CancellationToken);
            Assert.True(queued.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                queued.Error ?? queued.Outcome?.Reason ?? "Writer lane admission was not accepted.");
            var child = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes
                .Single(lane => lane.ParentTaskId is not null && lane.DelegationId is not null);
            var exit = await runtime.DelegationAsync(child.DelegationId!, diagnostics.Add,
                TestContext.Current.CancellationToken);
            Assert.NotEqual(0, exit);
            Assert.Null(process.LastLaunch);
            Assert.Contains(diagnostics, item => item.Contains("ExternalLanePermissionDenied:filesystem.write:Deny",
                StringComparison.Ordinal));
            Assert.Empty(ReadCurrentSessionEvents(workspace).OfType<ExternalLaneSessionBound>());
        });
    }

    [Fact]
    public async Task Tui_dispatches_managed_read_only_child_through_actual_HTTP_context_profile_and_result_boundaries()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            const string profile = "0199a000-0000-7000-8000-000000000001";
            File.AppendAllText(Path.Combine(config, "models.yaml"), "    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n");
            SetSourceModelFullAgent(data);
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
            Assert.Equal(4, provider.RequestCount); // two child requests plus one root wake after the initial root request
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
            var envelopes = ReadCurrentSessionJournalEvents(workspace);
            var returnEnvelope = Assert.Single(envelopes, envelope => EventCodecs.Create().Decode(envelope)
                is DelegationReturned item && item.DelegationId.ToString() == child.DelegationId);
            var wake = Assert.Single(events.OfType<WakeRequestCreated>(), item => item.Request.SourceEvent.EventId == returnEnvelope.EventId);
            var acceptedWake = Assert.Single(events.OfType<WakeRequestAccepted>(), item => item.WakeRequestId == wake.Request.WakeRequestId);
            var resolvedWake = Assert.Single(events.OfType<WakeRequestResolved>(), item => item.WakeRequestId == wake.Request.WakeRequestId);
            Assert.Equal(wake.Request.TargetExecutionId, acceptedWake.ExecutionId);
            Assert.Equal(wake.Request.TargetExecutionId, resolvedWake.ExecutionId);
            Assert.Empty(events.OfType<ResultDispositionRecorded>());
            Assert.DoesNotContain(events.OfType<TaskCompleted>(), e => e.TaskId.ToString() == child.TaskId);
            var snapshot = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
            var returnedLane = snapshot.Lanes.Single(e => e.DelegationId == child.DelegationId);
            Assert.Equal(produced.ResultRef.Id.ToString(), returnedLane.ResultId);
            Assert.Contains("Resultado de lectura", returnedLane.ResultSummary!);
            Assert.Equal(1, returnedLane.ResultIssueCount);
            Assert.Null(returnedLane.ResultDisposition);
            var accept = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"delegation.disposition\",\"delegationId\":\""
                + child.DelegationId + "\",\"resultId\":\"" + returnedLane.ResultId + "\",\"outcome\":\"Accepted\",\"reason\":\"Leído y aceptado\"}"), TestContext.Current.CancellationToken);
            Assert.Equal("error", accept.Status);
            var rework = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"delegation.disposition\",\"delegationId\":\""
                + child.DelegationId + "\",\"resultId\":\"" + returnedLane.ResultId + "\",\"outcome\":\"ReworkRequested\",\"reason\":\"Tool scope lacked read-only compatibility\"}"), TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, rework.Outcome?.Kind);
            Assert.Equal("Blocked", AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes.Single(e => e.DelegationId == child.DelegationId).TaskState);
        });
    }

    [Fact]
    public async Task Runtime_runs_two_delegations_on_one_host_and_defers_root_writer_behind_live_readers()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            const string profile = "0199a000-0000-7000-8000-000000000021";
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  scripted-model:
                    provider: scripted
                    context: 8192
                    maxOutput: 2048
                    inputPricePerMillionUsd: 0
                    outputPricePerMillionUsd: 0
                    reasoning:
                      supported: true
                      effortLevels: [high, adaptive]
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
            SetSourceModelFullAgent(data);
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
            var releaseFirstReader = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecondReader = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bothReadersAtProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReaderAtProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var principalReachedProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            static string WithUsage(string response) => response;
            provider.RespondWithAsync(async (index, body) =>
            {
                if (index == 0) return WithUsage(ResponsesTextResponse("stable root context"));
                if (index >= 3 && body.Contains("Managed worker result", StringComparison.Ordinal))
                {
                    principalReachedProvider.TrySetResult(true);
                    return WithUsage(ResponsesTextResponse("root writer reached provider"));
                }
                readersEntered.Signal();
                if (index == 1) firstReaderAtProvider.TrySetResult(true);
                if (readersEntered.CurrentCount == 0) bothReadersAtProvider.TrySetResult(true);
                var release = index == 1 ? releaseFirstReader : releaseSecondReader;
                try { await release.Task.WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken); }
                catch (TimeoutException)
                {
                    return WithUsage(ResponsesTextResponse("reader fixture timeout"));
                }
                return WithUsage(ResponsesTextResponse("reader completed " + index));
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
                "maxToolCalls":20,"maxElapsedSeconds":3600}
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
                try
                {
                    await firstReaderAtProvider.Task.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException("First reader never reached the provider; requests="
                        + provider.RequestCount + "; task completed=" + first.IsCompleted
                        + "; diagnostics=" + string.Join(" | ", firstDiagnostics)
                        + "; events=" + string.Join(" | ", ReadCurrentSessionEvents(workspace).TakeLast(20)
                            .Select(item => item.GetType().Name + ":" + item)));
                }
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
                releaseSecondReader.TrySetResult(true);
                var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                // The child return is durable, but its root wake is deferred while the
                // first reader still owns capacity. Do not accept or invoke root yet.
                Assert.NotEqual(0, secondResult);
                var pendingWakeEvents = ReadCurrentSessionEvents(workspace);
                Assert.Single(pendingWakeEvents.OfType<DelegationReturned>(),
                    item => item.DelegationId.ToString() == secondId);
                var pendingEnvelopes = ReadCurrentSessionJournalEvents(workspace);
                var secondReturnEnvelope = Assert.Single(pendingEnvelopes, envelope => EventCodecs.Create().Decode(envelope)
                    is DelegationReturned item && item.DelegationId.ToString() == secondId);
                var pendingWake = Assert.Single(pendingWakeEvents.OfType<WakeRequestCreated>(),
                    item => item.Request.SourceEvent.EventId == secondReturnEnvelope.EventId);
                Assert.DoesNotContain(pendingWakeEvents.OfType<WakeRequestAccepted>(),
                    item => item.WakeRequestId == pendingWake.Request.WakeRequestId);
                Assert.DoesNotContain(pendingWakeEvents.OfType<WakeRequestResolved>(),
                    item => item.WakeRequestId == pendingWake.Request.WakeRequestId);
                Assert.Equal(1, AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!
                    .Capacity!.Active);

                firstCancellation.Cancel();
                releaseFirstReader.TrySetResult(true);
                var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                Assert.NotEqual(0, firstResult);
                var afterReaders = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
                Assert.Contains(afterReaders.Lanes, item => item.DelegationId == firstId
                    && item.ExecutionState == "Started" && item.DelegationState == "Accepted"
                    && item.Heartbeat?.State == "Cancelled");
                Assert.False(principalReachedProvider.Task.IsCompleted);
                Assert.Equal(3, provider.RequestCount);
                Assert.Equal(0, await runtime.DelegationAsync(secondId, secondDiagnostics.Enqueue,
                    TestContext.Current.CancellationToken));
                Assert.True(principalReachedProvider.Task.IsCompleted,
                    "Root continuation was not invoked after capacity release; provider requests="
                    + provider.RequestCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "; diagnostics=" + string.Join(" | ", secondDiagnostics)
                    + "; wake events=" + string.Join(" | ", ReadCurrentSessionEvents(workspace)
                        .Where(item => item is WakeRequestCreated or WakeRequestAccepted or WakeRequestResolved or WakeRequestFailed)
                        .Select(item => item is WakeRequestFailed failed ? failed.GetType().Name + ":" + failed.Reason
                            : item.GetType().Name)
                        .Concat(ReadCurrentSessionEvents(workspace).OfType<TurnAbandoned>()
                            .Select(item => "TurnAbandoned:" + item.Reason))));
                Assert.Equal(4, provider.RequestCount);
                var resumedEvents = ReadCurrentSessionEvents(workspace);
                var acceptedWake = Assert.Single(resumedEvents.OfType<WakeRequestAccepted>(),
                    item => item.WakeRequestId == pendingWake.Request.WakeRequestId);
                var resolvedWake = Assert.Single(resumedEvents.OfType<WakeRequestResolved>(),
                    item => item.WakeRequestId == pendingWake.Request.WakeRequestId);
                Assert.Equal(pendingWake.Request.TargetExecutionId, acceptedWake.ExecutionId);
                Assert.Equal(pendingWake.Request.TargetExecutionId, resolvedWake.ExecutionId);
                Assert.Single(resumedEvents.OfType<WakeRequestCreated>(),
                    item => item.Request.WakeRequestId == pendingWake.Request.WakeRequestId);
                var final = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
                Assert.Equal(rootRun, final.RunId);
                Assert.NotEqual(rootContext, Assert.Single(final.Lanes, lane => lane.ParentTaskId is null).LastContextEventId);
            }
            finally
            {
                firstCancellation.Cancel();
                secondCancellation.Cancel();
                releaseFirstReader.TrySetResult(true);
                releaseSecondReader.TrySetResult(true);
                var workers = new[] { first, second }.Where(task => task is not null).Cast<Task<int>>().ToArray();
                try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception) { }
            }
        });
    }

    [Fact]
    public async Task Runtime_executes_two_writers_concurrently_in_distinct_owned_worktrees()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            const string profile = "0199a000-0000-7000-8000-000000000022";
            const string model = "claude-haiku-5-5";
            InitializeGitRepository(workspace);
            File.AppendAllText(Path.Combine(config, "models.yaml"),
                "    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n");
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), $$"""
                defaultProfile: {{profile}}
                agentProfiles:
                  writer:
                    id: {{profile}}
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: ["**"]
                      process: []
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read, filesystem.write, filesystem.patch]
                    executor: claude-code
                    externalModel: {{model}}
                """);
            using (var policies = OmniHost.CreateModelPolicyService(data))
                policies.Set(ModelPolicyKey.For("claude-code", model), 0, ModelPolicyPresets.FullAgent(),
                    TestContext.Current.CancellationToken);
            SetSourceModelFullAgent(data);
            static string WithUsage(string response) => response.Replace("data: [DONE]",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\ndata: [DONE]",
                StringComparison.Ordinal);
            provider.RespondWith((_, _) => WithUsage(TextResponse("root context for isolated writers")));
            var laneRuntime = new ConcurrentFakeClaudeLaneRuntime(model);

            var runtime = OmniCliRuntime.Create(workspace);
            runtime.ProcessRuntimeFactoryForTests = () => laneRuntime;
            runtime.ClaudeExecutableLocatorForTests = () => Path.Combine(workspace, "claude-fixture.exe");
            var host = new TuiTurnHost(runtime);
            var diagnostics = new List<string>();
            Assert.Equal(0, await host.ExecuteAsync("ROOT CONTEXT: isolated writer test", diagnostics.Add,
                TestContext.Current.CancellationToken));
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
            Assert.Equal("ok", trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
                {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,
                 "allowedModes":"plan,act,orq","maxAgents":3,"maxDepth":1,"maxTurns":12,
                 "maxToolCalls":12,"maxElapsedSeconds":600,"maxSpendUsd":3}
                """), TestContext.Current.CancellationToken).Status);
            var rootLane = Assert.Single(AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!.Lanes);
            string Admit(string objective)
            {
                var request = new DelegationCreateRequest(profile, rootLane.LastContextEventId!, objective,
                    rootLane.SelectableContextItemIds!, 8192, 2, 4, 20_000, 1m);
                var admitted = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()),
                    TestContext.Current.CancellationToken);
                Assert.True(admitted.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                    admitted.Error ?? admitted.Outcome?.Reason ?? "Writer lane admission was not accepted.");
                return AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!
                    .Lanes.Single(lane => lane.Objective == objective).DelegationId!;
            }
            var firstId = Admit("write lane-a.txt");
            var secondId = Admit("write lane-b.txt");
            using var firstCancellation = new CancellationTokenSource();
            using var secondCancellation = new CancellationTokenSource();
            var childDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var first = Task.Run(() => runtime.DelegationAsync(firstId, childDiagnostics.Enqueue, firstCancellation.Token));
            Task<int>? second = null;
            try
            {
                try
                {
                    await laneRuntime.FirstLaunched.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                    second = Task.Run(() => runtime.DelegationAsync(secondId, childDiagnostics.Enqueue, secondCancellation.Token));
                    await laneRuntime.BothLaunched.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException("Distinct writers did not reach external execution concurrently; calls="
                        + provider.RequestCount + "; child diagnostics=" + string.Join(" | ", childDiagnostics)
                        + "; events=" + string.Join(",", ReadCurrentSessionEvents(workspace).TakeLast(20)
                            .Select(item => item.GetType().Name)));
                }
                var live = AgentsJson.Decode(client.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
                Assert.Equal(2, live.Capacity!.Active);
                Assert.Equal(3, live.Capacity.Maximum);
                Assert.True(live.Capacity.WriterActive);
                Assert.Equal(1, provider.RequestCount); // child lanes use the isolated external-runtime fixture
                laneRuntime.ReleaseWriters.TrySetResult(true);
                var childResults = await Task.WhenAll(first, second!).WaitAsync(TimeSpan.FromSeconds(35),
                    TestContext.Current.CancellationToken);
                var writerIds = new[] { firstId, secondId };
                for (var index = 0; index < childResults.Length; index++)
                {
                    if (childResults[index] == 0) continue;
                    Assert.Equal(0, await runtime.DelegationAsync(writerIds[index], childDiagnostics.Enqueue,
                        TestContext.Current.CancellationToken));
                }
                Assert.Equal(3, provider.RequestCount); // initial context plus one admitted root generation per returned writer
                var wakeEvents = ReadCurrentSessionEvents(workspace);
                var returnedDelegations = wakeEvents.OfType<DelegationReturned>().ToArray();
                Assert.Equal(2, returnedDelegations.Length);
                var wakeEnvelopes = ReadCurrentSessionJournalEvents(workspace);
                foreach (var childReturn in returnedDelegations)
                {
                    var returnEnvelope = Assert.Single(wakeEnvelopes, envelope => EventCodecs.Create().Decode(envelope)
                        is DelegationReturned item && item.DelegationId == childReturn.DelegationId);
                    var request = Assert.Single(wakeEvents.OfType<WakeRequestCreated>(), item =>
                        item.Request.SourceEvent.EventId == returnEnvelope.EventId);
                    Assert.Single(wakeEvents.OfType<WakeRequestAccepted>(), item =>
                        item.WakeRequestId == request.Request.WakeRequestId);
                    Assert.Single(wakeEvents.OfType<WakeRequestResolved>(), item =>
                        item.WakeRequestId == request.Request.WakeRequestId);
                }
            }
            finally
            {
                firstCancellation.Cancel();
                secondCancellation.Cancel();
                laneRuntime.ReleaseWriters.TrySetResult(true);
                try
                {
                    if (second is null) await first.WaitAsync(TimeSpan.FromSeconds(10));
                    else await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception) { }
            }

            var events = ReadCurrentSessionEvents(workspace);
            var createdWorktrees = events.OfType<LaneWorktreeCreated>()
                .Where(item => item.LaneId.ToString() != rootLane.LaneId).ToArray();
            Assert.Equal(2, createdWorktrees.Length);
            Assert.NotEqual(Path.GetFullPath(createdWorktrees[0].WorktreePath),
                Path.GetFullPath(createdWorktrees[1].WorktreePath));
            Assert.Equal(2, laneRuntime.Launches.Count);
            foreach (var launch in laneRuntime.Launches)
            {
                Assert.NotEqual(Path.GetFullPath(workspace), Path.GetFullPath(launch.WorkingDirectory!));
                Assert.Contains(createdWorktrees, item => Path.GetFullPath(item.WorktreePath)
                    == Path.GetFullPath(launch.WorkingDirectory!));
                Assert.True(File.Exists(Path.Combine(launch.WorkingDirectory!, "external-writer.txt")));
            }
            Assert.False(File.Exists(Path.Combine(workspace, "external-writer.txt")));
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
            SetSessionSecurityFullAgent();
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
            const string originalProgram = "public static class Program\n{\n    public static int Main()\n    {\n        /* FEATURE_MODULE_HOOK */\n        return FeatureModule.IsInitialized ? 0 : 1;\n    }\n}\n";
            File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), originalProgram);
            File.WriteAllText(Path.Combine(projectDirectory, "FeatureApp.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <Nullable>enable</Nullable>
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
            File.WriteAllText(Path.Combine(workspace, ".gitignore"), "**/bin/\n**/obj/Debug/\n**/obj/Release/\n");
            InitializeGitRepository(workspace);
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
            // This fixture explicitly authorizes the exact Host-registered workflow verifier.
            // Its command remains pinned by the workflow and executes against the owned lane root.
            File.WriteAllText(Path.Combine(config, "permissions.yaml"), "profile: autonomous\nrules:\n  dyn.core.verify_integration: allow\n");

            using (var modelPolicies = OmniHost.CreateModelPolicyService(data))
            {
                var policyKey = ModelPolicyKey.For("scripted", "scripted-model");
                // Workflow stages run with their own compiled tool catalog. Keep this source
                // model policy explicit and narrow while retaining the tools the workflow's
                // Explore/Implement/Verify stages actually need.
                var workflowCapabilities = new HashSet<ModelToolCapability>
                {
                    ModelToolCapability.WorkspaceRead,
                    ModelToolCapability.PatchExisting,
                    ModelToolCapability.CreateFile,
                    ModelToolCapability.ReplaceFile,
                    ModelToolCapability.ValidationProcess,
                    ModelToolCapability.GeneralProcess,
                };
                var workflowPolicy = new UserModelPolicy(ModelPolicyCategory.Custom,
                    new ModelToolPolicy(ToolMode.Direct, 6, false, workflowCapabilities),
                    new FileMutationPolicy(FileMutationMode.Full,
                        DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny,
                        5, 600, 0.50, requirePriorRead: true, requireExpectedVersionToken: true,
                        requirePostEditValidation: true, allowParallelMutations: false),
                    "test", "Exact compiled workflow stage capabilities.",
                    maxModelStepsPerTurn: 8, maxToolCallsPerTurn: 32,
                    allowOrchestration: true, maxGenerationSeconds: 300);
                var existing = modelPolicies.Get(policyKey, CancellationToken.None);
                modelPolicies.Set(policyKey, existing?.Revision ?? 0, workflowPolicy, CancellationToken.None);
            }

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
            var processObservations = new System.Collections.Concurrent.ConcurrentQueue<string>();
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
            var previousNuGetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            runtime.ProcessRuntimeFactoryForTests = () => new RecordingProcessRuntime(
                new OmniCore.Execution.SystemProcessRuntime(new[] { "APPDATA", "LOCALAPPDATA" }),
                processObservations);
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
                var verifyArguments = VerifierArguments(executable, workspace);
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
                    // Keep the verifier's package root explicit. The process environment is
                    // intentionally reduced by SystemProcessRuntime, and NuGet otherwise falls
                    // back to machine/user defaults that are not part of this isolated fixture.
                    Environment.SetEnvironmentVariable("NUGET_PACKAGES",
                        Path.Combine(workspace, "src", "FeatureApp", ".packages"));
                    result = await Run(command);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("APPDATA", previousAppData);
                    Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
                    Environment.SetEnvironmentVariable("NUGET_PACKAGES", previousNuGetPackages);
                }
                var expectedCode = !connectModule || injectPostVerificationMutation ? 1 : 0;
                Assert.True(result.Code == expectedCode,
                    $"Workflow returned {result.Code}; output: {result.Output}\nDiagnostics: {string.Join(" | ", diagnostics)}"
                    + $"\nProvider requests={provider.RequestCount}; keys={string.Join(",", requests.Keys.Order())}"
                    + $"\nJournal tail={string.Join(" | ", ReadCurrentSessionEvents(workspace).TakeLast(12).Select(item => item.GetType().Name))}"
                    + $"\nTask failures={string.Join(" | ", ReadCurrentSessionEvents(workspace).OfType<TaskFailed>().Select(item => item.Cause))}"
                    + $"\nProgram={File.ReadAllText(Path.Combine(projectDirectory, "Program.cs"))}"
                    + $"\nModule={(File.Exists(Path.Combine(projectDirectory, "FeatureModule.cs")) ? File.ReadAllText(Path.Combine(projectDirectory, "FeatureModule.cs")) : "<missing>")}"
                    + $"\nFinal provider request={requests.OrderBy(pair => pair.Key).LastOrDefault().Value}"
                    + $"\nVerifier process observations={string.Join(" | ", processObservations)}"
                    + $"\nEvent details={string.Join(" | ", ReadCurrentSessionJournalEvents(workspace).TakeLast(18).Select(evt => evt.Sequence + ":" + EventCodecs.Create().Decode(evt)))}");
                Assert.Contains("Flujo", result.Output, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(originalProgram, File.ReadAllText(Path.Combine(projectDirectory, "Program.cs")));
                Assert.False(File.Exists(Path.Combine(projectDirectory, "FeatureModule.cs")));
                Assert.Contains(requests.OrderBy(pair => pair.Key).Select(pair => pair.Value), body =>
                    body.Contains("EXPLORE_RESULT_EVIDENCE", StringComparison.Ordinal));

                var journal = ReadCurrentSessionJournalEvents(workspace).ToArray();
                var codecs = EventCodecs.Create();
                var events = journal.Select(codecs.Decode).ToArray();
                var plan = PlanProjection.Replay(codecs, journal);
                var stageItems = plan.Items().Where(item => item.Metadata.ContainsKey(WorkflowStageContract.WorkflowIdKey))
                    .OrderBy(item => item.Metadata[WorkflowStageContract.StageKey], StringComparer.Ordinal).ToArray();
                Assert.Equal(3, stageItems.Length);
                Assert.Equal(connectModule ? 3 : 2,
                    stageItems.Count(item => item.State == PlanItemState.Completed));
                Assert.Contains(stageItems, item => item.Metadata[WorkflowStageContract.StageKey] == "Verify");
                var completionGate = Assert.Single(plan.Items(), item =>
                    item.Metadata.ContainsKey("workflow.gate.workflow"));
                Assert.Equal(connectModule && !injectPostVerificationMutation,
                    completionGate.State == PlanItemState.Completed);
                var produced = events.OfType<AgentResultProduced>().ToArray();
                Assert.Equal(3, produced.Length);
                var dispositions = events.OfType<ResultDispositionRecorded>().ToArray();
                Assert.Equal(3, dispositions.Length);
                Assert.Equal(connectModule ? ResultDispositionOutcome.Accepted : ResultDispositionOutcome.ReworkRequested,
                    dispositions[^1].Disposition.Outcome);
                Assert.Contains(events.OfType<ToolCallRequested>(), item => item.ToolName == "filesystem.write");
                Assert.Contains(events.OfType<ToolCallRequested>(), item =>
                    item.ToolName == "dyn.core.verify_integration");
                var implementationPlanItem = Assert.Single(stageItems,
                    item => item.Metadata[WorkflowStageContract.StageKey] == "Implement");
                var implementationTask = Assert.Single(events.OfType<PlanItemLinked>(), item =>
                    item.PlanItemId == implementationPlanItem.Id).Link.TaskId;
                var implementationLane = Assert.Single(events.OfType<LaneWorktreeCreated>(), item =>
                    item.TaskId == implementationTask);
                var verifyPlanItem = Assert.Single(stageItems,
                    item => item.Metadata[WorkflowStageContract.StageKey] == "Verify");
                var verifyTask = Assert.Single(events.OfType<PlanItemLinked>(), item =>
                    item.PlanItemId == verifyPlanItem.Id).Link.TaskId;
                var verifyLane = Assert.Single(events.OfType<LaneWorktreeBound>(), item => item.TaskId == verifyTask);
                Assert.Equal(implementationLane.LaneId, verifyLane.OwnerLaneId);
                Assert.Equal(implementationLane.ExecutionId, verifyLane.OwnerExecutionId);
                Assert.Equal(implementationLane.OwnershipId, verifyLane.OwnershipId);
                Assert.Equal(implementationLane.WorktreePath, verifyLane.WorktreePath);
                using (var packet = JsonDocument.Parse(OmniHost.CreateArtifactStore(
                    OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace))
                    .GetText(verifyLane.TaskPacketRef.Hash)!))
                {
                    Assert.Equal(implementationLane.OwnershipId,
                        packet.RootElement.GetProperty("WorkflowImplementation").GetProperty("OwnershipId").GetString());
                    Assert.Equal(implementationLane.SnapshotTree,
                        packet.RootElement.GetProperty("SnapshotTree").GetString());
                }
                var laneProjectDirectory = Path.Combine(implementationLane.WorktreePath, "src", "FeatureApp");
                Assert.Equal(connectModule, File.ReadAllText(Path.Combine(laneProjectDirectory, "Program.cs"))
                    .Contains("FeatureModule.Initialize", StringComparison.Ordinal));
                Assert.Equal(implementation, File.ReadAllText(Path.Combine(laneProjectDirectory, "FeatureModule.cs")));
                var providerCalls = provider.RequestCount;
                var modelSteps = events.OfType<ModelStepStarted>().Count();
                var toolCalls = events.OfType<ToolCallRequested>().Count();
                var retry = await Run(command);
                Assert.Equal(expectedCode, retry.Code);
                Assert.Contains("Flujo", retry.Output, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(providerCalls, provider.RequestCount);
                var retryEvents = ReadCurrentSessionEvents(workspace);
                Assert.Equal(modelSteps, retryEvents.OfType<ModelStepStarted>().Count());
                Assert.Equal(toolCalls, retryEvents.OfType<ToolCallRequested>().Count());
                Assert.Equal(injectPostVerificationMutation ? 1 : 0, injectedMutation);
            }
            finally { CliApp.UseRuntimeForTests(previousRuntime).Dispose(); }
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

    private sealed class RecordingProcessRuntime(IProcessRuntime inner,
        System.Collections.Concurrent.ConcurrentQueue<string> observations) : IProcessRuntime
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ProcessLaunch> _launches = new();

        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            var handle = inner.Launch(launch, cancellationToken);
            _launches[handle.Pid] = launch;
            return handle;
        }

        public void CancelTree(ProcessHandle handle) => inner.CancelTree(handle);

        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = inner.Wait(handle, timeout, cancellationToken);
            if (_launches.TryRemove(handle.Pid, out var launch)
                && launch.Args.Contains("run", StringComparer.Ordinal))
                observations.Enqueue("cwd=" + launch.WorkingDirectory + "; exit=" + result.ExitCode
                    + "; stdout=" + (result.Stdout ?? "") + "; stderr=" + (result.Stderr ?? ""));
            return result;
        }

        public bool IsAlive(ProcessHandle handle) => inner.IsAlive(handle);
    }

    private sealed class FakeClaudeLaneRuntime(string model, bool failAfterInit = false, bool includeToolActivity = false) : IProcessRuntime, IStreamingProcessRuntime
    {
        private int _nextPid = 1000;
        public ProcessLaunch? LastLaunch { get; private set; }
        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastLaunch = launch;
            return new ProcessHandle(Interlocked.Increment(ref _nextPid), this);
        }
        public void CancelTree(ProcessHandle handle) { }
        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(0, null, null, false);
        public bool IsAlive(ProcessHandle handle) => false;

        public async Task<ProcessResult> WaitStreamingAsync(ProcessHandle handle, TimeSpan timeout,
            Func<Stream, CancellationToken, Task> consumeStandardOutput, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var launch = LastLaunch ?? throw new InvalidOperationException("No fake external process was launched.");
            var toolsIndex = launch.Args.ToList().IndexOf("--tools");
            if (toolsIndex < 0 || toolsIndex + 1 >= launch.Args.Count)
                throw new InvalidDataException("The production lane invocation omitted its effective tools.");
            var tools = launch.Args[toolsIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Append("StructuredOutput").ToArray();
            if (failAfterInit)
            {
                var init = JsonSerializer.Serialize(new { type = "system", subtype = "init",
                    session_id = "claude-fixture-session", model, tools,
                    capabilities = new Dictionary<string, bool>() });
                await using var initStream = new MemoryStream(Encoding.UTF8.GetBytes(init + "\n"));
                await consumeStandardOutput(initStream, cancellationToken).ConfigureAwait(false);
                // The first process is lost immediately after its durable init/session barrier.
                // This leaves the dispatcher with a real open step and a session it may resume.
                throw new IOException("fixture process disappeared after session binding");
            }
            var lines = new List<string>
            {
                JsonSerializer.Serialize(new { type = "system", subtype = "init", session_id = "claude-fixture-session",
                    model, tools, capabilities = new Dictionary<string, bool>() }),
            };
            if (includeToolActivity)
            {
                lines.Add(JsonSerializer.Serialize(new { type = "assistant", session_id = "claude-fixture-session",
                    message = new { content = new object[] { new { type = "tool_use", id = "fixture-read-1",
                        name = "Read", input = new { file_path = "README.md" } } } } }));
                lines.Add(JsonSerializer.Serialize(new { type = "user", session_id = "claude-fixture-session",
                    message = new { content = new object[] { new { type = "tool_result", tool_use_id = "fixture-read-1",
                        is_error = false, content = "fixture content" } } } }));
            }
            lines.Add(JsonSerializer.Serialize(new { type = "assistant", session_id = "claude-fixture-session",
                message = new { id = "fixture-message", usage = new { input_tokens = 12, output_tokens = 4 }, content = Array.Empty<object>() } }));
            lines.Add(JsonSerializer.Serialize(new { type = "result", subtype = "success", session_id = "claude-fixture-session",
                    is_error = false, result = "Fixture lane completed.",
                    structured_output = new { outcome = "Succeeded", summary = "Fixture lane completed.",
                        findings = Array.Empty<string>(), filesChanged = Array.Empty<string>(),
                        remainingIssues = Array.Empty<string>(), confidence = "High",
                        proposedPlanMutations = Array.Empty<object>() },
                    usage = new { input_tokens = 12, output_tokens = 4 }, total_cost_usd = 0.01m }));
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
            await consumeStandardOutput(stream, cancellationToken).ConfigureAwait(false);
            return new ProcessResult(0, null, null, false);
        }
    }

    private sealed class ConcurrentFakeClaudeLaneRuntime(string model) : IProcessRuntime, IStreamingProcessRuntime
    {
        private int _nextPid = 2000;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ProcessLaunch> _byPid = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<ProcessLaunch> _launches = new();
        private readonly CountdownEvent _entered = new(2);
        public TaskCompletionSource<bool> FirstLaunched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BothLaunched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseWriters { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ProcessLaunch> Launches => _launches.ToArray();

        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pid = Interlocked.Increment(ref _nextPid);
            _byPid[pid] = launch;
            _launches.Enqueue(launch);
            return new ProcessHandle(pid, this);
        }

        public void CancelTree(ProcessHandle handle) { }
        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(0, null, null, false);
        public bool IsAlive(ProcessHandle handle) => false;

        public async Task<ProcessResult> WaitStreamingAsync(ProcessHandle handle, TimeSpan timeout,
            Func<Stream, CancellationToken, Task> consumeStandardOutput, CancellationToken cancellationToken)
        {
            if (!_byPid.TryGetValue(handle.Pid, out var launch))
                throw new InvalidOperationException("The fake external lane lost its launch binding.");
            _entered.Signal();
            FirstLaunched.TrySetResult(true);
            if (_entered.CurrentCount == 0) BothLaunched.TrySetResult(true);
            await ReleaseWriters.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            var directory = launch.WorkingDirectory ?? throw new InvalidOperationException("No lane working directory.");
            await File.WriteAllTextAsync(Path.Combine(directory, "external-writer.txt"),
                "lane-private effect " + handle.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n",
                cancellationToken).ConfigureAwait(false);
            var toolsIndex = launch.Args.ToList().IndexOf("--tools");
            if (toolsIndex < 0 || toolsIndex + 1 >= launch.Args.Count)
                throw new InvalidDataException("The product lane invocation omitted its effective tools.");
            var tools = launch.Args[toolsIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Append("StructuredOutput").ToArray();
            var sessionId = "fixture-session-" + handle.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lines = new[]
            {
                JsonSerializer.Serialize(new { type = "system", subtype = "init", session_id = sessionId,
                    model, tools, capabilities = new Dictionary<string, bool>() }),
                JsonSerializer.Serialize(new { type = "assistant", session_id = sessionId,
                    message = new { id = "fixture-message", usage = new { input_tokens = 12, output_tokens = 4 }, content = Array.Empty<object>() } }),
                JsonSerializer.Serialize(new { type = "result", subtype = "success", session_id = sessionId,
                    is_error = false, result = "Fixture lane completed.",
                    structured_output = new { outcome = "Succeeded", summary = "Fixture lane completed.",
                        findings = Array.Empty<string>(), filesChanged = Array.Empty<string>(),
                        remainingIssues = Array.Empty<string>(), confidence = "High",
                        proposedPlanMutations = Array.Empty<object>() },
                    usage = new { input_tokens = 12, output_tokens = 4 }, total_cost_usd = 0.01m }),
            };
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
            await consumeStandardOutput(stream, cancellationToken).ConfigureAwait(false);
            return new ProcessResult(0, null, null, false);
        }
    }

    [Fact]
    public async Task Normal_PLAN_chat_can_recommend_ORQ_without_granting_mode_or_creating_workers()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            // The normal 8k fixture has a six-tool Harness ceiling, under which artifact.read
            // correctly outranks optional proposals. Give this case enough declared context
            // for the existing PlanProposal capability to be disclosed without widening policy.
            File.WriteAllText(Path.Combine(config, "models.yaml"), "models:\n  scripted-model:\n    provider: scripted\n    context: 32768\n    maxOutput: 2048\n");
            using (var policies = OmniHost.CreateModelPolicyService(data))
            {
                var observe = ModelPolicyPresets.ObserveOnly();
                policies.Set(ModelPolicyKey.For("scripted", "scripted-model"), 0,
                    new UserModelPolicy(ModelPolicyCategory.Custom,
                        new ModelToolPolicy(ToolMode.Direct, 7, false,
                            new HashSet<ModelToolCapability>(observe.ToolPolicy.CapabilityCeiling)
                            { ModelToolCapability.PlanProposal }),
                        observe.MutationPolicy, "test", "Plan proposal only; ORQ remains unauthorized.",
                        allowOrchestration: false), CancellationToken.None);
            }
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
            Assert.Equal(new[] { "agent.profile", "context.policy", "model.descriptor", "model.harness", "model.output", "model.profile",
                "model.reasoning.resolved", "plan.revision", "prompt.template", "provider.adapter", "run.mode_authority",
                "runtime.build", "skills.active", "tools.plan", "tools.preferences", "turn.instruction" },
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

    [Fact]
    public async Task Act_stall_escalates_the_same_run_through_the_runtime_with_progress_stalled_cause()
    {
        // ADR-0048: un `omni act` que no avanza (el gate rechaza siempre igual) recorre la cadena
        // y, en EscalateModel, el runtime escala por la ruta autorizada y continúa el mismo Run.
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            // Sin TTY de forma explícita: al agotar la cadena, AskUser termina en InputRequired en lugar
            // de esperar una respuesta en la consola de quien lance los tests.
            var runtime = OmniCliRuntime.Create(workspace);
            runtime.UseConsoleInput = false;
            runtime.Localize = (key, values) => Localization.Spanish().Resolve(key, values);
            CliApp.UseRuntimeForTests(runtime).Dispose();
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  small-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [small] }
                  big-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [big] }
                routing:
                  implementation: [small]
                  escalation: { mode: auto, chain: [small, big] }
                """);
            Assert.Equal(0, (await Run("model", "policy", "set", "small-model", "--category", "PatchOnly", "--note", "stall")).Code);
            Assert.Equal(0, (await Run("model", "policy", "set", "big-model", "--category", "PatchOnly", "--note", "stall")).Code);
            Assert.Equal(0, (await Run("trust")).Code);
            Directory.CreateDirectory(Path.Combine(workspace, ".omnicore"));
            File.WriteAllText(Path.Combine(workspace, ".omnicore", "settings.yaml"),
                "gates:\n  test: [omnicore-stall-missing-gate-tool]\n");
            provider.RespondWith((_, _) => TextResponse("listo"));

            var act = await Run("act", "corrige este test");

            var events = ReadCurrentSessionEvents(workspace);
            var run = Assert.Single(events.OfType<RunCreated>());
            var responses = events.OfType<StallResponseSelected>().ToArray();
            Assert.Equal(new[] { StallPolicy.Replan, StallPolicy.Diagnose, StallPolicy.EscalateModel },
                responses.Take(3).Select(item => item.Policy));
            var requested = Assert.Single(events.OfType<ModelEscalationRequested>());
            Assert.Equal(EscalationCause.ProgressStalled, requested.Cause);
            Assert.Equal(run.RunId, requested.RunId);
            Assert.Equal("big-model", requested.ToModel);
            Assert.Contains(events.OfType<ModelStepStarted>(), step => step.ModelId == "big-model");
            Assert.Contains(events.OfType<UserInputReceived>(),
                input => input.Origin == ProgressWatchdog.RuntimeOriginPrefix + "StallEscalation)");
            // Ya escalado, la cadena sigue sin volver a escalar.
            Assert.All(responses.Skip(3), item => Assert.NotEqual(StallPolicy.EscalateModel, item.Policy));
            Assert.Contains("escalando a big-model", act.Output);
            Assert.Contains("Sin progreso en «corrige este test»", act.Output);
            AssertNoLeaks(act.Output);
        });
    }

    private const string FakeLlamaServer = """
        param([int]$Port, [string]$Log)
        $key = $env:LLAMA_API_KEY
        # Registra el arranque y vuelca salida de sobra: si nadie la drena, el servidor se bloquea aquí.
        Add-Content -Path $Log -Value "STARTED $PID"
        [Console]::Out.Write('x' * 3000000)
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        while ($true) {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $request = $reader.ReadLine()
                # A connection may close before sending HTTP (probe or cancelled client).
                # EOF is not an empty header line; never spin forever on a null ReadLine.
                if ([string]::IsNullOrEmpty($request)) { continue }
                $auth = ''
                $length = 0
                while ($null -ne ($line = $reader.ReadLine()) -and $line -ne '') {
                    if ($line -like 'Authorization:*') { $auth = $line.Substring(14).Trim() }
                    if ($line -like 'Content-Length:*') { $length = [int]$line.Substring(15).Trim() }
                }
                if ($null -eq $line) { continue }
                if ($length -gt 0) {
                    $buffer = New-Object char[] $length
                    $read = 0
                    while ($read -lt $length) { $read += $reader.Read($buffer, $read, $length - $read) }
                }
                $verb = $request.Split(' ')[0]
                if ($auth -ne "Bearer $key") {
                    Add-Content -Path $Log -Value "$verb auth=bad"
                    $head = "HTTP/1.1 401 Unauthorized`r`nContent-Length: 0`r`nConnection: close`r`n`r`n"
                    $body = ''
                } elseif ($verb -eq 'GET') {
                    Add-Content -Path $Log -Value "GET auth=ok"
                    $body = '{"data":[]}'
                    $head = "HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
                } else {
                    Add-Content -Path $Log -Value "POST auth=ok"
                    $body = 'data: {"choices":[{"delta":{"content":"respuesta-managed"},"finish_reason":null}]}' + "`n`n" + 'data: {"choices":[{"delta":{},"finish_reason":"stop"}]}' + "`n`n" + 'data: [DONE]' + "`n"
                    $head = "HTTP/1.1 200 OK`r`nContent-Type: text/event-stream`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
                }
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($head + $body)
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush()
            } finally { $client.Close() }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_fixture_accepts_authenticated_readiness_after_an_empty_or_partial_tcp_probe(bool partialRequest)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("The fixture is a Windows PowerShell server."); return; }
        await InIsolatedCli((workspace, config, _, _) =>
        {
            var script = Path.Combine(config, "fake-llama-empty-probe.ps1");
            var log = Path.Combine(config, "fake-llama-empty-probe.log");
            File.WriteAllText(script, FakeLlamaServer);
            var host = new LocalModelHost(SystemProcessRuntime.Instance());
            var probed = false;
            try
            {
                var spec = new ManagedServerSpec("powershell.exe",
                    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Port", "{port}", "-Log", log], workspace, 0);
                var status = host.StartManaged(spec, () =>
                {
                    var endpoint = "http://127.0.0.1:" + host.ChosenPort() + "/v1";
                    if (!probed)
                    {
                        if (!LocalServerSupervisor.TcpReachable(endpoint, TimeSpan.FromMilliseconds(200))) return false;
                        probed = true; // Deliberately connect and close without an HTTP request.
                        if (partialRequest)
                        {
                            using var client = new TcpClient();
                            client.Connect(IPAddress.Loopback, host.ChosenPort());
                            client.GetStream().Write(Encoding.ASCII.GetBytes("GET /v1/models HTTP/1.1\r\n"));
                        }
                    }
                    return LocalServerSupervisor.HttpReady(endpoint, host.GeneratedApiKeyValue(), TimeSpan.FromSeconds(1));
                // Match the existing managed fixture's startup budget. On Windows CI the
                // new ten-second budget expired before even the first TCP connection.
                }, TimeSpan.FromSeconds(90));
                Assert.True(probed, "The real child server accepted the empty connection.");
                Assert.Equal(LocalServerStatus.Ready, status);
                Assert.Contains("GET auth=ok", File.ReadAllLines(log));
            }
            finally { host.StopManaged(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Managed_local_server_is_launched_with_its_own_key_reused_across_turns_and_stopped_on_dispose()
    {
        // ADR-0011 §4: `host: managed` arranca el servidor con puerto efímero y API key generados en
        // runtime, lo reutiliza entre Turns y lo detiene al terminar el runtime.
        if (!OperatingSystem.IsWindows()) return;
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            var script = Path.Combine(config, "fake-llama.ps1");
            var log = Path.Combine(config, "fake-llama.log");
            File.WriteAllText(script, FakeLlamaServer);
            File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
                providers:
                  scripted:
                    family: OpenAiChatCompatible
                    billingMode: Local
                    host: managed
                    baseUrl: auto
                    managed:
                      executable: powershell.exe
                      args: ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", '{{script}}', "-Port", "{port}", "-Log", '{{log}}']
                      readinessTimeoutSeconds: 90
                """);
            using var runtime = OmniCliRuntime.Create(workspace);
            runtime.Localize = (key, values) => Localization.Spanish().Resolve(key, values);
            CliApp.UseRuntimeForTests(runtime).Dispose();

            var first = await Run("ask", "primera pregunta");
            Assert.True(first.Code == 0, first.Output);
            Assert.Contains("respuesta-managed", first.Output);
            var second = await Run("ask", "segunda pregunta");
            Assert.True(second.Code == 0, second.Output);

            var lines = File.ReadAllLines(log);
            var started = Assert.Single(lines, line => line.StartsWith("STARTED ", StringComparison.Ordinal));
            Assert.Contains("GET auth=ok", lines);
            Assert.Equal(2, lines.Count(line => line == "POST auth=ok"));
            Assert.DoesNotContain(lines, line => line.Contains("auth=bad", StringComparison.Ordinal));
            Assert.Equal(0, provider.RequestCount); // el provider scripted del arnés nunca recibe nada
            AssertNoLeaks(first.Output + second.Output);

            var doctor = await Run("doctor");
            Assert.Contains("administrado por OmniCore", doctor.Output);

            var pid = int.Parse(started["STARTED ".Length..]);
            Assert.True(IsRunning(pid), "el servidor sigue vivo entre Turns");
            runtime.Dispose();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && IsRunning(pid)) await Task.Delay(200);
            Assert.False(IsRunning(pid), "Dispose detiene el servidor managed");
        });
    }

    private static bool IsRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task Attach_to_a_down_local_server_fails_before_the_turn_with_a_clear_message()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            var free = new TcpListener(IPAddress.Loopback, 0);
            free.Start();
            var port = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            File.WriteAllText(Path.Combine(config, "providers.yaml"), string.Join(Environment.NewLine,
                "providers:", "  scripted:", "    family: OpenAiChatCompatible", "    baseUrl: http://127.0.0.1:" + port + "/v1",
                "    auth: none", "    billingMode: Local", "    host: attach", ""));

            var ask = await Run("ask", "hola");

            Assert.Equal(1, ask.Code);
            Assert.Contains("El servidor local de «scripted» no responde en http://127.0.0.1:" + port + "/v1", ask.Output);
            Assert.Contains("host: managed", ask.Output);
            Assert.Equal(0, provider.RequestCount);
            AssertNoLeaks(ask.Output);

            var doctor = await Run("doctor");
            Assert.Contains("servidor ya levantado en http://127.0.0.1:" + port + "/v1 — no responde", doctor.Output);
        });
    }

    [Fact]
    public async Task Non_interactive_act_on_an_unclassified_model_ends_with_model_policy_required_before_any_run()
    {
        // ADR-0044 §6: sin TTY no hay onboarding; `act` existe para modificar archivos, así que termina en lugar
        // de fingir que trabaja en ObserveOnly. Ni se crea un Run ni se llama al provider.
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            var refused = await Run("act", "cambia doc.txt");

            Assert.Equal(1, refused.Code);
            Assert.Contains("«scripted-model» no tiene una política de modelo", refused.Output);
            Assert.Contains("PatchExisting", refused.Output);
            Assert.Equal(0, provider.RequestCount);
            Assert.DoesNotContain(ReadAllSessionEventsOrEmpty(workspace), evt => evt is RunCreated);

            // Sin TTY, `model select` deja el modelo en modo seguro efímero, que tampoco concede escritura.
            Assert.Equal(0, (await Run("model", "select", "scripted-model")).Code);
            var ephemeral = await Run("act", "cambia doc.txt");
            Assert.Equal(1, ephemeral.Code);
            Assert.Equal(0, provider.RequestCount);

            // Un modelo clasificado sí puede actuar, y el modo `ask` nunca exigió política de escritura.
            provider.RespondWith((_, _) => TextResponse("listo"));
            Assert.Equal(0, (await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "e2e")).Code);
            Assert.Equal(0, (await Run("model", "select", "scripted-model")).Code);
            Assert.Equal(0, (await Run("act", "cambia doc.txt")).Code);
            Assert.True(provider.RequestCount > 0);
        });
    }

    private static IReadOnlyList<DomainEventPayload> ReadAllSessionEventsOrEmpty(string workspace)
    {
        var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
        return File.Exists(journal) ? ReadAllSessionEvents(workspace) : Array.Empty<DomainEventPayload>();
    }

    [Fact]
    public async Task Permissions_yaml_is_reported_by_doctor_and_an_invalid_one_stops_every_turn_before_the_provider()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "permissions.yaml"),
                "profile: balanced\nrules:\n  filesystem.write: ask\n");
            var doctor = await Run("doctor");
            Assert.True(doctor.Code == 0, doctor.Output);
            Assert.Contains("Permisos: perfil Equilibrado; reglas de usuario: 1", doctor.Output);

            // A TCP connectivity probe is not a model HTTP request.
            var requests = provider.RequestCount;
            File.WriteAllText(Path.Combine(config, "permissions.yaml"), "profile: reckless\n");
            var ask = await Run("ask", "hola");
            Assert.Equal(1, ask.Code);
            Assert.Contains("permissions.yaml:1:10", ask.Output);
            Assert.Equal(requests, provider.RequestCount);
            Assert.Equal(1, (await Run("doctor")).Code);
        });
    }

    [Fact]
    public async Task Explain_shows_the_context_and_the_effective_configuration_of_the_last_turn()
    {
        // ADR-0017 / ADR-0029: `omni explain` explica qué recibió el último Turn, no solo el estado del plan.
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            var before = await Run("explain");
            Assert.Equal(0, before.Code);
            Assert.Contains("Ningún Turn ha registrado su fingerprint todavía.", before.Output);

            provider.RespondWith((_, _) => TextResponse("hecho"));
            Assert.Equal(0, (await Run("ask", "explica el estado")).Code);

            var after = await Run("explain");
            Assert.Equal(0, after.Code);
            Assert.Contains("Contexto del último Turn:", after.Output);
            Assert.Matches(@"Tokens: \d+/\d+ \(", after.Output);
            Assert.Contains("Configuración efectiva del último Turn (fingerprint):", after.Output);
            Assert.Contains("Modelo: ", after.Output);
            Assert.True(Regex.IsMatch(after.Output, @"model\.profile@\S+  \S{12}"), after.Output);
            Assert.True(Regex.IsMatch(after.Output, @"tools\.plan@\S+  \S{12}"), after.Output);
        });
    }

    [Fact]
    public async Task Client_commands_run_in_the_plain_cli_instead_of_failing_as_unknown_prompts()
    {
        // ADR-0024/0025: /help, /exit, /tasks, /events, /permissions y /cancel resuelven a una ClientAction.
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            var help = await Run("/help");
            Assert.Equal(0, help.Code);
            Assert.Contains("Comandos disponibles:", help.Output);
            foreach (var name in new[] { "help", "exit", "plan", "tasks", "events", "permissions", "cancel" })
            {
                var displayed = OmniCore.Protocol.HumanCommandAliases.DisplayName(name, "es");
                Assert.Contains("/" + displayed + (displayed == name ? "" : " (/" + name + ")") + " — ", help.Output);
            }
            Assert.DoesNotContain("prompt_not_found", help.Output);

            Assert.Contains("Hasta pronto.", (await Run("/exit")).Output);
            var permissions = await Run("/permissions");
            Assert.Equal(0, permissions.Code);
            Assert.Contains("Permisos", permissions.Output, StringComparison.OrdinalIgnoreCase);

            foreach (var command in new[] { "/tasks", "/events", "/plan", "/cancel", "/interrupt" })
            {
                var result = await Run(command);
                Assert.Equal(0, result.Code);
                Assert.DoesNotContain("prompt_not_found", result.Output);
                AssertNoLeaks(result.Output);
            }
            Assert.Equal(0, provider.RequestCount);
        });
    }

    [Fact]
    public async Task Workspace_local_settings_choose_the_model_unless_user_settings_lock_it()
    {
        // ADR-0022 §4 / ADR-0039 §5: el scope Workspace gana sobre User, salvo claves `locked` de User.
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  small-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [small] }
                  big-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [big] }
                """);
            var localSettings = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace),
                "settings.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(localSettings)!);
            File.WriteAllText(localSettings, "defaultModel: big-model\n");
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: small-model\n");
            provider.RespondWith((_, _) => TextResponse("uno"));

            Assert.Equal(0, (await Run("ask", "primera")).Code);
            Assert.Equal("big-model", ReadCurrentSessionEvents(workspace).OfType<ModelStepStarted>().Last().ModelId);

            File.WriteAllText(Path.Combine(config, "settings.yaml"),
                "defaultModel: small-model\nlocked: [defaultModel]\n");
            var doctor = await Run("doctor");
            Assert.Equal(0, doctor.Code);
            Assert.Contains("defaultModel ← Usuario", doctor.Output);
            Assert.Contains("defaultModel: se ignora en Workspace (bloqueada «defaultModel» en Usuario)", doctor.Output);
            AssertNoLeaks(doctor.Output);
            provider.RespondWith((_, _) => TextResponse("dos"));
            Assert.Equal(0, (await Run("ask", "segunda")).Code);
            Assert.Equal("small-model", ReadCurrentSessionEvents(workspace).OfType<ModelStepStarted>().Last().ModelId);
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
                runtime.Dispose();
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

    [Fact]
    public async Task Act_image_attachment_uses_the_real_act_run_and_reaches_the_http_request()
    {
        await InIsolatedCli(async (workspace, _, config, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "models.yaml"), "models:\n  scripted-model: { provider: scripted, context: 8192, maxOutput: 2048, inputModalities: [text, image] }\n");
            string? requestBody = null;
            provider.RespondWith((_, body) =>
            { requestBody = body; return TextResponse("Imagen revisada."); });
            var imagePath = Path.Combine(workspace, "act-image.png");
            File.WriteAllBytes(imagePath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/z/kAAAAASUVORK5CYII="));

            using var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            var attachment = host.ImportAttachmentFromPath(imagePath);
            var diagnostics = new List<string>();
            Assert.Equal(0, await host.ExecuteActAsync("Describe esta imagen.", [attachment], diagnostics.Add,
                TestContext.Current.CancellationToken));

            Assert.Equal(1, provider.RequestCount);
            Assert.NotNull(requestBody);
            using (var request = JsonDocument.Parse(requestBody!))
            {
                var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                Assert.Contains(messages, message => message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array
                    && content.EnumerateArray().Any(part => part.TryGetProperty("type", out var type)
                        && type.GetString() == "image_url"));
            }

            var events = ReadCurrentSessionEvents(workspace);
            var run = Assert.Single(events.OfType<RunCreated>());
            Assert.Equal(RunMode.Act, run.Mode);
            var authority = Assert.Single(events.OfType<RunModeAuthoritySelected>());
            Assert.Equal(run.RunId, authority.Authority.RunId);
            Assert.Equal(RunMode.Act, authority.Authority.Mode);
            Assert.False(authority.Authority.AutoModeSwitch);
            var input = Assert.Single(events.OfType<UserInputReceived>());
            Assert.Equal(run.RunId, input.RunId);
            Assert.Contains("Describe esta imagen", input.InputPartsJson, StringComparison.Ordinal);
            var persisted = Assert.Single(input.Attachments!);
            Assert.Equal("image/png", persisted.MediaType);
            Assert.Equal(attachment.Size, persisted.Size);
            Assert.Contains(events.OfType<TurnStarted>(), turn => turn.RunId == run.RunId);
        });
    }

    [Fact]
    public async Task Act_rejects_image_before_creating_a_run_when_the_selected_model_is_text_only()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            provider.RespondWith((_, _) => TextResponse("No debe llegar al proveedor."));
            var imagePath = Path.Combine(workspace, "unsupported.png");
            File.WriteAllBytes(imagePath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/z/kAAAAASUVORK5CYII="));
            using var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            var attachment = host.ImportAttachmentFromPath(imagePath);

            var diagnostics = new List<string>();
            Assert.Equal(1, await host.ExecuteActAsync("Describe esta imagen.", [attachment], diagnostics.Add,
                TestContext.Current.CancellationToken));

            Assert.Equal(0, provider.RequestCount);
            Assert.Empty(ReadAllSessionEvents(workspace).OfType<RunCreated>());
            Assert.Empty(ReadAllSessionEvents(workspace).OfType<UserInputReceived>());
            Assert.Contains(diagnostics, line => line.Contains("imagen", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task Act_text_document_attachment_is_admitted_and_materialized_in_the_real_request()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            const string marker = "ACT_DOCUMENT_FIXTURE_7D91";
            string? requestBody = null;
            provider.RespondWith((_, body) =>
            { requestBody = body; return TextResponse("Documento recibido."); });
            var documentPath = Path.Combine(workspace, "brief.txt");
            File.WriteAllText(documentPath, "Nota de trabajo: " + marker, new UTF8Encoding(false));
            using var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            var attachment = host.ImportAttachmentFromPath(documentPath);

            Assert.Equal(0, await host.ExecuteActAsync("Resume el documento adjunto.", [attachment], _ => { },
                TestContext.Current.CancellationToken));

            Assert.Equal(1, provider.RequestCount);
            Assert.Contains(marker, Assert.IsType<string>(requestBody), StringComparison.Ordinal);
            var events = ReadCurrentSessionEvents(workspace);
            var run = Assert.Single(events.OfType<RunCreated>());
            Assert.Equal(RunMode.Act, run.Mode);
            var input = Assert.Single(events.OfType<UserInputReceived>());
            Assert.Equal(run.RunId, input.RunId);
            Assert.Contains("Resume el documento adjunto", input.InputPartsJson, StringComparison.Ordinal);
            Assert.Equal("text/plain", Assert.Single(input.Attachments!).MediaType);
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
            using var firstRuntime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(firstRuntime);
            Assert.Equal(0, await host.ExecuteAsync("Mi proyecto se llama Brújula; usa PostgreSQL.",
                _ => { }, TestContext.Current.CancellationToken));
            var original = ReadCurrentSessionEvents(workspace).OfType<RunCreated>().Single().SessionId;
            if (reopen) firstRuntime.Dispose();
            using var reopenedRuntime = reopen ? OmniCliRuntime.Create(workspace) : null;
            if (reopenedRuntime is not null) host = new TuiTurnHost(reopenedRuntime);
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
            CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace)).Dispose();

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
            // Las líneas «tipo · contributor ×n» y «componente@versión  hash» llevan ids (p. ej. run.mode_authority), no textos.
            AssertNoLeaks(string.Join('\n', explain.Output.Split('\n')
                .Where(line => !line.Contains(" ×", StringComparison.Ordinal) && !line.Contains('@'))));

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

            // ADR-0044 §6: sin TTY, un modelo sin clasificar no puede actuar y no se llama al provider.
            var requests = provider.RequestCount;
            var refused = await Run("act", "implementa el objetivo");
            Assert.Equal(1, refused.Code);
            Assert.Contains("«scripted-model» no tiene una política de modelo", refused.Output);
            Assert.DoesNotContain("modelPolicy.required", refused.Output);
            Assert.Equal(requests, provider.RequestCount);

            // M3: exercise the real Act path and verify the scripted provider response is rendered.
            Assert.Equal(0, (await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "e2e")).Code);
            var act = await Run("act", "implementa el objetivo");
            Assert.Equal(0, (await Run("model", "policy", "delete", "scripted-model")).Code);
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
            Assert.Contains("Sólo parches", policySet.Output);
            AssertNoLeaks(policySet.Output);

            var policyShow = await Run("model", "policy", "show", "scripted-model");
            Assert.Equal(0, policyShow.Code);
            Assert.Contains("Sólo parches", policyShow.Output);
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
            // Separate CLI invocations release the previous process's Session writer.
            CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace)).Dispose();
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
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime).Dispose();
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task Act_applies_approved_patch_but_does_not_complete_without_session_gates()
    {
        await InIsolatedCli(async (workspace, _, data, provider) =>
        {
            SetSessionSecurityFullAgent();
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
            Assert.Equal(1, act.Code);
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
            var rejected = Assert.Single(payloads.OfType<RunValidationRejected>());
            Assert.Equal(new[] { "post-edit-validation", "plan" }, rejected.Gates);
            Assert.DoesNotContain(payloads, payload => payload is RunCompleted);
            Assert.Equal(3, provider.RequestCount);
        });
    }

    [Fact]
    public async Task Act_mutations_are_recorded_as_file_mutation_reliability_on_the_qualified_profile()
    {
        // ADR-0044 §4: un Run ACT real alimenta FileMutationReliability en el perfil de cualificación
        // de la configuración exacta que lo ejecutó, y el onboarding la usa en su recomendación.
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            SetSessionSecurityFullAgent();
            const string original = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n";
            File.WriteAllText(Path.Combine(workspace, "doc.txt"), original);
            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
            var registry = OmniHost.LoadUserConfiguration(config).Registry;
            var model = registry.Model("scripted-model")!;
            var key = ModelQualificationHost.QualificationKeyFor(model, registry.Provider("scripted"));
            ModelQualificationProfile profile;
            using (var store = OmniHost.CreateModelQualificationStore(data))
            {
                profile = store.Upsert(key, 0, ModelQualificationState.Qualified, "fixture-quick", "1.0.0",
                    TestContext.Current.CancellationToken);
                store.SaveTraits(key, profile.ProfileRevision, new[]
                {
                    new ModelTraitRecord(key.QualificationKeyHash(), profile.ProfileRevision,
                        "InstructionFollowing", 1.0, 1.0, 10, "fixture"),
                    new ModelTraitRecord(key.QualificationKeyHash(), profile.ProfileRevision,
                        "StructuredOutputReliability", 1.0, 1.0, 10, "fixture"),
                }, TestContext.Current.CancellationToken);
            }
            provider.RespondWith((requestNumber, _) => requestNumber switch
            {
                0 => ToolCallResponse("call-read", "filesystem.read", """{"path":"doc.txt"}"""),
                1 => ToolCallResponse("call-patch", "filesystem.patch", PatchArguments(version)),
                _ => TextResponse("cambio aplicado"),
            });
            Assert.Equal(0, (await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly",
                "--note", "mutation-evidence")).Code);
            var policyService = OmniHost.CreateModelPolicyService(data);
            var policyKey = ModelPolicyKey.For("scripted", "scripted-model");
            var stored = Assert.IsType<StoredModelPolicy>(policyService.Get(policyKey, CancellationToken.None));
            var oldMutation = stored.Policy.MutationPolicy;
            policyService.Set(policyKey, stored.Revision, new UserModelPolicy(stored.Policy.Category,
                stored.Policy.ToolPolicy, new FileMutationPolicy(oldMutation.Mode, oldMutation.Delete,
                    oldMutation.MoveOrRename, oldMutation.MaxFilesPerTurn, oldMutation.MaxChangedLinesPerTurn,
                    oldMutation.MaxRewriteRatio, oldMutation.RequirePriorRead, oldMutation.RequireExpectedVersionToken,
                    requirePostEditValidation: false, allowParallelMutations: oldMutation.AllowParallelMutations),
                stored.Policy.Source, "mutation-evidence"), CancellationToken.None);

            var act = await Run("act", "actualiza doc.txt");

            Assert.Equal(1, act.Code);
            Assert.Equal("one\ntwo-X\nthree\nfour\nfive\nsix\nseven\neight\n",
                File.ReadAllText(Path.Combine(workspace, "doc.txt")));
            using var reopened = OmniHost.CreateModelQualificationStore(data);
            var traits = reopened.Traits(key, profile.ProfileRevision, TestContext.Current.CancellationToken);
            var mutation = Assert.Single(traits, trait => trait.Trait == MutationEvidenceRecorder.TraitName);
            Assert.Equal(1, mutation.Samples);
            Assert.Equal(MutationEvidenceRecorder.Source, mutation.Source);
            Assert.Equal(0.925, mutation.Value, 4); // 2 líneas cambiadas sobre 8: solo penaliza el tamaño del diff
            var payloads = ReadCurrentSessionEvents(workspace);
            var rejected = Assert.Single(payloads.OfType<RunValidationRejected>());
            Assert.Equal(new[] { "post-edit-validation", "plan" }, rejected.Gates);
            Assert.DoesNotContain(payloads, payload => payload is RunCompleted);
            Assert.Equal(3, provider.RequestCount);
            Assert.Contains(traits, trait => trait.Trait == "InstructionFollowing" && trait.Samples == 10);
            AssertNoLeaks(act.Output);
        });
    }

    [Fact]
    public async Task Act_permission_ask_without_tty_stays_awaiting_input_without_inventing_answer()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            SetSessionSecurityFullAgent();
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
            Assert.DoesNotContain(payloads, evt => evt is PermissionGranted);
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
            Assert.Equal(0, (await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "e2e")).Code);
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

    private static void SetSourceModelFullAgent(string dataDirectory)
    {
        using var policies = OmniHost.CreateModelPolicyService(dataDirectory);
        policies.Set(ModelPolicyKey.For("scripted", "scripted-model"), 0,
            ModelPolicyPresets.FullAgent(), CancellationToken.None);
    }

    private static void SetSessionSecurityFullAgent()
    {
        var configuration = new SessionSecurityDefaultConfiguration(OmniHost.CreatePlatformPaths());
        var current = configuration.Read();
        configuration.Set(current.Revision, ModelPolicyCategory.FullAgent,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task User_command_alias_dispatches_through_cli_commands_and_unknown_target_never_calls_model()
    {
        await InIsolatedCli(async (_, config, _, provider) =>
        {
            var preferences = new PolicyPreferencesHost(OmniHost.CreatePlatformPaths(), Environment.CurrentDirectory);
            var initial = preferences.Read();
            preferences.SaveCommandAliases(initial.Revision, new Dictionary<string, string>
            {
                ["t"] = "tools",
                ["x"] = "missing-command",
            });

            var tools = await Run("t");
            Assert.Equal(0, tools.Code);
            Assert.DoesNotContain("cli.ask_assumed", tools.Output, StringComparison.Ordinal);
            Assert.Equal(0, provider.RequestCount);

            var unknown = await Run("x", "literal argument");
            Assert.Equal(2, unknown.Code);
            Assert.DoesNotContain("cli.ask_assumed", unknown.Output, StringComparison.Ordinal);
            Assert.Equal(0, provider.RequestCount);
            Assert.True(File.Exists(Path.Combine(config, "settings.yaml")));
        });
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
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime).Dispose();
            Environment.CurrentDirectory = previousDirectory;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void InitializeGitRepository(string workspace)
    {
        File.WriteAllText(Path.Combine(workspace, "README.md"), "external lane fixture\n");
        RunGit("init", "--initial-branch=main", workspace);
        RunGit("-C", workspace, "config", "user.name", "OmniCore Tests");
        RunGit("-C", workspace, "config", "user.email", "tests@example.invalid");
        RunGit("-C", workspace, "add", "README.md");
        RunGit("-C", workspace, "commit", "-m", "initial fixture");
    }

    private static void RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Git fixture setup failed: " + stderr.GetAwaiter().GetResult());
        _ = stdout.GetAwaiter().GetResult();
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

    internal static string ResponsesToolCallResponse(string id, string name, string arguments)
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

    internal static string ResponsesTextResponse(string text)
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

    private static string UsageTextResponse(string text) => TextResponse(text).Replace("data: [DONE]",
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\ndata: [DONE]",
        StringComparison.Ordinal);

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
    internal sealed class ScriptedHttpProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private Func<int, string, string>? _response;
        private Func<int, string, Task<string>>? _asyncResponse;
        private int _requestCount;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<bool>> _requestWaiters = new();

        public ScriptedHttpProvider()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public Task WaitForRequestAsync(int count, CancellationToken cancellationToken)
        {
            if (RequestCount >= count) return Task.CompletedTask;
            var waiter = _requestWaiters.GetOrAdd(count,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (RequestCount >= count) waiter.TrySetResult(true);
            return waiter.Task.WaitAsync(cancellationToken);
        }
        public void RespondWith(Func<int, string, string> response)
        {
            _asyncResponse = null;
            _response = response;
        }

        public void RespondWithAsync(Func<int, string, Task<string>> response)
        {
            _response = null;
            _asyncResponse = response;
        }

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
                // doctor may connect and close without sending HTTP. Do not count it as
                // a model request or dispatch the scripted response for an empty socket.
                if (string.IsNullOrWhiteSpace(await reader.ReadLineAsync(cancellationToken))) return;
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
                _requestWaiters.GetOrAdd(requestNumber + 1,
                    _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true);
                var request = new string(body, 0, read);
                var eventText = _asyncResponse is { } asyncResponse
                    ? await asyncResponse(requestNumber, request).ConfigureAwait(false)
                    : _response?.Invoke(requestNumber, request) ?? TextResponse("respuesta-scripted");
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
