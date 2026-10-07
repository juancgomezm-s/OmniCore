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
                actual_usd TEXT, receipt TEXT, created_utc TEXT NOT NULL, pending_usd TEXT);
            CREATE TABLE IF NOT EXISTS spend_reservation_scopes (
                id TEXT NOT NULL REFERENCES spend_reservations(id), scope TEXT NOT NULL,
                identity TEXT NOT NULL, PRIMARY KEY(id, scope, identity));
            """;
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var columns = Command(connection, transaction, "PRAGMA table_info(spend_reservations)");
        var hasPending = false;
        using (var reader = columns.ExecuteReader())
            while (reader.Read()) hasPending |= reader.GetString(1) == "pending_usd";
        if (!hasPending)
        {
            using var migration = Command(connection, transaction, "ALTER TABLE spend_reservations ADD COLUMN pending_usd TEXT");
            migration.ExecuteNonQuery();
        }
        using var initialize = Command(connection, transaction,
            "UPDATE spend_reservations SET pending_usd=CASE WHEN state IN ('settled','released') THEN '0' ELSE maximum_usd END WHERE pending_usd IS NULL");
        initialize.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Read canonical spend while holding the shared write transaction. The callback
    /// must be readonly, must not invoke providers, and must propagate unavailable evidence.
    /// Daily identity is stable across UTC dates so unresolved requests do not disappear at midnight.</summary>
    public Admission TryReserve(string id, decimal maximumUsd, Func<IReadOnlyList<Limit>> readLimits)
        => TryReserve(id, maximumUsd, readLimits, out _);

    public Admission TryReserve(string id, decimal maximumUsd, Func<IReadOnlyList<Limit>> readLimits, out string? blockedScope)
    {
        blockedScope = null;
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
                SELECT r.pending_usd,r.state FROM spend_reservations r JOIN spend_reservation_scopes s ON s.id=r.id
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
                    case "dispatched":
                    case "uncertain": pending = checked(pending + amount); break;
                    case "released":
                    case "settled": break;
                    default: throw new InvalidDataException("Unsupported reservation state.");
                }
            }
            if (checked(limit.SpentUsd + pending + maximumUsd) > limit.LimitUsd)
            {
                blockedScope = limit.Scope;
                return Admission.Insufficient;
            }
        }
        using (var insert = Command(connection, transaction,
            "INSERT INTO spend_reservations (id,maximum_usd,state,actual_usd,receipt,created_utc,pending_usd) VALUES($id,$maximum,'reserved',NULL,NULL,$created,$maximum)", id))
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
    /// workspace journal or canonical User qualification receipt store. Settled amounts are
    /// audit data, never added to canonical consumption; the caller verifies the receipt.</summary>
    public void Settle(string id, decimal actualUsd, string receipt)
    {
        if (actualUsd < 0m) throw new ArgumentOutOfRangeException(nameof(actualUsd));
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        Transition(id, "dispatched", "settled", actualUsd, receipt);
    }

    /// <summary>The final response is durable, but earlier sends have no usage receipts.
    /// Keep the unaccounted bound pending. This is not a claim those sends were billed.</summary>
    public void RecordUncertainCompletion(string id, decimal knownUsd, string receipt)
    {
        if (knownUsd < 0m) throw new ArgumentOutOfRangeException(nameof(knownUsd));
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        Transition(id, "dispatched", "uncertain", knownUsd, receipt);
    }

    /// <summary>Recover the receipt-before-settlement crash window. The caller has already
    /// verified immutable canonical evidence. A receipt without a reservation is historical
    /// consumption only; it must not manufacture a ledger entry. Unknown usage requires the
    /// original full dispatched bound to remain held.</summary>
    public bool ReconcileCanonicalReceipt(string id, decimal maximumUsd, decimal? knownUsd,
        string receipt, bool fullyAccounted)
    {
        if (maximumUsd < 0m || knownUsd < 0m) throw new ArgumentOutOfRangeException(nameof(maximumUsd));
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        return Transition(id, "dispatched", knownUsd is null ? "dispatched"
            : fullyAccounted ? "settled" : "uncertain", knownUsd,
            knownUsd is null ? null : receipt, maximumUsd, allowMissing: true);
    }

    /// <summary>Readonly proof used while admission holds the write lock. Unknown usage
    /// may contribute no measured amount only while its entire original bound is pending.</summary>
    public bool HasFullDispatchedBound(string id, decimal maximumUsd)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state,maximum_usd,pending_usd,actual_usd,receipt FROM spend_reservations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetString(0) == "dispatched"
            && Parse(reader.GetString(1)) == maximumUsd && Parse(reader.GetString(2)) == maximumUsd
            && reader.IsDBNull(3) && reader.IsDBNull(4);
    }

    private bool Transition(string id, string from, string to, decimal? actual, string? receipt,
        decimal? expectedMaximum = null, bool allowMissing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var lookup = Command(connection, transaction,
            "SELECT state,actual_usd,receipt,maximum_usd,pending_usd FROM spend_reservations WHERE id=$id", id);
        string state;
        string? previousAmount;
        string? previousReceipt;
        decimal maximum;
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read())
            {
                if (allowMissing) return false;
                throw new InvalidOperationException("Unknown reservation.");
            }
            state = reader.GetString(0);
            previousAmount = reader.IsDBNull(1) ? null : reader.GetString(1);
            previousReceipt = reader.IsDBNull(2) ? null : reader.GetString(2);
            maximum = Parse(reader.GetString(3));
            if (expectedMaximum is { } expected && maximum != expected)
                throw new InvalidDataException("Canonical receipt differs from reserved maximum.");
            if (expectedMaximum is not null && to == "dispatched" && actual is null && Parse(reader.GetString(4)) != maximum)
                throw new InvalidDataException("Unknown usage must retain its full dispatched bound.");
        }
        if (state == to)
        {
            if (previousAmount != (actual is null ? null : Format(actual.Value)) || previousReceipt != receipt)
                throw new InvalidDataException("Conflicting reservation settlement.");
            return true;
        }
        if (state != from) throw new InvalidOperationException("Illegal reservation transition.");
        using var update = Command(connection, transaction,
            "UPDATE spend_reservations SET state=$state,actual_usd=$actual,receipt=$receipt,pending_usd=$pending WHERE id=$id", id);
        update.Parameters.AddWithValue("$state", to);
        update.Parameters.AddWithValue("$actual", actual is null ? DBNull.Value : Format(actual.Value));
        update.Parameters.AddWithValue("$receipt", (object?)receipt ?? DBNull.Value);
        var pending = to switch
        {
            "released" or "settled" => 0m,
            "uncertain" when actual is { } known && known <= maximum => maximum - known,
            _ => maximum,
        };
        update.Parameters.AddWithValue("$pending", Format(pending));
        update.ExecuteNonQuery();
        transaction.Commit();
        return true;
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
