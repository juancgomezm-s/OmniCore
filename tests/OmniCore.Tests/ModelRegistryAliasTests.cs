namespace OmniCore.Tests;

using OmniCore.Models;

/// <summary>
/// Tests for model alias resolution in <see cref="ModelRegistry"/> (M5: alias de modelos).
/// </summary>
public sealed class ModelRegistryAliasTests
{
    private static ModelRegistry RegistryWithTwoModels()
    {
        var m1 = new ModelDefinition(
            id: "qwen-27b",
            providerId: "local",
            contextWindow: 65536,
            recommendedUsableContext: 50000,
            maxOutputTokens: 8192,
            parameterCountBillions: 27,
            aliases: new[] { "local", "fast" });

        var m2 = new ModelDefinition(
            id: "sonnet-4",
            providerId: "anthropic",
            contextWindow: 200000,
            recommendedUsableContext: 150000,
            maxOutputTokens: 8192,
            parameterCountBillions: null,
            aliases: new[] { "frontier", "strong" });

        return new ModelRegistry()
            .AddModel(m1)
            .AddModel(m2);
    }

    // 1) Exact id match
    [Fact]
    public void Resolve_ExactId_ReturnsThatModel()
    {
        var registry = RegistryWithTwoModels();

        var model = registry.Resolve("qwen-27b");

        Assert.Equal("qwen-27b", model.Id);
        Assert.Equal("local", model.ProviderId);
    }

    // 2) Alias match
    [Fact]
    public void Resolve_Alias_ReturnsModelDeclaringIt()
    {
        var registry = RegistryWithTwoModels();

        var model = registry.Resolve("frontier");

        Assert.Equal("sonnet-4", model.Id);
    }

    // 3) Id wins over alias (when a name is both an id and an alias of another model, ambiguity is thrown at registration — never silent)
    [Fact]
    public void AddModel_IdAndAliasConflict_ThrowsAmbiguous()
    {
        // m1 id = "alpha", m2 alias = "alpha"
        var m1 = new ModelDefinition("alpha", "p1", 1000, 800, 100);
        var m2 = new ModelDefinition("beta", "p2", 2000, 1600, 200, aliases: new[] { "alpha" });
        var registry = new ModelRegistry().AddModel(m1);

        var ex = Assert.Throws<AmbiguousModelAliasException>(() => registry.AddModel(m2));

        Assert.Equal("alpha", ex.Alias);
        Assert.Equal("alpha", ex.ModelId1); // the existing model whose id matches
        Assert.Equal("beta", ex.ModelId2);  // the new model declaring the alias
    }

    // 4) Unknown name throws typed exception
    [Fact]
    public void Resolve_Unknown_ThrowsUnknownModelException()
    {
        var registry = RegistryWithTwoModels();

        var ex = Assert.Throws<UnknownModelException>(() => registry.Resolve("does-not-exist"));

        Assert.Equal("does-not-exist", ex.Name);
    }

    // 5) Duplicate alias across two models throws at registration time (second AddModel)
    [Fact]
    public void AddModel_DuplicateAlias_ThrowsAmbiguousModelAliasException()
    {
        var m1 = new ModelDefinition("m1", "p1", 1000, 800, 100, aliases: new[] { "shared" });
        var m2 = new ModelDefinition("m2", "p2", 2000, 1600, 200, aliases: new[] { "shared" });
        var registry = new ModelRegistry().AddModel(m1);

        var ex = Assert.Throws<AmbiguousModelAliasException>(() => registry.AddModel(m2));

        Assert.Equal("shared", ex.Alias);
        Assert.Equal("m1", ex.ModelId1);
        Assert.Equal("m2", ex.ModelId2);
    }

    // 6) Alias equal to another model's id is rejected at registration time
    [Fact]
    public void AddModel_AliasEqualsExistingId_ThrowsAmbiguousModelAliasException()
    {
        var m1 = new ModelDefinition("worker", "p1", 1000, 800, 100);
        var m2 = new ModelDefinition("m2", "p2", 2000, 1600, 200, aliases: new[] { "worker" });
        var registry = new ModelRegistry().AddModel(m1);

        var ex = Assert.Throws<AmbiguousModelAliasException>(() => registry.AddModel(m2));

        Assert.Equal("worker", ex.Alias);
        Assert.Equal("worker", ex.ModelId1); // the existing model whose id matches
        Assert.Equal("m2", ex.ModelId2);  // the new model declaring the alias
    }

    // 7) Case sensitivity: resolution is ordinal (case-sensitive)
    [Fact]
    public void Resolve_CaseSensitivity_IsOrdinal()
    {
        var m1 = new ModelDefinition("MyModel", "p1", 1000, 800, 100, aliases: new[] { "MyAlias" });
        var registry = new ModelRegistry().AddModel(m1);

        // Exact case works
        Assert.Equal("MyModel", registry.Resolve("MyModel").Id);
        Assert.Equal("MyModel", registry.Resolve("MyAlias").Id);

        // Wrong case fails
        Assert.Throws<UnknownModelException>(() => registry.Resolve("mymodel"));
        Assert.Throws<UnknownModelException>(() => registry.Resolve("myalias"));
        Assert.Throws<UnknownModelException>(() => registry.Resolve("MYMODEL"));
    }

    // 8) ResolveDefault unchanged (returns first model added)
    [Fact]
    public void ResolveDefault_Unchanged_ReturnsFirstModel()
    {
        var m1 = new ModelDefinition("first", "p1", 1000, 800, 100, aliases: new[] { "alias1" });
        var m2 = new ModelDefinition("second", "p2", 2000, 1600, 200, aliases: new[] { "alias2" });
        var registry = new ModelRegistry().AddModel(m1).AddModel(m2);

        var defaultModel = registry.ResolveDefault();

        Assert.Equal("first", defaultModel.Id);
    }

    // Additional: multiple aliases on same model all work
    [Fact]
    public void Resolve_MultipleAliasesSameModel_AllResolve()
    {
        var m1 = new ModelDefinition("model", "p1", 1000, 800, 100, aliases: new[] { "a", "b", "c" });
        var registry = new ModelRegistry().AddModel(m1);

        Assert.Equal("model", registry.Resolve("a").Id);
        Assert.Equal("model", registry.Resolve("b").Id);
        Assert.Equal("model", registry.Resolve("c").Id);
    }

    // Additional: empty alias list works
    [Fact]
    public void Resolve_ModelWithNoAliases_OnlyIdWorks()
    {
        var m1 = new ModelDefinition("solo", "p1", 1000, 800, 100, aliases: Array.Empty<string>());
        var registry = new ModelRegistry().AddModel(m1);

        Assert.Equal("solo", registry.Resolve("solo").Id);
        Assert.Throws<UnknownModelException>(() => registry.Resolve("anything-else"));
    }
}