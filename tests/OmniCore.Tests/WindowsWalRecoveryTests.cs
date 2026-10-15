using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Microsoft.Data.Sqlite;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class WindowsWalRecoveryTests
{
    [Fact]
    public void Writer_recovers_after_Windows_releases_the_real_shared_memory_mapping()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows mapped-file truncation semantics."); return; }
        using var fixture = new Fixture();
        using var mapping = fixture.MapWalIndex();
        using var listener = new RecoveryListener(mapping.Dispose);
        Trace.Listeners.Add(listener);
        try
        {
            using var writer = new SqliteEventStore(fixture.Journal);
            Assert.True(listener.SawWait, "The real mapped file must first prevent SQLite from truncating its WAL index.");
            var receipt = writer.ReserveCommandReceipt("mapped-recovery", new string('A', 64), null);
            Assert.Equal(CommandReceiptState.Reserved, receipt.State);
            writer.CompleteCommandReceipt("mapped-recovery", new string('A', 64), "ok", "Accepted", null, 0, 0);
            Assert.Equal(CommandReceiptState.Completed, writer.ReadCommandReceipt("mapped-recovery")!.State);
            writer.Close();
            SqliteConnection.ClearPool((SqliteConnection)writer.Connection);
            using var reopened = new SqliteEventStore(fixture.Journal);
            Assert.Equal(CommandReceiptState.Completed, reopened.ReadCommandReceipt("mapped-recovery")!.State);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public void Persistent_mapping_still_throws_the_original_SQLite_truncate_error()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows mapped-file truncation semantics."); return; }
        using var fixture = new Fixture();
        using var mapping = fixture.MapWalIndex();
        using var listener = new RecoveryListener(null);
        Trace.Listeners.Add(listener);
        try
        {
            var error = Assert.Throws<SqliteException>(() => { using var writer = new SqliteEventStore(fixture.Journal); });
            Assert.Equal(10, error.SqliteErrorCode);
            Assert.Equal(1546, error.SqliteExtendedErrorCode);
            Assert.True(listener.SawWait);
            Assert.Equal(32768, new FileInfo(fixture.Journal + "-shm").Length);
            mapping.Dispose();
            using var reopened = new SqliteEventStore(fixture.Journal);
            using var seed = reopened.Connection.CreateCommand();
            seed.CommandText = "SELECT count(*) FROM seed";
            Assert.Equal(0L, seed.ExecuteScalar());
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    private sealed class RecoveryListener(Action? release) : TraceListener
    {
        public bool SawWait { get; private set; }
        public override void Write(string? message) => Observe(message);
        public override void WriteLine(string? message) => Observe(message);
        private void Observe(string? message)
        {
            if (SawWait || message?.Contains("Journal WAL recovery is waiting", StringComparison.Ordinal) != true) return;
            SawWait = true;
            release?.Invoke();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omnicore-wal-mapping-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(_root, "journal.db");
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Journal, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE seed(value TEXT);";
            command.ExecuteNonQuery();
        }
        public MappingLease MapWalIndex()
        {
            File.WriteAllBytes(Journal + "-shm", new byte[32768]);
            return new MappingLease(Journal + "-shm");
        }
        public void Dispose()
        {
            using var poolKey = new SqliteConnection("DataSource=" + Journal);
            SqliteConnection.ClearPool(poolKey);
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class MappingLease : IDisposable
    {
        private readonly FileStream _file;
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        public MappingLease(string path)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            _mapping = MemoryMappedFile.CreateFromFile(_file, null, 32768,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            _view = _mapping.CreateViewAccessor();
        }
        public void Dispose() { _view.Dispose(); _mapping.Dispose(); _file.Dispose(); }
    }
}
