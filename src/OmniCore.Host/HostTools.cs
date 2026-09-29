namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Tools;

/// <summary>
/// Composición de tools para el runtime de M2: FakeTools de simulación + las tools Core reales
/// (filesystem.read, reference.resolve, plan.propose) sobre la frontera de paths y el PlanService.
/// El catálogo alimenta el pipeline de tools del simulation engine.
/// </summary>
public sealed class HostTools
{
    private readonly FakeCatalog _catalog;

    private readonly PlanProposeTool _planPropose;

    public HostTools(IPathBoundaryValidator boundary, PlanService planService, bool includeSimulationTools = true)
    {
        _planPropose = new PlanProposeTool(planService);
        _catalog = (includeSimulationTools ? FakeCatalog.Default() : new FakeCatalog())
            .Add(new ReadFileTool(boundary))
            .Add(new FilesystemPatchTool(boundary))
            .Add(new ReferenceResolveTool(boundary))
            .Add(new UserAskTool())
            .Add(_planPropose);
    }

    public static HostTools Default()
    {
        var boundary = new PathBoundaryValidator();
        var planService = new PlanService();
        return new HostTools(boundary, planService);
    }

    public static HostTools Explorer() =>
        new HostTools(new PathBoundaryValidator(), new PlanService(), false);

    public FakeCatalog Catalog() => _catalog;

    public PlanProposeTool PlanPropose() => _planPropose;
}
