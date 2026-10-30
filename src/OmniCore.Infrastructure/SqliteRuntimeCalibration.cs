namespace OmniCore.Infrastructure;

using OmniCore.Domain;

public sealed partial class SqliteModelQualificationStore
{
    public IReadOnlyDictionary<string, long> ReadRuntimeCalibration(ModelQualificationKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='model_runtime_counters'";
        if (Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) return new Dictionary<string, long>();
        cmd.CommandText = "SELECT counter,value FROM model_runtime_counters WHERE key_hash=:h"; Add(cmd, "h", key.QualificationKeyHash());
        using var reader = cmd.ExecuteReader(); Dictionary<string, long> result = new(StringComparer.Ordinal);
        while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(reader.GetString(0), reader.GetInt64(1)); }
        return result;
    }

    /// <summary>Atomically deduplicates canonical events and updates only empirical, descriptive traits.
    /// Never creates a qualification profile or changes the user's routing/mutation policy.</summary>
    public void RecordRuntimeCalibration(ModelQualificationKey key, IReadOnlyList<RuntimeCalibrationObservation> observations,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (observations.Count > 20_000 || observations.Any(item => !Guid.TryParse(item.EventId, out _)
                || !RuntimeCalibrationCounters.Names.Contains(item.Counter)))
            throw new ArgumentException("Invalid runtime calibration observations.");
        Exec("CREATE TABLE IF NOT EXISTS model_runtime_observations(event_id TEXT NOT NULL,counter TEXT NOT NULL,key_hash TEXT NOT NULL,PRIMARY KEY(event_id,counter));"
            + "CREATE TABLE IF NOT EXISTS model_runtime_counters(key_hash TEXT NOT NULL,counter TEXT NOT NULL,value INTEGER NOT NULL,PRIMARY KEY(key_hash,counter))");
        using var tx = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var profile = GetCore(key, token, tx);
        if (profile is null || profile.State is not (ModelQualificationState.Qualified or ModelQualificationState.Calibrated))
        { tx.Commit(); return; }
        var hash = key.QualificationKeyHash();
        using var cmd = _conn.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM model_runtime_observations WHERE key_hash=:h"; Add(cmd, "h", hash);
        if (Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) + observations.Count > 100_000)
            throw new InvalidOperationException("Runtime calibration receipt capacity reached.");
        foreach (var observation in observations)
        {
            token.ThrowIfCancellationRequested(); cmd.Parameters.Clear();
            Add(cmd, "e", observation.EventId); Add(cmd, "c", observation.Counter); Add(cmd, "h", hash);
            cmd.CommandText = "INSERT OR IGNORE INTO model_runtime_observations VALUES(:e,:c,:h)";
            if (cmd.ExecuteNonQuery() == 0) continue;
            cmd.CommandText = "INSERT INTO model_runtime_counters VALUES(:h,:c,1) ON CONFLICT(key_hash,counter) DO UPDATE SET value=value+1";
            cmd.ExecuteNonQuery();
        }
        cmd.Parameters.Clear(); Add(cmd, "h", hash);
        cmd.CommandText = "SELECT counter,value FROM model_runtime_counters WHERE key_hash=:h";
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) counts.Add(reader.GetString(0), checked((int)reader.GetInt64(1)));
        var steps = counts.GetValueOrDefault("model_steps");
        var failures = counts.GetValueOrDefault("invalid_tool_arguments");
        var valid = counts.GetValueOrDefault("valid_tool_arguments"); var calls = valid + failures;
        List<ModelTraitRecord> traits = [];
        void Score(string name, int numerator, int denominator)
        {
            if (denominator <= 0) return;
            traits.Add(new(hash, profile.ProfileRevision, name, Math.Clamp((double)numerator / denominator, 0, 1),
                (double)denominator / (denominator + 10), denominator, "observed:canonical-runtime-v1"));
        }
        Score("ToolArgumentValidity", valid, calls);
        Score("ArgumentRepairSuccess", counts.GetValueOrDefault("argument_repairs"), failures);
        // Preserve benchmark and mutation evidence. Stall/gate counts stay raw counters;
        // absence of a measured denominator never becomes a fabricated success rate.
        var updatedNames = traits.Select(t => t.Trait).ToHashSet(StringComparer.Ordinal);
        var merged = ReadTraitsCore(key, profile.ProfileRevision, token, tx).Where(t => !updatedNames.Contains(t.Trait)).Concat(traits).ToArray();
        SaveTraitsCore(hash, profile.ProfileRevision, merged, token, tx);
        if (calls >= RuntimeCalibrationCounters.MinimumSamples && steps >= RuntimeCalibrationCounters.MinimumSamples)
        {
            cmd.Parameters.Clear(); Add(cmd, "h", hash); Add(cmd, "r", profile.ProfileRevision);
            cmd.CommandText = "UPDATE model_profiles SET state='Calibrated' WHERE key_hash=:h AND profile_revision=:r AND state='Qualified'";
            cmd.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested(); tx.Commit();
    }
}