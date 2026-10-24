using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Native_retention_host_termination_after_real_git_commit_keeps_deferred_receipt_passive()
    {
        Assert.True(OperatingSystem.IsWindows(), "This fixture verifies Windows process-tree termination on ZephyrusG14.");
        var artifactRoot = Path.Combine(FindRepositoryRoot(), "artifacts", "m9-worktree-retention-crash",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        var observations = new List<object>();
        NativeCommandHandoffFixture? fixture = null;
        Process? host = null;
        Task<string>? hostStderr = null;
        var hostStopped = false;
        var fixtureHostStopped = false;
        var evidenceSnapshotVerified = false;
        var manifestVerified = false;
        string? workspace = null;
        string? sessionId = null;
        string? actionRun = null;
        string? invocationId = null;
        string? requestHash = null;
        string? proxyDirectory = null;
        string? proxyBarrier = null;
        string? proxyLog = null;
        int? proxyPid = null;
        var providerCountBeforeNativeAction = -1;
        string? terminalFailure = null;

        try
        {
            fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
                permissionsYaml: "profile: balanced\nrules:\n  worktree.retention: ask\n",
                includeRetentionQueryCapability: true, isolateAgentRoots: true);
            fixture.PreserveRootOnDispose = true;
            workspace = fixture.Workspace;
            sessionId = await fixture.CreateSessionAndQuiescentRunAsync("plan");
            var ownerRun = RunId.Parse(fixture.QueryJson("humanActionScope").GetProperty("runId").GetString()!);
            var (identity, _) = await CreateNativeWorktreeBindingAsync(fixture, sessionId, ownerRun.ToString(),
                includeUncommittedSnapshot: true);
            var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
            Assert.True((await store.CleanupAsync(identity.Ownership, CancellationToken.None)).Removed);

            var metadata = JsonNode.Parse(File.ReadAllBytes(identity.OwnershipMetadataPath))!.AsObject();
            var removedAtKey = metadata.Select(pair => pair.Key).Single(key =>
                string.Equals(key, "RemovedAtUtc", StringComparison.OrdinalIgnoreCase));
            metadata[removedAtKey] = DateTimeOffset.UtcNow.AddDays(-8).ToString("O");
            File.WriteAllText(identity.OwnershipMetadataPath, metadata.ToJsonString(), new UTF8Encoding(false));
            var retainedMetadata = File.ReadAllBytes(identity.OwnershipMetadataPath);
            var sentinelReference = "refs/heads/m9-retention-crash-sentinel";
            Git(identity.RepositoryRoot, "update-ref", sentinelReference, identity.SnapshotCommit);
            var sentinelOid = Git(identity.RepositoryRoot, "rev-parse", "--verify", sentinelReference).Trim();

            var journalStore = fixture.Server.AcquireStore();
            var codecs = fixture.Server.AcquireCodecs();
            var binding = Assert.IsType<LaneWorktreeCreated>(fixture.ReadEvents(sessionId)
                .Single(item => item.Envelope.RunId == ownerRun && item.Payload is LaneWorktreeCreated).Payload);
            void AppendOwner(DomainEventPayload payload)
            {
                var codec = codecs.CodecFor(payload.Type());
                var envelope = DomainEvent.Create(SessionId.Parse(sessionId), payload.Type(), payload.SchemaVersion(),
                    null, ownerRun, ownerRun, binding.TaskId, binding.LaneId, null, null, null, [],
                    codec.Encode(payload), binding.ExecutionId);
                journalStore.Append(SessionId.Parse(sessionId), envelope, DurabilityClass.Barrier, CancellationToken.None);
            }
            AppendOwner(new LaneCompleted(binding.LaneId, null));
            AppendOwner(new TaskCompleted(binding.TaskId, null));
            new RunControlService(journalStore, codecs).CancelRun(SessionId.Parse(sessionId), ownerRun);
            Assert.True(RunProjection.Replay(SessionId.Parse(sessionId), ownerRun, codecs,
                fixture.ReadEvents(sessionId).Select(item => item.Envelope).ToArray()).IsTerminal());

            var selection = fixture.QueryJson("session.model.selection");
            var startRun = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "conversation.start", text = "Authorize one current Run for fixture retention.", mode = "plan",
                expectedRevision = (long?)null,
                expectedPolicyRevision = selection.GetProperty("sessionPolicyRevision").GetInt64(),
                expectedDefaultRevision = (string?)null,
            }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted,
                Ack(fixture.ExchangeTrusted(startRun, sessionId, null)).Outcome?.Kind);
            await fixture.Provider.WaitForRequestAsync(2, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            actionRun = RunId.Parse(fixture.QueryJson("humanActionScope").GetProperty("runId").GetString()!).ToString();
            await WaitForTurnCompletedAsync(fixture, sessionId, actionRun);
            await fixture.WaitForReceiptExecutionAsync(startRun.MessageId, "completed");
            Assert.NotEqual(ownerRun.ToString(), actionRun);
            var actionScope = fixture.QueryJson("humanActionScope");
            var modeCommand = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "run.mode.select", runId = actionRun, mode = "act",
                expectedRevision = actionScope.GetProperty("revision").GetInt64(),
            }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted,
                Ack(fixture.ExchangeTrusted(modeCommand, sessionId, actionRun)).Outcome?.Kind);

            var previewResponse = fixture.Exchange(WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            { operation = "query", name = "worktree.retention:" + identity.OwnershipId })));
            using var previewOuter = JsonDocument.Parse(previewResponse.PayloadJson);
            using var previewDocument = JsonDocument.Parse(previewOuter.RootElement.GetProperty("json").GetString()!);
            var preview = previewDocument.RootElement.Clone();
            Assert.Equal("eligible", preview.GetProperty("state").GetString());
            var previewReferences = preview.GetProperty("references").EnumerateArray().Select(item =>
                (Name: item.GetProperty("name").GetString()!, Oid: item.GetProperty("expectedObjectId").GetString()!))
                .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(previewReferences);
            Assert.All(previewReferences, item => Assert.StartsWith("refs/omnicore/", item.Name));
            var retentionArguments = JsonSerializer.Serialize(new
            {
                ownerRunId = ownerRun.ToString(), ownershipId = identity.OwnershipId,
                removedAtUtc = preview.GetProperty("removedAtUtc").GetString(),
                references = previewReferences.Select(item => new { name = item.Name, expectedObjectId = item.Oid }),
            });

            var proxyDirectoryPath = Path.Combine(fixture.RootDirectory, "git-proxy");
            Directory.CreateDirectory(proxyDirectoryPath);
            CopySandboxProxyFiles(proxyDirectoryPath);
            proxyDirectory = proxyDirectoryPath;
            proxyBarrier = Path.Combine(proxyDirectoryPath, "effect-barrier.json");
            proxyLog = Path.Combine(proxyDirectoryPath, "proxy-events.jsonl");
            var exactTransaction = new StringBuilder("start\0");
            foreach (var reference in previewReferences)
                exactTransaction.Append("delete ").Append(reference.Name).Append('\0').Append(reference.Oid).Append('\0');
            exactTransaction.Append("prepare\0commit\0");
            var expectedTransaction = exactTransaction.ToString();
            var expectedInputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedTransaction)));
            var gitExecutable = new SystemExecutableResolver().Resolve("git", workspace).ResolvedPath;
            var proxyConfigPath = Path.Combine(proxyDirectoryPath, "retention-proxy.json");
            WriteRetentionProxyConfig(proxyConfigPath, fixture.RootDirectory, identity.RepositoryRoot,
                gitExecutable, proxyBarrier, proxyLog, expectedInputHash,
                previewReferences.Select(item => item.Name).ToArray(),
                previewReferences.Select(item => item.Oid).ToArray(), expectedHostPid: 0);

            observations.Add(new
            {
                phase = "fixture_ready",
                processPid = Environment.ProcessId,
                sessionId,
                ownerRunId = ownerRun.ToString(),
                actionRunId = actionRun,
                ownershipId = identity.OwnershipId,
                repositoryRoot = "[owned temporary workspace]",
                seededHistoricalBinding = true,
                bindingPreparation = "CreateNativeWorktreeBindingAsync before OS Host action; not provisioning evidence",
                targetReferenceCount = previewReferences.Length,
                targetReferenceNames = previewReferences.Select(item => item.Name).ToArray(),
                targetOids = previewReferences.Select(item => item.Oid).ToArray(),
                unrelatedSentinelReference = sentinelReference,
                unrelatedSentinelOid = sentinelOid,
                providerRequestsBeforeAction = fixture.Provider.RequestCount,
                pathGlobalUnchanged = true,
            });

            await fixture.StopHostAsync();
            fixtureHostStopped = true;
            observations.Add(new { phase = "inprocess_fixture_host_stopped_before_os_host",
                runtimeDisposed = true, persistentStoreClosed = true, sqlitePoolsCleared = true });
            CopyOwnedFixture(fixture.RootDirectory, Path.Combine(artifactRoot, "before-host-effect"));
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            var originalPath = Environment.GetEnvironmentVariable("PATH")
                ?? throw new InvalidOperationException("Fixture runner PATH is unavailable.");
            var bootstrapKey = fixture.BootstrapKeyForOwnedProcess;
            host = StartRetentionHost(cliDll, workspace, fixture.RootDirectory, proxyDirectoryPath,
                originalPath, bootstrapKey);
            hostStderr = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            WriteRetentionProxyConfig(proxyConfigPath, fixture.RootDirectory, identity.RepositoryRoot,
                gitExecutable, proxyBarrier, proxyLog, expectedInputHash,
                previewReferences.Select(item => item.Name).ToArray(),
                previewReferences.Select(item => item.Oid).ToArray(), host.Id);

            var hello = RawExchange(host, WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"retention-crash-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"],\"capabilities\":[\"query.commandCatalog\",\"query.commandRecovery\",\"query.processSessions\",\"command.invoke.user\",\"command.invoke.continue.user\",\"command.run.mode.select.user\",\"command.conversation.interaction.choice.respond.user\",\"query.worktreeRetention\"]}}"));
            var nonce = ReadRawTrustedNonce(hello);
            Assert.False(host.HasExited);
            var commandCatalog = QueryRawHost(host, "command.catalog");
            var descriptor = Assert.Single(commandCatalog.GetProperty("commands").EnumerateArray(), item =>
                item.GetProperty("canonicalCommandId").GetString() == "core:worktree.retention");
            var osScope = QueryRawHost(host, "humanActionScope");
            var osPreview = QueryRawHost(host, "worktree.retention:" + identity.OwnershipId);
            observations.Add(new { phase = "os_host_scope_and_preview_before_invoke",
                sessionId = osScope.GetProperty("sessionId").GetString(),
                runId = osScope.GetProperty("runId").GetString(),
                previewState = osPreview.TryGetProperty("state", out var state) ? state.GetString() : null,
                previewError = osPreview.TryGetProperty("error", out var previewError) ? previewError.GetString() : null,
                previewReferenceCount = osPreview.TryGetProperty("references", out var osReferences)
                    && osReferences.ValueKind == JsonValueKind.Array ? osReferences.GetArrayLength() : 0 });
            invocationId = Guid.NewGuid().ToString("D");
            var invoke = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
            {
                cmd = "command.invoke.user", canonicalCommandId = "core:worktree.retention",
                arguments = new[] { "worktree.retention", retentionArguments }, expectedSessionId = sessionId,
                expectedRunId = actionRun,
                expectedModeRevision = NullableJsonInt64(commandCatalog.GetProperty("modeRevision")),
                expectedPolicyRevision = commandCatalog.GetProperty("policyRevision").GetInt64(),
                expectedSelectionRevision = commandCatalog.GetProperty("selectionRevision").GetInt32(),
                expectedRoutingPolicyRevision = NullableJsonInt64(commandCatalog.GetProperty("routingPolicyRevision")),
                catalogRevision = commandCatalog.GetProperty("catalogRevision").GetString(),
                sourceFingerprint = descriptor.GetProperty("sourceFingerprint").GetString(),
            }));
            requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invoke.PayloadJson)));

            var wrongKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                var invalidProof = Ack(RawExchange(host, TrustedActionEnvelope(invoke, wrongKey, nonce,
                    workspace, sessionId, actionRun)));
                Assert.Equal("error", invalidProof.Status);
                Assert.Equal("trusted_action_invalid", invalidProof.Error);
                Assert.Null(fixture.ReadReceipt(invocationId));
                observations.Add(new { phase = "bad_proof_before_admission", status = invalidProof.Status,
                    error = invalidProof.Error, noReceipt = true, effectCount = 0 });
            }
            finally { CryptographicOperations.ZeroMemory(wrongKey); }

            var wrongSessionId = SessionId.New().ToString();
            var crossedPayload = JsonNode.Parse(invoke.PayloadJson)!.AsObject();
            crossedPayload["expectedSessionId"] = wrongSessionId;
            var crossedCommand = WireEnvelope.Command(Ids.NewV7(), crossedPayload.ToJsonString());
            var crossed = Ack(RawExchange(host, TrustedActionEnvelope(crossedCommand, fixture.KeyForTests, nonce,
                workspace, wrongSessionId, actionRun)));
            Assert.Equal("error", crossed.Status);
            Assert.Equal("trusted_action_scope_changed", crossed.Error);
            Assert.DoesNotContain(fixture.ReadEvents(sessionId), item => item.Envelope.ToolCallId is not null
                && item.Payload is ToolCallStarted or ToolCallSucceeded or ToolCallEffectUnknown);
            observations.Add(new { phase = "cross_session_scope_before_effect", status = crossed.Status,
                error = crossed.Error, noToolEffect = true });

            var accepted = Ack(RawExchange(host, TrustedActionEnvelope(invoke, fixture.KeyForTests, nonce,
                workspace, sessionId, actionRun)));
            var rejectedReceipt = fixture.ReadReceipt(invocationId);
            observations.Add(new { phase = "retention_invocation_admission_diagnostic",
                status = accepted.Status, error = accepted.Error, outcome = accepted.Outcome?.Kind.ToString(),
                firstSequence = accepted.FirstSeq, lastSequence = accepted.LastSeq,
                receiptState = rejectedReceipt?.State.ToString(), receiptStatus = rejectedReceipt?.AckStatus,
                receiptOutcome = rejectedReceipt?.OutcomeKind, receiptReason = rejectedReceipt?.OutcomeReason,
                toolEvents = fixture.ReadEvents(sessionId).Count(item => item.Payload is ToolCallStarted or ToolCallSucceeded
                    or ToolCallFailed or ToolCallEffectUnknown) });
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, accepted.Outcome?.Kind);
            await WaitForPermissionRequestAsync(fixture, sessionId, actionRun);
            var permissionEvents = fixture.ReadEvents(sessionId);
            var permission = Assert.IsType<PermissionRequested>(Assert.Single(permissionEvents, item =>
                item.Envelope.RunId?.ToString() == actionRun && item.Payload is PermissionRequested).Payload);
            var interaction = Assert.IsType<InteractionRequested>(Assert.Single(permissionEvents, item =>
                item.Payload is InteractionRequested requested && requested.InteractionId == permission.InteractionId).Payload);
            Assert.DoesNotContain(permissionEvents, item => item.Envelope.ToolCallId == permission.ToolCallId
                && item.Payload is ToolCallStarted or ToolCallSucceeded or ToolCallEffectUnknown);
            var security = QueryRawHost(host, "sessionSecurity");
            var approval = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond", responseType = "choice",
                interactionId = interaction.InteractionId.ToString(), optionId = "allow_once",
                expectedRunId = actionRun,
                expectedPolicyRevision = security.GetProperty("revision").GetInt64(),
            }));
            var approvalAck = Ack(RawExchange(host, TrustedActionEnvelope(approval, fixture.KeyForTests,
                nonce, workspace, sessionId, actionRun)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, approvalAck.Outcome?.Kind);
            providerCountBeforeNativeAction = fixture.Provider.RequestCount;
            observations.Add(new { phase = "native_action_accepted", commandId = invocationId,
                requestHash, status = accepted.Status, outcome = accepted.Outcome?.Kind.ToString(),
                firstSequence = accepted.FirstSeq, lastSequence = accepted.LastSeq,
                permissionToolCallId = permission.ToolCallId.ToString(), permissionAsk = true,
                humanChoice = "allow_once", providerRequests = providerCountBeforeNativeAction });

            var barrierDocument = await WaitForProxyBarrierAsync(proxyBarrier, host, TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            proxyPid = barrierDocument.GetProperty("proxyPid").GetInt32();
            Assert.Equal(host.Id, barrierDocument.GetProperty("proxyParentPid").GetInt32());
            Assert.Equal(host.Id, barrierDocument.GetProperty("expectedHostPid").GetInt32());
            Assert.Equal(0, barrierDocument.GetProperty("gitExitCode").GetInt32());
            Assert.Equal(expectedInputHash, barrierDocument.GetProperty("stdinSha256").GetString());
            Assert.True(barrierDocument.GetProperty("cwdMatchesFixtureRepository").GetBoolean());
            var readyRecords = File.ReadAllLines(proxyLog)
                .Where(line => line.Contains("committed_waiting", StringComparison.Ordinal)).ToArray();
            Assert.Single(readyRecords);
            var readyLogSha256 = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(readyRecords[0] + "\n")));
            Assert.Equal(readyLogSha256, barrierDocument.GetProperty("readyLogSha256").GetString());
            Assert.False(host.HasExited, "The Host must still be waiting on the proxy when its post-commit barrier is observed.");
            using (var proxyProcess = Process.GetProcessById(proxyPid.Value))
            {
                Assert.False(proxyProcess.HasExited, "The owned proxy must be waiting after the real Git commit.");
                Assert.Equal(Path.GetFullPath(Path.Combine(proxyDirectoryPath, "git.exe")),
                    Path.GetFullPath(proxyProcess.MainModule!.FileName!));
            }
            var effectsAtBarrier = fixture.ReadEvents(sessionId).Where(item => item.Envelope.ToolCallId == permission.ToolCallId)
                .OrderBy(item => item.Envelope.Sequence).ToArray();
            Assert.Single(effectsAtBarrier, item => item.Payload is ToolCallStarted);
            Assert.DoesNotContain(effectsAtBarrier, item => item.Payload is ToolCallSucceeded or ToolCallFailed
                or ToolCallEffectUnknown or ToolCallReconciled);
            var refsAtBarrier = Git(identity.RepositoryRoot, "for-each-ref", "--format=%(refname)", "refs/omnicore/")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.DoesNotContain(previewReferences.Select(item => item.Name), name => refsAtBarrier.Contains(name, StringComparer.Ordinal));
            Assert.Equal(sentinelOid, Git(identity.RepositoryRoot, "rev-parse", "--verify", sentinelReference).Trim());
            Assert.Equal(retainedMetadata, File.ReadAllBytes(identity.OwnershipMetadataPath));
            Assert.False(Directory.Exists(identity.WorktreePath));
            Assert.Equal(2, fixture.Provider.RequestCount);
            observations.Add(new { phase = "real_git_commit_barrier_before_host_result", hostPid = host.Id,
                proxyPid, realGitPid = barrierDocument.GetProperty("realGitPid").GetInt32(),
                gitExitCode = 0, exactUpdateRefStdinHash = expectedInputHash,
                readyLogSha256,
                toolCallId = permission.ToolCallId.ToString(), startedBarrierDurable = true,
                terminalPresent = false, targetRefsGone = true, foreignSentinelPreserved = true,
                providerRequests = fixture.Provider.RequestCount });

            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            hostStopped = true;
            Assert.True(OwnedProxyHasExited(proxyPid.Value), "The verified proxy child must exit with the owned Host process tree.");
            if (hostStderr is not null) _ = await hostStderr.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            fixture.ClearOwnedSqlitePools();
            CopyOwnedFixture(fixture.RootDirectory, Path.Combine(artifactRoot, "after-host-termination-before-reopen"));
            observations.Add(new { phase = "host_tree_terminated", hostPid = host.Id, proxyPid,
                hostExited = host.HasExited, exactOwnedHostTreeOnly = true, noProcessNameKill = true });

            var admittedReceiptAfterTermination = Assert.IsType<CommandReceipt>(fixture.ReadReceipt(invocationId));
            Assert.Equal(CommandReceiptState.Completed, admittedReceiptAfterTermination.State);
            Assert.Equal("Accepted", admittedReceiptAfterTermination.OutcomeKind);
            Assert.Equal("deferred", admittedReceiptAfterTermination.ExecutionState);
            var killedEvents = fixture.ReadEvents(sessionId).Where(item => item.Envelope.ToolCallId == permission.ToolCallId)
                .ToArray();
            Assert.Single(killedEvents, item => item.Payload is ToolCallStarted);
            Assert.DoesNotContain(killedEvents, item => item.Payload is ToolCallSucceeded or ToolCallFailed
                or ToolCallEffectUnknown or ToolCallReconciled);
            observations.Add(new { phase = "offline_receipt_after_termination", admissionState = admittedReceiptAfterTermination.State.ToString(),
                admissionOutcome = admittedReceiptAfterTermination.OutcomeKind, executionState = admittedReceiptAfterTermination.ExecutionState,
                executionCode = admittedReceiptAfterTermination.ExecutionCode, startedCount = 1,
                succeededCount = 0, failedCount = 0, unknownTerminalCount = 0,
                semantics = "admission ACK is Accepted; effect result is not recorded and remains uncertain" });

            host = StartRetentionHost(cliDll, workspace, fixture.RootDirectory, proxyDirectoryPath,
                originalPath, bootstrapKey, useProxyPath: false);
            hostStderr = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var reopenedHello = RawExchange(host, WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"retention-crash-reopen\",\"payload\":{\"supportedVersions\":[\"1.0\"],\"capabilities\":[\"query.commandCatalog\",\"query.commandRecovery\",\"query.processSessions\",\"command.invoke.user\",\"command.invoke.continue.user\",\"command.run.mode.select.user\",\"command.conversation.interaction.choice.respond.user\",\"query.worktreeRetention\"]}}"));
            var reopenedNonce = ReadRawTrustedNonce(reopenedHello);
            var uncertain = QueryRawCommandResult(host, "conversation.result", invocationId, requestHash);
            var reopenedResultState = uncertain.GetProperty("state").GetString();
            var receiptAckState = QueryRawCommandResult(host, "command.result", invocationId, requestHash);
            var reopenedCommandState = receiptAckState.GetProperty("state").GetString();
            var reopenedAckOutcome = receiptAckState.GetProperty("ack").GetProperty("outcome").GetString();
            var recovery = QueryRawHost(host, "command.recovery");
            var recoveryEntry = Assert.Single(recovery.GetProperty("invocations").EnumerateArray(), item =>
                item.GetProperty("invocationId").GetString() == invocationId);
            var recoveryState = recoveryEntry.GetProperty("state").GetString();
            var recoveryReason = recoveryEntry.GetProperty("reasonCode").GetString();
            observations.Add(new { phase = "reopen_observed_incomplete_state_before_assertions",
                conversationResult = reopenedResultState, commandReceiptState = reopenedCommandState,
                admissionOutcome = reopenedAckOutcome, recoveryState, recoveryReason });
            Assert.Equal("deferred", reopenedResultState);
            Assert.Equal("completed", reopenedCommandState);
            Assert.Equal("Accepted", reopenedAckOutcome);
            Assert.Equal("blocked", recoveryState);
            Assert.Equal("execution_started_or_uncertain", recoveryReason);

            // Host startup recovery is allowed to classify the already-started effect as uncertain.
            // Capture this product-generated recovery separately from the quiescent post-kill state.
            var recoveryEvents = fixture.ReadEvents(sessionId).Where(item => item.Envelope.ToolCallId == permission.ToolCallId)
                .OrderBy(item => item.Envelope.Sequence).ToArray();
            Assert.Single(recoveryEvents, item => item.Payload is ToolCallStarted);
            Assert.Single(recoveryEvents, item => item.Payload is ToolCallEffectUnknown);
            var reconciledRetentionEffect = Assert.IsType<ToolCallReconciled>(Assert.Single(recoveryEvents,
                item => item.Payload is ToolCallReconciled).Payload);
            Assert.Equal(ReconciliationOutcome.Unresolvable, reconciledRetentionEffect.Outcome);
            Assert.DoesNotContain(recoveryEvents, item => item.Payload is ToolCallSucceeded or ToolCallFailed);
            var recoveryBaseline = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
            observations.Add(new { phase = "host_start_recovery_classification",
                toolCallId = permission.ToolCallId.ToString(), startedCount = 1, unknownCount = 1,
                reconciledCount = 1, reconciliationOutcome = reconciledRetentionEffect.Outcome.ToString(),
                succeededCount = 0, failedCount = 0, genericReconcileDetail = true });

            var reopenedCatalog = QueryRawHost(host, "command.catalog");
            var explicitContinueId = Ids.NewV7();
            var explicitContinue = WireEnvelope.Command(explicitContinueId, JsonSerializer.Serialize(new
            {
                cmd = "command.continue", invocationId, expectedSessionId = sessionId,
                expectedRunId = actionRun,
                expectedModeRevision = NullableJsonInt64(reopenedCatalog.GetProperty("modeRevision")),
                expectedPolicyRevision = reopenedCatalog.GetProperty("policyRevision").GetInt64(),
                expectedSelectionRevision = reopenedCatalog.GetProperty("selectionRevision").GetInt32(),
                expectedRoutingPolicyRevision = NullableJsonInt64(reopenedCatalog.GetProperty("routingPolicyRevision")),
                catalogRevision = reopenedCatalog.GetProperty("catalogRevision").GetString(),
                sourceFingerprint = descriptor.GetProperty("sourceFingerprint").GetString(),
            }));
            var sequenceBeforeBlockedContinue = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
            var blockedContinue = Ack(RawExchange(host, TrustedActionEnvelope(explicitContinue, fixture.KeyForTests,
                reopenedNonce, workspace, sessionId, actionRun)));
            observations.Add(new { phase = "same_invocation_explicit_continue_after_uncertain_start",
                status = blockedContinue.Status, error = blockedContinue.Error,
                outcome = blockedContinue.Outcome?.Kind.ToString(),
                journalSequenceBefore = sequenceBeforeBlockedContinue,
                journalSequenceAfter = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence) });
            Assert.Equal("command.recovery_blocked", blockedContinue.Error);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, blockedContinue.Outcome?.Kind);
            Assert.Equal(sequenceBeforeBlockedContinue, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
            var recoveryAfterBlockedContinue = QueryRawHost(host, "command.recovery");
            var blockedEntryAfterContinue = Assert.Single(recoveryAfterBlockedContinue.GetProperty("invocations").EnumerateArray(), item =>
                item.GetProperty("invocationId").GetString() == invocationId);
            Assert.Equal("blocked", blockedEntryAfterContinue.GetProperty("state").GetString());
            Assert.Equal("execution_started_or_uncertain", blockedEntryAfterContinue.GetProperty("reasonCode").GetString());
            var recoveredAdmissionAck = AckFromResult(receiptAckState.GetProperty("ack"));
            Assert.Equal(accepted.FirstSeq, recoveredAdmissionAck.FirstSeq);
            Assert.Equal(accepted.LastSeq, recoveredAdmissionAck.LastSeq);

            var sequenceBeforeReplay = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
            var currentRefNames = Git(identity.RepositoryRoot, "for-each-ref", "--format=%(refname)", "refs/omnicore/")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.DoesNotContain(previewReferences.Select(item => item.Name), item => currentRefNames.Contains(item, StringComparer.Ordinal));
            Assert.Equal(sentinelOid, Git(identity.RepositoryRoot, "rev-parse", "--verify", sentinelReference).Trim());

            var invalidReplayKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                var invalidReplay = Ack(RawExchange(host, TrustedActionEnvelope(invoke, invalidReplayKey,
                    reopenedNonce, workspace, sessionId, actionRun)));
                Assert.Equal("error", invalidReplay.Status);
                Assert.Equal("trusted_action_invalid", invalidReplay.Error);
            }
            finally { CryptographicOperations.ZeroMemory(invalidReplayKey); }

            var changedPayload = JsonNode.Parse(invoke.PayloadJson)!.AsObject();
            changedPayload["catalogRevision"] = new string('0', 64);
            var changedCommand = WireEnvelope.Command(invocationId, changedPayload.ToJsonString());
            var conflict = Ack(RawExchange(host, TrustedActionEnvelope(changedCommand, fixture.KeyForTests,
                reopenedNonce, workspace, sessionId, actionRun)));
            Assert.Equal("error", conflict.Status);
            Assert.Equal("command_id_conflict", conflict.Error);

            var passiveBaseline = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
            var replay = Ack(RawExchange(host, TrustedActionEnvelope(invoke, fixture.KeyForTests, reopenedNonce,
                workspace, sessionId, actionRun)));
            Assert.Equal(accepted.CommandId, replay.CommandId);
            Assert.Equal(accepted.Status, replay.Status);
            Assert.Equal(accepted.Outcome?.Kind, replay.Outcome?.Kind);
            Assert.Equal(accepted.FirstSeq, replay.FirstSeq);
            Assert.Equal(accepted.LastSeq, replay.LastSeq);
            Assert.Equal(passiveBaseline, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
            var finalReceipt = Assert.IsType<CommandReceipt>(fixture.ReadReceipt(invocationId));
            Assert.Equal("Accepted", finalReceipt.OutcomeKind);
            Assert.Equal("deferred", finalReceipt.ExecutionState);
            var finalResult = QueryRawCommandResult(host, "conversation.result", invocationId, requestHash);
            Assert.Equal("deferred", finalResult.GetProperty("state").GetString());
            var finalRecovery = QueryRawHost(host, "command.recovery");
            var finalRecoveryEntry = Assert.Single(finalRecovery.GetProperty("invocations").EnumerateArray(), item =>
                item.GetProperty("invocationId").GetString() == invocationId);
            Assert.Equal("blocked", finalRecoveryEntry.GetProperty("state").GetString());
            Assert.Equal("execution_started_or_uncertain", finalRecoveryEntry.GetProperty("reasonCode").GetString());
            var blockedContinueReceipt = QueryRawCommandResult(host, "command.result", explicitContinueId,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(explicitContinue.PayloadJson))));
            Assert.Equal("completed", blockedContinueReceipt.GetProperty("state").GetString());
            Assert.Equal("Rejected", blockedContinueReceipt.GetProperty("ack").GetProperty("outcome").GetString());
            var finalToolEvents = fixture.ReadEvents(sessionId).Where(item => item.Envelope.ToolCallId == permission.ToolCallId)
                .ToArray();
            Assert.Single(finalToolEvents, item => item.Payload is ToolCallStarted);
            Assert.Single(finalToolEvents, item => item.Payload is ToolCallEffectUnknown);
            Assert.Single(finalToolEvents, item => item.Payload is ToolCallReconciled reconciled
                && reconciled.Outcome == ReconciliationOutcome.Unresolvable);
            Assert.DoesNotContain(finalToolEvents, item => item.Payload is ToolCallSucceeded or ToolCallFailed);
            Assert.Equal(recoveryBaseline, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
            Assert.Equal(2, fixture.Provider.RequestCount);
            var proxyRecords = File.ReadAllLines(proxyLog)
                .Where(line => line.Contains("committed_waiting", StringComparison.Ordinal)).ToArray();
            Assert.Single(proxyRecords);
            observations.Add(new { phase = "reopen_deferred_blocked_and_passive_replay",
                conversationResult = finalResult.GetProperty("state").GetString(),
                commandReceiptState = receiptAckState.GetProperty("state").GetString(),
                commandRecoveryState = finalRecoveryEntry.GetProperty("state").GetString(),
                explicitContinueError = blockedContinue.Error,
                originalAdmissionOutcome = recoveredAdmissionAck.Outcome?.Kind.ToString(),
                invalidProof = "trusted_action_invalid", changedPayload = "command_id_conflict",
                replayStatus = replay.Status, replayOutcome = replay.Outcome?.Kind.ToString(),
                replayAckMatchesOriginal = true, journalSequenceStableDuringReplay = true,
                startedCount = 1, unknownCount = 1, reconciledCount = 1,
                reconciliationOutcome = reconciledRetentionEffect.Outcome.ToString(),
                succeededCount = 0, failedCount = 0, providerRequests = fixture.Provider.RequestCount,
                targetRefsRemainAbsent = true, unrelatedSentinelPreserved = true,
                exactlyOnceForNewCommandId = false, gitReaderAtomicityClaimed = false });

            host.StandardInput.Close();
            await host.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            hostStopped = true;
            if (hostStderr is not null) _ = await hostStderr.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            host.Dispose();
            host = null;
            fixture.ClearOwnedSqlitePools();
            CopyOwnedFixture(fixture.RootDirectory, Path.Combine(artifactRoot, "after-reopen-passive-replay"));
            terminalFailure = null;
        }
        catch (Exception error)
        {
            terminalFailure = error.GetType().Name + ": " + error.Message;
            observations.Add(new { phase = "candidate_failure", exceptionType = error.GetType().Name,
                safeMessage = fixture is null ? error.GetType().Name
                    : error.Message.Replace(fixture.BootstrapKeyForOwnedProcess, "[redacted-key]", StringComparison.OrdinalIgnoreCase) });
            throw;
        }
        finally
        {
            var cleanupFailure = false;
            if (host is not null)
            {
                try
                {
                    if (!host.HasExited) host.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await host.WaitForExitAsync(cleanup.Token);
                    hostStopped = true;
                }
                catch (Exception error)
                {
                    cleanupFailure = true;
                    observations.Add(new { phase = "owned_host_cleanup_failure", exceptionType = error.GetType().Name });
                }
                try
                {
                    if (hostStderr is not null)
                    {
#pragma warning disable xUnit1051 // Cleanup uses its own bounded wait and must survive a cancelled test token.
                        var stderr = await hostStderr.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
#pragma warning restore xUnit1051
                        observations.Add(new { phase = "host_stderr_digest", utf8Bytes = Encoding.UTF8.GetByteCount(stderr),
                            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stderr))) });
                    }
                }
                catch (Exception error)
                {
                    cleanupFailure = true;
                    observations.Add(new { phase = "host_stderr_capture_failure", exceptionType = error.GetType().Name });
                }
                try { host.Dispose(); }
                catch (Exception error)
                {
                    cleanupFailure = true;
                    observations.Add(new { phase = "owned_host_handle_dispose_failure", exceptionType = error.GetType().Name });
                }
            }

            if (fixture is not null)
            {
                if (!fixtureHostStopped)
                {
                    try
                    {
                        await fixture.StopHostAsync();
                        fixtureHostStopped = true;
                        observations.Add(new { phase = "inprocess_fixture_host_stopped_before_capture" });
                    }
                    catch (Exception error)
                    {
                        cleanupFailure = true;
                        observations.Add(new { phase = "inprocess_fixture_host_stop_failure", exceptionType = error.GetType().Name });
                    }
                }

                if (hostStopped && fixtureHostStopped && Directory.Exists(fixture.RootDirectory))
                {
                    try
                    {
                        fixture.ClearOwnedSqlitePools();
                        observations.Add(new { phase = "owned_fixture_sqlite_pools_cleared",
                            exactDatabases = new[] { "data/user.db", "data/spend-reservations.db", "workspace journal.db" } });
                        var recoveryCapture = Path.Combine(artifactRoot,
                            terminalFailure is null ? "final-fixture" : "failure-fixture-after-owned-host-stop");
                        if (!Directory.Exists(recoveryCapture))
                            CopyOwnedFixture(fixture.RootDirectory, recoveryCapture);
                        evidenceSnapshotVerified = VerifyOwnedSnapshot(recoveryCapture);
                        observations.Add(new { phase = "owned_fixture_snapshot_verified",
                            capture = Path.GetFileName(recoveryCapture), evidenceSnapshotVerified });
                    }
                    catch (Exception error)
                    {
                        cleanupFailure = true;
                        observations.Add(new { phase = "fixture_capture_or_sqlite_backup_failure",
                            exceptionType = error.GetType().Name });
                    }
                }
                fixture.PreserveRootOnDispose = true;
            }

            observations.Add(new { phase = "candidate_final", terminalFailure,
                hostStopped, fixtureHostStopped, evidenceSnapshotVerified, cleanupFailure,
                providerRequestsBeforeNativeAction = providerCountBeforeNativeAction,
                targetCommandId = invocationId, targetRequestHash = requestHash,
                workspace = workspace is null ? null : "[owned temporary workspace]",
                sessionId, actionRun });

            void WriteAttempt() => File.WriteAllLines(Path.Combine(artifactRoot, "attempt.jsonl"),
                observations.Select(item => JsonSerializer.Serialize(item)), new UTF8Encoding(false));

            void SealArtifact()
            {
                WriteAttempt();
                WriteSafeArtifactManifest(artifactRoot);
                manifestVerified = VerifySafeArtifactManifest(artifactRoot);
                if (!manifestVerified) throw new InvalidDataException("Fixture evidence manifest verification failed.");
            }

            try
            {
                if (evidenceSnapshotVerified && !cleanupFailure)
                {
                    SealArtifact();
                    observations.Add(new { phase = "artifact_manifest_verified", manifestVerified });
                    SealArtifact();
                }
                else WriteAttempt();
            }
            catch (Exception error)
            {
                cleanupFailure = true;
                if (fixture is not null) fixture.PreserveRootOnDispose = true;
                try
                {
                    observations.Add(new { phase = "artifact_manifest_or_attempt_write_failure",
                        exceptionType = error.GetType().Name });
                    File.WriteAllLines(Path.Combine(artifactRoot, "attempt.jsonl"),
                        observations.Select(item => JsonSerializer.Serialize(item)), new UTF8Encoding(false));
                }
                catch { /* Preserve the original evidence-writing failure and retain the source fixture. */ }
            }

            if (fixture is not null)
            {
                fixture.PreserveRootOnDispose = terminalFailure is not null || cleanupFailure
                    || !hostStopped || !fixtureHostStopped || !evidenceSnapshotVerified || !manifestVerified;
                try
                {
                    await fixture.DisposeAsync();
                }
                catch (Exception error)
                {
                    cleanupFailure = true;
                    fixture.PreserveRootOnDispose = true;
                    observations.Add(new { phase = "fixture_dispose_failure", exceptionType = error.GetType().Name });
                    try
                    {
                        if (evidenceSnapshotVerified) SealArtifact();
                        else WriteAttempt();
                    }
                    catch (Exception sealingError)
                    {
                        observations.Add(new { phase = "final_failure_artifact_seal_failure",
                            exceptionType = sealingError.GetType().Name });
                        try { WriteAttempt(); } catch { /* The original fixture root remains preserved. */ }
                    }
                }
            }

            if (terminalFailure is null && (!hostStopped || !fixtureHostStopped
                    || !evidenceSnapshotVerified || !manifestVerified))
                cleanupFailure = true;
            if (cleanupFailure && terminalFailure is null)
                throw new InvalidOperationException("Owned fixture teardown or evidence sealing failed; the source fixture was retained.");
        }
    }

    private static void CopySandboxProxyFiles(string destination)
    {
        var output = AppContext.BaseDirectory;
        var helper = Path.Combine(output, "OmniCore.SandboxTestProcess.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("Sandbox process helper apphost is missing.", helper);
        foreach (var file in Directory.EnumerateFiles(output, "*.dll", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(output, "*.json", SearchOption.TopDirectoryOnly))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        File.Copy(helper, Path.Combine(destination, "git.exe"), overwrite: false);
    }

    private static void WriteRetentionProxyConfig(string path, string fixtureRoot, string repositoryRoot,
        string gitExecutable, string barrierPath, string logPath, string expectedInputSha256,
        string[] allowedReferences, string[] expectedObjectIds, int expectedHostPid)
    {
        var configuration = new
        {
            Version = 1,
            FixtureRoot = Path.GetFullPath(fixtureRoot),
            FixtureTempRoot = Path.GetFullPath(Path.GetTempPath()),
            RepositoryRoot = Path.GetFullPath(repositoryRoot),
            GitExecutable = Path.GetFullPath(gitExecutable),
            BarrierPath = Path.GetFullPath(barrierPath),
            LogPath = Path.GetFullPath(logPath),
            ExpectedInputSha256 = expectedInputSha256,
            AllowedReferences = allowedReferences,
            ExpectedObjectIds = expectedObjectIds,
            ExpectedHostPid = expectedHostPid,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(configuration), new UTF8Encoding(false));
    }

    private static Process StartRetentionHost(string cliDll, string workspace, string fixtureRoot,
        string proxyDirectory, string originalPath, string bootstrapKey, bool useProxyPath = true)
    {
        var dotnet = new SystemExecutableResolver().Resolve("dotnet", workspace).ResolvedPath;
        var root = Path.GetFullPath(fixtureRoot);
        var home = Path.Combine(root, "home");
        var agentHome = Path.Combine(root, "agent-home");
        var temp = Path.Combine(root, "temp");
        var roaming = Path.Combine(home, "AppData", "Roaming");
        var local = Path.Combine(home, "AppData", "Local");
        var packages = Path.Combine(root, "nuget-packages");
        foreach (var directory in new[] { home, agentHome, temp, roaming, local, packages,
                     Path.Combine(agentHome, ".codex"), Path.Combine(agentHome, ".claude"),
                     Path.Combine(agentHome, ".config"), Path.Combine(agentHome, ".config", "opencode"),
                     Path.Combine(home, ".dotnet") })
            Directory.CreateDirectory(directory);

        var pathValue = useProxyPath ? proxyDirectory + Path.PathSeparator + originalPath : originalPath;
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment.Clear();
        SetIfPresent(start, "SystemRoot", Environment.GetEnvironmentVariable("SystemRoot"));
        SetIfPresent(start, "WINDIR", Environment.GetEnvironmentVariable("WINDIR"));
        start.Environment["PATH"] = pathValue;
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment["OMNICORE_DATA_DIR"] = Path.Combine(root, "data");
        start.Environment["OMNICORE_CONFIG_DIR"] = Path.Combine(root, "config");
        start.Environment["OMNICORE_AGENT_HOME"] = agentHome;
        start.Environment["CODEX_HOME"] = Path.Combine(agentHome, ".codex");
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(agentHome, ".claude");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(agentHome, ".config");
        start.Environment["OPENCODE_CONFIG_DIR"] = Path.Combine(agentHome, ".config", "opencode");
        start.Environment["HOME"] = home;
        start.Environment["USERPROFILE"] = home;
        start.Environment["APPDATA"] = roaming;
        start.Environment["LOCALAPPDATA"] = local;
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(home, ".dotnet");
        start.Environment["NUGET_PACKAGES"] = packages;
        start.Environment["OMNICORE_TRUSTED_ACTION_BOOTSTRAP"] = bootstrapKey;
        start.ArgumentList.Add(Path.GetFullPath(cliDll));
        start.ArgumentList.Add("host");
        start.ArgumentList.Add("--stdio");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned stdio Host process.");

        static void SetIfPresent(ProcessStartInfo info, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) info.Environment[name] = value;
        }
    }

    private static JsonElement QueryRawHost(Process host, string name)
    {
        var response = RawExchange(host, WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        { operation = "query", name })));
        using var payload = JsonDocument.Parse(response.PayloadJson);
        using var result = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
        return result.RootElement.Clone();
    }

    private static JsonElement QueryRawCommandResult(Process host, string operation, string commandId, string requestHash)
    {
        var response = RawExchange(host, WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        { operation, commandId, requestHash })));
        using var payload = JsonDocument.Parse(response.PayloadJson);
        return payload.RootElement.Clone();
    }

    private static string ReadRawTrustedNonce(WireEnvelope hello)
    {
        using var payload = JsonDocument.Parse(hello.PayloadJson);
        return payload.RootElement.GetProperty("trustedActionNonce").GetString()
            ?? throw new InvalidDataException("Host omitted the trusted action nonce.");
    }

    private static async Task<JsonElement> WaitForProxyBarrierAsync(string path, Process host, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.HasExited) throw new InvalidOperationException("Owned Host exited before the post-Git barrier.");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                return document.RootElement.Clone();
            }
            await Task.Delay(25, cancellationToken);
        }
        throw new TimeoutException("Fixture Git proxy did not reach the exact post-update-ref barrier.");
    }

    private static bool OwnedProxyHasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private static async Task WaitForTurnCompletedAsync(NativeCommandHandoffFixture fixture, string sessionId,
        string runId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (fixture.ReadEvents(sessionId).Any(item => item.Envelope.RunId?.ToString() == runId
                && item.Payload is TurnCompleted)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The fixture authorization Run did not complete its Turn.");
    }

    private static CommandAck AckFromResult(JsonElement ack)
    {
        var outcome = ack.GetProperty("outcome").GetString();
        return new CommandAck(ack.GetProperty("commandId").GetString()!, ack.GetProperty("status").GetString()!,
            null, outcome == "Accepted" ? RuntimeCommandOutcome.Accepted() : RuntimeCommandOutcome.Rejected(),
            ack.TryGetProperty("firstSeq", out var first) && first.ValueKind == JsonValueKind.Number ? first.GetInt64() : null,
            ack.TryGetProperty("lastSeq", out var last) && last.ValueKind == JsonValueKind.Number ? last.GetInt64() : null);
    }

    private static void CopyOwnedFixture(string source, string destination)
    {
        var canonicalSource = Path.GetFullPath(source);
        if (!Path.GetFileName(canonicalSource).StartsWith("omni-native-command-handoff-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to copy a non-fixture directory.");
        var canonicalDestination = Path.GetFullPath(destination);
        if (Directory.Exists(canonicalDestination) || File.Exists(canonicalDestination))
            throw new IOException("Fixture evidence destination already exists.");
        var staging = canonicalDestination + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        var databasePaths = new List<string>();
        CopyDirectory(canonicalSource, staging, databasePaths);

        var backupRecords = new List<object>();
        foreach (var databasePath in databasePaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(canonicalSource, databasePath);
            var target = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string integrityResult;
            using (var input = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()))
            using (var output = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = target,
                Pooling = false,
            }.ToString()))
            {
                input.Open();
                output.Open();
                input.BackupDatabase(output);
                using var integrity = output.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check";
                integrityResult = Convert.ToString(integrity.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty;
                if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SQLite backup failed integrity_check for " + relative + ".");
            }
            foreach (var sidecar in new[] { target + "-wal", target + "-shm", target + "-journal" })
                if (File.Exists(sidecar)) File.Delete(sidecar);
            backupRecords.Add(new
            {
                relativePath = relative.Replace('\\', '/'),
                integrityCheck = integrityResult,
                sourceWalPresent = File.Exists(databasePath + "-wal"),
                backupSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))),
                backupBytes = new FileInfo(target).Length,
            });
        }

        File.WriteAllText(Path.Combine(staging, "sqlite-backups.json"),
            JsonSerializer.Serialize(backupRecords), new UTF8Encoding(false));
        Directory.Move(staging, canonicalDestination);

        static void CopyDirectory(string current, string target, List<string> databases)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixture evidence contains a reparse directory.");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Fixture evidence contains a reparse file.");
                if (IsSqliteDatabase(file))
                {
                    databases.Add(file);
                    continue;
                }
                if (IsSqliteSidecar(file)) continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
            }
            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), databases);
        }

        static bool IsSqliteDatabase(string path) => Path.GetExtension(path).ToLowerInvariant() is ".db" or ".sqlite" or ".sqlite3";
        static bool IsSqliteSidecar(string path) =>
            new[] { "-wal", "-shm", "-journal" }.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool VerifyOwnedSnapshot(string snapshotRoot)
    {
        var reportPath = Path.Combine(snapshotRoot, "sqlite-backups.json");
        if (!File.Exists(reportPath)) return false;
        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        var reportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in report.RootElement.EnumerateArray())
        {
            var relative = row.GetProperty("relativePath").GetString();
            if (string.IsNullOrWhiteSpace(relative)) return false;
            var path = Path.GetFullPath(Path.Combine(snapshotRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(snapshotRoot).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path)) return false;
            var expected = row.GetProperty("backupSha256").GetString();
            var beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(expected, beforeHash, StringComparison.OrdinalIgnoreCase)) return false;
            var sidecars = new[] { path + "-wal", path + "-shm", path + "-journal" };
            if (sidecars.Any(File.Exists)) return false;
            string? integrityResult;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()))
            {
                connection.Open();
                using var check = connection.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                integrityResult = Convert.ToString(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase)) return false;
            var wal = path + "-wal";
            var journal = path + "-journal";
            if ((File.Exists(wal) && new FileInfo(wal).Length != 0)
                || (File.Exists(journal) && new FileInfo(journal).Length != 0)) return false;
            // A read-only WAL-mode integrity check can create an empty WAL and a 32 KiB
            // SHM index in this owned copy. Sidecars were absent before opening; after
            // all connections close, only the WAL/journal must contain no data before
            // removing these reader-created artifacts from the sealed copy.
            foreach (var sidecar in new[] { wal, journal })
                if (File.Exists(sidecar)) File.Delete(sidecar);
            var shm = path + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
            var afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(expected, afterHash, StringComparison.OrdinalIgnoreCase)) return false;
            if (!reportedPaths.Add(path)) return false;
            foreach (var sidecar in sidecars)
                if (File.Exists(sidecar)) return false;
        }
        var databaseFiles = Directory.EnumerateFiles(snapshotRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".db" or ".sqlite" or ".sqlite3")
            .Select(path => Path.GetFullPath(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return reportedPaths.Count > 0 && reportedPaths.SetEquals(databaseFiles);
    }

    private static void WriteSafeArtifactManifest(string artifactRoot)
    {
        var root = Path.GetFullPath(artifactRoot);
        var manifestPath = Path.Combine(root, "fixture-sha256.txt");
        var temporaryPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var records = EnumerateFiles(root)
            .Where(path => !string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, temporaryPath, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + "  "
                + Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToArray();
        File.WriteAllLines(temporaryPath, records, new UTF8Encoding(false));
        File.Move(temporaryPath, manifestPath, overwrite: true);

        static IEnumerable<string> EnumerateFiles(string current)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Artifact tree contains a reparse directory.");
            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Artifact tree contains a reparse file.");
                yield return file;
            }
            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                foreach (var file in EnumerateFiles(directory)) yield return file;
        }
    }

    private static bool VerifySafeArtifactManifest(string artifactRoot)
    {
        var root = Path.GetFullPath(artifactRoot);
        var manifestPath = Path.Combine(root, "fixture-sha256.txt");
        if (!File.Exists(manifestPath)) return false;
        var records = File.ReadAllLines(manifestPath);
        var expectedFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (records.Length != expectedFiles.Length) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var separator = record.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64) return false;
            var relative = record[(separator + 2)..].Replace('/', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !seen.Add(path) || !File.Exists(path)) return false;
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(record[..separator], actual, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return seen.Count == expectedFiles.Length;
    }
}
