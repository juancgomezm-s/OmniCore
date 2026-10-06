using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Regression contracts for retaining usable qualification evidence across MarkStale.
/// All fixtures are isolated local SQLite databases; no provider or network is involved.
/// </summary>
public sealed class M5QualificationStaleEvidenceTests
{
    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-stale-evidence",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string DatabasePath(string directory) =>
        Path.GetFullPath(Path.Combine(directory, "user.db"));

    private static SqliteModelQualificationStore NewStore(string directory,
        Func<DateTimeOffset>? clock = null) => new(DatabasePath(directory), clock);

    private static ModelDefinition Model() => new("stale-evidence-model", "local", 8192, 8192, 2048);

    private static ModelQualificationKey Key() =>
        ModelQualificationHost.QualificationKeyFor(Model(), provider: null);

    private static ModelTraitRecord[] Traits(ModelQualificationKey key, long revision) =>
    [
        new(key.QualificationKeyHash(), revision, "InstructionFollowing", 0.82, 0.91, 14, "fixture-suite"),
        new(key.QualificationKeyHash(), revision, "StructuredOutputReliability", 0.73, 0.88, 9, "fixture-suite"),
    ];

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
    public void MarkStale_carries_traits_to_current_revision_and_keeps_history_usable_after_reopen()
    {
        var directory = TempDir();
        try
        {
            var key = Key();
            var initialTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            var staleTime = initialTime.AddMinutes(1);
            using (var store = NewStore(directory, () => initialTime))
            {
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(key, 1), CancellationToken.None);
            }

            ModelQualificationProfile stale;
            using (var store = NewStore(directory, () => staleTime))
                stale = store.MarkStale(key, 1, "2.0.0", CancellationToken.None);

            Assert.Equal(ModelQualificationState.Stale, stale.State);
            Assert.Equal(2L, stale.ProfileRevision);
            Assert.Equal("2.0.0", stale.StaleBySuiteVersion);

            using var reopened = NewStore(directory, () => staleTime);
            var persistedProfile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(persistedProfile);
            Assert.Equal(ModelQualificationState.Stale, persistedProfile!.State);
            Assert.Equal(2L, persistedProfile.ProfileRevision);
            Assert.Equal("quick", persistedProfile.SuiteId);
            Assert.Equal("1.0.0", persistedProfile.SuiteVersion);
            Assert.Equal("2.0.0", persistedProfile.StaleBySuiteVersion);

            var historical = TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None));
            var current = TraitSnapshots(reopened.Traits(key, 2, CancellationToken.None));
            Assert.Equal(TraitSnapshots(Traits(key, 1)), historical);
            Assert.Equal(TraitSnapshots(Traits(key, 2)), current);
            Assert.Equal(historical.Select(item => (item.Trait, item.Value, item.Confidence,
                    item.Samples, item.Source)),
                current.Select(item => (item.Trait, item.Value, item.Confidence, item.Samples, item.Source)));

            var usable = ModelQualificationHost.UsableTraits(reopened, Model(), provider: null,
                CancellationToken.None);
            Assert.NotNull(usable);
            Assert.Equal(0.82, usable!["InstructionFollowing"]);
            Assert.Equal(0.73, usable["StructuredOutputReliability"]);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Trait_insert_failure_rolls_back_stale_profile_and_revision_copy()
    {
        var directory = TempDir();
        try
        {
            var key = Key();
            var initialTime = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
            using (var store = NewStore(directory, () => initialTime))
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(key, 1), CancellationToken.None);

            var expectedProfileSnapshot = ProfileSnapshot(ReadProfile(directory, key));
            var expectedRevisionOneTraits = TraitSnapshots(ReadTraits(directory, key, 1));
            using (var connection = new SqliteConnection("DataSource=" + DatabasePath(directory)))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fixture_abort_stale_trait_copy
                    BEFORE INSERT ON model_traits
                    WHEN NEW.profile_revision = 2
                    BEGIN
                        SELECT RAISE(ABORT, 'fixture blocks copied stale evidence');
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            using (var store = NewStore(directory, () => initialTime.AddMinutes(1)))
            {
                Assert.Throws<SqliteException>(() =>
                    store.MarkStale(key, 1, "2.0.0", CancellationToken.None));
            }

            using var reopened = NewStore(directory, () => initialTime.AddMinutes(2));
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(expectedProfileSnapshot, ProfileSnapshot(profile!));
            Assert.Equal(expectedRevisionOneTraits,
                TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
            Assert.Empty(reopened.Traits(key, 2, CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Cancellation_during_stale_transaction_leaves_profile_and_traits_unchanged()
    {
        var directory = TempDir();
        try
        {
            var key = Key();
            var initialTime = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
            var cancellationTime = initialTime.AddMinutes(1);
            var cancelOnClock = false;
            var cancelledMarkStaleClockCalls = 0;
            using var cancellation = new CancellationTokenSource();
            using (var store = NewStore(directory, () =>
            {
                if (cancelOnClock)
                {
                    cancelledMarkStaleClockCalls++;
                    cancellation.Cancel();
                    cancelOnClock = false;
                    return cancellationTime;
                }

                return initialTime;
            }))
            {
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(key, 1), CancellationToken.None);
                cancelOnClock = true;

                Assert.Throws<OperationCanceledException>(() =>
                    store.MarkStale(key, 1, "2.0.0", cancellation.Token));
            }

            Assert.Equal(1, cancelledMarkStaleClockCalls);
            using var reopened = NewStore(directory, () => initialTime.AddMinutes(2));
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.Qualified, profile!.State);
            Assert.Equal(1L, profile.ProfileRevision);
            Assert.Equal("quick", profile.SuiteId);
            Assert.Equal("1.0.0", profile.SuiteVersion);
            Assert.Null(profile.StaleBySuiteVersion);
            Assert.Equal(initialTime, profile.CreatedAt);
            Assert.Equal(initialTime, profile.UpdatedAt);
            Assert.Equal(TraitSnapshots(Traits(key, 1)),
                TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
            Assert.Empty(reopened.Traits(key, 2, CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void MarkStale_rejects_revision_overflow_without_writing()
    {
        var directory = TempDir();
        try
        {
            var key = Key();
            var initialTime = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            using (var store = NewStore(directory, () => initialTime))
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(key, 1), CancellationToken.None);

            const long maxRevision = long.MaxValue;
            using (var connection = new SqliteConnection("DataSource=" + DatabasePath(directory)))
            {
                connection.Open();
                using (var profileCommand = connection.CreateCommand())
                {
                    profileCommand.CommandText =
                        "UPDATE model_profiles SET profile_revision = $revision WHERE key_hash = $keyHash";
                    profileCommand.Parameters.AddWithValue("$revision", maxRevision);
                    profileCommand.Parameters.AddWithValue("$keyHash", key.QualificationKeyHash());
                    Assert.Equal(1, profileCommand.ExecuteNonQuery());
                }

                using (var traitsCommand = connection.CreateCommand())
                {
                    traitsCommand.CommandText =
                        "UPDATE model_traits SET profile_revision = $revision WHERE key_hash = $keyHash";
                    traitsCommand.Parameters.AddWithValue("$revision", maxRevision);
                    traitsCommand.Parameters.AddWithValue("$keyHash", key.QualificationKeyHash());
                    Assert.Equal(2, traitsCommand.ExecuteNonQuery());
                }
            }

            var expectedProfileSnapshot = ProfileSnapshot(ReadProfile(directory, key));
            var expectedTraits = TraitSnapshots(ReadTraits(directory, key, maxRevision));
            using (var store = NewStore(directory, () => initialTime.AddMinutes(1)))
            {
                Assert.Throws<OverflowException>(() =>
                    store.MarkStale(key, maxRevision, "2.0.0", CancellationToken.None));
            }

            using var reopened = NewStore(directory, () => initialTime.AddMinutes(2));
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(expectedProfileSnapshot, ProfileSnapshot(profile!));
            Assert.Equal(ModelQualificationState.Qualified, profile!.State);
            Assert.Equal(maxRevision, profile.ProfileRevision);
            Assert.Equal(expectedTraits, TraitSnapshots(reopened.Traits(key, maxRevision, CancellationToken.None)));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Stale_revision_conflict_does_not_overwrite_new_profile_or_traits()
    {
        var directory = TempDir();
        try
        {
            var key = Key();
            var initialTime = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
            using (var store = NewStore(directory, () => initialTime))
            {
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(key, 1), CancellationToken.None);
                store.MarkStale(key, 1, "2.0.0", CancellationToken.None);
            }

            var expectedProfileSnapshot = ProfileSnapshot(ReadProfile(directory, key));
            var expectedRevisionOneTraits = TraitSnapshots(ReadTraits(directory, key, 1));
            var expectedRevisionTwoTraits = TraitSnapshots(ReadTraits(directory, key, 2));
            using (var staleStore = NewStore(directory, () => initialTime.AddMinutes(2)))
            {
                var conflict = Assert.Throws<ModelQualificationRevisionConflictException>(() =>
                    staleStore.MarkStale(key, 1, "3.0.0", CancellationToken.None));
                Assert.Equal(1L, conflict.ExpectedRevision);
                Assert.Equal(2L, conflict.ActualRevision);
            }

            using var reopened = NewStore(directory, () => initialTime.AddMinutes(3));
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(expectedProfileSnapshot, ProfileSnapshot(profile!));
            Assert.Equal(expectedRevisionOneTraits,
                TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
            Assert.Equal(expectedRevisionTwoTraits,
                TraitSnapshots(reopened.Traits(key, 2, CancellationToken.None)));
            Assert.Empty(reopened.Traits(key, 3, CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private static ModelQualificationProfile ReadProfile(string directory, ModelQualificationKey key)
    {
        using var store = NewStore(directory);
        return store.Get(key, CancellationToken.None)!;
    }

    [Fact]
    public void Reopen_repairs_empty_suite_stale_revision_from_retained_measurements_idempotently()
    {
        var directory = TempDir();
        try
        {
            var key = Key();
            var time = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
            using (var store = NewStore(directory, () => time))
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(key, 1), CancellationToken.None);
            // Exact persisted shape produced by the former MarkStale: revision2 has no
            // measurements, while revision1 retains the complete original trait set.
            using (var connection = new SqliteConnection("DataSource=" + DatabasePath(directory)))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM model_profile_migrations WHERE name = 'm5-suite-stale-trait-copy-v1'";
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = """
                    UPDATE model_profiles SET state = 'Stale', profile_revision = 2,
                        stale_by_suite_version = '2.0.0' WHERE key_hash = $hash
                    """;
                command.Parameters.AddWithValue("$hash", key.QualificationKeyHash());
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var store = NewStore(directory, () => time.AddHours(1));
                var profile = store.Get(key, CancellationToken.None)!;
                Assert.Equal(ModelQualificationState.Stale, profile.State);
                Assert.Equal(2L, profile.ProfileRevision);
                Assert.Equal(time, profile.CreatedAt);
                Assert.Equal(time, profile.UpdatedAt);
                Assert.Equal("1.0.0", profile.SuiteVersion);
                Assert.Equal("2.0.0", profile.StaleBySuiteVersion);
                Assert.Equal(TraitSnapshots(Traits(key, 1)),
                    TraitSnapshots(store.Traits(key, 1, CancellationToken.None)));
                Assert.Equal(TraitSnapshots(Traits(key, 2)),
                    TraitSnapshots(store.Traits(key, 2, CancellationToken.None)));
            }
            // The upgrade is one-shot: a later explicit clearing is not resurrected.
            using (var store = NewStore(directory))
                store.SaveTraits(key, 2, [], CancellationToken.None);
            using (var store = NewStore(directory))
            {
                Assert.Empty(store.Traits(key, 2, CancellationToken.None));
                Assert.Equal(TraitSnapshots(Traits(key, 1)),
                    TraitSnapshots(store.Traits(key, 1, CancellationToken.None)));
            }
        }
        finally { DeleteFixtureDirectory(directory); }
    }

    [Fact]
    public void Reopen_repair_does_not_merge_or_overwrite_an_existing_current_trait_set()
    {
        var directory = TempDir();
        try
        {
            var key = Key();
            var time = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
            using (var store = NewStore(directory, () => time))
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(key, 1), CancellationToken.None);

            var deliberatelyCurrent = new ModelTraitRecord(key.QualificationKeyHash(), 2,
                "InstructionFollowing", 0.31, 0.42, 3, "already-current");
            MarkLegacyStale(directory, key, "2.0.0", staleReason: null,
                currentTraits: [deliberatelyCurrent]);

            using (var store = NewStore(directory, () => time.AddHours(1)))
            {
                Assert.Equal(TraitSnapshots(Traits(key, 1)),
                    TraitSnapshots(store.Traits(key, 1, CancellationToken.None)));
                Assert.Equal(TraitSnapshots(new[] { deliberatelyCurrent }),
                    TraitSnapshots(store.Traits(key, 2, CancellationToken.None)));
                Assert.True(MigrationApplied(directory, "m5-suite-stale-trait-copy-v1"));
            }

            using var reopened = NewStore(directory, () => time.AddHours(2));
            Assert.Equal(TraitSnapshots(new[] { deliberatelyCurrent }),
                TraitSnapshots(reopened.Traits(key, 2, CancellationToken.None)));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Reopen_repair_skips_stale_profiles_without_suite_version_or_with_route_migration_reason()
    {
        var directory = TempDir();
        try
        {
            var noSuiteVersionKey = ModelQualificationKey.For("fixture", "stale-no-suite-version",
                ToolCallFormat.PromptedJson, ToolMode.Direct);
            var routeMigrationKey = ModelQualificationKey.For("fixture", "stale-route-migration",
                ToolCallFormat.PromptedJson, ToolMode.Direct);
            var time = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
            using (var store = NewStore(directory, () => time))
            {
                store.UpsertWithTraits(noSuiteVersionKey, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(noSuiteVersionKey, 1), CancellationToken.None);
                store.UpsertWithTraits(routeMigrationKey, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(routeMigrationKey, 1), CancellationToken.None);
            }

            MarkLegacyStale(directory, noSuiteVersionKey, staleBySuiteVersion: null, staleReason: null);
            MarkLegacyStale(directory, routeMigrationKey, staleBySuiteVersion: "2.0.0",
                staleReason: "route-identity-migration", removeRepairMarker: false);

            using var reopened = NewStore(directory, () => time.AddHours(1));
            foreach (var key in new[] { noSuiteVersionKey, routeMigrationKey })
            {
                var profile = reopened.Get(key, CancellationToken.None);
                Assert.NotNull(profile);
                Assert.Equal(ModelQualificationState.Stale, profile!.State);
                Assert.Equal(2L, profile.ProfileRevision);
                Assert.Empty(reopened.Traits(key, 2, CancellationToken.None));
                Assert.Equal(TraitSnapshots(Traits(key, 1)),
                    TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
            }

            Assert.True(MigrationApplied(directory, "m5-suite-stale-trait-copy-v1"));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Reopen_repair_does_not_invent_traits_when_previous_revision_has_no_measurements()
    {
        var directory = TempDir();
        try
        {
            var key = ModelQualificationKey.For("fixture", "stale-without-history",
                ToolCallFormat.PromptedJson, ToolMode.Direct);
            var time = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            using (var store = NewStore(directory, () => time))
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", [], CancellationToken.None);

            MarkLegacyStale(directory, key, "2.0.0", staleReason: null);

            using var reopened = NewStore(directory, () => time.AddHours(1));
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.Stale, profile!.State);
            Assert.Equal(2L, profile.ProfileRevision);
            Assert.Equal("2.0.0", profile.StaleBySuiteVersion);
            Assert.Empty(reopened.Traits(key, 1, CancellationToken.None));
            Assert.Empty(reopened.Traits(key, 2, CancellationToken.None));
            Assert.True(MigrationApplied(directory, "m5-suite-stale-trait-copy-v1"));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Failed_repair_rolls_back_copied_traits_and_markers_then_reopen_repairs()
    {
        var directory = TempDir();
        try
        {
            var key = Key();
            var time = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
            using (var store = NewStore(directory, () => time))
                store.UpsertWithTraits(key, 0, ModelQualificationState.Qualified,
                    "quick", "1.0.0", Traits(key, 1), CancellationToken.None);

            MarkLegacyStale(directory, key, "2.0.0", staleReason: null,
                removeRepairMarker: true, removeRouteMarker: true);
            var profileBefore = RawProfileSnapshot(directory, key);
            var revisionOneBefore = RawTraitSnapshots(directory, key, 1);
            using (var connection = new SqliteConnection("DataSource=" + DatabasePath(directory)))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fixture_abort_legacy_stale_repair
                    BEFORE INSERT ON model_traits
                    WHEN NEW.profile_revision = 2
                    BEGIN
                        SELECT RAISE(ABORT, 'fixture blocks stale trait repair');
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            Assert.Throws<SqliteException>(() => NewStore(directory, () => time.AddHours(1)).Dispose());

            Assert.Equal(profileBefore, RawProfileSnapshot(directory, key));
            Assert.Equal(revisionOneBefore, RawTraitSnapshots(directory, key, 1));
            Assert.Empty(RawTraitSnapshots(directory, key, 2));
            Assert.False(MigrationApplied(directory, "m5-suite-stale-trait-copy-v1"));
            Assert.False(MigrationApplied(directory, "adr0046-route-qualification-v1"));

            using (var connection = new SqliteConnection("DataSource=" + DatabasePath(directory)))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TRIGGER fixture_abort_legacy_stale_repair";
                command.ExecuteNonQuery();
                // SQLite DDL can report the connection's last DML change count.
                // Verify the actual schema postcondition instead of a stale row counter.
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='fixture_abort_legacy_stale_repair'";
                Assert.Equal(0L, (long)command.ExecuteScalar()!);
            }

            using var reopened = NewStore(directory, () => time.AddHours(2));
            Assert.Equal(profileBefore, RawProfileSnapshot(directory, key));
            Assert.Equal(revisionOneBefore, TraitSnapshots(reopened.Traits(key, 1, CancellationToken.None)));
            Assert.Equal(TraitSnapshots(Traits(key, 2)),
                TraitSnapshots(reopened.Traits(key, 2, CancellationToken.None)));
            Assert.True(MigrationApplied(directory, "m5-suite-stale-trait-copy-v1"));
            Assert.True(MigrationApplied(directory, "adr0046-route-qualification-v1"));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private static void MarkLegacyStale(string directory, ModelQualificationKey key,
        string? staleBySuiteVersion, string? staleReason, IReadOnlyList<ModelTraitRecord>? currentTraits = null,
        bool removeRepairMarker = true, bool removeRouteMarker = false)
    {
        using var connection = new SqliteConnection("DataSource=" + DatabasePath(directory));
        connection.Open();
        if (removeRepairMarker)
            DeleteMigrationMarker(connection, "m5-suite-stale-trait-copy-v1");
        if (removeRouteMarker)
            DeleteMigrationMarker(connection, "adr0046-route-qualification-v1");

        using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE model_profiles SET state = 'Stale', profile_revision = 2,
                    stale_by_suite_version = $staleBy, stale_reason = $staleReason
                WHERE key_hash = $hash
                """;
            update.Parameters.AddWithValue("$staleBy", (object?)staleBySuiteVersion ?? DBNull.Value);
            update.Parameters.AddWithValue("$staleReason", (object?)staleReason ?? DBNull.Value);
            update.Parameters.AddWithValue("$hash", key.QualificationKeyHash());
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        if (currentTraits is null)
            return;
        foreach (var trait in currentTraits)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO model_traits (key_hash, profile_revision, trait, value, confidence, samples, source)
                VALUES ($hash, $revision, $trait, $value, $confidence, $samples, $source)
                """;
            insert.Parameters.AddWithValue("$hash", trait.KeyHash);
            insert.Parameters.AddWithValue("$revision", trait.ProfileRevision);
            insert.Parameters.AddWithValue("$trait", trait.Trait);
            insert.Parameters.AddWithValue("$value", trait.Value);
            insert.Parameters.AddWithValue("$confidence", trait.Confidence);
            insert.Parameters.AddWithValue("$samples", trait.Samples);
            insert.Parameters.AddWithValue("$source", trait.Source);
            Assert.Equal(1, insert.ExecuteNonQuery());
        }
    }

    private static void DeleteMigrationMarker(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM model_profile_migrations WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static bool MigrationApplied(string directory, string name)
    {
        using var connection = new SqliteConnection("DataSource=" + DatabasePath(directory));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM model_profile_migrations WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static (string KeyJson, string KeyHash, string State, long Revision, string SuiteId,
        string SuiteVersion, string? StaleBy, string? StaleReason, string CreatedAt, string UpdatedAt)
        RawProfileSnapshot(string directory, ModelQualificationKey key)
    {
        using var connection = new SqliteConnection("DataSource=" + DatabasePath(directory));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key_json, key_hash, state, profile_revision, suite_id, suite_version,
                stale_by_suite_version, stale_reason, created_at, updated_at
            FROM model_profiles WHERE key_hash = $hash
            """;
        command.Parameters.AddWithValue("$hash", key.QualificationKeyHash());
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetString(9));
    }

    private static (string KeyHash, long Revision, string Trait, double Value, double Confidence,
        int Samples, string Source)[] RawTraitSnapshots(string directory, ModelQualificationKey key, long revision)
    {
        using var connection = new SqliteConnection("DataSource=" + DatabasePath(directory));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key_hash, profile_revision, trait, value, confidence, samples, source
            FROM model_traits WHERE key_hash = $hash AND profile_revision = $revision ORDER BY trait
            """;
        command.Parameters.AddWithValue("$hash", key.QualificationKeyHash());
        command.Parameters.AddWithValue("$revision", revision);
        using var reader = command.ExecuteReader();
        var rows = new List<(string KeyHash, long Revision, string Trait, double Value, double Confidence,
            int Samples, string Source)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetDouble(3),
                reader.GetDouble(4), (int)reader.GetInt64(5), reader.GetString(6)));
        }

        return rows.ToArray();
    }

    private static IReadOnlyList<ModelTraitRecord> ReadTraits(string directory, ModelQualificationKey key,
        long revision)
    {
        using var store = NewStore(directory);
        return store.Traits(key, revision, CancellationToken.None);
    }
}
