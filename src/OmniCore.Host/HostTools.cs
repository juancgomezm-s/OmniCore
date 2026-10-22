namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Extensions;
using OmniCore.Sandbox;
using OmniCore.Tools;

/// <summary>
/// Composición de tools para el runtime de M2: FakeTools de simulación + las tools Core reales
/// (filesystem.read, reference.resolve, plan.propose) sobre la frontera de paths y el PlanService.
/// El catálogo alimenta el pipeline de tools del simulation engine. Las tools que mutan el
/// workspace (filesystem.patch) solo entran si se piden: Explorer es de solo lectura y su
/// catálogo nunca las expone (arquitectura §24 M2, ADR-0044).
/// </summary>
public sealed class HostTools
{
    private readonly FakeCatalog _catalog;

    private readonly PlanProposeTool _planPropose;
    private readonly WorktreeIntegrationHostCoordinator? _extensionBatchCoordinator;

    public HostTools(IPathBoundaryValidator boundary, PlanService planService, bool includeSimulationTools = true,
        bool includeMutationTools = false, bool includeProcessTools = false,
        SandboxStrength processSandboxStrength = SandboxStrength.Strong, ArtifactReadTool? artifactReadTool = null,
        bool includeControlTools = true, bool includeMailboxReceive = false, bool includeShellTool = true,
        IWorktreeIntegrationCoordinator? worktreeIntegrationCoordinator = null,
        MediaReadTool? mediaReadTool = null)
        : this(boundary, planService, includeSimulationTools, includeMutationTools, includeProcessTools,
            processSandboxStrength, artifactReadTool, includeControlTools, includeMailboxReceive, includeShellTool,
            processRuntime: null, worktreeIntegrationCoordinator, mediaReadTool)
    {
    }

    private HostTools(IPathBoundaryValidator boundary, PlanService planService, bool includeSimulationTools,
        bool includeMutationTools, bool includeProcessTools, SandboxStrength processSandboxStrength,
        ArtifactReadTool? artifactReadTool, bool includeControlTools, bool includeMailboxReceive,
        bool includeShellTool, OmniCore.Abstractions.IProcessRuntime? processRuntime,
        IWorktreeIntegrationCoordinator? worktreeIntegrationCoordinator = null,
        MediaReadTool? mediaReadTool = null)
    {
        _planPropose = new PlanProposeTool(planService);
        _extensionBatchCoordinator = worktreeIntegrationCoordinator as WorktreeIntegrationHostCoordinator;
        var catalog = includeSimulationTools ? FakeCatalog.Default() : new FakeCatalog();
        if (includeControlTools) catalog = catalog.Add(new UserAskTool());
        catalog = catalog.Add(new ReadFileTool(boundary))
            .Add(new ListDirectoryTool(boundary))
            .Add(new SearchTextTool(boundary));
        if (includeMutationTools)
        {
            catalog = catalog.Add(new FilesystemPatchTool(boundary));
            // EPIC-021 (ADR-0044 §5): escritura completa create-only/reemplazo según el modo de
            // mutación; la frontera de capacidad decide la exposición por categoría.
            catalog = catalog.Add(new FilesystemWriteTool(boundary));
            if (worktreeIntegrationCoordinator is not null)
            {
                catalog = catalog.Add(new WorktreeIntegrationApplyTool(boundary, worktreeIntegrationCoordinator));
                if (worktreeIntegrationCoordinator is IExtensionBatchPublicationCoordinator extensionBatchCoordinator)
                    catalog = catalog.Add(new ExtensionBatchApplyTool(extensionBatchCoordinator));
            }        }
        if (includeProcessTools)
        {
            var processLauncher = OmniHost.CreateProcessSandboxLauncher(processRuntime ?? SystemProcessRuntime.Instance());
            var gitActivity = new OmniCore.Infrastructure.GitWorktreeStore(SystemProcessRuntime.Instance());
            catalog = catalog.Add(new ProcessExecTool(processLauncher, boundary, processSandboxStrength, gitActivity));
            if (includeShellTool) catalog = catalog.Add(new ShellExecTool(processLauncher, boundary, processSandboxStrength, gitActivity));
        }

        catalog = catalog.Add(new ReferenceResolveTool(boundary));
        if (artifactReadTool is not null) catalog = catalog.Add(artifactReadTool);
        if (mediaReadTool is not null) catalog = catalog.Add(mediaReadTool);
        if (includeMailboxReceive) catalog = catalog.Add(new AgentMailboxReceiveTool());
        if (includeControlTools) catalog = catalog.Add(new ToolSearchTool(catalog));
        _catalog = includeControlTools ? catalog.Add(_planPropose).Add(new ModeProposeTool()) : catalog;
    }

    public static HostTools Default()
    {
        var boundary = new PathBoundaryValidator();
        var planService = new PlanService();
        return new HostTools(boundary, planService, includeSimulationTools: true, includeMutationTools: true);
    }

    public static HostTools DelegatedReader(ArtifactReadTool? artifactReadTool = null) =>
        new(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            artifactReadTool: artifactReadTool, includeControlTools: false);

    public static HostTools DelegatedReaderWithMailbox(ArtifactReadTool? artifactReadTool = null) =>
        new(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            artifactReadTool: artifactReadTool, includeControlTools: false, includeMailboxReceive: true);

    /// <summary>Child workspace tools are composed from its durable profile; Security still intersects
    /// each request with the parent mode, grants, project restrictions and that exact profile ceiling.</summary>
    public static HostTools DelegatedAgent(AgentProfile profile, ArtifactReadTool? artifactReadTool = null,
        SandboxStrength processSandboxStrength = SandboxStrength.Strong) =>
        new(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            includeMutationTools: profile.PermissionCeiling.Writes.Count > 0,
            includeProcessTools: profile.PermissionCeiling.Process.Count > 0 || profile.PermissionCeiling.AllowShell,
            processSandboxStrength: processSandboxStrength, artifactReadTool: artifactReadTool,
            includeControlTools: false, includeMailboxReceive: true);

    /// <summary>Workflow stage catalog: only tools whose concrete capabilities are declared by that stage.</summary>
    public static HostTools WorkflowStage(WorkflowStage stage, AgentProfile profile,
        ArtifactReadTool? artifactReadTool = null,
        SandboxStrength processSandboxStrength = SandboxStrength.Strong) =>
        WorkflowStageCore(stage, profile, artifactReadTool, processSandboxStrength, processRuntime: null);

    internal static HostTools WorkflowStageForTests(WorkflowStage stage, AgentProfile profile,
        ArtifactReadTool? artifactReadTool, SandboxStrength processSandboxStrength,
        OmniCore.Abstractions.IProcessRuntime processRuntime) =>
        WorkflowStageCore(stage, profile, artifactReadTool, processSandboxStrength,
            processRuntime ?? throw new ArgumentNullException(nameof(processRuntime)));

    private static HostTools WorkflowStageCore(WorkflowStage stage, AgentProfile profile,
        ArtifactReadTool? artifactReadTool, SandboxStrength processSandboxStrength,
        OmniCore.Abstractions.IProcessRuntime? processRuntime) =>
        new(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            includeMutationTools: stage == global::OmniCore.Host.WorkflowStage.Implement,
            includeProcessTools: stage == global::OmniCore.Host.WorkflowStage.Verify && profile.PermissionCeiling.Process.Count > 0,
            processSandboxStrength: processSandboxStrength, artifactReadTool: artifactReadTool,
            includeControlTools: false, includeMailboxReceive: true, includeShellTool: false,
            processRuntime: processRuntime);

    public static HostTools Explorer(ArtifactReadTool? artifactReadTool = null, MediaReadTool? mediaReadTool = null) =>
        new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            includeMutationTools: false, includeProcessTools: false, artifactReadTool: artifactReadTool,
            mediaReadTool: mediaReadTool);

    public FakeCatalog Catalog() => _catalog;

    internal void RegisterCompanionTool(CompanionPetTool tool) => _catalog.Add(tool);

    /// <summary>Registers a source-namespaced extension tool after consented Host composition.</summary>
    internal void RegisterExtensionTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool is not ExtensionTool || tool.Descriptor.Source.Kind != SourceKind.Extension
            || tool.Descriptor.Source.Trust is TrustLevel.Untrusted or TrustLevel.ThirdParty
            || tool.Descriptor.Source.Scope != ScopeLevel.Project)
            throw new InvalidOperationException("Only a trusted Project ExtensionTool using the Strong Host bridge may enter the session catalog.");
        _catalog.Add(tool);
        if (_extensionBatchCoordinator is not null && tool is ExtensionTool extensionTool)
            _extensionBatchCoordinator.RegisterExtensionProposalValidator(extensionTool.Descriptor.Id.ToString(),
                extensionTool.ValidateCurrentBatchProposal);
    }

    internal void RegisterMcpTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool is not (McpTool or McpWebTool) || tool.Descriptor.Source.Kind != SourceKind.Mcp
            || tool.Descriptor.Source.Trust is not (TrustLevel.Trusted or TrustLevel.Project))
            throw new InvalidOperationException("Only configured trusted MCP sources may enter a session catalog.");
        _catalog.Add(tool);
    }

    internal void RegisterSkillLoadTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool is not SkillLoadTool || tool.Descriptor.Id.ToString() != "skill.load"
            || tool.Descriptor.Source.Kind != SourceKind.BuiltIn
            || tool.Descriptor.EffectClass != EffectClass.None)
            throw new InvalidOperationException("Only the Core, no-external-effect skill loader can enter a session catalog.");
        _catalog.Add(tool);
    }

    internal void RegisterMemoryProposeTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool is not MemoryProposeTool || tool.Descriptor.Id.ToString() != "memory.propose"
            || tool.Descriptor.Source.Kind != SourceKind.BuiltIn || tool.Descriptor.EffectClass != EffectClass.None)
            throw new InvalidOperationException("Only the Core, no-external-effect Session memory proposal tool may enter the principal catalog.");
        _catalog.Add(tool);
    }

    public PlanProposeTool PlanPropose() => _planPropose;
}
