using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class ProviderBootstrapTests
{
    [Fact]
    public void Fresh_configuration_materializes_examples_without_disabling_the_unconfigured_local_model()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omni-provider-bootstrap-" + Guid.NewGuid().ToString("N"));
        try
        {
            OmniHost.InitializeUserConfigurationExamples(directory);
            var loaded = OmniHost.LoadUserConfiguration(directory);
            Assert.True(File.Exists(Path.Combine(directory, "providers.example.yaml")));
            Assert.False(File.Exists(Path.Combine(directory, "providers.yaml")));
            Assert.Equal("http://127.0.0.1:8080", loaded.Registry.Provider("local")!.BaseUrl);
            Assert.Equal("local-worker", loaded.Registry.ResolveDefault().Id);
            Assert.Null(loaded.Registry.Provider("openrouter"));
            Assert.Null(loaded.Registry.Provider("anthropic"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Loading_configuration_does_not_create_files_or_change_onboarding_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omni-provider-bootstrap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("local-worker", OmniHost.LoadUserConfiguration(directory).Registry.ResolveDefault().Id);
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Existing_provider_configuration_is_preserved_byte_for_byte()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omni-provider-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "providers.yaml");
        const string content = "# user's connection\r\nproviders:\r\n  custom:\r\n    baseUrl: http://127.0.0.1:9123/v1\r\n";
        try
        {
            File.WriteAllText(path, content);
            var loaded = OmniHost.LoadUserConfiguration(directory);
            Assert.NotNull(loaded.Registry.Provider("custom"));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
