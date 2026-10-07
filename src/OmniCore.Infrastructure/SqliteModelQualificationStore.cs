namespace OmniCore.Infrastructure;

using System.Data.Common;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Store relacional de perfiles de cualificación empírica en `user.db` (ADR-0007 §6, M5).
/// Resolución por clave exacta con columnas tipadas: ninguna consulta vectorial participa en una
/// decisión de cualificación. Concasión optimista por `profile_revision` (equivalente de
/// ADR-0044 §9 para M5): Upsert/MarkStale exigen la revisión vigente y jamás pisan cambios
/// concurrentes. `UpsertWithTraits` confirma perfil y traits como unidad indivisible.
/// `MarkStale` no destruye la evidencia: solo cambia el estado a Stale y registra
/// la versión que lo volvió Stale en `stale_by_suite_version`, conservando suite_version (la suite
/// que produjo el perfil), key_json y el historial de traits (ADR-0007 §4).
/// </summary>
public sealed partial class SqliteModelQualificationStore : IModelQualificationStore, IModelQualificationEvidenceStore, IDisposable
{
    private readonly DbConnection _conn;

    private readonly Func<DateTimeOffset> _clock;
    private readonly string _dataDirectory;
    private readonly bool _isUserDatabase;

    public void Dispose()
    {
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }

    public SqliteModelQualificationStore(string databasePath) : this(databasePath, null)
    {
    }

    /// <summary>Reloj inyectable para tests deterministas.</summary>
    public SqliteModelQualificationStore(string databasePath, Func<DateTimeOffset>? clock)
    {
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _dataDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        _isUserDatabase = Path.GetFileName(Path.GetFullPath(databasePath)) == "user.db";
        var parent = Path.GetDirectoryName(databasePath);
        if (parent is not null && parent!.Length > 0 && !Directory.Exists(parent!))
        {
            Directory.CreateDirectory(parent!);
        }

        var connString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Pooling = false,
        }.ToString();
        _conn = Microsoft.Data.Sqlite.SqliteFactory.Instance!.CreateDataSource(connString)!.OpenConnection()!;
        try { Exec("PRAGMA synchronous=FULL"); InitializeSchema(); }
        catch { _conn.Dispose(); throw; }
    }

    public ModelQualificationProfile? Get(ModelQualificationKey key, CancellationToken cancellationToken)
        => GetCore(key, cancellationToken, null);

    private ModelQualificationProfile? GetCore(ModelQualificationKey key,
        CancellationToken cancellationToken, DbTransaction? transaction)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT * FROM model_profiles WHERE key_hash = :h";
        Add(cmd, "h", key.QualificationKeyHash());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public IReadOnlyList<ModelQualificationProfile> List(CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM model_profiles ORDER BY created_at, key_hash";
        using var reader = cmd.ExecuteReader();
        var result = new List<ModelQualificationProfile>();
        while (reader.Read())
        {
            result.Add(ReadRow(reader));
        }

        return result;
    }

    public ModelQualificationProfile Upsert(ModelQualificationKey key, long expectedRevision,
        ModelQualificationState state, string suiteId, string suiteVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var tx = _conn.BeginTransaction();
        var profile = UpsertCore(key, expectedRevision, state, suiteId, suiteVersion, cancellationToken, tx);
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
        return profile;
    }

    public ModelQualificationProfile UpsertWithTraits(ModelQualificationKey key, long expectedRevision,
        ModelQualificationState state, string suiteId, string suiteVersion,
        IReadOnlyList<ModelTraitRecord> traits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revision = checked(expectedRevision + 1);
        var keyHash = key.QualificationKeyHash();
        ValidateTraits(keyHash, revision, traits);
        using var tx = _conn.BeginTransaction();
        var profile = UpsertCore(key, expectedRevision, state, suiteId, suiteVersion, cancellationToken, tx);
        SaveTraitsCore(keyHash, profile.ProfileRevision, traits, cancellationToken, tx);
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
        return profile;
    }

    private ModelQualificationProfile UpsertCore(ModelQualificationKey key, long expectedRevision,
        ModelQualificationState state, string suiteId, string suiteVersion,
        CancellationToken cancellationToken, DbTransaction tx)
    {
        var keyHash = key.QualificationKeyHash();
        // Read and revision check occur under the same write transaction as the update.
        var existing = GetCore(key, cancellationToken, tx);
        var now = _clock();

        if (existing is null)
        {
            if (expectedRevision != 0)
            {
                throw new ModelQualificationRevisionConflictException(expectedRevision, 0);
            }

            using (var insert = _conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO model_profiles (
                        key_hash, key_json, state, profile_revision, suite_id, suite_version,
                        created_at, updated_at)
                    VALUES (:h, :kj, :st, 1, :sid, :sver, :now, :now)
                """;
                AddProfileParams(insert, key, keyHash, state, suiteId, suiteVersion, now);
                insert.ExecuteNonQuery();
            }

            return new ModelQualificationProfile(key, state, 1, suiteId, suiteVersion, now, now);
        }

        if (existing!.ProfileRevision != expectedRevision)
        {
            throw new ModelQualificationRevisionConflictException(expectedRevision, existing!.ProfileRevision);
        }

        var updated = new ModelQualificationProfile(key, state, checked(existing!.ProfileRevision + 1), suiteId,
            suiteVersion, existing!.CreatedAt, now);
        using (var update = _conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE model_profiles SET
                    state = :st, profile_revision = :rev, suite_id = :sid, suite_version = :sver,
                    updated_at = :now, stale_by_suite_version = NULL, stale_reason = NULL
                WHERE key_hash = :h AND profile_revision = :expected
            """;
            Add(update, "st", state.ToString());
            Add(update, "rev", updated.ProfileRevision);
            Add(update, "sid", suiteId);
            Add(update, "sver", suiteVersion);
            Add(update, "now", Iso(now));
            Add(update, "h", keyHash);
            Add(update, "expected", existing!.ProfileRevision);
            var rows = update.ExecuteNonQuery();
            if (rows == 0)
            {
                var current = GetCore(key, cancellationToken, tx);
                throw new ModelQualificationRevisionConflictException(existing!.ProfileRevision,
                    current?.ProfileRevision ?? 0);
            }
        }

        return updated;
    }

    public ModelQualificationProfile MarkStale(ModelQualificationKey key, long expectedRevision,
        string newSuiteVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keyHash = key.QualificationKeyHash();
        // Read, revision check, state transition and carried evidence share one transaction.
        using var tx = _conn.BeginTransaction();
        var existing = GetCore(key, cancellationToken, tx);
        if (existing is null)
        {
            throw new ModelQualificationRevisionConflictException(expectedRevision, 0);
        }

        if (existing!.ProfileRevision != expectedRevision)
        {
            throw new ModelQualificationRevisionConflictException(expectedRevision, existing!.ProfileRevision);
        }

        if (existing.State is not (ModelQualificationState.Qualified or ModelQualificationState.Calibrated))
        {
            throw new ModelQualificationStaleException(
                "MarkStale solo aplica sobre perfiles Qualified o Calibrated; estado actual: " + existing.State);
        }

        var newVersionParts = ParseVersionParts(newSuiteVersion);
        var currentVersionParts = ParseVersionParts(existing.SuiteVersion);
        if (newVersionParts[0] <= currentVersionParts[0])
        {
            throw new ModelQualificationStaleException(
                "MarkStale solo aplica ante una versión mayor nueva de la misma suite: actual "
                + existing.SuiteVersion + ", nueva " + newSuiteVersion);
        }

        var nextRevision = checked(existing.ProfileRevision + 1);
        var now = _clock();
        using (var update = _conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE model_profiles SET
                    state = :st, profile_revision = :rev, stale_by_suite_version = :stalever, updated_at = :now
                WHERE key_hash = :h AND profile_revision = :expected
            """;
            Add(update, "st", ModelQualificationState.Stale.ToString());
            Add(update, "rev", nextRevision);
            Add(update, "stalever", newSuiteVersion);
            Add(update, "now", Iso(now));
            Add(update, "h", keyHash);
            Add(update, "expected", existing!.ProfileRevision);
            var rows = update.ExecuteNonQuery();
            if (rows == 0)
            {
                var current = GetCore(key, cancellationToken, tx);
                throw new ModelQualificationRevisionConflictException(existing!.ProfileRevision,
                    current?.ProfileRevision ?? 0);
            }
        }

        // The original measurements remain historical; expose the same evidence at the
        // new profile revision because runtime snapshots resolve traits by current revision.
        using (var traits = _conn.CreateCommand())
        {
            traits.Transaction = tx;
            traits.CommandText = """
                INSERT INTO model_traits (key_hash, profile_revision, trait, value, confidence, samples, source)
                SELECT key_hash, :next, trait, value, confidence, samples, source FROM model_traits
                WHERE key_hash = :h AND profile_revision = :previous
                """;
            Add(traits, "next", nextRevision);
            Add(traits, "h", keyHash);
            Add(traits, "previous", existing.ProfileRevision);
            traits.ExecuteNonQuery();
        }
        CopyEvidence(keyHash, existing.ProfileRevision, nextRevision, tx);
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
        return new ModelQualificationProfile(key, ModelQualificationState.Stale, nextRevision,
            existing.SuiteId, existing.SuiteVersion, newSuiteVersion, existing.CreatedAt, now,
            existing.StaleReason);
    }

    private static int[] ParseVersionParts(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            throw new ArgumentException("Versión de suite inválida (se esperan al menos mayor.minor): " + version);
        }

        return [major, minor];
    }

    public IReadOnlyList<ModelTraitRecord> Traits(ModelQualificationKey key, long profileRevision,
        CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT key_hash, profile_revision, trait, value, confidence, samples, source
            FROM model_traits WHERE key_hash = :h AND profile_revision = :rev ORDER BY trait
        """;
        Add(cmd, "h", key.QualificationKeyHash());
        Add(cmd, "rev", profileRevision);
        using var reader = cmd.ExecuteReader();
        var result = new List<ModelTraitRecord>();
        while (reader.Read())
        {
            result.Add(new ModelTraitRecord(
                Text(reader, 0), reader.GetInt64(1), Text(reader, 2),
                reader.GetDouble(3), reader.GetDouble(4), (int)reader.GetInt64(5), Text(reader, 6)));
        }

        return result;
    }

    public void SaveTraits(ModelQualificationKey key, long profileRevision,
        IReadOnlyList<ModelTraitRecord> traits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keyHash = key.QualificationKeyHash();
        ValidateTraits(keyHash, profileRevision, traits);
        using var tx = _conn.BeginTransaction();
        SaveTraitsCore(keyHash, profileRevision, traits, cancellationToken, tx);
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
    }

    private static void ValidateTraits(string keyHash, long profileRevision,
        IReadOnlyList<ModelTraitRecord> traits)
    {
        ArgumentNullException.ThrowIfNull(traits);
        // Validación cerrada ANTES de tocar la base de datos:
        // cada trait debe tener KeyHash y ProfileRevision correctos.
        foreach (var t in traits)
        {
            if (t.KeyHash != keyHash)
            {
                throw new ArgumentException("ModelTraitRecord.KeyHash no coincide con la clave del perfil",
                    nameof(traits));
            }

            if (t.ProfileRevision != profileRevision)
            {
                throw new ArgumentException(
                    $"ModelTraitRecord.ProfileRevision ({t.ProfileRevision}) no coincide con la revisión solicitada ({profileRevision})",
                    nameof(traits));
            }
        }
    }

    private void SaveTraitsCore(string keyHash, long profileRevision,
        IReadOnlyList<ModelTraitRecord> traits, CancellationToken cancellationToken, DbTransaction tx)
    {
        // Verificación y reemplazo son atómicos dentro de la transacción: la revisión vigente
        // se revalida al reemplazar, para que un perfil subido por otro cliente entre la lectura
        // inicial y el DELETE/INSERT no pise traits de una revisión obsoleta.
        using (var check = _conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT profile_revision FROM model_profiles WHERE key_hash = :h";
            Add(check, "h", keyHash);
            using var reader = check.ExecuteReader();
            if (!reader.Read())
            {
                throw new ModelQualificationRevisionConflictException(profileRevision, 0);
            }

            var currentRevision = reader.GetInt64(0);
            if (currentRevision != profileRevision)
            {
                throw new ModelQualificationRevisionConflictException(profileRevision, currentRevision);
            }
        }

        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM model_traits WHERE key_hash = :h AND profile_revision = :rev";
            Add(del, "h", keyHash);
            Add(del, "rev", profileRevision);
            del.ExecuteNonQuery();
        }

        foreach (var t in traits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var insert = _conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO model_traits (key_hash, profile_revision, trait, value, confidence, samples, source)
                VALUES (:h, :rev, :trait, :val, :conf, :smp, :src)
            """;
            Add(insert, "h", keyHash);
            Add(insert, "rev", profileRevision);
            Add(insert, "trait", t.Trait);
            Add(insert, "val", t.Value);
            Add(insert, "conf", t.Confidence);
            Add(insert, "smp", (long)t.Samples);
            Add(insert, "src", t.Source);
            insert.ExecuteNonQuery();
        }
    }

    private void InitializeSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS model_profiles (
                key_hash TEXT PRIMARY KEY,
                key_json TEXT NOT NULL,
                state TEXT NOT NULL,
                profile_revision INTEGER NOT NULL,
                suite_id TEXT NOT NULL,
                suite_version TEXT NOT NULL,
                stale_by_suite_version TEXT,
                stale_reason TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
        """);
        // Migración compatible para bases anteriores a la columna stale_by_suite_version.
        if (!ColumnExists("model_profiles", "stale_by_suite_version"))
        {
            Exec("ALTER TABLE model_profiles ADD COLUMN stale_by_suite_version TEXT");
        }
        if (!ColumnExists("model_profiles", "stale_reason"))
            Exec("ALTER TABLE model_profiles ADD COLUMN stale_reason TEXT");
        Exec("""
            CREATE TABLE IF NOT EXISTS model_traits (
                key_hash TEXT NOT NULL,
                profile_revision INTEGER NOT NULL,
                trait TEXT NOT NULL,
                value REAL NOT NULL,
                confidence REAL NOT NULL,
                samples INTEGER NOT NULL,
                source TEXT NOT NULL
            )
        """);
        Exec("CREATE INDEX IF NOT EXISTS ix_model_traits_key ON model_traits (key_hash, profile_revision)");
        Exec("CREATE TABLE IF NOT EXISTS model_profile_migrations (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL)");
        using var migrationTransaction = _conn.BeginTransaction();
        RepairSuiteStaleTraitCopies(migrationTransaction);
        MigrateLegacyRouteProfiles(migrationTransaction);
        InitializeEvidenceSchema(migrationTransaction);
        InitializeProbeReceiptSchema(migrationTransaction);
        migrationTransaction.Commit();
    }

    private void RepairSuiteStaleTraitCopies(DbTransaction transaction)
    {
        // Older MarkStale retained the measurements only at revision-1. Materialize
        // them at the current revision without inventing values or changing profile identity.
        // Never merge into an existing current set or search arbitrary older revisions.
        // Run before route migration so its next revision carries the repaired measurements.
        const string migration = "m5-suite-stale-trait-copy-v1";
        using (var check = _conn.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM model_profile_migrations WHERE name = :name";
            Add(check, "name", migration);
            if (Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }
        using (var repair = _conn.CreateCommand())
        {
            repair.Transaction = transaction;
            repair.CommandText = """
            INSERT INTO model_traits (key_hash, profile_revision, trait, value, confidence, samples, source)
            SELECT previous.key_hash, profile.profile_revision, previous.trait, previous.value,
                previous.confidence, previous.samples, previous.source
            FROM model_profiles AS profile
            JOIN model_traits AS previous ON previous.key_hash = profile.key_hash
                AND previous.profile_revision = profile.profile_revision - 1
            WHERE profile.state = 'Stale' AND profile.stale_by_suite_version IS NOT NULL
                AND profile.stale_reason IS NULL AND profile.profile_revision > 1
                AND NOT EXISTS (SELECT 1 FROM model_traits AS current
                    WHERE current.key_hash = profile.key_hash
                        AND current.profile_revision = profile.profile_revision)
            """;
            repair.ExecuteNonQuery();
        }
        using (var marker = _conn.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = "INSERT INTO model_profile_migrations (name, applied_at) VALUES (:name, :now)";
            Add(marker, "name", migration);
            Add(marker, "now", Iso(_clock()));
            marker.ExecuteNonQuery();
        }
    }

    private void MigrateLegacyRouteProfiles(DbTransaction tx)
    {
        const string migration = "adr0046-route-qualification-v1";
        using (var check = _conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM model_profile_migrations WHERE name = :name";
            Add(check, "name", migration);
            if (Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }
        var legacy = new List<(string Hash, long Revision)>();
        using (var select = _conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT key_hash, key_json, profile_revision, state FROM model_profiles";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                using var json = JsonDocument.Parse(reader.GetString(1));
                var key = json.RootElement;
                if (OptionalRouteString(key, "endpoint") is null && OptionalRouteString(key, "protocol") is null &&
                    OptionalRouteString(key, "runtimeBuild") is null && reader.GetString(3) != "Stale")
                {
                    var parsed = ModelQualificationKeyFromJson(reader.GetString(1));
                    if (parsed.QualificationKeyHash() != reader.GetString(0) || parsed.CanonicalJson() != reader.GetString(1))
                        throw new InvalidDataException("No se puede migrar una clave de cualificación corrupta.");
                    legacy.Add((reader.GetString(0), reader.GetInt64(2)));
                }
            }
        }
        var now = Iso(_clock());
        foreach (var (hash, revision) in legacy)
        {
            using var update = _conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE model_profiles SET state = 'Stale', profile_revision = :next,
                    stale_reason = 'route-identity-migration', updated_at = :now
                WHERE key_hash = :h AND profile_revision = :rev
                """;
            Add(update, "next", checked(revision + 1)); Add(update, "now", now);
            Add(update, "h", hash); Add(update, "rev", revision);
            update.ExecuteNonQuery();
            // Preserve the historical traits and expose a copy under the new profile revision.
            using var traits = _conn.CreateCommand();
            traits.Transaction = tx;
            traits.CommandText = """
                INSERT INTO model_traits (key_hash, profile_revision, trait, value, confidence, samples, source)
                SELECT key_hash, :next, trait, value, confidence, samples, source FROM model_traits
                WHERE key_hash = :h AND profile_revision = :rev
                """;
            Add(traits, "next", checked(revision + 1)); Add(traits, "h", hash); Add(traits, "rev", revision);
            traits.ExecuteNonQuery();
            using var evidenceTable = _conn.CreateCommand();
            evidenceTable.Transaction = tx;
            evidenceTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='model_qualification_evidence'";
            if (Convert.ToInt64(evidenceTable.ExecuteScalar()) != 0)
                CopyEvidence(hash, revision, checked(revision + 1), tx);
        }
        using var marker = _conn.CreateCommand();
        marker.Transaction = tx;
        marker.CommandText = "INSERT INTO model_profile_migrations (name, applied_at) VALUES (:name, :now)";
        Add(marker, "name", migration); Add(marker, "now", now);
        marker.ExecuteNonQuery();
    }

    private void AddProfileParams(DbCommand cmd, ModelQualificationKey key, string keyHash,
        ModelQualificationState state, string suiteId, string suiteVersion, DateTimeOffset now)
    {
        Add(cmd, "h", keyHash);
        Add(cmd, "kj", key.CanonicalJson());
        Add(cmd, "st", state.ToString());
        Add(cmd, "sid", suiteId);
        Add(cmd, "sver", suiteVersion);
        Add(cmd, "now", Iso(now));
    }

    private ModelQualificationProfile ReadRow(DbDataReader reader)
    {
        var keyJson = Text(reader, "key_json");
        var keyHash = Text(reader, "key_hash");
        var key = ModelQualificationKeyFromJson(keyJson);
        if (key.QualificationKeyHash() != keyHash)
        {
            throw new InvalidDataException("key_hash no coincide con el hash de la clave reconstruida desde key_json");
        }

        if (key.CanonicalJson() != keyJson)
        {
            throw new InvalidDataException("key_json no es exactamente la serialización canónica de la clave reconstruida");
        }

        var staleByOrdinal = reader.GetOrdinal("stale_by_suite_version");
        var staleBy = reader.IsDBNull(staleByOrdinal) ? null : Text(reader, "stale_by_suite_version");
        var staleReasonOrdinal = reader.GetOrdinal("stale_reason");
        var staleReason = reader.IsDBNull(staleReasonOrdinal) ? null : Text(reader, "stale_reason");

        return new ModelQualificationProfile(key,
            Enum.Parse<ModelQualificationState>(Text(reader, "state")),
            reader.GetInt64(reader.GetOrdinal("profile_revision")),
            Text(reader, "suite_id"), Text(reader, "suite_version"), staleBy,
            ParseIso(Text(reader, "created_at")), ParseIso(Text(reader, "updated_at")), staleReason);
    }

    /// <summary>Reconstruye la clave exacta desde su JSON canónico persistido.</summary>
    internal static ModelQualificationKey ModelQualificationKeyFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var adapters = new List<string>();
        foreach (var a in root.GetProperty("adapters").EnumerateArray())
        {
            adapters.Add(a.GetString()!);
        }

        return new ModelQualificationKey(
            root.GetProperty("providerId").GetString()!,
            root.GetProperty("modelId").GetString()!,
            OptionalString(root, "modelRevision"),
            OptionalString(root, "quantization"),
            adapters,
            OptionalString(root, "backend"),
            OptionalString(root, "backendBuild"),
            OptionalString(root, "chatTemplateHash"),
            root.GetProperty("adapterProfile").GetString()!,
            Enum.Parse<ToolCallFormat>(root.GetProperty("toolCallFormat").GetString()!),
            Enum.Parse<ToolMode>(root.GetProperty("toolMode").GetString()!),
            root.GetProperty("promptProfileVersion").GetString()!,
            OptionalRouteString(root, "endpoint"), OptionalRouteString(root, "protocol"),
            OptionalRouteString(root, "runtimeBuild"));
    }

    private static string? OptionalRouteString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static string? OptionalString(JsonElement root, string name)
    {
        var el = root.GetProperty(name);
        return el.ValueKind == JsonValueKind.Null ? null : el.GetString();
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info(:t) WHERE name = :c";
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter(":t", table));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter(":c", column));
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var param = new Microsoft.Data.Sqlite.SqliteParameter(name, value);
        cmd.Parameters.Add(param);
    }

    private static string Text(DbDataReader reader, string column)
    {
        return reader.GetString(reader.GetOrdinal(column));
    }

    private static string Text(DbDataReader reader, int index)
    {
        return reader.GetString(index);
    }

    private static string Iso(DateTimeOffset dt)
    {
        return dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz");
    }

    private static DateTimeOffset ParseIso(string value)
    {
        return DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
    }
}
