namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

public sealed class UserWorkspaceSpendReaderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Standalone_reader_includes_all_workspaces_without_fake_session_and_rejects_incomplete_evidence(
        bool meta, bool notDispatched)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-all-workspace-spend-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspaces", "existing");
        Directory.CreateDirectory(workspace);
        var journal = Path.Combine(workspace, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session);
            var turn = TurnId.New();
            stream.Append(new TurnStarted(turn, run.RootLane), DurabilityClass.Barrier);
            if (meta)
            {
                var input = new FileArtifactStore(workspace).PutText("offline fixture", "text/plain",
                    ArtifactKind.Other, Sensitivity.Sensitive);
                stream.Append(new MetaModelInvocationStarted("fixture-meta", run.RunId,
                    "CompressContext", "fixture-fingerprint", input), DurabilityClass.Barrier);
                if (notDispatched)
                    stream.Append(new MetaModelInvocationNotDispatched("fixture-meta", run.RunId,
                        "CompressContext", "fixture-fingerprint"), DurabilityClass.Barrier);
            }
            else
            {
                stream.Append(new ModelStepStarted(turn, 0, "fixture-model", 8192, "Direct",
                    null, null, null), DurabilityClass.Barrier);
                if (notDispatched)
                    stream.Append(new ModelStepNotDispatched(turn, 0), DurabilityClass.Barrier);
            }
            store.Close();
            using var pooled = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(pooled);
            var before = File.ReadAllBytes(journal);
            var reader = new UserWorkspaceSpendReader(root);
            Assert.Single(reader.ReadOtherWorkspaces());
            const string day = "2026-10-07";
            if (notDispatched)
            {
                Assert.Equal(0m, CanonicalSpendReader.ReadAllWorkspaceDaily(reader, codecs, day));
                Assert.Equal(0m, CanonicalSpendReader.ReadAllWorkspaceDaily(reader, codecs, day));
            }
            else
                Assert.Throws<InvalidDataException>(() =>
                    CanonicalSpendReader.ReadAllWorkspaceDaily(reader, codecs, day));
            Assert.Equal(0m, CanonicalSpendReader.ReadAllWorkspaceDaily(
                new UserWorkspaceSpendReader(root, workspace), codecs, day));
            Assert.Equal(before, File.ReadAllBytes(journal));
        }
        finally
        {
            store.Close();
            Directory.Delete(root, true);
        }
    }

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
