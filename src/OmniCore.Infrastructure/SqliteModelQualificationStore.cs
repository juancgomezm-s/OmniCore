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
/// concurrentes. `MarkStale` no destruye la evidencia: solo cambia el estado a Stale y registra
/// la versión que lo volvió Stale en `stale_by_suite_version`, conservando suite_version (la suite
/// que produjo el perfil), key_json y el historial de traits (ADR-0007 §4).
/// </summary>
public sealed class SqliteModelQualificationStore : IModelQualificationStore, IDisposable
{
    private readonly DbConnection _conn;

    private readonly Func<DateTimeOffset> _clock;

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
        var parent = Path.GetDirectoryName(databasePath);
        if (parent is not null && parent!.Length > 0 && !Directory.Exists(parent!))
        {
            Directory.CreateDirectory(parent!);
        }

        var connString = "DataSource=" + databasePath;
        _conn = Microsoft.Data.Sqlite.SqliteFactory.Instance!.CreateDataSource(connString)!.OpenConnection()!;
        InitializeSchema();
    }

    public ModelQualificationProfile? Get(ModelQualificationKey key, CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
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
        var keyHash = key.QualificationKeyHash();
        var existing = Get(key, cancellationToken);
        var now = _clock();

        using var tx = _conn.BeginTransaction();
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

            tx.Commit();
            return new ModelQualificationProfile(key, state, 1, suiteId, suiteVersion, now, now);
        }

        if (existing!.ProfileRevision != expectedRevision)
        {
            throw new ModelQualificationRevisionConflictException(expectedRevision, existing!.ProfileRevision);
        }

        var updated = new ModelQualificationProfile(key, state, existing!.ProfileRevision + 1, suiteId,
            suiteVersion, existing!.CreatedAt, now);
        using (var update = _conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE model_profiles SET
                    state = :st, profile_revision = :rev, suite_id = :sid, suite_version = :sver,
                    updated_at = :now
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
                var current = Get(key, cancellationToken);
                throw new ModelQualificationRevisionConflictException(existing!.ProfileRevision,
                    current?.ProfileRevision ?? 0);
            }
        }

        tx.Commit();
        return updated;
    }

    public ModelQualificationProfile MarkStale(ModelQualificationKey key, long expectedRevision,
        string newSuiteVersion, CancellationToken cancellationToken)
    {
        var keyHash = key.QualificationKeyHash();
        var existing = Get(key, cancellationToken);
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

        var now = _clock();
        using var tx = _conn.BeginTransaction();
        using (var update = _conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE model_profiles SET
                    state = :st, profile_revision = :rev, stale_by_suite_version = :stalever, updated_at = :now
                WHERE key_hash = :h AND profile_revision = :expected
            """;
            Add(update, "st", ModelQualificationState.Stale.ToString());
            Add(update, "rev", existing!.ProfileRevision + 1);
            Add(update, "stalever", newSuiteVersion);
            Add(update, "now", Iso(now));
            Add(update, "h", keyHash);
            Add(update, "expected", existing!.ProfileRevision);
            var rows = update.ExecuteNonQuery();
            if (rows == 0)
            {
                var current = Get(key, cancellationToken);
                throw new ModelQualificationRevisionConflictException(existing!.ProfileRevision,
                    current?.ProfileRevision ?? 0);
            }
        }

        tx.Commit();
        return new ModelQualificationProfile(key, ModelQualificationState.Stale, existing!.ProfileRevision + 1,
            existing!.SuiteId, existing!.SuiteVersion, newSuiteVersion, existing!.CreatedAt, now);
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
        var keyHash = key.QualificationKeyHash();

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

        // Verificación y reemplazo son atómicos dentro de la transacción: la revisión vigente
        // se revalida al reemplazar, para que un perfil subido por otro cliente entre la lectura
        // inicial y el DELETE/INSERT no pise traits de una revisión obsoleta.
        using var tx = _conn.BeginTransaction();
        using (var check = _conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT profile_revision FROM model_profiles WHERE key_hash = :h";
            Add(check, "h", keyHash);
            using var reader = check.ExecuteReader();
            if (!reader.Read())
            {
                tx.Rollback();
                throw new ModelQualificationRevisionConflictException(profileRevision, 0);
            }

            var currentRevision = reader.GetInt64(0);
            if (currentRevision != profileRevision)
            {
                tx.Rollback();
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

        tx.Commit();
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
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
        """);
        // Migración compatible para bases anteriores a la columna stale_by_suite_version.
        if (!ColumnExists("model_profiles", "stale_by_suite_version"))
        {
            Exec("ALTER TABLE model_profiles ADD COLUMN stale_by_suite_version TEXT");
        }
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

        return new ModelQualificationProfile(key,
            Enum.Parse<ModelQualificationState>(Text(reader, "state")),
            reader.GetInt64(reader.GetOrdinal("profile_revision")),
            Text(reader, "suite_id"), Text(reader, "suite_version"), staleBy,
            ParseIso(Text(reader, "created_at")), ParseIso(Text(reader, "updated_at")));
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
            root.GetProperty("promptProfileVersion").GetString()!);
    }

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
