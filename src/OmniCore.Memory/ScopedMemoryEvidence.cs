namespace OmniCore.Memory;

using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using OmniCore.Domain;

/// <summary>Hash-only evidence, one vote per human Session. Never accepts model-supplied evidence refs.</summary>
public sealed record ScopedMemoryEvidence(IReadOnlyList<string> SessionRefs, bool Forgotten)
{
    public bool Eligible => !Forgotten && SessionRefs.Count >= 3;
    public bool Matches(IReadOnlyList<string> references) => Eligible && references.Count == 3
        && references.Distinct(StringComparer.Ordinal).Count() == 3
        && references.All(reference => SessionRefs.Contains(reference, StringComparer.Ordinal));
}

public sealed partial class ScopedMemoryStore
{
    /// <summary>Host calls only after a durable, privacy-screened, human-originated proposal.
    /// Three distinct Sessions are stricter than the ADR minimum of two Sessions.</summary>
    public ScopedMemoryEvidence ObserveUserEvidence(MemoryScope scope, MemoryKind kind, string content,
        SessionId session, CancellationToken cancellationToken)
    {
        if (scope.Level is not (ScopeLevel.Workspace or ScopeLevel.Project)) return new([], false);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(PathFor(scope), create: true); EnsureTable(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadEvidence(connection, transaction, scope, kind, content);
        if (!current.Forgotten && current.SessionRefs.Count < 32)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO scoped_memory_evidence (scope_level,scope_id,content_hash,session_id) "
                + "SELECT $level,$scope,$hash,$session WHERE (SELECT COUNT(*) FROM scoped_memory_evidence WHERE scope_level=$level AND scope_id=$scope)<3000";
            AddScopeParameters(command, scope); command.Parameters.AddWithValue("$hash", EvidenceHash(kind, content));
            command.Parameters.AddWithValue("$session", session.ToString()); command.ExecuteNonQuery();
        }
        var result = ReadEvidence(connection, transaction, scope, kind, content);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return result;
    }

    private static void EnsureEvidenceTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS scoped_memory_evidence (scope_level TEXT NOT NULL,scope_id TEXT NOT NULL,content_hash TEXT NOT NULL,session_id TEXT NOT NULL,PRIMARY KEY(scope_level,scope_id,content_hash,session_id));"
            + "CREATE TABLE IF NOT EXISTS scoped_memory_forgotten (scope_level TEXT NOT NULL,scope_id TEXT NOT NULL,content_hash TEXT NOT NULL,PRIMARY KEY(scope_level,scope_id,content_hash))";
        command.ExecuteNonQuery();
    }

    private static ScopedMemoryEvidence ReadEvidence(SqliteConnection connection, SqliteTransaction transaction,
        MemoryScope scope, MemoryKind kind, string content)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        AddScopeParameters(command, scope); command.Parameters.AddWithValue("$hash", EvidenceHash(kind, content));
        command.CommandText = "SELECT COUNT(*) FROM scoped_memory_forgotten WHERE scope_level=$level AND scope_id=$scope AND content_hash=$hash";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0) return new([], true);
        command.CommandText = "SELECT session_id FROM scoped_memory_evidence WHERE scope_level=$level AND scope_id=$scope AND content_hash=$hash ORDER BY session_id LIMIT 32";
        using var reader = command.ExecuteReader(); List<string> sessions = [];
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidDataException("Invalid memory evidence Session.");
            sessions.Add("user-session:" + id);
        }
        return new(sessions, false);
    }

    private static void TombstoneEvidence(SqliteConnection connection, SqliteTransaction transaction,
        MemoryScope scope, MemoryKind kind, string content)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        AddScopeParameters(command, scope); command.Parameters.AddWithValue("$hash", EvidenceHash(kind, content));
        // At most one tombstone per successful forget, bounded by the same operation ledger.
        command.CommandText = "INSERT OR IGNORE INTO scoped_memory_forgotten VALUES($level,$scope,$hash);"
            + "DELETE FROM scoped_memory_evidence WHERE scope_level=$level AND scope_id=$scope AND content_hash=$hash";
        command.ExecuteNonQuery();
    }

    private static string EvidenceHash(MemoryKind kind, string content) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + Normalize(content))));
}
