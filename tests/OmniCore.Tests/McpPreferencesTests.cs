using System.Text.Json;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class McpPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-mcp-preferences-" + Guid.NewGuid().ToString("N"));
    private readonly DefaultPlatformPaths _paths;
    private readonly McpPreferencesHost _host;
    private string FilePath => Path.Combine(_paths.ConfigDirectory, "mcp", "servers.json");
    public McpPreferencesTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new(Path.Combine(_root, "data"));
        _host = new(_paths, _root, new FileCredentialStore(Path.Combine(_root, "test-credentials")));
    }

    private McpConfigurationDraft Http(string id = "remote") => new(id, "http", "", [], "", "https://fixture.invalid/mcp");

    [Fact]
    public void Saved_configuration_feeds_the_runtime_catalog_only_after_explicit_remote_authorization()
    {
        var saved = _host.Save(_host.Read().Revision, null, Http());
        var transport = new CatalogTransport();
        var runtimeStore = new McpServerStore(_paths, _root, false);
        Assert.Empty(runtimeStore.DiscoverTools(transport, transport, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Calls);
        _host.SetTrust(saved.Revision, "remote", true);
        var tools = runtimeStore.DiscoverTools(transport, transport, TestContext.Current.CancellationToken);
        Assert.Equal("mcp.remote.lookup", Assert.Single(tools).Descriptor.Id.ToString());
        Assert.Equal(2, transport.Calls);
    }

    private sealed class CatalogTransport : IMcpRequestTransport
    {
        public int Calls;
        public Task<string> SendAsync(McpServerDefinition server, string method, string parametersJson, CancellationToken token)
        {
            Calls++;
            Assert.Equal("https://fixture.invalid/mcp", server.Endpoint);
            return Task.FromResult(method == "server/discover"
                ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"supportedVersions\":[\"2026-07-28\"]}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[{\"name\":\"lookup\",\"inputSchema\":{\"type\":\"object\"}}]}}");
        }
    }

    [Fact]
    public void Add_edit_authorize_revoke_remove_and_reopen_are_durable_without_network_or_execution()
    {
        var initial = _host.Read();
        Assert.Empty(initial.Servers);
        Assert.False(File.Exists(FilePath));
        var saved = _host.Save(initial.Revision, null, Http());
        Assert.False(Assert.Single(saved.Servers).Trusted);
        var trusted = _host.SetTrust(saved.Revision, "remote", true);
        Assert.True(Assert.Single(trusted.Servers).Trusted);
        var changed = _host.Save(trusted.Revision, "remote", Http() with { Endpoint = "https://other.invalid/api/mcp" });
        Assert.False(Assert.Single(changed.Servers).Trusted);
        var reopened = new McpPreferencesHost(_paths, _root).Read();
        Assert.Equal("https://other.invalid/api/mcp", Assert.Single(reopened.Servers).Endpoint);
        using (var json = JsonDocument.Parse(File.ReadAllBytes(FilePath)))
            Assert.Equal("other.invalid", json.RootElement[0].GetProperty("allowedHosts")[0].GetString());
        Assert.Empty(_host.Remove(reopened.Revision, "remote").Servers);
        // Removing and re-adding the exact same definition cannot restore an old trust receipt.
        var again = _host.Save(_host.Read().Revision, null, Http());
        Assert.False(Assert.Single(again.Servers).Trusted);
    }

    [Fact]
    public void Stale_forms_cannot_overwrite_or_remove_or_trust_a_concurrent_edit()
    {
        var first = _host.Save(_host.Read().Revision, null, Http());
        var second = _host.Save(first.Revision, null, Http("other"));
        var before = File.ReadAllBytes(FilePath);
        foreach (var operation in new Action[] {
            () => _host.Save(first.Revision, "remote", Http()),
            () => _host.Remove(first.Revision, "remote"),
            () => _host.SetTrust(first.Revision, "remote", true),
            () => _host.SetCredential(first.Revision, "remote", "fixture-secret") })
            Assert.Equal("mcpPreferences.stale", Assert.Throws<InvalidOperationException>(operation).Message);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal(2, second.Servers.Count);
        var authorized = _host.SetTrust(second.Revision, "remote", true);
        Assert.NotEqual(second.Revision, authorized.Revision);
        Assert.Throws<InvalidOperationException>(() => _host.Save(second.Revision, "remote", Http()));
    }

    [Fact]
    public void Stdio_preserves_literal_arguments_working_directory_and_advanced_fields()
    {
        var command = Path.Combine(_root, "not-launched.exe");
        string[] args = ["--directory", "a path with spaces", "", "$(literal)", "\\network"];
        var draft = new McpConfigurationDraft("local", "stdio", command, args, _root, "");
        var saved = _host.Save(_host.Read().Revision, null, draft);
        Assert.Equal(args, Assert.Single(saved.Servers).Arguments);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath))!;
        node[0]!["requestTimeoutSeconds"] = 77;
        File.WriteAllText(FilePath, node.ToJsonString());
        saved = _host.Save(_host.Read().Revision, "local", draft with { Arguments = ["changed"] });
        Assert.Equal(_root, Assert.Single(saved.Servers).WorkingDirectory);
        Assert.Contains("77", File.ReadAllText(FilePath));
        Assert.False(File.Exists(command)); // No executable is invoked during maintenance.
    }

    [Theory]
    [InlineData("http://external.invalid/mcp")]
    [InlineData("https://fixture.invalid/mcp?token=secret")]
    [InlineData("https://name:secret@fixture.invalid/mcp")]
    [InlineData("file:///fixture")]
    public void Invalid_remote_configuration_never_replaces_the_original_file(string endpoint)
    {
        var saved = _host.Save(_host.Read().Revision, null, Http());
        var bytes = File.ReadAllBytes(FilePath);
        Assert.Throws<InvalidDataException>(() => _host.Save(saved.Revision, "remote", Http() with { Endpoint = endpoint }));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void Credentials_are_not_written_to_configuration_and_project_servers_are_not_edited()
    {
        var saved = _host.Save(_host.Read().Revision, null, Http());
        saved = _host.SetCredential(saved.Revision, "remote", "fixture-mcp-secret");
        Assert.True(Assert.Single(saved.Servers).HasCredential);
        Assert.DoesNotContain("fixture-mcp-secret", File.ReadAllText(FilePath));
        Directory.CreateDirectory(Path.Combine(_root, ".omnicore"));
        var project = Path.Combine(_root, ".omnicore", "mcp.servers.json");
        const string text = "[{\"id\":\"project\",\"transport\":\"http\",\"endpoint\":\"https://project.invalid/mcp\",\"allowedHosts\":[\"project.invalid\"]}]";
        File.WriteAllText(project, text);
        saved = _host.Read();
        Assert.Throws<InvalidOperationException>(() => _host.Remove(saved.Revision, "project"));
        Assert.Throws<InvalidOperationException>(() => _host.Save(saved.Revision, "project", Http("project")));
        Assert.Equal(text, File.ReadAllText(project));
    }

    [Fact]
    public void Editing_one_server_preserves_another_servers_exact_identity_and_consent()
    {
        _host.Save(_host.Read().Revision, null, Http());
        const string other = "{\n  \"id\": \"other\", \"transport\": \"http\",\n  \"endpoint\": \"https://other.invalid/mcp\", \"allowedHosts\": [\"other.invalid\"]\n}";
        File.WriteAllText(FilePath, "[" + File.ReadAllText(FilePath)[1..^1] + "," + other + "]");
        var authorized = _host.SetTrust(_host.Read().Revision, "other", true);
        var identity = authorized.Servers.Single(server => server.Id == "other").Identity;
        var changed = _host.Save(authorized.Revision, "remote", Http() with { Endpoint = "https://changed.invalid/mcp" });
        var untouched = changed.Servers.Single(server => server.Id == "other");
        Assert.True(untouched.Trusted);
        Assert.Equal(identity, untouched.Identity);
        using var json = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        Assert.Equal(other, json.RootElement[1].GetRawText());
    }

    public void Dispose() { Directory.Delete(_root, recursive: true); }
}
