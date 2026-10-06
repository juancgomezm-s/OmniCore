using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Evidence roots are valid only in the User database scanned by ArtifactGc.</summary>
public sealed class M5QualificationEvidenceNamespaceTests
{
    [Fact]
    public void Semicolon_in_user_data_directory_does_not_change_sqlite_connection_namespace()
    {
        using var fixture = new Fixture(";scope");
        var artifact = fixture.Put("root in a legal semicolon-containing directory");
        using (var store = fixture.OpenUserStore())
            store.UpsertWithTraitsAndEvidence(fixture.Key, 0, ModelQualificationState.Qualified,
                "suite", "1.0.0", fixture.Traits(1), artifact, CancellationToken.None);
        Assert.True(File.Exists(fixture.UserDatabasePath));
        fixture.SetOld(artifact.Hash.Value);
        var swept = new ArtifactGc(fixture.DataDirectory).Sweep(null, TimeSpan.Zero, false,
            Fixture.FixedTime, CancellationToken.None);
        Assert.Equal(1, swept.LiveReferenced);
        Assert.Equal(0, swept.Deleted);
        using var reopened = fixture.OpenUserStore();
        Assert.Equal(artifact, reopened.Evidence(fixture.Key, 1, CancellationToken.None)!.Artifact);
    }

    [Fact]
    public void Durable_evidence_writer_rejects_alternate_database_before_profile_or_trait_mutation()
    {
        using var fixture = new Fixture();
        var artifact = fixture.Put("evidence that must not be rooted in alternate.db");
        using var store = fixture.OpenAlternateStore();

        Assert.Throws<InvalidOperationException>(() => store.UpsertWithTraitsAndEvidence(fixture.Key, 0,
            ModelQualificationState.Qualified, "suite", "1.0.0", fixture.Traits(1), artifact,
            CancellationToken.None));

        Assert.Null(store.Get(fixture.Key, CancellationToken.None));
        Assert.Empty(store.List(CancellationToken.None));
        Assert.Empty(store.Traits(fixture.Key, 1, CancellationToken.None));
        Assert.Null(store.Evidence(fixture.Key, 1, CancellationToken.None));
        Assert.True(fixture.Artifacts.Verify(artifact.Hash, artifact.Size));
    }

    [Fact]
    public void Legacy_profile_and_traits_upsert_remains_available_in_alternate_database()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenAlternateStore();
        var traits = fixture.Traits(1);

        var profile = store.UpsertWithTraits(fixture.Key, 0, ModelQualificationState.Qualified,
            "legacy-suite", "1.0.0", traits, CancellationToken.None);

        Assert.Equal(1L, profile.ProfileRevision);
        Assert.Equal(ModelQualificationState.Qualified, store.Get(fixture.Key, CancellationToken.None)!.State);
        Assert.Equal(fixture.Snapshots(traits), fixture.Snapshots(store.Traits(fixture.Key, 1, CancellationToken.None)));
        Assert.Null(store.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    [Fact]
    public void User_database_evidence_is_a_gc_root_without_a_journal()
    {
        using var fixture = new Fixture();
        var artifact = fixture.Put("evidence durably rooted by user.db");
        using (var store = fixture.OpenUserStore())
        {
            store.UpsertWithTraitsAndEvidence(fixture.Key, 0, ModelQualificationState.Qualified,
                "suite", "1.0.0", fixture.Traits(1), artifact, CancellationToken.None);
        }

        fixture.SetOld(artifact.Hash.Value);
        var sweep = new ArtifactGc(fixture.DataDirectory).Sweep(null, TimeSpan.Zero, dryRun: false,
            Fixture.FixedTime, CancellationToken.None);

        Assert.Equal(1, sweep.LiveReferenced);
        Assert.Equal(0, sweep.Deleted);
        Assert.True(File.Exists(fixture.BlobPath(artifact.Hash.Value)));
        using var reopened = fixture.OpenUserStore();
        Assert.Equal(new ModelQualificationEvidence(1, 1, artifact),
            reopened.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    [Fact]
    public void Pre_cancelled_writer_does_not_acquire_artifact_lease_or_change_schema()
    {
        using var fixture = new Fixture();
        using var store = fixture.OpenUserStore();
        var schemaBefore = fixture.SchemaObjects();
        var reference = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('0', 64)), 0,
            "application/json", ArtifactKind.Other, Sensitivity.Sensitive);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => store.UpsertWithTraitsAndEvidence(fixture.Key, 0,
            ModelQualificationState.Qualified, "suite", "1.0.0", fixture.Traits(1), reference,
            cancellation.Token));

        Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, ".artifact-gc.lease")));
        Assert.Equal(schemaBefore, fixture.SchemaObjects());
        Assert.Null(store.Get(fixture.Key, CancellationToken.None));
        Assert.Empty(store.Traits(fixture.Key, 1, CancellationToken.None));
        Assert.Null(store.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "omnicore-m5-evidence-namespace-" + Guid.NewGuid().ToString("N"));

        public Fixture(string suffix = "")
        {
            DataDirectory = Path.Combine(_root, "data" + suffix);
            Directory.CreateDirectory(DataDirectory);
            Artifacts = new FileArtifactStore(DataDirectory);
            Key = ModelQualificationKey.For("local", "m5-evidence-namespace",
                ToolCallFormat.PromptedJson, ToolMode.Direct);
        }

        public static DateTimeOffset FixedTime { get; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public string DataDirectory { get; }
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string AlternateDatabasePath => Path.Combine(DataDirectory, "alternate.db");
        public FileArtifactStore Artifacts { get; }
        public ModelQualificationKey Key { get; }

        public SqliteModelQualificationStore OpenUserStore() => new(UserDatabasePath, () => FixedTime);
        public SqliteModelQualificationStore OpenAlternateStore() => new(AlternateDatabasePath, () => FixedTime);

        public ArtifactRef Put(string content) => Artifacts.PutText(content, "application/json",
            ArtifactKind.ModelResponse, Sensitivity.Sensitive);

        public ModelTraitRecord[] Traits(long revision) =>
        [
            new ModelTraitRecord(Key.QualificationKeyHash(), revision, "InstructionFollowing", 0.75, 0.9, 4, "namespace-fixture"),
            new ModelTraitRecord(Key.QualificationKeyHash(), revision, "ToolCallReliability", 0.8, 0.85, 3, "namespace-fixture"),
        ];

        public (string KeyHash, long Revision, string Trait, double Value, double Confidence,
            int Samples, string Source)[] Snapshots(IEnumerable<ModelTraitRecord> traits) =>
            traits.OrderBy(trait => trait.Trait, StringComparer.Ordinal)
                .Select(trait => (trait.KeyHash, trait.ProfileRevision, trait.Trait, trait.Value,
                    trait.Confidence, trait.Samples, trait.Source)).ToArray();

        public string BlobPath(string hash) => Path.Combine(DataDirectory, "blobs", "sha256",
            hash[..2], hash.Substring(2, 2), hash);

        public void SetOld(string hash) => File.SetLastWriteTimeUtc(BlobPath(hash), FixedTime.AddDays(-2).UtcDateTime);

        public string[] SchemaObjects()
        {
            using var connection = OpenReadWrite(UserDatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT type || ':' || name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name";
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result.ToArray();
        }

        private static SqliteConnection OpenReadWrite(string path)
        {
            var connection = new SqliteConnection("DataSource=" + path + ";Pooling=False");
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            ClearPool(UserDatabasePath);
            ClearPool(AlternateDatabasePath);
            Directory.Delete(_root, recursive: true);
        }

        private static void ClearPool(string path)
        {
            if (!File.Exists(path)) return;
            // Avoid treating a legal semicolon inside a filesystem path as connection options.
            using var connection = new SqliteConnection(path.Contains(';')
                ? new SqliteConnectionStringBuilder { DataSource = path }.ToString()
                : "DataSource=" + path);
            SqliteConnection.ClearPool(connection);
        }
    }
}
