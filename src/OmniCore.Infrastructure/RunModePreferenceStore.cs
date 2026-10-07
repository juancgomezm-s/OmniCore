namespace OmniCore.Infrastructure;

using Microsoft.Data.Sqlite;
using OmniCore.Domain;

public sealed record RunModePreference(RunMode Mode, long Revision);

/// <summary>User-scoped default mode for newly created Runs. It never mutates an active Run.</summary>
public sealed class RunModePreferenceStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public RunModePreferenceStore(string userDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDatabasePath);
        var fullPath = Path.GetFullPath(userDatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Pooling = false,
        }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS user_run_mode_preference (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                mode TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 1)
            )
            """;
        command.ExecuteNonQuery();
    }

    public RunModePreference Read()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT mode, revision FROM user_run_mode_preference WHERE singleton = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(RunMode.Act, 0);
        var text = reader.GetString(0);
        if (!Enum.TryParse<RunMode>(text, ignoreCase: false, out var mode)
            || !Enum.IsDefined(mode) || reader.GetInt64(1) < 1)
            throw new InvalidDataException("Stored default Run mode is invalid.");
        return new(mode, reader.GetInt64(1));
    }

    public RunModePreference Set(RunMode mode, long expectedRevision)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        using var transaction = _connection.BeginTransaction();
        var current = ReadInTransaction(transaction);
        if (current.Revision != expectedRevision)
            throw new RunModePreferenceConflictException(expectedRevision, current.Revision);
        var next = new RunModePreference(mode, checked(current.Revision + 1));
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_run_mode_preference(singleton, mode, revision) VALUES(1, $mode, $revision)
            ON CONFLICT(singleton) DO UPDATE SET mode = excluded.mode, revision = excluded.revision
            """;
        command.Parameters.AddWithValue("$mode", next.Mode.ToString());
        command.Parameters.AddWithValue("$revision", next.Revision);
        command.ExecuteNonQuery();
        transaction.Commit();
        return next;
    }

    private RunModePreference ReadInTransaction(SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT mode, revision FROM user_run_mode_preference WHERE singleton = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(RunMode.Act, 0);
        var text = reader.GetString(0);
        var revision = reader.GetInt64(1);
        if (!Enum.TryParse<RunMode>(text, ignoreCase: false, out var mode) || !Enum.IsDefined(mode))
            throw new InvalidDataException("Stored default Run mode is invalid.");
        if (revision < 1) throw new InvalidDataException("Stored default Run mode revision is invalid.");
        return new(mode, revision);
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class RunModePreferenceConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException("Run mode preference revision conflict.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
