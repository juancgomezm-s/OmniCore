using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Theory]
    [InlineData("cleanup")]
    [InlineData("discard")]
    public async Task Native_worktree_disposition_host_crash_after_real_git_remove_recovers_uncertain_and_passive(
        string operation)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("This fixture verifies an owned Windows Host process tree."); return; }
        var artifactRoot = Path.Combine(FindRepositoryRoot(), "artifacts", "m9-worktree-disposition-crash",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        NativeCommandHandoffFixture? fixture = null;
        Process? host = null;
        Task<string>? stderrTask = null;
        var hostStopped = false;
        var fixtureHostStopped = false;
        var success = false;
        var stage = "fixture_start";
        string? sessionId = null;
        string? actionRun = null;
        string? invocationId = null;
        string? requestHash = null;
        string? worktreePath = null;
        string? repositoryRoot = null;
        string? proxyDirectory = null;
        string? proxyConfig = null;
        string? proxyBarrier = null;
        string? proxyLog = null;
        string? targetTree = null;
        string? retainedRef = null;
        string? snapshotRef = null;
        ToolCallId? toolCallId = null;
        var providerRequestsBeforeAction = -1;

        try
        {
            fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
                permissionsYaml: $"profile: balanced\nrules:\n  tool.search: allow\n  worktree.{operation}: ask\n",
                includeNativeChoiceCapability: true, isolateAgentRoots: true);
            fixture.PreserveRootOnDispose = true;
            sessionId = await fixture.CreateSessionAndQuiescentRunAsync("plan");
            var ownerRun = fixture.QueryJson("humanActionScope").GetProperty("runId").GetString()!;
            actionRun = ownerRun;
            var (identity, _) = await CreateNativeWorktreeBindingAsync(fixture, sessionId, ownerRun);
            stage = "terminal_binding_and_review_snapshot_ready";
            repositoryRoot = identity.RepositoryRoot;
            worktreePath = identity.WorktreePath;
            snapshotRef = identity.SnapshotRef;
            retainedRef = RetainedLaneRef(identity.WorkspaceId.ToString(), identity.LaneId.ToString(), identity.OwnershipId);
            if (operation == "discard")
                File.WriteAllText(Path.Combine(worktreePath, "discard-review.txt"), "retain this reviewed snapshot\n",
                    new UTF8Encoding(false));

            var lane = Assert.Single(fixture.QueryJson("agents").GetProperty("lanes").EnumerateArray(), item =>
                item.GetProperty("laneId").GetString() == identity.LaneId.ToString());
            var laneId = lane.GetProperty("laneId").GetString()!;
            var preview = fixture.QueryJson("worktree.preview:" + laneId);
            targetTree = preview.GetProperty("laneTree").GetString()!;
            var laneHead = preview.GetProperty("laneHead").GetString()!;
            var argumentsJson = JsonSerializer.Serialize(new { laneId, laneHead, laneTree = targetTree });

            var ownerEvents = fixture.ReadEvents(sessionId);
            var binding = Assert.IsType<LaneWorktreeCreated>(Assert.Single(ownerEvents,
                item => item.Payload is LaneWorktreeCreated).Payload);
            var store = fixture.Server.AcquireStore();
            var codecs = fixture.Server.AcquireCodecs();
            void AppendOwner(DomainEventPayload payload)
            {
                var codec = codecs.CodecFor(payload.Type());
                var envelope = DomainEvent.Create(SessionId.Parse(sessionId), payload.Type(), payload.SchemaVersion(), null,
                    RunId.Parse(ownerRun), RunId.Parse(ownerRun), binding.TaskId, binding.LaneId, null, null, null, [],
                    codec.Encode(payload), binding.ExecutionId);
                store.Append(SessionId.Parse(sessionId), envelope, DurabilityClass.Barrier, CancellationToken.None);
            }
            AppendOwner(new LaneCompleted(binding.LaneId, null));
            AppendOwner(new TaskCompleted(binding.TaskId, null));
            var scope = fixture.QueryJson("humanActionScope");
            var mode = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "run.mode.select", runId = actionRun, mode = "act",
                expectedRevision = scope.GetProperty("revision").GetInt64(),
            }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted,
                Ack(fixture.ExchangeTrusted(mode, sessionId, actionRun)).Outcome?.Kind);

            proxyDirectory = Path.Combine(fixture.RootDirectory, "disposition-proxy");
            Directory.CreateDirectory(proxyDirectory);
            CopySandboxProxyFiles(proxyDirectory);
            proxyConfig = Path.Combine(proxyDirectory, "retention-proxy.json");
            proxyBarrier = Path.Combine(proxyDirectory, "effect-barrier.json");
            proxyLog = Path.Combine(proxyDirectory, "proxy-events.jsonl");
            var statePath = Path.Combine(proxyDirectory, "discard-state.json");
            var gitExecutable = new SystemExecutableResolver().Resolve("git", fixture.Workspace).ResolvedPath;
            var repositoryObjectsPath = Path.GetFullPath(Git(identity.RepositoryRoot, "rev-parse",
                "--path-format=absolute", "--git-path", "objects").Trim());
            var originalPath = Environment.GetEnvironmentVariable("PATH")
                ?? throw new InvalidOperationException("Fixture runner PATH is unavailable.");
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            var bootstrap = fixture.BootstrapKeyForOwnedProcess;
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            WriteDispositionProxyConfig(proxyConfig, fixture.RootDirectory, tempRoot, identity, operation,
                laneHead, targetTree, retainedRef, statePath, repositoryObjectsPath, gitExecutable, proxyBarrier, proxyLog, 0,
                [".gitignore", "integration-target.txt", "discard-review.txt"]);

            await fixture.StopHostAsync();
            fixtureHostStopped = true;
            CopyOwnedFixture(fixture.RootDirectory, Path.Combine(artifactRoot, "before-os-host"));
            stage = "os_host_started";
            host = StartRetentionHost(cliDll, fixture.Workspace, fixture.RootDirectory, proxyDirectory, originalPath, bootstrap);
            stderrTask = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            WriteDispositionProxyConfig(proxyConfig, fixture.RootDirectory, tempRoot, identity, operation,
                laneHead, targetTree, retainedRef, statePath, repositoryObjectsPath, gitExecutable, proxyBarrier, proxyLog, host.Id,
                [".gitignore", "integration-target.txt", "discard-review.txt"]);

            var hello = RawExchange(host, WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"disposition-crash-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"],\"capabilities\":[\"query.commandCatalog\",\"query.commandRecovery\",\"query.processSessions\",\"query.worktreePreview\",\"command.invoke.user\",\"command.invoke.continue.user\",\"command.run.mode.select.user\",\"command.conversation.interaction.choice.respond.user\"]}}"));
            var nonce = ReadRawTrustedNonce(hello);
            Assert.False(host.HasExited);
            var catalog = QueryRawHost(host, "command.catalog");
            var toolName = "worktree." + operation;
            var descriptor = Assert.Single(catalog.GetProperty("commands").EnumerateArray(), item =>
                item.GetProperty("canonicalCommandId").GetString() == "core:" + toolName);
            var invokeId = invocationId = Guid.NewGuid().ToString("D");
            var invoke = WireEnvelope.Command(invokeId, JsonSerializer.Serialize(new
            {
                cmd = "command.invoke.user", canonicalCommandId = "core:" + toolName,
                arguments = new[] { toolName, argumentsJson }, expectedSessionId = sessionId,
                expectedRunId = actionRun,
                expectedModeRevision = NullableJsonInt64(catalog.GetProperty("modeRevision")),
                expectedPolicyRevision = catalog.GetProperty("policyRevision").GetInt64(),
                expectedSelectionRevision = catalog.GetProperty("selectionRevision").GetInt32(),
                expectedRoutingPolicyRevision = NullableJsonInt64(catalog.GetProperty("routingPolicyRevision")),
                catalogRevision = catalog.GetProperty("catalogRevision").GetString(),
                sourceFingerprint = descriptor.GetProperty("sourceFingerprint").GetString(),
            }));
            requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invoke.PayloadJson)));

            var wrongKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                var badProof = Ack(RawExchange(host, TrustedActionEnvelope(invoke, wrongKey, nonce,
                    fixture.Workspace, sessionId, actionRun)));
                Assert.Equal("trusted_action_invalid", badProof.Error);
                Assert.Null(fixture.ReadReceipt(invokeId));
            }
            finally { CryptographicOperations.ZeroMemory(wrongKey); }
            var foreignSession = SessionId.New().ToString();
            var foreignPayload = JsonNode.Parse(invoke.PayloadJson)!.AsObject();
            foreignPayload["expectedSessionId"] = foreignSession;
            var foreignCommand = WireEnvelope.Command(Ids.NewV7(), foreignPayload.ToJsonString());
            var sequenceBeforeForeign = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
            var foreignAck = Ack(RawExchange(host, TrustedActionEnvelope(foreignCommand, fixture.KeyForTests,
                nonce, fixture.Workspace, foreignSession, actionRun)));
            Assert.Equal("trusted_action_scope_changed", foreignAck.Error);
            var foreignReceipt = Assert.IsType<CommandReceipt>(fixture.ReadReceipt(foreignCommand.MessageId));
            Assert.Equal("Rejected", foreignReceipt.OutcomeKind);
            Assert.Equal(sequenceBeforeForeign, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));

            var accepted = Ack(RawExchange(host, TrustedActionEnvelope(invoke, fixture.KeyForTests, nonce,
                fixture.Workspace, sessionId, actionRun)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, accepted.Outcome?.Kind);
            stage = "disposition_admitted";
            await WaitForPermissionRequestAsync(fixture, sessionId, actionRun);
            await fixture.WaitForReceiptExecutionAsync(invokeId, "deferred");
            var permission = Assert.IsType<PermissionRequested>(Assert.Single(fixture.ReadEvents(sessionId), item =>
                item.Payload is PermissionRequested).Payload);
            toolCallId = permission.ToolCallId;
            var interaction = Assert.IsType<InteractionRequested>(Assert.Single(fixture.ReadEvents(sessionId), item =>
                item.Payload is InteractionRequested).Payload);
            var security = QueryRawHost(host, "sessionSecurity");
            var approve = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond", responseType = "choice", interactionId = interaction.InteractionId.ToString(),
                optionId = "allow_once", expectedRunId = actionRun,
                expectedPolicyRevision = security.GetProperty("revision").GetInt64(),
            }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted,
                Ack(RawExchange(host, TrustedActionEnvelope(approve, fixture.KeyForTests, nonce,
                    fixture.Workspace, sessionId, actionRun))).Outcome?.Kind);
            providerRequestsBeforeAction = fixture.Provider.RequestCount;

            // The recovery fence launches repeated real Git inspections through the isolated .NET proxy.
            // The prior bounded attempt recorded 268 Git invocations without rejection before the 30s
            // watchdog expired; keep this fixture wait bounded while allowing that verified work to finish.
            var barrier = await WaitForProxyBarrierAsync(proxyBarrier, host, TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken);
            stage = "real_git_remove_barrier";
            Assert.Equal(host.Id, barrier.GetProperty("proxyParentPid").GetInt32());
            Assert.Equal(host.Id, barrier.GetProperty("expectedHostPid").GetInt32());
            Assert.Equal(0, barrier.GetProperty("gitExitCode").GetInt32());
            Assert.Equal(operation, barrier.GetProperty("operationKind").GetString());
            Assert.False(host.HasExited);
            var started = fixture.ReadEvents(sessionId).Where(item => item.Envelope.ToolCallId == toolCallId).ToArray();
            Assert.Single(started, item => item.Payload is ToolCallStarted);
            Assert.DoesNotContain(started, item => item.Payload is ToolCallSucceeded or ToolCallFailed
                or ToolCallEffectUnknown or ToolCallReconciled);
            Assert.False(Directory.Exists(worktreePath));
            var metadataBeforeResult = JsonNode.Parse(File.ReadAllBytes(identity.OwnershipMetadataPath))!.AsObject();
            var stateKey = metadataBeforeResult.Select(pair => pair.Key)
                .Single(key => string.Equals(key, "State", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.Equals("removed", metadataBeforeResult[stateKey]?.GetValue<string>(),
                StringComparison.OrdinalIgnoreCase));
            var removedAtKey = metadataBeforeResult.Select(pair => pair.Key)
                .Single(key => string.Equals(key, "RemovedAtUtc", StringComparison.OrdinalIgnoreCase));
            Assert.Null(metadataBeforeResult[removedAtKey]);
            if (operation == "discard")
            {
                Assert.Equal(targetTree, Git(repositoryRoot, "rev-parse", "--verify", retainedRef + "^{tree}").Trim());
                Assert.Equal("retain this reviewed snapshot\n", Git(repositoryRoot, "show", retainedRef + ":discard-review.txt"));
            }
            else
            {
                Assert.Equal(targetTree, Git(repositoryRoot, "rev-parse", "--verify", snapshotRef + "^{tree}").Trim());
                Assert.DoesNotContain(retainedRef, Git(repositoryRoot, "for-each-ref", "--format=%(refname)", retainedRef)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            }

            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            hostStopped = true;
            var proxyPid = barrier.GetProperty("proxyPid").GetInt32();
            Assert.True(OwnedProxyHasExited(proxyPid));
            if (stderrTask is not null) _ = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            host.Dispose();
            host = null;
            fixture.ClearOwnedSqlitePools();
            CopyOwnedFixture(fixture.RootDirectory, Path.Combine(artifactRoot, "after-host-kill-before-reopen"));
            stage = "host_killed_and_database_backed_up";

            var admissionReceipt = Assert.IsType<CommandReceipt>(fixture.ReadReceipt(invokeId));
            Assert.Equal("deferred", admissionReceipt.ExecutionState);
            var killedEvents = fixture.ReadEvents(sessionId).Where(item => item.Envelope.ToolCallId == toolCallId).ToArray();
            Assert.Single(killedEvents, item => item.Payload is ToolCallStarted);
            Assert.DoesNotContain(killedEvents, item => item.Payload is ToolCallSucceeded or ToolCallFailed
                or ToolCallEffectUnknown or ToolCallReconciled);

            host = StartRetentionHost(cliDll, fixture.Workspace, fixture.RootDirectory, proxyDirectory, originalPath, bootstrap,
                useProxyPath: false);
            stderrTask = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var reopenedHello = RawExchange(host, WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"disposition-crash-reopen\",\"payload\":{\"supportedVersions\":[\"1.0\"],\"capabilities\":[\"query.commandCatalog\",\"query.commandRecovery\",\"query.processSessions\",\"query.worktreePreview\",\"command.invoke.user\",\"command.invoke.continue.user\",\"command.run.mode.select.user\",\"command.conversation.interaction.choice.respond.user\"]}}"));
            var reopenedNonce = ReadRawTrustedNonce(reopenedHello);
            var conversationResult = QueryRawCommandResult(host, "conversation.result", invokeId, requestHash);
            var commandResult = QueryRawCommandResult(host, "command.result", invokeId, requestHash);
            Assert.Equal("deferred", conversationResult.GetProperty("state").GetString());
            Assert.Equal("completed", commandResult.GetProperty("state").GetString());
            Assert.Equal("Accepted", commandResult.GetProperty("ack").GetProperty("outcome").GetString());
            var recovery = QueryRawHost(host, "command.recovery");
            var recoveryEntry = Assert.Single(recovery.GetProperty("invocations").EnumerateArray(), item =>
                item.GetProperty("invocationId").GetString() == invokeId);
            Assert.Equal("blocked", recoveryEntry.GetProperty("state").GetString());
            Assert.Equal("execution_started_or_uncertain", recoveryEntry.GetProperty("reasonCode").GetString());
            Assert.False(Directory.Exists(worktreePath));
            AssertPreservedDispositionArchive(operation, repositoryRoot, worktreePath, snapshotRef, retainedRef, targetTree);
            var recovered = fixture.ReadEvents(sessionId).Where(item => item.Envelope.ToolCallId == toolCallId).ToArray();
            Assert.Single(recovered, item => item.Payload is ToolCallStarted);
            Assert.Single(recovered, item => item.Payload is ToolCallEffectUnknown);
            var reconciled = Assert.IsType<ToolCallReconciled>(Assert.Single(recovered,
                item => item.Payload is ToolCallReconciled).Payload);
            Assert.Equal(ReconciliationOutcome.Unresolvable, reconciled.Outcome);
            Assert.DoesNotContain(recovered, item => item.Payload is ToolCallSucceeded or ToolCallFailed);
            var recoveryBaseline = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
            stage = "reopen_recovery_observed";

            var reopenedCatalog = QueryRawHost(host, "command.catalog");
            var badReplayKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                var badReplay = Ack(RawExchange(host, TrustedActionEnvelope(invoke, badReplayKey,
                    reopenedNonce, fixture.Workspace, sessionId, actionRun)));
                Assert.Equal("trusted_action_invalid", badReplay.Error);
            }
            finally { CryptographicOperations.ZeroMemory(badReplayKey); }
            var changedPayload = JsonNode.Parse(invoke.PayloadJson)!.AsObject();
            changedPayload["catalogRevision"] = new string('0', 64);
            var changed = WireEnvelope.Command(invokeId, changedPayload.ToJsonString());
            var conflict = Ack(RawExchange(host, TrustedActionEnvelope(changed, fixture.KeyForTests,
                reopenedNonce, fixture.Workspace, sessionId, actionRun)));
            Assert.Equal("command_id_conflict", conflict.Error);
            var continueCommand = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "command.continue", invocationId = invokeId, expectedSessionId = sessionId,
                expectedRunId = actionRun,
                expectedModeRevision = NullableJsonInt64(reopenedCatalog.GetProperty("modeRevision")),
                expectedPolicyRevision = reopenedCatalog.GetProperty("policyRevision").GetInt64(),
                expectedSelectionRevision = reopenedCatalog.GetProperty("selectionRevision").GetInt32(),
                expectedRoutingPolicyRevision = NullableJsonInt64(reopenedCatalog.GetProperty("routingPolicyRevision")),
                catalogRevision = reopenedCatalog.GetProperty("catalogRevision").GetString(),
                sourceFingerprint = descriptor.GetProperty("sourceFingerprint").GetString(),
            }));
            var continueAck = Ack(RawExchange(host, TrustedActionEnvelope(continueCommand, fixture.KeyForTests,
                reopenedNonce, fixture.Workspace, sessionId, actionRun)));
            Assert.Equal("command.recovery_blocked", continueAck.Error);
            Assert.Equal(recoveryBaseline, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));

            var replay = Ack(RawExchange(host, TrustedActionEnvelope(invoke, fixture.KeyForTests, reopenedNonce,
                fixture.Workspace, sessionId, actionRun)));
            Assert.Equal(accepted.Status, replay.Status);
            Assert.Equal(accepted.Outcome?.Kind, replay.Outcome?.Kind);
            Assert.Equal(accepted.FirstSeq, replay.FirstSeq);
            Assert.Equal(accepted.LastSeq, replay.LastSeq);
            Assert.Equal(recoveryBaseline, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
            var finalEvents = fixture.ReadEvents(sessionId).Where(item => item.Envelope.ToolCallId == toolCallId).ToArray();
            Assert.Single(finalEvents, item => item.Payload is ToolCallStarted);
            Assert.Single(finalEvents, item => item.Payload is ToolCallEffectUnknown);
            Assert.Single(finalEvents, item => item.Payload is ToolCallReconciled reconciledEvent
                && reconciledEvent.Outcome == ReconciliationOutcome.Unresolvable);
            Assert.False(Directory.Exists(worktreePath));
            AssertPreservedDispositionArchive(operation, repositoryRoot, worktreePath, snapshotRef, retainedRef, targetTree);
            Assert.Equal(providerRequestsBeforeAction, fixture.Provider.RequestCount);
            Assert.Single(File.ReadAllLines(proxyLog), line => line.Contains("ready_to_wait", StringComparison.Ordinal));
            success = true;
            stage = "recovery_and_passive_replay_verified";
        }
        finally
        {
            var cleanupErrors = new List<string>();
            int? hostPid = host?.Id;
            if (host is not null)
            {
                try
                {
                    if (!host.HasExited) host.Kill(entireProcessTree: true);
#pragma warning disable xUnit1051 // Owned-host teardown must finish even if the test token was cancelled.
                    await host.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
#pragma warning restore xUnit1051
                    hostStopped = true;
                }
                catch (Exception error) { cleanupErrors.Add("owned_host_wait:" + error.GetType().Name); }
                try { host.Dispose(); }
                catch (Exception error) { cleanupErrors.Add("owned_host_dispose:" + error.GetType().Name); }
            }
            if (fixture is not null)
            {
                if (!fixtureHostStopped)
                {
                    try { await fixture.StopHostAsync(); fixtureHostStopped = true; }
                    catch (Exception error) { cleanupErrors.Add("fixture_host_stop:" + error.GetType().Name); }
                }
                if (fixtureHostStopped && (host is null || hostStopped))
                {
                    try
                    {
                        fixture.ClearOwnedSqlitePools();
                        var evidence = Path.Combine(artifactRoot, success ? "final-fixture" : "failure-fixture");
                        if (!Directory.Exists(evidence)) CopyOwnedFixture(fixture.RootDirectory, evidence);
                    }
                    catch (Exception error) { cleanupErrors.Add("fixture_backup:" + error.GetType().Name); }
                }
                try
                {
                    File.WriteAllText(Path.Combine(artifactRoot, "attempt-summary.json"), JsonSerializer.Serialize(new
                    {
                        operation, stage, success, hostPid, hostStopped, fixtureHostStopped,
                        sessionId, actionRun, invocationId, requestHash, toolCallId = toolCallId?.ToString(),
                        worktreePathHash = worktreePath is null ? null : Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(worktreePath)))),
                        worktreeStillExists = worktreePath is not null && Directory.Exists(worktreePath),
                        providerRequestsBeforeAction, providerRequestsAfter = fixture.Provider.RequestCount,
                        cleanupErrors,
                    }), new UTF8Encoding(false));
                    if (Directory.Exists(Path.Combine(artifactRoot, success ? "final-fixture" : "failure-fixture")))
                    {
                        WriteSafeArtifactManifest(artifactRoot);
                        if (!VerifySafeArtifactManifest(artifactRoot)) cleanupErrors.Add("artifact_manifest_verify");
                    }
                }
                catch (Exception error) { cleanupErrors.Add("artifact_seal:" + error.GetType().Name); }
                fixture.PreserveRootOnDispose = !success || !hostStopped || !fixtureHostStopped || cleanupErrors.Count != 0;
                try { await fixture.DisposeAsync(); }
                catch (Exception error)
                {
                    fixture.PreserveRootOnDispose = true;
                    cleanupErrors.Add("fixture_dispose:" + error.GetType().Name);
                }
            }
            if (cleanupErrors.Count != 0)
                throw new InvalidOperationException("Owned fixture cleanup or evidence preservation failed: "
                    + string.Join(",", cleanupErrors));
        }
    }

    private static void WriteDispositionProxyConfig(string path, string fixtureRoot, string fixtureTempRoot,
        WorktreeIdentity identity, string operation, string laneHead, string laneTree, string retainedReference,
        string statePath, string repositoryObjectsPath, string gitExecutable, string barrierPath, string logPath, int expectedHostPid,
        string[] allowedGitPaths)
    {
        var serialized = JsonSerializer.Serialize(new
        {
            Version = 1,
            FixtureRoot = Path.GetFullPath(fixtureRoot),
            FixtureTempRoot = Path.GetFullPath(fixtureTempRoot),
            RepositoryRoot = Path.GetFullPath(identity.RepositoryRoot),
            GitExecutable = Path.GetFullPath(gitExecutable),
            BarrierPath = Path.GetFullPath(barrierPath),
            LogPath = Path.GetFullPath(logPath),
            ExpectedInputSha256 = new string('0', 64),
            AllowedReferences = Array.Empty<string>(),
            ExpectedObjectIds = Array.Empty<string>(),
            ExpectedHostPid = expectedHostPid,
            Mode = "disposition",
            DataRoot = Path.GetFullPath(identity.WorkspaceDataRoot),
            WorktreePath = Path.GetFullPath(identity.WorktreePath),
            DispositionOperation = operation,
            ExpectedLaneHead = laneHead,
            ExpectedLaneTree = laneTree,
            ExpectedRetainedReference = operation == "discard" ? retainedReference : null,
            StatePath = operation == "discard" ? Path.GetFullPath(statePath) : null,
            SnapshotReference = identity.SnapshotRef,
            AllowedGitPaths = allowedGitPaths,
            RepositoryObjectsPath = Path.GetFullPath(repositoryObjectsPath),
        });
        var temporaryPath = path + ".tmp-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(temporaryPath, serialized, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string RetainedLaneRef(string workspaceId, string laneId, string ownershipId)
    {
        static string SafeSegment(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
        return $"refs/omnicore/retained-lanes/{SafeSegment(workspaceId)}/{SafeSegment(laneId)}/{ownershipId}";
    }

    private static void AssertPreservedDispositionArchive(string operation, string repositoryRoot, string worktreePath,
        string snapshotReference, string retainedReference, string reviewedTree)
    {
        Assert.False(Directory.Exists(worktreePath));
        var preservedReference = operation == "discard" ? retainedReference : snapshotReference;
        Assert.Equal(reviewedTree, Git(repositoryRoot, "rev-parse", "--verify", preservedReference + "^{tree}").Trim());
        if (operation == "discard")
            Assert.Equal("retain this reviewed snapshot\n", Git(repositoryRoot, "show", preservedReference + ":discard-review.txt"));
    }
}
