namespace OmniCore.Infrastructure;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Abstractions;

public sealed partial class SqliteModelQualificationStore
{
    internal const string ProbeReceiptSchemaMarker = "m55-qualification-probe-receipts-v1";
    private const string ReceiptColumns = "execution_id,probe_id,ordinal,reservation_id,key_hash,suite_id,suite_version,task_set_hash,started_utc,completed_utc,status,termination,billing_mode,maximum_usd,maximum_attempts,observed_sends,cost_usd,has_usage,usage_fields,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,reasoning_tokens,artifact_id,artifact_algorithm,artifact_hash,artifact_size,media_type,artifact_kind,sensitivity,redacted";

    /// <summary>Fresh readonly User receipt snapshot, without migrations or creation. A
    /// legacy namespace with no installed receipt schema has no per-probe receipts; profile
    /// artifacts are not retrospectively split into billable invocations or UTC days.</summary>
    public static IReadOnlyList<QualificationProbeReceipt> ReadCanonicalProbeReceipts(
        string dataDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(Path.GetFullPath(dataDirectory), "user.db");
        if (!File.Exists(path)) return [];
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Canonical User receipt database cannot be a link.");
        using var store = new SqliteModelQualificationStore(path, null, readOnly: true);
        using var command = store._conn.CreateCommand();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name='qualification_probe_receipts' COLLATE NOCASE";
        var receiptType = command.ExecuteScalar();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name='model_profile_migrations' COLLATE NOCASE";
        var markerType = command.ExecuteScalar();
        var installed = false;
        if (Equals(markerType, "table"))
        {
            command.CommandText = "SELECT COUNT(*) FROM model_profile_migrations WHERE name=:marker";
            Add(command, "marker", ProbeReceiptSchemaMarker);
            installed = Convert.ToInt64(command.ExecuteScalar()) != 0;
        }
        if (!installed && receiptType is null) return [];
        if (!installed || !Equals(receiptType, "table"))
            throw new InvalidDataException("Canonical User receipt schema is inconsistent.");
        return store.ProbeReceipts(cancellationToken);
    }

    private void InitializeProbeReceiptSchema(DbTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM model_profile_migrations WHERE name=:marker";
        Add(command, "marker", ProbeReceiptSchemaMarker);
        var installed = Convert.ToInt64(command.ExecuteScalar()) != 0;
        command.CommandText = "SELECT type FROM sqlite_master WHERE name='qualification_probe_receipts' COLLATE NOCASE";
        var type = command.ExecuteScalar();
        if (installed && !Equals(type, "table"))
            throw new InvalidDataException("Qualification probe receipt schema is missing after installation.");
        if (type is not null && !Equals(type, "table"))
            throw new InvalidDataException("Qualification receipt schema object must be a table.");
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS qualification_probe_receipts (
                execution_id TEXT NOT NULL, probe_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
                reservation_id TEXT NOT NULL UNIQUE, key_hash TEXT NOT NULL,
                suite_id TEXT NOT NULL, suite_version TEXT NOT NULL, task_set_hash TEXT NOT NULL,
                started_utc TEXT NOT NULL, completed_utc TEXT NOT NULL,
                status TEXT NOT NULL, termination TEXT NOT NULL, billing_mode TEXT NOT NULL,
                maximum_usd TEXT NOT NULL, maximum_attempts INTEGER NOT NULL, observed_sends INTEGER NOT NULL,
                cost_usd TEXT, has_usage INTEGER NOT NULL, usage_fields INTEGER NOT NULL,
                input_tokens INTEGER, output_tokens INTEGER, cache_read_tokens INTEGER,
                cache_write_tokens INTEGER, reasoning_tokens INTEGER,
                artifact_id TEXT NOT NULL, artifact_algorithm TEXT NOT NULL, artifact_hash TEXT NOT NULL,
                artifact_size INTEGER NOT NULL, media_type TEXT NOT NULL, artifact_kind TEXT NOT NULL,
                sensitivity TEXT NOT NULL, redacted INTEGER NOT NULL,
                PRIMARY KEY(execution_id,probe_id), UNIQUE(execution_id,ordinal))
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT " + ReceiptColumns + " FROM qualification_probe_receipts LIMIT 0";
        using (command.ExecuteReader()) { }
        if (!installed)
        {
            command.CommandText = "INSERT INTO model_profile_migrations(name,applied_at) VALUES(:marker,:now)";
            Add(command, "now", Iso(_clock()));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Synchronous publication and FULL User receipt commit under one GC
    /// lease. The callback publishes with the supplied store on this thread; it must
    /// not recursively acquire a lease or mutate this receipt store. CAS/header
    /// validation remains mandatory before commit.</summary>
    public QualificationProbeReceipt PublishProbeReceipt(QualificationProbeReceipt receipt,
        Func<IArtifactStore, ArtifactRef> publishEvidence, CancellationToken cancellationToken)
    {
        RequireUserReceipts();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(publishEvidence);
        var artifacts = new FileArtifactStore(_dataDirectory);
        using var lease = artifacts.AcquirePublicationLease(cancellationToken);
        receipt = receipt with { Evidence = publishEvidence(artifacts) };
        RecordProbeReceiptCore(receipt, cancellationToken);
        return NormalizeReceipt(receipt);
    }

    /// <summary>FULL commit of a CAS-backed receipt before reservation settlement. Exact
    /// duplicates are idempotent; conflicting identities never overwrite history.</summary>
    public void RecordProbeReceipt(QualificationProbeReceipt receipt, CancellationToken cancellationToken)
    {
        RequireUserReceipts();
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = ArtifactStoreLease.Acquire(_dataDirectory, cancellationToken);
        RecordProbeReceiptCore(receipt, cancellationToken);
    }

    private void RecordProbeReceiptCore(QualificationProbeReceipt receipt, CancellationToken cancellationToken)
    {
        RequireUserReceipts();
        cancellationToken.ThrowIfCancellationRequested();
        receipt = NormalizeReceipt(receipt);
        ValidateReceipt(receipt);
        VerifyReceiptBlob(receipt);
        using var transaction = ((SqliteConnection)_conn).BeginTransaction(deferred: false);
        using var lookup = _conn.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT " + ReceiptColumns + " FROM qualification_probe_receipts WHERE execution_id=:execution AND probe_id=:probe";
        Add(lookup, "execution", receipt.ExecutionId.ToString("D")); Add(lookup, "probe", receipt.ProbeId);
        using (var reader = lookup.ExecuteReader())
        {
            if (reader.Read())
            {
                if (ReadReceipt(reader) != receipt)
                    throw new InvalidDataException("Conflicting qualification probe receipt.");
                return;
            }
        }
        using var insert = _conn.CreateCommand();
        insert.Transaction = transaction;
        var values = ReceiptValues(receipt);
        var columns = ReceiptColumns.Split(',');
        insert.CommandText = "INSERT INTO qualification_probe_receipts (" + ReceiptColumns + ") VALUES ("
            + string.Join(',', columns.Select(column => ":" + column)) + ")";
        for (var i = 0; i < columns.Length; i++) Add(insert, columns[i], values[i] ?? DBNull.Value);
        insert.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    /// <summary>Read canonical observations once, independent of profile/stale copies.
    /// Every CAS reference is verified; unavailable evidence propagates, never becomes zero.</summary>
    public IReadOnlyList<QualificationProbeReceipt> ProbeReceipts(CancellationToken cancellationToken)
    {
        RequireUserReceipts();
        using var command = _conn.CreateCommand();
        command.CommandText = "SELECT " + ReceiptColumns + " FROM qualification_probe_receipts ORDER BY completed_utc,execution_id,ordinal";
        using var reader = command.ExecuteReader();
        var receipts = new List<QualificationProbeReceipt>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = ReadReceipt(reader);
            ValidateReceipt(receipt);
            VerifyReceiptBlob(receipt);
            receipts.Add(receipt);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return receipts.AsReadOnly();
    }

    private void RequireUserReceipts()
    {
        if (!_isUserDatabase) throw new InvalidOperationException("Qualification receipts require the User user.db namespace.");
    }
    private void VerifyReceiptBlob(QualificationProbeReceipt receipt)
    {
        var artifacts = new FileArtifactStore(_dataDirectory);
        if (!artifacts.Verify(receipt.Evidence.Hash, receipt.Evidence.Size))
            throw new InvalidDataException("Qualification receipt CAS evidence is missing or invalid.");
        var content = artifacts.GetText(receipt.Evidence.Hash)
            ?? throw new InvalidDataException("Qualification receipt CAS evidence is unavailable.");
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || root.EnumerateObject().Count(p => p.Name == "schema") != 1
                || root.EnumerateObject().Count(p => p.Name == "receipt") != 1
                || root.GetProperty("schema").ValueKind != System.Text.Json.JsonValueKind.String
                || root.GetProperty("schema").GetString() != QualificationProbeReceipt.EvidenceSchema
                || root.GetProperty("receipt").GetRawText() != receipt.CanonicalObservationJson())
                throw new InvalidDataException("Qualification receipt differs from its canonical CAS observation.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException("Qualification receipt CAS observation is malformed.", exception);
        }
    }
    private static QualificationProbeReceipt NormalizeReceipt(QualificationProbeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Usage is { } raw
            && TokenUsageValidation.IsInvalid(raw, receipt.ReportedUsageFields))
            throw new InvalidDataException("Qualification receipt contains inconsistent raw usage.");
        var fields = receipt.ReportedUsageFields;
        long Count(long value, TokenUsageFields field) => fields.HasFlag(field) ? value : 0;
        return receipt with
        {
            StartedAtUtc = receipt.StartedAtUtc.ToUniversalTime(), CompletedAtUtc = receipt.CompletedAtUtc.ToUniversalTime(),
            Usage = receipt.Usage is { } usage ? new TokenUsage(Count(usage.Input, TokenUsageFields.Input),
                Count(usage.Output, TokenUsageFields.Output), Count(usage.CacheRead, TokenUsageFields.CacheRead),
                Count(usage.CacheWrite, TokenUsageFields.CacheWrite), Count(usage.Reasoning, TokenUsageFields.Reasoning)) : null,
        };
    }
    private static void ValidateReceipt(QualificationProbeReceipt receipt)
    {
        foreach (var text in new[] { receipt.ProbeId, receipt.ReservationId, receipt.SuiteId, receipt.SuiteVersion })
            if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) throw new InvalidDataException("Invalid qualification receipt identity.");
        static bool Hash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (receipt.ExecutionId == Guid.Empty || receipt.Ordinal < 0 || !Hash(receipt.KeyHash) || !Hash(receipt.TaskSetHash)
            || receipt.CompletedAtUtc < receipt.StartedAtUtc || receipt.StartedAtUtc.Offset != TimeSpan.Zero
            || receipt.CompletedAtUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(receipt.Status)
            || receipt.Status == ProbeStatus.NotRun || !Enum.IsDefined(receipt.Termination) || !Enum.IsDefined(receipt.BillingMode)
            || receipt.MaximumUsd < 0 || receipt.MaximumGenerationAttempts <= 0 || receipt.ObservedGenerationSends < 0
            || receipt.CostUsd < 0 || (receipt.ReportedUsageFields & ~TokenUsageFields.All) != 0
            || receipt.Usage is null && receipt.ReportedUsageFields != TokenUsageFields.None
            || receipt.Termination != ProbeExecutionTermination.Completed && receipt.Status != ProbeStatus.Error
            || receipt.Termination == ProbeExecutionTermination.Completed && receipt.Status == ProbeStatus.Error)
            throw new InvalidDataException("Invalid qualification receipt observation.");
        if (receipt.CostUsd is not null && (receipt.Usage is null
            || (receipt.ReportedUsageFields & (TokenUsageFields.Input | TokenUsageFields.Output)) != (TokenUsageFields.Input | TokenUsageFields.Output)
            || TokenUsageValidation.IsInvalid(receipt.Usage, receipt.ReportedUsageFields)))
            throw new InvalidDataException("Qualification cost lacks consistent reported usage.");
        if (receipt.Usage is { } usage && TokenUsageValidation.IsInvalid(usage, receipt.ReportedUsageFields))
            throw new InvalidDataException("Qualification receipt contains inconsistent reported usage.");
        var artifact = receipt.Evidence;
        if (artifact is null || artifact.Id.Value == Guid.Empty || artifact.Hash.Algorithm != "sha256"
            || !Hash(artifact.Hash.Value) || artifact.Size < 0 || string.IsNullOrWhiteSpace(artifact.MediaType)
            || !Enum.IsDefined(artifact.Kind) || !Enum.IsDefined(artifact.Sensitivity))
            throw new InvalidDataException("Invalid qualification receipt CAS metadata.");
    }
    private static string Amount(decimal amount) => amount.ToString("G29", CultureInfo.InvariantCulture);
    private static decimal ReadAmount(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static object?[] ReceiptValues(QualificationProbeReceipt r)
    {
        object? Count(long? count, TokenUsageFields field) => r.ReportedUsageFields.HasFlag(field) ? count : null;
        return [r.ExecutionId.ToString("D"), r.ProbeId, r.Ordinal, r.ReservationId, r.KeyHash, r.SuiteId, r.SuiteVersion, r.TaskSetHash,
            Iso(r.StartedAtUtc), Iso(r.CompletedAtUtc), r.Status.ToString(), r.Termination.ToString(), r.BillingMode.ToString(),
            Amount(r.MaximumUsd), r.MaximumGenerationAttempts, r.ObservedGenerationSends,
            r.CostUsd is { } cost ? Amount(cost) : null, r.Usage is null ? 0 : 1, (long)r.ReportedUsageFields,
            Count(r.Usage?.Input, TokenUsageFields.Input), Count(r.Usage?.Output, TokenUsageFields.Output),
            Count(r.Usage?.CacheRead, TokenUsageFields.CacheRead), Count(r.Usage?.CacheWrite, TokenUsageFields.CacheWrite),
            Count(r.Usage?.Reasoning, TokenUsageFields.Reasoning), r.Evidence.Id.ToString(), r.Evidence.Hash.Algorithm,
            r.Evidence.Hash.Value, r.Evidence.Size, r.Evidence.MediaType, r.Evidence.Kind.ToString(), r.Evidence.Sensitivity.ToString(),
            r.Evidence.Redacted ? 1 : 0];
    }
    private static QualificationProbeReceipt ReadReceipt(DbDataReader reader)
    {
        static bool Boolean(long value) => value switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid receipt boolean.") };
        var hasUsage = Boolean(reader.GetInt64(17));
        var rawFields = reader.GetInt64(18);
        if (rawFields is < 0 or > (long)TokenUsageFields.All)
            throw new InvalidDataException("Invalid receipt usage mask.");
        var fields = (TokenUsageFields)rawFields;
        long Count(int index, TokenUsageFields field)
        {
            var expected = hasUsage && fields.HasFlag(field);
            if (expected == reader.IsDBNull(index)) throw new InvalidDataException("Receipt usage mask/count mismatch.");
            return expected ? reader.GetInt64(index) : 0;
        }
        var input = Count(19, TokenUsageFields.Input); var output = Count(20, TokenUsageFields.Output);
        var cacheRead = Count(21, TokenUsageFields.CacheRead); var cacheWrite = Count(22, TokenUsageFields.CacheWrite);
        var reasoning = Count(23, TokenUsageFields.Reasoning);
        return new(Guid.Parse(reader.GetString(0)), reader.GetString(1), checked((int)reader.GetInt64(2)), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), ParseIso(reader.GetString(8)),
            ParseIso(reader.GetString(9)), ReceiptEnum<ProbeStatus>(reader.GetString(10)), ReceiptEnum<ProbeExecutionTermination>(reader.GetString(11)),
            ReceiptEnum<BillingMode>(reader.GetString(12)), ReadAmount(reader.GetString(13)), reader.GetInt64(14), reader.GetInt64(15),
            reader.IsDBNull(16) ? null : ReadAmount(reader.GetString(16)), hasUsage
                ? new TokenUsage(input, output, cacheRead, cacheWrite, reasoning) : null, fields,
            new ArtifactRef(ArtifactId.Parse(reader.GetString(24)), new ContentHash(reader.GetString(25), reader.GetString(26)),
                reader.GetInt64(27), reader.GetString(28), ReceiptEnum<ArtifactKind>(reader.GetString(29)),
                ReceiptEnum<Sensitivity>(reader.GetString(30)), Boolean(reader.GetInt64(31))));
    }
    private static T ReceiptEnum<T>(string text) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(text, out var value) || !Enum.IsDefined(value) || value.ToString() != text)
            throw new InvalidDataException("Invalid canonical receipt enum.");
        return value;
    }
}
