namespace OmniCore.Infrastructure;

using System.Data.Common;
using OmniCore.Abstractions;
using OmniCore.Domain;

public sealed partial class SqliteModelQualificationStore
{
    internal const string EvidenceSchemaMarker = "m5-qualification-evidence-v1";

    private void InitializeEvidenceSchema(DbTransaction transaction)
    {
        using var check = _conn.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM model_profile_migrations WHERE name = :name";
        Add(check, "name", EvidenceSchemaMarker);
        var installed = Convert.ToInt64(check.ExecuteScalar()) != 0;
        using var table = _conn.CreateCommand();
        table.Transaction = transaction;
        table.CommandText = "SELECT type FROM sqlite_master WHERE name = 'model_qualification_evidence' COLLATE NOCASE";
        var tableType = table.ExecuteScalar();
        if (installed && !Equals(tableType, "table"))
            throw new InvalidDataException("Qualification evidence schema is missing after installation.");
        using var create = _conn.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS model_qualification_evidence (
                key_hash TEXT NOT NULL,
                profile_revision INTEGER NOT NULL CHECK(profile_revision > 0),
                source_run_revision INTEGER NOT NULL CHECK(source_run_revision > 0 AND source_run_revision <= profile_revision),
                artifact_id TEXT NOT NULL,
                artifact_algorithm TEXT NOT NULL,
                artifact_hash TEXT NOT NULL,
                artifact_size INTEGER NOT NULL CHECK(artifact_size >= 0),
                media_type TEXT NOT NULL,
                artifact_kind TEXT NOT NULL,
                sensitivity TEXT NOT NULL,
                redacted INTEGER NOT NULL CHECK(redacted IN (0,1)),
                PRIMARY KEY(key_hash, profile_revision))
            """;
        create.ExecuteNonQuery();
        // Validate an existing schema before marking it installed. No invented legacy roots.
        using var validate = _conn.CreateCommand();
        validate.Transaction = transaction;
        validate.CommandText = "SELECT key_hash, profile_revision, source_run_revision, artifact_id, artifact_algorithm, artifact_hash, artifact_size, media_type, artifact_kind, sensitivity, redacted FROM model_qualification_evidence LIMIT 0";
        using (validate.ExecuteReader()) { }
        if (!installed)
        {
            using var marker = _conn.CreateCommand();
            marker.Transaction = transaction;
            marker.CommandText = "INSERT INTO model_profile_migrations(name, applied_at) VALUES(:name,:now)";
            Add(marker, "name", EvidenceSchemaMarker); Add(marker, "now", Iso(_clock()));
            marker.ExecuteNonQuery();
        }
    }

    public ModelQualificationProfile UpsertWithTraitsAndEvidence(ModelQualificationKey key,
        long expectedRevision, ModelQualificationState state, string suiteId, string suiteVersion,
        IReadOnlyList<ModelTraitRecord> traits, ArtifactRef artifact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Id.Value == Guid.Empty || string.IsNullOrWhiteSpace(artifact.MediaType)
            || !Enum.IsDefined(artifact.Kind) || !Enum.IsDefined(artifact.Sensitivity))
            throw new ArgumentException("Qualification evidence metadata is invalid.", nameof(artifact));
        var revision = checked(expectedRevision + 1);
        var hash = key.QualificationKeyHash();
        ValidateTraits(hash, revision, traits);
        // Never call PutText while holding this non-reentrant lease. Verify after acquiring it:
        // GC may have collected an unreferenced publication before we obtained the lease.
        using var lease = ArtifactStoreLease.Acquire(_dataDirectory, cancellationToken);
        if (artifact.Size < 0 || !new FileArtifactStore(_dataDirectory).Verify(artifact.Hash, artifact.Size))
            throw new InvalidDataException("Qualification evidence blob is missing or invalid.");
        using var transaction = _conn.BeginTransaction();
        var profile = UpsertCore(key, expectedRevision, state, suiteId, suiteVersion, cancellationToken, transaction);
        SaveTraitsCore(hash, revision, traits, cancellationToken, transaction);
        using var insert = _conn.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO model_qualification_evidence
                (key_hash,profile_revision,source_run_revision,artifact_id,artifact_algorithm,
                 artifact_hash,artifact_size,media_type,artifact_kind,sensitivity,redacted) VALUES
            (:h,:rev,:rev,:id,:alg,:blob,:size,:media,:kind,:sensitivity,:redacted)
            """;
        Add(insert, "h", hash); Add(insert, "rev", revision); Add(insert, "id", artifact.Id.ToString());
        Add(insert, "alg", artifact.Hash.Algorithm); Add(insert, "blob", artifact.Hash.Value);
        Add(insert, "size", artifact.Size); Add(insert, "media", artifact.MediaType);
        Add(insert, "kind", artifact.Kind.ToString()); Add(insert, "sensitivity", artifact.Sensitivity.ToString());
        Add(insert, "redacted", artifact.Redacted ? 1 : 0);
        insert.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return profile;
    }

    public ModelQualificationEvidence? Evidence(ModelQualificationKey key, long profileRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = _conn.CreateCommand();
        command.CommandText = "SELECT source_run_revision, artifact_id, artifact_algorithm, artifact_hash, artifact_size, media_type, artifact_kind, sensitivity, redacted FROM model_qualification_evidence WHERE key_hash=:h AND profile_revision=:rev";
        Add(command, "h", key.QualificationKeyHash()); Add(command, "rev", profileRevision);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ModelQualificationEvidence(profileRevision, reader.GetInt64(0), new ArtifactRef(
            ArtifactId.Parse(reader.GetString(1)), new ContentHash(reader.GetString(2), reader.GetString(3)),
            reader.GetInt64(4), reader.GetString(5), Enum.Parse<ArtifactKind>(reader.GetString(6)),
            Enum.Parse<Sensitivity>(reader.GetString(7)), reader.GetInt64(8) != 0));
    }

    private void CopyEvidence(string keyHash, long previous, long next, DbTransaction transaction)
    {
        using var copy = _conn.CreateCommand();
        copy.Transaction = transaction;
        copy.CommandText = """
            INSERT INTO model_qualification_evidence
                (key_hash,profile_revision,source_run_revision,artifact_id,artifact_algorithm,
                 artifact_hash,artifact_size,media_type,artifact_kind,sensitivity,redacted)
            SELECT key_hash,:next,source_run_revision,artifact_id,artifact_algorithm,artifact_hash,
                artifact_size,media_type,artifact_kind,sensitivity,redacted
            FROM model_qualification_evidence WHERE key_hash=:h AND profile_revision=:previous
            """;
        Add(copy, "next", next); Add(copy, "h", keyHash); Add(copy, "previous", previous);
        copy.ExecuteNonQuery();
    }
}
