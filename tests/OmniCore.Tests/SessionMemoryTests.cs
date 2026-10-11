namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Memory;
using OmniCore.Protocol;

public sealed class SessionMemoryTests
{
    [Fact]
    public void Store_survives_reopen_deduplicates_and_keeps_sessions_separate()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-memory-" + Guid.NewGuid().ToString("N"));
        var session = SessionId.New();
        var scope = MemoryScope.ForSession(session);
        try
        {
            var firstStore = new SessionMemoryStore(root);
            var original = Record(scope, "Remember the build command.");
            Assert.Equal(original, firstStore.Add(original, CancellationToken.None));
            var duplicate = Record(scope, "  remember   the build command. ");
            Assert.Equal(original.Id, firstStore.Add(duplicate, CancellationToken.None).Id);
            Assert.Equal(original.Id, firstStore.Add(original, CancellationToken.None).Id);
            Assert.Throws<InvalidDataException>(() => firstStore.Add(original with { Content = "different content" }, CancellationToken.None));

            var reopened = new SessionMemoryStore(root);
            var persisted = Assert.Single(reopened.Read(scope, CancellationToken.None));
            Assert.Equal(original.Id, persisted.Id);
            Assert.Equal(original.Content, persisted.Content);
            Assert.Equal(original.Tags, persisted.Tags);
            Assert.Empty(reopened.Read(MemoryScope.ForSession(SessionId.New()), CancellationToken.None));
            Assert.True(reopened.Remove(scope, original.Id, CancellationToken.None));
            Assert.Empty(new SessionMemoryStore(root).Read(scope, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Supersedes_requires_an_existing_unreplaced_record_and_context_uses_only_the_latest()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-memory-replacement-" + Guid.NewGuid().ToString("N"));
        var scope = MemoryScope.ForSession(SessionId.New());
        try
        {
            var store = new SessionMemoryStore(root);
            var original = Record(scope, "Use the old build command.");
            store.Add(original, CancellationToken.None);
            var missing = Record(scope, "Use the new build command.") with { Supersedes = "missing" };
            Assert.Throws<InvalidDataException>(() => store.Add(missing, CancellationToken.None));
            var missingDuplicate = Record(scope, "  use the   old build command. ") with { Supersedes = "missing" };
            Assert.Throws<InvalidDataException>(() => store.Add(missingDuplicate, CancellationToken.None));

            var replacement = Record(scope, "Use the new build command.") with
            {
                Supersedes = original.Id,
                CreatedAt = original.CreatedAt.AddSeconds(1),
            };
            store.Add(replacement, CancellationToken.None);
            Assert.Equal(replacement.Id, store.Add(replacement, CancellationToken.None).Id);
            var competing = Record(scope, "Another replacement.") with
            {
                Supersedes = original.Id,
                CreatedAt = original.CreatedAt.AddSeconds(2),
            };
            Assert.Throws<InvalidDataException>(() => store.Add(competing, CancellationToken.None));
            var competingDuplicate = competing with { Content = "  use the   new build command. " };
            Assert.Throws<InvalidDataException>(() => store.Add(competingDuplicate, CancellationToken.None));

            var contributor = new SessionMemoryContextContributor(store);
            var request = new MaterializeRequest(SessionId.Parse(scope.Id), RunId.New(), null, null, null, 0,
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"));
            var items = await contributor.GetContextAsync(request, CancellationToken.None);
            Assert.DoesNotContain(items, item => item.Content.Contains("old build command", StringComparison.Ordinal));
            Assert.Contains(items, item => item.Content.Contains("new build command", StringComparison.Ordinal));

            Assert.True(store.Remove(scope, original.Id, CancellationToken.None));
            Assert.Equal(replacement.Id, Assert.Single(new SessionMemoryStore(root).Read(scope, CancellationToken.None)).Id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Forgetting_a_replacement_releases_its_predecessor_without_dangling_links()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-memory-forget-replacement-" + Guid.NewGuid().ToString("N"));
        var scope = MemoryScope.ForSession(SessionId.New());
        try
        {
            var store = new SessionMemoryStore(root);
            var original = Record(scope, "Original fact.");
            var replacement = Record(scope, "Replacement fact.") with
            {
                Supersedes = original.Id,
                CreatedAt = original.CreatedAt.AddSeconds(1),
            };
            store.Add(original, CancellationToken.None);
            store.Add(replacement, CancellationToken.None);

            Assert.True(store.Remove(scope, replacement.Id, CancellationToken.None));
            var remaining = Assert.Single(new SessionMemoryStore(root).Read(scope, CancellationToken.None));
            Assert.Equal(original.Id, remaining.Id);
            Assert.Null(remaining.Supersedes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Last_used_tracks_only_memory_items_in_the_completed_context_and_never_moves_backwards()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-memory-last-used-" + Guid.NewGuid().ToString("N"));
        var session = SessionId.New();
        var scope = MemoryScope.ForSession(session);
        try
        {
            var store = new SessionMemoryStore(root);
            var used = Record(scope, "Used session fact.");
            var unused = Record(scope, "Unused session fact.");
            store.Add(used, CancellationToken.None);
            store.Add(unused, CancellationToken.None);
            var contributor = new SessionMemoryContextContributor(store);
            var fingerprint = new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build");
            var request = new MaterializeRequest(session, RunId.New(), null, null, null, 0, fingerprint);
            var items = await contributor.GetContextAsync(request, CancellationToken.None);
            var usedItem = Assert.Single(items, item => item.Id == "memory." + used.Id);
            var usedAt = DateTimeOffset.UtcNow.AddMinutes(1);

            Assert.Equal(1, contributor.RecordUsed(session, [usedItem], usedAt, CancellationToken.None));
            Assert.Equal(0, contributor.RecordUsed(session, [usedItem], usedAt.AddSeconds(-1), CancellationToken.None));
            var reopened = new SessionMemoryStore(root).Read(scope, CancellationToken.None).ToDictionary(item => item.Id);
            Assert.Equal(usedAt, reopened[used.Id].LastUsedAt);
            Assert.Null(reopened[unused.Id].LastUsedAt);

            var notMemory = new ContextItem(usedItem.Id, ContextItemKind.UserMessage, usedItem.Content,
                usedItem.EstimatedTokens, usedItem.Priority, usedItem.Retention, usedItem.Provenance);
            Assert.Equal(0, contributor.RecordUsed(session, [notMemory], usedAt.AddMinutes(1), CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Replacement_chain_keeps_only_latest_context_and_collapses_when_middle_is_forgotten()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-memory-replacement-chain-" + Guid.NewGuid().ToString("N"));
        var scope = MemoryScope.ForSession(SessionId.New());
        try
        {
            var store = new SessionMemoryStore(root);
            var original = Record(scope, "Build with the legacy command.");
            var first = Record(scope, "Build with the initial command.") with
            {
                Supersedes = original.Id,
                CreatedAt = original.CreatedAt.AddSeconds(1),
            };
            var latest = Record(scope, "Build with the current command.") with
            {
                Supersedes = first.Id,
                CreatedAt = first.CreatedAt.AddSeconds(1),
            };
            store.Add(original, CancellationToken.None);
            store.Add(first, CancellationToken.None);
            store.Add(latest, CancellationToken.None);
            Assert.Equal(latest.Id, store.Add(latest, CancellationToken.None).Id);

            var request = new MaterializeRequest(SessionId.Parse(scope.Id), RunId.New(), null, null, null, 0,
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"));
            var contributor = new SessionMemoryContextContributor(store);
            var items = await contributor.GetContextAsync(request, CancellationToken.None);
            Assert.DoesNotContain(items, item => item.Content.Contains("legacy command", StringComparison.Ordinal));
            Assert.DoesNotContain(items, item => item.Content.Contains("initial command", StringComparison.Ordinal));
            Assert.Contains(items, item => item.Content.Contains("current command", StringComparison.Ordinal));

            Assert.True(store.Remove(scope, first.Id, CancellationToken.None));
            var afterMiddleForget = new SessionMemoryStore(root).Read(scope, CancellationToken.None);
            Assert.Equal(2, afterMiddleForget.Count);
            Assert.Equal(original.Id, Assert.Single(afterMiddleForget, item => item.Id == latest.Id).Supersedes);
            Assert.True(store.Remove(scope, original.Id, CancellationToken.None));
            var onlyLatest = Assert.Single(new SessionMemoryStore(root).Read(scope, CancellationToken.None));
            Assert.Equal(latest.Id, onlyLatest.Id);
            Assert.Null(onlyLatest.Supersedes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Context_retrieval_is_session_scoped_provenanced_and_budgeted()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-memory-context-" + Guid.NewGuid().ToString("N"));
        var session = SessionId.New();
        var scope = MemoryScope.ForSession(session);
        try
        {
            var store = new SessionMemoryStore(root);
            for (var index = 0; index < 30; index++)
                store.Add(Record(scope, $"Fact {index}: " + new string('x', 480)), CancellationToken.None);
            store.Add(Record(scope, "This has expired.") with
            {
                Expiration = MemoryExpirationKind.At,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            }, CancellationToken.None);
            var contributor = new SessionMemoryContextContributor(store);
            var fingerprint = new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build");
            var request = new MaterializeRequest(session, RunId.New(), null, null, null, 0, fingerprint);

            var items = await contributor.GetContextAsync(request, CancellationToken.None);

            Assert.InRange(items.Count, 1, 24);
            Assert.DoesNotContain(items, item => item.Content.Contains("expired", StringComparison.OrdinalIgnoreCase));
            Assert.All(items, item =>
            {
                Assert.Equal(ContextItemKind.Memory, item.Kind);
                Assert.Equal(ContributionCategory.Memory, item.Provenance.Category);
                Assert.Contains("memory:", string.Join('|', item.Provenance.Refs ?? []));
            });
            var otherRequest = new MaterializeRequest(SessionId.New(), request.RunId, null, null, null, 0, fingerprint);
            Assert.Empty(await contributor.GetContextAsync(otherRequest, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Host_and_memory_client_action_require_trusted_user_action_and_reopen_the_session_store()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-memory-host-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var server = OmniHost.CreateInMemoryServer();
        server.ConfigurePlatformPaths(() => OmniHost.CreatePlatformPaths(data));
        server.ConfigureWorkspaceRoot(workspace);
        try
        {
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"start memory fixture\"}"),
                CancellationToken.None).Status);

            var untrusted = server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"memory.session.add\",\"content\":\"untrusted write\"}"), CancellationToken.None);
            Assert.Equal("error", untrusted.Status);
            Assert.Contains("trusted user", untrusted.Error ?? "", StringComparison.OrdinalIgnoreCase);

            var handler = new ClientActionHandler();
            var context = new ClientContext(server, Localization.English());
            var added = await handler.ExecuteAsync(new ClientActionInvocation(ClientActionIds.MemorySession,
                ["session", "add", "Remember the local build command"], "slash"), context, CancellationToken.None);
            Assert.Contains("Remember the local build command", added.Text);

            SessionId persistedSession;
            using (var query = JsonDocument.Parse(server.Query("memory.session", CancellationToken.None)!.Json))
            {
                persistedSession = SessionId.Parse(query.RootElement.GetProperty("session").GetString()!);
                var item = Assert.Single(query.RootElement.GetProperty("records").EnumerateArray());
                Assert.Equal("UserStated", item.GetProperty("source").GetString());
                Assert.Equal("Remember the local build command", item.GetProperty("content").GetString());
            }

            var sensitive = await handler.ExecuteAsync(new ClientActionInvocation(ClientActionIds.MemorySession,
                ["session", "add", "api_key=abcd12345678"], "slash"), context, CancellationToken.None);
            Assert.Contains("rejected", sensitive.Text, StringComparison.OrdinalIgnoreCase);
            var reopened = new SessionMemoryStore(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(data), workspace));
            Assert.Equal("Remember the local build command", Assert.Single(reopened.Read(
                MemoryScope.ForSession(persistedSession), CancellationToken.None)).Content);

            var forgotten = await handler.ExecuteAsync(new ClientActionInvocation(ClientActionIds.MemorySession,
                ["session", "forget", "bad-id"], "slash"), context, CancellationToken.None);
            Assert.Contains("item has that id", forgotten.Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static MemoryRecord Record(MemoryScope scope, string content) => new(Guid.NewGuid().ToString("N"),
        scope, MemoryKind.Fact, content, 0.7, 0.9, MemorySource.Observed, DateTimeOffset.UtcNow,
        null, MemoryExpirationKind.Never, ["fixture"], ["test:evidence"], null);
}
