namespace OmniCore.Infrastructure;

using Microsoft.Data.Sqlite;
using OmniCore.Domain;

/// <summary>Revisioned User default. No row means no user preference; a row with null request is explicit off.</summary>
public sealed record UserReasoningPreference(bool HasSelection, ReasoningRequest? Request, long Revision);

/// <summary>Persists a user-selected provider-neutral request without guessing a provider dialect.</summary>
public sealed class ReasoningPreferenceStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public ReasoningPreferenceStore(string userDatabasePath)
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
            CREATE TABLE IF NOT EXISTS user_reasoning_preference (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                has_selection INTEGER NOT NULL CHECK(has_selection IN (0, 1)),
                kind TEXT NULL,
                budget_tokens INTEGER NULL,
                revision INTEGER NOT NULL CHECK(revision >= 1),
                CHECK((has_selection = 0 AND kind IS NULL AND budget_tokens IS NULL)
                    OR (has_selection = 1 AND (kind IS NULL OR length(trim(kind)) > 0)))
            )
            """;
        command.ExecuteNonQuery();
    }

    public UserReasoningPreference Read()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT has_selection, kind, budget_tokens, revision FROM user_reasoning_preference WHERE singleton = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(false, null, 0);
        var hasSelection = reader.GetInt64(0) == 1;
        var kind = reader.IsDBNull(1) ? null : reader.GetString(1);
        int? budget = reader.IsDBNull(2) ? null : checked((int)reader.GetInt64(2));
        var revision = reader.GetInt64(3);
        if (revision < 1) throw new InvalidDataException("Stored User reasoning preference is malformed.");
        var request = ReadStoredRequest(hasSelection, kind, budget);
        return new(hasSelection, request, revision);
    }

    public UserReasoningPreference Set(ReasoningRequest? request, long expectedRevision)
    {
        Validate(request);
        return Write(hasSelection: true, request, expectedRevision);
    }

    public UserReasoningPreference Reset(long expectedRevision) => Write(false, null, expectedRevision);

    private UserReasoningPreference Write(bool hasSelection, ReasoningRequest? request, long expectedRevision)
    {
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        using var transaction = _connection.BeginTransaction();
        var current = ReadInTransaction(transaction);
        if (current.Revision != expectedRevision)
            throw new ReasoningPreferenceConflictException(expectedRevision, current.Revision);
        var nextRevision = checked(current.Revision + 1);
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_reasoning_preference(singleton, has_selection, kind, budget_tokens, revision)
            VALUES(1, $selected, $kind, $budget, $revision)
            ON CONFLICT(singleton) DO UPDATE SET has_selection = excluded.has_selection,
                kind = excluded.kind, budget_tokens = excluded.budget_tokens, revision = excluded.revision
            """;
        command.Parameters.AddWithValue("$selected", hasSelection ? 1 : 0);
        command.Parameters.AddWithValue("$kind", (object?)request?.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$budget", request?.BudgetTokens is { } budget ? budget : DBNull.Value);
        command.Parameters.AddWithValue("$revision", nextRevision);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new(hasSelection, request, nextRevision);
    }

    private UserReasoningPreference ReadInTransaction(SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT has_selection, kind, budget_tokens, revision FROM user_reasoning_preference WHERE singleton = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(false, null, 0);
        var selected = reader.GetInt64(0) == 1;
        var kind = reader.IsDBNull(1) ? null : reader.GetString(1);
        int? budget = reader.IsDBNull(2) ? null : checked((int)reader.GetInt64(2));
        var revision = reader.GetInt64(3);
        if (revision < 1) throw new InvalidDataException("Stored User reasoning preference is malformed.");
        var request = ReadStoredRequest(selected, kind, budget);
        return new(selected, request, revision);
    }

    private static ReasoningRequest? ReadStoredRequest(bool hasSelection, string? kind, int? budget)
    {
        if ((!hasSelection && (kind is not null || budget is not null))
            || (kind is null && budget is not null)
            || (hasSelection && kind is { Length: 0 }))
            throw new InvalidDataException("Stored User reasoning preference is malformed.");
        var request = hasSelection && kind is not null ? new ReasoningRequest(kind, budget) : null;
        try { Validate(request); }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Stored User reasoning preference is malformed.", exception);
        }
        return request;
    }

    private static void Validate(ReasoningRequest? request)
    {
        if (request is null) return;
        if (string.IsNullOrWhiteSpace(request.Kind)
            || (request.Kind == "budget" && request.BudgetTokens is null or < 1024)
            || (request.Kind != "budget" && request.BudgetTokens is not null))
            throw new ArgumentException("Reasoning preference must be a non-empty provider label with a valid explicit budget.", nameof(request));
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class ReasoningPreferenceConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException("User reasoning preference revision conflict.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
