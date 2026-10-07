using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>End-to-end authority boundary for advisory model mode proposals.</summary>
public sealed class ModeProposalUserAcceptanceTests
{
    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";

    private static WireEnvelope Command(string payload) => WireEnvelope.Command(Ids.NewV7(), payload);

    [Theory]
    [InlineData("orq", false, false)]
    [InlineData("act", true, false)]
    [InlineData("act", true, true)]
    public void Model_proposal_requires_user_selection_or_explicit_current_plan_coverage(
        string proposedMode, bool adaptive, bool coveredPlan)
    {
        var server = OmniHost.CreateInMemoryServer();
        var cancellationToken = TestContext.Current.CancellationToken;
        var started = server.SendUserAction(Command(Payload(
            JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "Review this task and recommend a mode"),
            JsonObj.Field("mode", "plan"))), cancellationToken);
        Assert.Equal("ok", started.Status);

        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var lane = Assert.IsType<LaneId>(server.LastLaneId());
        var store = server.AcquireStore();
        var codecs = server.AcquireCodecs();
        if (adaptive)
        {
            Assert.Null(server.EnsureSessionRoutingPolicy(session).Failure);
            var granted = server.SendUserAction(Command("{\"cmd\":\"run.mode.select\",\"mode\":\"plan\","
                + "\"effort\":\"ultracode\",\"adaptive\":true,\"allowedModes\":\"plan,act,orq\","
                + "\"maxAgents\":1,\"maxDepth\":1,\"maxTurns\":3,\"maxToolCalls\":8,"
                + "\"maxElapsedSeconds\":120,\"maxSpendUsd\":2.5,\"coverCurrentPlan\":"
                + (coveredPlan ? "true" : "false") + "}"), cancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, granted.Outcome?.Kind);
        }
        var initialAuthority = Assert.IsType<RunModeAuthority>(server.CurrentModeAuthority());
        Assert.Equal(RunMode.Plan, initialAuthority.Mode);
        var beforeTurnSequence = store.CurrentSequence(session);

        var workspace = Path.Combine(Path.GetTempPath(), "omni-mode-proposal-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var artifacts = new FileArtifactStore(Path.Combine(workspace, "cas"));
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var toolCall = ToolCallId.New();
            var providerCalls = 0;
            var turn = new ExplorerTurn((_, _) => ++providerCalls == 1
                    ? new ModelResponse([new ToolCallBlock(toolCall, "scripted-proposal", "mode.propose",
                        "{\"mode\":\"" + proposedMode + "\",\"reason\":\"Independent review may help\"}")],
                        StopReason.ToolUse, new TokenUsage(3, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "test", null))
                    : new ModelResponse([new TextBlock("The mode is unchanged until the user selects it.")],
                        StopReason.EndTurn, new TokenUsage(2, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "test", null)),
                ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy([]), workspace),
                catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "model", "tool", "context", "output", "test-build"),
                new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maximumGenerationRequestAttempts: 1);

            var execution = server.ExecuteExplorerTurn(session, run,
                token => turn.Ask("Review this task and recommend a mode", "system", session, run, lane, "", token),
                cancellationToken);
            Assert.Null(execution.Failure);
            Assert.Equal("ok", execution.Ack.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, execution.Ack.Outcome?.Kind);
            Assert.Equal(StopReason.EndTurn, Assert.IsType<ExplorerTurn.TurnResult>(execution.Result).StopReason);
            Assert.Equal(2, providerCalls);
            Assert.NotNull(execution.Ack.FirstSeq);
            Assert.NotNull(execution.Ack.LastSeq);
            Assert.Equal(beforeTurnSequence + 1, execution.Ack.FirstSeq.Value);
            Assert.True(execution.Ack.LastSeq.Value >= execution.Ack.FirstSeq.Value);
            Assert.Equal(session, server.LastSessionId());
            Assert.Equal(run, server.LastRunId());

            var afterProposal = store.ReadFrom(session, 1);
            var proposal = Assert.Single(ModeProposalProjection.Replay(session, run, codecs, afterProposal));
            Assert.Equal(toolCall, proposal.ToolCallId);
            Assert.Equal(RunMode.Plan, proposal.From);
            Assert.Equal(proposedMode == "act" ? RunMode.Act : RunMode.Orchestrate, proposal.To);

            if (adaptive && coveredPlan)
            {
                Assert.Equal(RuntimeCommandOutcomeKind.Accepted, execution.PolicyTransition?.Ack.Outcome?.Kind);
                Assert.Equal(RunMode.Act, server.CurrentRunMode());
                var transitionEvent = Assert.Single(afterProposal, evt => codecs.Decode(evt)
                    is RunModeTransitionAuthorized { Origin: "UltraCodePolicy" });
                var automaticTransition = Assert.IsType<RunModeTransitionAuthorized>(codecs.Decode(transitionEvent));
                var proposalEvent = Assert.Single(afterProposal, evt => codecs.Decode(evt) is RunModeProposed);
                Assert.Equal(proposalEvent.EventId, automaticTransition.ProposalEventId);
                Assert.Equal(initialAuthority.Authorization?.PlanCoverage, automaticTransition.PlanCoverage);
                Assert.True(transitionEvent.Sequence > execution.Ack.LastSeq);
                Assert.DoesNotContain(afterProposal.Select(codecs.Decode), payload => payload is InteractionResolved);
                Assert.Equal(RunMode.Act, RunProjection.Replay(session, run, codecs, afterProposal).Mode);
                return;
            }
            if (adaptive)
                Assert.Equal(RuntimeCommandOutcomeKind.Deferred, execution.PolicyTransition?.Ack.Outcome?.Kind);

            var advisoryProjection = RunProjection.Replay(session, run, codecs, afterProposal);
            Assert.Equal(RunMode.Plan, advisoryProjection.Mode);
            Assert.Equal(initialAuthority.Revision, advisoryProjection.ModeAuthority?.Revision);
            Assert.Equal(initialAuthority.ObjectiveRevision, advisoryProjection.ModeAuthority?.ObjectiveRevision);
            Assert.Equal(initialAuthority.PolicyRevision, advisoryProjection.ModeAuthority?.PolicyRevision);
            Assert.Equal(adaptive, advisoryProjection.ModeAuthority?.AutoModeSwitch);
            Assert.DoesNotContain(afterProposal.Where(evt => evt.Sequence > beforeTurnSequence).Select(codecs.Decode), payload => payload is RunModeChanged
                or RunModeTransitionAuthorized or RunModeAuthorityRevoked);

            // A generic wire command carries no trusted-user origin and must not consume a sequence.
            var select = Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
                JsonObj.Field("mode", "orq"), JsonObj.Field("effort", "standard")));
            var beforeUntrustedAttempt = store.CurrentSequence(session);
            var untrusted = server.Send(select, cancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, untrusted.Outcome?.Kind);
            Assert.Equal(beforeUntrustedAttempt, store.CurrentSequence(session));
            Assert.Equal(RunMode.Plan, server.CurrentRunMode());
            Assert.Equal(initialAuthority.Revision, server.CurrentModeAuthority()?.Revision);

            // The explicit trusted-user boundary accepts the same command on the same active Run.
            var selected = server.SendUserAction(select, cancellationToken);
            Assert.Equal("ok", selected.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
            Assert.NotNull(selected.FirstSeq);
            Assert.NotNull(selected.LastSeq);
            Assert.True(selected.FirstSeq.Value > beforeUntrustedAttempt);
            Assert.True(selected.LastSeq.Value >= selected.FirstSeq.Value);
            Assert.Equal(session, server.LastSessionId());
            Assert.Equal(run, server.LastRunId());

            var completeJournal = store.ReadFrom(session, 1);
            var finalProjection = RunProjection.Replay(session, run, codecs, completeJournal);
            Assert.Equal(RunMode.Orchestrate, finalProjection.Mode);
            Assert.Equal(ProductEffort.Standard, finalProjection.ModeAuthority?.ProductEffort);
            Assert.True(finalProjection.ModeAuthority?.ModePinned);
            Assert.False(finalProjection.ModeAuthority?.AutoModeSwitch);
            var transition = completeJournal.Select(codecs.Decode).OfType<RunModeTransitionAuthorized>().Last();
            Assert.Equal(select.MessageId, transition.CommandId);
            Assert.Equal("User", transition.Origin);
            Assert.Equal(RunMode.Plan, transition.From);
            Assert.Equal(RunMode.Orchestrate, transition.To);
            Assert.Single(ModeProposalProjection.Replay(session, run, codecs, completeJournal));
            Assert.Single(completeJournal.Select(codecs.Decode).OfType<RunModeProposed>());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
