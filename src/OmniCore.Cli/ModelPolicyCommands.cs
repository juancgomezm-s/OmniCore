using System.Globalization;
using System.Text;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Cli;

/// <summary>
/// Comandos de política de modelos (ADR-0044 §6): `omni model …`. Seleccionar una
/// configuración nueva abre el onboarding mínimo (TTY) o aplica ObserveOnly efímero (no
/// TTY). Consultar/cambiar/eliminar operan sobre la clave exacta; eliminar la política hace
/// reaparecer el onboarding en la siguiente selección. El frame TUI de cuestionarios es M4.
/// `omni model qualify` (M5) ejecuta la suite de cualificación con consentimiento explícito
/// (--yes o aviso interactivo) y muestra la recomendación resultante, que jamás se aplica
/// sola: aplicarla exige el flujo explícito `omni model policy set` (ADR-0044 §9).
/// </summary>
public sealed class ModelPolicyCommands
{
    private readonly ModelPolicyHost _host;

    private readonly ModelQualificationHost _qualification;

    private readonly Localization _loc;

    private readonly TextReader _input;

    private readonly TextWriter _output;

    private readonly bool _interactive;

    private readonly string _workspaceId;

    private readonly CancellationToken _cancellationToken;

    private ModelPolicyCommands(ModelPolicyHost host, ModelQualificationHost qualification,
        Localization loc, TextReader input, TextWriter output,
        bool interactive, string workspaceId, CancellationToken cancellationToken)
    {
        _host = host;
        _qualification = qualification;
        _loc = loc;
        _input = input;
        _output = output;
        _interactive = interactive;
        _workspaceId = workspaceId;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Presents the quota measurement before asking permission for one probe.</summary>
    internal static bool ConfirmQualificationQuota(ProviderQuotaSnapshot snapshot, Localization localization,
        TextReader input, TextWriter output, bool interactive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        output.WriteLine(localization.Resolve("cli.model.qualify.quota.header", new Dictionary<string, string>
        {
            ["provider"] = snapshot.ProviderId, ["account"] = snapshot.AccountId ?? "—",
            ["source"] = snapshot.Source, ["date"] = snapshot.AsOf.ToString("O", CultureInfo.InvariantCulture),
            ["availability"] = localization.Resolve("cli.model.qualify.quota.availability." + snapshot.Availability),
        }));
        foreach (var window in snapshot.Windows)
            output.WriteLine(localization.Resolve("cli.model.qualify.quota.window", new Dictionary<string, string>
            {
                ["window"] = window.Id,
                ["remaining"] = window.RemainingPercent.Value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—",
                ["reset"] = window.ResetsAt?.ToString("O", CultureInfo.InvariantCulture) ?? "—",
            }));
        if (!interactive)
        {
            output.WriteLine(localization.Resolve("cli.model.qualify.quota.noninteractive"));
            return false;
        }
        output.Write(localization.Resolve("cli.model.qualify.quota.prompt"));
        output.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        return input.ReadLine()?.Trim().ToLowerInvariant() is "s" or "si" or "sí" or "y" or "yes";
    }

    /// <summary>Entrada desde CliApp. input/output/interactive inyectables para tests.</summary>
    public static async System.Threading.Tasks.Task<int> Run(string[] args, TextReader? input = null,
        TextWriter? output = null, bool? interactive = null, string? dataDirectoryOverride = null,
        IReadOnlyList<ModelRegistryModelDescriptor>? registryOverride = null,
        CancellationToken cancellationToken = default)
    {
        var reader = input ?? Console.In;
        var writer = output ?? Console.Out;
        var tty = interactive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected);
        // null → DefaultPlatformPaths aplica OMNICORE_DATA_DIR o el directorio de la plataforma.
        // providers.yaml/models.yaml del usuario, nunca del cwd (INV-029, ADR-0039).
        using var host = ModelPolicyHost.Create(dataDirectoryOverride, registryOverride);
        if (args.Length >= 2 && args[1] == "refresh")
        {
            try
            {
                var catalog = await new TuiAccountHost().ListModelsAsync(cancellationToken,
                    args.Contains("--diagnose") ? writer.WriteLine : null).ConfigureAwait(false);
                var changed = host.ApplyChatGptCatalog(catalog, ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), cancellationToken);
                writer.WriteLine("ChatGPT: " + catalog.Count + " modelos disponibles");
                foreach (var model in catalog) writer.WriteLine(model.Id + " · " + model.DisplayName);
                if (changed is not null) writer.WriteLine(changed);
                return 0;
            }
            catch (Exception) { writer.WriteLine("No se pudo consultar el catálogo ChatGPT. Selección conservada."); return 1; }
        }
        // Mismo data dir y registro: la cualificación comparte el user.db del usuario (M5).
        using var qualification = ModelQualificationHost.Create(dataDirectoryOverride, registryOverride);
        var loc = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en"
            ? Localization.English()
            : Localization.Spanish();
        // Workspace del CLI: el directorio actual (una selección vigente por proyecto).
        var workspace = "cli|" + Path.GetFullPath(".");
        var commands = new ModelPolicyCommands(host, qualification, loc, reader, writer, tty,
            workspace, cancellationToken);
        return commands.Dispatch(args);
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
            case "qualify":
                return Qualify(args);
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

    // ---- omni model qualify <modelo> [--suite quick] [--yes] [--max-cost USD] ----

    /// <summary>
    /// Ejecuta la suite de cualificación (M5, ADR-0007 §6–§7) con consentimiento explícito:
    /// --yes o aviso interactivo; sin TTY y sin --yes se rechaza. Muestra la transición de
    /// estado, los probes, los traits guardados y la recomendación de política operativa
    /// (ADR-0044 §6), que NO se aplica: aplicarla exige `omni model policy set`.
    /// </summary>
    private int Qualify(string[] args)
    {
        string? modelId = null;
        var suite = "quick";
        var consentGiven = false;
        var maxCost = 1.00m;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--suite" && i + 1 < args.Length)
            {
                suite = args[++i];
            }
            else if (args[i] == "--yes")
            {
                consentGiven = true;
            }
            else if (args[i] == "--max-cost" && i + 1 < args.Length)
            {
                if (!decimal.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 0)
                {
                    _output.WriteLine(_loc.Resolve("cli.model.qualify.invalid_cost", "value", args[i]));
                    return 1;
                }

                maxCost = parsed;
            }
            else if (modelId is null)
            {
                modelId = args[i];
            }
        }

        if (modelId is null)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.usage"));
            return 1;
        }

        var model = FindModel(modelId);
        if (model is null)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.unknown_model", "model", modelId));
            return 2;
        }

        QualificationCostEstimate estimate;
        try
        {
            estimate = _qualification.PreviewSuiteCost(modelId, suite);
        }
        catch (ModelQualificationUnsupportedSuiteException ex)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.suite.unsupported", "suite", ex.Suite));
            return 2;
        }

        // Consentimiento explícito (ADR-0007 §7): la suite gasta dinero real y nunca corre sola.
        // Sin TTY no hay forma de preguntar: sin --yes se rechaza (nada se ejecuta).
        if (!consentGiven)
        {
            if (!_interactive)
            {
                _output.WriteLine(_loc.Resolve("cli.model.qualify.consent.required"));
                return 1;
            }

            _output.Write(_loc.Resolve(estimate.Usd is null
                    ? "cli.model.qualify.consent.prompt.unknown"
                    : "cli.model.qualify.consent.prompt",
                new Dictionary<string, string>
                {
                    ["cost"] = estimate.Usd?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "",
                    ["cap"] = maxCost.ToString("0.0000", CultureInfo.InvariantCulture),
                }));
            _output.Flush();
            _cancellationToken.ThrowIfCancellationRequested();
            var answer = _input.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("s" or "si" or "sí" or "y" or "yes"))
            {
                _output.WriteLine(_loc.Resolve("cli.model.qualify.consent.declined"));
                return 1;
            }
        }

        _output.WriteLine(_loc.Resolve("cli.model.qualify.running", "suite", suite));
        QualificationRunResult result;
        try
        {
            result = _qualification.QualifyAsync(modelId, new QualificationOptions
            {
                Suite = suite,
                ConsentGiven = true,
                MaxTotalCostUsd = maxCost,
                ConfirmLowQuota = (snapshot, token) => System.Threading.Tasks.Task.FromResult(
                    ConfirmQualificationQuota(snapshot, _loc, _input, _output, _interactive, token)),
            }, _cancellationToken).GetAwaiter().GetResult();
        }
        catch (ModelQualificationCostCapException ex)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.cost.exceeded",
                new Dictionary<string, string>
                {
                    ["estimated"] = ex.EstimatedUsd.ToString("0.00", CultureInfo.InvariantCulture),
                    ["cap"] = ex.CapUsd.ToString("0.00", CultureInfo.InvariantCulture),
                }));
            return 2;
        }
        catch (ModelQualificationCostEvidenceUnavailableException)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.cost.evidence.unavailable"));
            return 2;
        }
        catch (ModelQualificationSuiteIncompleteException ex)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.suite.failed",
                "failures", string.Join("; ", ex.Failures)));
            return 2;
        }
        catch (ModelQualificationUnsupportedSuiteException ex)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.suite.unsupported", "suite", ex.Suite));
            return 2;
        }
        catch (ModelQualificationConsentException)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.consent.required"));
            return 1;
        }
        catch (ModelQualificationCredentialMissingException)
        {
            _output.WriteLine(_loc.Resolve("cli.runtime.credential.missing", "command", "model qualify"));
            return 1;
        }

        // Estado (ADR-0007 §4): Unknown → Declared → ProvisionallyClassified → Qualified.
        _output.WriteLine(_loc.Resolve("cli.model.qualify.state", new Dictionary<string, string>
        {
            ["previous"] = result.PreviousState,
            ["state"] = result.NewState,
            ["revision"] = result.ProfileRevision.ToString(CultureInfo.InvariantCulture),
            ["key"] = result.KeyHash[..12] + "…",
        }));

        _output.WriteLine(_loc.Resolve("cli.model.qualify.probe.header"));
        foreach (var probe in result.Probes)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.probe.line", new Dictionary<string, string>
            {
                ["id"] = probe.Id,
                ["kind"] = probe.Kind,
                ["status"] = probe.Status,
                ["score"] = probe.Score.ToString("0.00", CultureInfo.InvariantCulture),
                ["cost"] = probe.CostUsd is { } cost
                    ? cost.ToString("0.0000", CultureInfo.InvariantCulture) + " USD"
                    : _loc.Resolve("cli.model.qualify.cost.unavailable"),
            }));
        }

        _output.WriteLine(_loc.Resolve("cli.model.qualify.trait.header"));
        foreach (var trait in result.Traits)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.trait.line", new Dictionary<string, string>
            {
                ["trait"] = trait.Trait,
                ["value"] = trait.Value.ToString("0.00", CultureInfo.InvariantCulture),
                ["confidence"] = trait.Confidence.ToString("0.0", CultureInfo.InvariantCulture),
                ["samples"] = trait.Samples.ToString(CultureInfo.InvariantCulture),
                ["source"] = trait.Source,
            }));
        }

        // Recomendación (ADR-0044 §6): evidencia → categoría recomendada, nunca auto-ampliada.
        _output.WriteLine(_loc.Resolve("cli.model.qualify.recommendation.header",
            "category", result.RecommendedCategory));
        _output.WriteLine(_loc.Resolve("cli.model.qualify.recommendation.mutation", new Dictionary<string, string>
        {
            ["mode"] = result.RecommendedMutationMode,
            ["delete"] = result.RecommendedDeletePolicy,
            ["move"] = result.RecommendedMoveOrRenamePolicy,
            ["files"] = result.RecommendedMaxFilesPerTurn.ToString(CultureInfo.InvariantCulture),
            ["lines"] = result.RecommendedMaxChangedLinesPerTurn.ToString(CultureInfo.InvariantCulture),
            ["ratio"] = result.RecommendedMaxRewriteRatio.ToString("0.00", CultureInfo.InvariantCulture),
        }));
        foreach (var note in result.RecommendationNotes)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.recommendation.note", "note", note));
        }

        _output.WriteLine(_loc.Resolve("cli.model.qualify.recommendation.apply_hint",
            new Dictionary<string, string>
            {
                ["model"] = modelId,
                ["category"] = result.RecommendedCategory,
            }));
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
        _output.WriteLine("uso: omni model list | refresh | select <modelo> | policy show|set|delete|history <modelo> | qualify <modelo>");
        _output.WriteLine("  omni model refresh  consulta el catálogo ChatGPT actual sin generar ni usar API keys");
        _output.WriteLine("  omni model select <m>       selecciona y abre onboarding si no hay política");
        _output.WriteLine("  omni model policy set <m> --category <c> [--revision N] [--note \"t\"]");
        _output.WriteLine("  omni model policy delete <m> [--revision N]");
        _output.WriteLine("  omni model qualify <m> [--suite quick] [--yes] [--max-cost USD]  ejecuta la suite y recomienda");
    }
}
