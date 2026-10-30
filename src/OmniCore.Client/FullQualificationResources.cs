namespace OmniCore.Client;

public static class FullQualificationResources
{
    private static readonly IReadOnlyDictionary<string, string[]> Values = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["cli.model.help.qualify"] = ["  omni model qualify <m> --suite quick|full [--yes] [--max-cost USD] [--probe-timeout s]  ejecuta la suite y recomienda", "  omni model qualify <m> --suite quick|full [--yes] [--max-cost USD] [--probe-timeout s]  runs the suite and recommends", "  omni model qualify <m> --suite quick|full [--yes] [--max-cost USD] [--probe-timeout s]  exécute la suite et recommande", "  omni model qualify <m> --suite quick|full [--yes] [--max-cost USD] [--probe-timeout s]  esegue la suite e formula una raccomandazione"],
        ["cli.model.calibration.usage"] = ["uso: omni model calibration <modelo>", "usage: omni model calibration <model>", "utilisation : omni model calibration <modèle>", "uso: omni model calibration <modello>"],
        ["cli.model.calibration.header"] = ["Calibración observada · clave {key}", "Observed calibration · key {key}", "Étalonnage observé · clé {key}", "Calibrazione osservata · chiave {key}"],
        ["cli.model.calibration.unknown"] = ["sin evidencia", "no evidence", "sans preuve", "nessuna prova"],
        ["cli.model.calibration.note"] = ["Contadores del runtime por configuración exacta; no cambian permisos ni política. Un dato ausente no es cero.", "Runtime counters per exact configuration; permissions and policy stay unchanged. Missing data is not zero.", "Compteurs du runtime par configuration exacte ; droits et politique inchangés. Une donnée absente ne vaut pas zéro.", "Contatori del runtime per configurazione esatta; permessi e criteri invariati. Un dato assente non equivale a zero."],
        ["cli.model.calibration.model_missing"] = ["Modelo no configurado.", "Model is not configured.", "Modèle non configuré.", "Modello non configurato."],
        ["cli.model.calibration.model_steps"] = ["Pasos de modelo completados", "Completed model steps", "Étapes du modèle terminées", "Passi del modello completati"],
        ["cli.model.calibration.tool_calls"] = ["Llamadas a herramientas", "Tool calls", "Appels d’outils", "Chiamate agli strumenti"],
        ["cli.model.calibration.valid_tool_arguments"] = ["Argumentos aceptados", "Accepted arguments", "Arguments acceptés", "Argomenti accettati"],
        ["cli.model.calibration.invalid_tool_arguments"] = ["Argumentos rechazados", "Rejected arguments", "Arguments refusés", "Argomenti rifiutati"],
        ["cli.model.calibration.argument_repairs"] = ["Recuperaciones de argumentos en un paso posterior", "Argument recoveries in a later step", "Récupérations d’arguments à une étape ultérieure", "Recuperi di argomenti in un passo successivo"],
        ["cli.model.calibration.tool_successes"] = ["Herramientas completadas", "Completed tools", "Outils terminés", "Strumenti completati"],
        ["cli.model.calibration.progress_stalls"] = ["Estancamientos observados", "Observed progress stalls", "Blocages observés", "Stalli osservati"],
        ["cli.model.calibration.completion_gate_rejections"] = ["Rechazos de validación final", "Completion gate rejections", "Refus de validation finale", "Rifiuti della validazione finale"],
        ["cli.model.qualify.usage"] = ["uso: omni model qualify <modelo> [--suite quick|full] [--yes] [--max-cost USD] [--probe-timeout segundos]", "usage: omni model qualify <model> [--suite quick|full] [--yes] [--max-cost USD] [--probe-timeout seconds]", "utilisation : omni model qualify <modèle> [--suite quick|full] [--yes] [--max-cost USD] [--probe-timeout secondes]", "uso: omni model qualify <modello> [--suite quick|full] [--yes] [--max-cost USD] [--probe-timeout secondi]"],
        ["cli.model.qualify.suite.unsupported"] = ["suite no disponible: '{suite}' (quick o full)", "unsupported suite: '{suite}' (quick or full)", "suite indisponible : '{suite}' (quick ou full)", "suite non disponibile: '{suite}' (quick o full)"],
        ["cli.model.qualify.consent.prompt"] = ["La suite solicitada llamará al proveedor (estimación {cost} USD; tope {cap} USD). ¿Continuar? (s/N): ", "The requested suite calls the provider (estimate {cost} USD; cap {cap} USD). Continue? (y/N): ", "La suite demandée appelle le fournisseur (estimation {cost} USD ; plafond {cap} USD). Continuer ? (o/N) : ", "La suite richiesta chiama il provider (stima {cost} USD; limite {cap} USD). Continuare? (s/N): "],
        ["cli.model.qualify.consent.prompt.unknown"] = ["La suite solicitada llamará al proveedor (costo desconocido; tope {cap} USD). ¿Continuar? (s/N): ", "The requested suite calls the provider (cost unknown; cap {cap} USD). Continue? (y/N): ", "La suite demandée appelle le fournisseur (coût inconnu ; plafond {cap} USD). Continuer ? (o/N) : ", "La suite richiesta chiama il provider (costo sconosciuto; limite {cap} USD). Continuare? (s/N): "],
        ["cli.model.qualify.full.info"] = ["Full añade fixtures de código en worktrees temporales: valida versiones y cambios exactos y ejecuta únicamente código conocido. No modifica tu proyecto ni amplía permisos del modelo.", "Full adds code fixtures in temporary worktrees: it validates versions and exact edits and executes only known code. It does not modify your project or widen model permissions.", "Full ajoute des fixtures de code dans des worktrees temporaires : versions et modifications exactes, exécution de code connu uniquement. Votre projet et les droits du modèle restent inchangés.", "Full aggiunge fixture di codice in worktree temporanei: verifica versioni e modifiche esatte ed esegue solo codice noto. Progetto e permessi del modello restano invariati."],
    };
    public static IReadOnlyCollection<string> Keys => Values.Keys.ToArray();
    internal static bool ContainsKey(string key) => Values.ContainsKey(key);
    internal static bool TryTranslate(string locale, string key, out string text)
    {
        if (Values.TryGetValue(key, out var values))
        { text = values[locale switch { "en" => 1, "fr" => 2, "it" => 3, _ => 0 }]; return true; }
        text = ""; return false;
    }
}
