namespace OmniCore.Infrastructure;

using System.Data.Common;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Store relacional de políticas de modelo en `user.db` (ADR-0044 §8). Resolución por clave
/// exacta con columnas tipadas: ninguna consulta vectorial participa en una decisión de
/// autorización. Concasión optimista por `policy_revision` (ADR-0044 §9): Set/Delete exigen la
/// revisión vigente y jamás pisan cambios concurrentes; todo cambio queda en
/// `model_policy_history`. La selección de modelo por workspace vive en `model_selections`
/// con soporte del modo efímero ObserveOnly (ADR-0044 §6).
/// </summary>
public sealed class SqliteModelPolicyStore : IModelPolicyStore, IDisposable
{
    private readonly DbConnection _conn;

    private readonly Func<DateTimeOffset> _clock;

    public void Dispose()
    {
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }

    public SqliteModelPolicyStore(string databasePath) : this(databasePath, null)
    {
    }

    /// <summary>Reloj inyectable para tests deterministas.</summary>
    public SqliteModelPolicyStore(string databasePath, Func<DateTimeOffset>? clock)
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

    public StoredModelPolicy? Get(ModelPolicyKey key, CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM model_user_policies WHERE policy_key_hash = :h";
        Add(cmd, "h", key.PolicyKeyHash());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public IReadOnlyList<StoredModelPolicy> List(CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM model_user_policies ORDER BY updated_at, policy_key_hash";
        using var reader = cmd.ExecuteReader();
        var result = new List<StoredModelPolicy>();
        while (reader.Read())
        {
            result.Add(ReadRow(reader));
        }

        return result;
    }

    public StoredModelPolicy Set(ModelPolicyKey key, long expectedRevision, UserModelPolicy policy,
        CancellationToken cancellationToken)
    {
        var keyHash = key.PolicyKeyHash();
        var existing = Get(key, cancellationToken);
        var now = _clock();

        using var tx = _conn.BeginTransaction();
        if (existing is null)
        {
            if (expectedRevision != 0)
            {
                throw new ModelPolicyRevisionConflictException(expectedRevision, 0);
            }

            using (var insert = _conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO model_user_policies (
                        policy_key_hash, policy_key_json, policy_revision, category,
                        mutation_mode, delete_policy, move_rename_policy,
                        tool_mode, max_visible_tools, allow_tool_discovery, capability_ceiling_json,
                        max_files_per_turn, max_changed_lines_per_turn, max_rewrite_ratio,
                        require_prior_read, require_version_token, require_validation,
                        allow_parallel_mutations, source, note, created_at, updated_at)
                    VALUES (:h, :kj, 1, :cat, :mm, :del, :mov, :tm, :vis, :disc, :caps,
                        :mfiles, :mlines, :mratio, :rread, :rtoken, :rvalid, :rpar, :src, :note, :now, :now)
                """;
                AddPolicyParams(insert, key, keyHash, policy, now);
                insert.ExecuteNonQuery();
            }

            AppendHistory(tx, keyHash, 1, "created", null, PolicyToJson(policy), now);
            tx.Commit();
            return new StoredModelPolicy(key, 1, policy, now, now);
        }

        if (existing!.Revision != expectedRevision)
        {
            throw new ModelPolicyRevisionConflictException(expectedRevision, existing!.Revision);
        }

        var updated = new StoredModelPolicy(key, existing!.Revision + 1, policy, existing!.CreatedAt, now);
        using (var update = _conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE model_user_policies SET
                    policy_revision = :rev, category = :cat,
                    mutation_mode = :mm, delete_policy = :del, move_rename_policy = :mov,
                    tool_mode = :tm, max_visible_tools = :vis, allow_tool_discovery = :disc,
                    capability_ceiling_json = :caps,
                    max_files_per_turn = :mfiles, max_changed_lines_per_turn = :mlines,
                    max_rewrite_ratio = :mratio,
                    require_prior_read = :rread, require_version_token = :rtoken,
                    require_validation = :rvalid, allow_parallel_mutations = :rpar,
                    source = :src, note = :note, updated_at = :now
                WHERE policy_key_hash = :h AND policy_revision = :expected
            """;
            AddPolicyParams(update, key, keyHash, policy, now);
            Add(update, "rev", updated.Revision);
            Add(update, "expected", existing!.Revision);
            var rows = update.ExecuteNonQuery();
            if (rows == 0)
            {
                // Concasión perdida contra otro escritor: re-leer y reportar la revisión real.
                var current = Get(key, cancellationToken);
                throw new ModelPolicyRevisionConflictException(existing!.Revision,
                    current?.Revision ?? 0);
            }
        }

        AppendHistory(tx, keyHash, updated.Revision, "updated", PolicyToJson(existing!.Policy),
            PolicyToJson(policy), now);
        tx.Commit();
        return updated;
    }

    public void Delete(ModelPolicyKey key, long expectedRevision, CancellationToken cancellationToken)
    {
        var keyHash = key.PolicyKeyHash();
        var existing = Get(key, cancellationToken);
        if (existing is null)
        {
            throw new ModelPolicyNotFoundException(key);
        }

        if (existing!.Revision != expectedRevision)
        {
            throw new ModelPolicyRevisionConflictException(expectedRevision, existing!.Revision);
        }

        var now = _clock();
        using var tx = _conn.BeginTransaction();
        using (var delete = _conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM model_user_policies WHERE policy_key_hash = :h AND policy_revision = :expected";
            Add(delete, "h", keyHash);
            Add(delete, "expected", existing!.Revision);
            var rows = delete.ExecuteNonQuery();
            if (rows == 0)
            {
                var current = Get(key, cancellationToken);
                throw new ModelPolicyRevisionConflictException(existing!.Revision,
                    current?.Revision ?? 0);
            }
        }

        // Eliminar borra SOLO la preferencia del usuario (ADR-0044 §7): el historial y la
        // cualificación sobreviven; la próxima selección reabre el onboarding.
        AppendHistory(tx, keyHash, existing!.Revision, "deleted", PolicyToJson(existing!.Policy), null, now);
        tx.Commit();
    }

    public IReadOnlyList<ModelPolicyChange> History(ModelPolicyKey key, CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT policy_key_hash, policy_revision, change_kind, old_value_json, new_value_json, changed_at
            FROM model_policy_history WHERE policy_key_hash = :h ORDER BY policy_revision, rowid
        """;
        Add(cmd, "h", key.PolicyKeyHash());
        using var reader = cmd.ExecuteReader();
        var result = new List<ModelPolicyChange>();
        while (reader.Read())
        {
            result.Add(new ModelPolicyChange(
                Text(reader, 0), reader.GetInt64(1), Text(reader, 2),
                NullText(reader, 3), NullText(reader, 4), DateTimeOffset.Parse(Text(reader, 5),
                System.Globalization.CultureInfo.InvariantCulture)));
        }

        return result;
    }

    public ModelSelectionState? GetSelection(string workspaceId, CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT workspace_id, model_id, policy_key_json, ephemeral_observe_only, selected_at
            FROM model_selections WHERE workspace_id = :w
        """;
        Add(cmd, "w", workspaceId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var key = ModelPolicyKeyFromJson(Text(reader, 2));
        return new ModelSelectionState(Text(reader, 0), key, Text(reader, 1),
            reader.GetInt64(3) != 0, DateTimeOffset.Parse(Text(reader, 4),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetSelection(ModelSelectionState selection, CancellationToken cancellationToken)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_selections (workspace_id, model_id, policy_key_hash, policy_key_json,
                ephemeral_observe_only, selected_at)
            VALUES (:w, :m, :h, :kj, :eph, :now)
            ON CONFLICT(workspace_id) DO UPDATE SET
                model_id = :m, policy_key_hash = :h, policy_key_json = :kj,
                ephemeral_observe_only = :eph, selected_at = :now
        """;
        Add(cmd, "w", selection.WorkspaceId);
        Add(cmd, "m", selection.ModelId);
        Add(cmd, "h", selection.Key.PolicyKeyHash());
        Add(cmd, "kj", selection.Key.CanonicalJson());
        Add(cmd, "eph", selection.EphemeralObserveOnly ? 1L : 0L);
        Add(cmd, "now", Iso(selection.SelectedAt));
        cmd.ExecuteNonQuery();
    }

    private void InitializeSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS model_user_policies (
                policy_key_hash TEXT PRIMARY KEY,
                policy_key_json TEXT NOT NULL,
                policy_revision INTEGER NOT NULL,
                category TEXT NOT NULL,
                mutation_mode TEXT NOT NULL,
                delete_policy TEXT NOT NULL,
                move_rename_policy TEXT NOT NULL,
                tool_mode TEXT NOT NULL,
                max_visible_tools INTEGER NOT NULL,
                allow_tool_discovery INTEGER NOT NULL,
                capability_ceiling_json TEXT NOT NULL,
                max_files_per_turn INTEGER NOT NULL,
                max_changed_lines_per_turn INTEGER NOT NULL,
                max_rewrite_ratio REAL NOT NULL,
                require_prior_read INTEGER NOT NULL,
                require_version_token INTEGER NOT NULL,
                require_validation INTEGER NOT NULL,
                allow_parallel_mutations INTEGER NOT NULL,
                source TEXT NOT NULL,
                note TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
        """);
        Exec("""
            CREATE TABLE IF NOT EXISTS model_policy_history (
                policy_key_hash TEXT NOT NULL,
                policy_revision INTEGER NOT NULL,
                change_kind TEXT NOT NULL,
                old_value_json TEXT,
                new_value_json TEXT,
                changed_at TEXT NOT NULL
            )
        """);
        Exec("CREATE INDEX IF NOT EXISTS ix_model_policy_history_key ON model_policy_history (policy_key_hash)");
        Exec("""
            CREATE TABLE IF NOT EXISTS model_selections (
                workspace_id TEXT PRIMARY KEY,
                model_id TEXT NOT NULL,
                policy_key_hash TEXT NOT NULL,
                policy_key_json TEXT NOT NULL,
                ephemeral_observe_only INTEGER NOT NULL,
                selected_at TEXT NOT NULL
            )
        """);
    }

    private void AppendHistory(DbTransaction tx, string keyHash, long revision, string kind,
        string? oldValueJson, string? newValueJson, DateTimeOffset at)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO model_policy_history (policy_key_hash, policy_revision, change_kind,
                old_value_json, new_value_json, changed_at)
            VALUES (:h, :rev, :kind, :old, :new, :at)
        """;
        Add(cmd, "h", keyHash);
        Add(cmd, "rev", revision);
        Add(cmd, "kind", kind);
        Add(cmd, "old", oldValueJson);
        Add(cmd, "new", newValueJson);
        Add(cmd, "at", Iso(at));
        cmd.ExecuteNonQuery();
    }

    private void AddPolicyParams(DbCommand cmd, ModelPolicyKey key, string keyHash, UserModelPolicy policy,
        DateTimeOffset now)
    {
        Add(cmd, "h", keyHash);
        Add(cmd, "kj", key.CanonicalJson());
        Add(cmd, "cat", policy.Category.ToString());
        Add(cmd, "mm", policy.MutationPolicy.Mode.ToString());
        Add(cmd, "del", policy.MutationPolicy.Delete.ToString());
        Add(cmd, "mov", policy.MutationPolicy.MoveOrRename.ToString());
        Add(cmd, "tm", policy.ToolPolicy.Mode.ToString());
        Add(cmd, "vis", (long)policy.ToolPolicy.MaxVisibleTools);
        Add(cmd, "disc", policy.ToolPolicy.AllowToolDiscovery ? 1L : 0L);
        Add(cmd, "caps", CapabilityCeilingJson(policy.ToolPolicy));
        Add(cmd, "mfiles", (long)policy.MutationPolicy.MaxFilesPerTurn);
        Add(cmd, "mlines", (long)policy.MutationPolicy.MaxChangedLinesPerTurn);
        Add(cmd, "mratio", policy.MutationPolicy.MaxRewriteRatio);
        Add(cmd, "rread", policy.MutationPolicy.RequirePriorRead ? 1L : 0L);
        Add(cmd, "rtoken", policy.MutationPolicy.RequireExpectedVersionToken ? 1L : 0L);
        Add(cmd, "rvalid", policy.MutationPolicy.RequirePostEditValidation ? 1L : 0L);
        Add(cmd, "rpar", policy.MutationPolicy.AllowParallelMutations ? 1L : 0L);
        Add(cmd, "src", policy.Source);
        Add(cmd, "note", policy.Note);
        Add(cmd, "now", Iso(now));
    }

    private StoredModelPolicy ReadRow(DbDataReader reader)
    {
        var keyJson = Text(reader, "policy_key_json");
        var key = ModelPolicyKeyFromJson(keyJson);
        var caps = ParseCapabilityCeiling(Text(reader, "capability_ceiling_json"));
        var toolPolicy = new ModelToolPolicy(
            Enum.Parse<ToolMode>(Text(reader, "tool_mode")),
            (int)reader.GetInt64(reader.GetOrdinal("max_visible_tools")),
            reader.GetInt64(reader.GetOrdinal("allow_tool_discovery")) != 0,
            caps);
        var mutation = new FileMutationPolicy(
            Enum.Parse<FileMutationMode>(Text(reader, "mutation_mode")),
            Enum.Parse<DestructiveActionPolicy>(Text(reader, "delete_policy")),
            Enum.Parse<DestructiveActionPolicy>(Text(reader, "move_rename_policy")),
            (int)reader.GetInt64(reader.GetOrdinal("max_files_per_turn")),
            (int)reader.GetInt64(reader.GetOrdinal("max_changed_lines_per_turn")),
            reader.GetDouble(reader.GetOrdinal("max_rewrite_ratio")),
            reader.GetInt64(reader.GetOrdinal("require_prior_read")) != 0,
            reader.GetInt64(reader.GetOrdinal("require_version_token")) != 0,
            reader.GetInt64(reader.GetOrdinal("require_validation")) != 0,
            reader.GetInt64(reader.GetOrdinal("allow_parallel_mutations")) != 0);
        var noteOrdinal = reader.GetOrdinal("note");
        var note = reader.IsDBNull(noteOrdinal) ? null : reader.GetString(noteOrdinal);
        var policy = new UserModelPolicy(
            Enum.Parse<ModelPolicyCategory>(Text(reader, "category")), toolPolicy, mutation,
            Text(reader, "source"), note);
        return new StoredModelPolicy(key, reader.GetInt64(reader.GetOrdinal("policy_revision")),
            policy, ParseIso(Text(reader, "created_at")), ParseIso(Text(reader, "updated_at")));
    }

    internal static string CapabilityCeilingJson(ModelToolPolicy toolPolicy)
    {
        var parts = new List<string>();
        foreach (var c in toolPolicy.CapabilityCeiling)
        {
            parts.Add("\"" + c + "\"");
        }

        parts.Sort(StringComparer.Ordinal);
        return "[" + string.Join(",", parts.ToArray()) + "]";
    }

    internal static IReadOnlySet<ModelToolCapability> ParseCapabilityCeiling(string json)
    {
        var result = new HashSet<ModelToolCapability>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            result.Add(Enum.Parse<ModelToolCapability>(item.GetString()!));
        }

        return result;
    }

    /// <summary>Reconstruye la clave exacta desde su JSON canónico persistido.</summary>
    internal static ModelPolicyKey ModelPolicyKeyFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var adapters = new List<string>();
        foreach (var a in root.GetProperty("adapters").EnumerateArray())
        {
            adapters.Add(a.GetString()!);
        }

        return new ModelPolicyKey(
            root.GetProperty("providerId").GetString()!,
            root.GetProperty("modelId").GetString()!,
            OptionalString(root, "modelRevision"),
            OptionalString(root, "quantization"),
            adapters,
            OptionalString(root, "backend"),
            OptionalString(root, "backendBuild"),
            OptionalString(root, "chatTemplateHash"),
            root.GetProperty("adapterProfile").GetString()!,
            root.GetProperty("promptProfileVersion").GetString()!);
    }

    internal static string PolicyToJson(UserModelPolicy policy)
    {
        var parts = new List<string>
        {
            "\"category\":\"" + policy.Category + "\"",
            "\"source\":\"" + JsonEscape(policy.Source) + "\"",
            policy.Note is null ? "\"note\":null" : "\"note\":\"" + JsonEscape(policy.Note) + "\"",
            "\"tool\":{"
            + "\"mode\":\"" + policy.ToolPolicy.Mode + "\""
            + ",\"maxVisibleTools\":" + policy.ToolPolicy.MaxVisibleTools
            + ",\"allowToolDiscovery\":" + (policy.ToolPolicy.AllowToolDiscovery ? "true" : "false")
            + ",\"capabilityCeiling\":" + CapabilityCeilingJson(policy.ToolPolicy)
            + "}",
            "\"mutation\":{"
            + "\"mode\":\"" + policy.MutationPolicy.Mode + "\""
            + ",\"delete\":\"" + policy.MutationPolicy.Delete + "\""
            + ",\"moveOrRename\":\"" + policy.MutationPolicy.MoveOrRename + "\""
            + ",\"maxFilesPerTurn\":" + policy.MutationPolicy.MaxFilesPerTurn
            + ",\"maxChangedLinesPerTurn\":" + policy.MutationPolicy.MaxChangedLinesPerTurn
            + ",\"maxRewriteRatio\":" + policy.MutationPolicy.MaxRewriteRatio.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            + ",\"requirePriorRead\":" + (policy.MutationPolicy.RequirePriorRead ? "true" : "false")
            + ",\"requireVersionToken\":" + (policy.MutationPolicy.RequireExpectedVersionToken ? "true" : "false")
            + ",\"requireValidation\":" + (policy.MutationPolicy.RequirePostEditValidation ? "true" : "false")
            + ",\"allowParallelMutations\":" + (policy.MutationPolicy.AllowParallelMutations ? "true" : "false")
            + "}",
        };
        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Text(DbDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));

    private static string Text(DbDataReader reader, int ordinal) => reader.GetString(ordinal);

    private static string? NullText(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string Iso(DateTimeOffset at) => at.ToString("O");

    private static DateTimeOffset ParseIso(string value) => DateTimeOffset.Parse(value,
        System.Globalization.CultureInfo.InvariantCulture);

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private void Exec(string ddl)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? (object)DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
