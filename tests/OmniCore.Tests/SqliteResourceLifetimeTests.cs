using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class SqliteResourceLifetimeTests
{
    [Fact]
    public void Close_clear_exact_pool_and_dispose_release_journal_after_repeated_commands()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-sqlite-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var journal = Path.Combine(root, "journal-" + iteration.ToString("D2") + ".db");
            var store = new SqliteEventStore(journal);
            var session = SessionId.New();
            for (var sequence = 0; sequence < 16; sequence++)
            {
                var evt = DomainEvent.Create(session, EventType.Of("resource.probe"), 1, null, null, null,
                    null, null, null, null, null, Array.Empty<ArtifactRef>(), "{\"n\":" + sequence + "}");
                store.Append(session, evt, DurabilityClass.Standard, CancellationToken.None);
            }

            Assert.Equal(16, store.CurrentSequence(session));
            Assert.Equal(16, store.ReadFrom(session, 1).Count);
            Assert.Equal(16, store.ReadEvents(EventType.Of("resource.probe")).Count);

            var connection = Assert.IsType<SqliteConnection>(store.Connection);
            store.Close();
            SqliteConnection.ClearPool(connection);
            connection.Dispose();

            File.Delete(journal);
            foreach (var sidecar in new[] { journal + "-wal", journal + "-shm" })
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }

            Assert.False(File.Exists(journal));
        }

        Directory.Delete(root, true);
    }
}
