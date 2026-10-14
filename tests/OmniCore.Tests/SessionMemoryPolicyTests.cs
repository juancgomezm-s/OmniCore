namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Memory;

public sealed class SessionMemoryPolicyTests
{
    [Fact]
    public void Session_candidate_policy_auto_promotes_safe_content_rejects_pii_and_replays_decisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var journal = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            var policy = Create(session, root, journal, codecs);
            var safe = policy.Propose("Use dotnet test for the unit suite.", MemoryKind.Convention, MemoryCandidateOrigin.Model);
            Assert.Equal("promoted", safe.Decision);
            Assert.Single(new SessionMemoryStore(root).Read(MemoryScope.ForSession(session), CancellationToken.None));
            var rejected = policy.Propose("Contact alice@example.com for the account.", MemoryKind.Fact, MemoryCandidateOrigin.Model);
            Assert.Equal("rejected", rejected.Decision);
            var payloads = journal.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Contains(payloads, item => item is MemoryCandidateProposed proposal
                && proposal.CandidateId == rejected.Id && !proposal.Content.Contains("alice@example.com", StringComparison.Ordinal));
            Assert.DoesNotContain(journal.ReadFrom(session, 1), item => item.PayloadJson.Contains("alice@example.com", StringComparison.Ordinal));

            var reopened = Create(session, root, journal, codecs);
            Assert.Equal("promoted", Assert.Single(reopened.Candidates(), item => item.Id == safe.Id).Decision);
            Assert.Equal("rejected", Assert.Single(reopened.Candidates(), item => item.Id == rejected.Id).Decision);
            Assert.Contains(payloads, item => item is MemoryPromoted);
            Assert.Contains(payloads, item => item is MemoryRejected);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Higher_confidence_conflict_requires_explicit_superseding_promotion_after_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var journal = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            var store = new SessionMemoryStore(root); var scope = MemoryScope.ForSession(session);
            store.Add(new MemoryRecord("trusted-build", scope, MemoryKind.Decision,
                "The build command is dotnet build.", .9, .95, MemorySource.UserStated,
                DateTimeOffset.UtcNow.AddMinutes(-1), null, MemoryExpirationKind.WithScope, ["build"], [], null), CancellationToken.None);
            var policy = Create(session, root, journal, codecs);
            var candidate = policy.Propose("The build command is msbuild.", MemoryKind.Decision, MemoryCandidateOrigin.Model);
            Assert.Equal("ask", candidate.Decision);
            Assert.Equal("trusted-build", candidate.ConflictMemoryId);
            var request = Assert.Single(journal.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<InteractionRequested>(), item => item.Kind == InteractionKind.MemoryPromotion);
            Assert.Equal("reject", request.DefaultOptionId);
            Assert.Contains(candidate.Id, request.SubjectJson, StringComparison.Ordinal);
            Assert.Contains("trusted-build", request.SubjectJson, StringComparison.Ordinal);
            Assert.Contains("msbuild", request.SubjectJson, StringComparison.Ordinal);
            Assert.Contains("\"id\":\"promote\"", request.OptionsJson, StringComparison.Ordinal);
            Assert.Contains("\"id\":\"reject\"", request.OptionsJson, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => policy.Promote(candidate.Id));

            policy = Create(session, root, journal, codecs);
            // Reopening and retrying the same unresolved proposal retains a single durable Ask.
            Assert.Equal("ask", policy.Propose("The build command is msbuild.", MemoryKind.Decision,
                MemoryCandidateOrigin.Model).Decision);
            Assert.Single(journal.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<InteractionRequested>(), item => item.Kind == InteractionKind.MemoryPromotion);
            var promoted = policy.Promote(candidate.Id, "trusted-build");
            Assert.Equal("trusted-build", promoted.Supersedes);
            Assert.Equal(2, store.Read(scope, CancellationToken.None).Count);
            Assert.Equal("promoted", Assert.Single(policy.Candidates(), item => item.Id == candidate.Id).Decision);
            Assert.Contains(journal.ReadFrom(session, 1).Select(codecs.Decode), item => item is MemoryPromoted promotedEvent
                && promotedEvent.CandidateId == candidate.Id && promotedEvent.Supersedes == "trusted-build");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Rejected_candidate_remains_idempotent_after_falling_outside_presentation_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-candidate-idempotency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var journal = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            var scope = MemoryScope.ForSession(session);
            var store = new SessionMemoryStore(root);
            store.Add(new MemoryRecord("trusted-network", scope, MemoryKind.Decision,
                "The production deployment uses the blue network segment.", .95, .95, MemorySource.UserStated,
                DateTimeOffset.UtcNow.AddMinutes(-1), null, MemoryExpirationKind.WithScope, ["network"], [], null),
                CancellationToken.None);
            var policy = Create(session, root, journal, codecs);
            var candidate = policy.Propose("The production deployment uses the green network segment.",
                MemoryKind.Decision, MemoryCandidateOrigin.Model);
            Assert.Equal("ask", candidate.Decision);
            policy.Reject(candidate.Id);
            for (var i = 0; i < 55; i++)
                policy.Propose($"Use fixture value {i} for local test case.", MemoryKind.Convention,
                    MemoryCandidateOrigin.User);

            Assert.DoesNotContain(SessionMemoryPolicy.ProjectForPresentation(
                journal.ReadFrom(session, 1).Select(codecs.Decode).ToArray()), item => item.Id == candidate.Id);
            var interactionsBefore = journal.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<InteractionRequested>().Count(item => item.Kind == InteractionKind.MemoryPromotion);
            var replayed = policy.Propose("The production deployment uses the green network segment.",
                MemoryKind.Decision, MemoryCandidateOrigin.Model);

            Assert.Equal(candidate.Id, replayed.Id);
            Assert.Equal("rejected", replayed.Decision);
            Assert.Equal(interactionsBefore, journal.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<InteractionRequested>().Count(item => item.Kind == InteractionKind.MemoryPromotion));
            Assert.DoesNotContain(journal.ReadFrom(session, 1).Select(codecs.Decode), item => item is MemoryPromoted
                promoted && promoted.CandidateId == candidate.Id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Replacement_chain_conflicts_only_with_the_active_record()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var journal = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            var store = new SessionMemoryStore(root); var scope = MemoryScope.ForSession(session);
            store.Add(new MemoryRecord("a", scope, MemoryKind.Decision,
                "The deployment uses the blue staging cluster.", .9, .95, MemorySource.UserStated,
                DateTimeOffset.UtcNow.AddMinutes(-3), null, MemoryExpirationKind.WithScope, ["deploy"], [], null), CancellationToken.None);
            var policy = Create(session, root, journal, codecs);
            var b = policy.Propose("The deployment uses the green staging cluster.", MemoryKind.Decision, MemoryCandidateOrigin.User);
            Assert.Equal("a", b.ConflictMemoryId);
            policy.Promote(b.Id, "a");
            var c = policy.Propose("The deployment uses the red staging cluster.", MemoryKind.Decision, MemoryCandidateOrigin.Model);
            Assert.Equal("ask", c.Decision);
            Assert.Equal("mem-" + b.Id, c.ConflictMemoryId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Expired_high_confidence_memory_does_not_block_a_new_candidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-expired-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var journal = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            var store = new SessionMemoryStore(root); var scope = MemoryScope.ForSession(session);
            store.Add(new MemoryRecord("expired-build", scope, MemoryKind.Decision,
                "The release uses the old staging server.", .9, .95, MemorySource.UserStated,
                DateTimeOffset.UtcNow.AddDays(-2), null, MemoryExpirationKind.At, ["release"], [], null,
                DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None);
            var candidate = Create(session, root, journal, codecs).Propose(
                "The release uses the new staging server.", MemoryKind.Decision, MemoryCandidateOrigin.Model);
            Assert.Equal("promoted", candidate.Decision);
            Assert.Null(candidate.ConflictMemoryId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Heuristic_origin_requires_three_distinct_successful_dotnet_build_or_test_events()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-observe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var journal = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            var policy = Create(session, root, journal, codecs);
            var scope = new EventStream(journal, codecs, session);
            var arguments = "{\"executable\":\"dotnet\",\"argv\":[\"build\"],\"cwd\":\".\",\"timeoutSeconds\":30}";
            for (var i = 0; i < 2; i++)
            {
                var id = ToolCallId.New();
                scope.Append(new ToolCallRequested(id, "provider-" + i, "process.exec", arguments));
                scope.Append(new ToolCallPrepared(id, arguments));
                scope.Append(new PermissionEvaluated(id, PermissionDecision.Allow, "{}", null));
                scope.Append(new ToolCallAuthorized(id));
                scope.Append(new ToolCallStarted(id, EffectClass.NonIdempotent, null));
                scope.Append(new ToolCallSucceeded(id, "{}"));
                policy.ObserveSuccessfulDotnetValidation(id.ToString(), arguments);
            }
            Assert.DoesNotContain(policy.Candidates(), item => item.Origin == MemoryCandidateOrigin.Heuristic.ToString());
            var third = ToolCallId.New();
            scope.Append(new ToolCallRequested(third, "provider-3", "process.exec", arguments));
            scope.Append(new ToolCallPrepared(third, arguments));
            scope.Append(new PermissionEvaluated(third, PermissionDecision.Allow, "{}", null));
            scope.Append(new ToolCallAuthorized(third));
            scope.Append(new ToolCallStarted(third, EffectClass.NonIdempotent, null));
            scope.Append(new ToolCallSucceeded(third, "{}"));
            policy.ObserveSuccessfulDotnetValidation(third.ToString(), arguments);
            var heuristic = Assert.Single(policy.Candidates(), item => item.Origin == MemoryCandidateOrigin.Heuristic.ToString());
            Assert.Equal("promoted", heuristic.Decision);
            Assert.Contains(journal.ReadFrom(session, 1).Select(codecs.Decode), item => item is MemoryCandidateProposed
                { Origin: MemoryCandidateOrigin.Heuristic, EvidenceRefs.Count: 3 });

            var repeatedModel = policy.Propose("The user always says build succeeded.", MemoryKind.Fact, MemoryCandidateOrigin.Model);
            policy.Propose("The user always says build succeeded.", MemoryKind.Fact, MemoryCandidateOrigin.Model);
            Assert.Equal("Model", repeatedModel.Origin);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Retry_reconciles_preexisting_store_record_when_promotion_receipt_publish_failed()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var journal = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            const string content = "Use the stable local test fixture.";
            var id = CandidateId(session, MemoryKind.Convention, content);
            var scope = MemoryScope.ForSession(session);
            var store = new SessionMemoryStore(root);
            store.Add(new MemoryRecord("mem-" + id, scope, MemoryKind.Convention, content, .45, .65,
                MemorySource.Promoted, DateTimeOffset.UtcNow, null, MemoryExpirationKind.WithScope,
                ["session", "model"], [], null), CancellationToken.None);
            IReadOnlyList<DomainEventPayload> Read() => journal.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var failPromotion = true;
            void Emit(DomainEventPayload payload)
            {
                if (payload is MemoryPromoted && failPromotion) throw new IOException("simulated journal failure");
                new EventStream(journal, codecs, session).Append(payload);
            }
            var policy = new SessionMemoryPolicy(session, store, Read, Emit);
            Assert.Throws<IOException>(() => policy.Propose(content, MemoryKind.Convention, MemoryCandidateOrigin.Model));
            Assert.Contains(store.Read(scope, CancellationToken.None), item => item.Id == "mem-" + id);
            failPromotion = false;
            policy = new SessionMemoryPolicy(session, store, Read, Emit);
            Assert.Equal("promoted", policy.Propose(content, MemoryKind.Convention, MemoryCandidateOrigin.Model).Decision);
            Assert.Contains(Read(), item => item is MemoryPromoted promoted && promoted.MemoryId == "mem-" + id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static SessionMemoryPolicy Create(SessionId session, string root, InMemoryEventStore journal,
        EventCodecs codecs)
    {
        IReadOnlyList<DomainEventPayload> Read() => journal.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
        void Emit(DomainEventPayload payload) => new EventStream(journal, codecs, session).Append(payload);
        return new SessionMemoryPolicy(session, new SessionMemoryStore(root), Read, Emit);
    }

    private static string CandidateId(SessionId session, MemoryKind kind, string content)
    {
        var normalized = string.Join(' ', content.Normalize(System.Text.NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(session + "|" + kind + "|" + normalized))).ToLowerInvariant();
    }
}
