using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Offline persistence and replay coverage for the internal UltraCode policy boundary.</summary>
public sealed class UltraCodePolicyTransitionIntegrationTests
{
    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";
    private static WireEnvelope Command(string payload) => WireEnvelope.Command(Ids.NewV7(), payload);

    [Fact]
    public void Policy_transition_is_one_causal_sqlite_batch_and_retains_the_grant_after_reopen()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "presión de presupuesto: reduce execution scope", authority.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Outcome?.Kind);
        Assert.Null(result.Failure);
        Assert.Equal(before + 3, fixture.Store.CurrentSequence(fixture.Session));
        Assert.Equal(before + 1, result.FirstSeq);
        Assert.Equal(before + 3, result.LastSeq);
        var appended = fixture.Store.ReadFrom(fixture.Session, before + 1);
        Assert.Collection(appended,
            item => Assert.IsType<RunModeChanged>(fixture.Codecs.Decode(item)),
            item => Assert.IsType<RunModeTransitionAuthorized>(fixture.Codecs.Decode(item)),
            item => Assert.IsType<RunModeAuthoritySelected>(fixture.Codecs.Decode(item)));
        Assert.All(appended, item =>
        {
            Assert.Equal(fixture.Session, item.SessionId);
            Assert.Equal(fixture.Run, item.RunId);
            Assert.Equal(fixture.Run, item.CorrelationId);
            Assert.Equal(new CommandCausation(CommandId.Parse(result.CommandId)), item.Causation);
        });
        var changed = Assert.IsType<RunModeChanged>(fixture.Codecs.Decode(appended[0]));
        var authorized = Assert.IsType<RunModeTransitionAuthorized>(fixture.Codecs.Decode(appended[1]));
        var selected = Assert.IsType<RunModeAuthoritySelected>(fixture.Codecs.Decode(appended[2]));
        Assert.Equal("presión de presupuesto: reduce execution scope", changed.Cause);
        Assert.Equal("UltraCodePolicy", authorized.Origin);
        Assert.Equal(result.CommandId, authorized.CommandId);
        Assert.Equal(authority.Authorization?.AuthorizationId, authorized.AuthorizationId);
        Assert.Equal(authority.Revision + 1, selected.Authority.Revision);
        Assert.Equal(authority.Authorization?.AuthorizationId, selected.Authority.Authorization?.AuthorizationId);
        Assert.Equal(authority.Authorization?.Limits, selected.Authority.Authorization?.Limits);
        Assert.True(authority.Authorization!.AllowedModes.SequenceEqual(
            selected.Authority.Authorization!.AllowedModes));

        fixture.CloseStore();
        fixture.ReopenStore();
        var replay = RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1));
        Assert.Equal(RunMode.Plan, replay.Mode);
        Assert.Equal(ProductEffort.UltraCode, replay.ModeAuthority?.ProductEffort);
        Assert.Equal(authority.Revision + 1, replay.ModeAuthority?.Revision);
        Assert.Equal(authority.Authorization?.AuthorizationId, replay.ModeAuthority?.Authorization?.AuthorizationId);
        Assert.Equal(authority.Authorization?.Limits, replay.ModeAuthority?.Authorization?.Limits);
    }

    [Fact]
    public void Replaying_one_run_validates_authority_envelopes_for_other_runs_against_their_own_payload_scope()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var firstRun = fixture.Run;
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var transitioned = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, firstRun,
            RunMode.Plan, "reduce execution scope", authority.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, transitioned.Outcome?.Kind);
        Assert.Null(transitioned.Failure);

        Assert.Equal("ok", fixture.Server.Send(Command(Payload(JsonObj.Field("cmd", "run.cancel"),
            JsonObj.Field("runId", firstRun.ToString()))), TestContext.Current.CancellationToken).Status);
        Assert.Equal("ok", fixture.Server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "second run in the same session"))), TestContext.Current.CancellationToken).Status);
        var secondRun = Assert.IsType<RunId>(fixture.Server.LastRunId());
        Assert.NotEqual(firstRun, secondRun);

        var completeSessionJournal = fixture.Store.ReadFrom(fixture.Session, 1);
        Assert.Equal(RunState.Cancelled,
            RunProjection.Replay(fixture.Session, firstRun, fixture.Codecs, completeSessionJournal).State);
        Assert.Equal(RunMode.Plan,
            RunProjection.Replay(fixture.Session, firstRun, fixture.Codecs, completeSessionJournal).Mode);
        Assert.Equal(RunState.Running,
            RunProjection.Replay(fixture.Session, secondRun, fixture.Codecs, completeSessionJournal).State);
        Assert.Equal(RunMode.Act,
            RunProjection.Replay(fixture.Session, secondRun, fixture.Codecs, completeSessionJournal).Mode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_policy_decisions_with_the_same_revision_accept_exactly_one()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var secondServer = new OmniServer(fixture.Store, fixture.Codecs, new InMemoryAuditSink());
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var cancellationToken = TestContext.Current.CancellationToken;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = System.Threading.Tasks.Task.Run(async () =>
        {
            await start.Task.WaitAsync(cancellationToken);
            return fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
                RunMode.Plan, "policy decision A", authority.Revision, cancellationToken);
        }, cancellationToken);
        var second = System.Threading.Tasks.Task.Run(async () =>
        {
            await start.Task.WaitAsync(cancellationToken);
            return secondServer.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
                RunMode.Orchestrate, "policy decision B", authority.Revision, cancellationToken);
        }, cancellationToken);

        start.SetResult(true);
        var results = await System.Threading.Tasks.Task.WhenAll(first, second).WaitAsync(cancellationToken);

        var accepted = Assert.Single(results, result => result.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted);
        Assert.Null(accepted.Failure);
        Assert.Equal(before + 1, accepted.FirstSeq);
        Assert.Equal(before + 3, accepted.LastSeq);
        Assert.Single(results, result => result.Outcome?.Kind == RuntimeCommandOutcomeKind.Rejected);
        var appended = fixture.Store.ReadFrom(fixture.Session, before + 1);
        Assert.Equal(3, appended.Count);
        Assert.All(appended, item => Assert.True(fixture.Codecs.Decode(item) is
            RunModeChanged or RunModeTransitionAuthorized or RunModeAuthoritySelected));
        Assert.DoesNotContain(appended, item => fixture.Codecs.Decode(item) is TaskCreated or InteractionResolved);
        var projection = RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1));
        Assert.Contains(Assert.IsType<RunMode>(projection.Mode), new[] { RunMode.Plan, RunMode.Orchestrate });
        Assert.Equal(authority.Revision + 1, projection.ModeAuthority?.Revision);
    }

    [Fact]
    public void SQLite_failure_during_authority_selection_rolls_back_the_whole_policy_batch()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        using (var trigger = new SqliteConnection("DataSource=" + fixture.Journal))
        {
            trigger.Open();
            using var command = trigger.CreateCommand();
            command.CommandText = $"CREATE TRIGGER fail_policy_append BEFORE INSERT ON events " +
                $"WHEN NEW.session_id = '{fixture.Session}' AND NEW.seq > {before + 2} " +
                "BEGIN SELECT RAISE(ABORT, 'fixture policy append failure'); END;";
            command.ExecuteNonQuery();
        }

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "must not partially publish", authority.Revision, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Failure);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        var unchanged = RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1));
        AssertAuthorityEqual(authority, unchanged.ModeAuthority);
        Assert.DoesNotContain(fixture.Store.ReadFrom(fixture.Session, 1), item =>
            fixture.Codecs.Decode(item) is RunModeChanged changed && changed.Cause == "must not partially publish");
        fixture.CloseStore();
        fixture.ReopenStore();
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        AssertAuthorityEqual(authority, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).ModeAuthority);
    }

    [Fact]
    public void Post_commit_store_error_returns_the_durable_ack_and_stale_retry_does_not_append_again()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90, failAfterNextBatchCommit: true);
        var faultStore = Assert.IsType<ThrowAfterCommitStore>(fixture.FaultStore);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var before = fixture.Store.CurrentSequence(fixture.Session);
        faultStore.ThrowAfterNextBatchCommit = true;

        var first = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "durable despite writer error", authority.Revision, TestContext.Current.CancellationToken);

        Assert.IsType<IOException>(first.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, first.Outcome?.Kind);
        Assert.Equal(before + 1, first.FirstSeq);
        Assert.Equal(before + 3, first.LastSeq);
        Assert.Equal(before + 3, fixture.Store.CurrentSequence(fixture.Session));
        var retry = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "durable despite writer error", authority.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, retry.Outcome?.Kind);
        Assert.Equal(before + 3, fixture.Store.CurrentSequence(fixture.Session));
        Assert.Equal(3, fixture.Store.ReadFrom(fixture.Session, before + 1).Count);

        fixture.CloseStore();
        fixture.ReopenStore();
        Assert.Equal(RunMode.Plan, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).Mode);
    }

    [Fact]
    public void Replay_rejects_policy_events_split_across_distinct_command_causations()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var authorization = Assert.IsType<ModeSwitchAuthorization>(authority.Authorization);
        var reason = "split causal batch";
        var firstCommand = CommandId.New();
        var secondCommand = CommandId.New();
        fixture.Store.Append(fixture.Session, RawEvent(fixture, new RunModeChanged(fixture.Run,
            authority.Mode, RunMode.Plan, reason), firstCommand), DurabilityClass.Standard,
            TestContext.Current.CancellationToken);
        var nextRevision = authority.Revision + 1;
        var selected = authority with
        {
            Revision = nextRevision,
            Mode = RunMode.Plan,
            Authorization = authorization with { AuthorityRevision = nextRevision },
        };
        fixture.Store.AppendBatch(fixture.Session, new[]
        {
            RawEvent(fixture, new RunModeTransitionAuthorized(fixture.Run, authority.Mode, RunMode.Plan,
                reason, "UltraCodePolicy", secondCommand.ToString(), nextRevision, authority.ObjectiveRevision,
                authority.ObjectiveDigest, authority.PolicyRevision, authorization.AuthorizationId), secondCommand),
            RawEvent(fixture, new RunModeAuthoritySelected(selected, secondCommand.ToString(), "UltraCodePolicy"),
                secondCommand),
        }, DurabilityClass.Barrier, TestContext.Current.CancellationToken);

        Assert.Throws<InvalidStateTransitionException>(() => RunProjection.Replay(fixture.Session, fixture.Run,
            fixture.Codecs, fixture.Store.ReadFrom(fixture.Session, 1)));
    }

    [Fact]
    public void Policy_origin_cannot_be_persisted_outside_the_atomic_transition_batch()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var authorization = Assert.IsType<ModeSwitchAuthorization>(authority.Authorization);
        var commandId = CommandId.New();
        var transition = new RunModeTransitionAuthorized(fixture.Run, authority.Mode, RunMode.Plan,
            "unpaired policy transition", "UltraCodePolicy", commandId.ToString(), authority.Revision + 1,
            authority.ObjectiveRevision, authority.ObjectiveDigest, authority.PolicyRevision,
            authorization.AuthorizationId);

        using (CausationScope.Begin(new CommandCausation(commandId)))
            Assert.Throws<InvalidStateTransitionException>(() =>
                new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(transition,
                    DurabilityClass.Barrier));

        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
    }

    [Fact]
    public void Stale_scope_and_unallowlisted_policy_decisions_do_not_write()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90, allowedModes: "act");
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var before = fixture.Store.CurrentSequence(fixture.Session);

        var stale = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "stale", authority.Revision - 1, TestContext.Current.CancellationToken);
        var wrongSession = fixture.Server.ApplyUltraCodePolicyTransition(SessionId.New(), fixture.Run,
            RunMode.Plan, "wrong session", authority.Revision, TestContext.Current.CancellationToken);
        var wrongRun = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, RunId.New(),
            RunMode.Plan, "wrong run", authority.Revision, TestContext.Current.CancellationToken);
        var deniedMode = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "not allowlisted", authority.Revision, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, stale.Outcome?.Kind);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongSession.Outcome?.Kind);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongRun.Outcome?.Kind);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, deniedMode.Outcome?.Kind);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        AssertAuthorityEqual(authority, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).ModeAuthority);
    }

    [Fact]
    public void Policy_batch_with_mixed_origins_is_rejected_before_append()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var authorization = Assert.IsType<ModeSwitchAuthorization>(authority.Authorization);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var commandId = CommandId.New();
        var revision = authority.Revision + 1;
        var selected = authority with
        {
            Revision = revision,
            Mode = RunMode.Plan,
            Authorization = authorization with { AuthorityRevision = revision },
        };
        var batch = new DomainEventPayload[]
        {
            new RunModeChanged(fixture.Run, authority.Mode, RunMode.Plan, "origin mismatch"),
            new RunModeTransitionAuthorized(fixture.Run, authority.Mode, RunMode.Plan, "origin mismatch",
                "User", commandId.ToString(), revision, authority.ObjectiveRevision, authority.ObjectiveDigest,
                authority.PolicyRevision, authorization.AuthorizationId),
            new RunModeAuthoritySelected(selected, commandId.ToString(), "UltraCodePolicy"),
        };

        using (CausationScope.Begin(new CommandCausation(commandId)))
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
            Assert.Throws<InvalidStateTransitionException>(() => new EventStream(fixture.Store, fixture.Codecs,
                fixture.Session).AppendBatch(batch, DurabilityClass.Barrier));

        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
    }

    [Theory]
    [InlineData("pinned")]
    [InlineData("revoked")]
    public void Pinned_or_revoked_authority_rejects_internal_policy_transition_after_reopen(string state)
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        if (state == "pinned")
        {
            var pin = fixture.Server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
                JsonObj.Field("mode", "act"), JsonObj.Field("effort", "standard"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, pin.Outcome?.Kind);
        }
        else
        {
            var revoke = fixture.Server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.revoke"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, revoke.Outcome?.Kind);
        }

        fixture.CloseStore();
        fixture.ReopenStore();
        var reopenedServer = new OmniServer(fixture.Store, fixture.Codecs, new InMemoryAuditSink());
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var current = Assert.IsType<RunModeAuthority>(RunProjection.Replay(fixture.Session, fixture.Run,
            fixture.Codecs, fixture.Store.ReadFrom(fixture.Session, 1)).ModeAuthority);

        var result = reopenedServer.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "must not widen pinned or revoked authority", current.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, result.Outcome?.Kind);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        var replay = RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1));
        AssertAuthorityEqual(current, replay.ModeAuthority);
        Assert.DoesNotContain(fixture.Store.ReadFrom(fixture.Session, 1), item =>
            fixture.Codecs.Decode(item) is RunModeTransitionAuthorized { Origin: "UltraCodePolicy" });
    }

    [Fact]
    public void Pending_model_route_consent_defers_policy_transition_without_mode_events()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var interaction = InteractionId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
        {
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(new InteractionRequested(
                interaction, InteractionKind.ModelRouteConsent, "{\"billingMode\":\"MeteredCurrency\"}",
                "[{\"id\":\"allow_route\"},{\"id\":\"deny\"}]", "deny",
                DateTimeOffset.UtcNow.AddMinutes(5), null, null, null, 1, 1), DurabilityClass.Barrier);
        }
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "route consent is a separate gate", authority.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
        Assert.Equal("ModelRouteConsent", result.Outcome?.Reason);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        Assert.DoesNotContain(fixture.Store.ReadFrom(fixture.Session, 1), item =>
            fixture.Codecs.Decode(item) is RunModeChanged
                or RunModeTransitionAuthorized { Origin: "UltraCodePolicy" }
                or RunModeAuthoritySelected { Origin: "UltraCodePolicy" });
    }

    [Fact]
    public void A_not_dispatched_model_step_is_a_safe_boundary_for_policy_transition()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var turn = TurnId.New();
        var lane = Assert.IsType<LaneId>(fixture.Server.LastLaneId());
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run, LaneId: lane, TurnId: turn)))
        {
            var stream = new EventStream(fixture.Store, fixture.Codecs, fixture.Session);
            stream.Append(new TurnStarted(turn, lane), DurabilityClass.Barrier);
            stream.Append(new ModelStepStarted(turn, 0, "private-fixture-model", 1, "none", null, null, null),
                DurabilityClass.Barrier);
            stream.Append(new ModelStepNotDispatched(turn, 0), DurabilityClass.Barrier);
        }
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var before = fixture.Store.CurrentSequence(fixture.Session);

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "no provider dispatch occurred", authority.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Outcome?.Kind);
        Assert.Null(result.Failure);
        Assert.Equal(before + 3, fixture.Store.CurrentSequence(fixture.Session));
        Assert.Contains(fixture.Store.ReadFrom(fixture.Session, 1), item =>
            fixture.Codecs.Decode(item) is ModelStepNotDispatched { TurnId: var id, StepIndex: 0 } && id == turn);
        Assert.Equal(RunMode.Plan, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).Mode);
    }

    [Fact]
    public void An_unfinished_model_step_still_defers_policy_transition()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var turn = TurnId.New();
        var lane = Assert.IsType<LaneId>(fixture.Server.LastLaneId());
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run, LaneId: lane, TurnId: turn)))
        {
            var stream = new EventStream(fixture.Store, fixture.Codecs, fixture.Session);
            stream.Append(new TurnStarted(turn, lane), DurabilityClass.Barrier);
            stream.Append(
                new ModelStepStarted(turn, 0, "private-fixture-model", 1, "none", null, null, null),
                DurabilityClass.Barrier);
        }
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var before = fixture.Store.CurrentSequence(fixture.Session);

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "model request is still open", authority.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
        Assert.Equal("ModelStepActive", result.Outcome?.Reason);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
    }

    [Fact]
    public void Closing_one_exact_step_does_not_close_another_open_step()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var turn = TurnId.New();
        var lane = Assert.IsType<LaneId>(fixture.Server.LastLaneId());
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run, LaneId: lane, TurnId: turn)))
        {
            var stream = new EventStream(fixture.Store, fixture.Codecs, fixture.Session);
            stream.Append(new TurnStarted(turn, lane), DurabilityClass.Barrier);
            stream.Append(new ModelStepStarted(turn, 0, "private-fixture-model", 1,
                "none", null, null, null), DurabilityClass.Barrier);
            stream.Append(new ModelStepNotDispatched(turn, 0), DurabilityClass.Barrier);
            stream.Append(
                new ModelStepStarted(turn, 1, "private-fixture-model", 1, "none", null, null, null),
                DurabilityClass.Barrier);
        }
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var before = fixture.Store.CurrentSequence(fixture.Session);

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "the second model step remains open", authority.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
        Assert.Equal("ModelStepActive", result.Outcome?.Reason);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        Assert.Contains(fixture.Store.ReadFrom(fixture.Session, 1), item =>
            fixture.Codecs.Decode(item) is ModelStepNotDispatched { TurnId: var id, StepIndex: 0 } && id == turn);
    }

    [Fact]
    public void An_unfinished_tool_call_defers_policy_transition_without_writes()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var call = ToolCallId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run, ToolCallId: call)))
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(
                new ToolCallRequested(call, "fixture-provider-call", "fixture.read", "{}"),
                DurabilityClass.Barrier);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var before = fixture.Store.CurrentSequence(fixture.Session);

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "tool lifecycle is still open", authority.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
        Assert.Equal("ToolCallActive", result.Outcome?.Reason);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
    }

    [Theory]
    [InlineData("objective-revision")]
    [InlineData("policy-revision")]
    [InlineData("authorization-id")]
    [InlineData("allowed-modes")]
    [InlineData("limits")]
    public void Forged_policy_transition_metadata_is_rejected_before_append_and_reopen(string field)
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var priorAuthorization = Assert.IsType<ModeSwitchAuthorization>(authority.Authorization);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var command = CommandId.New();
        var revision = checked(authority.Revision + 1);
        var objectiveRevision = authority.ObjectiveRevision;
        var objectiveDigest = authority.ObjectiveDigest;
        var policyRevision = authority.PolicyRevision;
        var authorizationId = priorAuthorization.AuthorizationId;
        var allowedModes = priorAuthorization.AllowedModes.ToArray();
        var limits = priorAuthorization.Limits;

        switch (field)
        {
            case "objective-revision": objectiveRevision++; break;
            case "policy-revision": policyRevision++; break;
            case "authorization-id": authorizationId = Guid.NewGuid(); break;
            case "allowed-modes": allowedModes = [RunMode.Plan]; break;
            case "limits": limits = limits with { MaxSpendUsd = limits.MaxSpendUsd + 0.01m }; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }

        var reason = "forged authority metadata";
        var authorization = priorAuthorization with
        {
            AuthorizationId = authorizationId,
            AuthorityRevision = revision,
            ObjectiveRevision = objectiveRevision,
            ObjectiveDigest = objectiveDigest,
            PolicyRevision = policyRevision,
            AllowedModes = allowedModes,
            Limits = limits,
        };
        var selected = authority with
        {
            Revision = revision,
            Mode = RunMode.Plan,
            ObjectiveRevision = objectiveRevision,
            ObjectiveDigest = objectiveDigest,
            PolicyRevision = policyRevision,
            Authorization = authorization,
        };
        var payloads = new DomainEventPayload[]
        {
            new RunModeChanged(fixture.Run, authority.Mode, RunMode.Plan, reason),
            new RunModeTransitionAuthorized(fixture.Run, authority.Mode, RunMode.Plan, reason,
                "UltraCodePolicy", command.ToString(), revision, objectiveRevision, objectiveDigest,
                policyRevision, authorizationId),
            new RunModeAuthoritySelected(selected, command.ToString(), "UltraCodePolicy"),
        };

        using (CausationScope.Begin(new CommandCausation(command)))
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
            Assert.Throws<InvalidStateTransitionException>(() => new EventStream(fixture.Store, fixture.Codecs,
                fixture.Session).AppendBatch(payloads, DurabilityClass.Barrier));

        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        fixture.CloseStore();
        fixture.ReopenStore();
        var replay = RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1));
        AssertAuthorityEqual(authority, replay.ModeAuthority);
    }

    [Fact]
    public void Policy_triplet_with_mixed_command_causes_is_rejected_before_any_batch_row()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var authorization = Assert.IsType<ModeSwitchAuthorization>(authority.Authorization);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var command = CommandId.New();
        var foreignCommand = CommandId.New();
        var nextRevision = checked(authority.Revision + 1);
        var selected = authority with
        {
            Revision = nextRevision,
            Mode = RunMode.Plan,
            Authorization = authorization with { AuthorityRevision = nextRevision },
        };
        var reason = "causal scope fixture";
        var payloads = new DomainEventPayload[]
        {
            new RunModeChanged(fixture.Run, authority.Mode, RunMode.Plan, reason),
            new RunModeTransitionAuthorized(fixture.Run, authority.Mode, RunMode.Plan, reason,
                "UltraCodePolicy", command.ToString(), nextRevision, authority.ObjectiveRevision,
                authority.ObjectiveDigest, authority.PolicyRevision, authorization.AuthorizationId),
            new RunModeAuthoritySelected(selected, command.ToString(), "UltraCodePolicy"),
        };
        CausationId?[] causations =
        [new CommandCausation(command), new CommandCausation(command), new CommandCausation(foreignCommand)];

        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
            Assert.Throws<InvalidStateTransitionException>(() => new EventStream(fixture.Store, fixture.Codecs,
                fixture.Session).AppendBatch(payloads, DurabilityClass.Barrier, null, causations));

        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        AssertAuthorityEqual(authority, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).ModeAuthority);
    }

    [Fact]
    public void Textual_reason_is_redacted_before_the_transition_is_durable()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "review Authorization: Bearer synthetic-secret-token-123 before lowering mode", authority.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Outcome?.Kind);
        var changed = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1)
            .Select(fixture.Codecs.Decode).OfType<RunModeChanged>());
        Assert.DoesNotContain("synthetic-secret-token-123", changed.Cause, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", changed.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Expired_durable_authority_is_rejected_without_appending_a_transition()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 1);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(1200),
            TestContext.Current.CancellationToken);

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Plan, "elapsed allowance", authority.Revision, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, result.Outcome?.Kind);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        AssertAuthorityEqual(authority, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).ModeAuthority);
    }

    [Fact]
    public void Explicit_user_grant_captures_the_current_plan_identity_revision_and_root_task()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var events = fixture.Store.ReadFrom(fixture.Session, 1);
        var plan = Assert.IsType<Plan>(PlanProjection.Replay(fixture.Codecs, events).Latest());
        var rootTask = Assert.IsType<TaskId>(RunProjection.Replay(fixture.Session, fixture.Run,
            fixture.Codecs, events).RootTask);
        var before = fixture.Store.CurrentSequence(fixture.Session);

        var grant = SelectAdaptivePlan(fixture, coverCurrentPlan: "true");

        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, grant.Outcome?.Kind);
        // Replacing the existing ACT grant revokes it before writing the new authority batch.
        Assert.Equal(before + 4, fixture.Store.CurrentSequence(fixture.Session));
        var authority = Assert.IsType<RunModeAuthority>(RunProjection.Replay(fixture.Session, fixture.Run,
            fixture.Codecs, fixture.Store.ReadFrom(fixture.Session, 1)).ModeAuthority);
        Assert.Equal(RunMode.Plan, authority.Mode);
        Assert.Equal(new ModeSwitchPlanCoverage(fixture.Run, plan.Id, plan.Revision, rootTask),
            authority.Authorization?.PlanCoverage);
    }

    [Fact]
    public void Legacy_adaptive_grant_without_plan_coverage_cannot_automatically_authorize_plan_to_act()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var selected = SelectAdaptivePlan(fixture, coverCurrentPlan: null);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        Assert.Equal(RunMode.Plan, authority.Mode);
        Assert.Null(authority.Authorization?.PlanCoverage);

        var before = fixture.Store.CurrentSequence(fixture.Session);
        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Act, "review the authorized plan", authority.Revision, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
        Assert.Equal("PlanCoverageUnavailable", result.Outcome?.Reason);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        Assert.Equal(RunMode.Plan, fixture.Server.CurrentRunMode());
        Assert.Null(Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority())
            .Authorization?.PlanCoverage);
    }

    [Fact]
    public void Exact_current_plan_coverage_allows_PLAN_to_ACT_and_is_recorded_on_the_transition()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var plan = Assert.IsType<Plan>(PlanProjection.Replay(fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).Latest());
        var rootTask = Assert.IsType<TaskId>(RunProjection.Replay(fixture.Session, fixture.Run,
            fixture.Codecs, fixture.Store.ReadFrom(fixture.Session, 1)).RootTask);
        var selected = SelectAdaptivePlan(fixture, coverCurrentPlan: "true");
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var expectedCoverage = new ModeSwitchPlanCoverage(fixture.Run, plan.Id, plan.Revision, rootTask);
        Assert.Equal(expectedCoverage, authority.Authorization?.PlanCoverage);

        var before = fixture.Store.CurrentSequence(fixture.Session);
        var transition = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Act, "review the authorized plan", authority.Revision, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, transition.Outcome?.Kind);
        Assert.Null(transition.Failure);
        Assert.Equal(before + 3, fixture.Store.CurrentSequence(fixture.Session));
        var authorized = Assert.Single(fixture.Store.ReadFrom(fixture.Session, before + 1)
            .Select(fixture.Codecs.Decode).OfType<RunModeTransitionAuthorized>());
        Assert.Equal(RunMode.Plan, authorized.From);
        Assert.Equal(RunMode.Act, authorized.To);
        Assert.Equal(expectedCoverage, authorized.PlanCoverage);
        var replay = RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1));
        Assert.Equal(RunMode.Act, replay.Mode);
        Assert.Equal(expectedCoverage, replay.ModeAuthority?.Authorization?.PlanCoverage);
    }

    [Fact]
    public void A_plan_revision_change_after_grant_defers_PLAN_to_ACT_without_writes()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var plan = Assert.IsType<Plan>(PlanProjection.Replay(fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).Latest());
        var selected = SelectAdaptivePlan(fixture, coverCurrentPlan: "true");
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        Assert.Equal(plan.Revision, authority.Authorization?.PlanCoverage?.PlanRevision);

        new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(new PlanRevised(
            plan.Id, fixture.Run, checked(plan.Revision + 1), "[]", MutationImpact.Minor));
        var revisionAfterEdit = PlanProjection.Replay(fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).Revision();
        Assert.Equal(plan.Revision + 1, revisionAfterEdit);
        var before = fixture.Store.CurrentSequence(fixture.Session);

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Act, "review the authorized plan", authority.Revision, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
        Assert.Equal("PlanCoverageStale", result.Outcome?.Reason);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        var replay = RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1));
        Assert.Equal(RunMode.Plan, replay.Mode);
        Assert.Equal(plan.Revision, replay.ModeAuthority?.Authorization?.PlanCoverage?.PlanRevision);
        Assert.Equal(revisionAfterEdit, PlanProjection.Replay(fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).Revision());
    }

    [Fact]
    public void Policy_transition_cannot_rebind_the_granted_plan_coverage()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var selectedGrant = SelectAdaptivePlan(fixture, coverCurrentPlan: "true");
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selectedGrant.Outcome?.Kind);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var authorization = Assert.IsType<ModeSwitchAuthorization>(authority.Authorization);
        var coverage = Assert.IsType<ModeSwitchPlanCoverage>(authorization.PlanCoverage);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var command = CommandId.New();
        var revision = checked(authority.Revision + 1);
        var forgedAuthorization = authorization with
        {
            AuthorityRevision = revision,
            PlanCoverage = coverage with { PlanRevision = checked(coverage.PlanRevision + 1) },
        };
        var forgedAuthority = authority with
        {
            Revision = revision,
            Mode = RunMode.Act,
            Authorization = forgedAuthorization,
        };
        var reason = "attempted plan coverage rebind";
        var payloads = new DomainEventPayload[]
        {
            new RunModeChanged(fixture.Run, RunMode.Plan, RunMode.Act, reason),
            new RunModeTransitionAuthorized(fixture.Run, RunMode.Plan, RunMode.Act, reason,
                "UltraCodePolicy", command.ToString(), revision, authority.ObjectiveRevision,
                authority.ObjectiveDigest, authority.PolicyRevision, authorization.AuthorizationId,
                null, coverage),
            new RunModeAuthoritySelected(forgedAuthority, command.ToString(), "UltraCodePolicy"),
        };

        using (CausationScope.Begin(new CommandCausation(command)))
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
            Assert.Throws<InvalidStateTransitionException>(() => new EventStream(fixture.Store, fixture.Codecs,
                fixture.Session).AppendBatch(payloads, DurabilityClass.Barrier));

        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        AssertAuthorityEqual(authority, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).ModeAuthority);
    }

    [Fact]
    public void Pending_interaction_defers_covered_PLAN_to_ACT_without_writing_a_transition()
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var selected = SelectAdaptivePlan(fixture, coverCurrentPlan: "true");
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        Assert.NotNull(authority.Authorization?.PlanCoverage);
        var runProjection = RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1));
        var rootTask = Assert.IsType<TaskId>(runProjection.RootTask);
        var lane = Assert.IsType<LaneId>(fixture.Server.LastLaneId());
        var interaction = new InteractionRequested(InteractionId.New(), InteractionKind.Permission,
            "{}", "[{\"id\":\"deny\"},{\"id\":\"allow\"}]", "deny", null,
            lane, rootTask, null, 0, 1);
        using (ExecutionScope.Begin(new ExecutionScopeState(fixture.Run, rootTask, lane)))
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session).AppendBatch(
                new DomainEventPayload[] { interaction, new RunAwaitingInput(fixture.Run, lane) },
                DurabilityClass.Barrier);
        var before = fixture.Store.CurrentSequence(fixture.Session);

        var result = fixture.Server.ApplyUltraCodePolicyTransition(fixture.Session, fixture.Run,
            RunMode.Act, "review the authorized plan", authority.Revision, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
        Assert.Equal("PendingInteraction", result.Outcome?.Reason);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        Assert.Equal(RunMode.Plan, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).Mode);
        Assert.DoesNotContain(fixture.Store.ReadFrom(fixture.Session, before + 1), item =>
            fixture.Codecs.Decode(item) is RunModeChanged or RunModeTransitionAuthorized or RunModeAuthoritySelected);
    }

    [Theory]
    [InlineData("not-a-boolean", true)]
    [InlineData("true", false)]
    public void Plan_coverage_requires_a_valid_value_and_an_explicit_adaptive_grant(
        string coverCurrentPlan, bool adaptive)
    {
        using var fixture = OpenGrantedFixture(maxElapsedSeconds: 90);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        var authority = Assert.IsType<RunModeAuthority>(fixture.Server.CurrentModeAuthority());
        var result = SelectAdaptivePlan(fixture, coverCurrentPlan, adaptive);

        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, result.Outcome?.Kind);
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        AssertAuthorityEqual(authority, RunProjection.Replay(fixture.Session, fixture.Run, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).ModeAuthority);
    }

    private static CommandAck SelectAdaptivePlan(Fixture fixture, string? coverCurrentPlan,
        bool adaptive = true)
    {
        var fields = new List<string>
        {
            JsonObj.Field("cmd", "run.mode.select"),
            JsonObj.Field("mode", "plan"),
            JsonObj.Field("effort", "ultracode"),
            JsonObj.FieldBool("adaptive", adaptive),
            JsonObj.Field("allowedModes", "plan,act,orq"),
            JsonObj.FieldRaw("maxAgents", "1"),
            JsonObj.FieldRaw("maxDepth", "1"),
            JsonObj.FieldRaw("maxTurns", "3"),
            JsonObj.FieldRaw("maxToolCalls", "8"),
            JsonObj.FieldRaw("maxElapsedSeconds", "90"),
            JsonObj.FieldRaw("maxSpendUsd", "2.50"),
        };
        if (coverCurrentPlan is not null)
            fields.Add(JsonObj.Field("coverCurrentPlan", coverCurrentPlan));
        return fixture.Server.SendUserAction(Command(Payload(fields.ToArray())), TestContext.Current.CancellationToken);
    }

    private static Fixture OpenGrantedFixture(int maxElapsedSeconds, bool failAfterNextBatchCommit = false,
        string allowedModes = "plan,act,orq")
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-ultracode-transition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journal);
        var faultStore = failAfterNextBatchCommit ? new ThrowAfterCommitStore(store) : null;
        IEventStore writeStore = faultStore is null ? store : faultStore;
        try
        {
            var server = new OmniServer(writeStore, codecs, new InMemoryAuditSink());
            var start = server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
                JsonObj.Field("text", "UltraCode transition fixture"))), TestContext.Current.CancellationToken);
            Assert.Equal("ok", start.Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var grant = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
                JsonObj.Field("mode", "act"), JsonObj.Field("effort", "ultracode"),
                JsonObj.FieldBool("adaptive", true), JsonObj.Field("allowedModes", allowedModes),
                JsonObj.FieldRaw("maxAgents", "1"), JsonObj.FieldRaw("maxDepth", "1"),
                JsonObj.FieldRaw("maxTurns", "3"), JsonObj.FieldRaw("maxToolCalls", "8"),
                JsonObj.FieldRaw("maxElapsedSeconds", maxElapsedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                JsonObj.FieldRaw("maxSpendUsd", "2.50"))), TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, grant.Outcome?.Kind);
            return new Fixture(root, journal, store, writeStore, faultStore, codecs, server, session, run);
        }
        catch
        {
            CloseStore(journal, store);
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static void AssertAuthorityEqual(RunModeAuthority expected, RunModeAuthority? actual)
    {
        var actualValue = Assert.IsType<RunModeAuthority>(actual);
        Assert.Equal(expected.RunId, actualValue.RunId);
        Assert.Equal(expected.Revision, actualValue.Revision);
        Assert.Equal(expected.Mode, actualValue.Mode);
        Assert.Equal(expected.Strategy, actualValue.Strategy);
        Assert.Equal(expected.ProductEffort, actualValue.ProductEffort);
        Assert.Equal(expected.ModePinned, actualValue.ModePinned);
        Assert.Equal(expected.AutoModeSwitch, actualValue.AutoModeSwitch);
        Assert.Equal(expected.ObjectiveRevision, actualValue.ObjectiveRevision);
        Assert.Equal(expected.ObjectiveDigest, actualValue.ObjectiveDigest);
        Assert.Equal(expected.PolicyRevision, actualValue.PolicyRevision);
        if (expected.Authorization is null)
        {
            Assert.Null(actualValue.Authorization);
            return;
        }

        var actualAuthorization = Assert.IsType<ModeSwitchAuthorization>(actualValue.Authorization);
        Assert.Equal(expected.Authorization.AuthorizationId, actualAuthorization.AuthorizationId);
        Assert.Equal(expected.Authorization.AuthorityRevision, actualAuthorization.AuthorityRevision);
        Assert.Equal(expected.Authorization.ObjectiveRevision, actualAuthorization.ObjectiveRevision);
        Assert.Equal(expected.Authorization.ObjectiveDigest, actualAuthorization.ObjectiveDigest);
        Assert.Equal(expected.Authorization.PolicyRevision, actualAuthorization.PolicyRevision);
        Assert.Equal(expected.Authorization.Limits, actualAuthorization.Limits);
        Assert.Equal(expected.Authorization.GrantedAtUtc, actualAuthorization.GrantedAtUtc);
        Assert.True(expected.Authorization.AllowedModes.SequenceEqual(actualAuthorization.AllowedModes));
    }

    private static DomainEvent RawEvent(Fixture fixture, DomainEventPayload payload, CommandId cause)
    {
        var type = payload.Type();
        var json = fixture.Codecs.CodecFor(type).Encode(payload);
        return DomainEvent.Create(fixture.Session, type, fixture.Codecs.CurrentVersion(type),
            new CommandCausation(cause), fixture.Run, fixture.Run, null, null, null, null, null,
            Array.Empty<ArtifactRef>(), json);
    }

    private sealed class Fixture(string root, string journal, SqliteEventStore store,
        IEventStore writeStore, ThrowAfterCommitStore? faultStore,
        OmniCore.Abstractions.IEventCodecRegistry codecs, OmniServer server, SessionId session, RunId run) : IDisposable
    {
        public string Journal { get; } = journal;
        public SqliteEventStore SqliteStore { get; private set; } = store;
        public IEventStore Store { get; private set; } = writeStore;
        public ThrowAfterCommitStore? FaultStore { get; } = faultStore;
        public OmniCore.Abstractions.IEventCodecRegistry Codecs { get; } = codecs;
        public OmniServer Server { get; } = server;
        public SessionId Session { get; } = session;
        public RunId Run { get; } = run;

        public void CloseStore()
        {
            UltraCodePolicyTransitionIntegrationTests.CloseStore(Journal, SqliteStore);
        }

        public void ReopenStore()
        {
            SqliteStore = new SqliteEventStore(Journal);
            Store = SqliteStore;
        }

        public void Dispose()
        {
            CloseStore();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CloseStore(string journal, SqliteEventStore store)
    {
        var connection = (SqliteConnection)store.Connection;
        store.Close();
        SqliteConnection.ClearPool(connection);
        connection.Dispose();
    }

    private sealed class ThrowAfterCommitStore(SqliteEventStore inner) : IEventStore
    {
        public bool ThrowAfterNextBatchCommit { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => inner.Append(sessionId, evt, durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            inner.AppendBatch(sessionId, evts, durability, cancellationToken);
            if (ThrowAfterNextBatchCommit)
            {
                ThrowAfterNextBatchCommit = false;
                throw new IOException("fixture failed after SQLite commit");
            }
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            inner.ReadFrom(sessionId, fromSequenceInclusive);
    }
}
