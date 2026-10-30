namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;

using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Memory;

public sealed class ScopedMemoryEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-memory-evidence-" + Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly MemoryScope Scope = MemoryScope.ForWorkspace(WorkspaceId.Of("evidence-fixture"));
    private const string Content = "Run focused checks before making edits.";
    private readonly Dictionary<SessionId, List<DomainEventPayload>> _journals = [];
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private ScopedMemoryPolicy Policy(SessionId session, List<DomainEventPayload> events, bool allowed = true)
    {
        _journals[session] = events;
        return new(session, new ScopedMemoryStore(_root), () => events.ToArray(), events.Add, _ => allowed,
            source => _journals.GetValueOrDefault(source)?.ToArray() ?? []);
    }

    [Fact]
    public void Three_distinct_human_sessions_promote_and_same_session_repetition_does_not()
    {
        var firstEvents = new List<DomainEventPayload>(); var first = Policy(SessionId.New(), firstEvents);
        for (var n = 0; n < 4; n++) Assert.Equal("ask", first.Propose(Scope, Content, MemoryKind.Fact, None).Decision);
        Assert.Empty(new ScopedMemoryStore(_root).Read(Scope, None));
        var secondEvents = new List<DomainEventPayload>();
        Assert.Equal("ask", Policy(SessionId.New(), secondEvents).Propose(Scope, Content, MemoryKind.Fact, None).Decision);
        var thirdEvents = new List<DomainEventPayload>();
        Assert.Equal("promoted", Policy(SessionId.New(), thirdEvents).Propose(Scope, Content, MemoryKind.Fact, None).Decision);
        Assert.DoesNotContain(thirdEvents, item => item is InteractionRequested);
        var record = Assert.Single(new ScopedMemoryStore(_root).Read(Scope, None));
        Assert.Equal(3, record.EvidenceRefs.Count);
        Assert.Single(thirdEvents.OfType<ScopedMemoryMutationCompleted>());
    }

    [Fact]
    public void Global_privacy_trust_and_conflict_do_not_bypass_Ask()
    {
        for (var n = 0; n < 4; n++)
        {
            List<DomainEventPayload> events = [];
            Assert.Equal("ask", Policy(SessionId.New(), events).Propose(MemoryScope.ForUser(), Content, MemoryKind.Fact, None).Decision);
            Assert.Equal("rejected", Policy(SessionId.New(), []).Propose(Scope, "Contact alice@example.com", MemoryKind.Fact, None).Decision);
        }
        Assert.Throws<InvalidOperationException>(() => Policy(SessionId.New(), [], false).Propose(Scope, Content, MemoryKind.Fact, None));
        Assert.Empty(new ScopedMemoryStore(_root).Read(MemoryScope.ForUser(), None));
        var old = new MemoryRecord("existing", Scope, MemoryKind.Fact, "Run focused checks after making edits.",
            .9, 1, MemorySource.UserStated, DateTimeOffset.UtcNow, null, MemoryExpirationKind.WithScope, [], [], null);
        new ScopedMemoryStore(_root).Add(old, None);
        for (var n = 0; n < 3; n++) Assert.Equal("ask", Policy(SessionId.New(), []).Propose(Scope, Content, MemoryKind.Fact, None).Decision);
        Assert.Single(new ScopedMemoryStore(_root).Read(Scope, None));
    }

    [Fact]
    public void Forget_tombstone_prevents_evidence_resurrection_and_prepared_replay()
    {
        for (var n = 0; n < 2; n++) Policy(SessionId.New(), []).Propose(Scope, Content, MemoryKind.Fact, None);
        List<DomainEventPayload> events = []; var session = SessionId.New(); var policy = Policy(session, events);
        policy.Propose(Scope, Content, MemoryKind.Fact, None);
        var prepared = Assert.Single(events.OfType<ScopedMemoryMutationPrepared>());
        Assert.True(policy.Forget(Scope, prepared.MemoryId, None));
        var store = new ScopedMemoryStore(_root);
        store.ApplyPrepared(prepared, None); // Existing receipt, never re-adds the record.
        Assert.Empty(store.Read(Scope, None));
        for (var n = 0; n < 4; n++) Assert.Equal("ask", Policy(SessionId.New(), []).Propose(Scope, Content, MemoryKind.Fact, None).Decision);
        Assert.Empty(store.Read(Scope, None));
    }

    [Fact]
    public void Evidence_promotion_recovers_after_database_commit_before_journal_receipt()
    {
        for (var n = 0; n < 2; n++) Policy(SessionId.New(), []).Propose(Scope, Content, MemoryKind.Fact, None);
        List<DomainEventPayload> events = []; var session = SessionId.New(); var fail = true;
        var policy = new ScopedMemoryPolicy(session, new ScopedMemoryStore(_root), () => events.ToArray(), item =>
        {
            if (fail && item is MemoryPromoted) throw new IOException("injected after database commit");
            events.Add(item);
        }, _ => true, source => _journals.GetValueOrDefault(source)?.ToArray() ?? []);
        Assert.Throws<IOException>(() => policy.Propose(Scope, Content, MemoryKind.Fact, None));
        Assert.Single(new ScopedMemoryStore(_root).Read(Scope, None));
        fail = false;
        Assert.Single(policy.Reconcile(None)); Assert.Empty(policy.Reconcile(None));
        Assert.Single(events.OfType<MemoryPromoted>());
        Assert.Single(events.OfType<ScopedMemoryMutationCompleted>());
    }

    [Fact]
    public void Crash_before_durable_proposal_does_not_cast_an_evidence_vote()
    {
        Policy(SessionId.New(), []).Propose(Scope, Content, MemoryKind.Fact, None);
        var crashed = new ScopedMemoryPolicy(SessionId.New(), new ScopedMemoryStore(_root), () => [],
            _ => throw new IOException("before durable proposal"), _ => true,
            source => _journals.GetValueOrDefault(source)?.ToArray() ?? []);
        Assert.Throws<IOException>(() => crashed.Propose(Scope, Content, MemoryKind.Fact, None));
        Assert.Equal("ask", Policy(SessionId.New(), []).Propose(Scope, Content, MemoryKind.Fact, None).Decision);
        Assert.Empty(new ScopedMemoryStore(_root).Read(Scope, None));
    }

    [Fact]
    public void Orphan_votes_and_durable_user_rejections_never_supply_automatic_authority()
    {
        // Even a vote already present in the database is only an index, not authority.
        var store = new ScopedMemoryStore(_root);
        store.ObserveUserEvidence(Scope, MemoryKind.Fact, Content, SessionId.New(), None);
        List<DomainEventPayload> rejected = [];
        Policy(SessionId.New(), rejected).Propose(Scope, Content, MemoryKind.Fact, None);
        var ask = Assert.Single(rejected.OfType<InteractionRequested>());
        // Cut after the durable decision but before MemoryRejected/store maintenance.
        rejected.Add(new InteractionResolved(ask.InteractionId, "reject", InteractionCause.User));
        Assert.Equal("ask", Policy(SessionId.New(), []).Propose(Scope, Content, MemoryKind.Fact, None).Decision);
        Assert.Empty(store.Read(Scope, None));
    }

    [Fact]
    public void Crash_after_evidence_decision_before_Prepared_requires_fresh_human_Ask()
    {
        for(var n=0;n<2;n++) Policy(SessionId.New(), []).Propose(Scope, Content, MemoryKind.Fact, None);
        List<DomainEventPayload> events=[]; var session=SessionId.New();
        var policy=new ScopedMemoryPolicy(session,new ScopedMemoryStore(_root),()=>events.ToArray(),item=>
        {
            if(item is ScopedMemoryMutationPrepared) throw new IOException("before Prepared");
            events.Add(item);
        },_=>true, source=>_journals.GetValueOrDefault(source)?.ToArray()??[]);
        Assert.Throws<IOException>(()=>policy.Propose(Scope,Content,MemoryKind.Fact,None));
        Assert.Contains(events,item=>item is MemoryCandidateProposed { Decision:"promote-evidence" });
        var reopened=Policy(session,events);
        Assert.Equal("ask",reopened.Propose(Scope,Content,MemoryKind.Fact,None).Decision);
        Assert.Empty(new ScopedMemoryStore(_root).Read(Scope,None));
        Assert.Single(events.OfType<InteractionRequested>());
    }

    [Fact]
    public void Legacy_full_operation_ledger_still_allows_forgetting_existing_memory()
    {
        var store = new ScopedMemoryStore(_root); var scope = MemoryScope.ForUser();
        var record = store.Add(new MemoryRecord("legacy", scope, MemoryKind.Fact, Content, .9, 1,
            MemorySource.UserStated, DateTimeOffset.UtcNow, null, MemoryExpirationKind.WithScope, [], [], null), None);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(_root, "user.db") + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000) INSERT INTO scoped_memory_operations SELECT 'User','user','old-'||x,'old','old',0 FROM n";
            command.ExecuteNonQuery();
        }
        // Scope's wire id is not assumed by the fixture SQL: normalize the seeded partition.
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(_root, "user.db") + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE scoped_memory_operations SET scope_id=$id"; command.Parameters.AddWithValue("$id", scope.Id); command.ExecuteNonQuery();
        }
        Assert.True(Policy(SessionId.New(), []).Forget(scope, record.Id, None));
        Assert.Empty(store.Read(scope, None));
    }
}
