using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class WorkflowToolFactoryTests
{
    [Fact]
    public void Workflow_dynamic_tool_uses_dynamic_owner_namespace_and_normal_authorization_journal()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-dynamic-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "probe.txt"), "tool-backed content");
        try
        {
            var workflow = new CompiledWorkflowDescriptor(new WorkflowRef("core:explore-implement-verify", "1"),
                ComponentSource.Core());
            var catalog = new FakeCatalog();
            var dynamicTool = WorkflowToolFactory.Register(catalog, workflow, "probe_read",
                "Read a workflow-declared evidence target.", ModelToolCapability.WorkspaceRead,
                new ReadFileTool(new PathBoundaryValidator()));

            Assert.Equal("dyn.core.probe_read", dynamicTool.Descriptor.Id.ToString());
            Assert.Equal(SourceKind.Dynamic, dynamicTool.Descriptor.Source.Kind);
            Assert.Equal("core", dynamicTool.Descriptor.Source.Owner);

            var effective = EffectiveObservePolicy();
            var boundary = OmniCliRuntime.CreateBoundary(effective, root, catalog);
            Assert.True(boundary.IsToolVisible("dyn.core.probe_read"));
            Assert.False(boundary.IsToolVisible("dyn.core.not-registered"), "No se permite ningún prefijo dyn.* genérico.");
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                ScriptedPermissionPolicy.WithTool("dyn.core.probe_read", PermissionDecision.Allow), root, boundary);
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var callId = ToolCallId.New();
            var outcome = executor.ExecuteTool(new ValidatedToolCall(callId, dynamicTool.Descriptor.Id,
                "dynamic-probe", "{\"path\":\"probe.txt\"}"), false, CancellationToken.None, stream);
            Assert.True(outcome.Succeeded);
            stream.AppendBatch(outcome.Events, DurabilityClass.Standard);

            var events = store.ReadFrom(session, 1);
            Assert.Contains(events, evt => codecs.Decode(evt) is ToolCallRequested requested
                && requested.ToolCallId == callId && requested.ToolName == "dyn.core.probe_read");
            Assert.Contains(events, evt => codecs.Decode(evt) is PermissionEvaluated permission
                && permission.ToolCallId == callId && permission.Decision == PermissionDecision.Allow);
            Assert.True(events.Any(evt => codecs.Decode(evt) is ToolCallSucceeded succeeded
                && succeeded.ToolCallId == callId), string.Join(",", events.Select(evt => codecs.Decode(evt).GetType().Name)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Dynamic_tool_rejects_mismatched_workflow_owner_and_id_collision()
    {
        var catalog = new FakeCatalog();
        var workflow = new CompiledWorkflowDescriptor(new WorkflowRef("core:workflow", "1"), ComponentSource.Core());
        WorkflowToolFactory.Register(catalog, workflow, "probe", "probe", ModelToolCapability.WorkspaceRead,
            new ReadFileTool(new PathBoundaryValidator()));
        Assert.Throws<ToolRegistrationRejected>(() => WorkflowToolFactory.Register(catalog, workflow,
            "probe", "second", ModelToolCapability.WorkspaceRead, new ReadFileTool(new PathBoundaryValidator())));

        var untrusted = workflow with { Source = new ComponentSource(SourceKind.Extension, ScopeLevel.Run,
            TrustLevel.ThirdParty, "core", "1") };
        Assert.Throws<InvalidOperationException>(() => WorkflowToolFactory.Register(new FakeCatalog(), untrusted,
            "probe", "probe", ModelToolCapability.WorkspaceRead, new ReadFileTool(new PathBoundaryValidator())));
    }

    [Fact]
    public void Dynamic_tool_capability_is_exact_and_still_intersected_with_the_effective_ceiling()
    {
        var catalog = new FakeCatalog();
        var workflow = new CompiledWorkflowDescriptor(new WorkflowRef("core:explore-implement-verify", "1"),
            ComponentSource.Core());
        WorkflowToolFactory.Register(catalog, workflow, "probe_process", "run a workflow check",
            ModelToolCapability.GeneralProcess, new ReadFileTool(new PathBoundaryValidator()));
        var boundary = OmniCliRuntime.CreateBoundary(EffectiveObservePolicy(), Path.GetTempPath(), catalog);
        Assert.False(boundary.IsToolVisible("dyn.core.probe_process"));
        Assert.False(boundary.IsToolVisible("dyn.other.probe_process"));
        Assert.Contains("dyn.core.probe_process", WorkflowToolFactory.Capabilities(catalog).Keys);
    }

    private static EffectiveModelPolicy EffectiveObservePolicy()
    {
        var key = ModelPolicyKey.For("fixture", "fixture");
        return EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, ModelPolicyPresets.ObserveOnly(), DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 16, GuidanceLevel.Full, 2,
                PlanControl.ModelDriven, 6));
    }
}
