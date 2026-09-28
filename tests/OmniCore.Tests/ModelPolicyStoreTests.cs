using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Tests de persistencia de políticas (ADR-0044 §8–§9): store relacional por clave exacta,
/// concurrencia optimista por revisión (sin sobrescrituras silenciosas), historial,
/// selección por workspace y ciclo de onboarding del ModelPolicyService.
/// </summary>
public sealed class ModelPolicyStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SqliteModelPolicyStore NewStore(string dir, Func<DateTimeOffset>? clock = null) =>
        new(Path.Combine(dir, "user.db"), clock);

    // ---- Persistencia básica ----

    [Fact]
    public void Set_creates_revision_1_and_Get_roundtrips()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");

        using (var store = NewStore(dir))
        {
            var stored = store.Set(key, 0, ModelPolicyPresets.ScopedCoder(), CancellationToken.None);
            Assert.Equal(1L, stored.Revision);

            var read = store.Get(key, CancellationToken.None);
            Assert.NotNull(read);
            Assert.Equal(1L, read!.Revision);
            Assert.Equal(ModelPolicyCategory.ScopedCoder, read.Policy.Category);
            Assert.True(read.Policy.Equals(ModelPolicyPresets.ScopedCoder()));
            // La clave exacta sobrevive al round-trip por su JSON canónico.
            Assert.True(key.Equals(read.Key));
        }

        // Persistencia real: otro store sobre el mismo archivo ve lo mismo.
        using (var store = NewStore(dir))
        {
            var read = store.Get(key, CancellationToken.None);
            Assert.NotNull(read);
            Assert.Equal(ModelPolicyCategory.ScopedCoder, read!.Policy.Category);
        }
    }

    [Fact]
    public void Keys_are_isolated_by_exact_configuration()
    {
        var dir = TempDir();
        var baseKey = ModelPolicyKey.For("openai", "gpt-x");
        var quantized = new ModelPolicyKey("openai", "gpt-x", null, "q8",
            Array.Empty<string>(), null, null, null, "default", "v1");

        using var store = NewStore(dir);
        store.Set(baseKey, 0, ModelPolicyPresets.ObserveOnly(), CancellationToken.None);
        store.Set(quantized, 0, ModelPolicyPresets.FullAgent(), CancellationToken.None);

        var list = store.List(CancellationToken.None);
        Assert.Equal(2, list.Count);
        // Cada clave resuelve su propia política: sin herencia silenciosa entre configuraciones.
        Assert.Equal(ModelPolicyCategory.ObserveOnly,
            store.Get(baseKey, CancellationToken.None)!.Policy.Category);
        Assert.Equal(ModelPolicyCategory.FullAgent,
            store.Get(quantized, CancellationToken.None)!.Policy.Category);
    }

    [Fact]
    public void Fields_roundtrip_exactly()
    {
        var dir = TempDir();
        var key = new ModelPolicyKey("p", "m", "rev-7", "q8", new[] { "adapter-a" }, "llama.cpp",
            "build-1", "tpl-hash", "coding", "v2");
        var policy = new UserModelPolicy(ModelPolicyCategory.Custom,
            new ModelToolPolicy(ToolMode.Discovered, 11, true, new HashSet<ModelToolCapability>
            {
                ModelToolCapability.WorkspaceRead, ModelToolCapability.PatchExisting,
                ModelToolCapability.CreateFile, ModelToolCapability.Network,
            }),
            new FileMutationPolicy(FileMutationMode.PatchAndCreate, DestructiveActionPolicy.Ask,
                DestructiveActionPolicy.Allow, 4, 444, 0.44, requirePriorRead: true,
                requireExpectedVersionToken: false, requirePostEditValidation: true,
                allowParallelMutations: true),
            "user", "nota con \"comillas\" y \\ barra");

        using (var store = NewStore(dir))
        {
            store.Set(key, 0, policy, CancellationToken.None);
        }

        using (var store = NewStore(dir))
        {
            var read = store.Get(key, CancellationToken.None);
            Assert.NotNull(read);
            Assert.Equal("nota con \"comillas\" y \\ barra", read!.Policy.Note);
            Assert.Equal(ToolMode.Discovered, read.Policy.ToolPolicy.Mode);
            Assert.Equal(11, read.Policy.ToolPolicy.MaxVisibleTools);
            Assert.True(read.Policy.ToolPolicy.AllowToolDiscovery);
            Assert.Equal(4, read.Policy.MutationPolicy.MaxFilesPerTurn);
            Assert.Equal(444, read.Policy.MutationPolicy.MaxChangedLinesPerTurn);
            Assert.Equal(0.44, read.Policy.MutationPolicy.MaxRewriteRatio);
            Assert.False(read.Policy.MutationPolicy.RequireExpectedVersionToken);
            Assert.True(read.Policy.MutationPolicy.AllowParallelMutations);
            Assert.Equal(DestructiveActionPolicy.Allow, read.Policy.MutationPolicy.MoveOrRename);
            Assert.Contains(ModelToolCapability.Network, read.Policy.ToolPolicy.CapabilityCeiling);
            Assert.True(key.Equals(read.Key));
        }
    }

    // ---- Conflicto de revisión: sin sobrescrituras silenciosas (ADR-0044 §9) ----

    [Fact]
    public void Set_with_stale_revision_conflicts_and_keeps_original()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");

        using var store = NewStore(dir);
        store.Set(key, 0, ModelPolicyPresets.ObserveOnly(), CancellationToken.None);

        // Un segundo cliente escribe con la revisión que cree vigente (0 = crear).
        var conflict = Assert.Throws<ModelPolicyRevisionConflictException>(() =>
            store.Set(key, 0, ModelPolicyPresets.FullAgent(), CancellationToken.None));
        Assert.Equal(0L, conflict.ExpectedRevision);
        Assert.Equal(1L, conflict.ActualRevision);

        // La política original sobrevive: nada se pisó.
        Assert.Equal(ModelPolicyCategory.ObserveOnly,
            store.Get(key, CancellationToken.None)!.Policy.Category);
    }

    [Fact]
    public void Two_writers_cannot_silently_overwrite_each_other()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");

        using var store = NewStore(dir);
        store.Set(key, 0, ModelPolicyPresets.ObserveOnly(), CancellationToken.None);

        // Ambos clientes leen la revisión vigente (1) y preparan cambios distintos.
        var winner = store.Set(key, 1, ModelPolicyPresets.PatchOnly(), CancellationToken.None);
        Assert.Equal(2L, winner.Revision);

        var loser = Assert.Throws<ModelPolicyRevisionConflictException>(() =>
            store.Set(key, 1, ModelPolicyPresets.FullAgent(), CancellationToken.None));
        Assert.Equal(1L, loser.ExpectedRevision);
        Assert.Equal(2L, loser.ActualRevision);

        // El ganador no fue pisado por el perdedor.
        Assert.Equal(ModelPolicyCategory.PatchOnly,
            store.Get(key, CancellationToken.None)!.Policy.Category);
        // El perdedor reintenta con la revisión real y gana.
        var retried = store.Set(key, 2, ModelPolicyPresets.FullAgent(), CancellationToken.None);
        Assert.Equal(3L, retried.Revision);
    }

    [Fact]
    public void Update_bumps_revision_and_delete_reopens_onboarding()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");

        using var store = NewStore(dir);
        store.Set(key, 0, ModelPolicyPresets.PatchOnly(), CancellationToken.None);
        var updated = store.Set(key, 1, ModelPolicyPresets.ScopedCoder(), CancellationToken.None);
        Assert.Equal(2L, updated.Revision);

        store.Delete(key, 2, CancellationToken.None);
        Assert.Null(store.Get(key, CancellationToken.None));

        // Eliminar una política inexistente es un error tipado, no un no-op silencioso.
        Assert.Throws<ModelPolicyNotFoundException>(() =>
            store.Delete(key, 2, CancellationToken.None));

        // Tras eliminar, la próxima creación vuelve a empezar en revisión 1: el onboarding
        // reaparece (Get devuelve null ⇒ fallback ObserveOnly ⇒ draft de onboarding).
        var recreated = store.Set(key, 0, ModelPolicyPresets.ObserveOnly(), CancellationToken.None);
        Assert.Equal(1L, recreated.Revision);
    }

    [Fact]
    public void Delete_with_stale_revision_conflicts()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");

        using var store = NewStore(dir);
        store.Set(key, 0, ModelPolicyPresets.PatchOnly(), CancellationToken.None);
        store.Set(key, 1, ModelPolicyPresets.ScopedCoder(), CancellationToken.None);

        // Borrar con revisión obsoleta no borra: el cambio concurrente sobrevive.
        var conflict = Assert.Throws<ModelPolicyRevisionConflictException>(() =>
            store.Delete(key, 1, CancellationToken.None));
        Assert.Equal(1L, conflict.ExpectedRevision);
        Assert.Equal(2L, conflict.ActualRevision);
        Assert.Equal(ModelPolicyCategory.ScopedCoder,
            store.Get(key, CancellationToken.None)!.Policy.Category);
    }

    // ---- Historial (ADR-0044 §8) ----

    [Fact]
    public void History_records_created_updated_deleted()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");
        var t0 = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var clockIndex = 0;
        var stamps = new[] { t0, t1, t2 };
        var store = new SqliteModelPolicyStore(Path.Combine(dir, "user.db"),
            () => stamps[clockIndex++]);

        store.Set(key, 0, ModelPolicyPresets.ObserveOnly(), CancellationToken.None);
        store.Set(key, 1, ModelPolicyPresets.PatchOnly(), CancellationToken.None);
        store.Delete(key, 2, CancellationToken.None);

        var history = store.History(key, CancellationToken.None);
        Assert.Equal(3, history.Count);
        Assert.Equal("created", history[0].ChangeKind);
        Assert.Equal("updated", history[1].ChangeKind);
        Assert.Equal("deleted", history[2].ChangeKind);
        Assert.Null(history[0].OldValueJson);
        Assert.NotNull(history[0].NewValueJson);
        Assert.Contains("ObserveOnly", history[0].NewValueJson);
        Assert.NotNull(history[1].OldValueJson);
        Assert.Contains("PatchOnly", history[1].NewValueJson);
        // Eliminar borra la preferencia, no el historial (ADR-0044 §7).
        Assert.Null(history[2].NewValueJson);
        Assert.Contains("PatchOnly", history[2].OldValueJson);
        Assert.Equal(t0, history[0].ChangedAt);
        Assert.Equal(t2, history[2].ChangedAt);
    }

    // ---- Selección por workspace + onboarding (ADR-0044 §6) ----

    [Fact]
    public void Select_unknown_model_requires_onboarding_and_persists_nothing()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");
        var service = new OmniCore.Host.ModelPolicyService(NewStore(dir));

        var result = service.Select("ws|test", key, "gpt-x", ephemeralSafe: false, CancellationToken.None);

        Assert.True(result.NeedsOnboarding);
        Assert.Null(result.Selection);
        Assert.Null(result.Policy);
        Assert.NotNull(result.Draft);
        // M3: sin evidencia de cualificación la recomendación es conservadora.
        Assert.Equal(ModelPolicyCategory.ObserveOnly, result.Draft!.RecommendedCategory);
        Assert.False(result.Draft!.HasQualificationEvidence);
        // No se persistió ninguna selección: el onboarding debe confirmarse antes.
        Assert.Null(service.CurrentSelection("ws|test", CancellationToken.None));
    }

    [Fact]
    public void Select_with_policy_persists_selection()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");
        var service = new OmniCore.Host.ModelPolicyService(NewStore(dir));

        service.Set(key, 0, ModelPolicyPresets.PatchOnly(), CancellationToken.None);
        var result = service.Select("ws|test", key, "gpt-x", ephemeralSafe: false, CancellationToken.None);

        Assert.False(result.NeedsOnboarding);
        Assert.NotNull(result.Policy);
        Assert.False(result.Selection!.EphemeralObserveOnly);
        var current = service.CurrentSelection("ws|test", CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal("gpt-x", current!.ModelId);
        Assert.True(key.Equals(current.Key));
    }

    [Fact]
    public void Select_ephemeral_safe_marks_selection()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");
        var service = new OmniCore.Host.ModelPolicyService(NewStore(dir));

        var result = service.Select("ws|test", key, "gpt-x", ephemeralSafe: true, CancellationToken.None);

        Assert.False(result.NeedsOnboarding);
        Assert.True(result.Selection!.EphemeralObserveOnly);
        // El modo efímero sobrevive al reinicio (persistido en model_selections).
        var current = service.CurrentSelection("ws|test", CancellationToken.None);
        Assert.True(current!.EphemeralObserveOnly);
    }

    [Fact]
    public void Deleting_policy_reopens_onboarding_on_next_selection()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");
        var store = NewStore(dir);
        var service = new OmniCore.Host.ModelPolicyService(store);

        store.Set(key, 0, ModelPolicyPresets.FullAgent(), CancellationToken.None);
        var selected = service.Select("ws|test", key, "gpt-x", ephemeralSafe: false, CancellationToken.None);
        Assert.False(selected.NeedsOnboarding);

        // El usuario elimina la política...
        store.Delete(key, 1, CancellationToken.None);
        // ...y la siguiente selección de la misma configuración reabre el onboarding.
        var again = service.Select("ws|test", key, "gpt-x", ephemeralSafe: false, CancellationToken.None);
        Assert.True(again.NeedsOnboarding);
        Assert.NotNull(again.Draft);
    }

    [Fact]
    public void Effective_for_ephemeral_selection_is_ObserveOnly_even_with_saved_policy()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");
        var service = new OmniCore.Host.ModelPolicyService(NewStore(dir));

        // Hay una política guardada amplia...
        service.Set(key, 0, ModelPolicyPresets.FullAgent(), CancellationToken.None);
        // ...pero el usuario eligió "usar una vez en modo seguro" en esta selección.
        service.Select("ws|test", key, "gpt-x", ephemeralSafe: true, CancellationToken.None);

        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 16,
            GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8);
        var effective = service.EffectiveForSelection("ws|test", harness, CancellationToken.None);

        Assert.Equal(ModelPolicyCategory.ObserveOnly, effective.Category);
        Assert.Equal(FileMutationMode.None, effective.MutationPolicy.Mode);
    }

    [Fact]
    public void Effective_without_policy_is_fallback_ObserveOnly()
    {
        var dir = TempDir();
        var key = ModelPolicyKey.For("openai", "gpt-x");
        var service = new OmniCore.Host.ModelPolicyService(NewStore(dir));

        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 16,
            GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8);
        var effective = service.Effective(key, harness, CancellationToken.None);

        Assert.True(effective.IsFallback);
        Assert.Equal(ModelPolicyCategory.ObserveOnly, effective.Category);
        Assert.False(effective.MutationPolicy.CanMutateFiles);
    }
}
