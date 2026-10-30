namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Memory;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class ScopedMemoryIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-scoped-wiring-" + Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken None = CancellationToken.None;
    private string Data => Path.Combine(_root, "data");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Theory]
    [InlineData("project")]
    [InlineData("workspace")]
    [InlineData("global")]
    public async System.Threading.Tasks.Task User_commands_always_ask_before_sharing_and_keep_Session_projection_separate(string name)
    {
        var server = Server("first", trusted: true);
        var handler = new ClientActionHandler();
        var context = new ClientContext(server, Localization.Spanish());
        var proposed = await handler.ExecuteAsync(new(ClientActionIds.MemorySession,
            [name, "propose", "Run focused checks before editing."], "slash"), context, None);
        Assert.Contains("confirmación", proposed.Text);
        Assert.Empty(Query(server, name).GetProperty("records").EnumerateArray());
        var candidate = Assert.Single(Query(server, name).GetProperty("candidates").EnumerateArray());
        var pending = Assert.Single(Journal(server).OfType<InteractionRequested>(), item => item.Kind == InteractionKind.MemoryPromotion);
        Assert.Equal("reject", pending.DefaultOptionId);
        Assert.DoesNotContain(Journal(server), item => item is MemoryPromoted or ScopedMemoryMutationPrepared);
        var denied = server.Send(Envelope(name, "promote", "candidateId", candidate.GetProperty("id").GetString()!, server), None);
        Assert.Equal("error", denied.Status);
        var approved = server.RespondToInteraction(pending.InteractionId, "promote");
        Assert.True(approved.Status == "ok", approved.Error);
        var memory = Assert.Single(Query(server, name).GetProperty("records").EnumerateArray());
        Assert.Equal("Run focused checks before editing.", memory.GetProperty("content").GetString());
        Assert.Empty(Query(server, "session").GetProperty("candidates").EnumerateArray());
        Assert.Single(Journal(server).OfType<ScopedMemoryMutationPrepared>());
        Assert.Single(Journal(server).OfType<ScopedMemoryMutationCompleted>());
    }

    [Fact]
    public void New_sessions_share_Global_and_trusted_Project_but_keep_workspace_memory_local()
    {
        var first = Server("clone-one", trusted: true);
        foreach (var name in new[] { "global", "project", "workspace" }) Approve(first, name, name + " fact.");
        var second = Server("clone-two", trusted: true);
        Assert.Single(Query(second, "global").GetProperty("records").EnumerateArray());
        Assert.Single(Query(second, "project").GetProperty("records").EnumerateArray());
        Assert.Empty(Query(second, "workspace").GetProperty("records").EnumerateArray());
        Assert.NotEqual(first.LastSessionId(), second.LastSessionId());
        var forgottenId = Query(second, "global").GetProperty("records")[0].GetProperty("id").GetString()!;
        var forgotten = Send(second, "global", "forget", "memoryId", forgottenId);
        Assert.True(forgotten.Status == "ok", forgotten.Error);
        Assert.Empty(Query(first, "global").GetProperty("records").EnumerateArray());
        Assert.Contains(Journal(second), item => item is ScopedMemoryMutationCompleted { Removed: true });
        Assert.Equal("ok", Send(first, "global", "propose", "content", "global fact.").Status);
        Assert.Equal(2, Journal(first).OfType<InteractionRequested>().Count(item => JsonObj.Parse(item.SubjectJson).GetValueOrDefault("scopeLevel") == "User"));
        Assert.Empty(Query(first, "global").GetProperty("records").EnumerateArray());
    }

    [Fact]
    public void Revoked_workspace_trust_blocks_Project_reads_and_an_already_open_approval()
    {
        var server = Server("trusted", trusted: true);
        Assert.Equal("ok", Send(server, "project", "propose", "content", "Project convention.").Status);
        var pending = Journal(server).OfType<InteractionRequested>().Single();
        new WorkspaceTrustStore(OmniHost.CreatePlatformPaths(Data)).SetTrusted(Path.Combine(_root, "trusted"), false);
        Assert.False(Query(server, "project").GetProperty("available").GetBoolean());
        Assert.Equal("error", server.RespondToInteraction(pending.InteractionId, "promote").Status);
        Assert.DoesNotContain(Journal(server), item => item is ScopedMemoryMutationPrepared);
        new WorkspaceTrustStore(OmniHost.CreatePlatformPaths(Data)).SetTrusted(Path.Combine(_root, "trusted"), true);
        Assert.Equal("ok", server.RespondToInteraction(pending.InteractionId, "promote").Status);
    }

    [Fact]
    public void Sensitive_proposals_are_withheld_and_cannot_be_promoted()
    {
        var server = Server("privacy");
        Assert.Equal("ok", Send(server, "global", "propose", "content", "Contact alice@example.com.").Status);
        Assert.DoesNotContain(Journal(server).OfType<InteractionRequested>(), item => item.Kind == InteractionKind.MemoryPromotion);
        Assert.All(server.AcquireStore().ReadFrom(server.LastSessionId()!, 1), item => Assert.DoesNotContain("alice@example.com", item.PayloadJson));
        var candidate = Query(server, "global").GetProperty("candidates")[0];
        Assert.Equal("rejected", candidate.GetProperty("decision").GetString());
        Assert.Equal("error", Send(server, "global", "promote", "candidateId", candidate.GetProperty("id").GetString()!).Status);
    }

    [Fact]
    public void Approval_of_a_stale_partition_cannot_replace_a_newer_fact_and_can_be_reproposed()
    {
        var server = Server("stale");
        Assert.Equal("ok", Send(server, "global", "propose", "content", "First proposed convention.").Status);
        var pending = Journal(server).OfType<InteractionRequested>().Single();
        new ScopedMemoryStore(Data).Add(Record(MemoryScope.ForUser(), "A newer independent convention."), None);
        Assert.Equal("error", server.RespondToInteraction(pending.InteractionId, "promote").Status);
        Assert.Single(Query(server, "global").GetProperty("records").EnumerateArray());
        Assert.Equal("ok", Send(server, "global", "propose", "content", "First proposed convention.").Status);
        var next = Journal(server).OfType<InteractionRequested>().Last();
        Assert.NotEqual(pending.InteractionId, next.InteractionId);
        var approved = server.RespondToInteraction(next.InteractionId, "promote");
        Assert.True(approved.Status == "ok", approved.Error);
    }

    [Fact]
    public void Reconciliation_after_another_Session_forgot_the_record_never_resurrects_it()
    {
        List<DomainEventPayload> firstJournal = [];
        var session = SessionId.New();
        var store = new ScopedMemoryStore(Data);
        var failReceipt = true;
        void Emit(DomainEventPayload payload)
        {
            if (failReceipt && payload is MemoryPromoted) throw new IOException("fixture receipt failure");
            firstJournal.Add(payload);
        }
        var policy = new ScopedMemoryPolicy(session, store, () => firstJournal, Emit, _ => true);
        var proposal = policy.Propose(MemoryScope.ForUser(), "A shared convention.", MemoryKind.Convention, None);
        var request = firstJournal.OfType<InteractionRequested>().Single();
        firstJournal.Add(new InteractionResolved(request.InteractionId, "promote", InteractionCause.User));
        Assert.Throws<IOException>(() => policy.Promote(proposal.Id, None));
        var written = Assert.Single(store.Read(MemoryScope.ForUser(), None));
        List<DomainEventPayload> otherJournal = [];
        var other = new ScopedMemoryPolicy(SessionId.New(), new ScopedMemoryStore(Data), () => otherJournal, otherJournal.Add, _ => true);
        Assert.True(other.Forget(MemoryScope.ForUser(), written.Id, None));
        failReceipt = false;
        policy.Reconcile(None);
        policy.Reconcile(None);
        Assert.Empty(new ScopedMemoryStore(Data).Read(MemoryScope.ForUser(), None));
        Assert.Single(firstJournal.OfType<MemoryPromoted>());
        Assert.Single(firstJournal.OfType<ScopedMemoryMutationCompleted>());
    }

    [Fact]
    public void Prepared_but_unapplied_stale_intent_is_rejected_without_blocking_future_reconciliation()
    {
        List<DomainEventPayload> journal = [];
        var store = new ScopedMemoryStore(Data);
        var sabotage = true;
        void Emit(DomainEventPayload payload)
        {
            journal.Add(payload);
            if (sabotage && payload is ScopedMemoryMutationPrepared)
            {
                sabotage = false;
                store.Add(Record(MemoryScope.ForUser(), "Concurrent update."), None);
            }
        }
        var policy = new ScopedMemoryPolicy(SessionId.New(), store, () => journal, Emit, _ => true);
        var proposal = policy.Propose(MemoryScope.ForUser(), "Proposed convention.", MemoryKind.Fact, None);
        var ask = journal.OfType<InteractionRequested>().Single();
        journal.Add(new InteractionResolved(ask.InteractionId, "promote", InteractionCause.User));
        Assert.Throws<InvalidOperationException>(() => policy.Promote(proposal.Id, None));
        Assert.Single(journal.OfType<ScopedMemoryMutationRejected>());
        policy.Reconcile(None);
        policy.Propose(MemoryScope.ForUser(), "Proposed convention.", MemoryKind.Fact, None);
        Assert.Equal(2, journal.OfType<InteractionRequested>().Count());
        Assert.Single(store.Read(MemoryScope.ForUser(), None));
    }

    [Fact]
    public void Concurrent_forgets_from_different_Sessions_do_not_collide_in_the_operation_ledger()
    {
        var store = new ScopedMemoryStore(Data);
        var record = Record(MemoryScope.ForUser(), "Concurrent forget."); store.Add(record, None);
        List<DomainEventPayload> firstJournal = [], secondJournal = [];
        var second = new ScopedMemoryPolicy(SessionId.New(), store, () => secondJournal, secondJournal.Add, _ => true);
        var first = new ScopedMemoryPolicy(SessionId.New(), store, () => firstJournal, payload =>
        {
            firstJournal.Add(payload);
            if (payload is ScopedMemoryMutationPrepared { Operation: "forget" })
                Assert.True(second.Forget(MemoryScope.ForUser(), record.Id, None));
        }, _ => true);
        Assert.Throws<InvalidOperationException>(() => first.Forget(MemoryScope.ForUser(), record.Id, None));
        Assert.NotEqual(firstJournal.OfType<ScopedMemoryMutationPrepared>().Single().OperationId,
            secondJournal.OfType<ScopedMemoryMutationPrepared>().Single().OperationId);
        Assert.Single(firstJournal.OfType<ScopedMemoryMutationRejected>());
        first.Reconcile(None);
        Assert.Empty(store.Read(MemoryScope.ForUser(), None));
    }

    [Fact]
    public void Retry_after_interrupted_Ask_recomputes_conflict_from_a_fresh_snapshot()
    {
        List<DomainEventPayload> journal = [];
        var store = new ScopedMemoryStore(Data);
        var failAsk = true;
        var policy = new ScopedMemoryPolicy(SessionId.New(), store, () => journal, payload =>
        {
            if (failAsk && payload is InteractionRequested) throw new IOException("fixture interrupted Ask");
            journal.Add(payload);
        }, _ => true);
        Assert.Throws<IOException>(() => policy.Propose(MemoryScope.ForUser(), "Use the green production region.", MemoryKind.Fact, None));
        var initial = journal.OfType<MemoryCandidateProposed>().Single();
        var replacement = Record(MemoryScope.ForUser(), "Use the blue production region."); store.Add(replacement, None);
        failAsk = false;
        var fresh = policy.Propose(MemoryScope.ForUser(), "Use the green production region.", MemoryKind.Fact, None);
        Assert.NotEqual(initial.CandidateId, fresh.Id);
        Assert.Equal(replacement.Id, fresh.ConflictMemoryId);
        var ask = journal.OfType<InteractionRequested>().Single();
        journal.Add(new InteractionResolved(ask.InteractionId, "promote", InteractionCause.User));
        policy.Promote(fresh.Id, None);
        Assert.Contains(store.Read(MemoryScope.ForUser(), None), item => item.Supersedes == replacement.Id);
    }

    [Fact]
    public void Prepared_operation_retries_are_immutable_and_cannot_resurrect_a_removed_record()
    {
        var store = new ScopedMemoryStore(Data); var scope = MemoryScope.ForUser();
        var record = Record(scope, "Immutable operation.");
        var prepared = new ScopedMemoryMutationPrepared("operation-one", scope, "promote", "candidate-one",
            record.Id, record, store.Revision(scope, None), DateTimeOffset.UtcNow);
        var applied = store.ApplyPrepared(prepared, None);
        Assert.Equal(record.Id, applied.MemoryId);
        Assert.Equal(applied, store.ApplyPrepared(prepared, None));
        Assert.Throws<InvalidDataException>(() => store.ApplyPrepared(prepared with { Record = record with { Content = "Changed." } }, None));
        Assert.True(store.Remove(scope, record.Id, None));
        Assert.Equal(applied, new ScopedMemoryStore(Data).ApplyPrepared(prepared, None));
        Assert.Empty(store.Read(scope, None));
    }

    [Fact]
    public async System.Threading.Tasks.Task Context_filters_expiration_deduplicates_and_rechecks_revocation_before_marking_usage()
    {
        var store = new ScopedMemoryStore(Data);
        var project = MemoryScope.ForProject(ProjectId.Derive("example-project"));
        var workspace = MemoryScope.ForWorkspace(WorkspaceId.Of("example-workspace"));
        var user = MemoryScope.ForUser();
        var projectRecord = Record(project, "Project convention."); store.Add(projectRecord, None);
        store.Add(Record(workspace, "The same convention."), None);
        store.Add(Record(user, "The same convention."), None);
        store.Add(Record(user, "Expired convention.") with { Expiration = MemoryExpirationKind.At, ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) }, None);
        var trusted = true;
        var contributor = new ScopedMemoryContextContributor(store, () => trusted ? [project, workspace, user] : [workspace, user]);
        var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 0,
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"));
        var items = await contributor.GetContextAsync(request, None);
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.Content.Contains("Expired", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Provenance.Scope == ScopeLevel.Workspace && item.Content.Contains("The same", StringComparison.Ordinal));
        trusted = false;
        var revoked = await contributor.GetContextAsync(request, None);
        Assert.DoesNotContain(revoked, item => item.Provenance.Scope == ScopeLevel.Project);
        contributor.RecordUsed(request.SessionId, items, DateTimeOffset.UtcNow, None);
        Assert.Null(Assert.Single(store.Read(project, None)).LastUsedAt);
        Assert.NotNull(Assert.Single(store.Read(workspace, None)).LastUsedAt);
    }

    [Fact]
    public void Cancelled_proposal_cannot_create_an_Ask_or_prepared_mutation()
    {
        List<DomainEventPayload> journal = [];
        var policy = new ScopedMemoryPolicy(SessionId.New(), new ScopedMemoryStore(Data), () => journal, journal.Add, _ => true);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => policy.Propose(MemoryScope.ForUser(), "Cancelled.", MemoryKind.Fact, cancellation.Token));
        Assert.Empty(journal);
    }

    [Fact]
    public void Full_partition_rejects_the_intent_without_poisoning_later_reconciliation()
    {
        var scope = MemoryScope.ForUser(); var store = new ScopedMemoryStore(Data);
        for (var index = 0; index < 500; index++) store.Add(Record(scope, "Existing " + index), None);
        List<DomainEventPayload> journal = [];
        var policy = new ScopedMemoryPolicy(SessionId.New(), store, () => journal, journal.Add, _ => true);
        var proposal = policy.Propose(scope, "Another convention.", MemoryKind.Fact, None);
        var ask = journal.OfType<InteractionRequested>().Single();
        journal.Add(new InteractionResolved(ask.InteractionId, "promote", InteractionCause.User));
        var failure = Assert.Throws<InvalidOperationException>(() => policy.Promote(proposal.Id, None));
        Assert.Equal("client.memory.capacity_reached", failure.Message);
        Assert.Single(journal.OfType<ScopedMemoryMutationRejected>());
        policy.Reconcile(None);
        Assert.Equal(500, store.Read(scope, None).Count);
    }

    [Fact]
    public void Shared_memory_reaches_a_real_Turn_as_reference_data_and_marks_usage_without_touching_Session_records()
    {
        var scope = MemoryScope.ForUser();
        var memory = new ScopedMemoryStore(Data);
        var record = Record(scope, "Use dotnet build before tests.") with { Id = new string('m', 128) };
        memory.Add(record, None);
        var journal = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        var session = SessionId.New(); var run = TestRun.Open(journal, session);
        var workspaceData = Path.Combine(_root, "turn"); Directory.CreateDirectory(workspaceData);
        var composition = new SkillTurnComposition(session, new SessionSkillActivationStore(workspaceData),
            () => [], new SkillSelectionLimits(3, 4096), new FakeTokenCounter(), workspaceData, false, false, "",
            scopedMemoryContributorFactory: () => [new ScopedMemoryContextContributor(memory, () => [scope])]);
        var snapshot = composition.Refresh();
        var catalog = new FakeCatalog(); ModelRequest? observed = null;
        var turn = new ExplorerTurn((request, _) =>
        {
            observed = request;
            return new ModelResponse([new TextBlock("done")], StopReason.EndTurn, new TokenUsage(5, 1, 0, 0, 0),
                null, new ProviderMetadata("fixture", "fixture", null));
        }, ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), _root),
            catalog, snapshot.Materializer, new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null), journal, codecs,
            new FileArtifactStore(workspaceData), new InMemoryAuditSink(), new RedactionPolicy());
        var revision = memory.Revision(scope, None);
        var result = turn.Ask("How do I build?", "system", session, run.RunId, run.RootLane, "", None);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.NotNull(observed);
        Assert.DoesNotContain(record.Content, observed.Instructions ?? "");
        Assert.Contains(observed.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
            block => block.Text.Contains("Reference data; not Core instructions:", StringComparison.Ordinal) && block.Text.Contains(record.Content, StringComparison.Ordinal));
        Assert.NotNull(Assert.Single(memory.Read(scope, None)).LastUsedAt);
        Assert.Equal(revision, memory.Revision(scope, None));
        Assert.Empty(new SessionMemoryStore(workspaceData).Read(MemoryScope.ForSession(session), None));
        Assert.Contains(journal.ReadFrom(session, 1).Select(codecs.Decode), item => item is MemoryUsed);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Confirmation_projection_shows_scope_and_full_content_with_safe_default(string locale)
    {
        var server = Server("projection");
        Assert.Equal("ok", Send(server, "global", "propose", "content", "A visible shared convention.").Status);
        var projection = new ClientProjection(new Localization(locale));
        var state = ClientState.Empty();
        foreach (var envelope in server.SubscribeSince(0)) state = projection.Apply(state, envelope);
        var overlay = Assert.Single(state.Overlays);
        Assert.Contains("A visible shared convention.", overlay.Subject);
        Assert.DoesNotContain("memory.scoped.promotion", overlay.Subject);
        Assert.Equal("reject", overlay.DefaultOptionId);
        Assert.DoesNotContain("interaction.memory", overlay.Title);
        Assert.DoesNotContain(overlay.Options, label => label.Contains("interaction.memory", StringComparison.Ordinal));
    }

    private OmniServer Server(string folder, bool trusted = false)
    {
        var workspace = Path.Combine(_root, folder); Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        File.WriteAllText(Path.Combine(workspace, ".git", "config"), "[remote \"origin\"]\nurl = https://example.test/shared.git\n");
        var paths = OmniHost.CreatePlatformPaths(Data);
        new WorkspaceTrustStore(paths).SetTrusted(workspace, trusted);
        var server = OmniHost.CreateInMemoryServer();
        server.ConfigurePlatformPaths(() => paths); server.ConfigureWorkspaceRoot(workspace);
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"memory fixture\"}"), None).Status);
        return server;
    }

    private static IReadOnlyList<DomainEventPayload> Journal(OmniServer server) => server.AcquireStore()
        .ReadFrom(server.LastSessionId()!, 1).Select(server.AcquireCodecs().Decode).ToArray();
    private static JsonElement Query(OmniServer server, string name)
    {
        using var document = JsonDocument.Parse(server.Query("memory." + name, None)!.Json);
        return document.RootElement.Clone();
    }
    private static WireEnvelope Envelope(string name, string operation, string field, string value, OmniServer server)
    {
        var query = Query(server, name);
        return WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "memory.scoped." + operation) + "," + JsonObj.Field("scope", name)
            + "," + JsonObj.Field(field, value) + "," + JsonObj.Field("expectedSessionId", server.LastSessionId()!.ToString())
            + "," + JsonObj.Field("expectedScopeId", query.GetProperty("scopeId").GetString()!)
            + "," + JsonObj.Field("expectedRevision", query.GetProperty("revision").GetString()!) + "}");
    }
    private static CommandAck Send(OmniServer server, string name, string operation, string field, string value) =>
        server.SendUserAction(Envelope(name, operation, field, value, server), None);
    private static void Approve(OmniServer server, string name, string content)
    {
        var proposed = Send(server, name, "propose", "content", content); Assert.True(proposed.Status == "ok", proposed.Error);
        var pending = Journal(server).OfType<InteractionRequested>().Last();
        var ack = server.RespondToInteraction(pending.InteractionId, "promote"); Assert.True(ack.Status == "ok", ack.Error);
    }
    private static MemoryRecord Record(MemoryScope scope, string content) => new(Guid.NewGuid().ToString("N"), scope,
        MemoryKind.Fact, content, .7, .9, MemorySource.UserStated, DateTimeOffset.UtcNow, null,
        MemoryExpirationKind.Never, [], [], null);
}
