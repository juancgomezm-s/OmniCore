namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed class SkillWorkflowCommandTests
{
    [Fact]
    public async System.Threading.Tasks.Task Activated_skill_workflow_resolves_to_compiled_taskgraph_request_and_release_removes_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-workflow-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var userData = Path.Combine(root, "user-data");
        var workspaceData = Path.Combine(root, "workspace-data");
        var package = Path.Combine(workspace, ".omnicore", "skills", "workflow.fixture");
        Directory.CreateDirectory(Path.Combine(package, "workflows"));
        var session = SessionId.New();
        try
        {
            File.WriteAllText(Path.Combine(package, "skill.yaml"), """
                id: workflow.fixture
                version: 1
                displayName: Workflow fixture
                budgetTokens: 256
                contributions:
                  workflows: [workflows/implement.json]
                """);
            File.WriteAllText(Path.Combine(package, "instructions.md"), "Instructions remain package data.");
            File.WriteAllText(Path.Combine(package, "workflows", "implement.json"), """
                {"id":"implement","workflow":"core:explore-implement-verify","descriptions":{
                  "es":"Implementar mediante el flujo compilado.",
                  "en":"Implement through the compiled workflow.",
                  "fr":"Implémenter via le flux compilé.",
                  "it":"Implementare con il flusso compilato."}}
                """);

            var sources = new SkillSourceCatalog();
            var discovered = Assert.Single(sources.Discover(workspace, userData, workspaceData, session,
                workspaceTrusted: true), skill => skill.Manifest.Id == "workflow.fixture");
            var loaded = new SkillManifestLoader().Activate(discovered);
            var store = new SessionSkillActivationStore(workspaceData);
            store.Activate(session, ScopeLevel.Project, loaded.Skill.Manifest.Id!, loaded.Skill.ManifestSha256,
                loaded.PackageContentSha256!);
            var registry = new CommandRegistry();
            SessionSkillWorkflowCommands.Refresh(registry, session, workspace, userData, workspaceData,
                workspaceTrusted: true);
            var name = "skill-workflow-fixture-implement";
            var handler = registry.Resolve(name);
            Assert.Equal(CommandKind.Workflow, handler.Descriptor.Kind);
            Assert.Equal("Implémenter via le flux compilé.", handler.Descriptor.UiDescriptions!["fr"]);

            var service = new CommandService(registry);
            var context = new CommandContext(session, RunId.New(), RunMode.Orchestrate, workspace,
                WorkflowAuthorized: true);
            var arguments = new[] { "implement this module", "--verify-executable", "fixture.exe",
                "--verify-argv-json", "[\"--offline-check\"]" };
            var outcome = Assert.IsType<WorkflowRequested>(await service.InvokeAsync(
                new CommandInvocation(name, arguments, "Typed"), context, CancellationToken.None));
            Assert.Equal("core:explore-implement-verify", outcome.Workflow.Id);
            Assert.Equal("1", outcome.Workflow.Version);
            Assert.Equal(arguments, outcome.Arguments);

            store.Release(session, ScopeLevel.Project, "workflow.fixture");
            SessionSkillWorkflowCommands.Refresh(registry, session, workspace, userData, workspaceData,
                workspaceTrusted: true);
            Assert.Throws<InvalidOperationException>(() => registry.Resolve(name));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
