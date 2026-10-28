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
        ("securityPreferences.session_ceiling", "Techo de esta sesión", "This Session ceiling", "Plafond de cette Session", "Limite di questa Sessione"),
        ("securityPreferences.default_ceiling", "Techo para sesiones nuevas", "Ceiling for new Sessions", "Plafond des nouvelles Sessions", "Limite per nuove Sessioni"),
        ("securityPreferences.policy_revision", "Revisión", "Revision", "Révision", "Revisione"),
        ("securityPreferences.provenance", "Procedencia", "Provenance", "Provenance", "Provenienza"),
        ("securityPreferences.provenance.Native", "Creada aquí", "Created here", "Créée ici", "Creata qui"),
        ("securityPreferences.provenance.Imported", "Importada", "Imported", "Importée", "Importata"),
        ("securityPreferences.provenance.LegacyMigrated", "Sesión anterior protegida", "Protected legacy Session", "Ancienne Session protégée", "Sessione precedente protetta"),
        ("securityPreferences.category.ObserveOnly", "Sólo observación", "Observe only", "Observation uniquement", "Solo osservazione"),
        ("securityPreferences.category.PatchOnly", "Sólo cambios puntuales", "Patch only", "Correctifs uniquement", "Solo patch"),
        ("securityPreferences.category.ScopedCoder", "Codificación acotada", "Scoped coding", "Codage limité", "Codifica circoscritta"),
        ("securityPreferences.category.FullAgent", "Agente completo", "Full agent", "Agent complet", "Agente completo"),
        ("securityPreferences.session_security_unavailable", "No hay una política durable disponible para esta sesión.", "No durable policy is available for this Session.", "Aucune politique durable n’est disponible pour cette Session.", "Nessuna policy durevole disponibile per questa Sessione."),
        ("securityPreferences.session_security_no_session", "Abre una sesión para consultar su techo.", "Open a Session to inspect its ceiling.", "Ouvrez une Session pour consulter son plafond.", "Apri una Sessione per consultare il limite."),
        ("securityPreferences.session_security_default_unconfigured", "Se usará este valor conservador hasta guardar un predeterminado.", "This conservative value applies until a default is saved.", "Cette valeur prudente s’applique jusqu’à l’enregistrement d’un plafond par défaut.", "Questo valore prudente si applica finché non viene salvato un limite predefinito."),
        ("securityPreferences.session_security_confirm", "Cambiará el techo durable de esta sesión. Los permisos del usuario, las autorizaciones existentes y el sandbox siguen aplicándose.", "This changes the durable ceiling for this Session. User permissions, existing grants, and the sandbox still apply.", "Le plafond durable de cette Session changera. Les permissions utilisateur, autorisations existantes et le sandbox restent appliqués.", "Cambierà il limite durevole di questa Sessione. Permessi utente, autorizzazioni esistenti e sandbox restano attivi."),
        ("securityPreferences.session_security_change", "Cambiar techo de sesión ›", "Change Session ceiling ›", "Modifier le plafond de Session ›", "Cambia limite Sessione ›"),
        ("securityPreferences.default_security_confirm", "Cambiará el techo predeterminado de sesiones nuevas; no modifica sesiones existentes ni concede permisos.", "This changes the default ceiling for new Sessions; it does not change existing Sessions or grant permission.", "Le plafond par défaut des nouvelles Sessions changera ; les Sessions existantes ne changent pas et aucun droit n’est accordé.", "Cambierà il limite predefinito per nuove Sessioni; non modifica quelle esistenti né concede permessi."),
        ("securityPreferences.default_security_change", "Cambiar predeterminado ›", "Change default ›", "Modifier le plafond par défaut ›", "Cambia predefinito ›"),
        ("securityPreferences.session_security_saved", "El techo de la sesión quedó actualizado.", "The Session ceiling was updated.", "Le plafond de la Session a été mis à jour.", "Il limite della Sessione è stato aggiornato."),
        ("securityPreferences.default_security_saved", "El techo predeterminado quedó actualizado.", "The default ceiling was updated.", "Le plafond par défaut a été mis à jour.", "Il limite predefinito è stato aggiornato."),
        ("session_security.stale_revision", "La política de sesión cambió. Vuelve a cargar antes de actuar.", "Session policy changed. Reload before acting.", "La politique de Session a changé. Rechargez avant d’agir.", "La policy della Sessione è cambiata. Ricarica prima di agire."),
        ("session_security.recovery_blocked", "No se pudo recuperar la política de seguridad de esta sesión. No se ejecutó la acción.", "This Session's security policy could not be recovered. The action was not run.", "Impossible de récupérer la politique de sécurité de cette Session. L’action n’a pas été exécutée.", "Impossibile recuperare la policy di sicurezza di questa Sessione. L’azione non è stata eseguita."),
        ("session_security.session_scope_changed", "La sesión activa cambió. Vuelve a cargar antes de enviar.", "The active Session changed. Reload before sending.", "La Session active a changé. Rechargez avant d’envoyer.", "La Sessione attiva è cambiata. Ricarica prima di inviare."),
        ("session_security.approval_required", "La política de sesión requiere una nueva aprobación para esta acción.", "Session policy requires fresh approval for this action.", "La politique de Session exige une nouvelle approbation pour cette action.", "La policy della Sessione richiede una nuova approvazione per questa azione."),
        ("session_security.execution_in_flight", "Espera a que termine la ejecución antes de cambiar el techo de sesión.", "Wait for execution to finish before changing the Session ceiling.", "Attendez la fin de l’exécution avant de modifier le plafond de Session.", "Attendi il termine dell’esecuzione prima di cambiare il limite della Sessione."),
        ("session_security.run_requires_tightening", "Durante un Run activo sólo puedes reducir el techo de sesión.", "During an active Run, the Session ceiling can only be tightened.", "Pendant un Run actif, le plafond de Session ne peut qu’être resserré.", "Durante un Run attivo, il limite della Sessione può solo essere ridotto."),
        ("session_security.shell_ceiling_refused", "El techo de seguridad de esta sesión ({category}) no permite ejecutar comandos con `!`. Cámbialo a FullAgent en F4 → Herramientas y seguridad y vuelve a intentarlo.", "The security ceiling for this session ({category}) does not allow `!` commands. Set it to FullAgent in F4 → Tools and security and try again.", "Le plafond de sécurité de cette session ({category}) n’autorise pas les commandes `!`. Passez-le à FullAgent dans F4 → Outils et sécurité et réessayez.", "Il limite di sicurezza di questa sessione ({category}) non consente i comandi `!`. Impostalo su FullAgent in F4 → Strumenti e sicurezza e riprova."),
        ("session_security.default_update_failed", "No se pudo guardar el predeterminado de seguridad.", "Could not save the security default.", "Impossible d’enregistrer le plafond de sécurité par défaut.", "Impossibile salvare il limite di sicurezza predefinito."),
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
