using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Durable profile-revision to CAS evidence contract, exercised with private SQLite/CAS fixtures.</summary>
public sealed class M5QualificationEvidenceStoreTests
{
    [Fact]
    public void Evidence_profile_and_traits_roundtrip_after_reopen()
    {
        using var fixture = new Fixture();
        var key = fixture.Key;
        var traits = fixture.Traits(1, "initial");
        var artifact = fixture.Put("complete qualification result revision one");

        using (var store = fixture.OpenStore())
        {
            var profile = store.UpsertWithTraitsAndEvidence(key, 0, ModelQualificationState.Qualified,
                "suite", "1.0.0", traits, artifact, CancellationToken.None);
            Assert.Equal(1L, profile.ProfileRevision);
        }

        using var reopened = fixture.OpenStore();
        Assert.Equal(ModelQualificationState.Qualified, reopened.Get(key, CancellationToken.None)!.State);
        Assert.Equal(fixture.TraitSnapshots(traits),
            fixture.TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
        Assert.Equal(new ModelQualificationEvidence(1, 1, artifact),
            reopened.Evidence(key, 1, CancellationToken.None));
        Assert.True(fixture.Artifacts.Verify(artifact.Hash, artifact.Size));
    }

    [Fact]
    public void New_revision_retains_history_and_stale_revision_copies_source_reference()
    {
        using var fixture = new Fixture();
        var key = fixture.Key;
        var first = fixture.Put("qualification result from run one");
        var second = fixture.Put("qualification result from run two");
        using (var store = fixture.OpenStore())
        {
            store.UpsertWithTraitsAndEvidence(key, 0, ModelQualificationState.Qualified, "suite", "1.0.0",
                fixture.Traits(1, "run-one"), first, CancellationToken.None);
            store.UpsertWithTraitsAndEvidence(key, 1, ModelQualificationState.Qualified, "suite", "2.0.0",
                fixture.Traits(2, "run-two"), second, CancellationToken.None);
            store.MarkStale(key, 2, "3.0.0", CancellationToken.None);
        }

        using var reopened = fixture.OpenStore();
        Assert.Equal(new ModelQualificationEvidence(1, 1, first), reopened.Evidence(key, 1, CancellationToken.None));
        Assert.Equal(new ModelQualificationEvidence(2, 2, second), reopened.Evidence(key, 2, CancellationToken.None));
        Assert.Equal(new ModelQualificationEvidence(3, 2, second), reopened.Evidence(key, 3, CancellationToken.None));
        Assert.Equal(ModelQualificationState.Stale, reopened.Get(key, CancellationToken.None)!.State);
        Assert.Equal(fixture.TraitSnapshots(fixture.Traits(1, "run-one")),
            fixture.TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
        Assert.Equal(fixture.TraitSnapshots(fixture.Traits(2, "run-two")),
            fixture.TraitSnapshots(reopened.Traits(key, 2, CancellationToken.None)));
        Assert.Equal(fixture.TraitSnapshots(fixture.Traits(3, "run-two")),
            fixture.TraitSnapshots(reopened.Traits(key, 3, CancellationToken.None)));
    }

    [Fact]
    public void Legacy_profile_without_evidence_is_not_backfilled_during_schema_upgrade()
    {
        using var fixture = new Fixture();
        using (var store = fixture.OpenStore())
        {
            store.UpsertWithTraits(fixture.Key, 0, ModelQualificationState.Qualified, "legacy-suite", "1.0.0",
                fixture.Traits(1, "legacy"), CancellationToken.None);
        }

        fixture.DropEvidenceSchemaAndMarker();

        using var migrated = fixture.OpenStore();
        Assert.Equal(ModelQualificationState.Qualified, migrated.Get(fixture.Key, CancellationToken.None)!.State);
        Assert.Equal(1L, migrated.Get(fixture.Key, CancellationToken.None)!.ProfileRevision);
        Assert.Equal(fixture.TraitSnapshots(fixture.Traits(1, "legacy")),
            fixture.TraitSnapshots(migrated.Traits(fixture.Key, 1, CancellationToken.None)));
        Assert.Null(migrated.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    [Fact]
    public void Evidence_insert_trigger_rolls_back_profile_and_traits()
    {
        using var fixture = new Fixture();
        var artifact = fixture.Put("artifact left unrooted when database commit fails");
        using (var store = fixture.OpenStore())
        {
            fixture.CreateAbortTrigger("evidence");
            Assert.Throws<SqliteException>(() => store.UpsertWithTraitsAndEvidence(fixture.Key, 0,
                ModelQualificationState.Qualified, "suite", "1.0.0", fixture.Traits(1, "failed"),
                artifact, CancellationToken.None));
        }

        using var reopened = fixture.OpenStore();
        Assert.Null(reopened.Get(fixture.Key, CancellationToken.None));
        Assert.Empty(reopened.List(CancellationToken.None));
        Assert.Empty(reopened.Traits(fixture.Key, 1, CancellationToken.None));
        Assert.Null(reopened.Evidence(fixture.Key, 1, CancellationToken.None));
        Assert.True(fixture.Artifacts.Verify(artifact.Hash, artifact.Size));
    }

    [Fact]
    public void Trait_insert_trigger_rolls_back_profile_and_evidence_reference()
    {
        using var fixture = new Fixture();
        var artifact = fixture.Put("artifact left unrooted when trait insert fails");
        using (var store = fixture.OpenStore())
        {
            fixture.CreateAbortTrigger("traits");
            Assert.Throws<SqliteException>(() => store.UpsertWithTraitsAndEvidence(fixture.Key, 0,
                ModelQualificationState.Qualified, "suite", "1.0.0", fixture.Traits(1, "failed"),
                artifact, CancellationToken.None));
        }

        using var reopened = fixture.OpenStore();
        Assert.Null(reopened.Get(fixture.Key, CancellationToken.None));
        Assert.Empty(reopened.Traits(fixture.Key, 1, CancellationToken.None));
        Assert.Null(reopened.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData("traits")]
    [InlineData("evidence")]
    public void Failed_requalification_preserves_calibrated_profile_traits_and_old_evidence(string table)
    {
        using var fixture = new Fixture();
        var original = fixture.Put("original calibrated audit result");
        var replacement = fixture.Put("requalification that cannot commit");
        using (var store = fixture.OpenStore())
        {
            store.UpsertWithTraitsAndEvidence(fixture.Key, 0, ModelQualificationState.Calibrated,
                "original-suite", "1.0.0", fixture.Traits(1, "original"), original, CancellationToken.None);
            fixture.CreateAbortTrigger(table);
            Assert.Throws<SqliteException>(() => store.UpsertWithTraitsAndEvidence(fixture.Key, 1,
                ModelQualificationState.Qualified, "replacement-suite", "2.0.0",
                fixture.Traits(2, "replacement"), replacement, CancellationToken.None));
        }
        using var reopened = fixture.OpenStore();
        var profile = reopened.Get(fixture.Key, CancellationToken.None)!;
        Assert.Equal(1, profile.ProfileRevision);
        Assert.Equal(ModelQualificationState.Calibrated, profile.State);
        Assert.Equal("original-suite", profile.SuiteId);
        Assert.Equal(fixture.TraitSnapshots(fixture.Traits(1, "original")),
            fixture.TraitSnapshots(reopened.Traits(fixture.Key, 1, CancellationToken.None)));
        Assert.Empty(reopened.Traits(fixture.Key, 2, CancellationToken.None));
        Assert.Equal(new ModelQualificationEvidence(1, 1, original), reopened.Evidence(fixture.Key, 1, CancellationToken.None));
        Assert.Null(reopened.Evidence(fixture.Key, 2, CancellationToken.None));
    }

    [Fact]
    public void Writer_holds_gc_file_lease_while_inside_profile_transaction()
    {
        using var fixture = new Fixture();
        var artifact = fixture.Put("audit result whose transaction owns the GC lease");
        var inspectLease = false;
        var checks = 0;
        using var store = fixture.OpenStore(() =>
        {
            if (inspectLease)
            {
                checks++;
                Assert.Throws<IOException>(() =>
                {
                    using var competingLease = new FileStream(Path.Combine(fixture.DataDirectory, ".artifact-gc.lease"),
                        FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                });
            }
            return Fixture.FixedTime;
        });
        inspectLease = true;
        store.UpsertWithTraitsAndEvidence(fixture.Key, 0, ModelQualificationState.Qualified,
            "suite", "1.0.0", fixture.Traits(1, "lease"), artifact, CancellationToken.None);
        Assert.Equal(1, checks);
        using var releasedLease = new FileStream(Path.Combine(fixture.DataDirectory, ".artifact-gc.lease"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(store.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    [Fact]
    public void Cancellation_after_profile_mutation_rolls_back_profile_traits_and_evidence()
    {
        using var fixture = new Fixture();
        var artifact = fixture.Put("artifact left unrooted after cancellation");
        var cancelOnClock = false;
        var canceledClockCalls = 0;
        using var source = new CancellationTokenSource();
        using (var store = fixture.OpenStore(() =>
        {
            if (cancelOnClock)
            {
                canceledClockCalls++;
                source.Cancel();
            }

            return Fixture.FixedTime;
        }))
        {
            cancelOnClock = true;
            Assert.Throws<OperationCanceledException>(() => store.UpsertWithTraitsAndEvidence(fixture.Key, 0,
                ModelQualificationState.Qualified, "suite", "1.0.0", fixture.Traits(1, "cancelled"),
                artifact, source.Token));
            Assert.Equal(1, canceledClockCalls);
        }

        using var reopened = fixture.OpenStore();
        Assert.Null(reopened.Get(fixture.Key, CancellationToken.None));
        Assert.Empty(reopened.Traits(fixture.Key, 1, CancellationToken.None));
        Assert.Null(reopened.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("wrong-size")]
    public void Missing_or_invalid_blob_is_rejected_without_database_mutation(string invalidity)
    {
        using var fixture = new Fixture();
        var artifact = fixture.Put("blob verified before evidence reference commit");
        switch (invalidity)
        {
            case "missing":
                File.Delete(fixture.BlobPath(artifact.Hash.Value));
                break;
            case "corrupt":
                File.WriteAllText(fixture.BlobPath(artifact.Hash.Value), new string('x', (int)artifact.Size));
                break;
            case "wrong-size":
                artifact = artifact with { Size = artifact.Size + 1 };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidity), invalidity, "Unknown invalidity fixture.");
        }

        using (var store = fixture.OpenStore())
        {
            Assert.Throws<InvalidDataException>(() => store.UpsertWithTraitsAndEvidence(fixture.Key, 0,
                ModelQualificationState.Qualified, "suite", "1.0.0", fixture.Traits(1, "invalid"),
                artifact, CancellationToken.None));
        }

        using var reopened = fixture.OpenStore();
        Assert.Null(reopened.Get(fixture.Key, CancellationToken.None));
        Assert.Empty(reopened.Traits(fixture.Key, 1, CancellationToken.None));
        Assert.Null(reopened.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    [Fact]
    public void Revision_conflict_keeps_only_winning_profile_traits_and_evidence()
    {
        using var fixture = new Fixture();
        var winnerArtifact = fixture.Put("winner evidence");
        var loserArtifact = fixture.Put("loser evidence");
        using (var first = fixture.OpenStore())
        using (var second = fixture.OpenStore())
        {
            first.UpsertWithTraitsAndEvidence(fixture.Key, 0, ModelQualificationState.Qualified,
                "winner-suite", "1.0.0", fixture.Traits(1, "winner"), winnerArtifact, CancellationToken.None);
            var conflict = Assert.Throws<ModelQualificationRevisionConflictException>(() =>
                second.UpsertWithTraitsAndEvidence(fixture.Key, 0, ModelQualificationState.Calibrated,
                    "loser-suite", "2.0.0", fixture.Traits(1, "loser"), loserArtifact, CancellationToken.None));
            Assert.Equal(0L, conflict.ExpectedRevision);
            Assert.Equal(1L, conflict.ActualRevision);
        }

        using var reopened = fixture.OpenStore();
        var profile = reopened.Get(fixture.Key, CancellationToken.None)!;
        Assert.Equal(ModelQualificationState.Qualified, profile.State);
        Assert.Equal("winner-suite", profile.SuiteId);
        Assert.Equal(fixture.TraitSnapshots(fixture.Traits(1, "winner")),
            fixture.TraitSnapshots(reopened.Traits(fixture.Key, 1, CancellationToken.None)));
        Assert.Equal(new ModelQualificationEvidence(1, 1, winnerArtifact),
            reopened.Evidence(fixture.Key, 1, CancellationToken.None));
        Assert.Null(reopened.Evidence(fixture.Key, 2, CancellationToken.None));
        Assert.True(fixture.Artifacts.Verify(loserArtifact.Hash, loserArtifact.Size));
    }

    [Fact]
    public void Grace_zero_gc_keeps_durable_evidence_and_sweeps_an_unreferenced_blob()
    {
        using var fixture = new Fixture();
        var evidence = fixture.Put("rooted durable qualification evidence");
        var orphan = fixture.Put("old orphan blob");
        using (var store = fixture.OpenStore())
        {
            store.UpsertWithTraitsAndEvidence(fixture.Key, 0, ModelQualificationState.Qualified,
                "suite", "1.0.0", fixture.Traits(1, "gc"), evidence, CancellationToken.None);
        }

        fixture.SetOld(evidence.Hash.Value);
        fixture.SetOld(orphan.Hash.Value);
        var result = new ArtifactGc(fixture.DataDirectory).Sweep(null, TimeSpan.Zero, false,
            Fixture.FixedTime, CancellationToken.None);

        Assert.Equal(1, result.LiveReferenced);
        Assert.Equal(1, result.Deleted);
        Assert.True(File.Exists(fixture.BlobPath(evidence.Hash.Value)));
        Assert.False(File.Exists(fixture.BlobPath(orphan.Hash.Value)));
        using var reopened = fixture.OpenStore();
        Assert.Equal(new ModelQualificationEvidence(1, 1, evidence),
            reopened.Evidence(fixture.Key, 1, CancellationToken.None));
    }

    [Fact]
    public void Installed_evidence_marker_with_missing_table_fails_store_open_and_gc_closed()
    {
        using var fixture = new Fixture();
        var orphan = fixture.Put("must survive corrupt schema failure");
        fixture.SetOld(orphan.Hash.Value);
        using (fixture.OpenStore()) { }
        fixture.DropEvidenceTableOnly();

        Assert.Throws<InvalidDataException>(() => fixture.OpenStore());
        Assert.Throws<InvalidDataException>(() => new ArtifactGc(fixture.DataDirectory).Sweep(null,
            TimeSpan.Zero, false, Fixture.FixedTime, CancellationToken.None));
        Assert.True(File.Exists(fixture.BlobPath(orphan.Hash.Value)),
            "GC must abort before sweeping when its durable-root schema is missing");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omnicore-m5-evidence-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            DataDirectory = Path.Combine(_root, "data");
            Directory.CreateDirectory(DataDirectory);
            Artifacts = new FileArtifactStore(DataDirectory);
            Key = ModelQualificationKey.For("local", "m5-evidence-store", ToolCallFormat.PromptedJson, ToolMode.Direct);
        }

        public static DateTimeOffset FixedTime { get; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public string DataDirectory { get; }
        public string DatabasePath => Path.Combine(DataDirectory, "user.db");
        public FileArtifactStore Artifacts { get; }
        public ModelQualificationKey Key { get; }

        public SqliteModelQualificationStore OpenStore(Func<DateTimeOffset>? clock = null) =>
            new(DatabasePath, clock ?? (() => FixedTime));

        public ArtifactRef Put(string text) =>
            Artifacts.PutText(text, "application/json", ArtifactKind.ModelResponse, Sensitivity.Normal);

        public ModelTraitRecord[] Traits(long revision, string source) =>
        [
            new ModelTraitRecord(Key.QualificationKeyHash(), revision, "InstructionFollowing", 0.75, 0.9, 4, source),
            new ModelTraitRecord(Key.QualificationKeyHash(), revision, "ToolCallReliability", 0.8, 0.85, 3, source),
        ];

        public (string KeyHash, long Revision, string Trait, double Value, double Confidence,
            int Samples, string Source)[] TraitSnapshots(IEnumerable<ModelTraitRecord> records) =>
            records.OrderBy(record => record.Trait, StringComparer.Ordinal)
                .Select(record => (record.KeyHash, record.ProfileRevision, record.Trait, record.Value,
                    record.Confidence, record.Samples, record.Source)).ToArray();

        public string BlobPath(string hash) => Path.Combine(DataDirectory, "blobs", "sha256", hash[..2], hash.Substring(2, 2), hash);

        public void SetOld(string hash) => File.SetLastWriteTimeUtc(BlobPath(hash), FixedTime.AddDays(-2).UtcDateTime);

        public void CreateAbortTrigger(string table)
        {
            if (table is not ("evidence" or "traits")) throw new ArgumentOutOfRangeException(nameof(table));
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = table == "evidence"
                ? "CREATE TRIGGER abort_m5_evidence BEFORE INSERT ON model_qualification_evidence BEGIN SELECT RAISE(ABORT, 'fixture evidence insert failure'); END"
                : "CREATE TRIGGER abort_m5_traits BEFORE INSERT ON model_traits BEGIN SELECT RAISE(ABORT, 'fixture traits insert failure'); END";
            command.ExecuteNonQuery();
        }

        public void DropEvidenceTableOnly()
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE model_qualification_evidence";
            command.ExecuteNonQuery();
        }

        public void DropEvidenceSchemaAndMarker()
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE model_qualification_evidence; DELETE FROM model_profile_migrations WHERE name = 'm5-qualification-evidence-v1'";
            command.ExecuteNonQuery();
        }

        private SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection("DataSource=" + DatabasePath + ";Pooling=False");
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            ClearPool(DatabasePath);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static void ClearPool(string path)
        {
            if (!File.Exists(path)) return;
            using var connection = new SqliteConnection("DataSource=" + path);
            SqliteConnection.ClearPool(connection);
        }
    }
}
