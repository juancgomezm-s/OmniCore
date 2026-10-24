using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public void Resumed_permission_rechecks_do_not_authorize_until_a_grant_is_recorded()
    {
        var callId = ToolCallId.New();
        var interactionId = InteractionId.New();
        var state = StateMachines.ApplyToolCall(ToolCallState.Requested,
            new ToolCallPrepared(callId, "{}"));
        state = StateMachines.ApplyToolCall(state,
            new PermissionEvaluated(callId, PermissionDecision.Ask, "{}", null));
        state = StateMachines.ApplyToolCall(state, new PermissionRequested(callId, interactionId));
        Assert.Equal(ToolCallState.AwaitingPermission, state);

        var reevaluated = StateMachines.ApplyToolCall(state,
            new PermissionEvaluated(callId, PermissionDecision.Allow, "{}", null));
        Assert.Equal(ToolCallState.AwaitingPermission, reevaluated);
        Assert.Throws<InvalidStateTransitionException>(() => StateMachines.ApplyToolCall(reevaluated,
            new ToolCallStarted(callId, EffectClass.NonIdempotent, null)));

        var authorized = StateMachines.ApplyToolCall(reevaluated,
            new PermissionGranted(callId, null, GrantLifetime.Once));
        Assert.Equal(ToolCallState.Authorized, authorized);
        authorized = StateMachines.ApplyToolCall(authorized, new ToolCallAuthorized(callId));
        Assert.Equal(ToolCallState.Authorized, authorized);
        Assert.Equal(ToolCallState.Started, StateMachines.ApplyToolCall(authorized,
            new ToolCallStarted(callId, EffectClass.NonIdempotent, null)));

        var denied = StateMachines.ApplyToolCall(state, new PermissionDenied(callId, "denied"));
        Assert.Equal(ToolCallState.Rejected, denied);
        Assert.Throws<InvalidStateTransitionException>(() => StateMachines.ApplyToolCall(denied,
            new ToolCallStarted(callId, EffectClass.NonIdempotent, null)));
    }

    [Fact]
    public async Task Legacy_hello_keeps_questionnaire_without_advertising_native_choice_recovery()
    {
        var fixture = await NativeChoiceFixture.StartAsync("permission", requestNativeChoices: false);
        try
        {
            Assert.Contains("command.conversation.questionnaire.respond.user", fixture.NegotiatedCapabilities);
            Assert.DoesNotContain("command.conversation.interaction.choice.respond.user", fixture.NegotiatedCapabilities);
            Assert.DoesNotContain("command.conversation.interaction.continue.user", fixture.NegotiatedCapabilities);
            Assert.DoesNotContain("query.interactionRecovery", fixture.NegotiatedCapabilities);

            await fixture.RestartHostAsync(requestNativeChoices: true);
            Assert.Contains("command.conversation.questionnaire.respond.user", fixture.NegotiatedCapabilities);
            Assert.Contains("command.conversation.interaction.choice.respond.user", fixture.NegotiatedCapabilities);
            Assert.Contains("command.conversation.interaction.continue.user", fixture.NegotiatedCapabilities);
            Assert.Contains("query.interactionRecovery", fixture.NegotiatedCapabilities);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public void NoClient_ask_denies_even_when_resolver_exists_without_native_defer_opt_in()
    {
        var events = new List<DomainEventPayload>();
        var resolverCalls = 0;
        var runtime = ToolRuntime.For(FakeCatalog.Default(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Ask),
            payload => { events.Add(payload); return VoidBox.Instance; });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("fake.write"), "no-client-ask", "{}");
        var context = new ToolExecutionContext("sim", null, events.Add,
            _ => { resolverCalls++; return "allow_once"; },
            isInteractive: false, allowDeferredInteraction: false);

        var outcome = runtime.Run(call, new ToolPreparationContext("sim", DateTimeOffset.UtcNow),
            context, userApprovesAsk: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.Equal(EffectOutcome.None, outcome.Effect);
        Assert.Equal(0, resolverCalls);
        Assert.Contains(events, item => item is InteractionResolved { OptionId: "deny", Cause: InteractionCause.NoClient });
        Assert.Contains(events, item => item is PermissionDenied);
        Assert.DoesNotContain(events, item => item is ToolCallSucceeded);
    }

    [Fact]
    public void Resumed_process_permission_denies_when_real_resolver_changes_effective_claim()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "omni-native-choice-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var resolver = new ChangingExecutableResolver("C:\\approved\\tool.exe", "C:\\changed\\tool.exe");
            var launcher = new CountingSandboxLauncher();
            var tool = new ProcessExecTool(launcher, new PathBoundaryValidator(), SandboxStrength.Weak);
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(new FakeCatalog().Add(tool),
                ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Ask),
                payload => { events.Add(payload); return VoidBox.Instance; }, null, resolver);
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "claim-change",
                JsonSerializer.Serialize(new
                {
                    executable = "tool-from-path",
                    argv = new[] { "--version" },
                    cwd = ".",
                    timeoutSeconds = 5,
                }));
            var preparation = new ToolPreparationContext(workspace, DateTimeOffset.UtcNow);
            var execution = new ToolExecutionContext(workspace, null, events.Add,
                isInteractive: false, allowDeferredInteraction: true);

            var initial = runtime.Run(call, preparation, execution, userApprovesAsk: false,
                CancellationToken.None);
            Assert.Equal(ToolCallState.AwaitingPermission, initial.FinalState);
            var request = Assert.Single(events.OfType<InteractionRequested>());
            using var subject = JsonDocument.Parse(request.SubjectJson);
            var fingerprint = subject.RootElement.GetProperty("intentFingerprint").GetString();
            Assert.False(string.IsNullOrWhiteSpace(fingerprint));

            var resumed = runtime.ResumeApprovedPermission(call, preparation, execution,
                CancellationToken.None, request.InteractionId, "allow_once", call.NormalizedArgumentsJson,
                fingerprint!);

            Assert.False(resumed.Succeeded);
            Assert.Equal(ToolCallState.Rejected, resumed.FinalState);
            Assert.Equal(EffectOutcome.None, resumed.Effect);
            Assert.Equal(2, resolver.ResolveCount);
            Assert.Equal(0, launcher.Starts);
            Assert.Contains(events, item => item is PermissionDenied);
            Assert.DoesNotContain(events, item => item is ToolCallStarted);
            Assert.DoesNotContain(events, item => item is ToolCallSucceeded);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("allow_once", true)]
    [InlineData("deny", false)]
    public async Task Native_permission_choice_resumes_exact_turn_with_one_effect_and_receipt(
        string optionId, bool shouldWrite)
    {
        var fixture = await NativeChoiceFixture.StartAsync("permission");
        try
        {
            var pending = await fixture.StartAndWaitForInteractionAsync(InteractionKind.Permission);
            var available = ParseOptionIds(pending.Request.OptionsJson);
            Assert.Contains("allow_once", available);
            Assert.Contains("deny", available);
            Assert.Contains(optionId, available);

            var forgedKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                var forged = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                    pending.PolicyRevision, optionId, Ids.NewV7());
                var forgedAck = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(forged,
                    forgedKey, fixture.Nonce!, fixture.Workspace, fixture.CurrentSessionId, pending.RunId)));
                Assert.Equal(RuntimeCommandOutcomeKind.Rejected, forgedAck.Outcome?.Kind);
                Assert.False(File.Exists(fixture.MarkerPath));
            }
            finally { CryptographicOperations.ZeroMemory(forgedKey); }

            // A correctly signed command for a different Session cannot consume this interaction.
            var foreignSession = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                pending.PolicyRevision, optionId, Ids.NewV7());
            var foreignAck = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(foreignSession,
                fixture.Key, fixture.Nonce!, fixture.Workspace, Guid.NewGuid().ToString("D"), pending.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, foreignAck.Outcome?.Kind);
            Assert.False(File.Exists(fixture.MarkerPath));

            // A stale policy revision is rejected without consuming the pending choice.
            var stale = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                pending.PolicyRevision + 1, optionId, Ids.NewV7());
            var staleAck = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(stale,
                fixture.Key, fixture.Nonce!, fixture.Workspace, fixture.CurrentSessionId, pending.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, staleAck.Outcome?.Kind);
            Assert.False(File.Exists(fixture.MarkerPath));

            var response = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                pending.PolicyRevision, optionId, Ids.NewV7());
            var proof = TrustedActionEnvelope(response, fixture.Key, fixture.Nonce!, fixture.Workspace,
                fixture.CurrentSessionId, pending.RunId);
            Assert.NotEqual(response.MessageId, proof.MessageId);
            var ack = Ack(RawExchange(fixture.Host!, proof));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
            var duplicate = Ack(RawExchange(fixture.Host!, proof));
            Assert.Equal(ack.Status, duplicate.Status);
            Assert.Equal(ack.FirstSeq, duplicate.FirstSeq);
            Assert.Equal(ack.LastSeq, duplicate.LastSeq);

            try
            {
                await fixture.Provider.WaitForRequestAsync(3, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException error)
            {
                var safeEvents = fixture.ReadEvents()
                    .Where(item => item.Envelope.RunId == RunId.Parse(pending.RunId))
                    .Select(item =>
                    {
                        var code = item.Payload switch
                        {
                            ToolCallRejected rejected => rejected.ErrorCode?.ToString(),
                            ToolCallFailed failed => failed.ErrorCode?.ToString(),
                            _ => null,
                        };
                        var toolName = item.Payload is ToolCallRequested requested ? ":tool=" + requested.ToolName : string.Empty;
                        return $"{item.Envelope.Sequence}:{item.Payload.GetType().Name}{toolName}{(code is null ? string.Empty : ":code=" + code)}";
                    }).TakeLast(24);
                var receipt = fixture.ReadReceipt(response.MessageId);
                var result = fixture.ConversationResult(response);
                var hostDiagnostics = await fixture.StopAndCaptureHostDiagnosticsAsync();
                throw new TimeoutException($"Native choice did not continue provider after response. requests={fixture.Provider.RequestCount}; choiceReceipt={receipt?.State}/{receipt?.OutcomeKind}/{receipt?.ExecutionState}/{receipt?.ExecutionCode}; conversationResult={result.GetProperty("state").GetString()}/{(result.TryGetProperty("code", out var resultCode) ? resultCode.GetString() : null)}; hostDiagnostics=[{hostDiagnostics}]; events=[{string.Join(";", safeEvents)}]", error);
            }
            await fixture.WaitForTurnCompletionAsync(pending.RunId, pending.TurnId);
            Assert.Equal(3, fixture.Provider.RequestCount);
            Assert.Equal(shouldWrite, File.Exists(fixture.MarkerPath));
            if (shouldWrite) Assert.Equal("permission-approved", File.ReadAllText(fixture.MarkerPath));

            var events = fixture.ReadEvents();
            Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                && item.Payload is InteractionResolved resolved
                && resolved.InteractionId == pending.Request.InteractionId
                && resolved.OptionId == optionId);
            var write = Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                && item.Payload is ToolCallRequested requested && requested.ToolName == "filesystem.write");
            var writeCallId = ((ToolCallRequested)write.Payload).ToolCallId;
            var writeEvaluations = events.Where(item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                && item.Payload is PermissionEvaluated evaluated && evaluated.ToolCallId == writeCallId)
                .Select(item => (PermissionEvaluated)item.Payload).ToArray();
            Assert.NotEmpty(writeEvaluations);
            Assert.All(writeEvaluations, evaluation => Assert.Null(evaluation.AppliedGrant));
            if (shouldWrite)
            {
                Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                    && item.Payload is PermissionGranted granted && granted.Lifetime == GrantLifetime.Once);
                Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                    && item.Envelope.ToolCallId == writeCallId
                    && item.Payload is ToolCallSucceeded);
            }
            else
            {
                Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                    && item.Payload is PermissionDenied);
                Assert.DoesNotContain(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                    && item.Envelope.ToolCallId == writeCallId && item.Payload is ToolCallSucceeded);
            }
            Assert.Contains(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                && item.Envelope.TurnId == pending.TurnId && item.Payload is TurnCompleted);
            CanonicalStateTracker.Replay(EventCodecs.Create(), fixture.ReadEnvelopes());
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Native_pending_permission_survives_host_restart_without_replaying_provider_request()
    {
        var fixture = await NativeChoiceFixture.StartAsync("permission");
        try
        {
            var pending = await fixture.StartAndWaitForInteractionAsync(InteractionKind.Permission);
            Assert.Equal(2, fixture.Provider.RequestCount);
            await fixture.RestartHostAsync();
            var restartedScope = ReadCurrentScope(fixture.Host!);
            Assert.Equal(fixture.CurrentSessionId, restartedScope.SessionId);
            Assert.Equal(pending.RunId, restartedScope.RunId);
            Assert.Equal(2, fixture.Provider.RequestCount);

            var response = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                pending.PolicyRevision, "allow_once", Ids.NewV7());
            var proof = TrustedActionEnvelope(response, fixture.Key, fixture.Nonce!, fixture.Workspace,
                fixture.CurrentSessionId, pending.RunId);
            var ack = Ack(RawExchange(fixture.Host!, proof));
            if (ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted)
            {
                var startReceipt = fixture.ReadReceipt(fixture.LastStartCommandId!);
                var safeEvents = fixture.ReadEvents()
                    .Select(item =>
                    {
                        var code = item.Payload switch
                        {
                            ToolCallRejected rejected => rejected.ErrorCode?.ToString(),
                            ToolCallFailed failed => failed.ErrorCode?.ToString(),
                            _ => null,
                        };
                        return $"{item.Envelope.Sequence}:{item.Payload.GetType().Name}{(code is null ? string.Empty : ":code=" + code)}";
                    }).TakeLast(20);
                Assert.Fail($"Restarted permission response was not accepted. status={ack.Status}; error={ack.Error}; outcome={ack.Outcome?.Kind}; startReceipt={startReceipt?.State}/{startReceipt?.OutcomeKind}/{startReceipt?.ExecutionState}/{startReceipt?.ExecutionCode}; events=[{string.Join(";", safeEvents)}]");
            }
            var duplicate = Ack(RawExchange(fixture.Host!, proof));
            Assert.Equal(ack.FirstSeq, duplicate.FirstSeq);
            Assert.Equal(ack.LastSeq, duplicate.LastSeq);
            await fixture.Provider.WaitForRequestAsync(3, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await fixture.WaitForTurnCompletionAsync(pending.RunId, pending.TurnId);
            Assert.Equal(3, fixture.Provider.RequestCount);
            Assert.Equal("permission-approved", File.ReadAllText(fixture.MarkerPath));
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Two_successive_permission_choices_resume_the_same_run_and_turn_once_each()
    {
        var fixture = await NativeChoiceFixture.StartAsync("permission-sequential");
        try
        {
            var first = await fixture.StartAndWaitForInteractionAsync(InteractionKind.Permission);
            var firstCommand = fixture.ChoiceCommand(first.Request.InteractionId, first.RunId,
                first.PolicyRevision, "allow_once", Ids.NewV7());
            var firstAck = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(firstCommand, fixture.Key,
                fixture.Nonce!, fixture.Workspace, fixture.CurrentSessionId, first.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, firstAck.Outcome?.Kind);

            await fixture.Provider.WaitForRequestAsync(3, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            InteractionRequested? second = null;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline && second is null)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                second = fixture.ReadEvents().Select(item => item.Payload).OfType<InteractionRequested>()
                    .SingleOrDefault(request => request.Kind == InteractionKind.Permission
                        && request.InteractionId != first.Request.InteractionId);
                if (second is null) await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            Assert.NotNull(second);
            var beforeSecondAnswer = fixture.ReadEvents();
            var firstResolved = Assert.Single(beforeSecondAnswer, item => item.Payload is InteractionResolved resolved
                && resolved.InteractionId == first.Request.InteractionId);
            var secondWrite = Assert.Single(beforeSecondAnswer, item => item.Payload is ToolCallRequested requested
                && requested.ToolName == "filesystem.write"
                && item.Envelope.Sequence > firstResolved.Envelope.Sequence);
            Assert.Equal(first.RunId, secondWrite.Envelope.RunId?.ToString());
            Assert.Equal(first.TurnId, secondWrite.Envelope.TurnId);

            var secondCommand = fixture.ChoiceCommand(second!.InteractionId, first.RunId,
                first.PolicyRevision, "allow_once", Ids.NewV7());
            var secondProof = TrustedActionEnvelope(secondCommand, fixture.Key, fixture.Nonce!, fixture.Workspace,
                fixture.CurrentSessionId, first.RunId);
            var secondAck = Ack(RawExchange(fixture.Host!, secondProof));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, secondAck.Outcome?.Kind);
            var duplicateAck = Ack(RawExchange(fixture.Host!, secondProof));
            Assert.Equal(secondAck.FirstSeq, duplicateAck.FirstSeq);
            Assert.Equal(secondAck.LastSeq, duplicateAck.LastSeq);

            await fixture.Provider.WaitForRequestAsync(4, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await fixture.WaitForTurnCompletionAsync(first.RunId, first.TurnId);

            Assert.True(File.Exists(fixture.MarkerPath));
            Assert.True(File.Exists(fixture.SecondMarkerPath));
            Assert.Equal(4, fixture.Provider.RequestCount);
            var events = fixture.ReadEvents();
            Assert.Equal(2, events.Count(item => item.Payload is InteractionResolved resolved
                && (resolved.InteractionId == first.Request.InteractionId || resolved.InteractionId == second.InteractionId)));
            Assert.Equal(2, events.Count(item => item.Payload is PermissionGranted grant
                && grant.Lifetime == GrantLifetime.Once));
            var writeCallIds = events.Where(item => item.Payload is ToolCallRequested requested
                    && requested.ToolName == "filesystem.write")
                .Select(item => ((ToolCallRequested)item.Payload).ToolCallId).ToHashSet();
            Assert.Equal(2, writeCallIds.Count);
            Assert.Equal(2, events.Count(item => item.Payload is ToolCallSucceeded succeeded
                && writeCallIds.Contains(succeeded.ToolCallId)));
            Assert.DoesNotContain(events, item => item.Payload is ToolCallRejected);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Native_permission_ack_is_accepted_but_changed_model_fingerprint_blocks_effect()
    {
        var fixture = await NativeChoiceFixture.StartAsync("permission");
        try
        {
            var pending = await fixture.StartAndWaitForInteractionAsync(InteractionKind.Permission);
            var securityBefore = ReadNativeSessionSecurity(fixture.Host!);
            var sessionRevision = securityBefore.GetProperty("revision").GetInt64();
            Assert.Equal("FullAgent", securityBefore.GetProperty("category").GetString());

            await fixture.ChangeModelPolicyAndRestartAsync();
            var securityAfter = ReadNativeSessionSecurity(fixture.Host!);
            Assert.Equal(securityBefore.GetRawText(), securityAfter.GetRawText());
            Assert.Equal(sessionRevision, securityAfter.GetProperty("revision").GetInt64());
            Assert.Equal("FullAgent", securityAfter.GetProperty("category").GetString());
            Assert.Equal(2, fixture.Provider.RequestCount);

            var response = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                pending.PolicyRevision, "allow_once", Ids.NewV7());
            var ack = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(response, fixture.Key,
                fixture.Nonce!, fixture.Workspace, fixture.CurrentSessionId, pending.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
            Assert.Equal("Accepted", ack.Outcome?.Kind.ToString());

            var receipt = await fixture.WaitForExecutionReceiptAsync(response.MessageId);
            Assert.Equal(CommandReceiptState.Completed, receipt.State);
            Assert.NotNull(receipt.ExecutionState);
            Assert.Equal(pending.RunId, receipt.ExecutionRunId);
            Assert.Equal(2, fixture.Provider.RequestCount);
            Assert.False(File.Exists(fixture.MarkerPath));

            var events = fixture.ReadEvents();
            Assert.Contains(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                && item.Payload is InteractionResolved resolved
                && resolved.InteractionId == pending.Request.InteractionId
                && resolved.OptionId == "allow_once");
            var write = Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                && item.Payload is ToolCallRequested requested && requested.ToolName == "filesystem.write");
            var callId = ((ToolCallRequested)write.Payload).ToolCallId;
            Assert.DoesNotContain(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                && item.Envelope.ToolCallId == callId && item.Payload is ToolCallStarted);
            Assert.DoesNotContain(events, item => item.Envelope.RunId == RunId.Parse(pending.RunId)
                && item.Envelope.ToolCallId == callId && item.Payload is ToolCallSucceeded);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData("approve_execute")]
    [InlineData("approve_only")]
    [InlineData("continue_planning")]
    [InlineData("reject")]
    public async Task Native_plan_approval_choice_applies_only_its_server_defined_effect(string optionId)
    {
        var fixture = await NativeChoiceFixture.StartAsync("plan", optionId);
        try
        {
            var pending = await fixture.StartAndWaitForInteractionAsync(InteractionKind.PlanApproval);
            var available = ParseOptionIds(pending.Request.OptionsJson);
            Assert.Equal(new[] { "approve_execute", "approve_only", "continue_planning", "reject" }, available);
            var providerRequestsBeforeChoice = fixture.Provider.RequestCount;

            var forgedKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                var forged = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                    pending.PolicyRevision, optionId, Ids.NewV7());
                var forgedAck = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(forged,
                    forgedKey, fixture.Nonce!, fixture.Workspace, fixture.CurrentSessionId, pending.RunId)));
                Assert.Equal(RuntimeCommandOutcomeKind.Rejected, forgedAck.Outcome?.Kind);
                Assert.False(File.Exists(fixture.MarkerPath));
            }
            finally { CryptographicOperations.ZeroMemory(forgedKey); }

            var stale = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                pending.PolicyRevision + 1, optionId, Ids.NewV7());
            var staleAck = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(stale,
                fixture.Key, fixture.Nonce!, fixture.Workspace, fixture.CurrentSessionId, pending.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, staleAck.Outcome?.Kind);
            Assert.False(File.Exists(fixture.MarkerPath));

            var response = fixture.ChoiceCommand(pending.Request.InteractionId, pending.RunId,
                pending.PolicyRevision, optionId, Ids.NewV7());
            var proof = TrustedActionEnvelope(response, fixture.Key, fixture.Nonce!, fixture.Workspace,
                fixture.CurrentSessionId, pending.RunId);
            var ack = Ack(RawExchange(fixture.Host!, proof));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
            var duplicate = Ack(RawExchange(fixture.Host!, proof));
            Assert.Equal(ack.FirstSeq, duplicate.FirstSeq);
            Assert.Equal(ack.LastSeq, duplicate.LastSeq);

            var run = RunId.Parse(pending.RunId);
            if (optionId == "approve_only")
            {
                await fixture.WaitForRunCompletionAsync(pending.RunId);
                Assert.Equal(providerRequestsBeforeChoice, fixture.Provider.RequestCount);
            }
            else if (optionId == "reject")
            {
                Assert.Equal(providerRequestsBeforeChoice, fixture.Provider.RequestCount);
            }
            else if (optionId == "approve_execute")
            {
                await fixture.Provider.WaitForRequestAsync(providerRequestsBeforeChoice + 3, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Equal(providerRequestsBeforeChoice + 3, fixture.Provider.RequestCount);
            }
            else
            {
                await fixture.Provider.WaitForRequestAsync(providerRequestsBeforeChoice + 1, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Equal(providerRequestsBeforeChoice + 1, fixture.Provider.RequestCount);
            }

            var events = fixture.ReadEvents();
            Assert.Single(events, item => item.Envelope.RunId == run && item.Payload is InteractionResolved resolved
                && resolved.InteractionId == pending.Request.InteractionId && resolved.OptionId == optionId);
            var modeChanges = events.Where(item => item.Envelope.RunId == run && item.Payload is RunModeChanged)
                .Select(item => (RunModeChanged)item.Payload).ToArray();
            if (optionId == "approve_execute")
                Assert.Contains(modeChanges, change => change.From == RunMode.Plan && change.To == RunMode.Act
                    && change.Cause == "PlanApproved");
            else
                Assert.DoesNotContain(modeChanges, change => change.To == RunMode.Act
                    && change.Cause == "PlanApproved");
            if (optionId == "approve_only")
                Assert.Contains(events, item => item.Envelope.RunId == run
                    && item.Payload is RunCompleted completed && completed.Outcome == RunOutcome.Planned);
            Assert.Equal(optionId == "approve_execute", File.Exists(fixture.MarkerPath));
            if (optionId == "approve_execute")
            {
                Assert.Equal("plan-approved-execute", File.ReadAllText(fixture.MarkerPath));
                var writeRequest = Assert.Single(events, item => item.Envelope.RunId == run
                    && item.Payload is ToolCallRequested requested && requested.ToolName == "filesystem.write");
                Assert.NotEqual(pending.TurnId, writeRequest.Envelope.TurnId);
                var writeCall = (ToolCallRequested)writeRequest.Payload;
                Assert.Single(events, item => item.Envelope.RunId == run
                    && item.Envelope.ToolCallId == writeCall.ToolCallId
                    && item.Payload is ToolCallSucceeded succeeded && succeeded.ToolCallId == writeCall.ToolCallId);
            }
            if (optionId is "continue_planning" or "reject")
                Assert.Contains(events, item => item.Envelope.RunId == run
                    && item.Payload is RunAwaitingInput);
            CanonicalStateTracker.Replay(EventCodecs.Create(), fixture.ReadEnvelopes());
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static string[] ParseOptionIds(string optionsJson)
    {
        using var document = JsonDocument.Parse(optionsJson);
        return document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!).ToArray();
    }

    private sealed class NativeChoiceFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _dataDirectory;
        private readonly string _journal;
        private readonly string? _oldData;
        private readonly string? _oldConfig;
        private readonly string _oldDirectory;
        private Task? _stderrCapture;
        private readonly StringBuilder _stderrOutput = new();
        private bool _disposed;

        private NativeChoiceFixture(string root, string workspace, string dataDirectory, string journal,
            string? oldData, string? oldConfig, string oldDirectory, byte[] key,
            CliEndToEndTests.ScriptedHttpProvider provider, string mode, string? selectedOption)
        {
            _root = root;
            _dataDirectory = dataDirectory;
            Workspace = workspace;
            _journal = journal;
            _oldData = oldData;
            _oldConfig = oldConfig;
            _oldDirectory = oldDirectory;
            Key = key;
            Provider = provider;
            Mode = mode;
            SelectedOption = selectedOption;
            MarkerPath = Path.Combine(workspace, "permission-choice-marker.txt");
            SecondMarkerPath = Path.Combine(workspace, "permission-choice-marker-second.txt");
        }

        public string Workspace { get; }
        public byte[] Key { get; }
        public CliEndToEndTests.ScriptedHttpProvider Provider { get; }
        public string Mode { get; }
        public string? SelectedOption { get; }
        public string MarkerPath { get; }
        public string SecondMarkerPath { get; }
        public string GrantTargetPath => Path.Combine(Workspace, "grant-dispatch-target.txt");
        public string? LastStartCommandId { get; private set; }
        public string? Nonce { get; private set; }
        public IReadOnlyList<string> NegotiatedCapabilities { get; private set; } = [];
        public string? CurrentSessionId { get; private set; }
        public Process? Host { get; private set; }
        public string HostErrorDiagnostic
        {
            get
            {
                string text;
                lock (_stderrOutput) text = _stderrOutput.ToString();
                return text.Length > 2048 ? text[^2048..] : text;
            }
        }

        public void SetCurrentSessionIdForTest(string sessionId) => CurrentSessionId = sessionId;

        public static async Task<NativeChoiceFixture> StartAsync(string mode, string? selectedOption = null,
            bool requestNativeChoices = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "omni-native-choice-" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var config = Path.Combine(root, "config");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(config);
            var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
            var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
            var oldDirectory = Environment.CurrentDirectory;
            var key = RandomNumberGenerator.GetBytes(32);
            var provider = new CliEndToEndTests.ScriptedHttpProvider();
            var marker = Path.Combine(workspace, "permission-choice-marker.txt");
            var secondMarker = Path.Combine(workspace, "permission-choice-marker-second.txt");
            var toolId = "choice-tool-" + Guid.NewGuid().ToString("N");
            provider.RespondWith((index, _) =>
            {
                if (mode == "permission-grant-scope")
                {
                    var phase = index % 3;
                    var run = index / 3;
                    const string relativePath = "grant-dispatch-target.txt";
                    if (phase == 0)
                        return CliEndToEndTests.ResponsesToolCallResponse("grant-read-" + index,
                            "filesystem.read", JsonSerializer.Serialize(new { path = relativePath }));
                    if (phase == 1)
                    {
                        var state = run switch { 0 => "state-0", 1 => "state-1", 2 => "state-2", _ => "state-2" };
                        var next = run switch
                        {
                            0 => "state-1\n",
                            1 => "state-2\n",
                            2 => string.Join("\n", Enumerable.Range(0, 201).Select(line => "expanded-" + line)) + "\n",
                            _ => "state-3\n",
                        };
                        var before = GrantContent(state);
                        return CliEndToEndTests.ResponsesToolCallResponse("grant-patch-" + index,
                            "filesystem.patch", JsonSerializer.Serialize(new
                            {
                                path = relativePath,
                                expectedVersion = FilesystemPatchTool.VersionToken(Encoding.UTF8.GetBytes(before)),
                                    oldText = state + "\n",
                                newText = next,
                            }));
                    }
                    return CliEndToEndTests.ResponsesTextResponse("Grant scope dispatch completed.");
                }
                if (mode is "permission" or "permission-sequential" && index == 0)
                    return CliEndToEndTests.ResponsesToolCallResponse("discover-write-" + toolId, "tool.search",
                        "{\"query\":\"filesystem.write\",\"maxResults\":8}");
                if (mode is "permission" or "permission-sequential" && index == 1)
                    return CliEndToEndTests.ResponsesToolCallResponse(toolId, "filesystem.write",
                        JsonSerializer.Serialize(new { path = Path.GetFileName(marker), content = "permission-approved" }));
                if (mode == "permission-sequential" && index == 2)
                    return CliEndToEndTests.ResponsesToolCallResponse("second-" + toolId, "filesystem.write",
                        JsonSerializer.Serialize(new { path = Path.GetFileName(secondMarker), content = "second-permission-approved" }));
                if (mode == "plan" && index == 0)
                    return CliEndToEndTests.ResponsesToolCallResponse("discover-plan-propose-" + toolId, "tool.search",
                        "{\"query\":\"plan.propose\",\"maxResults\":8}");
                if (mode == "plan" && index == 1)
                    return CliEndToEndTests.ResponsesToolCallResponse("plan-propose-" + toolId, "plan.propose",
                        "{\"kind\":\"start\",\"itemId\":\"P1\",\"reason\":\"Review proposed work\"}");
                if (mode == "plan" && selectedOption == "approve_execute" && index == 3)
                    return CliEndToEndTests.ResponsesToolCallResponse("discover-plan-write-" + toolId, "tool.search",
                        "{\"query\":\"filesystem.write\",\"maxResults\":8}");
                if (mode == "plan" && selectedOption == "approve_execute" && index == 4)
                    return CliEndToEndTests.ResponsesToolCallResponse("plan-write-" + toolId, "filesystem.write",
                        JsonSerializer.Serialize(new { path = Path.GetFileName(marker), content = "plan-approved-execute" }));
                return CliEndToEndTests.ResponsesTextResponse("Choice continuation completed.");
            });

            var fixture = new NativeChoiceFixture(root, workspace, data,
                Path.Combine(OmniHost.WorkspaceDataDirectory(new ChoicePlatformPaths(root), workspace), "journal.db"),
                oldData, oldConfig, oldDirectory, key, provider, mode, selectedOption);
            try
            {
                File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
                    providers:
                      scripted:
                        family: OpenAIResponses
                        profile: api
                        baseUrl: {{provider.BaseUrl}}
                        auth: none
                        billingMode: Local
                    """, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "models.yaml"),
                    "models:\n  scripted-model:\n    provider: scripted\n    context: 32768\n    recommendedUsableContext: 24000\n    maxOutput: 512\n    parametersBillions: 100\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "permissions.yaml"), mode is "permission" or "permission-sequential"
                    ? "profile: balanced\nrules:\n  tool.search: allow\n  filesystem.write: ask\n"
                    : mode == "permission-grant-scope"
                        ? "profile: balanced\nrules:\n  tool.search: allow\n  filesystem.read: allow\n  filesystem.patch: ask\n"
                    : "profile: balanced\nrules:\n  tool.search: allow\n  plan.propose: allow\n  filesystem.write: allow\n", new UTF8Encoding(false));
                Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
                Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
                Environment.CurrentDirectory = workspace;

                using (var modelPolicies = OmniHost.CreateModelPolicyService(data))
                {
                    var fullAgent = ModelPolicyPresets.FullAgent();
                    var directFullAgent = new UserModelPolicy(fullAgent.Category,
                        new ModelToolPolicy(ToolMode.Direct, fullAgent.ToolPolicy.MaxVisibleTools, false,
                            fullAgent.ToolPolicy.CapabilityCeiling), fullAgent.MutationPolicy, fullAgent.Source,
                        fullAgent.Note, fullAgent.MaxModelStepsPerTurn, fullAgent.MaxToolCallsPerTurn,
                        fullAgent.AllowOrchestration, fullAgent.MaxGenerationSeconds);
                    modelPolicies.Set(ModelPolicyKey.For("scripted", "scripted-model"), 0,
                        mode == "permission-grant-scope" ? directFullAgent : fullAgent, CancellationToken.None);
                }
                var paths = OmniHost.CreatePlatformPaths();
                var defaults = new SessionSecurityDefaultConfiguration(paths);
                defaults.Set(defaults.Read().Revision, ModelPolicyCategory.FullAgent, CancellationToken.None);
                fixture.StartHost(requestNativeChoices);
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
            return fixture;
        }

        public async Task<(InteractionRequested Request, string RunId, TurnId TurnId, long PolicyRevision)>
            StartAndWaitForInteractionAsync(InteractionKind kind)
        {
            var providerRequestsBeforeStart = Provider.RequestCount;
            if (CurrentSessionId is { } priorSession)
            {
                var create = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}");
                var createAck = Ack(RawExchange(Host!, TrustedActionEnvelope(create, Key, Nonce!, Workspace,
                    priorSession, null)));
                Assert.Equal(RuntimeCommandOutcomeKind.Accepted, createAck.Outcome?.Kind);
                var createdSession = ReadCurrentSessionId(Host!);
                Assert.NotEqual(priorSession, createdSession);
                CurrentSessionId = createdSession;
            }
            var defaultRevision = ReadSessionSecurityDefaultRevision(Host!);
            var currentPolicyRevision = CurrentSessionId is null
                ? (long?)null : ReadNativeSessionSecurity(Host!).GetProperty("revision").GetInt64();
            var mode = Mode is "permission" or "permission-sequential" or "permission-grant-scope" ? "act" : "plan";
            var start = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.start\",\"text\":\"Exercise a server choice\",\"mode\":"
                + JsonSerializer.Serialize(mode) + ",\"expectedRevision\":null,\"expectedPolicyRevision\":"
                + (currentPolicyRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
                + ",\"expectedDefaultRevision\":"
                + (currentPolicyRevision is null ? JsonSerializer.Serialize(defaultRevision) : "null") + "}");
            LastStartCommandId = start.MessageId;
            var ack = Ack(RawExchange(Host!, TrustedActionEnvelope(start, Key, Nonce!, Workspace,
                CurrentSessionId, null)));
            Assert.True(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                "conversation.start rejected: " + ack.Error);
            await Provider.WaitForRequestAsync(providerRequestsBeforeStart + 1, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            var scope = ReadCurrentScope(Host!);
            CurrentSessionId = scope.SessionId;
            var run = RunId.Parse(scope.RunId);
            var store = new SqliteEventStore(_journal, readOnly: true);
            var codecs = EventCodecs.Create();
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            try
            {
                while (DateTimeOffset.UtcNow < deadline)
                {
                    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                    var events = store.ReadFrom(OmniCore.Domain.SessionId.Parse(scope.SessionId), 1);
                    var runEvents = events.Where(item => item.RunId == run)
                        .Select(envelope => (Envelope: envelope, Payload: codecs.Decode(envelope)))
                        .ToArray();
                    var found = runEvents.FirstOrDefault(item => item.Payload is InteractionRequested request && request.Kind == kind);
                    if (found.Payload is InteractionRequested interaction)
                    {
                        var sourceTurn = found.Envelope.TurnId;
                        if (sourceTurn is null && kind == InteractionKind.PlanApproval)
                            sourceTurn = runEvents.LastOrDefault(item => item.Envelope.LaneId == found.Envelope.LaneId
                                && item.Payload is ToolCallRequested requested && requested.ToolName == "plan.propose")
                                .Envelope?.TurnId;
                        if (sourceTurn is not { } turnId)
                            throw new InvalidOperationException("The native interaction has no source Turn in its Run/Lane.");
                        var deferred = await WaitForDeferredStartAsync(start);
                        Assert.Equal("deferred", deferred.GetProperty("state").GetString());
                        Assert.Equal("input_required", deferred.GetProperty("code").GetString());
                        Assert.Equal(providerRequestsBeforeStart + (kind == InteractionKind.PlanApproval ? 3
                            : Mode == "permission-grant-scope" ? 2 : 2), Provider.RequestCount);
                        var policyRevision = ReadNativeSessionSecurity(Host!).GetProperty("revision").GetInt64();
                        return (interaction, run.ToString(), turnId, policyRevision);
                    }
                    await Task.Delay(25, TestContext.Current.CancellationToken);
                }
                var safeEvents = store.ReadFrom(OmniCore.Domain.SessionId.Parse(scope.SessionId), 1)
                    .Select(envelope =>
                    {
                        var payload = codecs.Decode(envelope);
                        var code = payload switch
                        {
                            ToolCallRejected rejected => rejected.ErrorCode?.ToString(),
                            ToolCallFailed failed => failed.ErrorCode?.ToString(),
                            _ => null,
                        };
                        var toolName = payload is ToolCallRequested requested ? ":tool=" + requested.ToolName : string.Empty;
                        return $"{envelope.Sequence}:{payload.GetType().Name}{toolName}{(code is null ? string.Empty : ":code=" + code)}:run={envelope.RunId}:turn={envelope.TurnId}";
                    })
                    .TakeLast(32);
                var receipt = store.ReadCommandReceipt(start.MessageId);
                var projection = RunProjection.Replay(OmniCore.Domain.SessionId.Parse(scope.SessionId), run,
                    codecs, store.ReadFrom(OmniCore.Domain.SessionId.Parse(scope.SessionId), 1));
                var allRunEvents = store.ReadFrom(OmniCore.Domain.SessionId.Parse(scope.SessionId), 1)
                    .Where(item => item.RunId == run).ToArray();
                var runPayloads = allRunEvents.Select(item => codecs.Decode(item)).ToArray();
                var rootLaneStates = projection.RootTask is { } rootTask
                    ? string.Join(",", LaneProjection.Replay(codecs, allRunEvents).ForTask(rootTask)
                        .Select(lane => lane.State)) : "none";
                var proposalCalls = runPayloads.OfType<ToolCallRequested>()
                    .Where(call => call.ToolName == "plan.propose").Select(call => call.ToolCallId).ToHashSet();
                var acceptedProposals = runPayloads.OfType<ToolCallSucceeded>()
                    .Count(call => proposalCalls.Contains(call.ToolCallId));
                var diagnostic = $"providerRequests={Provider.RequestCount}; startReceipt={receipt?.State}/{receipt?.OutcomeKind}/{receipt?.ExecutionState}/{receipt?.ExecutionCode}; projection={projection.Mode}/{projection.State}; rootLanes={rootLaneStates}; acceptedProposals={acceptedProposals}; events=[{string.Join(";", safeEvents)}]";
                throw new TimeoutException("The native Host did not persist the expected interaction choice. " + diagnostic);
            }
            finally { store.Close(); }
        }

        public WireEnvelope ChoiceCommand(InteractionId interaction, string runId, long revision,
            string optionId, string messageId) => WireEnvelope.Command(messageId,
                "{\"cmd\":\"interaction.respond\",\"responseType\":\"choice\",\"interactionId\":"
                + JsonSerializer.Serialize(interaction.ToString()) + ",\"optionId\":" + JsonSerializer.Serialize(optionId)
                + ",\"expectedRunId\":" + JsonSerializer.Serialize(runId) + ",\"expectedPolicyRevision\":"
                + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

        public async Task RestartHostAsync(bool requestNativeChoices = true)
        {
            await StopHostAsync();
            StartHost(requestNativeChoices);
        }

        public async Task ChangeModelPolicyAndRestartAsync()
        {
            await StopHostAsync();
            var key = ModelPolicyKey.For("scripted", "scripted-model");
            using (var policies = OmniHost.CreateModelPolicyService(_dataDirectory))
            {
                var current = policies.Get(key, TestContext.Current.CancellationToken)
                    ?? throw new InvalidOperationException("The fixture model policy is missing.");
                var prior = current.Policy;
                var tightened = new UserModelPolicy(prior.Category, prior.ToolPolicy, prior.MutationPolicy,
                    prior.Source, prior.Note, prior.MaxModelStepsPerTurn, prior.MaxToolCallsPerTurn - 1,
                    prior.AllowOrchestration, prior.MaxGenerationSeconds);
                Assert.Equal(prior.Category, tightened.Category);
                Assert.Equal(prior.ToolPolicy, tightened.ToolPolicy);
                Assert.Equal(prior.MutationPolicy, tightened.MutationPolicy);
                Assert.NotEqual(prior.MaxToolCallsPerTurn, tightened.MaxToolCallsPerTurn);
                policies.Set(key, current.Revision, tightened, TestContext.Current.CancellationToken);
            }
            StartHost();
        }

        private async Task StopHostAsync()
        {
            if (Host is null) return;
            Host.Kill(entireProcessTree: true);
            await Host.WaitForExitAsync(TestContext.Current.CancellationToken);
            if (_stderrCapture is not null) await _stderrCapture;
            Host.Dispose();
            Host = null;
            _stderrCapture = null;
        }

        private void StartHost(bool requestNativeChoices = true)
        {
            // Resolve the sibling CLI published by the same build as this test assembly. This
            // also keeps --artifacts-path builds isolated from shared src/bin outputs.
            var cli = Path.Combine(AppContext.BaseDirectory, "omni.dll");
            Host = StartRawHost(cli, Workspace, Convert.ToHexString(Key));
            _stderrCapture = CaptureHostErrorAsync(Host.StandardError);
            var requested = requestNativeChoices
                ? ",\"capabilities\":[\"command.conversation.interaction.choice.respond.user\"]"
                : string.Empty;
            var hello = RawExchange(Host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"choice-restart-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"]"
                + requested + "}}"));
            using var helloJson = JsonDocument.Parse(hello.PayloadJson);
            Nonce = helloJson.RootElement.GetProperty("trustedActionNonce").GetString();
            NegotiatedCapabilities = helloJson.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(value => value.GetString()!).ToArray();
        }

        private async Task CaptureHostErrorAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrOutput) _stderrOutput.AppendLine(line);
            }
        }

        public async Task<string> StopAndCaptureHostDiagnosticsAsync()
        {
            if (Host is null) return "host-already-stopped";
            if (!Host.HasExited)
            {
                Host.Kill(entireProcessTree: true);
                await Host.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
            if (_stderrCapture is not null) await _stderrCapture;
            var diagnostics = HostErrorDiagnostic;
            Host.Dispose();
            Host = null;
            _stderrCapture = null;
            var lines = diagnostics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).Where(line => line.Length > 0).TakeLast(8)
                .Select(line => line.Length > 256 ? line[..256] : line);
            var bounded = string.Join(" | ", lines);
            return bounded.Length > 2048 ? bounded[..2048] : bounded;
        }

        public async Task<CommandReceipt> WaitForExecutionReceiptAsync(string commandId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                using var store = new SqliteEventStore(_journal, readOnly: true);
                var receipt = store.ReadCommandReceipt(commandId);
                if (receipt is { State: CommandReceiptState.Completed, ExecutionState: not null }) return receipt;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("Native choice execution receipt did not reach a terminal state.");
        }

        public IReadOnlyList<(DomainEvent Envelope, DomainEventPayload Payload)> ReadEvents()
        {
            var session = OmniCore.Domain.SessionId.Parse(CurrentSessionId!);
            var codecs = EventCodecs.Create();
            using var store = new SqliteEventStore(_journal, readOnly: true);
            return store.ReadFrom(session, 1).Select(envelope => (envelope, codecs.Decode(envelope))).ToArray();
        }

        public IReadOnlyList<(DomainEvent Envelope, DomainEventPayload Payload)> ReadSessionEvents(string sessionId)
        {
            var session = OmniCore.Domain.SessionId.Parse(sessionId);
            var codecs = EventCodecs.Create();
            using var store = new SqliteEventStore(_journal, readOnly: true);
            return store.ReadFrom(session, 1).Select(envelope => (envelope, codecs.Decode(envelope))).ToArray();
        }

        public IReadOnlyList<DomainEvent> ReadEnvelopes()
        {
            using var store = new SqliteEventStore(_journal, readOnly: true);
            return store.ReadFrom(OmniCore.Domain.SessionId.Parse(CurrentSessionId!), 1);
        }

        public CommandReceipt? ReadReceipt(string commandId)
        {
            using var store = new SqliteEventStore(_journal, readOnly: true);
            return store.ReadCommandReceipt(commandId);
        }

        public JsonElement ConversationResult(WireEnvelope command)
        {
            var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            var payload = "{\"operation\":\"conversation.result\",\"commandId\":"
                + JsonSerializer.Serialize(command.MessageId) + ",\"requestHash\":"
                + JsonSerializer.Serialize(requestHash) + "}";
            using var document = JsonDocument.Parse(RawExchange(Host!,
                WireEnvelope.Command("conversation-result-" + Guid.NewGuid().ToString("N"), payload)).PayloadJson);
            return document.RootElement.Clone();
        }

        private async Task<JsonElement> WaitForDeferredStartAsync(WireEnvelope start)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var result = ConversationResult(start);
                if (result.GetProperty("state").GetString() == "deferred") return result;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            var receipt = ReadReceipt(start.MessageId);
            throw new TimeoutException($"Start command did not reach durable deferred state before interaction test. receipt={receipt?.State}/{receipt?.OutcomeKind}/{receipt?.ExecutionState}/{receipt?.ExecutionCode}");
        }

        public async Task WaitForTurnCompletionAsync(string runId, TurnId turnId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (ReadEvents().Any(item => item.Envelope.RunId == RunId.Parse(runId)
                    && item.Envelope.TurnId == turnId && item.Payload is TurnCompleted)) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("Native interaction did not complete its original Turn.");
        }

        public async Task WaitForRunCompletionAsync(string runId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (ReadEvents().Any(item => item.Envelope.RunId == RunId.Parse(runId)
                    && item.Payload is RunCompleted)) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("approve_only did not complete the original Plan Run.");
        }

        public async Task WaitForRunCancellationAsync(string runId)
        {
            var run = RunId.Parse(runId);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var events = ReadEvents().Where(item => item.Envelope.RunId == run).ToArray();
                if (events.Any(item => item.Payload is RunCancelled)) return;
                if (events.Any(item => item.Payload is RunCompleted or RunFailed))
                    throw new InvalidOperationException("The fixture Run reached a different terminal outcome before cancel.");
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("The authorized fixture run.cancel did not persist RunCancelled.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (Host is not null)
            {
                try { if (!Host.HasExited) Host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await Host.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
                Host.Dispose();
            }
            await Provider.DisposeAsync();
            CryptographicOperations.ZeroMemory(Key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", _oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", _oldConfig);
            Environment.CurrentDirectory = _oldDirectory;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class NativeChoiceRecoveryFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _journal;
        private readonly string? _oldData;
        private readonly string? _oldConfig;
        private readonly string _oldDirectory;
        private readonly TaskCompletionSource _continuationGateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverReleaseContinuation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _mode;
        private OmniCliRuntime? _runtime;
        private OmniServer? _server;
        private RecoveryDuplex? _duplex;
        private Task<int>? _hostTask;
        private bool _disposed;

        private NativeChoiceRecoveryFixture(string root, string workspace, string data, string journal,
            string? oldData, string? oldConfig, string oldDirectory, byte[] key,
            CliEndToEndTests.ScriptedHttpProvider provider, string mode)
        {
            _root = root;
            Workspace = workspace;
            _journal = journal;
            _oldData = oldData;
            _oldConfig = oldConfig;
            _oldDirectory = oldDirectory;
            Key = key;
            Provider = provider;
            _mode = mode;
            MarkerPath = Path.Combine(workspace, mode + "-recovery-marker.txt");
        }

        public string Workspace { get; }
        public byte[] Key { get; }
        public CliEndToEndTests.ScriptedHttpProvider Provider { get; }
        public string MarkerPath { get; }
        public string DefaultRevision { get; private set; } = "";
        public string? Nonce { get; private set; }
        public string? CurrentSessionId { get; private set; }
        public TaskCompletionSource ContinuationGateEntered => _continuationGateEntered;

        public static async Task<NativeChoiceRecoveryFixture> StartAsync(string mode = "permission")
        {
            var root = Path.Combine(Path.GetTempPath(), "omni-native-choice-recovery-" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var config = Path.Combine(root, "config");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(config);
            var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
            var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
            var oldDirectory = Environment.CurrentDirectory;
            var key = RandomNumberGenerator.GetBytes(32);
            var provider = new CliEndToEndTests.ScriptedHttpProvider();
            var marker = Path.Combine(workspace, mode + "-recovery-marker.txt");
            var toolId = "choice-recovery-tool-" + Guid.NewGuid().ToString("N");
            provider.RespondWith((index, _) => mode switch
            {
                "permission" => index == 0
                    ? CliEndToEndTests.ResponsesToolCallResponse("discover-recovery-write-" + toolId, "tool.search",
                        "{\"query\":\"filesystem.write\",\"maxResults\":8}")
                    : index == 1
                        ? CliEndToEndTests.ResponsesToolCallResponse(toolId, "filesystem.write",
                            JsonSerializer.Serialize(new { path = Path.GetFileName(marker), content = "permission-recovered" }))
                        : CliEndToEndTests.ResponsesTextResponse("Recovered permission continuation completed."),
                "plan" => index == 0
                    ? CliEndToEndTests.ResponsesToolCallResponse("discover-recovery-plan-" + toolId, "tool.search",
                        "{\"query\":\"plan.propose\",\"maxResults\":8}")
                    : index == 1
                        ? CliEndToEndTests.ResponsesToolCallResponse(toolId, "plan.propose",
                            "{\"kind\":\"start\",\"itemId\":\"P1\"}")
                        : CliEndToEndTests.ResponsesTextResponse("Recovered plan continuation completed."),
                _ => throw new InvalidOperationException("Unknown native choice recovery fixture mode."),
            });

            var fixture = new NativeChoiceRecoveryFixture(root, workspace, data,
                Path.Combine(OmniHost.WorkspaceDataDirectory(new ChoicePlatformPaths(root), workspace), "journal.db"),
                oldData, oldConfig, oldDirectory, key, provider, mode);
            try
            {
                File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
                    providers:
                      scripted:
                        family: OpenAIResponses
                        profile: api
                        baseUrl: {{provider.BaseUrl}}
                        auth: none
                        billingMode: Local
                    """, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "models.yaml"),
                    "models:\n  scripted-model:\n    provider: scripted\n    context: 32768\n    recommendedUsableContext: 24000\n    maxOutput: 512\n    parametersBillions: 100\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "permissions.yaml"),
                    "profile: balanced\nrules:\n  tool.search: allow\n  filesystem.write: ask\n", new UTF8Encoding(false));
                Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
                Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
                Environment.CurrentDirectory = workspace;
                using (var modelPolicies = OmniHost.CreateModelPolicyService(data))
                    modelPolicies.Set(ModelPolicyKey.For("scripted", "scripted-model"), 0,
                        ModelPolicyPresets.FullAgent(), CancellationToken.None);
                var defaults = new SessionSecurityDefaultConfiguration(OmniHost.CreatePlatformPaths());
                defaults.Set(defaults.Read().Revision, ModelPolicyCategory.FullAgent, CancellationToken.None);
                fixture.StartRuntimeAndHost(gateChoiceContinuation: true);
                fixture.Negotiate();
                using var defaultSnapshot = fixture.QueryJson("sessionSecurityDefault");
                fixture.DefaultRevision = defaultSnapshot.RootElement.GetProperty("revision").GetString()
                    ?? throw new InvalidOperationException("Session security default revision is missing.");
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
            return fixture;
        }

        public WireEnvelope Command(string name, string properties) => WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":" + JsonSerializer.Serialize(name) + "," + properties + "}");

        public WireEnvelope ChoiceCommand(string interactionId, string runId, long policyRevision, string optionId) =>
            Command("interaction.respond", "\"responseType\":\"choice\",\"interactionId\":"
                + JsonSerializer.Serialize(interactionId) + ",\"optionId\":" + JsonSerializer.Serialize(optionId)
                + ",\"expectedRunId\":" + JsonSerializer.Serialize(runId) + ",\"expectedPolicyRevision\":"
                + policyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public WireEnvelope ContinueCommand(string interactionId, string runId, long policyRevision) =>
            Command("interaction.continue", "\"interactionId\":" + JsonSerializer.Serialize(interactionId)
                + ",\"expectedRunId\":" + JsonSerializer.Serialize(runId) + ",\"expectedPolicyRevision\":"
                + policyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public CommandAck SendTrusted(WireEnvelope command, string? sessionId, string? runId) =>
            Ack(Exchange(TrustedActionEnvelope(command, Key, Nonce!, Workspace, sessionId, runId)));

        public WireEnvelope Exchange(WireEnvelope request)
        {
            var duplex = _duplex ?? throw new InvalidOperationException("The native Host is not connected.");
            WireFrame.Write(duplex.ClientInput, request.ToJson());
            var response = WireFrame.Read(duplex.ClientOutput)
                ?? throw new EndOfStreamException("The in-process native Host closed before its response.");
            var envelope = WireEnvelope.FromJson(response);
            Assert.Equal(request.MessageId, envelope.MessageId);
            return envelope;
        }

        public JsonDocument QueryJson(string name)
        {
            var response = Exchange(WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"query\",\"name\":"
                + JsonSerializer.Serialize(name) + "}"));
            using var payload = JsonDocument.Parse(response.PayloadJson);
            return JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
        }

        public (string SessionId, string RunId) ReadScope()
        {
            using var scope = QueryJson("humanActionScope");
            var session = scope.RootElement.GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException("Native scope has no Session.");
            var run = scope.RootElement.GetProperty("runId").GetString()
                ?? throw new InvalidOperationException("Native scope has no Run.");
            CurrentSessionId = session;
            return (session, run);
        }

        public long ReadSessionPolicyRevision()
        {
            using var policy = QueryJson("sessionSecurity");
            return policy.RootElement.GetProperty("revision").GetInt64();
        }

        public async Task<InteractionRequested> WaitForPermissionAsync(string sessionId, string runId)
        {
            CurrentSessionId = sessionId;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var item = ReadEvents().SingleOrDefault(value => value.Envelope.RunId == RunId.Parse(runId)
                    && value.Payload is InteractionRequested request && request.Kind == InteractionKind.Permission);
                if (item.Payload is InteractionRequested request) return request;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("The real Host did not persist the expected Permission interaction.");
        }

        public async Task<(InteractionRequested Request, TurnId SourceTurn)> StartAndWaitForPlanApprovalAsync()
        {
            if (_mode != "plan") throw new InvalidOperationException("This fixture is not a Plan scenario.");
            var start = Command("conversation.start", "\"text\":\"Propose a recovery plan\",\"mode\":\"plan\","
                + "\"expectedRevision\":null,\"expectedPolicyRevision\":null,\"expectedDefaultRevision\":"
                + JsonSerializer.Serialize(DefaultRevision));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, SendTrusted(start, null, null).Outcome?.Kind);
            await Provider.WaitForRequestAsync(3, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            var (sessionId, runId) = ReadScope();
            var run = RunId.Parse(runId);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var events = ReadEvents().Where(item => item.Envelope.RunId == run).ToArray();
                var pending = events.SingleOrDefault(item => item.Payload is InteractionRequested request
                    && request.Kind == InteractionKind.PlanApproval);
                if (pending.Payload is InteractionRequested request)
                {
                    var source = events.LastOrDefault(item => item.Envelope.LaneId == pending.Envelope.LaneId
                        && item.Payload is ToolCallRequested tool && tool.ToolName == "plan.propose");
                    if (source.Envelope?.TurnId is not { } turn)
                        throw new InvalidOperationException("The real plan proposal has no source Turn in its Lane.");
                    return (request, turn);
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            var safeEvents = ReadEvents().Select(item =>
            {
                var detail = item.Payload switch
                {
                    ToolCallRequested requested => ":tool=" + requested.ToolName,
                    ToolCallFailed failed => ":code=" + failed.ErrorCode,
                    ToolCallRejected rejected => ":code=" + rejected.ErrorCode,
                    PlanMutationRejected rejected => ":kind=" + rejected.Kind,
                    _ => string.Empty,
                };
                return $"{item.Envelope.Sequence}:{item.Payload.GetType().Name}{detail}";
            }).TakeLast(24);
            throw new TimeoutException("The real PlanApproval interaction was not persisted. events=["
                + string.Join(";", safeEvents) + "]");
        }

        public CommandAck RespondLegacy(InteractionRequested request, string optionId)
        {
            var server = _server ?? throw new InvalidOperationException("The real OmniServer is not connected.");
            return server.RespondToInteraction(request.InteractionId, optionId);
        }

        public IReadOnlyList<(DomainEvent Envelope, DomainEventPayload Payload)> ReadEvents()
        {
            if (CurrentSessionId is null) return [];
            var session = SessionId.Parse(CurrentSessionId);
            var codecs = EventCodecs.Create();
            using var store = new SqliteEventStore(_journal, readOnly: true);
            return store.ReadFrom(session, 1).Select(envelope => (envelope, codecs.Decode(envelope))).ToArray();
        }

        public CommandReceipt? ReadReceipt(string commandId)
        {
            using var store = new SqliteEventStore(_journal, readOnly: true);
            return store.ReadCommandReceipt(commandId);
        }

        public async Task WaitForExecutionAsync(string commandId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (ReadReceipt(commandId) is { State: CommandReceiptState.Completed, ExecutionState: not null }) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("Recovered choice execution did not reach a durable terminal outcome.");
        }

        public void StartRecoveredRuntimeAndHost()
        {
            StartRuntimeAndHost(gateChoiceContinuation: false);
            Negotiate();
        }

        private void Negotiate()
        {
            var hello = Exchange(WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":"
                + JsonSerializer.Serialize("choice-recovery-hello-" + Guid.NewGuid().ToString("N"))
                + ",\"payload\":{\"supportedVersions\":[\"1.0\"],\"capabilities\":[\"command.conversation.interaction.choice.respond.user\"]}}"));
            using var helloJson = JsonDocument.Parse(hello.PayloadJson);
            Nonce = helloJson.RootElement.GetProperty("trustedActionNonce").GetString();
        }

        public async Task StopAndDisposeFirstRuntimeAsync()
        {
            if (_duplex is not null) _duplex.CompleteClientInput();
            if (_hostTask is not null)
                _ = await _hostTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _hostTask = null;
            _duplex?.Dispose();
            _duplex = null;
            _runtime?.Dispose();
            _runtime = null;
        }

        private void StartRuntimeAndHost(bool gateChoiceContinuation)
        {
            var runtime = OmniCliRuntime.Create(Workspace);
            runtime.UseConsoleInput = false;
            runtime.QuestionnaireInput = null;
            runtime.PrepareConversationSessionPolicy();
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            _server = Assert.IsType<OmniServer>(client);
            var duplex = new RecoveryDuplex();
            _runtime = runtime;
            _duplex = duplex;
            _hostTask = Task.Run(() => StdioProtocolHost.RunWithTrustedActions(client,
                duplex.HostInput, duplex.HostOutput, TextWriter.Null, Key,
                runPersistedConversation: async (admitted, token) =>
                {
                    if (gateChoiceContinuation && admitted.ContinuationResponseType == "choice"
                        && !admitted.ExplicitChoiceRecovery)
                    {
                        _continuationGateEntered.TrySetResult();
                        await _neverReleaseContinuation.Task.WaitAsync(token);
                    }
                    return await runtime.ExecutePersistedConversationAsync(admitted, _ => { }, token);
                }));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await StopAndDisposeFirstRuntimeAsync(); } catch (Exception) { }
            await Provider.DisposeAsync();
            CryptographicOperations.ZeroMemory(Key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", _oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", _oldConfig);
            Environment.CurrentDirectory = _oldDirectory;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class RecoveryDuplex : IDisposable
    {
        private readonly BlockingByteStream _clientToHost = new();
        private readonly BlockingByteStream _hostToClient = new();
        public Stream HostInput => _clientToHost;
        public Stream HostOutput => _hostToClient;
        public Stream ClientInput => _clientToHost;
        public Stream ClientOutput => _hostToClient;
        public void CompleteClientInput() => _clientToHost.CompleteWriter();
        public void Dispose() { _clientToHost.Dispose(); _hostToClient.Dispose(); }
    }

    private sealed class BlockingByteStream : Stream
    {
        private readonly BlockingCollection<byte> _bytes = new();
        private readonly object _writeGate = new();
        private bool _writerCompleted;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => !_writerCompleted;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (count == 0) return 0;
            var first = ReadByte();
            if (first < 0) return 0;
            buffer[offset] = (byte)first;
            var read = 1;
            while (read < count && _bytes.TryTake(out var next)) buffer[offset + read++] = next;
            return read;
        }
        public override int ReadByte()
        {
            try { return _bytes.Take(); }
            catch (InvalidOperationException) { return -1; }
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            lock (_writeGate)
            {
                ObjectDisposedException.ThrowIf(_writerCompleted, this);
                for (var index = offset; index < offset + count; index++) _bytes.Add(buffer[index]);
            }
        }
        public override void WriteByte(byte value)
        {
            lock (_writeGate)
            {
                ObjectDisposedException.ThrowIf(_writerCompleted, this);
                _bytes.Add(value);
            }
        }
        public void CompleteWriter()
        {
            lock (_writeGate)
            {
                if (_writerCompleted) return;
                _writerCompleted = true;
                _bytes.CompleteAdding();
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) CompleteWriter();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Accepted_permission_choice_recovers_from_new_runtime_and_store_after_orderly_cancellation()
    {
        await using var fixture = await NativeChoiceRecoveryFixture.StartAsync();
        var start = fixture.Command("conversation.start", "\"text\":\"Write one approved marker\",\"mode\":\"act\","
            + "\"expectedRevision\":null,\"expectedPolicyRevision\":null,\"expectedDefaultRevision\":"
            + JsonSerializer.Serialize(fixture.DefaultRevision));
        var startAck = fixture.SendTrusted(start, null, null);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, startAck.Outcome?.Kind);

        await fixture.Provider.WaitForRequestAsync(2, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var scope = fixture.ReadScope();
        var interaction = await fixture.WaitForPermissionAsync(scope.SessionId, scope.RunId);
        var permission = Assert.IsType<PermissionRequested>(fixture.ReadEvents()
            .Select(item => item.Payload).Single(payload => payload is PermissionRequested));
        Assert.False(File.Exists(fixture.MarkerPath));

        var response = fixture.ChoiceCommand(interaction.InteractionId.ToString(), scope.RunId,
            fixture.ReadSessionPolicyRevision(), "allow_once");
        var responseProof = TrustedActionEnvelope(response, fixture.Key, fixture.Nonce!, fixture.Workspace,
            scope.SessionId, scope.RunId);
        var responseAck = Ack(fixture.Exchange(responseProof));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, responseAck.Outcome?.Kind);
        await fixture.ContinuationGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(fixture.MarkerPath));
        Assert.Equal(2, fixture.Provider.RequestCount);
        Assert.DoesNotContain(fixture.ReadEvents(), item => item.Payload is ToolCallStarted started
            && started.ToolCallId == permission.ToolCallId);

        var duplicateAck = Ack(fixture.Exchange(responseProof));
        Assert.Equal(responseAck.FirstSeq, duplicateAck.FirstSeq);
        Assert.Equal(responseAck.LastSeq, duplicateAck.LastSeq);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, duplicateAck.Outcome?.Kind);

        await fixture.StopAndDisposeFirstRuntimeAsync();
        var cancelledReceipt = fixture.ReadReceipt(response.MessageId);
        Assert.Equal("cancelled", cancelledReceipt?.ExecutionState);
        Assert.False(File.Exists(fixture.MarkerPath));
        Assert.DoesNotContain(fixture.ReadEvents(), item => item.Payload is ToolCallStarted started
            && started.ToolCallId == permission.ToolCallId);

        fixture.StartRecoveredRuntimeAndHost();
        var recovery = fixture.QueryJson("interaction.recovery");
        Assert.Equal("ready", recovery.RootElement.GetProperty("state").GetString());
        var candidate = recovery.RootElement.GetProperty("choices").EnumerateArray().Single();
        Assert.Equal(interaction.InteractionId.ToString(), candidate.GetProperty("interactionId").GetString());
        Assert.Equal(scope.RunId, candidate.GetProperty("runId").GetString());

        var continueCommand = fixture.ContinueCommand(interaction.InteractionId.ToString(), scope.RunId,
            candidate.GetProperty("policyRevision").GetInt64());
        var continueAck = fixture.SendTrusted(continueCommand, scope.SessionId, scope.RunId);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, continueAck.Outcome?.Kind);
        await fixture.Provider.WaitForRequestAsync(3, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await fixture.WaitForExecutionAsync(continueCommand.MessageId);

        Assert.Equal("permission-recovered", File.ReadAllText(fixture.MarkerPath));
        Assert.Equal(3, fixture.Provider.RequestCount);
        var events = fixture.ReadEvents();
        Assert.Single(events, item => item.Payload is InteractionResolved resolved
            && resolved.InteractionId == interaction.InteractionId);
        Assert.Single(events, item => item.Payload is ToolCallStarted started
            && started.ToolCallId == permission.ToolCallId);
        Assert.Single(events, item => item.Payload is ToolCallSucceeded succeeded
            && succeeded.ToolCallId == permission.ToolCallId);
        Assert.Contains(events, item => item.Payload is PostEditValidationPending pending
            && pending.RunId == RunId.Parse(scope.RunId) && pending.ToolCallId == permission.ToolCallId);
        Assert.DoesNotContain(events, item => item.Payload is PostEditValidationConsumed consumed
            && consumed.RunId == RunId.Parse(scope.RunId) && consumed.EditIds.Contains(permission.ToolCallId));
        Assert.DoesNotContain(events, item => item.Envelope.RunId == RunId.Parse(scope.RunId)
            && item.Payload is RunCompleted);
    }

    [Fact]
    public async Task Accepted_plan_approval_choice_recovers_from_new_runtime_and_store()
    {
        await using var fixture = await NativeChoiceRecoveryFixture.StartAsync("plan");
        var (interaction, sourceTurn) = await fixture.StartAndWaitForPlanApprovalAsync();
        var (sessionId, runId) = fixture.ReadScope();
        var policyRevision = fixture.ReadSessionPolicyRevision();
        var response = fixture.ChoiceCommand(interaction.InteractionId.ToString(), runId,
            policyRevision, "continue_planning");
        var proof = TrustedActionEnvelope(response, fixture.Key, fixture.Nonce!, fixture.Workspace,
            sessionId, runId);
        var ack = Ack(fixture.Exchange(proof));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        await fixture.ContinuationGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(3, fixture.Provider.RequestCount);

        var duplicate = Ack(fixture.Exchange(proof));
        Assert.Equal(ack.FirstSeq, duplicate.FirstSeq);
        Assert.Equal(ack.LastSeq, duplicate.LastSeq);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, duplicate.Outcome?.Kind);

        await fixture.StopAndDisposeFirstRuntimeAsync();
        Assert.Equal("cancelled", fixture.ReadReceipt(response.MessageId)?.ExecutionState);
        fixture.StartRecoveredRuntimeAndHost();
        var recovery = fixture.QueryJson("interaction.recovery");
        Assert.Equal("ready", recovery.RootElement.GetProperty("state").GetString());
        var candidate = Assert.Single(recovery.RootElement.GetProperty("choices").EnumerateArray());
        Assert.Equal(interaction.InteractionId.ToString(), candidate.GetProperty("interactionId").GetString());
        Assert.Equal(runId, candidate.GetProperty("runId").GetString());

        var continueCommand = fixture.ContinueCommand(interaction.InteractionId.ToString(), runId,
            candidate.GetProperty("policyRevision").GetInt64());
        var continueAck = fixture.SendTrusted(continueCommand, sessionId, runId);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, continueAck.Outcome?.Kind);
        await fixture.Provider.WaitForRequestAsync(4, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await fixture.WaitForExecutionAsync(continueCommand.MessageId);

        Assert.Equal(4, fixture.Provider.RequestCount);
        var events = fixture.ReadEvents();
        Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(runId)
            && item.Payload is InteractionResolved resolved
            && resolved.InteractionId == interaction.InteractionId
            && resolved.OptionId == "continue_planning");
        Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(runId)
            && item.Payload is ToolCallRequested requested && requested.ToolName == "plan.propose");
        Assert.Single(events, item => item.Envelope.RunId == RunId.Parse(runId)
            && item.Payload is ToolCallSucceeded succeeded
            && events.Any(candidateEvent => candidateEvent.Envelope.ToolCallId == succeeded.ToolCallId
                && candidateEvent.Payload is ToolCallRequested requested && requested.ToolName == "plan.propose"));
        Assert.Contains(events, item => item.Envelope.RunId == RunId.Parse(runId)
            && item.Envelope.TurnId != sourceTurn && item.Payload is TurnStarted);
    }

    [Fact]
    public async Task Legacy_plan_approval_input_is_not_offered_as_native_choice_recovery()
    {
        await using var fixture = await NativeChoiceRecoveryFixture.StartAsync("plan");
        var (interaction, _) = await fixture.StartAndWaitForPlanApprovalAsync();

        var legacyAck = fixture.RespondLegacy(interaction, "reject");
        Assert.Equal("ok", legacyAck.Status);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var events = fixture.ReadEvents();
            var resolved = events.Any(item => item.Payload is InteractionResolved value
                && value.InteractionId == interaction.InteractionId && value.OptionId == "reject");
            var legacyInput = events.Any(item => item.Payload is UserInputReceived input
                && input.Origin == "InteractionResponse(PlanApproval)"
                && input.InputPartsJson == "[\"PlanApproval: reject\"]");
            if (resolved && legacyInput) break;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        var recorded = fixture.ReadEvents();
        Assert.Single(recorded, item => item.Payload is InteractionResolved resolved
            && resolved.InteractionId == interaction.InteractionId && resolved.OptionId == "reject");
        Assert.Single(recorded, item => item.Payload is UserInputReceived input
            && input.Origin == "InteractionResponse(PlanApproval)"
            && input.InputPartsJson == "[\"PlanApproval: reject\"]");

        await fixture.StopAndDisposeFirstRuntimeAsync();
        fixture.StartRecoveredRuntimeAndHost();
        var recovery = fixture.QueryJson("interaction.recovery");
        Assert.Equal("none", recovery.RootElement.GetProperty("state").GetString());
        Assert.Empty(recovery.RootElement.GetProperty("choices").EnumerateArray());
    }

    private sealed class ChoicePlatformPaths(string root) : OmniCore.Abstractions.IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }

    private sealed class ChangingExecutableResolver(params string[] resolvedPaths) : IExecutableResolver
    {
        private readonly Queue<string> _resolvedPaths = new(resolvedPaths);
        public int ResolveCount { get; private set; }

        public ExecutableResolution Resolve(string executable, string workspaceRoot)
        {
            ResolveCount++;
            if (!_resolvedPaths.TryDequeue(out var resolved))
                throw new InvalidOperationException("Test resolver has no further result.");
            return new ExecutableResolution(executable, resolved);
        }
    }

    private sealed class CountingSandboxLauncher : ISandboxProcessLauncher
    {
        public int Starts { get; private set; }

        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
            CancellationToken cancellationToken)
        {
            Starts++;
            throw new InvalidOperationException("The resumed claim must be rejected before launch.");
        }
    }
}
