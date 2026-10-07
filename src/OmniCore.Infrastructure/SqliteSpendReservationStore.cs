namespace OmniCore.Infrastructure;

using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>One shared User ledger for pending monetary admission, not a second usage journal.
/// No automatic expiry: a dispatched request may have consumed money even without a response.</summary>
public sealed class SqliteSpendReservationStore
{
    public sealed record Limit(string Scope, string Identity, decimal LimitUsd, decimal SpentUsd);
    public enum Admission { Reserved, AlreadyExists, Insufficient }
    private readonly string _path;

    public SqliteSpendReservationStore(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS spend_reservations (
                id TEXT PRIMARY KEY, maximum_usd TEXT NOT NULL, state TEXT NOT NULL,
                actual_usd TEXT, receipt TEXT, created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS spend_reservation_scopes (
                id TEXT NOT NULL REFERENCES spend_reservations(id), scope TEXT NOT NULL,
                identity TEXT NOT NULL, PRIMARY KEY(id, scope, identity));
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Read canonical spend while holding the shared write transaction. The callback
    /// must be readonly, must not invoke providers, and must propagate unavailable evidence.
    /// Daily identity is stable across UTC dates so unresolved requests do not disappear at midnight.</summary>
    public Admission TryReserve(string id, decimal maximumUsd, Func<IReadOnlyList<Limit>> readLimits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (maximumUsd < 0m) throw new ArgumentOutOfRangeException(nameof(maximumUsd));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var existing = Command(connection, transaction, "SELECT maximum_usd FROM spend_reservations WHERE id=$id", id))
        {
            if (existing.ExecuteScalar() is string amount)
            {
                if (Parse(amount) != maximumUsd) throw new InvalidDataException("Reservation identity reused with another bound.");
                return Admission.AlreadyExists; // Never authorize a second dispatch, even after settlement.
            }
        }
        var limits = readLimits().ToArray();
        if (limits.Length == 0) throw new ArgumentException("Capped admission needs at least one scope.");
        if (limits.Select(item => (item.Scope, item.Identity)).Distinct().Count() != limits.Length)
            throw new ArgumentException("Duplicate reservation scope.");
        foreach (var limit in limits)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(limit.Scope);
            ArgumentException.ThrowIfNullOrWhiteSpace(limit.Identity);
            if (limit.LimitUsd < 0m || limit.SpentUsd < 0m) throw new ArgumentOutOfRangeException(nameof(readLimits));
            decimal pending = 0m;
            using var command = Command(connection, transaction, """
                SELECT r.maximum_usd,r.state FROM spend_reservations r JOIN spend_reservation_scopes s ON s.id=r.id
                WHERE s.scope=$scope AND s.identity=$identity
                """);
            command.Parameters.AddWithValue("$scope", limit.Scope);
            command.Parameters.AddWithValue("$identity", limit.Identity);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var amount = Parse(reader.GetString(0));
                if (amount < 0m) throw new InvalidDataException("Invalid reservation amount.");
                switch (reader.GetString(1))
                {
                    case "reserved":
                    case "dispatched": pending = checked(pending + amount); break;
                    case "released":
                    case "settled": break;
                    default: throw new InvalidDataException("Unsupported reservation state.");
                }
            }
            if (checked(limit.SpentUsd + pending + maximumUsd) > limit.LimitUsd) return Admission.Insufficient;
        }
        using (var insert = Command(connection, transaction,
            "INSERT INTO spend_reservations VALUES($id,$maximum,'reserved',NULL,NULL,$created)", id))
        {
            insert.Parameters.AddWithValue("$maximum", Format(maximumUsd));
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
        foreach (var limit in limits)
        {
            using var insert = Command(connection, transaction,
                "INSERT INTO spend_reservation_scopes VALUES($id,$scope,$identity)", id);
            insert.Parameters.AddWithValue("$scope", limit.Scope);
            insert.Parameters.AddWithValue("$identity", limit.Identity);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return Admission.Reserved;
    }

    /// <summary>Durable before any network request. A crash afterwards leaves the bound held.</summary>
    public void MarkDispatched(string id) => Transition(id, "reserved", "dispatched", null, null);
    /// <summary>Only a known not-dispatched reservation can be released.</summary>
    public void ReleaseBeforeDispatch(string id) => Transition(id, "reserved", "released", null, null);
    /// <summary>Call only after the matching completed usage receipt is durable in the canonical
    /// workspace journal. Settled amounts are audit data, never added to canonical consumption.</summary>
    public void Settle(string id, decimal actualUsd, string receipt)
    {
        if (actualUsd < 0m) throw new ArgumentOutOfRangeException(nameof(actualUsd));
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        Transition(id, "dispatched", "settled", actualUsd, receipt);
    }

    private void Transition(string id, string from, string to, decimal? actual, string? receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var lookup = Command(connection, transaction,
            "SELECT state,actual_usd,receipt FROM spend_reservations WHERE id=$id", id);
        string state;
        string? previousAmount;
        string? previousReceipt;
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Unknown reservation.");
            state = reader.GetString(0);
            previousAmount = reader.IsDBNull(1) ? null : reader.GetString(1);
            previousReceipt = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        if (state == to)
        {
            if (previousAmount != (actual is null ? null : Format(actual.Value)) || previousReceipt != receipt)
                throw new InvalidDataException("Conflicting reservation settlement.");
            return;
        }
        if (state != from) throw new InvalidOperationException("Illegal reservation transition.");
        using var update = Command(connection, transaction,
            "UPDATE spend_reservations SET state=$state,actual_usd=$actual,receipt=$receipt WHERE id=$id", id);
        update.Parameters.AddWithValue("$state", to);
        update.Parameters.AddWithValue("$actual", actual is null ? DBNull.Value : Format(actual.Value));
        update.Parameters.AddWithValue("$receipt", (object?)receipt ?? DBNull.Value);
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _path, Pooling = false, DefaultTimeout = 10 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, string? id = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        return command;
    }
    private static string Format(decimal amount) => amount.ToString("G29", CultureInfo.InvariantCulture);
    private static decimal Parse(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);
}
