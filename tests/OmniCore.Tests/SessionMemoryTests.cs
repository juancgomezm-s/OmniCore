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
