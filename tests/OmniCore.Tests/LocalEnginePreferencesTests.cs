namespace OmniCore.Tests;

using OmniCore.Host;

public sealed class LocalEnginePreferencesTests
{
    [Fact]
    public void Reading_a_new_configuration_does_not_create_files_or_probe_an_endpoint()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Host.Read(CancellationToken.None);
        Assert.Empty(snapshot.Diagnostics);
        var local = Assert.Single(snapshot.Engines);
        Assert.Equal("local", local.ProviderId);
        Assert.Equal("attach", local.Mode);
        Assert.Equal(120, local.ReadinessTimeoutSeconds);
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public void Attach_roundtrip_preserves_remote_provider_auth_models_and_prior_local_defaults()
    {
        using var fixture = new Fixture();
        fixture.WriteProviders("""
            providers:
              cloud:
                family: AnthropicMessages
                baseUrl: https://api.example.invalid
                authRef: fixture-secret-reference
                billingMode: MeteredCurrency
                inputPricePerMillionUsd: 2.5
              local:
                family: OpenAiChatCompatible
                baseUrl: http://127.0.0.1:8080
                billingMode: Local
                auth: none
            """);
        var models = "models:\n  fixture: { provider: local, context: 8192, maxOutput: 2048 }\n";
        File.WriteAllText(fixture.Models, models);
        var initial = fixture.Host.Read(CancellationToken.None);
        var saved = fixture.Host.Save(new("local", "attach", "http://127.0.0.1:12345/v1"), initial.Revision, CancellationToken.None);
        Assert.Empty(saved.Diagnostics);
        Assert.Equal("http://127.0.0.1:12345/v1", Assert.Single(saved.Engines).BaseUrl);
        Assert.NotEqual(initial.Revision, saved.Revision);
        Assert.Equal(models, File.ReadAllText(fixture.Models));
        var loaded = new ConfigLoader().Load(File.ReadAllText(fixture.Providers), models);
        Assert.Equal("fixture-secret-reference", loaded.Registry.Provider("cloud")!.Auth.SecretRef);
        Assert.Equal(2.5m, loaded.Providers!.Providers!["cloud"].InputPricePerMillionUsd);
        Assert.DoesNotContain("fixture-secret-reference", saved.ToString());
    }

    [Fact]
    public void Managed_roundtrip_preserves_literal_argv_and_does_not_launch_the_executable()
    {
        using var fixture = new Fixture();
        var initial = fixture.Host.Read(CancellationToken.None);
        string[] arguments = ["--model", "C:/models/My model.gguf", "--port", "{port}", "", "a \"quoted\" argument"];
        var saved = fixture.Host.Save(new("my-engine", "managed", "auto", "C:/does-not-exist/server.exe",
            arguments, "C:/models", 180), initial.Revision, CancellationToken.None);
        var row = Assert.Single(saved.Engines, item => item.ProviderId == "my-engine");
        Assert.Equal("managed", row.Mode);
        Assert.Equal("auto", row.BaseUrl);
        Assert.Equal(arguments, row.Arguments);
        Assert.Equal("C:/does-not-exist/server.exe", row.Executable);
        Assert.Equal(180, row.ReadinessTimeoutSeconds);
        Assert.True(row.CanEdit);
        Assert.Contains(saved.Engines, item => item.ProviderId == "local");
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory, "*.tmp-*"));
        Assert.False(File.Exists(fixture.Providers + ".write.lock"));
    }

    [Fact]
    public void Changing_managed_to_attach_removes_only_the_managed_configuration()
    {
        using var fixture = new Fixture();
        var initial = fixture.Host.Read(CancellationToken.None);
        var managed = fixture.Host.Save(new("local", "managed", "auto", "server.exe", ["--port", "{port}"]),
            initial.Revision, CancellationToken.None);
        fixture.Host.Save(new("local", "attach", "http://localhost:9000"), managed.Revision, CancellationToken.None);
        var configuration = new ConfigLoader().Load(File.ReadAllText(fixture.Providers), null);
        var provider = configuration.Providers!.Providers!["local"];
        Assert.Null(provider.Managed);
        Assert.Equal("attach", provider.Host);
        Assert.True(provider.Auth!.IsNone);
    }

    [Fact]
    public void Stale_provider_or_model_revision_never_overwrites_either_file()
    {
        using var fixture = new Fixture();
        var initial = fixture.Host.Read(CancellationToken.None);
        var current = fixture.Host.Save(new("local", "attach", "http://localhost:9000"), initial.Revision, CancellationToken.None);
        var original = File.ReadAllText(fixture.Providers);
        AssertKey("revisionConflict", () => fixture.Host.Save(new("local", "attach", "http://localhost:9001"),
            initial.Revision, CancellationToken.None));
        File.WriteAllText(fixture.Models, "models: {}\n");
        AssertKey("revisionConflict", () => fixture.Host.Save(new("local", "attach", "http://localhost:9001"),
            current.Revision, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(fixture.Providers));
        Assert.Equal("models: {}\n", File.ReadAllText(fixture.Models));
    }

    [Theory]
    [InlineData("http://user:password@localhost:1234")]
    [InlineData("http://localhost:1234?key=value")]
    [InlineData("http://localhost:1234/#fragment")]
    [InlineData("file:///C:/server")]
    public void Endpoint_credentials_and_non_http_urls_are_rejected_without_writing(string endpoint)
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Host.Read(CancellationToken.None);
        AssertKey("invalidEndpoint", () => fixture.Host.Save(new("engine", "attach", endpoint),
            snapshot.Revision, CancellationToken.None));
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Theory]
    [InlineData("--api-key=fixture-value")]
    [InlineData("--password")]
    [InlineData("--token=fixture-value")]
    [InlineData("--access-token")]
    [InlineData("--refresh_token")]
    [InlineData("--hf-token")]
    [InlineData("--secret-key")]
    [InlineData("--credential")]
    [InlineData("--bearer")]
    public void Credential_arguments_are_not_accepted_by_the_engine_editor(string argument)
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Host.Read(CancellationToken.None);
        AssertKey("sensitive", () => fixture.Host.Save(new("engine", "managed", "auto", "server.exe", [argument, "fixture-value"]),
            snapshot.Revision, CancellationToken.None));
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public void Existing_sensitive_arguments_are_hidden_and_cannot_be_overwritten_from_a_redacted_draft()
    {
        using var fixture = new Fixture();
        fixture.WriteProviders("""
            providers:
              engine:
                family: OpenAiChatCompatible
                host: managed
                baseUrl: auto
                managed:
                  executable: server.exe
                  args: [--api-key, fixture-secret-value]
            """);
        var original = File.ReadAllText(fixture.Providers);
        var snapshot = fixture.Host.Read(CancellationToken.None);
        var row = Assert.Single(snapshot.Engines);
        Assert.False(row.CanEdit);
        Assert.Empty(row.Arguments);
        Assert.Null(row.Executable);
        Assert.Equal("localEngines.error.sensitive", row.UnavailableReasonKey);
        AssertKey("sensitive", () => fixture.Host.Save(new("engine", "managed", "auto", "other.exe"),
            snapshot.Revision, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(fixture.Providers));
    }

    [Fact]
    public void Remote_provider_cannot_be_replaced_by_a_local_engine_with_the_same_id()
    {
        using var fixture = new Fixture();
        fixture.WriteProviders("providers:\n  remote: { family: AnthropicMessages, baseUrl: https://example.invalid }\n");
        var original = File.ReadAllText(fixture.Providers);
        var snapshot = fixture.Host.Read(CancellationToken.None);
        Assert.Empty(snapshot.Engines);
        AssertKey("notLocal", () => fixture.Host.Save(new("remote", "attach", "http://localhost:9000"),
            snapshot.Revision, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(fixture.Providers));
    }

    [Theory]
    [InlineData("providers: [not-a-mapping]\n")]
    [InlineData(" \n")]
    public void Corrupt_or_empty_configuration_is_not_repaired_by_overwriting_it(string original)
    {
        using var fixture = new Fixture();
        fixture.WriteProviders(original);
        var snapshot = fixture.Host.Read(CancellationToken.None);
        Assert.Equal(["localEngines.error.configurationInvalid"], snapshot.Diagnostics);
        Assert.Empty(snapshot.Engines);
        AssertKey("configurationInvalid", () => fixture.Host.Save(
            new("local", "attach", "http://localhost:9000"), snapshot.Revision, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(fixture.Providers));
    }

    [Fact]
    public void Cancelled_save_and_non_loopback_managed_configuration_do_not_write()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Host.Read(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Host.Save(
            new("local", "attach", "http://localhost:9000"), snapshot.Revision, cancellation.Token));
        AssertKey("invalidEndpoint", () => fixture.Host.Save(new("local", "managed", "https://example.invalid", "server.exe"),
            snapshot.Revision, CancellationToken.None));
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public void Missing_provider_file_keeps_implicit_local_model_references_when_adding_an_engine()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        var models = "models:\n  fixture: { provider: local, context: 8192, maxOutput: 2048 }\n";
        File.WriteAllText(fixture.Models, models);
        var snapshot = fixture.Host.Read(CancellationToken.None);
        Assert.Empty(snapshot.Diagnostics);
        var saved = fixture.Host.Save(new("second", "attach", "http://localhost:12345"),
            snapshot.Revision, CancellationToken.None);
        Assert.Empty(saved.Diagnostics);
        Assert.Equal(new[] { "local", "second" }, saved.Engines.Select(row => row.ProviderId));
        Assert.Equal(models, File.ReadAllText(fixture.Models));
    }

    [Fact]
    public void Another_writer_lease_is_preserved_and_does_not_allow_an_overwrite()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Host.Read(CancellationToken.None);
        var saved = fixture.Host.Save(new("local", "attach", "http://localhost:12345"),
            snapshot.Revision, CancellationToken.None);
        var original = File.ReadAllText(fixture.Providers);
        var lease = fixture.Providers + ".write.lock";
        File.WriteAllText(lease, "another-writer");
        AssertKey("unavailable", () => fixture.Host.Save(new("local", "attach", "http://localhost:12346"),
            saved.Revision, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(fixture.Providers));
        Assert.Equal("another-writer", File.ReadAllText(lease));
    }

    [Theory]
    [InlineData("invalidId", "../escape", "attach", 120)]
    [InlineData("invalidMode", "local", "invalid", 120)]
    [InlineData("invalidTimeout", "local", "attach", 0)]
    [InlineData("invalidTimeout", "local", "attach", 3601)]
    public void Invalid_draft_fields_are_rejected_before_creating_the_config_directory(
        string code, string id, string mode, int timeout)
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Host.Read(CancellationToken.None);
        AssertKey(code, () => fixture.Host.Save(new(id, mode, "http://localhost:9000",
            ReadinessTimeoutSeconds: timeout), snapshot.Revision, CancellationToken.None));
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public void Tokenizer_and_token_count_arguments_remain_usable_and_explicit_endpoint_is_preserved()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Host.Read(CancellationToken.None);
        string[] arguments = ["--tokenizer", "C:/models/tokenizer.json", "--max-tokens", "2048"];
        var saved = fixture.Host.Save(new("engine", "managed", "http://127.0.0.1:12345/v1", "server.exe", arguments),
            snapshot.Revision, CancellationToken.None);
        var row = Assert.Single(saved.Engines, item => item.ProviderId == "engine");
        Assert.Equal("http://127.0.0.1:12345/v1", row.BaseUrl);
        Assert.Equal(arguments, row.Arguments);
        Assert.True(row.CanEdit);
    }

    private static void AssertKey(string code, Action action) =>
        Assert.Equal("localEngines.error." + code, Assert.Throws<LocalEngineConfigurationException>(action).MessageKey);

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "omni-engine-settings-" + Guid.NewGuid().ToString("N"));
        public string Providers => Path.Combine(Directory, "providers.yaml");
        public string Models => Path.Combine(Directory, "models.yaml");
        public TuiLocalEngineHost Host => new(Directory);
        public void WriteProviders(string text)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Providers, text);
        }
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
