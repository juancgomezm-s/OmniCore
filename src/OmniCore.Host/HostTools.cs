namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Execution;
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
        bool includeMutationTools = false)
    {
        _planPropose = new PlanProposeTool(planService);
        var catalog = (includeSimulationTools ? FakeCatalog.Default() : new FakeCatalog())
            .Add(new ReadFileTool(boundary))
            .Add(new ListDirectoryTool(boundary));
        if (includeMutationTools)
        {
            catalog = catalog.Add(new FilesystemPatchTool(boundary));
        }

        _catalog = catalog
            .Add(new ReferenceResolveTool(boundary))
            .Add(_planPropose);
        // user.ask (ADR-0045) NO se expone todavía: su ExecuteAsync devuelve éxito con el propio
        // cuestionario y el Host aún no publica la InteractionRequest ni entrega la respuesta al
        // Turn (QuestionnaireInteractionService sin cablear). Exponerlo haría creer al modelo que
        // tiene respuesta (INV-025). Entra cuando el Turn pueda suspenderse y reanudarse.
    }

    public static HostTools Default()
    {
        var boundary = new PathBoundaryValidator();
        var planService = new PlanService();
        return new HostTools(boundary, planService, includeSimulationTools: true, includeMutationTools: true);
    }

    public static HostTools Explorer() =>
        new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            includeMutationTools: false);

    public FakeCatalog Catalog() => _catalog;

    public PlanProposeTool PlanPropose() => _planPropose;
}
