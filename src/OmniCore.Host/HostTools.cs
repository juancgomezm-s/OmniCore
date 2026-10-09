namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Execution;
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

    public HostTools(IPathBoundaryValidator boundary, PlanService planService, bool includeSimulationTools = true,
        bool includeMutationTools = false, bool includeProcessTools = false,
        SandboxStrength processSandboxStrength = SandboxStrength.Strong, ArtifactReadTool? artifactReadTool = null,
        bool includeControlTools = true, bool includeMailboxReceive = false)
    {
        _planPropose = new PlanProposeTool(planService);
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
        }
        if (includeProcessTools)
        {
            var processLauncher = OmniHost.CreateProcessSandboxLauncher(SystemProcessRuntime.Instance());
            catalog = catalog.Add(new ProcessExecTool(processLauncher, boundary, processSandboxStrength))
                .Add(new ShellExecTool(processLauncher, boundary, processSandboxStrength));
        }

        catalog = catalog.Add(new ReferenceResolveTool(boundary));
        if (artifactReadTool is not null) catalog = catalog.Add(artifactReadTool);
        if (includeMailboxReceive) catalog = catalog.Add(new AgentMailboxReceiveTool());
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

    public static HostTools Explorer(ArtifactReadTool? artifactReadTool = null) =>
        new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            includeMutationTools: false, includeProcessTools: false, artifactReadTool: artifactReadTool);

    public FakeCatalog Catalog() => _catalog;

    public PlanProposeTool PlanPropose() => _planPropose;
}
