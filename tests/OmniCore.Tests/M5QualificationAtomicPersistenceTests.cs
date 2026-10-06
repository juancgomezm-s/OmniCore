using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Regression fixtures for atomic persistence of a qualification profile and its traits. A
/// SQLite trigger aborts the second trait insert; all model responses come from an exact local
/// quick-suite fixture, with no network provider or fabricated pass result.
/// </summary>
public sealed class M5QualificationAtomicPersistenceTests
{
    private const string ModelId = "m5-atomic-fixture";

    private static IReadOnlyList<ModelRegistryModelDescriptor> Registry() =>
    [
        new ModelRegistryModelDescriptor(ModelId, "local", 8192, 8192, 2048),
    ];

    private static ModelDefinition Model() => new(ModelId, "local", 8192, 8192, 2048);

    private static ModelQualificationKey Key() => ModelQualificationHost.QualificationKeyFor(Model(), provider: null);

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-qualification-atomic",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ExactFixtureProvider : IModelProvider
    {
        private readonly IReadOnlyDictionary<string, string> _answers = QuickProbeSuite.Probes()
            .ToDictionary(probe => probe.Prompt, probe => probe.Expected, StringComparer.Ordinal);

        public int Calls { get; private set; }

        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var prompt = (request.Messages[0].Content[0] as TextBlock)?.Text ?? "";
            if (!_answers.TryGetValue(prompt, out var answer))
            {
                throw new InvalidOperationException("prompt outside the exact quick fixture");
            }

            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock(answer)], StopReason.EndTurn, new TokenUsage(10, 5, 0, 0, 0), null,
                new ProviderMetadata("fixture", ModelId, null)));
        }
    }

    private static QualificationOptions Options(ExactFixtureProvider provider) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = 1m,
        Provider = provider,
    };

    [Fact]
    public async Task New_qualification_does_not_leave_profile_or_traits_when_trait_insert_aborts()
    {
        var directory = TempDir();
        try
        {
            InstallTraitInsertFailure(directory);
            var provider = new ExactFixtureProvider();
            using (var host = ModelQualificationHost.Create(directory, Registry()))
            {
                await Assert.ThrowsAsync<SqliteException>(() =>
                    host.QualifyAsync(ModelId, Options(provider), CancellationToken.None));
            }

            Assert.Equal(QuickProbeSuite.Probes().Count, provider.Calls);
            using var reopened = OmniHost.CreateModelQualificationStore(directory);
            Assert.Null(reopened.Get(Key(), CancellationToken.None));
            Assert.Empty(reopened.List(CancellationToken.None));
            Assert.Empty(reopened.Traits(Key(), 1, CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Requalification_failure_preserves_profile_revision_metadata_and_all_old_traits()
    {
        var directory = TempDir();
        try
        {
            ModelQualificationProfile original;
            ModelTraitRecord[] originalTraits;
            var key = Key();
            using (var store = OmniHost.CreateModelQualificationStore(directory))
            {
                original = store.Upsert(key, 0, ModelQualificationState.Calibrated,
                    "prior-suite", "4.2.0", CancellationToken.None);
                originalTraits =
                [
                    new ModelTraitRecord(key.QualificationKeyHash(), original.ProfileRevision,
                        "InstructionFollowing", 0.25, 0.6, 4, "prior-fixture"),
                    new ModelTraitRecord(key.QualificationKeyHash(), original.ProfileRevision,
                        "StructuredOutputReliability", 0.5, 0.7, 3, "prior-fixture"),
                    new ModelTraitRecord(key.QualificationKeyHash(), original.ProfileRevision,
                        "ToolCallReliability", 0.75, 0.8, 8, "prior-fixture"),
                ];
                store.SaveTraits(key, original.ProfileRevision, originalTraits, CancellationToken.None);
            }

            InstallTraitInsertFailure(directory);
            var provider = new ExactFixtureProvider();
            using (var host = ModelQualificationHost.Create(directory, Registry()))
            {
                await Assert.ThrowsAsync<SqliteException>(() =>
                    host.QualifyAsync(ModelId, Options(provider), CancellationToken.None));
            }

            Assert.Equal(QuickProbeSuite.Probes().Count, provider.Calls);
            using var reopened = OmniHost.CreateModelQualificationStore(directory);
            var current = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(current);
            Assert.Equal(original.KeyHash, current!.KeyHash);
            Assert.Equal(original.State, current.State);
            Assert.Equal(original.ProfileRevision, current.ProfileRevision);
            Assert.Equal(original.SuiteId, current.SuiteId);
            Assert.Equal(original.SuiteVersion, current.SuiteVersion);
            Assert.Equal(original.StaleBySuiteVersion, current.StaleBySuiteVersion);
            Assert.Equal(original.StaleReason, current.StaleReason);
            Assert.Equal(original.CreatedAt, current.CreatedAt);
            Assert.Equal(original.UpdatedAt, current.UpdatedAt);

            var currentTraits = reopened.Traits(key, original.ProfileRevision, CancellationToken.None);
            Assert.Equal(originalTraits.Length, currentTraits.Count);
            Assert.Equal(
                originalTraits.OrderBy(trait => trait.Trait).Select(TraitSnapshot),
                currentTraits.OrderBy(trait => trait.Trait).Select(TraitSnapshot));
            Assert.Empty(reopened.Traits(key, original.ProfileRevision + 1, CancellationToken.None));
            Assert.Single(reopened.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Successful_qualification_persists_profile_and_both_measured_traits()
    {
        var directory = TempDir();
        try
        {
            var provider = new ExactFixtureProvider();
            using (var host = ModelQualificationHost.Create(directory, Registry()))
            {
                var result = await host.QualifyAsync(ModelId, Options(provider), CancellationToken.None);
                Assert.Equal(ModelQualificationState.Qualified.ToString(), result.NewState);
                Assert.Equal(1, result.ProfileRevision);
                Assert.All(result.Probes, probe => Assert.Equal(ProbeStatus.Passed.ToString(), probe.Status));
            }

            Assert.Equal(QuickProbeSuite.Probes().Count, provider.Calls);
            using var reopened = OmniHost.CreateModelQualificationStore(directory);
            var profile = reopened.Get(Key(), CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.Qualified, profile!.State);
            Assert.Equal(1, profile.ProfileRevision);
            Assert.Equal(QuickProbeSuite.SuiteId, profile.SuiteId);
            Assert.Equal(QuickProbeSuite.SuiteVersion, profile.SuiteVersion);

            var traits = reopened.Traits(Key(), profile.ProfileRevision, CancellationToken.None);
            Assert.Equal(2, traits.Count);
            Assert.Contains(traits, trait => trait.Trait == "InstructionFollowing"
                && trait.Value == 1.0 && trait.Source == "empirical");
            Assert.Contains(traits, trait => trait.Trait == "StructuredOutputReliability"
                && trait.Value == 1.0 && trait.Source == "empirical");
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private static void InstallTraitInsertFailure(string dataDirectory)
    {
        using (var store = OmniHost.CreateModelQualificationStore(dataDirectory))
        {
            store.List(CancellationToken.None);
        }

        var databasePath = Path.GetFullPath(Path.Combine(dataDirectory, "user.db"));
        using var connection = new SqliteConnection("DataSource=" + databasePath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fixture_abort_structured_trait
            BEFORE INSERT ON model_traits
            WHEN NEW.trait = 'StructuredOutputReliability'
            BEGIN
                SELECT RAISE(ABORT, 'fixture aborts StructuredOutputReliability insert');
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void DeleteFixtureDirectory(string directory)
    {
        var databasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
        using (var connection = new SqliteConnection("DataSource=" + databasePath))
        {
            SqliteConnection.ClearPool(connection);
        }

        Directory.Delete(directory, recursive: true);
    }

    private static (string KeyHash, long Revision, string Trait, double Value, double Confidence,
        int Samples, string Source) TraitSnapshot(ModelTraitRecord trait) =>
        (trait.KeyHash, trait.ProfileRevision, trait.Trait, trait.Value, trait.Confidence,
            trait.Samples, trait.Source);
}
