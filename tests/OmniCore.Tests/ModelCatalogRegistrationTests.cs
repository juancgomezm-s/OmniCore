namespace OmniCore.Tests;

using OmniCore.Host;

public sealed class ModelCatalogRegistrationTests
{
    [Fact]
    public void Discovery_replaces_withdrawn_selection_in_same_provider_without_inheriting_permissions()
    {
        using var directory = new CatalogDirectory();
        using var host = ModelPolicyHost.Create(directory.Root);
        var ct = TestContext.Current.CancellationToken;
        var workspace = ModelPolicyHost.WorkspaceSelectionId(directory.Root);
        var oldKey = new ModelPolicyKeyDto("chatgpt", "retired");
        host.RegisterChatGptModel("retired", 8192, 2048);
        host.Set(oldKey, 0, "PatchOnly", null, ct);
        host.Select(workspace, oldKey, "retired", false, ct);
        var replacementKey = new ModelPolicyKeyDto("chatgpt", "replacement");
        host.Set(replacementKey, 0, "PatchOnly", null, ct);
        var changed = host.ApplyChatGptCatalog(new[] { new AvailableChatGptModel("other", "Other"),
            new AvailableChatGptModel("replacement", "Replacement", IsDefault: true) }, workspace, ct);
        Assert.Contains("retired → replacement", changed);
        var current = host.CurrentSelection(workspace, ct)!;
        Assert.Equal("replacement", current.ModelId);
        Assert.Equal("chatgpt", current.Key.ProviderId);
        Assert.True(current.ObserveOnly);
        Assert.DoesNotContain(host.Models, m => m.Id == "retired");
        Assert.Contains(host.Models, m => m.Id == "local-worker");
        Assert.Equal("PatchOnly", host.Get(oldKey, ct)!.Category);
        Assert.Equal("PatchOnly", host.Get(replacementKey, ct)!.Category);
        Assert.NotNull(OmniHost.LoadUserConfiguration(directory.Config).Registry.Model("retired"));
        var contents = File.ReadAllText(Path.Combine(directory.Config, "models.yaml"));
        Assert.Null(host.ApplyChatGptCatalog(new[] { new AvailableChatGptModel("other", "Other"),
            new AvailableChatGptModel("replacement", "Replacement", IsDefault: true) }, workspace, ct));
        Assert.Equal(contents, File.ReadAllText(Path.Combine(directory.Config, "models.yaml")));
    }

    [Fact]
    public void Empty_catalog_or_failed_registration_does_not_switch_to_a_different_provider()
    {
        using var directory = new CatalogDirectory();
        using var host = ModelPolicyHost.Create(directory.Root);
        var ct = TestContext.Current.CancellationToken;
        var workspace = ModelPolicyHost.WorkspaceSelectionId(directory.Root);
        host.RegisterChatGptModel("old", 8192, 2048);
        host.Select(workspace, new ModelPolicyKeyDto("chatgpt", "old"), "old", true, ct);
        var before = File.ReadAllText(Path.Combine(directory.Config, "models.yaml"));
        Assert.Throws<ArgumentException>(() => host.ApplyChatGptCatalog(new[] {
            new AvailableChatGptModel("invalid\nID", "Bad") }, workspace, ct));
        Assert.Equal(before, File.ReadAllText(Path.Combine(directory.Config, "models.yaml")));
        Assert.Equal("old", host.CurrentSelection(workspace, ct)!.ModelId);
        Assert.Contains("No hay modelos", host.ApplyChatGptCatalog(Array.Empty<AvailableChatGptModel>(), workspace, ct));
        Assert.Equal("old", host.CurrentSelection(workspace, ct)!.ModelId);
        Assert.DoesNotContain(host.Models, m => m.ProviderId == "chatgpt");
    }

    [Fact]
    public void Subscription_registration_preserves_local_provider_routing_and_original_backups()
    {
        using var directory = new CatalogDirectory();
        const string providers = "providers:\n  local-qwen: { baseUrl: https://127.0.0.1:8080/v1, family: OpenAiChatCompatible, authRef: test-ref, caCertificate: C:/test.pem }\n";
        const string models = "models:\n  qwen: { provider: local-qwen, context: 32768, maxOutput: 8192, aliases: [worker] }\nrouting:\n  exploration: [worker]\n";
        File.WriteAllText(Path.Combine(directory.Config, "providers.yaml"), providers);
        File.WriteAllText(Path.Combine(directory.Config, "models.yaml"), models);
        using var host = ModelPolicyHost.Create(directory.Root);
        host.RegisterChatGptModel("subscription-test-model", 8192, 2048);
        var loaded = OmniHost.LoadUserConfiguration(directory.Config);
        var local = loaded.Registry.Provider("local-qwen")!;
        Assert.Equal("https://127.0.0.1:8080/v1", local.BaseUrl);
        Assert.Equal("C:/test.pem", local.TrustedCertificatePath);
        Assert.Equal("test-ref", local.Auth.SecretRef);
        Assert.Equal("qwen", loaded.Registry.Resolve("worker").Id);
        Assert.Equal("codex", loaded.Registry.Provider("chatgpt")!.Profile);
        Assert.Equal("chatgpt", loaded.Registry.Model("subscription-test-model")!.ProviderId);
        Assert.Equal(providers, File.ReadAllText(Directory.GetFiles(directory.Config, "providers.yaml.backup-*").Single()));
        Assert.Equal(models, File.ReadAllText(Directory.GetFiles(directory.Config, "models.yaml.backup-*").Single()));
        Assert.Equal("subscription-test-model", host.Models.Last().Id);
        Assert.Equal("chatgpt", OmniCliRuntime.ResolveExplicitModel(loaded, "subscription-test-model")!.ProviderId);
        Assert.Null(OmniCliRuntime.ResolveExplicitModel(loaded, "missing-model"));
    }

    [Fact]
    public void Invalid_or_duplicate_registration_does_not_change_configuration()
    {
        using var directory = new CatalogDirectory();
        using var host = ModelPolicyHost.Create(directory.Root);
        Assert.Throws<ArgumentException>(() => host.RegisterChatGptModel("bad\nmodel", 8192, 2048));
        Assert.Empty(Directory.GetFiles(directory.Config));
        host.RegisterChatGptModel("subscription-test", 8192, 2048);
        var before = File.ReadAllText(Path.Combine(directory.Config, "models.yaml"));
        Assert.Throws<ArgumentException>(() => host.RegisterChatGptModel("subscription-test", 8192, 2048));
        Assert.Throws<ArgumentException>(() => host.RegisterChatGptModel("another", 10, 20));
        Assert.Equal(before, File.ReadAllText(Path.Combine(directory.Config, "models.yaml")));
        Assert.Contains(host.Models, m => m.Id == "local-worker");
    }

    [Fact]
    public void Selection_roundtrips_by_workspace_and_retains_explicit_observe_only()
    {
        using var directory = new CatalogDirectory();
        using var host = ModelPolicyHost.Create(directory.Root);
        host.RegisterChatGptModel("subscription-test", 8192, 2048);
        var workspace = ModelPolicyHost.WorkspaceSelectionId(directory.Root);
        var key = new ModelPolicyKeyDto("chatgpt", "subscription-test");
        var ct = TestContext.Current.CancellationToken;
        Assert.True(host.Select(workspace, key, key.ModelId, false, ct).NeedsOnboarding);
        Assert.Null(host.CurrentSelection(workspace, ct));
        host.Select(workspace, key, key.ModelId, true, ct);
        using var reopened = ModelPolicyHost.Create(directory.Root);
        var restored = reopened.CurrentSelection(workspace, ct)!;
        Assert.Equal("subscription-test", restored.ModelId);
        Assert.True(restored.ObserveOnly);
        Assert.Null(host.CurrentSelection(workspace + "-other", ct));
    }

    private sealed class CatalogDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-model-catalog-" + Guid.NewGuid().ToString("N"));
        public string Config => Path.Combine(Root, "config");
        public CatalogDirectory() => Directory.CreateDirectory(Config);
        public void Dispose()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + Path.Combine(Root, "user.db"));
            connection.Open();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            connection.Close();
            Directory.Delete(Root, true);
        }
    }
}
