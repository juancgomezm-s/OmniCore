namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Domain;

public sealed class SessionPreferencesHostTests
{
    [Fact]
    public void Session_memory_and_skill_actions_reject_stale_session_and_manifest_from_ui_snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-session-preferences-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        var skillPath = Path.Combine(workspace, ".omnicore", "skills", "fixture.skill");
        Directory.CreateDirectory(skillPath);
        File.WriteAllText(Path.Combine(skillPath, "skill.yaml"), "id: fixture.skill\nversion: 1.0.0\ndisplayName: Fixture skill\n");
        File.WriteAllText(Path.Combine(skillPath, "instructions.md"), "bounded fixture instructions");
        var server = OmniHost.CreateInMemoryServer();
        var paths = OmniHost.CreatePlatformPaths(data);
        server.ConfigurePlatformPaths(() => paths);
        server.ConfigureWorkspaceRoot(workspace);
        new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
        try
        {
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"start fixture\"}"),
                CancellationToken.None).Status);
            using var sessionQuery = JsonDocument.Parse(server.Query("memory.session", CancellationToken.None)!.Json);
            var sessionId = sessionQuery.RootElement.GetProperty("session").GetString()!;
            using var skillQuery = JsonDocument.Parse(server.Query("skills", CancellationToken.None)!.Json);
            var skill = Assert.Single(skillQuery.RootElement.GetProperty("skills").EnumerateArray(),
                row => row.GetProperty("id").GetString() == "fixture.skill" && row.GetProperty("scope").GetString() == "Project");
            var manifestHash = skill.GetProperty("manifestSha256").GetString()!;

            var staleMemory = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"memory.session.add\","
                + "\"content\":\"must not be stored\",\"expectedSessionId\":\"stale-session\"}"), CancellationToken.None);
            Assert.Equal("error", staleMemory.Status);
            Assert.Equal("sessionPreferences.error.session_changed", staleMemory.Error);
            Assert.Empty(JsonDocument.Parse(server.Query("memory.session", CancellationToken.None)!.Json)
                .RootElement.GetProperty("records").EnumerateArray());

            File.WriteAllText(Path.Combine(skillPath, "skill.yaml"),
                "id: fixture.skill\nversion: 1.0.1\ndisplayName: Fixture skill\n");
            var staleSkill = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"skills.activate\","
                + "\"skillId\":\"fixture.skill\",\"expectedSessionId\":" + Quote(sessionId)
                + ",\"expectedManifestSha256\":" + Quote(manifestHash) + "}"), CancellationToken.None);
            Assert.Equal("error", staleSkill.Status);
            Assert.Equal("sessionPreferences.error.selection_changed", staleSkill.Error);

            var validMemory = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"memory.session.add\","
                + "\"content\":\"session-only note\",\"expectedSessionId\":" + Quote(sessionId) + "}"), CancellationToken.None);
            Assert.Equal("ok", validMemory.Status);
            using var after = JsonDocument.Parse(server.Query("memory.session", CancellationToken.None)!.Json);
            Assert.Equal("session-only note", Assert.Single(after.RootElement.GetProperty("records").EnumerateArray())
                .GetProperty("content").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Extension_consent_requires_trusted_action_and_exact_rediscovered_package_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-session-extension-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        var extensionPath = Path.Combine(workspace, ".omnicore", "extensions", "fixture.extension");
        Directory.CreateDirectory(extensionPath);
        var manifestPath = Path.Combine(extensionPath, ExtensionManifestLoader.ManifestFileName);
        File.WriteAllText(manifestPath, ExtensionManifest);
        File.WriteAllText(Path.Combine(extensionPath, "schema.json"), "{\"type\":\"object\"}");
        var server = OmniHost.CreateInMemoryServer();
        var paths = OmniHost.CreatePlatformPaths(data);
        server.ConfigurePlatformPaths(() => paths);
        server.ConfigureWorkspaceRoot(workspace);
        new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
        try
        {
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"start fixture\"}"),
                CancellationToken.None).Status);
            using var query = JsonDocument.Parse(server.Query("extensions", CancellationToken.None)!.Json);
            var sessionId = query.RootElement.GetProperty("session").GetString()!;
            var extension = Assert.Single(query.RootElement.GetProperty("extensions").EnumerateArray());
            Assert.Equal("fixture.extension", extension.GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Array, extension.GetProperty("requestedCapabilities").ValueKind);
            Assert.Equal(JsonValueKind.Array, extension.GetProperty("consentedCapabilities").ValueKind);

            var request = ConsentPayload(sessionId, extension, ["tool.register"]);
            var untrustedAction = server.Send(WireEnvelope.Command(Ids.NewV7(), request), CancellationToken.None);
            Assert.Equal("error", untrustedAction.Status);
            Assert.Equal("sessionPreferences.error.trusted_action_required", untrustedAction.Error);

            var stringGrant = request.Replace("\"grant\":true", "\"grant\":\"true\"", StringComparison.Ordinal);
            var invalidGrant = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), stringGrant), CancellationToken.None);
            Assert.Equal("error", invalidGrant.Status);
            Assert.Equal("sessionPreferences.error.invalid_request", invalidGrant.Error);
            var stringCapabilities = request.Replace("\"capabilities\":[\"tool.register\"]",
                "\"capabilities\":\"[\\\"tool.register\\\"]\"", StringComparison.Ordinal);
            var invalidCapabilities = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), stringCapabilities), CancellationToken.None);
            Assert.Equal("error", invalidCapabilities.Status);
            Assert.Equal("sessionPreferences.error.invalid_request", invalidCapabilities.Error);
            var staleSession = request.Replace(Quote(sessionId), Quote("stale-session"), StringComparison.Ordinal);
            var sessionChanged = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), staleSession), CancellationToken.None);
            Assert.Equal("error", sessionChanged.Status);
            Assert.Equal("sessionPreferences.error.session_changed", sessionChanged.Error);

            File.WriteAllText(Path.Combine(extensionPath, "payload.data"), "package changed after selection");
            var stale = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), request), CancellationToken.None);
            Assert.Equal("error", stale.Status);
            Assert.Equal("sessionPreferences.error.selection_changed", stale.Error);

            using var latest = JsonDocument.Parse(server.Query("extensions", CancellationToken.None)!.Json);
            var currentExtension = Assert.Single(latest.RootElement.GetProperty("extensions").EnumerateArray());
            var valid = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), ConsentPayload(sessionId, currentExtension,
                ["tool.register"])), CancellationToken.None);
            Assert.Equal("ok", valid.Status);
            using var after = JsonDocument.Parse(server.Query("extensions", CancellationToken.None)!.Json);
            var granted = Assert.Single(after.RootElement.GetProperty("extensions").EnumerateArray());
            Assert.Equal("Ready", granted.GetProperty("status").GetString());
            Assert.Equal("tool.register", Assert.Single(granted.GetProperty("consentedCapabilities").EnumerateArray()).GetString());

            var staleRevoke = ConsentPayload(sessionId, extension, [], grant: false);
            var revoke = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), staleRevoke), CancellationToken.None);
            Assert.Equal("error", revoke.Status);
            Assert.Equal("sessionPreferences.error.selection_changed", revoke.Error);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Duplicate_extension_ids_are_marked_ambiguous_and_never_receive_shared_consent()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-session-extension-duplicate-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        var first = Path.Combine(workspace, ".omnicore", "extensions", "first");
        var second = Path.Combine(workspace, ".omnicore", "extensions", "second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, ExtensionManifestLoader.ManifestFileName), ExtensionManifest);
        File.WriteAllText(Path.Combine(second, ExtensionManifestLoader.ManifestFileName), ExtensionManifest);
        File.WriteAllText(Path.Combine(first, "schema.json"), "{\"type\":\"object\"}");
        File.WriteAllText(Path.Combine(second, "schema.json"), "{\"type\":\"object\"}");
        var server = OmniHost.CreateInMemoryServer();
        var paths = OmniHost.CreatePlatformPaths(data);
        server.ConfigurePlatformPaths(() => paths); server.ConfigureWorkspaceRoot(workspace);
        new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
        try
        {
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"start fixture\"}"),
                CancellationToken.None).Status);
            using var query = JsonDocument.Parse(server.Query("extensions", CancellationToken.None)!.Json);
            var entries = query.RootElement.GetProperty("extensions").EnumerateArray().ToArray();
            Assert.Equal(2, entries.Length);
            Assert.All(entries, entry => Assert.True(entry.GetProperty("ambiguousId").GetBoolean()));
            Assert.All(entries, entry => Assert.Empty(entry.GetProperty("consentedCapabilities").EnumerateArray()));
            var sessionId = query.RootElement.GetProperty("session").GetString()!;
            var attempt = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), ConsentPayload(sessionId, entries[0], ["tool.register"])),
                CancellationToken.None);
            Assert.Equal("error", attempt.Status);
            Assert.Equal("sessionPreferences.error.selection_changed", attempt.Error);
            var consentStore = new ExtensionConsentStore(paths, workspace);
            var currentPackages = new ExtensionManifestLoader().Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true);
            Assert.All(currentPackages, package => Assert.Empty(consentStore.ConsentedCapabilities(package)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static string ConsentPayload(string sessionId, JsonElement extension, string[] capabilities, bool grant = true) =>
        "{\"cmd\":\"extensions.consent\",\"extensionId\":" + Quote(extension.GetProperty("id").GetString()!)
        + ",\"version\":" + Quote(extension.GetProperty("version").GetString()!)
        + ",\"manifestSha256\":" + Quote(extension.GetProperty("manifestSha256").GetString()!)
        + ",\"packageSha256\":" + Quote(extension.GetProperty("packageSha256").GetString()!)
        + ",\"consentHash\":" + Quote(extension.GetProperty("consentHash").GetString()!)
        + ",\"capabilities\":[" + string.Join(",", capabilities.Select(Quote)) + "]"
        + ",\"grant\":" + grant.ToString().ToLowerInvariant()
        + ",\"expectedSessionId\":" + Quote(sessionId) + "}";

    private static string Quote(string value) => "\"" + JsonObj.Escape(value) + "\"";

    private const string ExtensionManifest = """
        manifestVersion: 1
        id: fixture.extension
        version: 1.0.0
        displayName: Fixture extension
        publisher: tests
        omnicoreApi: '>=1.0 <2.0'
        provides:
          tools:
            - name: fixture
              schema: schema.json
        requests:
          capabilities: [tool.register]
          permissions:
            process:
              - executable: fixture.exe
        runtime:
          kind: process
          command: fixture.exe
          args: []
        source:
          kind: local
          location: .
          sha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
        """;
}
