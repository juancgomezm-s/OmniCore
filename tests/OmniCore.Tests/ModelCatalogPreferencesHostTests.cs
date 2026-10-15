namespace OmniCore.Tests;

using OmniCore.Host;

public sealed class ModelCatalogPreferencesHostTests
{
    [Fact]
    public void Default_local_model_has_capacity_metadata_but_no_requested_output_limit()
    {
        var loaded = new ConfigLoader().Load(null, null);
        var model = loaded.Registry.Model("local-worker")!;
        Assert.Equal(2048, model.MaxOutputTokens);
        Assert.False(model.OutputLimitConfigured);
        Assert.Null(ModelRoutingHost.OutputTokenLimit(model, loaded.Registry.Provider("local")));
    }

    [Fact]
    public void Clearing_output_limit_removes_it_from_yaml_and_the_actual_request_selection()
    {
        using var fixture = new Fixture();
        var before = fixture.Host.Read(CancellationToken.None);
        var saved = fixture.Host.Save(new ModelCatalogDraft("fixture-model", "fixture", 8192, 6144,
            null, null, ["fixture-alias"], "fixture-model"), before.Revision, CancellationToken.None);
        Assert.Null(Assert.Single(saved.Models).MaxOutput);
        Assert.DoesNotContain("maxOutput:", File.ReadAllText(fixture.Models));
        var loaded = new ConfigLoader().Load(File.ReadAllText(fixture.Providers), File.ReadAllText(fixture.Models));
        var model = loaded.Registry.Model("fixture-model")!;
        Assert.False(model.OutputLimitConfigured);
        Assert.Null(ModelRoutingHost.OutputTokenLimit(model, loaded.Registry.Provider("fixture")));
        Assert.Equal(8192, model.ContextWindow);
        Assert.Equal(new[] { "fixture-alias" }, model.Aliases);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "omnicore-model-catalog-" + Guid.NewGuid().ToString("N"));
        internal string Config => Path.Combine(Root, "config");
        internal string Workspace => Path.Combine(Root, "workspace");
        internal string WorkspaceData => Path.Combine(Root, "workspace-data");
        internal string Models => Path.Combine(Config, "models.yaml");
        internal string Providers => Path.Combine(Config, "providers.yaml");
        internal string UserSettings => Path.Combine(Config, "settings.yaml");
        internal string ProjectSettings => Path.Combine(Workspace, ".omnicore", "settings.yaml");
        internal string WorkspaceSettings => Path.Combine(WorkspaceData, "settings.yaml");
        internal IModelCatalogPreferencesHost Host => ModelCatalogPreferencesHost.Create(Config, Workspace, WorkspaceData);

        internal Fixture()
        {
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Config);
            File.WriteAllText(Providers, "providers:\n  fixture: { family: OpenAiChatCompatible, baseUrl: https://example.invalid/v1, auth: none, billingMode: Local }\n");
        File.WriteAllText(Models, "models:\n  fixture-model:\n    provider: fixture\n    context: 8192\n    recommendedUsableContext: 6144\n    maxOutput: 2048\n    aliases: [fixture-alias]\n    reasoning: { supported: true, effortLevels: [low, high] }\n");
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    [Fact]
    public void Read_does_not_create_config_or_workspace_data_and_lists_configured_models_and_provider_families()
    {
        using var fixture = new Fixture();
        var result = fixture.Host.Read(CancellationToken.None);
        Assert.True(result.Available);
        Assert.Equal("fixture", Assert.Single(result.Providers).Id);
        var model = Assert.Single(result.Models);
        Assert.Equal("fixture-model", model.Id);
        Assert.Equal("fixture", model.ProviderId);
        Assert.Equal(new[] { "fixture-alias" }, model.Aliases);
        Assert.True(model.CanDelete);
        Assert.Empty(result.Diagnostics);
        Assert.False(Directory.Exists(fixture.WorkspaceData));
    }

    [Fact]
    public void Factory_computes_default_workspace_data_path_without_creating_it()
    {
        using var fixture = new Fixture();
        var dataRoot = Path.Combine(fixture.Root, "data-root");
        var host = ModelCatalogPreferencesHost.Create(fixture.Config, fixture.Workspace,
            workspaceDataDirectory: null, dataDirectory: dataRoot);
        Assert.True(host.Read(CancellationToken.None).Available);
        Assert.False(Directory.Exists(dataRoot));
    }

    [Fact]
    public void Save_adds_and_edits_supported_fields_aliases_and_preserves_unknown_model_leaves()
    {
        using var fixture = new Fixture();
        var initial = fixture.Host.Read(CancellationToken.None);
        var added = fixture.Host.Save(new("new-model", "fixture", 16384, 12288, 4096, 7.5,
            ["new-alias", "new-short"]), initial.Revision, CancellationToken.None);
        Assert.True(added.Available);
        Assert.Contains(added.Models, item => item.Id == "new-model" && item.Aliases.SequenceEqual(["new-alias", "new-short"]));
        var current = added.Models.Single(item => item.Id == "fixture-model");
        var edited = fixture.Host.Save(new(current.Id, current.ProviderId, 12288, 8192, 3072, null,
            ["renamed-alias"], current.Id), added.Revision, CancellationToken.None);
        Assert.True(new ConfigLoader().Load(File.ReadAllText(fixture.Providers), File.ReadAllText(fixture.Models))
            .Models!.Models!["fixture-model"].Reasoning!.Supported == true);
        Assert.Equal(12288, Assert.Single(edited.Models, item => item.Id == "fixture-model").Context);
        Assert.Contains("new-model", File.ReadAllText(fixture.Models));
        Assert.DoesNotContain("SECRET", edited.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_exposes_output_modality_and_positive_image_cost_cap_as_effective_catalog_fields()
    {
        using var fixture = new Fixture();
        var before = fixture.Host.Read(CancellationToken.None);
        var saved = fixture.Host.Save(new("fixture-model", "fixture", 8192, 6144, 2048, null,
            ["fixture-alias"], "fixture-model", ["text"], ["text", "image"], 0.35m),
            before.Revision, CancellationToken.None);
        var model = Assert.Single(saved.Models);
        Assert.Equal(["text", "image"], model.OutputModalities);
        Assert.Equal(0.35m, model.ImageGenerationMaxCostUsd);
        Assert.Contains("outputModalities", File.ReadAllText(fixture.Models));
        Assert.Contains("imageGenerationMaxCostUsd", File.ReadAllText(fixture.Models));
        File.WriteAllText(fixture.Providers, "providers:\n  fixture: { family: OpenAiChatCompatible, profile: openrouter-images, baseUrl: https://openrouter.ai/api/v1, authRef: fixture-key, billingMode: MeteredCurrency }\n");
        var loaded = new ConfigLoader().Load(File.ReadAllText(fixture.Providers), File.ReadAllText(fixture.Models));
        var profile = new ModelProfileResolver().Resolve(loaded.Registry.Model("fixture-model")!,
            loaded.Registry.Provider("fixture"));
        Assert.Contains("image", profile.OutputModalities);
        Assert.DoesNotContain("image", profile.InputModalities);
        Assert.Equal(0.35m, profile.ImageGenerationMaxCostUsd);
        Assert.Equal("openrouter-images", Assert.Single(fixture.Host.Read(CancellationToken.None).Providers).Profile);
    }

    [Fact]
    public void Image_generation_catalog_entry_requires_an_explicit_positive_cost_cap()
    {
        using var fixture = new Fixture();
        var before = fixture.Host.Read(CancellationToken.None);
        var error = Assert.Throws<ModelCatalogConfigurationException>(() => fixture.Host.Save(new("fixture-model", "fixture", 8192,
            6144, 2048, null, ["fixture-alias"], "fixture-model", ["text"], ["text", "image"], null),
            before.Revision, CancellationToken.None));
        Assert.Equal("modelCatalog.error.imageCostRequired", error.MessageKey);
        Assert.DoesNotContain("outputModalities", File.ReadAllText(fixture.Models));
    }

    [Fact]
    public void OpenRouter_image_route_is_created_atomically_with_shared_auth_reference_only()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Providers, "providers:\n  fixture: { family: OpenAiChatCompatible, baseUrl: https://example.invalid/v1, auth: none, billingMode: Local }\n  text-route: { family: OpenAiChatCompatible, baseUrl: https://openrouter.ai/api/v1, caCertificate: fixture-root.pem, authRef: shared-openrouter, billingMode: MeteredCurrency }\n");
        var host = Assert.IsType<ModelCatalogPreferencesHost>(fixture.Host);
        var before = host.Read(CancellationToken.None);
        Assert.Equal(before.Revision, host.Read(CancellationToken.None).Revision);
        var created = host.CreateOpenRouterImageRoute("text-route", before.Revision, CancellationToken.None);
        Assert.Equal("text-route-images", created.ProviderId);
        var config = File.ReadAllText(fixture.Providers);
        Assert.Contains("profile: openrouter-images", config);
        Assert.Contains("authRef: shared-openrouter", config);
        Assert.Contains("caCertificate: fixture-root.pem", config);
        Assert.DoesNotContain("apiKey", config, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("OpenAiChatCompatible", created.Snapshot.Providers.Single(item => item.Id == "text-route").Family);
        Assert.Null(created.Snapshot.Providers.Single(item => item.Id == "text-route").Profile);
        Assert.False(created.Snapshot.Providers.Single(item => item.Id == "text-route-images").CanCreateOpenRouterImageRoute);

        var modelSaved = fixture.Host.Save(new("image-model", created.ProviderId, 8192, 6144, null, null,
            [], InputModalities: ["text"], OutputModalities: ["text", "image"], ImageGenerationMaxCostUsd: 0.25m),
            created.Snapshot.Revision, CancellationToken.None);
        var effective = new ConfigLoader().Load(File.ReadAllText(fixture.Providers), File.ReadAllText(fixture.Models));
        Assert.Equal("fixture-root.pem", effective.Registry.Provider(created.ProviderId)!.TrustedCertificatePath);
        Assert.Equal(created.ProviderId, effective.Registry.Model("image-model")!.ProviderId);
        Assert.Equal(0.25m, modelSaved.Models.Single(item => item.Id == "image-model").ImageGenerationMaxCostUsd);
    }

    [Fact]
    public void OpenRouter_image_route_stale_revision_and_collision_leave_provider_yaml_unchanged()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Providers, "providers:\n  fixture: { family: OpenAiChatCompatible, baseUrl: https://example.invalid/v1, auth: none, billingMode: Local }\n  text-route: { family: OpenAiChatCompatible, baseUrl: https://openrouter.ai/api/v1, authRef: shared-openrouter }\n");
        var host = Assert.IsType<ModelCatalogPreferencesHost>(fixture.Host);
        var before = host.Read(CancellationToken.None);
        var original = File.ReadAllText(fixture.Providers);
        Assert.Throws<ModelCatalogConfigurationException>(() => host.CreateOpenRouterImageRoute("text-route", "stale", CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(fixture.Providers));

        var first = host.CreateOpenRouterImageRoute("text-route", before.Revision, CancellationToken.None);
        var second = host.CreateOpenRouterImageRoute("text-route", first.Snapshot.Revision, CancellationToken.None);
        Assert.Equal(first.ProviderId, second.ProviderId);
        var snapshot = File.ReadAllText(fixture.Providers);
        Assert.Equal(3, new ConfigLoader().Load(snapshot, File.ReadAllText(fixture.Models)).Registry.Providers().Count);
    }

    [Fact]
    public void Delete_is_blocked_by_user_routing_and_by_project_or_workspace_defaults_resolved_through_aliases()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Models, File.ReadAllText(fixture.Models) + "routing:\n  meta: [fixture-alias]\n");
        var routingHost = fixture.Host.Read(CancellationToken.None);
        Assert.Contains("modelCatalog.reference.routing.meta", Assert.Single(routingHost.Models).DeleteBlockers);
        Assert.Throws<ModelCatalogConfigurationException>(() => fixture.Host.Delete("fixture-model", routingHost.Revision, CancellationToken.None));

        File.WriteAllText(fixture.Models, File.ReadAllText(fixture.Models).Replace("routing:\n  meta: [fixture-alias]\n", "", StringComparison.Ordinal));
        File.WriteAllText(fixture.UserSettings, "defaultModel: fixture-alias\n");
        var userHost = fixture.Host.Read(CancellationToken.None);
        Assert.Contains("modelCatalog.reference.userDefault", Assert.Single(userHost.Models).DeleteBlockers);
        Assert.Throws<ModelCatalogConfigurationException>(() => fixture.Host.Delete("fixture-model", userHost.Revision, CancellationToken.None));

        File.Delete(fixture.UserSettings);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ProjectSettings)!);
        File.WriteAllText(fixture.ProjectSettings, "defaultModel: fixture-alias\n");
        var projectHost = fixture.Host.Read(CancellationToken.None);
        Assert.Contains("modelCatalog.reference.projectDefault", Assert.Single(projectHost.Models).DeleteBlockers);
        Assert.Throws<ModelCatalogConfigurationException>(() => fixture.Host.Delete("fixture-model", projectHost.Revision, CancellationToken.None));

        File.Delete(fixture.ProjectSettings);
        Directory.CreateDirectory(fixture.WorkspaceData);
        File.WriteAllText(fixture.WorkspaceSettings, "defaultModel: fixture-alias\n");
        var workspaceHost = fixture.Host.Read(CancellationToken.None);
        Assert.Contains("modelCatalog.reference.workspaceDefault", Assert.Single(workspaceHost.Models).DeleteBlockers);
        Assert.Throws<ModelCatalogConfigurationException>(() => fixture.Host.Delete("fixture-model", workspaceHost.Revision, CancellationToken.None));
    }

    [Fact]
    public void Delete_is_disabled_without_an_explicit_current_workspace_root()
    {
        using var fixture = new Fixture();
        var host = ModelCatalogPreferencesHost.Create(fixture.Config);
        var snapshot = host.Read(CancellationToken.None);
        Assert.False(Assert.Single(snapshot.Models).CanDelete);
        var error = Assert.Throws<ModelCatalogConfigurationException>(() => host.Delete("fixture-model", snapshot.Revision, CancellationToken.None));
        Assert.Equal("modelCatalog.error.workspaceScopeUnavailable", error.MessageKey);
    }

    [Fact]
    public void Delete_removes_only_unreferenced_definition_and_preserves_unrelated_yaml()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Models, "models:\n  fixture-model:\n    provider: fixture\n    context: 8192\n    maxOutput: 2048\n    aliases: [fixture-alias]\n    reasoning: { supported: true, effortLevels: [low, high] }\n  other: { provider: fixture, context: 8192, maxOutput: 1024 }\nrouting: { meta: [other] }\n");
        var initial = fixture.Host.Read(CancellationToken.None);
        Assert.True(initial.Models.Single(item => item.Id == "fixture-model").CanDelete);
        var after = fixture.Host.Delete("fixture-model", initial.Revision, CancellationToken.None);
        Assert.DoesNotContain(after.Models, item => item.Id == "fixture-model");
        var text = File.ReadAllText(fixture.Models);
        Assert.Contains("meta:", text);
        Assert.Contains("other:", text);
    }

    [Theory]
    [InlineData("providers")]
    [InlineData("models")]
    [InlineData("settings")]
    [InlineData("project")]
    [InlineData("workspace")]
    public void Save_rejects_a_revision_changed_in_any_known_scope_without_overwriting_it(string changedFile)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ProjectSettings)!);
        Directory.CreateDirectory(fixture.WorkspaceData);
        var initial = fixture.Host.Read(CancellationToken.None);
        var path = changedFile switch
        {
            "providers" => fixture.Providers, "models" => fixture.Models, "settings" => fixture.UserSettings,
            "project" => fixture.ProjectSettings, _ => fixture.WorkspaceSettings
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, (File.Exists(path) ? File.ReadAllText(path) : "") + "# concurrent change\n");
        var changed = File.ReadAllText(path);
        var exception = Assert.Throws<ModelCatalogConfigurationException>(() => fixture.Host.Save(
            new("new-model", "fixture", 8192, 8192, 1024, null, []), initial.Revision, CancellationToken.None));
        Assert.Equal("modelCatalog.error.revisionConflict", exception.MessageKey);
        Assert.Equal(changed, File.ReadAllText(path));
        Assert.DoesNotContain("new-model", File.ReadAllText(fixture.Models));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("project")]
    [InlineData("workspace")]
    public void Save_respects_existing_settings_writer_leases(string scope)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ProjectSettings)!);
        Directory.CreateDirectory(fixture.WorkspaceData);
        var path = scope switch
        {
            "settings" => fixture.UserSettings, "project" => fixture.ProjectSettings, _ => fixture.WorkspaceSettings
        };
        File.WriteAllText(path, "{} # existing scope\n");
        var lockPath = path + ".write.lock";
        using (new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // The scope file and its competing lease both exist before reading the content revision.
            // The lease itself is not part of that revision and must fail as unavailable, not stale.
            var snapshot = fixture.Host.Read(CancellationToken.None);
            Assert.True(snapshot.Available);
            var error = Assert.Throws<ModelCatalogConfigurationException>(() => fixture.Host.Save(
                new("new-model", "fixture", 8192, 8192, 2048, null, []), snapshot.Revision, CancellationToken.None));
            Assert.Equal("modelCatalog.error.unavailable", error.MessageKey);
            Assert.DoesNotContain("new-model", File.ReadAllText(fixture.Models));
        }
        File.Delete(lockPath);
    }

    [Fact]
    public void Invalid_draft_does_not_change_models_yaml()
    {
        using var fixture = new Fixture();
        var initial = fixture.Host.Read(CancellationToken.None);
        var original = File.ReadAllText(fixture.Models);
        var exception = Assert.Throws<ModelCatalogConfigurationException>(() => fixture.Host.Save(
            new("bad id", "fixture", 8192, 8192, 2048, null, []), initial.Revision, CancellationToken.None));
        Assert.Equal("modelCatalog.error.invalidId", exception.MessageKey);
        Assert.Equal(original, File.ReadAllText(fixture.Models));
    }
}
