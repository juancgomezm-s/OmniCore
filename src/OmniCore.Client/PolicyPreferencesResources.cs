namespace OmniCore.Client;

/// <summary>Copy for the editable models/execution preferences surface.</summary>
public static class PolicyPreferencesResources
{
    private static readonly (string Key, string Es, string En, string Fr, string It)[] Rows =
    [
        ("policyPreferences.title", "Modelos y ejecución", "Models and execution", "Modèles et exécution", "Modelli ed esecuzione"),
        ("policyPreferences.unavailable", "No se pudo leer la configuración de usuario.", "User configuration could not be read.", "Impossible de lire la configuration utilisateur.", "Impossibile leggere la configurazione utente."),
        ("policyPreferences.scope_note", "Usuario · Rutas vacías: modelo predeterminado.", "User · Empty routes: default model.", "Utilisateur · Routes vides : modèle par défaut.", "Utente · Percorsi vuoti: modello predefinito."),
        ("policyPreferences.effective_model", "Modelo efectivo", "Effective model", "Modèle effectif", "Modello effettivo"),
        ("policyPreferences.source", "Origen", "Source", "Source", "Origine"),
        ("policyPreferences.default_model", "Modelo del usuario", "User default model", "Modèle utilisateur", "Modello utente"),
        ("policyPreferences.session_cap", "Límite de sesión USD", "Session cap USD", "Plafond de session USD", "Limite sessione USD"),
        ("policyPreferences.daily_cap", "Límite diario USD", "Daily cap USD", "Plafond quotidien USD", "Limite giornaliero USD"),
        ("policyPreferences.route_meta", "Ruta meta", "Meta route", "Route méta", "Percorso meta"),
        ("policyPreferences.route_exploration", "Ruta exploración", "Exploration route", "Route exploration", "Percorso esplorazione"),
        ("policyPreferences.route_implementation", "Ruta implementación", "Implementation route", "Route implémentation", "Percorso implementazione"),
        ("policyPreferences.route_reasoning", "Ruta razonamiento", "Reasoning route", "Route raisonnement", "Percorso ragionamento"),
        ("policyPreferences.route_architecture", "Ruta arquitectura", "Architecture route", "Route architecture", "Percorso architettura"),
        ("policyPreferences.prefer_local", "Preferir local", "Prefer local", "Préférer le local", "Preferisci locale"),
        ("policyPreferences.escalation_mode", "Modo escalación", "Escalation mode", "Mode d’escalade", "Modalità escalation"),
        ("policyPreferences.escalation_chain", "Cadena escalación", "Escalation chain", "Chaîne d’escalade", "Catena escalation"),
        ("policyPreferences.route_help", "Aliases separados por coma; dejar una ruta vacía para no definirla. true/false controla preferencia local.", "Comma-separated aliases; leave a route empty to omit it. true/false controls local preference.", "Aliases séparés par des virgules ; laisser une route vide pour l’omettre. true/false règle la préférence locale.", "Alias separati da virgole; lascia vuoto per omettere una route. true/false imposta la preferenza locale."),
        ("policyPreferences.not_set", "sin definir", "not set", "non défini", "non impostato"),
        ("policyPreferences.default", "Predeterminado", "Default", "Par défaut", "Predefinito"),
        ("policyPreferences.save", "Guardar cambios", "Save changes", "Enregistrer", "Salva modifiche"),
        ("policyPreferences.save_routes", "Guardar rutas", "Save routes", "Enregistrer les routes", "Salva percorsi"),
        ("policyPreferences.model_policies", "Políticas por modelo…", "Per-model policies…", "Politiques par modèle…", "Criteri per modello…"),
        ("policyPreferences.reasoning_reset", "Restablecer preferencia de razonamiento", "Reset reasoning preference", "Réinitialiser la préférence de raisonnement", "Reimposta preferenza di ragionamento"),
        ("policyPreferences.reasoning_note", "Restablecer quita el valor predeterminado User; cada modelo aplicará sólo los niveles que declara.", "Reset removes the User default; each model applies only the levels it declares.", "La réinitialisation supprime le défaut User ; chaque modèle applique uniquement les niveaux qu’il déclare.", "Il ripristino rimuove il valore predefinito User; ogni modello applica solo i livelli dichiarati."),
        ("policyPreferences.back", "Volver", "Back", "Retour", "Indietro"),
        ("policyPreferences.invalid_values", "Revisa valores, límites y modo de escalación.", "Check values, caps, and escalation mode.", "Vérifiez les valeurs, plafonds et le mode d’escalade.", "Controlla valori, limiti e modalità escalation."),
        ("policyPreferences.saved", "Preferencias guardadas.", "Preferences saved.", "Préférences enregistrées.", "Preferenze salvate."),
        ("policyPreferences.saved_but_invalid", "Los cambios no pasaron la validación de configuración.", "The changes did not pass configuration validation.", "Les modifications n’ont pas passé la validation de configuration.", "Le modifiche non hanno superato la convalida della configurazione."),
        ("policyPreferences.stale", "La configuración cambió; vuelve a cargar antes de guardar.", "Configuration changed; reload before saving.", "La configuration a changé ; rechargez avant d’enregistrer.", "La configurazione è cambiata; ricarica prima di salvare."),
        ("policyPreferences.save_failed", "No se pudieron guardar las preferencias.", "Preferences could not be saved.", "Impossible d’enregistrer les préférences.", "Impossibile salvare le preferenze.")
    ];
    private static readonly Dictionary<string, string>[] Catalog = Enumerable.Range(0, 4).Select(index =>
        Rows.ToDictionary(row => row.Key, row => index switch { 0 => row.Es, 1 => row.En, 2 => row.Fr, _ => row.It }, StringComparer.Ordinal)).ToArray();
    private static readonly string[] Locales = ["es", "en", "fr", "it"];
    public static IReadOnlyCollection<string> Keys => Catalog[0].Keys.ToArray();
    internal static bool ContainsKey(string key) => Catalog[0].ContainsKey(key);
    internal static bool TryTranslate(string locale, string key, out string text)
    {
        var index = Array.IndexOf(Locales, locale);
        if (index >= 0 && Catalog[index].TryGetValue(key, out text!)) return true;
        text = ""; return false;
    }
}
