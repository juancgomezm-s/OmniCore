using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Proposed M5 GC-root contract tests. SQL is deliberately limited to the agreed minimum
/// user-evidence columns; it is a fixture shape, not a claim about the eventual production schema.
/// Every artifact, database, and directory is private to a GUID-scoped local fixture.
/// </summary>
public sealed class M5UserQualificationGcRootTests
{
    [Fact]
    public void User_database_roots_all_historical_evidence_and_sweep_removes_only_old_orphan()
    {
        using var fixture = new Fixture();
        var revisionOne = fixture.PutBlob("qualification evidence revision one");
        var revisionTwo = fixture.PutBlob("qualification evidence revision two");
        var orphanPath = fixture.PutOldOrphan("unreferenced-old-blob");
        fixture.CreateEvidenceTable();
        fixture.AddEvidence("profile-hash-a", 1, revisionOne);
        fixture.AddEvidence("profile-hash-a", 2, revisionTwo);

        var result = fixture.Sweep(fixture.MissingJournalPath);

        Assert.Equal(2, result.LiveReferenced);
        Assert.Equal(1, result.Deleted);
        Assert.True(File.Exists(fixture.BlobPath(revisionOne.Hash.Value)), "historical revision 1 was collected");
        Assert.True(File.Exists(fixture.BlobPath(revisionTwo.Hash.Value)), "current revision 2 was collected");
        Assert.False(File.Exists(orphanPath), "old unreferenced blob should be swept");
    }

    [Theory]
    [InlineData("missing-blob")]
    [InlineData("corrupt-content")]
    [InlineData("wrong-size")]
    [InlineData("malformed-hash")]
    [InlineData("malformed-algorithm")]
    [InlineData("negative-size")]
    public void Invalid_user_evidence_reference_aborts_before_any_sweep(string invalidReference)
    {
        using var fixture = new Fixture();
        var validRoot = fixture.PutBlob("independent valid qualification root");
        var invalidRoot = fixture.PutBlob("qualification evidence under validation");
        var orphanPath = fixture.PutOldOrphan("orphan-must-survive-failed-mark");
        fixture.CreateEvidenceTable();
        fixture.AddEvidence("profile-hash-valid", 1, validRoot);

        switch (invalidReference)
        {
            case "missing-blob":
                File.Delete(fixture.BlobPath(invalidRoot.Hash.Value));
                fixture.AddEvidence("profile-hash-invalid", 1, invalidRoot);
                break;
            case "corrupt-content":
                File.WriteAllText(fixture.BlobPath(invalidRoot.Hash.Value), new string('x', (int)invalidRoot.Size));
                fixture.SetOld(invalidRoot.Hash.Value);
                fixture.AddEvidence("profile-hash-invalid", 1, invalidRoot);
                break;
            case "wrong-size":
                fixture.AddEvidence("profile-hash-invalid", 1, invalidRoot,
                    sizeOverride: invalidRoot.Size + 1);
                break;
            case "malformed-hash":
                fixture.AddRawEvidence("profile-hash-invalid", 1, "sha256", "../../outside", 13);
                break;
            case "malformed-algorithm":
                fixture.AddEvidence("profile-hash-invalid", 1, invalidRoot, algorithmOverride: "SHA256");
                break;
            case "negative-size":
                fixture.AddEvidence("profile-hash-invalid", 1, invalidRoot, sizeOverride: -1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidReference), invalidReference,
                    "Unknown malformed evidence-reference fixture.");
        }

        Assert.Throws<InvalidDataException>(() => fixture.Sweep(fixture.MissingJournalPath));

        Assert.True(File.Exists(orphanPath), "GC must validate all user roots before deleting any orphan");
        Assert.True(File.Exists(fixture.BlobPath(validRoot.Hash.Value)), "valid user root must survive mark failure");
        if (invalidReference is "wrong-size" or "malformed-algorithm" or "negative-size")
            Assert.True(File.Exists(fixture.BlobPath(invalidRoot.Hash.Value)));
        if (invalidReference == "corrupt-content")
            Assert.Equal(new string('x', (int)invalidRoot.Size), File.ReadAllText(fixture.BlobPath(invalidRoot.Hash.Value)));
        if (invalidReference == "missing-blob")
            Assert.False(File.Exists(fixture.BlobPath(invalidRoot.Hash.Value)));
    }

    [Fact]
    public void Old_user_database_without_evidence_table_is_not_treated_as_corruption()
    {
        using var fixture = new Fixture();
        var orphanPath = fixture.PutOldOrphan("old-user-db-orphan");
        fixture.CreateLegacyUserTablesWithoutEvidenceRoots();

        var result = fixture.Sweep(fixture.MissingJournalPath);

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(orphanPath));
    }

    [Fact]
    public void Empty_journal_still_marks_user_evidence_roots()
    {
        using var fixture = new Fixture();
        var evidence = fixture.PutBlob("user evidence with an empty event journal");
        var orphanPath = fixture.PutOldOrphan("empty-journal-orphan");
        fixture.CreateEvidenceTable();
        fixture.AddEvidence("profile-empty-journal", 1, evidence);
        fixture.CreateEmptyJournal();

        var result = fixture.Sweep(fixture.EmptyJournalPath);

        Assert.Equal(1, result.Deleted);
        Assert.True(File.Exists(fixture.BlobPath(evidence.Hash.Value)));
        Assert.False(File.Exists(orphanPath));
    }

    // --------------------------------------------------------------- fixture

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "omnicore-m5-user-cas-gc-" + Guid.NewGuid().ToString("N"));
            DataDirectory = Path.Combine(Root, "data");
            Directory.CreateDirectory(DataDirectory);
            Artifacts = new FileArtifactStore(DataDirectory);
            MissingJournalPath = Path.Combine(DataDirectory, "no-journal.db");
            EmptyJournalPath = Path.Combine(DataDirectory, "empty-journal.db");
        }

        public string Root { get; }
        public string DataDirectory { get; }
        public string MissingJournalPath { get; }
        public string EmptyJournalPath { get; }
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public FileArtifactStore Artifacts { get; }

        public ArtifactRef PutBlob(string content)
        {
            var artifact = Artifacts.PutText(content, "text/plain", ArtifactKind.Other, Sensitivity.Normal);
            SetOld(artifact.Hash.Value);
            return artifact;
        }

        public string PutOldOrphan(string content)
        {
            var artifact = PutBlob(content);
            return BlobPath(artifact.Hash.Value);
        }

        public string BlobPath(string hash) => Path.Combine(DataDirectory, "blobs", "sha256",
            hash[..2], hash.Substring(2, 2), hash);

        public void SetOld(string hash) => File.SetLastWriteTimeUtc(BlobPath(hash), Now.UtcDateTime.AddDays(-2));

        public void CreateEvidenceTable()
        {
            using var connection = OpenUserDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE model_qualification_evidence (
                    key_hash TEXT NOT NULL,
                    profile_revision INTEGER NOT NULL,
                    artifact_algorithm TEXT NOT NULL,
                    artifact_hash TEXT NOT NULL,
                    artifact_size INTEGER NOT NULL,
                    PRIMARY KEY (key_hash, profile_revision)
                )
                """;
            command.ExecuteNonQuery();
        }

        public void AddEvidence(string keyHash, long profileRevision, ArtifactRef artifact,
            long? sizeOverride = null, string? algorithmOverride = null) =>
            AddRawEvidence(keyHash, profileRevision, algorithmOverride ?? artifact.Hash.Algorithm,
                artifact.Hash.Value, sizeOverride ?? artifact.Size);

        public void AddRawEvidence(string keyHash, long profileRevision, string algorithm,
            string hash, long size)
        {
            using var connection = OpenUserDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO model_qualification_evidence
                    (key_hash, profile_revision, artifact_algorithm, artifact_hash, artifact_size)
                VALUES ($keyHash, $revision, $algorithm, $hash, $size)
                """;
            command.Parameters.AddWithValue("$keyHash", keyHash);
            command.Parameters.AddWithValue("$revision", profileRevision);
            command.Parameters.AddWithValue("$algorithm", algorithm);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$size", size);
            command.ExecuteNonQuery();
        }

        public void CreateLegacyUserTablesWithoutEvidenceRoots()
        {
            using var connection = OpenUserDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE model_profiles (key_hash TEXT PRIMARY KEY, profile_revision INTEGER NOT NULL);
                CREATE TABLE model_traits (key_hash TEXT NOT NULL, profile_revision INTEGER NOT NULL, trait TEXT NOT NULL);
                INSERT INTO model_profiles (key_hash, profile_revision) VALUES ('legacy-profile', 1);
                """;
            command.ExecuteNonQuery();
        }

        public void CreateEmptyJournal()
        {
            using var connection = new SqliteConnection("DataSource=" + EmptyJournalPath);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE events (artifacts TEXT, payload TEXT)";
            command.ExecuteNonQuery();
        }

        public ArtifactGc.SweepResult Sweep(string journalPath) =>
            new ArtifactGc(DataDirectory).Sweep(journalPath, TimeSpan.Zero, dryRun: false, Now,
                CancellationToken.None);

        private SqliteConnection OpenUserDatabase()
        {
            var connection = new SqliteConnection("DataSource=" + UserDatabasePath);
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            ClearPool(UserDatabasePath);
            ClearPool(EmptyJournalPath);
            ClearPool(MissingJournalPath);
            Directory.Delete(Root, recursive: true);
        }

        private static void ClearPool(string databasePath)
        {
            if (!File.Exists(databasePath)) return;
            using var connection = new SqliteConnection("DataSource=" + databasePath);
            SqliteConnection.ClearPool(connection);
        }

        private static readonly DateTimeOffset Now =
            new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    }
}
