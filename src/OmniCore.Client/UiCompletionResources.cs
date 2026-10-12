namespace OmniCore.Client;

/// <summary>
/// Supplemental labels for presentation values whose wire/configuration identifiers must stay intact.
/// Unknown provider, plugin, and policy values are intentionally not translated.
/// </summary>
public static class UiCompletionResources
{
    private static readonly (string Key, string Es, string En, string Fr, string It)[] Rows =
    [
        ("uiCompletion.scope.user", "Usuario", "User", "Utilisateur", "Utente"),
        ("uiCompletion.scope.workspace", "Área de trabajo", "Workspace", "Espace de travail", "Area di lavoro"),
        ("uiCompletion.scope.session", "Sesión", "Session", "Session", "Sessione"),
        ("uiCompletion.scope.project", "Proyecto", "Project", "Projet", "Progetto"),
        ("uiCompletion.scope.builtIn", "Integrado", "Built-in", "Intégré", "Integrato"),
        ("uiCompletion.source.user", "Usuario", "User", "Utilisateur", "Utente"),
        ("uiCompletion.source.workspace", "Área de trabajo", "Workspace", "Espace de travail", "Area di lavoro"),
        ("uiCompletion.source.project", "Proyecto", "Project", "Projet", "Progetto"),
        ("uiCompletion.source.builtIn", "Integrado", "Built-in", "Intégré", "Integrato"),
        ("uiCompletion.source.default", "Predeterminado", "Default", "Par défaut", "Predefinito"),
        ("uiCompletion.lifetime.run", "Ejecución", "Run", "Exécution", "Esecuzione"),
        ("uiCompletion.lifetime.session", "Sesión", "Session", "Session", "Sessione"),
        ("uiCompletion.lifetime.once", "Una vez", "Once", "Une fois", "Una volta"),
        ("uiCompletion.lifetime.workspace", "Área de trabajo", "Workspace", "Espace de travail", "Area di lavoro"),
        ("uiCompletion.maintenance.sessionPurge", "limpieza de sesiones", "session cleanup", "nettoyage des sessions", "pulizia sessioni"),
        ("uiCompletion.maintenance.auditRetention", "retención de auditoría", "audit retention", "conservation de l’audit", "conservazione audit"),
        ("uiCompletion.security.profileAutonomous", "Autónomo", "Autonomous", "Autonome", "Autonomo"),
        ("uiCompletion.security.profileBalanced", "Equilibrado", "Balanced", "Équilibré", "Bilanciato"),
        ("uiCompletion.security.profileConservative", "Conservador", "Conservative", "Prudent", "Conservatore"),
        ("uiCompletion.security.decisionAllow", "Permitir", "Allow", "Autoriser", "Consenti"),
        ("uiCompletion.security.decisionAsk", "Preguntar", "Ask", "Demander", "Chiedi"),
        ("uiCompletion.security.decisionDeny", "Denegar", "Deny", "Refuser", "Nega"),
        ("uiCompletion.effect.none", "Sin efectos secundarios", "No side effects", "Aucun effet secondaire", "Nessun effetto collaterale"),
        ("uiCompletion.effect.rerunnable", "Repetible", "Rerunnable", "Réexécutable", "Ripetibile"),
        ("uiCompletion.effect.reconcilable", "Conciliable", "Reconcilable", "Réconciliable", "Riconciliabile"),
        ("uiCompletion.effect.nonIdempotent", "No idempotente", "Non-idempotent", "Non idempotent", "Non idempotente"),
        ("uiCompletion.toolMode.direct", "Directo", "Direct", "Direct", "Diretto"),
        ("uiCompletion.toolMode.discovered", "Descubierto", "Discovered", "Découvert", "Rilevato"),
        ("uiCompletion.toolMode.code", "Código", "Code", "Code", "Codice"),
        ("uiCompletion.external.originalName", "Nombre original sin traducir", "Original untranslated name", "Nom d’origine non traduit", "Nome originale non tradotto"),
        ("uiCompletion.external.originalDescription", "Descripción original sin traducir", "Original untranslated description", "Description d’origine non traduite", "Descrizione originale non tradotta"),
        ("uiCompletion.usage.session", "sesión", "session", "session", "sessione"),
        ("uiCompletion.usage.tokenAbbreviation", "tok", "tok", "jet.", "tok"),
        ("uiCompletion.usage.credits", "créditos", "credits", "crédits", "crediti"),
        ("uiCompletion.usage.remaining", "restantes", "remaining", "restants", "rimanenti"),
        ("uiCompletion.usage.stale", "dato anterior", "stale", "ancienne mesure", "dato precedente"),
        ("uiCompletion.usage.quota", "cuota", "quota", "quota", "quota"),
        ("uiCompletion.usage.accountBalance", "saldo de la cuenta", "account balance", "solde du compte", "saldo del conto"),
        ("uiCompletion.usage.keyLimit", "límite de la clave", "key limit", "limite de la clé", "limite della chiave"),
        ("uiCompletion.usage.tokenLimit", "límite de tokens", "token allowance", "plafond de jetons", "limite di token"),
        ("uiCompletion.command.coreSource", "Integrado en OmniCore", "Built into OmniCore", "Intégré à OmniCore", "Integrato in OmniCore"),
        ("uiCompletion.contextPriority.pinned", "Fijada", "Pinned", "Épinglé", "Fissato"),
        ("uiCompletion.contextPriority.high", "Alta", "High", "Élevée", "Alta"),
        ("uiCompletion.contextPriority.normal", "Normal", "Normal", "Normale", "Normale"),
        ("uiCompletion.contextPriority.low", "Baja", "Low", "Faible", "Bassa"),
    ];

    private static readonly string[] Locales = ["es", "en", "fr", "it"];
    private static readonly Dictionary<string, string>[] Catalog = Enumerable.Range(0, Locales.Length)
        .Select(index => Rows.ToDictionary(row => row.Key,
            row => index switch { 0 => row.Es, 1 => row.En, 2 => row.Fr, _ => row.It }, StringComparer.Ordinal))
        .ToArray();

    public static IReadOnlyCollection<string> Keys => Catalog[0].Keys.ToArray();
    internal static bool ContainsKey(string key) => Catalog[0].ContainsKey(key);

    internal static bool TryTranslate(string locale, string key, out string text)
    {
        var index = Array.IndexOf(Locales, locale);
        if (index >= 0 && Catalog[index].TryGetValue(key, out text!)) return true;
        text = "";
        return false;
    }

    /// <summary>Maps only known presentation enums; leaves unknown wire values untouched.</summary>
    public static string EnumLabel(string group, string value, string locale)
    {
        var canonical = value.Trim();
        var suffix = (group, canonical.ToLowerInvariant()) switch
        {
            ("scope", "user") => "scope.user",
            ("scope", "workspace") => "scope.workspace",
            ("scope", "session") => "scope.session",
            ("scope", "project") => "scope.project",
            ("scope", "builtin" or "built-in") => "scope.builtIn",
            ("source", "user" or "usersetting") => "source.user",
            ("source", "workspace") => "source.workspace",
            ("source", "project") => "source.project",
            ("source", "builtin" or "built-in") => "source.builtIn",
            ("source", "default") => "source.default",
            ("lifetime", "run") => "lifetime.run",
            ("lifetime", "session") => "lifetime.session",
            ("lifetime", "once") => "lifetime.once",
            ("lifetime", "workspace") => "lifetime.workspace",
            ("security.profile", "autonomous") => "security.profileAutonomous",
            ("security.profile", "balanced") => "security.profileBalanced",
            ("security.profile", "conservative") => "security.profileConservative",
            ("security.decision", "allow") => "security.decisionAllow",
            ("security.decision", "ask") => "security.decisionAsk",
            ("security.decision", "deny") => "security.decisionDeny",
            ("effect", "none") => "effect.none",
            ("effect", "rerunnable") => "effect.rerunnable",
            ("effect", "reconcilable") => "effect.reconcilable",
            ("effect", "nonidempotent") => "effect.nonIdempotent",
            ("toolmode", "direct") => "toolMode.direct",
            ("toolmode", "discovered") => "toolMode.discovered",
            ("toolmode", "code") => "toolMode.code",
            ("contextpriority", "pinned") => "contextPriority.pinned",
            ("contextpriority", "high") => "contextPriority.high",
            ("contextpriority", "normal") => "contextPriority.normal",
            ("contextpriority", "low") => "contextPriority.low",
            ("maintenance", "session_purge") => "maintenance.sessionPurge",
            ("maintenance", "audit_retention") => "maintenance.auditRetention",
            _ => "",
        };
        return suffix.Length > 0 && TryTranslate(locale, "uiCompletion." + suffix, out var translated)
            ? translated : value;
    }
}
