namespace OmniCore.Client;

/// <summary>Copy for the editable models/execution preferences surface.</summary>
public static class PolicyPreferencesResources
{
    private static readonly (string Key, string Es, string En, string Fr, string It)[] Rows =
    [
        ("policyPreferences.mapping_source", "Herramienta original", "Original tool", "Outil d’origine", "Strumento originale"),
        ("policyPreferences.mapping_alias", "Nombre del alias", "Alias name", "Nom de l’alias", "Nome alias"),
        ("policyPreferences.mapping_target", "Destino", "Target", "Cible", "Destinazione"),
        ("policyPreferences.mapping_apply", "Añadir o actualizar", "Add or update", "Ajouter ou modifier", "Aggiungi o aggiorna"),
        ("policyPreferences.mapping_remove", "Quitar selección", "Remove selection", "Supprimer la sélection", "Rimuovi selezione"),
        ("policyPreferences.mapping_help", "Edita una fila por vez. Guardar aplica los cambios; Volver los descarta.", "Edit one row at a time. Save applies changes; Back discards them.", "Modifiez une ligne à la fois. Enregistrer applique les changements ; Retour les annule.", "Modifica una riga alla volta. Salva applica le modifiche; Indietro le annulla."),
        ("policyPreferences.mapping_pending", "Cambios sin guardar.", "Unsaved changes.", "Modifications non enregistrées.", "Modifiche non salvate."),
        ("policyPreferences.mapping_empty", "No hay filas configuradas.", "No rows configured.", "Aucune ligne configurée.", "Nessuna riga configurata."),
        ("policyPreferences.service.hooks", "Automatizaciones del proyecto", "Project hooks", "Automatisations du projet", "Automazioni del progetto"),
        ("policyPreferences.service.servers", "Servidores", "Servers", "Serveurs", "Server"),
        ("policyPreferences.service.transport", "Transporte", "Transport", "Transport", "Trasporto"),
        ("policyPreferences.service.identity", "Identidad", "Identity", "Identité", "Identità"),
        ("policyPreferences.service.endpoint", "Dirección", "Address", "Adresse", "Indirizzo"),
        ("policyPreferences.service.enabled", "Activado", "Enabled", "Activé", "Attivato"),
        ("policyPreferences.service.hookPoint", "Momento de ejecución", "Execution point", "Point d’exécution", "Punto di esecuzione"),
        ("policyPreferences.service.capabilities", "Capacidades", "Capabilities", "Capacités", "Capacità"),
        ("policyPreferences.service.extensionId", "Extensión", "Extension", "Extension", "Estensione"),
        ("policyPreferences.service.yes", "Sí", "Yes", "Oui", "Sì"),
        ("policyPreferences.service.no", "No", "No", "Non", "No"),
        ("policyPreferences.service.point", "Momento de ejecución", "Execution point", "Point d’exécution", "Punto di esecuzione"),
        ("policyPreferences.service.requestedCapabilities", "Capacidades solicitadas", "Requested capabilities", "Capacités demandées", "Capacità richieste"),
        ("policyPreferences.service.consentedCapabilities", "Capacidades autorizadas", "Consented capabilities", "Capacités autorisées", "Capacità autorizzate"),
        ("policyPreferences.service.state.Ready", "Disponible", "Ready", "Disponible", "Disponibile"),
        ("policyPreferences.service.state.Untrusted", "Sin confianza", "Untrusted", "Non approuvé", "Non attendibile"),

        ("policyPreferences.section_defaults", "Modelo por defecto y límites de gasto", "Default model and spending caps", "Modèle par défaut et plafonds de dépense", "Modello predefinito e limiti di spesa"),
        ("policyPreferences.section_routes", "Rutas por tipo de trabajo", "Routes by kind of work", "Routes par type de travail", "Percorsi per tipo di lavoro"),
        ("policyPreferences.section_escalation", "Escalación a un modelo más capaz", "Escalation to a more capable model", "Escalade vers un modèle plus capable", "Escalation a un modello più capace"),
        ("policyPreferences.section_command_aliases", "Alias de comandos del usuario", "User command aliases", "Alias de commandes utilisateur", "Alias comandi utente"),
        ("policyPreferences.command_aliases", "alias=command canónico separados por coma", "alias=canonical command pairs, comma separated", "alias=commande canonique, séparés par des virgules", "coppie alias=comando canonico separate da virgole"),
        ("policyPreferences.save_aliases", "Guardar alias", "Save aliases", "Enregistrer les alias", "Salva alias"),
        ("policyPreferences.invalid_command_aliases", "Revisa alias, destino y colisiones con comandos reservados.", "Check aliases, targets, and reserved command collisions.", "Vérifiez les alias, les cibles et les collisions avec les commandes réservées.", "Controlla alias, destinazioni e collisioni con comandi riservati."),
        ("policyPreferences.section_tool_preferences", "Preferencias de herramientas", "Tool preferences", "Préférences des outils", "Preferenze strumenti"),
        ("policyPreferences.tool_preferences", "toolId=alternativa; coma: sólo alternativas elegibles. Las sustituciones protegidas piden autorización al usarse.", "toolId=alternative; comma separated: only eligible alternatives. Protected substitutions ask when used.", "toolId=alternative, séparés par des virgules : alternatives éligibles uniquement. Les substitutions protégées demandent une autorisation à l’usage.", "toolId=alternativa, separati da virgole: solo alternative idonee. Le sostituzioni protette chiedono l’autorizzazione quando usate."),
        ("policyPreferences.save_tool_preferences", "Guardar preferencias", "Save tool preferences", "Enregistrer les préférences", "Salva preferenze"),
        ("policyPreferences.invalid_tool_preferences", "Revisa los IDs canónicos de origen y alternativa.", "Check canonical source and alternative IDs.", "Vérifiez les identifiants canoniques de la source et de l’alternative.", "Controlla gli ID canonici di origine e alternativa."),
        ("policyPreferences.help.default_model", "Alias del modelo que se usa cuando ninguna ruta aplica. Vacío: el predeterminado del catálogo. Ver F3 o Catálogo de modelos para los alias.", "Model alias used when no route applies. Empty: catalog default. See F3 or the model catalog for aliases.", "Alias du modèle utilisé quand aucune route ne s’applique. Vide : défaut du catalogue. Voir F3 ou le catalogue.", "Alias del modello usato quando nessun percorso si applica. Vuoto: predefinito del catalogo. Vedi F3 o il catalogo."),
        ("policyPreferences.help.session_cap", "Gasto máximo en USD por sesión con modelos de pago por uso. Al alcanzarlo, el Run se detiene y te pide autorizar más gasto. Vacío: 5 USD.", "Maximum USD spend per session on metered models. When reached, the Run pauses and asks you to authorize more. Empty: 5 USD.", "Dépense maximale en USD par session sur les modèles à l’usage. Atteinte : le Run s’arrête et demande d’autoriser plus. Vide : 5 USD.", "Spesa massima in USD per sessione sui modelli a consumo. Raggiunta: il Run si ferma e chiede di autorizzare altra spesa. Vuoto: 5 USD."),
        ("policyPreferences.help.daily_cap", "Gasto máximo en USD por día sumando todas las sesiones; al alcanzarlo pide autorización. Vacío: 20 USD. Modelos locales y suscripciones no gastan USD.", "Maximum USD spend per day across sessions; asks for authorization when reached. Empty: 20 USD. Local models and subscriptions spend no USD.", "Dépense maximale en USD par jour, toutes sessions ; demande une autorisation une fois atteinte. Vide : 20 USD. Modèles locaux et abonnements ne coûtent pas d’USD.", "Spesa massima in USD al giorno su tutte le sessioni; raggiunta chiede autorizzazione. Vuoto: 20 USD. Modelli locali e abbonamenti non spendono USD."),
        ("policyPreferences.help.route_meta", "Modelos para tareas internas cortas (títulos, resúmenes). Hoy ningún flujo la consulta: reservada.", "Models for short internal tasks (titles, summaries). No flow reads it yet: reserved.", "Modèles pour tâches internes courtes (titres, résumés). Aucun flux ne la lit encore : réservée.", "Modelli per brevi attività interne (titoli, riepiloghi). Nessun flusso la usa ancora: riservata."),
        ("policyPreferences.help.route_exploration", "Modelos para PLAN y exploración de solo lectura. Se prueban en orden; el primero disponible gana. Vacío: modelo por defecto.", "Models for PLAN and read-only exploration. Tried in order; first available wins. Empty: default model.", "Modèles pour PLAN et l’exploration en lecture seule. Essayés dans l’ordre ; le premier disponible gagne. Vide : modèle par défaut.", "Modelli per PLAN e l’esplorazione in sola lettura. Provati in ordine; vince il primo disponibile. Vuoto: modello predefinito."),
        ("policyPreferences.help.route_implementation", "Modelos para ACT (editar archivos, ejecutar herramientas). Se prueban en orden. Vacío: modelo por defecto.", "Models for ACT (editing files, running tools). Tried in order. Empty: default model.", "Modèles pour ACT (modifier des fichiers, exécuter des outils). Essayés dans l’ordre. Vide : modèle par défaut.", "Modelli per ACT (modificare file, eseguire strumenti). Provati in ordine. Vuoto: modello predefinito."),
        ("policyPreferences.help.route_reasoning", "Modelos para razonamiento profundo. Hoy ningún flujo la consulta: reservada.", "Models for deep reasoning. No flow reads it yet: reserved.", "Modèles pour le raisonnement approfondi. Aucun flux ne la lit encore : réservée.", "Modelli per ragionamento approfondito. Nessun flusso la usa ancora: riservata."),
        ("policyPreferences.help.route_architecture", "Modelos para decisiones de arquitectura. Hoy ningún flujo la consulta: reservada.", "Models for architecture decisions. No flow reads it yet: reserved.", "Modèles pour les décisions d’architecture. Aucun flux ne la lit encore : réservée.", "Modelli per decisioni di architettura. Nessun flusso la usa ancora: riservata."),
        ("policyPreferences.help.prefer_local", "true: entre los modelos válidos de la ruta, los locales van primero. false o vacío: se respeta el orden escrito en la ruta.", "true: among the route’s valid models, local ones go first. false or empty: the route’s written order is kept.", "true : parmi les modèles valides de la route, les locaux passent d’abord. false ou vide : l’ordre écrit est conservé.", "true: tra i modelli validi del percorso, i locali vanno per primi. false o vuoto: si mantiene l’ordine scritto."),
        ("policyPreferences.help.escalation_mode", "ask: pide tu confirmación antes de cambiar a un modelo más capaz cuando el Run se atasca. auto: escala sin preguntar, siempre dentro de rutas y límites.", "ask: confirm before switching to a more capable model when the Run stalls. auto: escalate without asking, always within routes and caps.", "ask : confirmer avant de passer à un modèle plus capable si le Run bloque. auto : escalade sans demander, toujours dans les routes et plafonds.", "ask: conferma prima di passare a un modello più capace se il Run si blocca. auto: escala senza chiedere, sempre entro percorsi e limiti."),
        ("policyPreferences.help.escalation_chain", "Alias en orden de escalación, del menos al más capaz. Vacío: no hay escalación de modelo.", "Aliases in escalation order, least to most capable. Empty: no model escalation.", "Alias dans l’ordre d’escalade, du moins au plus capable. Vide : pas d’escalade de modèle.", "Alias in ordine di escalation, dal meno al più capace. Vuoto: nessuna escalation del modello."),
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
