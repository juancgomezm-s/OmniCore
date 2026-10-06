namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using Xunit;

public sealed class ModelQualificationSnapshotTests
{
    private static ModelDefinition Model() => new("snapshot-model", "snapshot-provider", 8192, 7000, 2048);

    [Theory]
    [InlineData(ModelQualificationState.Qualified)]
    [InlineData(ModelQualificationState.Calibrated)]
    [InlineData(ModelQualificationState.Stale)]
    public void Snapshot_returns_identity_and_traits_from_the_same_revision(ModelQualificationState state)
    {
        var key = ModelQualificationHost.QualificationKeyFor(Model(), provider: null);
        var store = new ChangingQualificationStore(key, state, 4, advanceOnGet: true, includeTraits: true);

        var snapshot = ModelQualificationHost.UsableSnapshot(store, Model(), provider: null, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(key, snapshot!.Key);
        Assert.Equal(key.QualificationKeyHash(), snapshot.KeyHash);
        Assert.Equal(4, snapshot.ProfileRevision);
        Assert.Equal(state, snapshot.State);
        Assert.Equal(.42, snapshot.Traits["InstructionFollowing"]);
        Assert.Equal(1, store.GetCount);
        Assert.Equal(new[] { 4L }, store.TraitReadRevisions);
        Assert.Equal(5, store.CurrentProfile!.ProfileRevision);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, double>)snapshot.Traits).Add("mutated", .9));
    }

    [Fact]
    public void Public_usable_traits_delegates_and_preserves_dictionary_values()
    {
        var key = ModelQualificationHost.QualificationKeyFor(Model(), provider: null);
        var store = new ChangingQualificationStore(key, ModelQualificationState.Qualified, 2,
            advanceOnGet: false, includeTraits: true);

        var traits = ModelQualificationHost.UsableTraits(store, Model(), provider: null, CancellationToken.None);

        Assert.NotNull(traits);
        Assert.Equal(.42, traits!["InstructionFollowing"]);
        Assert.Equal(1, store.GetCount);
        Assert.Equal(new[] { 2L }, store.TraitReadRevisions);
    }

    [Theory]
    [InlineData(false, ModelQualificationState.Qualified, false, 3)]
    [InlineData(true, ModelQualificationState.Declared, false, -1)]
    [InlineData(true, ModelQualificationState.Qualified, true, -1)]
    public void Snapshot_is_null_without_an_eligible_profile_and_traits(
        bool includeTraits, ModelQualificationState state, bool missingProfile, long expectedTraitReadRevision)
    {
        var key = ModelQualificationHost.QualificationKeyFor(Model(), provider: null);
        var store = new ChangingQualificationStore(key, state, 3,
            advanceOnGet: false, includeTraits: includeTraits, missingProfile: missingProfile);

        var snapshot = ModelQualificationHost.UsableSnapshot(store, Model(), provider: null, CancellationToken.None);

        Assert.Null(snapshot);
        Assert.Equal(1, store.GetCount);
        if (expectedTraitReadRevision < 0)
            Assert.Empty(store.TraitReadRevisions);
        else
            Assert.Equal(new[] { expectedTraitReadRevision }, store.TraitReadRevisions);
    }

    [Fact]
    public void Snapshot_passes_cancellation_to_store_and_does_not_continue_after_cancel()
    {
        var key = ModelQualificationHost.QualificationKeyFor(Model(), provider: null);
        var store = new ChangingQualificationStore(key, ModelQualificationState.Qualified, 1,
            advanceOnGet: false, includeTraits: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ModelQualificationHost.UsableSnapshot(store,
            Model(), provider: null, cancellation.Token));
        Assert.Equal(1, store.GetCount);
        Assert.Empty(store.TraitReadRevisions);
    }

    [Fact]
    public void Sqlite_reopen_reads_current_revision_traits_without_using_other_endpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-qualification-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "user.db");
        var endpoint = "http://snapshot-fixture.invalid/v1";
        var key = ModelQualificationHost.QualificationKeyFor(Model(), null, endpoint);
        try
        {
            using (var store = new SqliteModelQualificationStore(database))
            {
                var first = store.Upsert(key, 0, ModelQualificationState.Qualified, "fixture", "1.0.0", CancellationToken.None);
                store.SaveTraits(key, first.ProfileRevision, new[]
                {
                    new ModelTraitRecord(key.QualificationKeyHash(), first.ProfileRevision,
                        "InstructionFollowing", .21, .9, 5, "fixture")
                }, CancellationToken.None);
                var second = store.Upsert(key, first.ProfileRevision, ModelQualificationState.Calibrated,
                    "fixture", "1.0.0", CancellationToken.None);
                store.SaveTraits(key, second.ProfileRevision, new[]
                {
                    new ModelTraitRecord(key.QualificationKeyHash(), second.ProfileRevision,
                        "InstructionFollowing", .84, .9, 10, "fixture")
                }, CancellationToken.None);
            }
            using (var store = new SqliteModelQualificationStore(database))
            {
                var snapshot = ModelQualificationHost.UsableSnapshot(store, Model(), null, CancellationToken.None, endpoint);
                Assert.NotNull(snapshot);
                Assert.Equal(key.QualificationKeyHash(), snapshot.KeyHash);
                Assert.Equal(2, snapshot.ProfileRevision);
                Assert.Equal(ModelQualificationState.Calibrated, snapshot.State);
                Assert.Equal(.84, snapshot.Traits["InstructionFollowing"]);
                Assert.Equal(.21, Assert.Single(store.Traits(key, 1, CancellationToken.None)).Value);
                Assert.Null(ModelQualificationHost.UsableSnapshot(store, Model(), null, CancellationToken.None,
                    "http://other-snapshot-fixture.invalid/v1"));
            }
        }
        finally
        {
            // Dispose closes the logical connection; release only this fixture's SQLite pool
            // before deleting its private directory on Windows, never the global pool.
            using var poolIdentity = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + database);
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(poolIdentity);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ChangingQualificationStore : IModelQualificationStore
    {
        private readonly ModelQualificationKey _key;
        private readonly bool _advanceOnGet;
        private readonly bool _includeTraits;
        private ModelQualificationProfile? _profile;

        public int GetCount { get; private set; }
        public ModelQualificationProfile? CurrentProfile => _profile;
        public List<long> TraitReadRevisions { get; } = new();

        public ChangingQualificationStore(ModelQualificationKey key, ModelQualificationState state,
            long revision, bool advanceOnGet, bool includeTraits, bool missingProfile = false)
        {
            _key = key;
            _advanceOnGet = advanceOnGet;
            _includeTraits = includeTraits;
            if (!missingProfile) _profile = Profile(state, revision);
        }

        public ModelQualificationProfile? Get(ModelQualificationKey key, CancellationToken cancellationToken)
        {
            GetCount++;
            cancellationToken.ThrowIfCancellationRequested();
            var captured = _profile;
            if (_advanceOnGet && captured is not null)
                _profile = Profile(ModelQualificationState.Calibrated, captured.ProfileRevision + 1);
            return captured;
        }

        public IReadOnlyList<ModelTraitRecord> Traits(ModelQualificationKey key, long profileRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TraitReadRevisions.Add(profileRevision);
            if (!_includeTraits) return Array.Empty<ModelTraitRecord>();
            return new[] { new ModelTraitRecord(key.QualificationKeyHash(), profileRevision,
                "InstructionFollowing", .42, .9, 5, "fixture") };
        }

        public IReadOnlyList<ModelQualificationProfile> List(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ModelQualificationProfile Upsert(ModelQualificationKey key, long expectedRevision,
            ModelQualificationState state, string suiteId, string suiteVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ModelQualificationProfile MarkStale(ModelQualificationKey key, long expectedRevision,
            string newSuiteVersion, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ModelQualificationProfile UpsertWithTraits(ModelQualificationKey key, long expectedRevision,
            ModelQualificationState state, string suiteId, string suiteVersion,
            IReadOnlyList<ModelTraitRecord> traits, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void SaveTraits(ModelQualificationKey key, long profileRevision,
            IReadOnlyList<ModelTraitRecord> traits, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private ModelQualificationProfile Profile(ModelQualificationState state, long revision) =>
            new(_key, state, revision, "quick", "1.0.0", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    }
}
