using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;
using Microsoft.Data.Sqlite;

namespace OmniCore.Tests;

// Scripted model fixture; the production tool pipeline, SQLite, codecs and protocol are real.
public sealed class ModeProposalIntegrationTests
{
    [Theory]
    [InlineData("orq", RunMode.Orchestrate, "Independent review would help")]
    [InlineData("plan", RunMode.Plan, "Review api_key=abcdefghijklmnop safely")]
    public void Normal_model_tool_records_advice_without_changing_authority_and_reopens(string target, RunMode mode, string reason)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mode-proposal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SqliteEventStore(Path.Combine(root, "journal.db"));
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(new EventStream(store, codecs, session), session, "mode proposal fixture");
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var tool = ToolCallId.New();
            var calls = 0;
            var turn = new ExplorerTurn((_, _) => ++calls == 1
                ? new ModelResponse([new ToolCallBlock(tool, "fixture-proposal", "mode.propose",
                    System.Text.Json.JsonSerializer.Serialize(new { mode = target, reason }))],
                    StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "", null))
                : new ModelResponse([new TextBlock("Recommendation only; current mode is unchanged.")],
                    StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "", null)),
                ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy([]), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null), store, codecs,
                artifacts, new InMemoryAuditSink(), new RedactionPolicy());
            var answer = turn.Ask("Suggest a suitable mode", "system", session, run.RunId, run.RootLane, "",
                TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, answer.StopReason);
            Assert.Equal(2, calls);
            var journal = store.ReadFrom(session, 1);
            var proposal = Assert.Single(ModeProposalProjection.Replay(session, run.RunId, codecs, journal));
            Assert.Equal(mode, proposal.To);
            Assert.Equal(RunMode.Act, proposal.From);
            Assert.Equal(tool, proposal.ToolCallId);
            var before = RunProjection.Replay(session, run.RunId, codecs, journal);
            Assert.Equal(RunMode.Act, before.Mode);
            Assert.False(before.ModeAuthority!.AutoModeSwitch);
            Assert.DoesNotContain(journal.Select(codecs.Decode), e => e is RunModeChanged
                or RunModeTransitionAuthorized or AgentExecutionStarted);
            var wire = Assert.Single(new ProtocolMapper(codecs, artifacts).Map(journal),
                e => e.PayloadJson.Contains("run.mode_proposed", StringComparison.Ordinal));
            Assert.Contains("\"advisory\":\"true\"", wire.PayloadJson);
            Assert.Contains(proposal.TurnId.ToString(), wire.PayloadJson);
            Assert.Contains(session.ToString(), wire.PayloadJson);
            Assert.Equal(new PiiRedactor().Redact(reason), proposal.Reason);
            Assert.DoesNotContain("abcdefghijklmnop", wire.PayloadJson);
            var proposalEnvelope = Assert.Single(journal, item => codecs.Decode(item) is RunModeProposed);
            var prefix = journal.TakeWhile(item => item.EventId != proposalEnvelope.EventId).ToArray();
            foreach (var wrongScope in new[]
            {
                new ExecutionScopeState(run.RunId, TaskId.New(), run.RootLane, proposal.TurnId),
                new ExecutionScopeState(run.RunId, run.RootTask, LaneId.New(), proposal.TurnId),
                new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, proposal.TurnId,
                    ExecutionId: ExecutionId.New()),
            })
            {
                var candidateStore = new InMemoryEventStore();
                candidateStore.AppendBatch(session, prefix, DurabilityClass.Standard, CancellationToken.None);
                var beforeCandidate = candidateStore.CurrentSequence(session);
                Assert.Throws<InvalidStateTransitionException>(() => new EventStream(candidateStore, codecs, session)
                    .AppendBatch([proposal], DurabilityClass.Standard, [wrongScope]));
                Assert.Equal(beforeCandidate, candidateStore.CurrentSequence(session));
                // Retry with the real scope must succeed after the rejected append.
                new EventStream(candidateStore, codecs, session).AppendBatch([proposal], DurabilityClass.Standard,
                    [new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, proposal.TurnId)]);
                Assert.Single(ModeProposalProjection.Replay(session, run.RunId, codecs, candidateStore.ReadFrom(session, 1)));
            }
            DomainEvent Candidate(RunModeProposed value, int version = 1) => DomainEvent.Create(session,
                value.Type(), version, proposalEnvelope.Causation, value.RunId, value.RunId,
                proposalEnvelope.TaskId, proposalEnvelope.LaneId, value.TurnId, null, value.ToolCallId,
                [], codecs.CodecFor(value.Type()).Encode(value));
            foreach (var invalid in new[]
            {
                proposal with { AuthorityRevision = proposal.AuthorityRevision + 1 },
                proposal with { ObjectiveDigest = "foreign objective" },
                proposal with { PolicyRevision = proposal.PolicyRevision + 1 },
                proposal with { To = proposal.To == RunMode.Plan ? RunMode.Orchestrate : RunMode.Plan },
                proposal with { Reason = "invented rationale" },
                proposal with { TurnId = TurnId.New() },
                proposal with { RunId = RunId.New() },
            })
                Assert.Throws<InvalidStateTransitionException>(() => ModeProposalProjection.Replay(session,
                    run.RunId, codecs, prefix.Append(Candidate(invalid))));
            Assert.Throws<InvalidStateTransitionException>(() => ModeProposalProjection.Replay(session,
                run.RunId, codecs, prefix.Append(Candidate(proposal)).Append(Candidate(proposal))));
            Assert.Throws<EventParseException>(() => codecs.Decode(Candidate(proposal, 0)));
            store.Close();
            store = new SqliteEventStore(Path.Combine(root, "journal.db"));
            Assert.Equal(proposal, Assert.Single(ModeProposalProjection.Replay(session, run.RunId, codecs,
                store.ReadFrom(session, 1))));

            // A copied record is not another recommendation. Rejection cannot consume sequence/identity.
            var sequence = store.CurrentSequence(session);
            Assert.Throws<InvalidStateTransitionException>(() =>
                new EventStream(store, codecs, session).Append(proposal));
            Assert.Equal(sequence, store.CurrentSequence(session));
            Assert.Empty(ModeProposalProjection.Replay(session, RunId.New(), codecs, store.ReadFrom(session, 1)));
            Assert.Throws<InvalidStateTransitionException>(() => ModeProposalProjection.Replay(SessionId.New(),
                run.RunId, codecs, store.ReadFrom(session, 1)));
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + Path.Combine(root, "journal.db"));
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mode\":\"orq\",\"reason\":\"\"}")]
    [InlineData("{\"mode\":\"ORQ\",\"reason\":\"review\"}")]
    [InlineData("{\"mode\":\"orq\",\"reason\":\"review\",\"origin\":\"User\"}")]
    [InlineData("{\"mode\":\"orq\",\"mode\":\"act\",\"reason\":\"review\"}")]
    [InlineData("{\"mode\":\"orq\",\"reason\":123}")]
    public void Tool_does_not_accept_authority_or_invalid_recommendations(string arguments) =>
        Assert.False(ModeProposeTool.TryParse(arguments, out _, out _));

    [Fact]
    public void Missing_tool_and_turn_cannot_write_a_proposal()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var stream = new EventStream(store, codecs, session);
        var run = TestRun.Open(stream, session, "missing receipt fixture");
        var authority = RunProjection.Replay(session, run.RunId, codecs, store.ReadFrom(session, 1)).ModeAuthority!;
        var proposal = new RunModeProposed(run.RunId, TurnId.New(), ToolCallId.New(), RunMode.Act,
            RunMode.Orchestrate, "review", authority.Revision, authority.ObjectiveRevision,
            authority.ObjectiveDigest, authority.PolicyRevision);
        var before = store.CurrentSequence(session);
        Assert.Throws<InvalidStateTransitionException>(() => stream.Append(proposal));
        Assert.Equal(before, store.CurrentSequence(session));
    }
}
