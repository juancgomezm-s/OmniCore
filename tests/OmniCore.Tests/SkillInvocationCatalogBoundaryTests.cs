namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Infrastructure;

public sealed class SkillInvocationCatalogBoundaryTests
{
    [Fact]
    public void Fresh_occupied_names_use_current_extension_consent_and_aliases_before_any_command_query()
    {
        using var fixture = new Fixture();
        var extensionRoot = Path.Combine(fixture.Workspace, ".omnicore", "extensions");
        var package = Path.Combine(extensionRoot, "fixture.commands");
        Directory.CreateDirectory(Path.Combine(package, "schemas"));
        File.WriteAllText(Path.Combine(package, "schemas", "inspect.json"), "{\"type\":\"object\"}");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.commands
            version: 1.0.0
            displayName: Fixture commands
            publisher: fixture
            omnicoreApi: '>=1.0 <2.0'
            provides:
              tools:
                - name: inspect
                  schema: schemas/inspect.json
              commands:
                - name: review-fixture
                  kind: Extension
                  tool: inspect
            requests:
              capabilities: [tool.register, command.register]
              permissions:
                process:
                  - executable: fixture-never-executed.exe
            runtime:
              kind: process
              command: fixture-never-executed.exe
              args: [--omnicore-extension]
            source:
              kind: local
              location: .
              sha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            """);
        var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(),
            platformPathsFactory: () => fixture.Paths);
        server.ConfigureWorkspaceRoot(fixture.Workspace);
        new WorkspaceTrustStore(fixture.Paths).SetTrusted(fixture.Workspace, true);
        var extension = Assert.Single(new ExtensionManifestLoader().Discover(extensionRoot,
            TrustLevel.Project, workspaceTrusted: true));
        Assert.Equal(ExtensionDiscoveryStatus.ConsentRequired, extension.Status);
        var consent = new ExtensionConsentStore(fixture.Paths, fixture.Workspace);
        Assert.DoesNotContain("review-fixture", server.SkillInvocationOccupiedNames());
        consent.Grant(extension, new HashSet<string>(["tool.register", "command.register"], StringComparer.Ordinal));
        Assert.Contains("review-fixture", server.SkillInvocationOccupiedNames());
        consent.Revoke("fixture.commands");
        Assert.DoesNotContain("review-fixture", server.SkillInvocationOccupiedNames());
        var preferences = new PolicyPreferencesHost(fixture.Paths, fixture.Workspace);
        var saved = preferences.SaveCommandAliases(preferences.Read().Revision,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["my-help"] = "help" });
        Assert.True(saved.Available, string.Join("; ", saved.Diagnostics));
        Assert.Contains("my-help", server.SkillInvocationOccupiedNames());
        preferences.SaveCommandAliases(saved.Revision, new Dictionary<string, string>());
        Assert.DoesNotContain("my-help", server.SkillInvocationOccupiedNames());
        Assert.Null(server.LastSessionId());
    }

    [Fact]
    public void An_invocation_alias_cannot_replace_an_existing_active_skill_workflow_name()
    {
        using var fixture = new Fixture();
        const string workflowName = "skill-workflow-fixture-implement";
        var skillRoot = Path.Combine(fixture.Workspace, ".omnicore", "skills");
        var workflowPackage = Path.Combine(skillRoot, "workflow.fixture");
        Directory.CreateDirectory(Path.Combine(workflowPackage, "workflows"));
        File.WriteAllText(Path.Combine(workflowPackage, "skill.yaml"), """
            id: workflow.fixture
            version: 1
            contributions:
              workflows: [workflows/implement.json]
            """);
        File.WriteAllText(Path.Combine(workflowPackage, "instructions.md"), "Bounded fixture workflow instructions");
        File.WriteAllText(Path.Combine(workflowPackage, "workflows", "implement.json"), """
            {"id":"implement","workflow":"core:explore-implement-verify","descriptions":{
              "es":"Implementar mediante el flujo compilado.","en":"Implement through the compiled workflow.",
              "fr":"Implémenter via le flux compilé.","it":"Implementare con il flusso compilato."}}
            """);
        var otherPackage = Path.Combine(skillRoot, workflowName);
        Directory.CreateDirectory(otherPackage);
        File.WriteAllText(Path.Combine(otherPackage, "skill.yaml"), "id: " + workflowName
            + "\nversion: 1\nactivation:\n  explicit: true\n");
        File.WriteAllText(Path.Combine(otherPackage, "instructions.md"), "Unrelated fixture instructions");
        var loader = new SkillManifestLoader();
        var discovered = loader.Discover(skillRoot, ScopeLevel.Project, TrustLevel.Project, workspaceTrusted: true);
        var workflowSkill = loader.Activate(discovered.Single(skill => skill.Manifest.Id == "workflow.fixture"));
        var session = SessionId.New();
        new SessionSkillActivationStore(fixture.RuntimeData).Activate(session, ScopeLevel.Project, "workflow.fixture",
            workflowSkill.Skill.ManifestSha256, workflowSkill.PackageContentSha256!);
        var registry = new CommandRegistry();
        SessionSkillWorkflowCommands.Refresh(registry, session, fixture.Workspace, fixture.Paths.DataDirectory,
            fixture.RuntimeData, true, allCandidates: discovered);
        Assert.Equal(CommandKind.Workflow, registry.Resolve(workflowName).Descriptor.Kind);
        Assert.Equal(CommandKind.Prompt, registry.Resolve("skill-" + workflowName).Descriptor.Kind);
        Assert.DoesNotContain(workflowName, registry.Resolve("skill-" + workflowName).Descriptor.Aliases);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-skill-command-boundary-" + Guid.NewGuid().ToString("N"));
        public string Workspace { get; }
        public string RuntimeData => Path.Combine(_root, "runtime-data");
        public DefaultPlatformPaths Paths { get; }
        public Fixture()
        {
            Workspace = Path.Combine(_root, "workspace");
            Paths = new DefaultPlatformPaths(Path.Combine(_root, "data"), Path.Combine(_root, "config"));
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Paths.ConfigDirectory);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
