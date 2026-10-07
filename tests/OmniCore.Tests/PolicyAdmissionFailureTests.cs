using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Faults at internal-command admission/checkpoint boundaries, using private SQLite only.</summary>
public sealed class PolicyAdmissionFailureTests
{
    [Theory]
    [InlineData("sequence")]
    [InlineData("read-first")]
    public void Explorer_admission_failure_before_checkpoint_is_deferred_without_callback_or_scope_leak(string fault)
    {
        using var fixture = Fixture.Open();
        var session = fixture.Session;
        var run = fixture.Run;
        var before = fixture.Inner.CurrentSequence(session);
        fixture.Store.FailNext = fault;
        var calls = 0;
        var parent = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());

        using (CausationScope.Begin(parent))
        using (ExecutionScope.Begin(parentExecution))
        {
            var result = fixture.Server.ExecuteExplorerTurn(session, run, _ =>
            {
                calls++;
                throw new InvalidOperationException("must not execute");
            }, CancellationToken.None);

            Assert.Null(result.Result);
            Assert.IsType<IOException>(result.Failure);
            Assert.Equal("error", result.Ack.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Ack.Outcome?.Kind);
            Assert.Null(result.Ack.FirstSeq);
            Assert.Null(result.Ack.LastSeq);
            Assert.Equal(parent, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
        }

        Assert.Equal(0, calls);
        Assert.Equal(session, fixture.Server.LastSessionId());
        Assert.Equal(run, fixture.Server.LastRunId());
        Assert.Equal(before, fixture.Inner.CurrentSequence(session));
        Assert.Empty(fixture.Inner.ReadFrom(session, before + 1));
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("read-first")]
    public void Routing_policy_failure_before_checkpoint_or_during_initial_read_does_not_write(string fault)
    {
        using var fixture = Fixture.OpenWithoutRoutingPolicy();
        var before = fixture.Inner.CurrentSequence(fixture.Session);
        fixture.Store.FailNext = fault;
        var parent = new EventCausation(new EventId(Guid.NewGuid()));

        InternalCommandResult result;
        using (CausationScope.Begin(parent))
        {
            result = fixture.Server.EnsureSessionRoutingPolicy(fixture.Session);
            Assert.Equal(parent, CausationScope.Current);
        }

        Assert.IsType<IOException>(result.Failure);
        Assert.Equal(fault == "sequence" ? RuntimeCommandOutcomeKind.Deferred : RuntimeCommandOutcomeKind.Rejected,
            result.Outcome?.Kind);
        Assert.Null(result.FirstSeq);
        Assert.Null(result.LastSeq);
        Assert.Null(CausationScope.Current);
        Assert.Equal(before, fixture.Inner.CurrentSequence(fixture.Session));
        Assert.Empty(fixture.Inner.ReadFrom(fixture.Session, before + 1));
    }

    [Fact]
    public void Routing_policy_append_then_throw_reports_confirmed_prefix_and_retry_is_noop()
    {
        using var fixture = Fixture.OpenWithoutRoutingPolicy();
        var before = fixture.Inner.CurrentSequence(fixture.Session);
        fixture.Store.ThrowAfterNextWrite = true;
        var parent = new EventCausation(new EventId(Guid.NewGuid()));

        InternalCommandResult result;
        using (CausationScope.Begin(parent))
        {
            result = fixture.Server.EnsureSessionRoutingPolicy(fixture.Session);
            Assert.Equal(parent, CausationScope.Current);
        }

        Assert.IsType<IOException>(result.Failure);
        Assert.Equal("error", result.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Outcome?.Kind);
        Assert.Null(CausationScope.Current);
        var row = Assert.Single(fixture.Inner.ReadFrom(fixture.Session, before + 1));
        Assert.IsType<SessionRoutingPolicySet>(fixture.Codecs.Decode(row));
        Assert.Equal(row.Sequence, result.FirstSeq);
        Assert.Equal(row.Sequence, result.LastSeq);
        Assert.Equal(result.CommandId,
            Assert.IsType<CommandCausation>(row.Causation).CommandId.Value.ToString());

        var end = fixture.Inner.CurrentSequence(fixture.Session);
        var retry = fixture.Server.EnsureSessionRoutingPolicy(fixture.Session);
        Assert.Null(retry.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, retry.Outcome?.Kind);
        Assert.Equal(end, fixture.Inner.CurrentSequence(fixture.Session));
    }

    [Fact]
    public void Routing_policy_append_with_unreadable_confirmation_is_deferred_but_retry_observes_it()
    {
        using var fixture = Fixture.OpenWithoutRoutingPolicy();
        fixture.Store.ThrowAfterNextWrite = true;
        fixture.Store.FailConfirmationRead = true;

        var first = fixture.Server.EnsureSessionRoutingPolicy(fixture.Session);

        Assert.IsType<IOException>(first.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, first.Outcome?.Kind);
        Assert.Null(first.FirstSeq);
        Assert.Null(first.LastSeq);
        var policy = Assert.Single(fixture.Inner.ReadFrom(fixture.Session, 1)
            .Select(fixture.Codecs.Decode).OfType<SessionRoutingPolicySet>());
        Assert.Equal(fixture.Session, policy.SessionId);

        var end = fixture.Inner.CurrentSequence(fixture.Session);
        var retry = fixture.Server.EnsureSessionRoutingPolicy(fixture.Session);
        Assert.Null(retry.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, retry.Outcome?.Kind);
        Assert.Equal(end, fixture.Inner.CurrentSequence(fixture.Session));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("read-first")]
    public void Follow_up_checkpoint_failure_is_deferred_and_not_queued(string fault)
    {
        using var fixture = Fixture.Open();
        // LastLaneId performs its own journal read; resolve input before arming the
        // admission fault so the failure is exercised by the command, not the fixture.
        var lane = fixture.Lane;
        var turn = TurnId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(fixture.Run, fixture.Task, fixture.Lane, turn)))
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session)
                .Append(new TurnStarted(turn, fixture.Lane), DurabilityClass.Standard);
        var before = fixture.Inner.CurrentSequence(fixture.Session);
        fixture.Store.FailNext = fault;

        var result = fixture.Server.QueueFollowUpPromptCommand(fixture.Session, fixture.Run,
            lane, "retry after admission", null);

        Assert.False(result.Queued);
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Ack.Outcome?.Kind);
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);
        Assert.Equal(before, fixture.Inner.CurrentSequence(fixture.Session));
        Assert.Empty(FollowUpQueue.Pending(fixture.Inner, fixture.Codecs, fixture.Session, fixture.Run, lane));
    }

    [Fact]
    public void Follow_up_append_then_throw_reports_durable_prefix_without_claiming_queue_success()
    {
        using var fixture = Fixture.Open();
        var turn = TurnId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(fixture.Run, fixture.Task, fixture.Lane, turn)))
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session)
                .Append(new TurnStarted(turn, fixture.Lane), DurabilityClass.Standard);
        var before = fixture.Inner.CurrentSequence(fixture.Session);
        fixture.Store.ThrowAfterNextWrite = true;

        var result = fixture.Server.QueueFollowUpPromptCommand(fixture.Session, fixture.Run,
            fixture.Lane, "durable queue", null);

        Assert.False(result.Queued);
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal("error", result.Ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
        var row = Assert.Single(fixture.Inner.ReadFrom(fixture.Session, before + 1));
        Assert.IsType<FollowUpQueued>(fixture.Codecs.Decode(row));
        Assert.Equal(row.Sequence, result.Ack.FirstSeq);
        Assert.Equal(row.Sequence, result.Ack.LastSeq);
        Assert.Equal(result.Ack.CommandId,
            Assert.IsType<CommandCausation>(row.Causation).CommandId.Value.ToString());
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("read-first")]
    public void Escalation_checkpoint_failure_is_deferred_without_event_or_scope_leak(string fault)
    {
        using var fixture = Fixture.Open();
        var before = fixture.Inner.CurrentSequence(fixture.Session);
        fixture.Store.FailNext = fault;
        var parentCause = new EventCausation(new EventId(Guid.NewGuid()));
        var parentScope = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());

        using (CausationScope.Begin(parentCause))
        using (ExecutionScope.Begin(parentScope))
        {
            var result = fixture.Server.RecordModelEscalationRequested(fixture.Session,
                new ModelEscalationRequested(fixture.Run, "source", "target", EscalationCause.ManualRequest));
            Assert.IsType<IOException>(result.Failure);
            Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
            Assert.Null(result.FirstSeq);
            Assert.Null(result.LastSeq);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentScope, ExecutionScope.Current);
        }

        Assert.Equal(before, fixture.Inner.CurrentSequence(fixture.Session));
        Assert.Empty(fixture.Inner.ReadFrom(fixture.Session, before + 1));
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Escalation_append_then_throw_reports_durable_prefix_and_confirmation_failure_is_deferred()
    {
        using var fixture = Fixture.Open();
        var before = fixture.Inner.CurrentSequence(fixture.Session);
        fixture.Store.ThrowAfterNextWrite = true;
        var result = fixture.Server.RecordModelEscalationApproved(fixture.Session,
            new ModelEscalationApproved(fixture.Run, "target", "policy:fixture"));
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Outcome?.Kind);
        var durable = Assert.Single(fixture.Inner.ReadFrom(fixture.Session, before + 1));
        Assert.IsType<ModelEscalationApproved>(fixture.Codecs.Decode(durable));
        Assert.Equal(durable.Sequence, result.FirstSeq);
        Assert.Equal(durable.Sequence, result.LastSeq);

        var secondBefore = fixture.Inner.CurrentSequence(fixture.Session);
        fixture.Store.ThrowAfterNextWrite = true;
        fixture.Store.FailConfirmationRead = true;
        var uncertain = fixture.Server.RecordModelEscalationCompleted(fixture.Session,
            new ModelEscalationCompleted(fixture.Run, "target"));
        Assert.IsType<IOException>(uncertain.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, uncertain.Outcome?.Kind);
        Assert.Null(uncertain.FirstSeq);
        Assert.Null(uncertain.LastSeq);
        Assert.Single(fixture.Inner.ReadFrom(fixture.Session, secondBefore + 1));
    }

    [Fact]
    public void Cancellation_from_follow_up_and_escalation_writes_keeps_original_token_and_scopes()
    {
        using var fixture = Fixture.Open();
        var turn = TurnId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(fixture.Run, fixture.Task, fixture.Lane, turn)))
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session)
                .Append(new TurnStarted(turn, fixture.Lane), DurabilityClass.Standard);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException("fixture cancelled", cancellation.Token);
        var parentCause = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());

        using (CausationScope.Begin(parentCause))
        using (ExecutionScope.Begin(parentExecution))
        {
            fixture.Store.ThrowBeforeNextWrite = original;
            var followup = fixture.Server.QueueFollowUpPromptCommand(fixture.Session, fixture.Run,
                fixture.Lane, "cancelled follow-up", null);
            Assert.Same(original, followup.Failure);
            Assert.False(followup.Queued);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, followup.Ack.Outcome?.Kind);
            Assert.Null(followup.Ack.FirstSeq);
            Assert.Null(followup.Ack.LastSeq);
            Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(followup.Failure).CancellationToken);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);

            fixture.Store.ThrowBeforeNextWrite = original;
            var escalation = fixture.Server.RecordModelEscalationApproved(fixture.Session,
                new ModelEscalationApproved(fixture.Run, "target", "policy:cancel"));
            Assert.Same(original, escalation.Failure);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, escalation.Outcome?.Kind);
            Assert.Null(escalation.FirstSeq);
            Assert.Null(escalation.LastSeq);
            Assert.Same(original, Assert.Throws<OperationCanceledException>(() => escalation.ThrowIfFailure()));
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Routing_cli_propagates_policy_failure_before_authorization()
    {
        using var fixture = Fixture.Open();
        fixture.Store.FailNext = "sequence";
        var runtime = OmniCliRuntime.Create(fixture.Root);
        var route = ModelRoute.DefaultForModel("local-model", "local", "http://127.0.0.1:1/v1",
            ProviderFamily.OpenAiChatCompatible);

        var thrown = Assert.Throws<IOException>(() => runtime.AuthorizeRouteForInvocation(fixture.Server,
            fixture.Session, fixture.Run, route, BillingMode.Local, _ => { }, "en"));

        Assert.Equal("synthetic sequence failure", thrown.Message);
        Assert.Equal(fixture.Session, fixture.Server.LastSessionId());
        Assert.Equal(fixture.Run, fixture.Server.LastRunId());
        Assert.DoesNotContain(fixture.Inner.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode),
            payload => payload is InteractionRequested request && request.Kind == InteractionKind.ModelRouteConsent);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _database;
        private readonly string _stateFile;
        private SqliteEventStore _sqlite;
        public string Root { get; }
        public FaultStore Store { get; private set; }
        public IEventCodecRegistry Codecs { get; } = EventCodecs.Create();
        public OmniServer Server { get; private set; }
        public SessionId Session => Assert.IsType<SessionId>(Server.LastSessionId());
        public RunId Run => Assert.IsType<RunId>(Server.LastRunId());
        public LaneId Lane => Assert.IsType<LaneId>(Server.LastLaneId());
        public TaskId Task => new(RunProjection.Replay(Session, Run, Codecs, Inner.ReadFrom(Session, 1)).RootTask!.Value);
        public IEventStore Inner => _sqlite;

        private Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "omni-policy-command-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _database = Path.Combine(Root, "journal.db");
            _stateFile = Path.Combine(Root, "last-session.txt");
            _sqlite = new SqliteEventStore(_database);
            Store = new FaultStore(_sqlite);
            Server = NewServer();
            Assert.Equal("ok", Server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"policy command fixture\"}"), CancellationToken.None).Status);
        }

        public static Fixture Open() => new();

        public static Fixture OpenWithoutRoutingPolicy()
        {
            var fixture = new Fixture();
            var session = fixture.Session;
            var routeEvent = Assert.Single(fixture.Inner.ReadFrom(session, 1),
                evt => fixture.Codecs.Decode(evt) is SessionRoutingPolicySet);
            fixture.Store.CloseAndPool();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fixture._database,
                Pooling = false,
            }.ToString()))
            {
                connection.Open();
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM events WHERE session_id = $session AND seq = $seq";
                delete.Parameters.AddWithValue("$session", session.ToString());
                delete.Parameters.AddWithValue("$seq", routeEvent.Sequence);
                Assert.Equal(1, delete.ExecuteNonQuery());
            }
            fixture._sqlite = new SqliteEventStore(fixture._database);
            fixture.Store = new FaultStore(fixture._sqlite);
            fixture.Server = fixture.NewServer();
            Assert.Equal(session, fixture.Server.LastSessionId());
            Assert.DoesNotContain(fixture.Inner.ReadFrom(session, 1),
                evt => fixture.Codecs.Decode(evt) is SessionRoutingPolicySet);
            return fixture;
        }

        private OmniServer NewServer() => new(Store, Codecs, new InMemoryAuditSink(), _stateFile,
            new FileArtifactStore(Path.Combine(Root, "artifacts")));

        public void Dispose()
        {
            Store.CloseAndPool();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FaultStore(IEventStore inner) : IEventStore
    {
        private bool _failConfirmationRead;
        public IEventStore Inner { get; } = inner;
        public string? FailNext { get; set; }
        public bool ThrowAfterNextWrite { get; set; }
        public bool FailConfirmationRead { get; set; }
        public Exception? ThrowBeforeNextWrite { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            FailBeforeWrite();
            Inner.Append(sessionId, evt, durability, cancellationToken);
            AfterWrite();
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            FailBeforeWrite();
            Inner.AppendBatch(sessionId, events, durability, cancellationToken);
            AfterWrite();
        }

        public long CurrentSequence(SessionId sessionId)
        {
            if (FailNext == "sequence")
            {
                FailNext = null;
                throw new IOException("synthetic sequence failure");
            }
            return Inner.CurrentSequence(sessionId);
        }

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive)
        {
            if (FailNext == "read-first" && fromSequenceInclusive == 1)
            {
                FailNext = null;
                throw new IOException("synthetic admission read failure");
            }
            if (_failConfirmationRead && fromSequenceInclusive > 1)
            {
                _failConfirmationRead = false;
                throw new IOException("synthetic outcome confirmation read failure");
            }
            return Inner.ReadFrom(sessionId, fromSequenceInclusive);
        }

        private void AfterWrite()
        {
            if (FailConfirmationRead)
            {
                FailConfirmationRead = false;
                _failConfirmationRead = true;
            }
            if (!ThrowAfterNextWrite) return;
            ThrowAfterNextWrite = false;
            throw new IOException("synthetic append committed before throw");
        }

        private void FailBeforeWrite()
        {
            if (ThrowBeforeNextWrite is not { } failure) return;
            ThrowBeforeNextWrite = null;
            throw failure;
        }

        public void CloseAndPool()
        {
            if (Inner is not SqliteEventStore sqlite) return;
            var connection = (SqliteConnection)sqlite.Connection;
            sqlite.Close();
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
        }
    }
}
