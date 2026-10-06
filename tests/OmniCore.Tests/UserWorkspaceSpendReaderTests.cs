namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Infrastructure;

public sealed class UserWorkspaceSpendReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_read_preserves_other_journal_and_does_not_migrate_legacy_columns(bool legacy)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-user-spend-reader-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(root, "workspaces", "other");
        var current = Path.Combine(root, "workspaces", "current");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(current);
        var journal = Path.Combine(other, "journal.db");
        var store = new SqliteEventStore(journal);
        store.Close();
        using var pooled = new SqliteConnection("DataSource=" + journal);
        SqliteConnection.ClearPool(pooled);
        try
        {
            if (legacy)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = journal, Pooling = false }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE events DROP COLUMN execution_id; ALTER TABLE events DROP COLUMN source;";
                command.ExecuteNonQuery();
            }
            var before = File.ReadAllBytes(journal);
            var reader = new UserWorkspaceSpendReader(root, current);
            Assert.Empty(Assert.Single(reader.ReadOtherWorkspaces()).Events);
            Assert.Empty(Assert.Single(reader.ReadOtherWorkspaces()).Events);
            Assert.Equal(before, File.ReadAllBytes(journal));
            // Excluding the current workspace also avoids counting its own journal twice.
            Assert.Empty(new UserWorkspaceSpendReader(root, other).ReadOtherWorkspaces());
        }
        finally { Directory.Delete(root, true); }
    }
}
