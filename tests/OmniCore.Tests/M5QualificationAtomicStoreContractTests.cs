using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Contract tests for atomically writing a qualification profile and the traits tied to its
/// resulting revision. These use real, separate SQLite connections and reopen the persisted file.
/// </summary>
public sealed class M5QualificationAtomicStoreContractTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m5-atomic-store",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string DatabasePath(string directory) => Path.GetFullPath(Path.Combine(directory, "user.db"));

    private static SqliteModelQualificationStore NewStore(string directory,
        Func<DateTimeOffset>? clock = null) => new(DatabasePath(directory), clock);

    private static ModelQualificationKey LocalKey() =>
        ModelQualificationKey.For("local", "m5-atomic-store-contract", ToolCallFormat.PromptedJson,
            ToolMode.Direct);

    private static ModelTraitRecord Trait(ModelQualificationKey key, long revision, string name,
        double value, string source = "contract-fixture") =>
        new(key.QualificationKeyHash(), revision, name, value, 0.9, 5, source);

    private static (string CanonicalKey, string KeyHash, ModelQualificationState State, long Revision,
        string SuiteId, string SuiteVersion, string? StaleBySuiteVersion, string? StaleReason,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) ProfileSnapshot(ModelQualificationProfile profile) =>
        (profile.Key.CanonicalJson(), profile.KeyHash, profile.State, profile.ProfileRevision,
            profile.SuiteId, profile.SuiteVersion, profile.StaleBySuiteVersion, profile.StaleReason,
            profile.CreatedAt, profile.UpdatedAt);

    private static (string KeyHash, long Revision, string Trait, double Value, double Confidence,
        int Samples, string Source)[] TraitSnapshots(IEnumerable<ModelTraitRecord> traits) =>
        traits.OrderBy(trait => trait.Trait, StringComparer.Ordinal)
            .Select(trait => (trait.KeyHash, trait.ProfileRevision, trait.Trait, trait.Value,
                trait.Confidence, trait.Samples, trait.Source)).ToArray();

    private static void DeleteFixtureDirectory(string directory)
    {
        var databasePath = DatabasePath(directory);
        using (var connection = new SqliteConnection("DataSource=" + databasePath))
        {
            SqliteConnection.ClearPool(connection);
        }

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Competing_new_profiles_allow_one_revision_zero_writer_and_keep_only_winners_traits()
    {
        var directory = TempDir();
        try
        {
            var key = LocalKey();
            using var firstStore = NewStore(directory);
            using var secondStore = NewStore(directory);
            var firstTraits = new[] { Trait(key, 1, "InstructionFollowing", 0.8, "winner") };
            var losingTraits = new[] { Trait(key, 1, "ToolCallReliability", 0.2, "loser") };

            var stored = firstStore.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                "first-suite", "1.0.0", firstTraits, CancellationToken.None);
            Assert.Equal(1L, stored.ProfileRevision);
            var expectedProfile = ProfileSnapshot(firstStore.Get(key, CancellationToken.None)!);
            var expectedTraits = TraitSnapshots(firstStore.Traits(key, 1, CancellationToken.None));

            var conflict = Assert.Throws<ModelQualificationRevisionConflictException>(() =>
                secondStore.UpsertWithTraits(key, 0, ModelQualificationState.Calibrated,
                    "second-suite", "2.0.0", losingTraits, CancellationToken.None));
            Assert.Equal(0L, conflict.ExpectedRevision);
            Assert.Equal(1L, conflict.ActualRevision);

            using var reopened = NewStore(directory);
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(expectedProfile, ProfileSnapshot(profile!));
            Assert.Equal(expectedTraits, TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
            Assert.Empty(reopened.Traits(key, 2, CancellationToken.None));
            Assert.Single(reopened.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Obsolete_upsert_preserves_current_metadata_and_trait_history()
    {
        var directory = TempDir();
        try
        {
            var key = LocalKey();
            using (var store = NewStore(directory))
            {
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "suite-one", "1.0.0", new[] { Trait(key, 1, "InstructionFollowing", 0.6) },
                    CancellationToken.None);
                store.UpsertWithTraits(key, 1, ModelQualificationState.Calibrated,
                    "suite-two", "2.0.0", new[] { Trait(key, 2, "ToolCallReliability", 0.9) },
                    CancellationToken.None);
            }

            (string CanonicalKey, string KeyHash, ModelQualificationState State, long Revision,
                string SuiteId, string SuiteVersion, string? StaleBySuiteVersion, string? StaleReason,
                DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) expectedProfile;
            (string KeyHash, long Revision, string Trait, double Value, double Confidence,
                int Samples, string Source)[] expectedRevisionOneTraits;
            (string KeyHash, long Revision, string Trait, double Value, double Confidence,
                int Samples, string Source)[] expectedRevisionTwoTraits;
            using (var beforeConflict = NewStore(directory))
            {
                expectedProfile = ProfileSnapshot(beforeConflict.Get(key, CancellationToken.None)!);
                expectedRevisionOneTraits = TraitSnapshots(beforeConflict.Traits(key, 1, CancellationToken.None));
                expectedRevisionTwoTraits = TraitSnapshots(beforeConflict.Traits(key, 2, CancellationToken.None));
            }

            using (var staleStore = NewStore(directory))
            {
                var conflict = Assert.Throws<ModelQualificationRevisionConflictException>(() =>
                    staleStore.UpsertWithTraits(key, 1, ModelQualificationState.ProvisionallyClassified,
                        "obsolete-suite", "9.9.9",
                        new[] { Trait(key, 2, "StructuredOutputReliability", 0.1, "obsolete") },
                        CancellationToken.None));
                Assert.Equal(1L, conflict.ExpectedRevision);
                Assert.Equal(2L, conflict.ActualRevision);
            }

            using var reopened = NewStore(directory);
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(expectedProfile, ProfileSnapshot(profile!));
            Assert.Equal(expectedRevisionOneTraits,
                TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
            Assert.Equal(expectedRevisionTwoTraits,
                TraitSnapshots(reopened.Traits(key, 2, CancellationToken.None)));
            Assert.Empty(reopened.Traits(key, 3, CancellationToken.None));
            Assert.Single(reopened.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Invalid_trait_identity_is_rejected_before_profile_write_and_valid_retry_roundtrips()
    {
        var directory = TempDir();
        try
        {
            var key = LocalKey();
            using (var store = NewStore(directory))
            {
                var wrongKey = new ModelTraitRecord("different-key-hash", 1, "InstructionFollowing",
                    0.8, 0.9, 5, "invalid-key");
                Assert.Throws<ArgumentException>(() => store.UpsertWithTraits(key, 0,
                    ModelQualificationState.Qualified, "invalid-suite", "1.0.0",
                    new[] { wrongKey }, CancellationToken.None));

                var wrongRevision = new ModelTraitRecord(key.QualificationKeyHash(), 2,
                    "InstructionFollowing", 0.8, 0.9, 5, "invalid-revision");
                Assert.Throws<ArgumentException>(() => store.UpsertWithTraits(key, 0,
                    ModelQualificationState.Qualified, "invalid-suite", "1.0.0",
                    new[] { wrongRevision }, CancellationToken.None));

                Assert.Null(store.Get(key, CancellationToken.None));
                Assert.Empty(store.List(CancellationToken.None));
                Assert.Empty(store.Traits(key, 1, CancellationToken.None));
                Assert.Empty(store.Traits(key, 2, CancellationToken.None));

                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "valid-suite", "1.2.3", new[] { Trait(key, 1, "InstructionFollowing", 0.7) },
                    CancellationToken.None);
            }

            using var reopened = NewStore(directory);
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(1L, profile!.ProfileRevision);
            Assert.Equal("valid-suite", profile.SuiteId);
            Assert.Equal("1.2.3", profile.SuiteVersion);
            Assert.Equal(new[] { ("InstructionFollowing", 0.7, "contract-fixture") },
                reopened.Traits(key, 1, CancellationToken.None)
                    .Select(trait => (trait.Trait, trait.Value, trait.Source)).ToArray());
            Assert.Single(reopened.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Cancellation_after_profile_update_rolls_back_profile_and_preserves_previous_traits()
    {
        var directory = TempDir();
        try
        {
            var key = LocalKey();
            var initialTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            var cancellationTime = initialTime.AddMinutes(1);
            var cancelOnClock = false;
            var cancelledCoreClockCalls = 0;
            using var source = new CancellationTokenSource();
            using (var store = NewStore(directory, () =>
            {
                if (cancelOnClock)
                {
                    cancelledCoreClockCalls++;
                    source.Cancel();
                    cancelOnClock = false;
                    return cancellationTime;
                }

                return initialTime;
            }))
            {
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "original-suite", "1.0.0",
                    new[]
                    {
                        Trait(key, 1, "InstructionFollowing", 0.4, "original"),
                        Trait(key, 1, "ToolCallReliability", 0.5, "original"),
                    }, CancellationToken.None);

                cancelOnClock = true;
                Assert.Throws<OperationCanceledException>(() => store.UpsertWithTraits(key, 1,
                    ModelQualificationState.Calibrated, "cancelled-suite", "2.0.0",
                    new[] { Trait(key, 2, "InstructionFollowing", 1.0, "cancelled") },
                    source.Token));
                Assert.Equal(1, cancelledCoreClockCalls);
            }

            using var reopened = NewStore(directory);
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.Qualified, profile!.State);
            Assert.Equal(1L, profile.ProfileRevision);
            Assert.Equal("original-suite", profile.SuiteId);
            Assert.Equal("1.0.0", profile.SuiteVersion);
            Assert.Equal(initialTime, profile.CreatedAt);
            Assert.Equal(initialTime, profile.UpdatedAt);
            Assert.Equal(new[]
                {
                    ("InstructionFollowing", 0.4, "original"),
                    ("ToolCallReliability", 0.5, "original"),
                }, reopened.Traits(key, 1, CancellationToken.None)
                .Select(trait => (trait.Trait, trait.Value, trait.Source)).ToArray());
            Assert.Empty(reopened.Traits(key, 2, CancellationToken.None));
            Assert.Single(reopened.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }
}
