using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Abstractions;

namespace OmniCore.Tests;

/// <summary>SQLite migration fixtures; no authenticated provider or user database is used.</summary>
public sealed class RouteQualificationMigrationTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Migrated = new(2026, 10, 6, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ModelQualificationState.Qualified)]
    [InlineData(ModelQualificationState.Calibrated)]
    [InlineData(ModelQualificationState.ProvisionallyClassified)]
    [InlineData(ModelQualificationState.Declared)]
    public void Legacy_profile_becomes_stale_without_destroying_key_suite_or_traits(ModelQualificationState state)
    {
        using var fixture = new Fixture();
        var key = ModelQualificationKey.For("fixture", "model", ToolCallFormat.Native, ToolMode.Direct);
        fixture.Seed(key, state);
        using (var store = new SqliteModelQualificationStore(fixture.Path, () => Migrated))
        {
            var profile = Assert.IsType<ModelQualificationProfile>(store.Get(key, CancellationToken.None));
            Assert.Equal(ModelQualificationState.Stale, profile.State);
            Assert.Equal("route-identity-migration", profile.StaleReason);
            Assert.Null(profile.StaleBySuiteVersion);
            Assert.Equal(key.QualificationKeyHash(), profile.KeyHash);
            Assert.Equal(key.CanonicalJson(), profile.Key.CanonicalJson());
            Assert.Equal("fixture-quick", profile.SuiteId);
            Assert.Equal("1.2.3", profile.SuiteVersion);
            Assert.Equal(Created, profile.CreatedAt);
            Assert.Equal(Migrated, profile.UpdatedAt);
            Assert.Equal(2, profile.ProfileRevision);
            var historical = Assert.Single(store.Traits(key, 1, CancellationToken.None));
            var current = Assert.Single(store.Traits(key, 2, CancellationToken.None));
            Assert.Equal(historical.Value, current.Value);
            Assert.Equal(historical.Confidence, current.Confidence);
            Assert.Equal(historical.Samples, current.Samples);
            Assert.Equal(historical.Source, current.Source);
            Assert.Throws<ModelQualificationRevisionConflictException>(() =>
                store.Upsert(key, 1, ModelQualificationState.Qualified, "fixture-quick", "1.2.3", CancellationToken.None));
            var routed = ModelQualificationKey.For("fixture", "model", ToolCallFormat.Native, ToolMode.Direct,
                "https://fixture.test/v1", "OpenAIResponses", "new-build");
            Assert.Null(store.Get(routed, CancellationToken.None));
        }
        using var reopened = new SqliteModelQualificationStore(fixture.Path, () => Migrated.AddDays(1));
        var again = reopened.Get(key, CancellationToken.None)!;
        Assert.Equal(2, again.ProfileRevision);
        Assert.Equal(Migrated, again.UpdatedAt);
        Assert.Single(reopened.Traits(key, 2, CancellationToken.None));
    }

    [Fact]
    public void Route_aware_profile_is_not_invalidated_and_round_trips_new_fields()
    {
        using var fixture = new Fixture();
        var key = ModelQualificationKey.For("fixture", "model", ToolCallFormat.Native, ToolMode.Direct,
            "https://fixture.test/v1", "OpenAIResponses", "runtime");
        fixture.Seed(key, ModelQualificationState.Qualified);
        using var store = new SqliteModelQualificationStore(fixture.Path, () => Migrated);
        var read = store.Get(key, CancellationToken.None)!;
        Assert.Equal(ModelQualificationState.Qualified, read.State);
        Assert.Equal(1, read.ProfileRevision);
        Assert.Equal(key, read.Key);
        Assert.Null(read.StaleReason);
        Assert.Equal(Created, read.UpdatedAt);
    }

    [Fact]
    public void Requalification_clears_migration_reason_without_losing_historical_evidence()
    {
        using var fixture = new Fixture();
        var key = ModelQualificationKey.For("fixture", "model", ToolCallFormat.Native, ToolMode.Direct);
        fixture.Seed(key, ModelQualificationState.Qualified);
        using var store = new SqliteModelQualificationStore(fixture.Path, () => Migrated);
        store.Upsert(key, 2, ModelQualificationState.Qualified, "fixture-quick", "1.3.0", CancellationToken.None);
        Assert.Null(store.Get(key, CancellationToken.None)!.StaleReason);
        Assert.Single(store.Traits(key, 1, CancellationToken.None));
        Assert.Single(store.Traits(key, 2, CancellationToken.None));
    }

    [Fact]
    public void Host_key_tracks_actual_route_profile_and_runtime_build()
    {
        var model = new ModelDefinition("model", "provider", 8192, 7000, 1024);
        var provider = new ProviderDescriptor("provider", ProviderFamily.OpenAIResponses, "https://fixture.test/v1",
            AuthConfig.None(), false, false, true) { Profile = "codex" };
        var baseline = ModelQualificationHost.QualificationKeyFor(model, provider, provider.BaseUrl, "build-a");
        Assert.Equal("codex", baseline.AdapterProfile);
        Assert.Equal(provider.BaseUrl, baseline.Endpoint);
        Assert.Equal("OpenAIResponses", baseline.Protocol);
        Assert.Equal("build-a", baseline.RuntimeBuild);
        Assert.NotEqual(baseline, ModelQualificationHost.QualificationKeyFor(model, provider, "https://other.test/v1", "build-a"));
        Assert.NotEqual(baseline, ModelQualificationHost.QualificationKeyFor(model, provider, provider.BaseUrl, "build-b"));
        var actual = ModelQualificationHost.QualificationKeyFor(model, provider);
        Assert.False(string.IsNullOrWhiteSpace(actual.RuntimeBuild));
        Assert.Contains(typeof(ModelQualificationHost).Assembly.ManifestModule.ModuleVersionId.ToString("D"), actual.RuntimeBuild);
        var route = new ModelRoute(model.ProviderId, provider.BaseUrl, provider.Family, provider.Profile, model.Id);
        Assert.Equal(route.Id, new ModelProfileResolver().Resolve(model, provider, route: route).RouteId);
        var defaultRoute = ModelRoutingHost.RouteFor(model, provider, provider.BaseUrl);
        Assert.Equal(model.Id, defaultRoute.Id.Value);
        var overrideRoute = ModelRoutingHost.RouteFor(model, provider, "https://other.test/v1");
        Assert.NotEqual(defaultRoute.Id, overrideRoute.Id);
        Assert.Equal("https://other.test/v1", overrideRoute.Endpoint);
    }

    [Fact]
    public void Migration_rejects_corrupt_identity_without_changing_profile_state()
    {
        using var fixture = new Fixture();
        var key = ModelQualificationKey.For("fixture", "model", ToolCallFormat.Native, ToolMode.Direct);
        fixture.Seed(key, ModelQualificationState.Qualified);
        using (var connection = new SqliteConnection("DataSource=" + fixture.Path))
        {
            connection.Open();
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE model_profiles SET key_hash = 'corrupt'";
            corrupt.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => new SqliteModelQualificationStore(fixture.Path, () => Migrated));
        using var read = new SqliteConnection("DataSource=" + fixture.Path);
        read.Open();
        using var command = read.CreateCommand();
        command.CommandText = "SELECT state FROM model_profiles";
        Assert.Equal("Qualified", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM model_profile_migrations";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Traits_from_one_endpoint_are_not_used_for_another()
    {
        using var fixture = new Fixture();
        using var store = new SqliteModelQualificationStore(fixture.Path, () => Migrated);
        var model = new ModelDefinition("model", "provider", 8192, 7000, 1024);
        var providerA = new ProviderDescriptor("provider", ProviderFamily.OpenAIResponses, "https://a.test/v1", AuthConfig.None(), false, false, true);
        var providerB = new ProviderDescriptor("provider", ProviderFamily.OpenAIResponses, "https://b.test/v1", AuthConfig.None(), false, false, true);
        // Use the effective endpoint explicitly so the fixture never depends on a process override.
        var key = ModelQualificationHost.QualificationKeyFor(model, providerA, providerA.BaseUrl);
        store.Upsert(key, 0, ModelQualificationState.Qualified, "fixture", "1.0.0", CancellationToken.None);
        store.SaveTraits(key, 1, new[] { new ModelTraitRecord(key.QualificationKeyHash(), 1, "InstructionFollowing", 0.9, 0.8, 12, "fixture") }, CancellationToken.None);
        var otherKey = ModelQualificationHost.QualificationKeyFor(model, providerB, providerB.BaseUrl);
        Assert.Null(store.Get(otherKey, CancellationToken.None));
        Assert.Empty(store.Traits(otherKey, 1, CancellationToken.None));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omni-route-qual-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "fixture.db");
        public Fixture() => Directory.CreateDirectory(_directory);
        public void Seed(ModelQualificationKey key, ModelQualificationState state)
        {
            using var connection = new SqliteConnection("DataSource=" + Path);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE model_profiles (key_hash TEXT PRIMARY KEY, key_json TEXT NOT NULL,
                    state TEXT NOT NULL, profile_revision INTEGER NOT NULL, suite_id TEXT NOT NULL,
                    suite_version TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE model_traits (key_hash TEXT NOT NULL, profile_revision INTEGER NOT NULL,
                    trait TEXT NOT NULL, value REAL NOT NULL, confidence REAL NOT NULL, samples INTEGER NOT NULL, source TEXT NOT NULL);
                INSERT INTO model_profiles VALUES ($hash, $json, $state, 1, 'fixture-quick', '1.2.3', $date, $date);
                INSERT INTO model_traits VALUES ($hash, 1, 'InstructionFollowing', 0.8, 0.7, 12, 'fixture');
                """;
            command.Parameters.AddWithValue("$hash", key.QualificationKeyHash());
            command.Parameters.AddWithValue("$json", key.CanonicalJson());
            command.Parameters.AddWithValue("$state", state.ToString());
            command.Parameters.AddWithValue("$date", Created.ToString("o"));
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            using var connection = new SqliteConnection("DataSource=" + Path);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
