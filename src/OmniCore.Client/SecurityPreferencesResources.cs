namespace OmniCore.Client;

/// <summary>Localized copy for explicit User permission and Workspace trust administration.</summary>
public static class SecurityPreferencesResources
{
    private static readonly (string Key, string Es, string En, string Fr, string It)[] Rows =
    [
        ("securityPreferences.level_heading", "Nivel de autonomía en ACT", "Autonomy level in ACT", "Niveau d’autonomie en ACT", "Livello di autonomia in ACT"),
        ("securityPreferences.level_restrict", "Restringir", "Restrict", "Restreindre", "Limita"),
        ("securityPreferences.level_ask", "Preguntar", "Ask", "Demander", "Chiedi"),
        ("securityPreferences.level_free", "Libre", "Free", "Libre", "Libero"),
        ("securityPreferences.level_restrict_help", "Pide confirmación antes de escribir archivos y antes de compilar, probar o usar la red. Leer el workspace sigue permitido.", "Asks before writing files and before building, testing or using the network. Reading the workspace stays allowed.", "Demande avant d’écrire des fichiers et avant de compiler, tester ou utiliser le réseau. La lecture reste autorisée.", "Chiede prima di scrivere file e prima di compilare, testare o usare la rete. La lettura resta consentita."),
        ("securityPreferences.level_ask_help", "Escribe en el workspace sin preguntar; pide confirmación para compilar, probar y su red. Procesos externos, shell y escrituras fuera siempre preguntan.", "Writes in the workspace without asking; asks before building, testing and their network. External processes, shell and outside writes always ask.", "Écrit dans l’espace sans demander ; demande pour compiler, tester et leur réseau. Processus externes, shell et écritures externes demandent toujours.", "Scrive nello spazio senza chiedere; chiede per compilare, testare e la loro rete. Processi esterni, shell e scritture esterne chiedono sempre."),
        ("securityPreferences.level_free_help", "Predeterminado. Escribe, compila y prueba en el workspace sin preguntar. Procesos externos, shell, otra red y escrituras fuera siguen preguntando; los secretos nunca.", "Default. Writes, builds and tests in the workspace without asking. External processes, shell, other network and outside writes still ask; secrets never.", "Par défaut. Écrit, compile et teste sans demander. Processus externes, shell, autre réseau et écritures externes demandent encore ; secrets jamais.", "Predefinito. Scrive, compila e testa senza chiedere. Processi esterni, shell, altra rete e scritture esterne chiedono ancora; segreti mai."),
        ("securityPreferences.rules_heading", "Reglas por herramienta", "Per-tool rules", "Règles par outil", "Regole per strumento"),
        ("securityPreferences.tool_pick", "Elegir herramienta ›", "Pick tool ›", "Choisir l’outil ›", "Scegli strumento ›"),
        ("securityPreferences.decision_cycle", "Cambiar decisión ›", "Change decision ›", "Changer la décision ›", "Cambia decisione ›"),
        ("securityPreferences.decision_short", "Decisión", "Decision", "Décision", "Decisione"),
        ("securityPreferences.trust_heading", "Confianza del workspace", "Workspace trust", "Confiance de l’espace", "Attendibilità dello spazio"),
        ("securityPreferences.title", "Herramientas y seguridad", "Tools and security", "Outils et sécurité", "Strumenti e sicurezza"),
        ("securityPreferences.unavailable", "No se pudo leer el estado de seguridad.", "Security state could not be read.", "Impossible de lire l’état de sécurité.", "Impossibile leggere lo stato di sicurezza."),
        ("securityPreferences.scope_note", "Perfil y reglas: Usuario. Confianza: sólo esta área de trabajo.", "Profile and rules: User. Trust: this workspace only.", "Profil et règles : utilisateur. Confiance : cet espace uniquement.", "Profilo e regole: utente. Attendibilità: solo questa area di lavoro."),
        ("securityPreferences.profile", "Perfil de permisos", "Permission profile", "Profil d’autorisations", "Profilo autorizzazioni"),
        ("securityPreferences.profile_values", "Códigos válidos: Autonomous (Autónomo), Balanced (Equilibrado), Conservative (Conservador).", "Valid IDs: Autonomous, Balanced, Conservative.", "Identifiants admis : Autonomous (autonome), Balanced (équilibré), Conservative (prudent).", "ID validi: Autonomous (autonomo), Balanced (bilanciato), Conservative (conservatore)."),
        ("securityPreferences.trust_state", "Confianza del área", "Workspace trust", "Confiance de l’espace", "Attendibilità dell’area"),
        ("securityPreferences.trusted", "Confiable", "Trusted", "Approuvé", "Attendibile"),
        ("securityPreferences.untrusted", "No confiable", "Untrusted", "Non approuvé", "Non attendibile"),
        ("securityPreferences.decision.allow", "Permitir", "Allow", "Autoriser", "Consenti"),
        ("securityPreferences.decision.ask", "Preguntar", "Ask", "Demander", "Chiedi"),
        ("securityPreferences.decision.deny", "Denegar", "Deny", "Refuser", "Nega"),
        ("securityPreferences.tool_id", "ID de herramienta", "Exact tool ID", "ID exact de l’outil", "ID esatto dello strumento"),
        ("securityPreferences.decision", "Decisión: Permitir / Preguntar / Denegar", "Decision: Allow / Ask / Deny", "Décision : Autoriser / Demander / Refuser", "Decisione: Consenti / Chiedi / Nega"),
        ("securityPreferences.rule_note", "Las reglas sólo afectan herramientas identificadas por su ID exacto.", "Rules affect only tools identified by their exact ID.", "Les règles ne concernent que les outils identifiés par leur ID exact.", "Le regole riguardano solo strumenti identificati dal loro ID esatto."),
        ("securityPreferences.rule_add", "Preparar regla", "Draft rule", "Préparer une règle", "Prepara regola"),
        ("securityPreferences.save", "Guardar perfil y reglas", "Save profile and rules", "Enregistrer profil et règles", "Salva profilo e regole"),
        ("securityPreferences.invalid_decision", "Usa Permitir, Preguntar o Denegar.", "Use Allow, Ask, or Deny.", "Utilisez Autoriser, Demander ou Refuser.", "Usa Consenti, Chiedi o Nega."),
        ("securityPreferences.invalid_values", "El perfil o alguna regla no es válida.", "The profile or a rule is invalid.", "Le profil ou une règle est invalide.", "Il profilo o una regola non è valida."),
        ("securityPreferences.saved", "Preferencias guardadas.", "Preferences saved.", "Préférences enregistrées.", "Preferenze salvate."),
        ("securityPreferences.permissions_title", "Guardar permisos del usuario", "Save user permissions", "Enregistrer les autorisations utilisateur", "Salva autorizzazioni utente"),
        ("securityPreferences.permissions_confirm", "Confirma el perfil y las decisiones exactas. Afectarán permisos de Runs futuros; la configuración en curso no cambia.", "Confirm the profile and exact decisions. They affect permissions for future Runs; current configuration is unchanged.", "Confirmez le profil et les décisions exactes. Ils affecteront les autorisations des prochains Runs ; la configuration en cours reste inchangée.", "Conferma il profilo e le decisioni esatte. Influiranno sui permessi dei Run futuri; la configurazione corrente non cambia."),
        ("securityPreferences.stale", "El estado cambió; vuelve a cargar antes de actuar.", "State changed; reload before acting.", "L’état a changé ; rechargez avant d’agir.", "Lo stato è cambiato; ricarica prima di agire."),
        ("securityPreferences.save_failed", "No se pudieron guardar las preferencias.", "Preferences could not be saved.", "Impossible d’enregistrer les préférences.", "Impossibile salvare le preferenze."),
        ("securityPreferences.trust_title", "Cambiar confianza", "Change trust", "Modifier la confiance", "Modifica attendibilità"),
        ("securityPreferences.trust_confirm", "La decisión sólo aplica a esta área de trabajo y no concede consentimientos de herramientas.", "This decision applies only to this workspace and does not grant tool consent.", "Cette décision concerne cet espace uniquement et n’accorde aucun consentement aux outils.", "La decisione riguarda solo questa area di lavoro e non concede consensi agli strumenti."),
        ("securityPreferences.revoke_trust", "Revocar confianza…", "Revoke trust…", "Révoquer la confiance…", "Revoca attendibilità…"),
        ("securityPreferences.trust_workspace", "Confiar en esta área…", "Trust this workspace…", "Faire confiance à cet espace…", "Considera attendibile questa area…"),
        ("securityPreferences.grant_title", "Revocar consentimiento", "Revoke consent", "Révoquer le consentement", "Revoca consenso"),
        ("securityPreferences.grant_confirm", "Se eliminará esta autorización persistente para la herramienta:", "This persistent grant for the tool will be removed:", "Cette autorisation persistante pour l’outil sera supprimée :", "Questa autorizzazione persistente per lo strumento verrà rimossa:"),
        ("securityPreferences.revoke_grant", "Revocar autorización…", "Revoke grant…", "Révoquer l’autorisation…", "Revoca autorizzazione…"),
        ("securityPreferences.grants", "Autorizaciones de esta área", "Grants for this workspace", "Autorisations de cet espace", "Autorizzazioni di questa area"),
        ("securityPreferences.back", "Volver", "Back", "Retour", "Indietro"),
        ("securityPreferences.help", "Consultar no concede permisos. Aquí sólo puedes revocar autorizaciones existentes.", "Viewing does not grant permission. Only existing grants can be revoked here.", "Consulter n’accorde aucune autorisation. Ici, vous pouvez seulement révoquer les autorisations existantes.", "La consultazione non concede permessi. Qui puoi solo revocare autorizzazioni esistenti."),
        ("securityPreferences.cancel", "Cancelar", "Cancel", "Annuler", "Annulla"),
        ("securityPreferences.confirm", "Confirmar", "Confirm", "Confirmer", "Conferma")
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
