using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class Epic013ConfigurationTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-epic013-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Typed_static_yaml_loads_valid_provider_and_model_values()
    {
        var loaded = new ConfigLoader().Load(
            "providers:\n  local:\n    kind: llamaCpp\n    baseUrl: http://127.0.0.1:8080/v1\n    authRef: local-key\n",
            "models:\n  worker:\n    provider: local\n    context: 16384\n    recommendedUsableContext: 12000\n    maxOutput: 2048\n    parametersBillions: 7\n");

        Assert.Equal("llamaCpp", loaded.ProviderKind("local"));
        Assert.Equal("local-key", loaded.Registry.Provider("local")!.Auth.SecretRef);
        Assert.Equal(16384, loaded.Registry.Model("worker")!.ContextWindow);
        Assert.Equal(12000, loaded.Registry.Model("worker")!.RecommendedUsableContext);
        Assert.Equal(7, loaded.Registry.Model("worker")!.ParameterCountBillions);
    }

    [Fact]
    public void Schema_validation_collects_unknown_wrong_missing_and_range_problems_with_paths()
    {
        var error = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(
            "providers:\n  local:\n    baseUrl: http://127.0.0.1:8080\n    surprise: true\n  missing-url: {}\n",
            "models:\n  worker:\n    context: not-a-number\n    maxOutput: 0\n"));

        Assert.Contains(error.Diagnostics, d => d.File == "providers.yaml" && d.KeyPath == "providers.local.surprise");
        Assert.Contains(error.Diagnostics, d => d.File == "providers.yaml" && d.KeyPath == "providers.missing-url.baseUrl");
        Assert.Contains(error.Diagnostics, d => d.File == "models.yaml" && d.KeyPath == "models.worker.provider");
        Assert.Contains(error.Diagnostics, d => d.File == "models.yaml" && d.KeyPath == "models.worker.context");
        Assert.Contains(error.Diagnostics, d => d.File == "models.yaml" && d.KeyPath == "models.worker.maxOutput");
        Assert.All(error.Diagnostics, d => Assert.NotNull(d.Message.Key));
    }

    [Fact]
    public void Invalid_yaml_reports_line_and_column_and_secret_values_never_enter_errors()
    {
        var syntax = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(
            "providers:\n  local: [\n", "models:\n  worker: {}\n"));
        Assert.Contains(syntax.Diagnostics, d => d.File == "providers.yaml" && d.Line is > 0 && d.Column is > 0);
        Assert.Contains(syntax.Diagnostics, d => d.File == "models.yaml" && d.KeyPath == "models.worker.provider");

        const string secret = "sk-never-print-this-value";
        var rejected = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(
            "providers:\n  local:\n    baseUrl: http://127.0.0.1:8080\n    auth: " + secret + "\n", null));
        Assert.DoesNotContain(secret, rejected.ToString(), StringComparison.Ordinal);
        Assert.Contains(rejected.Diagnostics, d => d.KeyPath == "providers.local.auth");
    }

    [Fact]
    public void Configuration_scopes_use_most_specific_value_unless_broader_scope_locks_key()
    {
        var resolved = ConfigurationScopeResolver.Resolve(new[]
        {
            new ConfigScopeLayer<string>(ConfigScope.BuiltIn,
                new Dictionary<string, string> { ["model"] = "builtin", ["theme"] = "builtin" }),
            new ConfigScopeLayer<string>(ConfigScope.User,
                new Dictionary<string, string> { ["model"] = "user" }, new HashSet<string> { "model" }),
            new ConfigScopeLayer<string>(ConfigScope.Project,
                new Dictionary<string, string> { ["model"] = "project", ["theme"] = "project" }),
            new ConfigScopeLayer<string>(ConfigScope.Workspace,
                new Dictionary<string, string> { ["model"] = "workspace", ["theme"] = "workspace" }),
        });

        Assert.Equal("user", resolved.Values["model"]);
        Assert.Equal("workspace", resolved.Values["theme"]);
        Assert.Contains(resolved.Contributions, c => c.Scope == ConfigScope.Workspace && c.Key == "theme");
    }

    [Fact]
    public void Project_permission_restrictions_act_as_deny_or_ask_and_never_grant()
    {
        var deny = OmniHost.CreateProjectRestrictionPolicy(RunMode.Act,
            new Dictionary<string, string> { ["filesystem.write"] = "deny" });
        var ask = OmniHost.CreateProjectRestrictionPolicy(RunMode.Act,
            new Dictionary<string, string> { ["filesystem.write"] = "ask" });
        var intent = new ToolIntent(ToolCallId.New(), new ToolId("filesystem.write"), "{}",
            EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null);

        Assert.Equal(PermissionDecision.Deny, deny.Evaluate(intent).Final);
        Assert.Equal(PermissionDecision.Ask, ask.Evaluate(intent).Final);
        Assert.Equal(PermissionDecision.Allow,
            OmniHost.CreateProjectRestrictionPolicy(RunMode.Act, null).Evaluate(intent).Final);
    }

    [Fact]
    public void Workspace_config_is_ignored_untrusted_and_applies_allowed_settings_when_trusted()
    {
        var root = TempDirectory();
        var config = Path.Combine(root, ".omnicore");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.yaml"),
            "defaultModel: worker\npermissionRestrictions:\n  filesystem.write: deny\n");
        File.WriteAllText(Path.Combine(config, "providers.yaml"), "this is not valid schema but untrusted ignores it\n");

        var ignored = WorkspaceConfigurationLoader.Load(root, trusted: false);
        Assert.True(ignored.Ignored);
        Assert.Null(ignored.Settings);
        Assert.Empty(ignored.Diagnostics);

        File.Delete(Path.Combine(config, "providers.yaml"));
        var applied = WorkspaceConfigurationLoader.Load(root, trusted: true, alias => alias == "worker");
        Assert.Equal("worker", applied.Settings!.DefaultModel);
        Assert.Equal("deny", applied.Settings.PermissionRestrictions!["filesystem.write"]);
        var intent = new ToolIntent(ToolCallId.New(), new ToolId("filesystem.write"), "{}",
            EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null);
        var effectivePolicy = OmniHost.CreateProjectRestrictionPolicy(RunMode.Act,
            applied.Settings.PermissionRestrictions);
        Assert.Equal(PermissionDecision.Deny, effectivePolicy.Evaluate(intent).Final);

        var rejectedAlias = Assert.Throws<ConfigValidationException>(() =>
            WorkspaceConfigurationLoader.Load(root, true, _ => false));
        Assert.Contains(rejectedAlias.Diagnostics, d => d.KeyPath == "defaultModel");
    }

    [Fact]
    public void Trusted_repo_configuration_rejects_forbidden_security_keys_and_files()
    {
        var root = TempDirectory();
        var config = Path.Combine(root, ".omnicore");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.yaml"),
            "providers: {}\ncredentials: {}\npermissions:\n  shell.exec: allow\nsandbox: none\n"
            + "permissionRestrictions:\n  filesystem.write: allow\n");
        File.WriteAllText(Path.Combine(config, "providers.yaml"), "providers: {}\n");

        var error = Assert.Throws<ConfigValidationException>(() => WorkspaceConfigurationLoader.Load(root, true));
        Assert.Contains(error.Diagnostics, d => d.File == "settings.yaml" && d.KeyPath == "providers");
        Assert.Contains(error.Diagnostics, d => d.File == "settings.yaml" && d.KeyPath == "credentials");
        Assert.Contains(error.Diagnostics, d => d.File == "settings.yaml" && d.KeyPath == "permissions");
        Assert.Contains(error.Diagnostics, d => d.File == "settings.yaml" && d.KeyPath == "sandbox");
        Assert.Contains(error.Diagnostics, d => d.File == "settings.yaml"
            && d.KeyPath == "permissionRestrictions.filesystem.write");
        Assert.Contains(error.Diagnostics, d => d.File == "providers.yaml");
    }

    [Fact]
    public void Workspace_trust_persists_by_id_and_canonical_path_outside_the_repository()
    {
        var root = TempDirectory();
        var data = Path.Combine(TempDirectory(), "user-data");
        var paths = new DefaultPlatformPaths(data);
        var store = new WorkspaceTrustStore(paths);

        Assert.False(store.IsTrusted(root));
        store.SetTrusted(root, true);
        Assert.True(new WorkspaceTrustStore(paths).IsTrusted(root));
        Assert.StartsWith(data, Path.GetFullPath(Path.Combine(data, "trust.yaml")));
        Assert.False(File.Exists(Path.Combine(root, "trust.yaml")));

        var moved = Path.Combine(TempDirectory(), "moved");
        Directory.CreateDirectory(moved);
        Assert.False(store.IsTrusted(moved));
        store.SetTrusted(root, false);
        Assert.False(store.IsTrusted(root));
    }

    [Fact]
    public void Token_counter_selection_uses_declared_provider_kind_and_normalizes_root_endpoint()
    {
        var provider = new ProviderDescriptor("local", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:8080/proxy/v1/", AuthConfig.ApiKey("secret"), true, true, true);

        Assert.IsType<LlamaCppTokenCounter>(OmniHost.CreateTokenCounter(provider, "llamaCpp", "key"));
        Assert.IsType<HeuristicTokenCounter>(OmniHost.CreateTokenCounter(provider, null, "key"));
        Assert.Equal("http://127.0.0.1:8080/proxy",
            OmniHost.GetLlamaCppTokenizeBaseUrl(provider.BaseUrl));
        Assert.Equal("https://host.example",
            OmniHost.GetLlamaCppTokenizeBaseUrl("https://host.example/v1"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Llama_token_counter_posts_to_root_tokenize_with_provider_auth()
    {
        var handler = new CaptureHandler(request =>
        {
            Assert.Equal("http://127.0.0.1:8080/tokenize", request.RequestUri!.ToString());
            Assert.Equal("Bearer exact-provider-key", request.Headers.Authorization!.ToString());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tokens\":[1,2,3]}")
            };
        });
        var counter = OmniHost.CreateTokenCounter(new ProviderDescriptor("llama", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:8080/v1", AuthConfig.ApiKey("provider-secret"), true, true, true),
            "ikLlama", "exact-provider-key", () => new HttpClient(handler));
        var item = new ContextItem("message", ContextItemKind.UserMessage, "distinct tokenizer request", 1,
            ContextPriority.Normal, RetentionPolicy.ConversationWindow,
            new ContextProvenance("test", ContributionCategory.Conversation, "test", ScopeLevel.User, false));

        Assert.Equal(3, await counter.CountAsync(item, TestContext.Current.CancellationToken));
        Assert.Equal(TokenCountAccuracy.Exact, counter.Accuracy);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(response(request));
    }
}
