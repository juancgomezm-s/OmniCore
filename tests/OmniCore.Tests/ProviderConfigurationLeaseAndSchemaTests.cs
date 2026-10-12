namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Host;

public sealed class ProviderConfigurationLeaseAndSchemaTests
{
    [Fact]
    public void Shared_lease_excludes_each_provider_or_model_yaml_writer()
    {
        using var fixture = new Fixture();
        using (ProviderConfigurationLease.Acquire(fixture.Directory))
        {
            Assert.Throws<IOException>(() => ModelCatalogRegistration.RegisterChatGpt(
                fixture.Directory, "fixture-model", 4096, 1024));
            Assert.Throws<IOException>(() => ProviderConnectionRegistration.EnsureAnthropicProvider(fixture.Directory));
            Assert.Throws<IOException>(() => ProviderConnectionRegistration.RegisterAnthropicModels(
                fixture.Directory, "fixture-provider", []));
        }

        Assert.False(File.Exists(Path.Combine(fixture.Directory, "providers.yaml.write.lock")));
    }

    [Fact]
    public void Providers_schema_describes_local_host_fields_and_auto_endpoint()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(FindProvidersSchema()));
        var provider = schema.RootElement.GetProperty("properties").GetProperty("providers")
            .GetProperty("additionalProperties");
        var properties = provider.GetProperty("properties");

        Assert.Equal(new[] { "attach", "managed" }, properties.GetProperty("host").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Contains(properties.GetProperty("baseUrl").GetProperty("oneOf").EnumerateArray(), alternative =>
            alternative.TryGetProperty("const", out var value) && value.GetString() == "auto");
        Assert.Equal("object", properties.GetProperty("managed").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("managed").GetProperty("properties").GetProperty("args")
            .GetProperty("items").GetProperty("type").GetString());
        Assert.NotEmpty(provider.GetProperty("allOf").EnumerateArray());
    }

    [Theory]
    [InlineData("attach", "http://127.0.0.1:8080", false)]
    [InlineData("managed", "auto", true)]
    [InlineData("managed", "http://127.0.0.1:9234", true)]
    public void Config_loader_accepts_each_schema_supported_local_engine_shape(
        string mode, string endpoint, bool managed)
    {
        var managedSection = managed
            ? "    managed:\n      executable: server.exe\n      args: [--port, '{port}']\n      workingDirectory: C:/models\n      readinessTimeoutSeconds: 180\n"
            : "";
        var yaml = $"providers:\n  local:\n    family: OpenAiChatCompatible\n    baseUrl: {endpoint}\n    auth: none\n    billingMode: Local\n    host: {mode}\n" + managedSection;

        var loaded = new ConfigLoader().Load(yaml, "models: {}\n");

        Assert.NotNull(loaded.Registry.Provider("local"));
        Assert.Equal(managed ? "managed" : "attach", loaded.Providers!.Providers!["local"].Host);
    }

    private static string FindProvidersSchema()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "src", "OmniCore.Host", "Schemas", "providers.schema.json");
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate providers.schema.json from the test process.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(),
            "omni-provider-lease-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
