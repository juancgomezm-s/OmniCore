using System.Globalization;
using OmniCore.Client;
using OmniCore.Domain;
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
        CancellationToken cancellationToken = default, Localization? selectedLocalization = null)
    {
        var reader = input ?? Console.In;
        var writer = output ?? Console.Out;
        var tty = interactive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected);
        var loc = selectedLocalization ?? new Localization(LocaleSelectionResolver.Resolve(
            null, Environment.GetEnvironmentVariable("OMNI_LOCALE"), null).Locale);
        // null → DefaultPlatformPaths aplica OMNICORE_DATA_DIR o el directorio de la plataforma.
        // providers.yaml/models.yaml del usuario, nunca del cwd (INV-029, ADR-0039).
        using var host = ModelPolicyHost.Create(dataDirectoryOverride, registryOverride);
        if (args.Length >= 2 && args[1] == "refresh")
        {
            try
            {
                var catalog = await new TuiAccountHost().ListModelsAsync(cancellationToken,
                    args.Contains("--diagnose") ? writer.WriteLine : null).ConfigureAwait(false);
                var changed = host.ApplyChatGptCatalogWithChange(catalog,
                    ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), cancellationToken);
                writer.WriteLine(loc.Resolve("cli.model.refresh.count", "count",
                    catalog.Count.ToString(CultureInfo.InvariantCulture)));
                foreach (var model in catalog)
                    writer.WriteLine(loc.Resolve("cli.model.refresh.item", new Dictionary<string, string>
                    { ["id"] = model.Id, ["name"] = model.DisplayName }));
                if (changed is { ReplacementModelId: null })
                    writer.WriteLine(loc.Resolve("cli.model.refresh.no_models"));
                else if (changed is not null)
                    writer.WriteLine(loc.Resolve("cli.model.refresh.replaced", new Dictionary<string, string>
                    { ["previous"] = changed.PreviousModelId,
                      ["replacement"] = changed.ReplacementModelId! }));
                return 0;
            }
            catch (Exception) { writer.WriteLine(loc.Resolve("cli.model.refresh.failed")); return 1; }
        }
        // Mismo data dir y registro: la cualificación comparte el user.db del usuario (M5).
        using var qualification = ModelQualificationHost.Create(dataDirectoryOverride, registryOverride);
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
            case "calibration":
                if (args.Length != 3) { _output.WriteLine(_loc.Resolve("cli.model.calibration.usage")); return 1; }
                try
                {
                    var snapshot = _qualification.Calibration(args[2], _cancellationToken);
                    _output.WriteLine(_loc.Resolve("cli.model.calibration.header", "key", snapshot.KeyHash));
                    foreach (var (counter, value) in snapshot.Counters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                        _output.WriteLine(_loc.Resolve("cli.model.calibration." + counter) + ": " + (value?.ToString(CultureInfo.InvariantCulture) ?? _loc.Resolve("cli.model.calibration.unknown")));
                    _output.WriteLine(_loc.Resolve("cli.model.calibration.note"));
                    return 0;
                }
                catch (ArgumentException) { _output.WriteLine(_loc.Resolve("cli.model.calibration.model_missing")); return 1; }
            default:
                Usage();
                return 1;
        }
    }

    // ---- omni model list ----

    private int List()
    {
        _output.WriteLine(_loc.Resolve("cli.model.policy.list.header"));
        var found = false;
        foreach (var model in _host.Models)
        {
            found = true;
            var key = KeyFor(model);
            var stored = _host.Get(key, _cancellationToken);
            var status = stored is null
                ? _loc.Resolve("cli.model.policy.list.status.none", "category",
                    LocalizeModelValue(_loc, "category", "ObserveOnly"))
                : LocalizeModelValue(_loc, "category", stored.Category) + " rev=" + stored.Revision;
            _output.WriteLine("  " + model.Id + " [" + key + "] → " + status);
        }

        if (!found)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.list.unconfigured"));
        }

        return 0;
    }

    // ---- omni model select <id> ----

    private int Select(string[] args)
    {
        if (args.Length < 3)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.select.usage"));
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
            _output.WriteLine(_loc.Resolve(result.IsEphemeralSafe
                ? "cli.model.policy.select.safe_done" : "cli.model.policy.select.done",
                new Dictionary<string, string> { ["model"] = modelId,
                    ["category"] = LocalizeModelValue(_loc, "category", result.Policy!.Category),
                    ["revision"] = result.Policy.Revision.ToString(CultureInfo.InvariantCulture) }));
            return 0;
        }

        // Selección nueva sin política: onboarding mínimo (ADR-0044 §6).
        if (!_interactive)
        {
            // Sin TTY no se puede preguntar: ObserveOnly solo para esta selección.
            _host.Select(_workspaceId, key, modelId, ephemeralSafe: true, _cancellationToken);
            _output.WriteLine(_loc.Resolve("cli.model.policy.select.noninteractive", new Dictionary<string, string>
            { ["model"] = modelId, ["category"] = LocalizeModelValue(_loc, "category", "ObserveOnly") }));
            return 0;
        }

        return Onboard(key, modelId);
    }

    private string RecommendedChoice(string category) => category switch
    {
        "ObserveOnly" => "1) " + LocalizeModelValue(_loc, "category", category),
        "PatchOnly" => "2) " + LocalizeModelValue(_loc, "category", category),
        "ScopedCoder" => "3) " + LocalizeModelValue(_loc, "category", category),
        "FullAgent" => "4) " + LocalizeModelValue(_loc, "category", category),
        _ => category,
    };

    /// <summary>Onboarding lineal mínimo: tarjetas de categoría + elección explícita (ADR-0044 §6).</summary>
    internal int Onboard(ModelPolicyKeyDto key, string modelId)
    {
        var draft = _host.Draft(key, _cancellationToken);
        _output.WriteLine("");
        _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.title", "key", key.ToString()));
        _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.explanation"));
        _output.WriteLine("");
        _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.option.observe"));
        _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.option.patch"));
        _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.option.scoped"));
        _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.option.full"));
        _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.option.safe"));
        _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.option.cancel"));
        foreach (var warning in draft.Warnings)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.warning", "warning", warning));
        }

        _output.WriteLine(_loc.Resolve(draft.HasQualificationEvidence
            ? "cli.model.policy.onboarding.recommendation"
            : "cli.model.policy.onboarding.no_evidence", "choice", RecommendedChoice(draft.RecommendedCategory)));
        while (true)
        {
            _output.Write(_loc.Resolve("cli.model.policy.onboarding.choose"));
            _output.Flush();
            _cancellationToken.ThrowIfCancellationRequested();
            var choice = _input.ReadLine()?.Trim();
            if (choice is null || choice.Length == 0)
            {
                _output.WriteLine("");
                _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.eof"));
                return 1;
            }

            if (choice == "c")
            {
                _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.cancelled"));
                return 1;
            }

            if (choice == "s")
            {
                _host.Select(_workspaceId, key, modelId, ephemeralSafe: true, _cancellationToken);
                _output.WriteLine(_loc.Resolve("cli.model.policy.onboarding.safe", new Dictionary<string, string>
                { ["model"] = modelId, ["category"] = LocalizeModelValue(_loc, "category", "ObserveOnly") }));
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
                _output.WriteLine(_loc.Resolve("cli.model.policy.saved.selection", new Dictionary<string, string>
                { ["category"] = LocalizeModelValue(_loc, "category", category), ["revision"] = stored.Revision.ToString(CultureInfo.InvariantCulture),
                  ["model"] = modelId }));
                return 0;
            }

            _output.WriteLine(_loc.Resolve("cli.model.policy.invalid_choice", "choice", choice));
        }
    }

    // ---- omni model policy show|set|delete|history <id> ----

    private int Policy(string[] args)
    {
        if (args.Length < 3)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.usage"));
            return 1;
        }

        var action = args[2];
        if (action == "set")
        {
            return SetPolicy(args);
        }

        if (args.Length < 4)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.action_usage", "action", action));
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
            _output.WriteLine(_loc.Resolve("cli.model.policy.set.usage"));
            return 1;
        }

        if (category is not ("ObserveOnly" or "PatchOnly" or "ScopedCoder" or "FullAgent"))
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.invalid_category", "category", category));
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
            _output.WriteLine(_loc.Resolve("cli.model.policy.saved", new Dictionary<string, string>
            { ["model"] = modelId, ["category"] = LocalizeModelValue(_loc, "category", category),
              ["revision"] = stored.Revision.ToString(CultureInfo.InvariantCulture) }));
            return 0;
        }
        catch (ModelPolicyRevisionConflict ex)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.revision_conflict", new Dictionary<string, string>
            { ["expected"] = ex.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
              ["actual"] = ex.ActualRevision.ToString(CultureInfo.InvariantCulture) }));
            return 2;
        }
    }

    private int Show(ModelPolicyKeyDto key, string modelId)
    {
        var stored = _host.Get(key, _cancellationToken);
        if (stored is null)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.no_saved_fallback", "model", modelId));
            return 1;
        }

        _output.WriteLine(_loc.Resolve("cli.model.policy.show.head", new Dictionary<string, string>
        { ["model"] = modelId, ["category"] = LocalizeModelValue(_loc, "category", stored.Category),
          ["revision"] = stored.Revision.ToString(CultureInfo.InvariantCulture) }));
        _output.WriteLine(_loc.Resolve("cli.model.policy.show.tools", new Dictionary<string, string>
        { ["mode"] = LocalizeModelValue(_loc, "toolMode", stored.ToolMode), ["visible"] = stored.MaxVisibleTools.ToString(CultureInfo.InvariantCulture),
          ["discovery"] = stored.AllowToolDiscovery.ToString(CultureInfo.InvariantCulture) }));
        _output.WriteLine(_loc.Resolve("cli.model.policy.show.capabilities", "value",
            string.Join(", ", stored.CapabilityCeiling.Select(value =>
                LocalizeModelValue(_loc, "capability", value)))));
        _output.WriteLine(_loc.Resolve("cli.model.policy.show.mutation", new Dictionary<string, string>
        {
            ["mode"] = LocalizeModelValue(_loc, "mutationMode", stored.MutationMode),
            ["delete"] = LocalizeModelValue(_loc, "destructivePolicy", stored.DeletePolicy),
            ["move"] = LocalizeModelValue(_loc, "destructivePolicy", stored.MoveOrRenamePolicy),
            ["files"] = stored.MaxFilesPerTurn.ToString(CultureInfo.InvariantCulture),
            ["lines"] = stored.MaxChangedLinesPerTurn.ToString(CultureInfo.InvariantCulture),
            ["ratio"] = stored.MaxRewriteRatio.ToString(CultureInfo.InvariantCulture),
        }));
        if (stored.Note is not null)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.show.note", "value", stored.Note));
        }
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
            _output.WriteLine(_loc.Resolve("cli.model.policy.no_saved", "model", modelId));
            return 1;
        }

        var expected = revision >= 0 ? revision : existing.Revision;
        try
        {
            _host.Delete(key, expected, _cancellationToken);
            _output.WriteLine(_loc.Resolve("cli.model.policy.deleted", "model", modelId));
            return 0;
        }
        catch (ModelPolicyRevisionConflict ex)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.delete_conflict", new Dictionary<string, string>
            { ["expected"] = ex.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
              ["actual"] = ex.ActualRevision.ToString(CultureInfo.InvariantCulture) }));
            return 2;
        }
    }

    private int History(ModelPolicyKeyDto key, string modelId)
    {
        var history = _host.History(key, _cancellationToken);
        if (history.Count == 0)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.no_history", "model", modelId));
            return 1;
        }

        foreach (var change in history)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.history.entry", new Dictionary<string, string>
            { ["revision"] = change.Revision.ToString(CultureInfo.InvariantCulture),
              ["kind"] = LocalizeChangeKind(_loc, change.ChangeKind),
              ["date"] = change.ChangedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) }));
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
        TimeSpan? probeTimeout = null;
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
            else if (args[i] == "--probe-timeout" && i + 1 < args.Length)
            {
                // Segundos por probe: un modelo local grande con razonamiento puede necesitar más que el
                // tope por defecto (60 s). Acotado para que un valor absurdo no deje la suite colgada.
                if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    || seconds is < 1 or > 3600)
                {
                    _output.WriteLine(_loc.Resolve("cli.model.qualify.invalid_timeout", "value", args[i]));
                    return 1;
                }

                probeTimeout = TimeSpan.FromSeconds(seconds);
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

        if (suite == "full") _output.WriteLine(_loc.Resolve("cli.model.qualify.full.info"));
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
                PerProbeTimeout = probeTimeout,
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
            ["previous"] = LocalizeModelValue(_loc, "qualificationState", result.PreviousState),
            ["state"] = LocalizeModelValue(_loc, "qualificationState", result.NewState),
            ["revision"] = result.ProfileRevision.ToString(CultureInfo.InvariantCulture),
            ["key"] = result.KeyHash[..12] + "…",
        }));

        _output.WriteLine(_loc.Resolve("cli.model.qualify.probe.header"));
        foreach (var probe in result.Probes)
        {
            _output.WriteLine(_loc.Resolve("cli.model.qualify.probe.line", new Dictionary<string, string>
            {
                ["id"] = probe.Id,
                ["kind"] = LocalizeModelValue(_loc, "probeKind", probe.Kind),
                ["status"] = LocalizeModelValue(_loc, "probeStatus", probe.Status),
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
                ["trait"] = LocalizeModelValue(_loc, "trait", trait.Trait),
                ["value"] = trait.Value.ToString("0.00", CultureInfo.InvariantCulture),
                ["confidence"] = trait.Confidence.ToString("0.0", CultureInfo.InvariantCulture),
                ["samples"] = trait.Samples.ToString(CultureInfo.InvariantCulture),
                ["source"] = LocalizeModelValue(_loc, "traitSource", trait.Source),
            }));
        }

        // Recomendación (ADR-0044 §6): evidencia → categoría recomendada, nunca auto-ampliada.
        _output.WriteLine(_loc.Resolve("cli.model.qualify.recommendation.header",
            "category", LocalizeModelValue(_loc, "category", result.RecommendedCategory)));
        _output.WriteLine(_loc.Resolve("cli.model.qualify.recommendation.mutation", new Dictionary<string, string>
        {
            ["mode"] = LocalizeModelValue(_loc, "mutationMode", result.RecommendedMutationMode),
            ["delete"] = LocalizeModelValue(_loc, "destructivePolicy", result.RecommendedDeletePolicy),
            ["move"] = LocalizeModelValue(_loc, "destructivePolicy", result.RecommendedMoveOrRenamePolicy),
            ["files"] = result.RecommendedMaxFilesPerTurn.ToString(CultureInfo.InvariantCulture),
            ["lines"] = result.RecommendedMaxChangedLinesPerTurn.ToString(CultureInfo.InvariantCulture),
            ["ratio"] = result.RecommendedMaxRewriteRatio.ToString("0.00", CultureInfo.InvariantCulture),
        }));
        foreach (var note in result.RecommendationNotes)
        {
            var localizedNote = LocalizeQualificationRecommendationNote(_loc, note);
            _output.WriteLine(_loc.Resolve("cli.model.qualify.recommendation.note", "note", localizedNote));
        }

        _output.WriteLine(_loc.Resolve("cli.model.qualify.recommendation.apply_hint",
            new Dictionary<string, string>
            {
                ["model"] = modelId,
                // Executable examples retain the parser's canonical enum values.
                ["category"] = result.RecommendedCategory,
            }));
        return 0;
    }

    private ModelPolicyKeyDto? ResolveKeyOrError(string modelId, out int exitCode)
    {
        const string externalPrefix = "claude-code/";
        if (modelId.StartsWith(externalPrefix, StringComparison.Ordinal))
        {
            var externalModel = modelId[externalPrefix.Length..];
            if (externalModel.Length is > 0 and <= 128
                && externalModel[0] != '-'
                && externalModel.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.' or ':'))
            {
                exitCode = 0;
                return new ModelPolicyKeyDto("claude-code", externalModel);
            }
            _output.WriteLine(_loc.Resolve("cli.model.policy.external_invalid", "model", modelId));
            exitCode = 2;
            return null;
        }
        var model = FindModel(modelId);
        if (model is null)
        {
            _output.WriteLine(_loc.Resolve("cli.model.policy.unknown_model", "model", modelId));
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

    internal static string LocalizeModelValue(Localization localization, string kind, string value)
    {
        var known = kind switch
        {
            "category" => value is "ObserveOnly" or "PatchOnly" or "ScopedCoder" or "FullAgent" or "Custom",
            "toolMode" => value is "Direct" or "Discovered" or "Code",
            "mutationMode" => value is "None" or "PatchExisting" or "PatchAndCreate" or "Full",
            "destructivePolicy" => value is "Deny" or "Ask" or "Allow",
            "capability" => value is "WorkspaceRead" or "Search" or "ReferenceResolve" or "PlanProposal"
                or "PatchExisting" or "CreateFile" or "ReplaceFile" or "DeleteFile" or "MoveOrRename"
                or "ValidationProcess" or "GeneralProcess" or "Shell" or "Network" or "AgentMailboxWait",
            "qualificationState" => value is "Unknown" or "Declared" or "ProvisionallyClassified"
                or "Qualified" or "Calibrated" or "Stale",
            "probeKind" => value is "Reading" or "Reasoning" or "StructuredOutput" or "CodeFixture",
            "probeStatus" => value is "Passed" or "Failed" or "Error" or "Timeout" or "NotRun",
            "trait" => value is "InstructionFollowing" or "StructuredOutputReliability",
            "traitSource" => value is "empirical",
            "recommendationNote" => value is "NoGeneralMutationEvidence" or "FullAgentNeverRecommended"
                or "ConservativePatchOnly" or "LowFileMutationReliability" or "HighFileMutationReliability",
            _ => false,
        };
        return known ? localization.Resolve($"cli.model.value.{kind}.{value}") : value;
    }

    internal static string LocalizeQualificationRecommendationNote(Localization localization, string note)
    {
        var key = note switch
        {
            var value when value == QualificationRecommender.FullAgentNeverRecommendedNote => "FullAgentNeverRecommended",
            var value when value == QualificationRecommender.NoGeneralFileMutationEvidenceNote => "NoGeneralMutationEvidence",
            var value when value == QualificationRecommender.ConservativePatchOnlyNote => "ConservativePatchOnly",
            var value when value == QualificationRecommender.LowFileMutationReliabilityNote => "LowFileMutationReliability",
            var value when value == QualificationRecommender.HighFileMutationReliabilityNote => "HighFileMutationReliability",
            _ => null,
        };
        if (key is not null)
        {
            return LocalizeModelValue(localization, "recommendationNote", key);
        }

        const string prefix = QualificationRecommender.InsufficientFileMutationEvidencePrefix;
        const string middle = QualificationRecommender.InsufficientFileMutationEvidenceMiddle;
        const string suffix = QualificationRecommender.InsufficientFileMutationEvidenceSuffix;
        if (note.StartsWith(prefix, StringComparison.Ordinal) && note.EndsWith(suffix, StringComparison.Ordinal))
        {
            var body = note[prefix.Length..^suffix.Length];
            var separator = body.IndexOf(middle, StringComparison.Ordinal);
            if (separator >= 0
                && int.TryParse(body.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var samples)
                && int.TryParse(body.AsSpan(separator + middle.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimum))
            {
                return localization.Resolve("cli.model.value.recommendationNote.InsufficientMutationEvidence",
                    new Dictionary<string, string>
                    {
                        ["samples"] = samples.ToString(CultureInfo.InvariantCulture),
                        ["minimum"] = minimum.ToString(CultureInfo.InvariantCulture),
                    });
            }
        }

        return note;
    }

    internal static string LocalizeChangeKind(Localization localization, string changeKind) => changeKind switch
    {
        "created" or "Created" => localization.Resolve("cli.model.policy.history.change.created"),
        "updated" or "Updated" => localization.Resolve("cli.model.policy.history.change.updated"),
        "deleted" or "Deleted" => localization.Resolve("cli.model.policy.history.change.deleted"),
        _ => changeKind,
    };

    private void Usage()
    {
        _output.WriteLine(_loc.Resolve("cli.model.usage"));
        _output.WriteLine(_loc.Resolve("cli.model.help.refresh"));
        _output.WriteLine(_loc.Resolve("cli.model.help.select"));
        _output.WriteLine(_loc.Resolve("cli.model.help.policy_set"));
        _output.WriteLine(_loc.Resolve("cli.model.help.external"));
        _output.WriteLine(_loc.Resolve("cli.model.help.delete"));
        _output.WriteLine(_loc.Resolve("cli.model.help.qualify"));
        _output.WriteLine(_loc.Resolve("cli.model.calibration.usage"));
    }
}
