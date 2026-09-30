using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Tests de persistencia de perfiles de cualificación empírica (ADR-0007 §6, M5): store
/// relacional por clave exacta, roundtrip tras reabrir SQLite, aislamiento por clave,
/// concurrencia optimista por revisión, y transición a Stale sin destruir la evidencia.
/// </summary>
public sealed class ModelQualificationStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m5-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SqliteModelQualificationStore NewStore(string dir, Func<DateTimeOffset>? clock = null) =>
        new(Path.Combine(dir, "user.db"), clock);

    private static ModelQualificationKey LocalKey() =>
        ModelQualificationKey.For("local", "qwen-x", ToolCallFormat.PromptedJson, ToolMode.Direct);

    [Fact]
    public void Upsert_creates_revision_1_and_Get_roundtrips_after_reopen()
    {
        var dir = TempDir();
        var key = LocalKey();

        using (var store = NewStore(dir))
        {
            var stored = store.Upsert(key, 0, ModelQualificationState.Qualified,
                "quick", "1.2.3", CancellationToken.None);
            Assert.Equal(1L, stored.ProfileRevision);
            Assert.Equal(ModelQualificationState.Qualified, stored.State);
            Assert.True(key.Equals(store.Get(key, CancellationToken.None)!.Key));
        }

        // Persistencia real: otro store sobre el mismo archivo ve lo mismo (roundtrip tras
        // reabrir SQLite), reconstruyendo la clave exacta desde su JSON canónico.
        using (var store = NewStore(dir))
        {
            var read = store.Get(key, CancellationToken.None);
            Assert.NotNull(read);
            Assert.True(key.Equals(read!.Key));
            Assert.Equal(ModelQualificationState.Qualified, read.State);
            Assert.Equal("quick", read.SuiteId);
            Assert.Equal("1.2.3", read.SuiteVersion);
            Assert.Equal(1L, read.ProfileRevision);
        }
    }

    [Fact]
    public void Keys_are_isolated_by_exact_configuration()
    {
        var dir = TempDir();
        var baseKey = LocalKey();
        var quantized = new ModelQualificationKey("local", "qwen-x", null, "Q8_0",
            Array.Empty<string>(), null, null, null, "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "default");

        using var store = NewStore(dir);
        store.Upsert(baseKey, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
        store.Upsert(quantized, 0, ModelQualificationState.Calibrated, "full", "2.0.0", CancellationToken.None);

        Assert.Equal(2, store.List(CancellationToken.None).Count);
        Assert.Equal(ModelQualificationState.Qualified,
            store.Get(baseKey, CancellationToken.None)!.State);
        Assert.Equal(ModelQualificationState.Calibrated,
            store.Get(quantized, CancellationToken.None)!.State);
        // Cada clave tiene su propio suite/version: sin herencia silenciosa.
        Assert.Equal("1.0.0", store.Get(baseKey, CancellationToken.None)!.SuiteVersion);
        Assert.Equal("2.0.0", store.Get(quantized, CancellationToken.None)!.SuiteVersion);
    }

    [Fact]
    public void Upsert_requires_current_revision_and_never_overwrites()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);

        // Revisión correcta: pasa y produce la siguiente.
        var next = store.Upsert(key, 1, ModelQualificationState.Calibrated, "full", "2.0.0", CancellationToken.None);
        Assert.Equal(2L, next.ProfileRevision);

        // Revisión obsoleta: conflicto explícito, sin sobrescritura silenciosa.
        var ex = Assert.Throws<ModelQualificationRevisionConflictException>(() =>
            store.Upsert(key, 1, ModelQualificationState.Calibrated, "full", "2.0.0", CancellationToken.None));
        Assert.Equal(1L, ex.ExpectedRevision);
        Assert.Equal(2L, ex.ActualRevision);

        // El estado no cambió a causa del intento fallido.
        Assert.Equal(ModelQualificationState.Calibrated,
            store.Get(key, CancellationToken.None)!.State);
        Assert.Equal(2L, store.Get(key, CancellationToken.None)!.ProfileRevision);
    }

    [Fact]
    public void MarkStale_preserves_evidence_and_bumps_revision()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);

        // Guardar traits en la revisión 1 antes de MarkStale.
        var hash = key.QualificationKeyHash();
        store.SaveTraits(key, 1,
            new[] { new ModelTraitRecord(hash, 1, "InstructionFollowing", 0.8, 0.9, 12, "suite") },
            CancellationToken.None);

        // Una versión mayor nueva de la suite marca el perfil Stale sin destruir la evidencia.
        var stale = store.MarkStale(key, 1, "2.0.0", CancellationToken.None);
        Assert.Equal(ModelQualificationState.Stale, stale.State);
        Assert.Equal(2L, stale.ProfileRevision);
        Assert.Equal("1.0.0", stale.SuiteVersion);
        Assert.Equal("2.0.0", stale.StaleBySuiteVersion);

        // La evidencia de cualificación anterior sobrevive: key_json, suite_id y traits intactos.
        var read = store.Get(key, CancellationToken.None)!;
        Assert.Equal(ModelQualificationState.Stale, read.State);
        Assert.Equal("quick", read.SuiteId);
        Assert.True(key.Equals(read.Key));

        // Los traits de la revisión histórica (1) siguen consultables.
        var historical = store.Traits(key, 1, CancellationToken.None);
        Assert.Single(historical);
        Assert.Equal("InstructionFollowing", historical[0].Trait);
        Assert.Equal(0.8, historical[0].Value);
    }

    [Fact]
    public void MarkStale_minor_version_does_not_stale()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);

        Assert.Throws<Domain.ModelQualificationStaleException>(() =>
            store.MarkStale(key, 1, "1.5.0", CancellationToken.None));

        var read = store.Get(key, CancellationToken.None)!;
        Assert.Equal(ModelQualificationState.Qualified, read.State);
        Assert.Null(read.StaleBySuiteVersion);
    }

    [Fact]
    public void MarkStale_rejects_unqualified_state()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        store.Upsert(key, 0, ModelQualificationState.ProvisionallyClassified, "quick", "1.0.0", CancellationToken.None);

        Assert.Throws<Domain.ModelQualificationStaleException>(() =>
            store.MarkStale(key, 1, "2.0.0", CancellationToken.None));

        Assert.Equal(ModelQualificationState.ProvisionallyClassified,
            store.Get(key, CancellationToken.None)!.State);
    }

    [Fact]
    public void MarkStale_stale_by_roundtrips_after_reopen()
    {
        var dir = TempDir();
        var key = LocalKey();
        using (var store = NewStore(dir))
        {
            store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
            store.MarkStale(key, 1, "3.1.0", CancellationToken.None);
        }

        using var reopened = NewStore(dir);
        var read = reopened.Get(key, CancellationToken.None)!;
        Assert.Equal(ModelQualificationState.Stale, read.State);
        Assert.Equal("1.0.0", read.SuiteVersion);
        Assert.Equal("3.1.0", read.StaleBySuiteVersion);
    }

    [Fact]
    public void MarkStale_migrates_db_without_stale_by_column()
    {
        var dir = TempDir();
        var dbPath = Path.Combine(dir, "user.db");
        var key = LocalKey();

        // Simula una base anterior: tabla sin la columna stale_by_suite_version.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + dbPath))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE model_profiles (
                    key_hash TEXT PRIMARY KEY,
                    key_json TEXT NOT NULL,
                    state TEXT NOT NULL,
                    profile_revision INTEGER NOT NULL,
                    suite_id TEXT NOT NULL,
                    suite_version TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                )
            """;
            cmd.ExecuteNonQuery();
        }

        using (var store = NewStore(dir))
        {
            store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
            var stale = store.MarkStale(key, 1, "2.0.0", CancellationToken.None);
            Assert.Equal("2.0.0", stale.StaleBySuiteVersion);
        }

        using var reopened = NewStore(dir);
        var read = reopened.Get(key, CancellationToken.None)!;
        Assert.Equal(ModelQualificationState.Stale, read.State);
        Assert.Equal("1.0.0", read.SuiteVersion);
        Assert.Equal("2.0.0", read.StaleBySuiteVersion);
    }

    [Fact]
    public void MarkStale_requires_current_revision()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);

        var ex = Assert.Throws<ModelQualificationRevisionConflictException>(() =>
            store.MarkStale(key, 99, "2.0.0", CancellationToken.None));
        Assert.Equal(99L, ex.ExpectedRevision);
        Assert.Equal(1L, ex.ActualRevision);

        Assert.Equal(ModelQualificationState.Qualified,
            store.Get(key, CancellationToken.None)!.State);
    }

    [Fact]
    public void Traits_roundtrip_and_replacement_is_per_revision()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        var profile = store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
        var hash = key.QualificationKeyHash();

        store.SaveTraits(key, profile.ProfileRevision,
            new[]
            {
                new ModelTraitRecord(hash, profile.ProfileRevision, "InstructionFollowing", 0.8, 0.9, 12, "suite"),
                new ModelTraitRecord(hash, profile.ProfileRevision, "ToolCallReliability", 0.6, 0.7, 8, "suite"),
            },
            CancellationToken.None);

        var traits = store.Traits(key, profile.ProfileRevision, CancellationToken.None);
        Assert.Equal(2, traits.Count);
        Assert.Equal("InstructionFollowing", traits[0].Trait);
        Assert.Equal(0.8, traits[0].Value);
        Assert.Equal("ToolCallReliability", traits[1].Trait);

        // Reemplazo por revisión: el conjunto de traits de esa revisión se sustituye, no se
        // acumula silenciosamente.
        store.SaveTraits(key, profile.ProfileRevision,
            new[]
            {
                new ModelTraitRecord(hash, profile.ProfileRevision, "InstructionFollowing", 0.5, 0.5, 3, "recal"),
            },
            CancellationToken.None);
        var after = store.Traits(key, profile.ProfileRevision, CancellationToken.None);
        Assert.Single(after);
        Assert.Equal(0.5, after[0].Value);
    }

    [Fact]
    public void Trait_value_out_of_range_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelTraitRecord("hash", 1, "TraitX", 1.5, 0.5, 1, "src"));
    }

    [Fact]
    public void ReadRow_rejects_tampered_key_hash()
    {
        var dir = TempDir();
        var key = LocalKey();
        using (var store = NewStore(dir))
        {
            store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
        }

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + Path.Combine(dir, "user.db"));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE model_profiles SET key_hash = 'deadbeef' WHERE key_hash != 'deadbeef'";
        cmd.ExecuteNonQuery();

        using var store2 = NewStore(dir);
        Assert.Throws<System.IO.InvalidDataException>(() => store2.List(CancellationToken.None));
    }

    [Fact]
    public void ReadRow_rejects_tampered_key_json()
    {
        var dir = TempDir();
        var key = LocalKey();
        using (var store = NewStore(dir))
        {
            store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
        }

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + Path.Combine(dir, "user.db"));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE model_profiles SET key_json = REPLACE(key_json, 'qwen-x', 'qwen-y')";
        cmd.ExecuteNonQuery();

        using var store2 = NewStore(dir);
        Assert.Throws<System.IO.InvalidDataException>(() => store2.List(CancellationToken.None));
    }

    [Fact]
    public void SaveTraits_rejects_mismatched_key_hash()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        var profile = store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
        Assert.Throws<ArgumentException>(() =>
            store.SaveTraits(key, profile.ProfileRevision,
                new[] { new ModelTraitRecord("hash-otro", profile.ProfileRevision, "TraitX", 0.5, 0.5, 1, "src") },
                CancellationToken.None));
    }

    [Fact]
    public void SaveTraits_rejects_mismatched_profile_revision()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
        var hash = key.QualificationKeyHash();
        Assert.Throws<ModelQualificationRevisionConflictException>(() =>
            store.SaveTraits(key, 99,
                new[] { new ModelTraitRecord(hash, 99, "TraitX", 0.5, 0.5, 1, "src") },
                CancellationToken.None));
    }

    [Fact]
    public void SaveTraits_rejects_nonexistent_profile()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        var hash = key.QualificationKeyHash();
        Assert.Throws<ModelQualificationRevisionConflictException>(() =>
            store.SaveTraits(key, 1,
                new[] { new ModelTraitRecord(hash, 1, "TraitX", 0.5, 0.5, 1, "src") },
                CancellationToken.None));
    }

    [Fact]
    public void SaveTraits_rejects_trait_with_wrong_revision_and_preserves_existing()
    {
        var dir = TempDir();
        var key = LocalKey();
        using var store = NewStore(dir);
        store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
        var hash = key.QualificationKeyHash();

        // Guardar traits válidos primero.
        store.SaveTraits(key, 1,
            new[] { new ModelTraitRecord(hash, 1, "InstructionFollowing", 0.8, 0.9, 12, "suite") },
            CancellationToken.None);

        // Intento inválido: trait con ProfileRevision incorrecto (2 en vez de 1).
        Assert.Throws<ArgumentException>(() =>
            store.SaveTraits(key, 1,
                new[] { new ModelTraitRecord(hash, 2, "TraitX", 0.5, 0.5, 1, "src") },
                CancellationToken.None));

        // Los traits existentes siguen intactos.
        var existing = store.Traits(key, 1, CancellationToken.None);
        Assert.Single(existing);
        Assert.Equal("InstructionFollowing", existing[0].Trait);
    }

    [Fact]
    public void SaveTraits_atomic_revision_check_with_two_connections()
    {
        var dir = TempDir();
        var key = LocalKey();
        var hash = key.QualificationKeyHash();

        using var storeA = NewStore(dir);
        storeA.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", CancellationToken.None);
        storeA.SaveTraits(key, 1,
            new[] { new ModelTraitRecord(hash, 1, "InstructionFollowing", 0.8, 0.9, 12, "suite") },
            CancellationToken.None);

        using var storeB = NewStore(dir);
        // storeB sube la revisión del perfil mientras storeA sigue operando sobre la revisión 1.
        storeB.MarkStale(key, 1, "2.0.0", CancellationToken.None);

        // El intento de storeA con la revisión obsoleta debe fallar sin tocar los traits.
        var ex = Assert.Throws<ModelQualificationRevisionConflictException>(() =>
            storeA.SaveTraits(key, 1,
                new[] { new ModelTraitRecord(hash, 1, "InstructionFollowing", 0.9, 0.9, 13, "stale") },
                CancellationToken.None));
        Assert.Equal(1L, ex.ExpectedRevision);
        Assert.Equal(2L, ex.ActualRevision);

        // La evidencia histórica de la revisión 1 sigue intacta.
        var historical = storeA.Traits(key, 1, CancellationToken.None);
        Assert.Single(historical);
        Assert.Equal(0.8, historical[0].Value);
    }
}
