using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Native_permission_grants_respect_run_scope_share_workspace_scope_and_session_ceiling_in_dispatch()
    {
        var fixture = await NativeChoiceFixture.StartAsync("permission-grant-scope");
        var initialContent = GrantContent("state-0");
        try
        {
            File.WriteAllText(fixture.GrantTargetPath, initialContent, new UTF8Encoding(false));

            // Session A: a genuine Ask on filesystem.patch is approved for this Run only.
            var a1 = await fixture.StartAndWaitForInteractionAsync(InteractionKind.Permission);
            var sessionA = fixture.CurrentSessionId!;
            var runA1 = RunId.Parse(a1.RunId);
            var a1Events = fixture.ReadSessionEvents(sessionA);
            var patchA1 = PatchRequest(a1Events, runA1);
            var claimsKeyA1 = PatchClaimsKey(patchA1, fixture.Workspace);
            var readRequest = Assert.Single(a1Events, item => item.Envelope.RunId == runA1
                && item.Payload is ToolCallRequested requested && requested.ToolName == "filesystem.read");
            var readCall = (ToolCallRequested)readRequest.Payload;
            var readCoverage = Assert.Single(a1Events, item => item.Envelope.RunId == runA1
                && item.Payload is WorkspaceFileReadCoverageRecorded coverage && coverage.ToolCallId == readCall.ToolCallId);
            var readSucceeded = Assert.Single(a1Events, item => item.Envelope.RunId == runA1
                && item.Payload is ToolCallSucceeded succeeded && succeeded.ToolCallId == readCall.ToolCallId);
            Assert.Equal(readCall.ToolCallId, readRequest.Envelope.ToolCallId);
            Assert.Equal(readCall.ToolCallId, readCoverage.Envelope.ToolCallId);
            Assert.Equal(readCall.ToolCallId, readSucceeded.Envelope.ToolCallId);
            using (var patchArguments = JsonDocument.Parse(patchA1.ArgumentsJson))
            using (var readArguments = JsonDocument.Parse(readCall.ArgumentsJson))
            {
                Assert.Equal("grant-dispatch-target.txt", readArguments.RootElement.GetProperty("path").GetString());
                Assert.Equal("grant-dispatch-target.txt", patchArguments.RootElement.GetProperty("path").GetString());
                Assert.Equal(patchArguments.RootElement.GetProperty("expectedVersion").GetString(),
                    ((WorkspaceFileReadCoverageRecorded)readCoverage.Payload).VersionToken);
            }
            Assert.Equal("grant-dispatch-target.txt", ((WorkspaceFileReadCoverageRecorded)readCoverage.Payload).RelativePath);
            Assert.Equal(readRequest.Envelope.TurnId,
                Assert.Single(a1Events, item => item.Envelope.RunId == runA1
                    && item.Payload is ToolCallRequested requested && requested.ToolCallId == patchA1.ToolCallId)
                    .Envelope.TurnId);
            Assert.Equal(readRequest.Envelope.LaneId,
                Assert.Single(a1Events, item => item.Envelope.RunId == runA1
                    && item.Payload is ToolCallRequested requested && requested.ToolCallId == patchA1.ToolCallId)
                    .Envelope.LaneId);
            var a1Evaluation = Assert.Single(a1Events, item => item.Envelope.RunId == runA1
                && item.Payload is PermissionEvaluated evaluated && evaluated.ToolCallId == patchA1.ToolCallId);
            Assert.Equal(PermissionDecision.Ask, ((PermissionEvaluated)a1Evaluation.Payload).Decision);
            Assert.Null(((PermissionEvaluated)a1Evaluation.Payload).AppliedGrant);

            var a1Response = fixture.ChoiceCommand(a1.Request.InteractionId, a1.RunId,
                a1.PolicyRevision, "allow_run", Ids.NewV7());
            var a1Ack = await fixture.ExchangeWhenAdmissibleAsync(a1Response, TrustedActionEnvelope(a1Response, fixture.Key,
                fixture.Nonce!, fixture.Workspace, sessionA, a1.RunId));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, a1Ack.Outcome?.Kind);
            await fixture.Provider.WaitForRequestAsync(3, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await fixture.WaitForTurnCompletionAsync(a1.RunId, a1.TurnId);
            var cancelA1 = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"run.cancel\",\"runId\":"
                + JsonSerializer.Serialize(a1.RunId) + "}");
            var cancelAck = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(cancelA1, fixture.Key,
                fixture.Nonce!, fixture.Workspace, sessionA, a1.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, cancelAck.Outcome?.Kind);
            await fixture.WaitForRunCancellationAsync(a1.RunId);
            Assert.Equal(CommandReceiptState.Completed, fixture.ReadReceipt(cancelA1.MessageId)?.State);
            var a1CompletedEvents = fixture.ReadSessionEvents(sessionA);
            var a1Trace = string.Join(";", a1CompletedEvents.Where(item => item.Envelope.RunId == runA1)
                .Select(item => item.Payload switch
                {
                    ToolCallRequested requested => "requested:" + requested.ToolName,
                    PermissionEvaluated evaluated => "permission:" + evaluated.Decision + ":grant=" + evaluated.AppliedGrant,
                    PermissionGranted granted => "granted:" + granted.Lifetime,
                    ToolCallRejected rejected => "rejected:" + rejected.ErrorCode + ":" + rejected.Reason,
                    ToolCallFailed failed => "failed:" + failed.ErrorCode + ":" + failed.Cause,
                    ToolCallSucceeded succeeded => "succeeded:" + succeeded.ToolCallId,
                    _ => item.Payload.GetType().Name,
                }))
                + ";readSuccessScope=" + readSucceeded.Envelope.LaneId + "/" + readSucceeded.Envelope.TurnId
                + ";readCoverageScope=" + readCoverage.Envelope.LaneId + "/" + readCoverage.Envelope.TurnId
                + ";readToolCallId=" + readCall.ToolCallId;
            Assert.Contains(a1CompletedEvents, item => item.Envelope.RunId == runA1
                && item.Envelope.ToolCallId == patchA1.ToolCallId && item.Payload is ToolCallSucceeded);
            Assert.Contains(a1CompletedEvents, item => item.Envelope.RunId == runA1
                && item.Payload is RunCancelled);
            Assert.Equal(GrantContent("state-1"), File.ReadAllText(fixture.GrantTargetPath));

            var workspaceId = WorkspaceId.Parse(a1Events.Select(item => item.Payload)
                .OfType<SessionCreated>().Single().WorkspaceId);
            var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), fixture.Workspace);
            var grants = new FilePermissionGrantStore(workspaceData, new InMemoryAuditSink());
            var a1Grant = Assert.Single(grants.List(workspaceId, runA1));
            Assert.Equal(GrantLifetime.Run, a1Grant.Lifetime);
            Assert.Equal(runA1, a1Grant.Run);
            Assert.Equal("filesystem.patch", a1Grant.ToolId);
            Assert.Equal(claimsKeyA1, a1Grant.ClaimsKey);
            var a1GrantedEvent = Assert.Single(fixture.ReadSessionEvents(sessionA), item => item.Envelope.RunId == runA1
                && item.Payload is PermissionGranted granted && granted.ToolCallId == patchA1.ToolCallId);
            Assert.Equal(GrantLifetime.Run, ((PermissionGranted)a1GrantedEvent.Payload).Lifetime);

            // The default change only affects a newly created Session. A keeps its FullAgent snapshot.
            SetSessionSecurityDefault(fixture, ModelPolicyCategory.PatchOnly);
            var b1 = await fixture.StartAndWaitForInteractionAsync(InteractionKind.Permission);
            var sessionB = fixture.CurrentSessionId!;
            Assert.NotEqual(sessionA, sessionB);
            Assert.Equal(workspaceId, WorkspaceId.Parse(fixture.ReadSessionEvents(sessionB)
                .Select(item => item.Payload).OfType<SessionCreated>().Single().WorkspaceId));
            Assert.Equal("PatchOnly", ReadNativeSessionSecurity(fixture.Host!)
                .GetProperty("category").GetString());
            var runB1 = RunId.Parse(b1.RunId);
            var bEvents = fixture.ReadSessionEvents(sessionB);
            Assert.DoesNotContain(bEvents, item => item.Envelope.RunId == RunId.Parse(b1.RunId)
                && item.Payload is ToolCallRequested requested && requested.ToolName == "tool.search");
            var patchB1 = PatchRequest(bEvents, runB1);
            var claimsKeyB1 = PatchClaimsKey(patchB1, fixture.Workspace);
            Assert.Equal(claimsKeyA1, claimsKeyB1);
            var b1Evaluation = Assert.Single(bEvents, item => item.Envelope.RunId == runB1
                && item.Payload is PermissionEvaluated evaluated && evaluated.ToolCallId == patchB1.ToolCallId);
            Assert.Equal(PermissionDecision.Ask, ((PermissionEvaluated)b1Evaluation.Payload).Decision);
            Assert.Null(((PermissionEvaluated)b1Evaluation.Payload).AppliedGrant);
            Assert.DoesNotContain(bEvents, item => item.Envelope.RunId == runB1
                && item.Payload is ToolCallStarted started && started.ToolCallId == patchB1.ToolCallId);

            // A Workspace approval in B is durable for the same workspace and exact claims.
            var b1Response = fixture.ChoiceCommand(b1.Request.InteractionId, b1.RunId,
                b1.PolicyRevision, "allow_workspace", Ids.NewV7());
            var b1Ack = await fixture.ExchangeWhenAdmissibleAsync(b1Response, TrustedActionEnvelope(b1Response, fixture.Key,
                fixture.Nonce!, fixture.Workspace, sessionB, b1.RunId));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, b1Ack.Outcome?.Kind);
            try
            {
                await fixture.Provider.WaitForRequestAsync(6, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                var diagnostic = string.Join(";", fixture.ReadSessionEvents(sessionB)
                    .Where(item => item.Envelope.RunId == runB1)
                    .Select(item => item.Payload switch
                    {
                        ToolCallFailed failed => "failed:" + failed.ErrorCode + ":" + failed.Cause,
                        ToolCallSucceeded succeeded => "succeeded:" + succeeded.ToolCallId,
                        PermissionEvaluated evaluated => "permission:" + evaluated.Decision + ":" + evaluated.AppliedGrant,
                        _ => item.Payload.GetType().Name,
                    }));
                throw new TimeoutException("B1 provider request timeout count=" + fixture.Provider.RequestCount
                    + " hostExited=" + fixture.Host!.HasExited
                    + " choiceReceipt=" + fixture.ReadReceipt(b1Response.MessageId)?.State + "/"
                    + fixture.ReadReceipt(b1Response.MessageId)?.OutcomeKind + "/"
                    + fixture.ReadReceipt(b1Response.MessageId)?.ExecutionState + "/"
                    + fixture.ReadReceipt(b1Response.MessageId)?.ExecutionCode
                    + " hostStderr=" + fixture.HostErrorDiagnostic
                    + " trace=" + diagnostic);
            }
            await fixture.WaitForTurnCompletionAsync(b1.RunId, b1.TurnId);
            Assert.Contains(fixture.ReadSessionEvents(sessionB), item => item.Envelope.RunId == runB1
                && item.Envelope.ToolCallId == patchB1.ToolCallId && item.Payload is ToolCallSucceeded);
            Assert.Equal(GrantContent("state-2"), File.ReadAllText(fixture.GrantTargetPath));

            var b1GrantedEvent = Assert.Single(fixture.ReadSessionEvents(sessionB), item => item.Envelope.RunId == runB1
                && item.Payload is PermissionGranted granted && granted.ToolCallId == patchB1.ToolCallId);
            Assert.Equal(GrantLifetime.Workspace, ((PermissionGranted)b1GrantedEvent.Payload).Lifetime);
            var workspaceGrantId = ((PermissionGranted)b1GrantedEvent.Payload).GrantId;
            Assert.NotNull(workspaceGrantId);
            var workspaceGrant = Assert.Single(grants.List(workspaceId));
            Assert.Equal(workspaceGrantId, workspaceGrant.Id);
            Assert.Equal(GrantLifetime.Workspace, workspaceGrant.Lifetime);
            Assert.Null(workspaceGrant.Run);
            Assert.Equal(claimsKeyB1, workspaceGrant.ClaimsKey);

            var cancelB1 = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"run.cancel\",\"runId\":"
                + JsonSerializer.Serialize(b1.RunId) + "}");
            var cancelB1Ack = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(cancelB1, fixture.Key,
                fixture.Nonce!, fixture.Workspace, sessionB, b1.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, cancelB1Ack.Outcome?.Kind);
            await fixture.WaitForRunCancellationAsync(b1.RunId);
            Assert.Equal(CommandReceiptState.Completed, fixture.ReadReceipt(cancelB1.MessageId)?.State);

            // Same B Session, new Run: the Workspace grant does not bypass PatchOnly's per-Turn ceiling.
            var b2 = await StartInput(fixture, sessionB, "Try an oversized localized patch.");
            Assert.NotEqual(runB1, RunId.Parse(b2.RunId));
            Assert.Equal("PatchOnly", ReadNativeSessionSecurity(fixture.Host!)
                .GetProperty("category").GetString());
            await fixture.Provider.WaitForRequestAsync(9, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await fixture.WaitForTurnCompletionAsync(b2.RunId, b2.TurnId);
            var b2ExecutionReceipt = await fixture.WaitForExecutionReceiptAsync(b2.CommandId);
            Assert.NotNull(b2ExecutionReceipt.ExecutionState);
            var b2Events = fixture.ReadSessionEvents(sessionB);
            var patchB2 = PatchRequest(b2Events, RunId.Parse(b2.RunId));
            Assert.Equal(claimsKeyB1, PatchClaimsKey(patchB2, fixture.Workspace));
            var b2Evaluation = Assert.Single(b2Events, item => item.Envelope.RunId == RunId.Parse(b2.RunId)
                && item.Payload is PermissionEvaluated evaluated && evaluated.ToolCallId == patchB2.ToolCallId);
            Assert.Equal(PermissionDecision.Allow, ((PermissionEvaluated)b2Evaluation.Payload).Decision);
            var b2Permission = (PermissionEvaluated)b2Evaluation.Payload;
            Assert.Equal(workspaceGrantId, b2Permission.AppliedGrant);
            Assert.Contains(grants.List(workspaceId), grant => grant.Id == workspaceGrantId
                && grant.Lifetime == GrantLifetime.Workspace && grant.Run is null
                && grant.ClaimsKey == claimsKeyB1);
            var b2Failure = Assert.Single(b2Events, item => item.Envelope.RunId == RunId.Parse(b2.RunId)
                && item.Payload is ToolCallFailed failed && failed.ToolCallId == patchB2.ToolCallId);
            Assert.Equal(ToolErrorCode.LimitExceeded, ((ToolCallFailed)b2Failure.Payload).ErrorCode);
            Assert.Contains("200 líneas cambiadas por Turn", ((ToolCallFailed)b2Failure.Payload).Cause,
                StringComparison.Ordinal);
            Assert.DoesNotContain(b2Events, item => item.Envelope.RunId == RunId.Parse(b2.RunId)
                && item.Payload is ToolCallSucceeded succeeded && succeeded.ToolCallId == patchB2.ToolCallId);
            Assert.Equal(GrantContent("state-2"), File.ReadAllText(fixture.GrantTargetPath));

            // Turn completion does not end the enclosing Run; cancel it through the same signed
            // native control path before switching the active Session.
            var b2Terminal = fixture.ReadSessionEvents(sessionB).Any(item => item.Envelope.RunId == RunId.Parse(b2.RunId)
                && item.Payload is RunCompleted or RunFailed or RunCancelled);
            Assert.True(b2Terminal, $"B2 execution receipt is {b2ExecutionReceipt.ExecutionState}/"
                + $"{b2ExecutionReceipt.ExecutionCode}, but its journal has no terminal Run event.");

            // Select A in the same Host, then submit a fresh input: A2 must not reuse A1's Run grant.
            ResumeSession(fixture, sessionA, sessionB);
            Assert.Equal("FullAgent", ReadNativeSessionSecurity(fixture.Host!)
                .GetProperty("category").GetString());
            var a2 = await StartInput(fixture, sessionA, "Apply the patch in a new Run using current workspace approval.");
            var runA2 = RunId.Parse(a2.RunId);
            Assert.NotEqual(runA1, runA2);
            Assert.Equal(sessionA, fixture.CurrentSessionId);
            await fixture.Provider.WaitForRequestAsync(12, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await fixture.WaitForTurnCompletionAsync(a2.RunId, a2.TurnId);
            var a2ExecutionReceipt = await fixture.WaitForExecutionReceiptAsync(a2.CommandId);
            Assert.NotNull(a2ExecutionReceipt.ExecutionState);

            var a2Events = fixture.ReadSessionEvents(sessionA);
            var patchA2 = PatchRequest(a2Events, runA2);
            Assert.Equal(claimsKeyA1, PatchClaimsKey(patchA2, fixture.Workspace));
            var a2Evaluation = Assert.Single(a2Events, item => item.Envelope.RunId == runA2
                && item.Payload is PermissionEvaluated evaluated && evaluated.ToolCallId == patchA2.ToolCallId);
            Assert.Equal(PermissionDecision.Allow, ((PermissionEvaluated)a2Evaluation.Payload).Decision);
            Assert.Equal(workspaceGrantId, ((PermissionEvaluated)a2Evaluation.Payload).AppliedGrant);
            Assert.DoesNotContain(a2Events, item => item.Envelope.RunId == runA2
                && item.Payload is InteractionRequested request && request.Kind == InteractionKind.Permission);
            Assert.Contains(a2Events, item => item.Envelope.RunId == runA2
                && item.Envelope.ToolCallId == patchA2.ToolCallId && item.Payload is ToolCallSucceeded);
            Assert.Equal(GrantContent("state-3"), File.ReadAllText(fixture.GrantTargetPath));

            var allGrantsInA2 = grants.List(workspaceId, runA2);
            Assert.Contains(allGrantsInA2, grant => grant.Id == workspaceGrantId);
            Assert.DoesNotContain(allGrantsInA2, grant => grant.Id == a1Grant.Id);
            Assert.Equal(12, fixture.Provider.RequestCount);
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static string GrantContent(string state) => string.Join("\n",
        Enumerable.Range(1, 8).Select(index => "left-padding-" + index)
            .Append("prefix")
            .Append(state)
            .Append("suffix")
            .Concat(Enumerable.Range(1, 8).Select(index => "right-padding-" + index))) + "\n";

    private static void SetSessionSecurityDefault(NativeChoiceFixture fixture, ModelPolicyCategory category)
    {
        var prior = QueryNativeJson(fixture.Host!, "sessionSecurityDefault");
        var command = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.security.default.set\",\"category\":"
            + JsonSerializer.Serialize(category.ToString()) + ",\"expectedRevision\":"
            + JsonSerializer.Serialize(prior.GetProperty("revision").GetString()) + "}");
        var ack = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(command, fixture.Key, fixture.Nonce!,
            fixture.Workspace, fixture.CurrentSessionId, null)));
        Assert.True(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            "session.security.default.set rejected: " + ack.Error);
        Assert.Equal(category.ToString(), QueryNativeJson(fixture.Host!, "sessionSecurityDefault")
            .GetProperty("category").GetString());
    }

    private static void ResumeSession(NativeChoiceFixture fixture, string targetSession, string currentSession)
    {
        var command = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.resume\",\"sessionId\":"
            + JsonSerializer.Serialize(targetSession) + "}");
        var ack = Ack(RawExchange(fixture.Host!, TrustedActionEnvelope(command, fixture.Key, fixture.Nonce!,
            fixture.Workspace, currentSession, null)));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        fixture.SetCurrentSessionIdForTest(targetSession);
        Assert.Equal(targetSession, QueryNativeJson(fixture.Host!, "humanActionScope")
            .GetProperty("sessionId").GetString());
    }

    private static async Task<(string RunId, TurnId TurnId, string CommandId)> StartInput(
        NativeChoiceFixture fixture, string sessionId, string text)
    {
        var policy = ReadNativeSessionSecurity(fixture.Host!);
        Assert.Equal(sessionId, policy.GetProperty("sessionId").GetString());
        var revision = policy.GetProperty("revision").GetInt64();
        var command = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.start\",\"text\":"
            + JsonSerializer.Serialize(text) + ",\"mode\":\"act\",\"expectedRevision\":null,\"expectedPolicyRevision\":"
            + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"expectedDefaultRevision\":null}");
        var proof = TrustedActionEnvelope(command, fixture.Key, fixture.Nonce!, fixture.Workspace, sessionId, null);
        var startDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        CommandAck ack;
        do
        {
            ack = Ack(RawExchange(fixture.Host!, proof));
            if (ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Deferred || ack.Error != "conversation_turn_busy") break;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        } while (DateTimeOffset.UtcNow < startDeadline);
        Assert.True(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            "conversation.start rejected: " + ack.Error);
        fixture.SetCurrentSessionIdForTest(sessionId);
        var scope = ReadCurrentScope(fixture.Host!);
        Assert.Equal(sessionId, scope.SessionId);
        Assert.NotNull(scope.RunId);
        var turnDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < turnDeadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var events = fixture.ReadSessionEvents(sessionId);
            var turn = events.SingleOrDefault(item => item.Envelope.RunId?.ToString() == scope.RunId
                && item.Payload is TurnStarted);
            if (turn.Payload is TurnStarted)
            {
                Assert.Contains(events, item => item.Envelope.RunId?.ToString() == scope.RunId
                    && item.Payload is UserInputReceived);
                Assert.NotNull(turn.Envelope.TurnId);
                return (scope.RunId!, new TurnId(turn.Envelope.TurnId!.Value), command.MessageId);
            }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("conversation.start did not persist a TurnStarted for its new Run.");
    }

    private static ToolCallRequested PatchRequest(
        IReadOnlyList<(DomainEvent Envelope, DomainEventPayload Payload)> events, RunId run)
    {
        var request = Assert.Single(events, item => item.Envelope.RunId == run
            && item.Payload is ToolCallRequested call && call.ToolName == "filesystem.patch");
        var patch = (ToolCallRequested)request.Payload;
        Assert.Contains(events, item => item.Envelope.RunId == run
            && item.Payload is ToolCallPrepared prepared && prepared.ToolCallId == patch.ToolCallId);
        return patch;
    }

    private static string PatchClaimsKey(ToolCallRequested request, string workspace)
    {
        using var args = JsonDocument.Parse(request.ArgumentsJson);
        var path = args.RootElement.GetProperty("path").GetString();
        Assert.Equal("grant-dispatch-target.txt", path);
        var prepared = new FilesystemPatchTool(new PathBoundaryValidator()).Prepare(
            new ValidatedToolCall(request.ToolCallId, new ToolId("filesystem.patch"), request.ProviderCallId,
                request.ArgumentsJson), new ToolPreparationContext(workspace, DateTimeOffset.UtcNow));
        return FilePermissionGrantStore.ClaimsKey(Assert.IsType<Prepared>(prepared).Intent);
    }

}
