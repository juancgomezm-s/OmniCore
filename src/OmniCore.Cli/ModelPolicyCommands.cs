using System.Text;
using OmniCore.Host;

namespace OmniCore.Cli;

/// <summary>
/// Comandos de política de modelos (ADR-0044 §6): `omni model …`. Seleccionar una
/// configuración nueva abre el onboarding mínimo (TTY) o aplica ObserveOnly efímero (no
/// TTY). Consultar/cambiar/eliminar operan sobre la clave exacta; eliminar la política hace
/// reaparecer el onboarding en la siguiente selección. El frame TUI de cuestionarios es M4.
/// </summary>
public sealed class ModelPolicyCommands
{
    private readonly ModelPolicyHost _host;

    private readonly TextReader _input;

    private readonly TextWriter _output;

    private readonly bool _interactive;

    private readonly string _workspaceId;

    private readonly CancellationToken _cancellationToken;

    private ModelPolicyCommands(ModelPolicyHost host, TextReader input, TextWriter output,
        bool interactive, string workspaceId, CancellationToken cancellationToken)
    {
        _host = host;
        _input = input;
        _output = output;
        _interactive = interactive;
        _workspaceId = workspaceId;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Entrada desde CliApp. input/output/interactive inyectables para tests.</summary>
    public static System.Threading.Tasks.Task<int> Run(string[] args, TextReader? input = null,
        TextWriter? output = null, bool? interactive = null, string? dataDirectoryOverride = null,
        IReadOnlyList<ModelRegistryModelDescriptor>? registryOverride = null,
        CancellationToken cancellationToken = default)
    {
        var reader = input ?? Console.In;
        var writer = output ?? Console.Out;
        var tty = interactive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected);
        // null → DefaultPlatformPaths aplica OMNICORE_DATA_DIR o el directorio de la plataforma.
        // providers.yaml/models.yaml del usuario, nunca del cwd (INV-029, ADR-0039).
        var host = ModelPolicyHost.Create(dataDirectoryOverride, registryOverride);
        // Workspace del CLI: el directorio actual (una selección vigente por proyecto).
        var workspace = "cli|" + Path.GetFullPath(".");
        var commands = new ModelPolicyCommands(host, reader, writer, tty, workspace, cancellationToken);
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
        foreach (var model in _host.Models)
        {
            found = true;
            var key = KeyFor(model);
            var stored = _host.Get(key, _cancellationToken);
            var status = stored is null
                ? "sin política → onboarding en la próxima selección (fallback ObserveOnly)"
                : stored.Category + " rev=" + stored.Revision;
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

        var result = _host.Select(_workspaceId, key, modelId, ephemeralSafe: false, _cancellationToken);
        if (!result.NeedsOnboarding)
        {
            _output.WriteLine("Seleccionado: " + modelId
                + (result.IsEphemeralSafe ? " (modo seguro efímero)" : "")
                + " → " + result.Policy!.Category
                + " rev=" + result.Policy.Revision);
            return 0;
        }

        // Selección nueva sin política: onboarding mínimo (ADR-0044 §6).
        if (!_interactive)
        {
            // Sin TTY no se puede preguntar: ObserveOnly solo para esta selección.
            _host.Select(_workspaceId, key, modelId, ephemeralSafe: true, _cancellationToken);
            _output.WriteLine("Sin terminal interactiva: " + modelId
                + " queda en modo seguro ObserveOnly por esta selección. Ejecuta 'omni model select "
                + modelId + "' en una TTY para clasificarlo.");
            return 0;
        }

        return Onboard(key, modelId);
    }

    /// <summary>Onboarding lineal mínimo: tarjetas de categoría + elección explícita (ADR-0044 §6).</summary>
    internal int Onboard(ModelPolicyKeyDto key, string modelId)
    {
        var draft = _host.Draft(key, _cancellationToken);
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
            _cancellationToken.ThrowIfCancellationRequested();
            var choice = _input.ReadLine()?.Trim();
            if (choice is null || choice.Length == 0)
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
                _host.Select(_workspaceId, key, modelId, ephemeralSafe: true, _cancellationToken);
                _output.WriteLine("Seleccionado " + modelId + " en modo seguro ObserveOnly (solo esta selección).");
                return 0;
            }

            if (choice is "1" or "2" or "3" or "4")
            {
                var category = choice switch
                {
                    "1" => "ObserveOnly",
                    "2" => "PatchOnly",
                    "3" => "ScopedCoder",
                    _ => "FullAgent",
                };
                var stored = _host.Set(key, 0, category, null, _cancellationToken);
                _host.Select(_workspaceId, key, modelId, ephemeralSafe: false, _cancellationToken);
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
                return Show(key, modelId);
            case "delete":
                return Delete(key, modelId, args);
            case "history":
                return History(key, modelId);
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

        if (category is not ("ObserveOnly" or "PatchOnly" or "ScopedCoder" or "FullAgent"))
        {
            _output.WriteLine("categoría no válida: '" + category + "' (Custom se configura con campos explícitos; usa 1-4 del onboarding)");
            return 1;
        }

        var key = ResolveKeyOrError(modelId, out var resolveResult);
        if (key is null)
        {
            return resolveResult;
        }

        // Revisión esperada: explícita con --revision, o la vigente si ya existe (0 para crear).
        var existing = _host.Get(key, _cancellationToken);
        var expected = revision >= 0 ? revision : existing?.Revision ?? 0;
        try
        {
            var stored = _host.Set(key, expected, category, note, _cancellationToken);
            _output.WriteLine("Política guardada: " + modelId + " → " + category + " rev=" + stored.Revision);
            return 0;
        }
        catch (ModelPolicyRevisionConflict ex)
        {
            _output.WriteLine("Conflicto de revisión (nada se guardó): esperaba " + ex.ExpectedRevision
                + ", la vigente es " + ex.ActualRevision + ". Re-intenta con --revision "
                + ex.ActualRevision + " solo si revisaste el cambio concurrente.");
            return 2;
        }
    }

    private int Show(ModelPolicyKeyDto key, string modelId)
    {
        var stored = _host.Get(key, _cancellationToken);
        if (stored is null)
        {
            _output.WriteLine(modelId + " no tiene política guardada: fallback ObserveOnly y onboarding"
                + " en la próxima selección.");
            return 1;
        }

        var sb = new StringBuilder();
        sb.Append(modelId).Append(" → ").Append(stored.Category)
            .Append(" rev=").Append(stored.Revision).Append('\n');
        sb.Append("  tools: modo=").Append(stored.ToolMode)
            .Append(" visibles=").Append(stored.MaxVisibleTools)
            .Append(" discovery=").Append(stored.AllowToolDiscovery).Append('\n');
        sb.Append("  capacidades: ");
        foreach (var capability in stored.CapabilityCeiling)
        {
            sb.Append(capability).Append(' ');
        }

        sb.Append('\n');
        sb.Append("  mutación: modo=").Append(stored.MutationMode)
            .Append(" delete=").Append(stored.DeletePolicy)
            .Append(" mover=").Append(stored.MoveOrRenamePolicy)
            .Append(" archivos/turno=").Append(stored.MaxFilesPerTurn)
            .Append(" líneas/turno=").Append(stored.MaxChangedLinesPerTurn)
            .Append(" ratio=").Append(stored.MaxRewriteRatio).Append('\n');
        if (stored.Note is not null)
        {
            sb.Append("  nota: ").Append(stored.Note).Append('\n');
        }

        _output.Write(sb.ToString());
        return 0;
    }

    private int Delete(ModelPolicyKeyDto key, string modelId, string[] args)
    {
        long revision = -1;
        for (var i = 4; i + 1 < args.Length; i++)
        {
            if (args[i] == "--revision" && long.TryParse(args[i + 1], out var r))
            {
                revision = r;
            }
        }

        var existing = _host.Get(key, _cancellationToken);
        if (existing is null)
        {
            _output.WriteLine(modelId + " no tiene política guardada.");
            return 1;
        }

        var expected = revision >= 0 ? revision : existing.Revision;
        try
        {
            _host.Delete(key, expected, _cancellationToken);
            _output.WriteLine("Política eliminada: " + modelId + " (el historial se conserva). La próxima");
            _output.WriteLine("selección de esta configuración reabre el onboarding.");
            return 0;
        }
        catch (ModelPolicyRevisionConflict ex)
        {
            _output.WriteLine("Conflicto de revisión (nada se eliminó): esperaba " + ex.ExpectedRevision
                + ", la vigente es " + ex.ActualRevision + ".");
            return 2;
        }
    }

    private int History(ModelPolicyKeyDto key, string modelId)
    {
        var history = _host.History(key, _cancellationToken);
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

    private ModelPolicyKeyDto? ResolveKeyOrError(string modelId, out int exitCode)
    {
        var model = FindModel(modelId);
        if (model is null)
        {
            _output.WriteLine("modelo desconocido: '" + modelId
                + "'. 'omni model list' muestra los modelos del registro.");
            exitCode = 2;
            return null;
        }

        exitCode = 0;
        return KeyFor(model);
    }

    private ModelRegistryModelDescriptor? FindModel(string modelId)
    {
        foreach (var model in _host.Models)
        {
            if (model.Id.Equals(modelId, StringComparison.Ordinal))
            {
                return model;
            }
        }

        return null;
    }

    private static ModelPolicyKeyDto KeyFor(ModelRegistryModelDescriptor model) =>
        new(model.ProviderId, model.Id);

    private void Usage()
    {
        _output.WriteLine("uso: omni model list | select <modelo> | policy show|set|delete|history <modelo>");
        _output.WriteLine("  omni model select <m>       selecciona y abre onboarding si no hay política");
        _output.WriteLine("  omni model policy set <m> --category <c> [--revision N] [--note \"t\"]");
        _output.WriteLine("  omni model policy delete <m> [--revision N]");
    }
}
