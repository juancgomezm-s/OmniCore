using System.Text;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Cli;

/// <summary>
/// Comandos de política de modelos (ADR-0044 §6): `omni model …`. Seleccionar una
/// configuración nueva abre el onboarding mínimo (TTY) o aplica ObserveOnly efímero (no
/// TTY). Consultar/cambiar/eliminar operan sobre la clave exacta; eliminar la política hace
/// reaparecer el onboarding en la siguiente selección. El frame TUI de cuestionarios es M4.
/// </summary>
public sealed class ModelPolicyCommands
{
    private readonly ModelPolicyService _service;

    private readonly ModelRegistry _registry;

    private readonly TextReader _input;

    private readonly TextWriter _output;

    private readonly bool _interactive;

    private readonly string _workspaceId;

    private ModelPolicyCommands(ModelPolicyService service, ModelRegistry registry, TextReader input,
        TextWriter output, bool interactive, string workspaceId)
    {
        _service = service;
        _registry = registry;
        _input = input;
        _output = output;
        _interactive = interactive;
        _workspaceId = workspaceId;
    }

    /// <summary>Entrada desde CliApp. input/output/interactive inyectables para tests.</summary>
    public static System.Threading.Tasks.Task<int> Run(string[] args, TextReader? input = null,
        TextWriter? output = null, bool? interactive = null, string? dataDirectoryOverride = null,
        ModelRegistry? registryOverride = null)
    {
        var reader = input ?? Console.In;
        var writer = output ?? Console.Out;
        var tty = interactive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected);
        // null → DefaultPlatformPaths aplica OMNICORE_DATA_DIR o el directorio de la plataforma.
        var dataDirectory = dataDirectoryOverride;
        var service = OmniHost.CreateModelPolicyService(dataDirectory);
        // providers.yaml/models.yaml del usuario, nunca del cwd (INV-029, ADR-0039).
        var registry = registryOverride
            ?? OmniHost.LoadUserModelRegistry(OmniHost.CreatePlatformPaths(dataDirectory));
        // Workspace del CLI: el directorio actual (una selección vigente por proyecto).
        var workspace = "cli|" + Path.GetFullPath(".");
        var commands = new ModelPolicyCommands(service, registry, reader, writer, tty, workspace);
        return System.Threading.Tasks.Task.FromResult(commands.Dispatch(args));
    }

    private int Dispatch(string[] args)
    {
        if (args.Length < 2)
        {
            Usage();
            return 1;
        }

        var sub = args[1];
        switch (sub)
        {
            case "list":
                return List();
            case "select":
                return Select(args);
            case "policy":
                return Policy(args);
            default:
                Usage();
                return 1;
        }
    }

    // ---- omni model list ----

    private int List()
    {
        _output.WriteLine("Modelos del registro y estado de su política (clave exacta):");
        var found = false;
        foreach (var model in _registry.Models())
        {
            found = true;
            if (KeyFor(model.Id) is not { } key)
            {
                continue;
            }

            var stored = _service.Get(key, CancellationToken.None);
            var status = stored is null
                ? "sin política → onboarding en la próxima selección (fallback ObserveOnly)"
                : stored!.Policy.Category + " rev=" + stored.Revision;
            _output.WriteLine("  " + model.Id + " [" + key + "] → " + status);
        }

        if (!found)
        {
            _output.WriteLine("  (sin modelos configurados; ejecuta 'omni doctor')");
        }

        return 0;
    }

    // ---- omni model select <id> ----

    private int Select(string[] args)
    {
        if (args.Length < 3)
        {
            _output.WriteLine("uso: omni model select <modelo>");
            return 1;
        }

        var modelId = args[2];
        var key = ResolveKeyOrError(modelId, out var resolveResult);
        if (key is null)
        {
            return resolveResult;
        }

        var result = _service.Select(_workspaceId, key!, modelId, ephemeralSafe: false,
            CancellationToken.None);
        if (!result.NeedsOnboarding)
        {
            _output.WriteLine("Seleccionado: " + modelId
                + (result.Selection!.EphemeralObserveOnly ? " (modo seguro efímero)" : "")
                + " → " + result.Policy!.Policy.Category
                + " rev=" + result.Policy.Revision);
            return 0;
        }

        // Selección nueva sin política: onboarding mínimo (ADR-0044 §6).
        if (!_interactive)
        {
            // Sin TTY no se puede preguntar: ObserveOnly solo para esta selección.
            var ephemeral = _service.Select(_workspaceId, key!, modelId, ephemeralSafe: true,
                CancellationToken.None);
            _output.WriteLine("Sin terminal interactiva: " + modelId
                + " queda en modo seguro ObserveOnly por esta selección. Ejecuta 'omni model select "
                + modelId + "' en una TTY para clasificarlo.");
            return 0;
        }

        return Onboard(key!, modelId);
    }

    /// <summary>Onboarding lineal mínimo: tarjetas de categoría + elección explícita (ADR-0044 §6).</summary>
    internal int Onboard(ModelPolicyKey key, string modelId)
    {
        var draft = _service.Draft(key, CancellationToken.None);
        _output.WriteLine("");
        _output.WriteLine("Onboarding de política — " + key);
        _output.WriteLine("Una categoría es un TECHO de autonomía, nunca un permiso: el Permission");
        _output.WriteLine("Engine sigue decidiendo cada operación. Sin política, el fallback es");
        _output.WriteLine("ObserveOnly (lectura; ninguna escritura).");
        _output.WriteLine("");
        _output.WriteLine("  1) ObserveOnly  — leer, listar, buscar, resolver referencias, proponer plan.");
        _output.WriteLine("  2) PatchOnly    — lo anterior + parche estructurado sobre archivos ya leídos.");
        _output.WriteLine("  3) ScopedCoder  — parche + crear + build/test acotado a la Task.");
        _output.WriteLine("  4) FullAgent    — techo explícito del usuario (todas las capacidades).");
        _output.WriteLine("  s) Modo seguro una vez — ObserveOnly solo por esta selección.");
        _output.WriteLine("  c) Cancelar — no se guarda ni selecciona nada.");
        foreach (var warning in draft.Warnings)
        {
            _output.WriteLine("  aviso: " + warning);
        }

        _output.WriteLine("Recomendación sin evidencia de cualificación: 1) ObserveOnly.");
        while (true)
        {
            _output.Write("Elige 1-4, s o c: ");
            _output.Flush();
            var choice = _input.ReadLine()?.Trim();
            if (choice is null || choice!.Length == 0)
            {
                _output.WriteLine("");
                _output.WriteLine("Entrada cerrada: se cancela sin guardar nada (fallback ObserveOnly).");
                return 1;
            }

            if (choice == "c")
            {
                _output.WriteLine("Cancelado: no se guardó política ni selección.");
                return 1;
            }

            if (choice == "s")
            {
                _service.Select(_workspaceId, key, modelId, ephemeralSafe: true, CancellationToken.None);
                _output.WriteLine("Seleccionado " + modelId + " en modo seguro ObserveOnly (solo esta selección).");
                return 0;
            }

            if (choice is "1" or "2" or "3" or "4")
            {
                var category = choice switch
                {
                    "1" => ModelPolicyCategory.ObserveOnly,
                    "2" => ModelPolicyCategory.PatchOnly,
                    "3" => ModelPolicyCategory.ScopedCoder,
                    _ => ModelPolicyCategory.FullAgent,
                };
                var policy = ModelPolicyPresets.For(category);
                var stored = _service.Set(key, 0, policy, CancellationToken.None);
                _service.Select(_workspaceId, key, modelId, ephemeralSafe: false, CancellationToken.None);
                _output.WriteLine("Política guardada: " + category + " rev=" + stored.Revision
                    + ". Seleccionado: " + modelId);
                return 0;
            }

            _output.WriteLine("Opción no válida: '" + choice + "'.");
        }
    }

    // ---- omni model policy show|set|delete|history <id> ----

    private int Policy(string[] args)
    {
        if (args.Length < 3)
        {
            _output.WriteLine("uso: omni model policy show|set|delete|history <modelo> [...]");
            return 1;
        }

        var action = args[2];
        if (action == "set")
        {
            return SetPolicy(args);
        }

        if (args.Length < 4)
        {
            _output.WriteLine("uso: omni model policy " + action + " <modelo>");
            return 1;
        }

        var modelId = args[3];
        var key = ResolveKeyOrError(modelId, out var resolveResult);
        if (key is null)
        {
            return resolveResult;
        }

        switch (action)
        {
            case "show":
                return Show(key!, modelId);
            case "delete":
                return Delete(key!, modelId, args);
            case "history":
                return History(key!, modelId);
            default:
                Usage();
                return 1;
        }
    }

    private int SetPolicy(string[] args)
    {
        string? category = null;
        string? note = null;
        long revision = -1;
        string? modelId = null;
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] == "--category" && i + 1 < args.Length)
            {
                category = args[++i];
            }
            else if (args[i] == "--note" && i + 1 < args.Length)
            {
                note = args[++i];
            }
            else if (args[i] == "--revision" && i + 1 < args.Length && long.TryParse(args[i + 1], out var r))
            {
                revision = r;
                i++;
            }
            else if (modelId is null)
            {
                modelId = args[i];
            }
        }

        if (modelId is null || category is null)
        {
            _output.WriteLine("uso: omni model policy set <modelo> --category ObserveOnly|PatchOnly|ScopedCoder|FullAgent [--revision N] [--note \"texto\"]");
            return 1;
        }

        if (!Enum.TryParse<ModelPolicyCategory>(category, ignoreCase: false, out var parsed)
            || parsed == ModelPolicyCategory.Custom)
        {
            _output.WriteLine("categoría no válida: '" + category + "' (Custom se configura con campos explícitos; usa 1-4 del onboarding)");
            return 1;
        }

        var key = ResolveKeyOrError(modelId!, out var resolveResult);
        if (key is null)
        {
            return resolveResult;
        }

        // Revisión esperada: explícita con --revision, o la vigente si ya existe (0 para crear).
        var existing = _service.Get(key!, CancellationToken.None);
        var expected = revision >= 0 ? revision : existing?.Revision ?? 0;
        var policy = ModelPolicyPresets.For(parsed);
        var withNote = note is null ? policy : new UserModelPolicy(parsed, policy.ToolPolicy,
            policy.MutationPolicy, policy.Source, note);
        try
        {
            var stored = _service.Set(key!, expected, withNote, CancellationToken.None);
            _output.WriteLine("Política guardada: " + modelId + " → " + parsed + " rev=" + stored.Revision);
            return 0;
        }
        catch (ModelPolicyRevisionConflictException ex)
        {
            _output.WriteLine("Conflicto de revisión (nada se guardó): esperaba " + ex.ExpectedRevision
                + ", la vigente es " + ex.ActualRevision + ". Re-intenta con --revision "
                + ex.ActualRevision + " solo si revisaste el cambio concurrente.");
            return 2;
        }
    }

    private int Show(ModelPolicyKey key, string modelId)
    {
        var stored = _service.Get(key, CancellationToken.None);
        if (stored is null)
        {
            _output.WriteLine(modelId + " no tiene política guardada: fallback ObserveOnly y onboarding"
                + " en la próxima selección.");
            return 1;
        }

        var p = stored!.Policy;
        var sb = new StringBuilder();
        sb.Append(modelId).Append(" → ").Append(p.Category)
            .Append(" rev=").Append(stored.Revision).Append('\n');
        sb.Append("  tools: modo=").Append(p.ToolPolicy.Mode)
            .Append(" visibles=").Append(p.ToolPolicy.MaxVisibleTools)
            .Append(" discovery=").Append(p.ToolPolicy.AllowToolDiscovery).Append('\n');
        sb.Append("  capacidades: ");
        foreach (var c in p.ToolPolicy.CapabilityCeiling.OrderBy(c => (int)c))
        {
            sb.Append(c).Append(' ');
        }

        sb.Append('\n');
        sb.Append("  mutación: modo=").Append(p.MutationPolicy.Mode)
            .Append(" delete=").Append(p.MutationPolicy.Delete)
            .Append(" mover=").Append(p.MutationPolicy.MoveOrRename)
            .Append(" archivos/turno=").Append(p.MutationPolicy.MaxFilesPerTurn)
            .Append(" líneas/turno=").Append(p.MutationPolicy.MaxChangedLinesPerTurn)
            .Append(" ratio=").Append(p.MutationPolicy.MaxRewriteRatio).Append('\n');
        if (p.Note is not null)
        {
            sb.Append("  nota: ").Append(p.Note).Append('\n');
        }

        _output.Write(sb.ToString());
        return 0;
    }

    private int Delete(ModelPolicyKey key, string modelId, string[] args)
    {
        long revision = -1;
        for (var i = 4; i + 1 < args.Length; i++)
        {
            if (args[i] == "--revision" && long.TryParse(args[i + 1], out var r))
            {
                revision = r;
            }
        }

        var existing = _service.Get(key, CancellationToken.None);
        if (existing is null)
        {
            _output.WriteLine(modelId + " no tiene política guardada.");
            return 1;
        }

        var expected = revision >= 0 ? revision : existing!.Revision;
        try
        {
            _service.Delete(key, expected, CancellationToken.None);
            _output.WriteLine("Política eliminada: " + modelId + " (el historial se conserva). La próxima");
            _output.WriteLine("selección de esta configuración reabre el onboarding.");
            return 0;
        }
        catch (ModelPolicyRevisionConflictException ex)
        {
            _output.WriteLine("Conflicto de revisión (nada se eliminó): esperaba " + ex.ExpectedRevision
                + ", la vigente es " + ex.ActualRevision + ".");
            return 2;
        }
    }

    private int History(ModelPolicyKey key, string modelId)
    {
        var history = _service.History(key, CancellationToken.None);
        if (history.Count == 0)
        {
            _output.WriteLine(modelId + " sin historial.");
            return 1;
        }

        foreach (var change in history)
        {
            _output.WriteLine("  rev=" + change.Revision + " " + change.ChangeKind
                + " en " + change.ChangedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        return 0;
    }

    private ModelPolicyKey? ResolveKeyOrError(string modelId, out int exitCode)
    {
        var key = KeyFor(modelId);
        if (key is null)
        {
            _output.WriteLine("modelo desconocido: '" + modelId
                + "'. 'omni model list' muestra los modelos del registro.");
            exitCode = 2;
            return null;
        }

        exitCode = 0;
        return key;
    }

    private ModelPolicyKey? KeyFor(string modelId)
    {
        foreach (var model in _registry.Models())
        {
            if (model.Id.Equals(modelId, StringComparison.Ordinal))
            {
                return ModelPolicyKey.For(model.ProviderId, model.Id);
            }
        }

        return null;
    }

    private void Usage()
    {
        _output.WriteLine("uso: omni model list | select <modelo> | policy show|set|delete|history <modelo>");
        _output.WriteLine("  omni model select <m>       selecciona y abre onboarding si no hay política");
        _output.WriteLine("  omni model policy set <m> --category <c> [--revision N] [--note \"t\"]");
        _output.WriteLine("  omni model policy delete <m> [--revision N]");
    }
}
