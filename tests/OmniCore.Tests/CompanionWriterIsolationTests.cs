using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class CompanionWriterIsolationTests
{
    private sealed class TestPaths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }

    [Fact]
    public void Profile_pet_action_is_independent_of_session_writer_and_deduplicates_by_command_id()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-companion-writer-isolation-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var journal = Path.Combine(root, "journal.db");
        var stateFile = Path.Combine(root, "active-session.txt");
        var session = SessionId.New();
        var codecs = EventCodecs.Create();
        SqliteEventStore? ownerStore = null;
        SqliteEventStore? observerStore = null;
        OmniServer? observer = null;
        var petDatabase = Path.Combine(root, "data", "companion", "pet.db");
        try
        {
            using (var seed = new SqliteEventStore(journal))
                new EventStream(seed, codecs, session).Append(new SessionCreated(session,
                    WorkspaceId.Of(workspace).ToString(), workspace, ProfileId.New(), DateTimeOffset.UtcNow));
            File.WriteAllText(stateFile, session + "\n-\n");

            ownerStore = new SqliteEventStore(journal);
            Assert.True(ownerStore.AcquireSessionWriterLease(session));
            observerStore = new SqliteEventStore(journal);
            observer = new OmniServer(observerStore, codecs, new InMemoryAuditSink(), stateFile,
                platformPathsFactory: () => new TestPaths(root));
            Assert.False(observerStore.OwnsSessionWriterLease(session));
            var sequenceBefore = observerStore.CurrentSequence(session);
            var invalid = observer.Send(WireEnvelope.Command("not-a-command-id",
                "{\"cmd\":\"companion.pet\",\"action\":\"discover\"}"), CancellationToken.None);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, invalid.Outcome?.Kind);
            Assert.False(observer.CompanionService.Status().Discovered);


            var command = WireEnvelope.Command(Guid.NewGuid().ToString("D"),
                "{\"cmd\":\"companion.pet\",\"action\":\"discover\",\"clientId\":\"observer\"}");
            var first = observer.Send(command, CancellationToken.None);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, first.Outcome?.Kind);
            var discovered = CompanionPetProtocol.Decode(
                observer.Query("companion.pet:observer", CancellationToken.None)!.Json)!;
            Assert.True(discovered.Discovered);
            Assert.Equal(sequenceBefore, observerStore.CurrentSequence(session));

            var replay = observer.Send(command, CancellationToken.None);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, replay.Outcome?.Kind);
            var afterReplay = CompanionPetProtocol.Decode(
                observer.Query("companion.pet:observer", CancellationToken.None)!.Json)!;
            Assert.Equal(discovered.Revision, afterReplay.Revision);

            foreach (var payload in new[]
            {
                "{\"cmd\":\"session.input\",\"text\":\"must stay blocked\"}",
                "{\"cmd\":\"session.security.set\",\"mode\":\"act\"}",
                "{\"cmd\":\"run.mode.select\",\"mode\":\"act\"}",
            })
            {
                var ack = observer.Send(WireEnvelope.Command(Guid.NewGuid().ToString("D"), payload),
                    CancellationToken.None);
                Assert.Equal("busy", ack.Status);
                Assert.Equal("session_writer_owned", ack.Error);
                Assert.Equal(RuntimeCommandOutcomeKind.Deferred, ack.Outcome?.Kind);
            }

            Assert.True(ownerStore.OwnsSessionWriterLease(session));
            Assert.False(observerStore.OwnsSessionWriterLease(session));
            Assert.Equal(sequenceBefore, observerStore.CurrentSequence(session));
        }
        finally
        {
            observer?.ClosePersistentStore();
            ownerStore?.Dispose();
            observerStore?.Dispose();
            if (File.Exists(petDatabase))
            {
                using var poolKey = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = petDatabase, DefaultTimeout = 10 }.ToString());
                SqliteConnection.ClearPool(poolKey);
            }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
