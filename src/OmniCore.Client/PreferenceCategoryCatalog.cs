namespace OmniCore.Client;

/// <summary>Stable category identity and copy keys for the preferences center.</summary>
public sealed record PreferenceCategoryDefinition(string Id, string TitleKey, string DescriptionKey, string HelpKey);

public sealed record LocalizedPreferenceCategory(string Id, string Title, string Description, string Help);

public static class PreferenceCategoryCatalog
{
    public static IReadOnlyList<PreferenceCategoryDefinition> All { get; } =
    [
        Category("general"),
        Category("accounts_connections"),
        Category("local_engines"),
        Category("models_execution"),
        Category("tools_security"),
        Category("context_sessions"),
        Category("extensions_skills"),
        Category("diagnostics_data"),
    ];

    public static IReadOnlyCollection<string> Keys => All
        .SelectMany(category => new[] { category.TitleKey, category.DescriptionKey, category.HelpKey }).ToArray();

    public static IReadOnlyList<LocalizedPreferenceCategory> Resolve(Localization localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return All.Select(category => new LocalizedPreferenceCategory(category.Id,
            localization.Resolve(category.TitleKey), localization.Resolve(category.DescriptionKey),
            localization.Resolve(category.HelpKey))).ToArray();
    }

    internal static bool ContainsKey(string key) => Keys.Contains(key, StringComparer.Ordinal);

    internal static bool TryTranslate(string locale, string key, out string text)
    {
        var resources = locale switch
        {
            "es" => _es,
            "en" => _en,
            "fr" => _fr,
            "it" => _it,
            _ => null,
        };
        if (resources is not null && resources.TryGetValue(key, out text!)) return true;
        text = "";
        return false;
    }

    private static PreferenceCategoryDefinition Category(string id) => new(id,
        "preferences.category." + id + ".title",
        "preferences.category." + id + ".description",
        "preferences.category." + id + ".help");

    private static readonly Dictionary<string, string> _es = new(StringComparer.Ordinal)
    {
        ["preferences.category.general.title"] = "General y apariencia",
        ["preferences.category.general.description"] = "Idioma, panel lateral, widgets y animaciones de carga. Configúrelas aquí o con /animation.",
        ["preferences.category.general.help"] = "El idioma se guarda para el usuario; los ajustes del panel lateral indican su alcance y su origen.",
        ["preferences.category.accounts_connections.title"] = "Cuentas y conexiones",
        ["preferences.category.accounts_connections.description"] = "Cuentas por suscripción y conexiones de proveedores mediante API.",
        ["preferences.category.accounts_connections.help"] = "Ver el estado no inicia sesión ni prueba una conexión.",
        ["preferences.category.local_engines.title"] = "Motores locales",
        ["preferences.category.local_engines.description"] = "Servidores locales existentes y motores administrados por OmniCore.",
        ["preferences.category.local_engines.help"] = "Un motor administrado solo se controla mientras este runtime sea su propietario.",
        ["preferences.category.models_execution.title"] = "Modelos y perfiles",
        ["preferences.category.models_execution.description"] = "Modelos, rutas y preferencias para Runs futuros.",
        ["preferences.category.models_execution.help"] = "Los cambios de preferencias no modifican un Run que ya está en curso.",
        ["preferences.category.tools_security.title"] = "Herramientas y seguridad",
        ["preferences.category.tools_security.description"] = "Herramientas disponibles, permisos y confianza del workspace.",
        ["preferences.category.tools_security.help"] = "Consultar una capacidad no la activa ni concede permisos.",
        ["preferences.category.extensions_skills.title"] = "Extensiones y habilidades",
        ["preferences.category.extensions_skills.description"] = "Extensiones del proyecto y habilidades activables para esta sesión.",
        ["preferences.category.extensions_skills.help"] = "La confianza del workspace y el consentimiento de capacidades son decisiones distintas.",
        ["preferences.category.context_sessions.title"] = "Contexto y sesiones",
        ["preferences.category.context_sessions.description"] = "Contexto, memoria y consumo medido de la sesión.",
        ["preferences.category.context_sessions.help"] = "Inspeccionar no hace inferencias ni cambia cuotas o memoria.",
        ["preferences.category.diagnostics_data.title"] = "Diagnóstico y mantenimiento",
        ["preferences.category.diagnostics_data.description"] = "Diagnósticos, fuentes de configuración y mantenimiento.",
        ["preferences.category.diagnostics_data.help"] = "Las acciones de mantenimiento explican sus efectos antes de ejecutarse.",
    };

    private static readonly Dictionary<string, string> _en = new(StringComparer.Ordinal)
    {
        ["preferences.category.general.title"] = "General and appearance",
        ["preferences.category.general.description"] = "Language, sidebar, widgets, and loading animations. Configure them here or with /animation.",
        ["preferences.category.general.help"] = "The language is saved for the user; sidebar settings show their scope and source.",
        ["preferences.category.accounts_connections.title"] = "Accounts and connections",
        ["preferences.category.accounts_connections.description"] = "Subscription accounts and API provider connections.",
        ["preferences.category.accounts_connections.help"] = "Viewing status does not sign in or test a connection.",
        ["preferences.category.local_engines.title"] = "Local engines",
        ["preferences.category.local_engines.description"] = "Existing local servers and engines managed by OmniCore.",
        ["preferences.category.local_engines.help"] = "A managed engine can only be controlled while this runtime owns it.",
        ["preferences.category.models_execution.title"] = "Models and profiles",
        ["preferences.category.models_execution.description"] = "Models, routes, and preferences for future Runs.",
        ["preferences.category.models_execution.help"] = "Preference changes do not alter a Run already in progress.",
        ["preferences.category.tools_security.title"] = "Tools and security",
        ["preferences.category.tools_security.description"] = "Available tools, permissions, and workspace trust.",
        ["preferences.category.tools_security.help"] = "Viewing a capability does not activate it or grant permission.",
        ["preferences.category.extensions_skills.title"] = "Extensions and skills",
        ["preferences.category.extensions_skills.description"] = "Project extensions and skills that can be activated for this session.",
        ["preferences.category.extensions_skills.help"] = "Workspace trust and capability consent are separate decisions.",
        ["preferences.category.context_sessions.title"] = "Context and sessions",
        ["preferences.category.context_sessions.description"] = "Session context, memory, and measured usage.",
        ["preferences.category.context_sessions.help"] = "Inspection does not run inference or change quotas or memory.",
        ["preferences.category.diagnostics_data.title"] = "Diagnostics and maintenance",
        ["preferences.category.diagnostics_data.description"] = "Diagnostics, configuration sources, and maintenance.",
        ["preferences.category.diagnostics_data.help"] = "Maintenance actions explain their effects before they run.",
    };

    private static readonly Dictionary<string, string> _fr = new(StringComparer.Ordinal)
    {
        ["preferences.category.general.title"] = "Général et apparence",
        ["preferences.category.general.description"] = "Langue, panneau latéral, widgets et animations de chargement. Configurez-les ici ou avec /animation.",
        ["preferences.category.general.help"] = "La langue est enregistrée pour l’utilisateur ; les réglages du panneau latéral indiquent leur portée et leur source.",
        ["preferences.category.accounts_connections.title"] = "Comptes et connexions",
        ["preferences.category.accounts_connections.description"] = "Comptes par abonnement et connexions API aux fournisseurs.",
        ["preferences.category.accounts_connections.help"] = "Consulter l’état ne lance ni connexion ni test réseau.",
        ["preferences.category.local_engines.title"] = "Moteurs locaux",
        ["preferences.category.local_engines.description"] = "Serveurs locaux existants et moteurs gérés par OmniCore.",
        ["preferences.category.local_engines.help"] = "Un moteur géré n’est contrôlable que tant que ce runtime en est propriétaire.",
        ["preferences.category.models_execution.title"] = "Modèles et profils",
        ["preferences.category.models_execution.description"] = "Modèles, routes et préférences pour les prochains Runs.",
        ["preferences.category.models_execution.help"] = "Une préférence ne modifie pas un Run déjà en cours.",
        ["preferences.category.tools_security.title"] = "Outils et sécurité",
        ["preferences.category.tools_security.description"] = "Outils disponibles, autorisations et confiance de l’espace de travail.",
        ["preferences.category.tools_security.help"] = "Consulter une capacité ne l’active pas et n’accorde aucune autorisation.",
        ["preferences.category.extensions_skills.title"] = "Extensions et skills",
        ["preferences.category.extensions_skills.description"] = "Extensions du projet et skills activables pour cette session.",
        ["preferences.category.extensions_skills.help"] = "La confiance de l’espace de travail et le consentement aux capacités sont distincts.",
        ["preferences.category.context_sessions.title"] = "Contexte et sessions",
        ["preferences.category.context_sessions.description"] = "Contexte, mémoire et usage mesuré de la session.",
        ["preferences.category.context_sessions.help"] = "L’inspection ne lance aucune inférence et ne change ni quotas ni mémoire.",
        ["preferences.category.diagnostics_data.title"] = "Diagnostic et maintenance",
        ["preferences.category.diagnostics_data.description"] = "Diagnostics, sources de configuration et maintenance.",
        ["preferences.category.diagnostics_data.help"] = "Les effets d’une action de maintenance sont expliqués avant son exécution.",
    };

    private static readonly Dictionary<string, string> _it = new(StringComparer.Ordinal)
    {
        ["preferences.category.general.title"] = "Generale e aspetto",
        ["preferences.category.general.description"] = "Lingua, pannello laterale, widget e animazioni di caricamento. Configurale qui o con /animation.",
        ["preferences.category.general.help"] = "La lingua viene salvata per l’utente; le impostazioni del pannello laterale indicano ambito e origine.",
        ["preferences.category.accounts_connections.title"] = "Account e connessioni",
        ["preferences.category.accounts_connections.description"] = "Account in abbonamento e connessioni API ai provider.",
        ["preferences.category.accounts_connections.help"] = "Visualizzare lo stato non avvia l’accesso né una prova di connessione.",
        ["preferences.category.local_engines.title"] = "Motori locali",
        ["preferences.category.local_engines.description"] = "Server locali esistenti e motori gestiti da OmniCore.",
        ["preferences.category.local_engines.help"] = "Un motore gestito può essere controllato solo finché questo runtime ne è proprietario.",
        ["preferences.category.models_execution.title"] = "Modelli e profili",
        ["preferences.category.models_execution.description"] = "Modelli, percorsi e preferenze per i Run futuri.",
        ["preferences.category.models_execution.help"] = "Le preferenze non modificano un Run già in corso.",
        ["preferences.category.tools_security.title"] = "Strumenti e sicurezza",
        ["preferences.category.tools_security.description"] = "Strumenti disponibili, autorizzazioni e attendibilità del workspace.",
        ["preferences.category.tools_security.help"] = "Consultare una capacità non la attiva e non concede autorizzazioni.",
        ["preferences.category.extensions_skills.title"] = "Estensioni e skill",
        ["preferences.category.extensions_skills.description"] = "Estensioni del progetto e skill attivabili per questa sessione.",
        ["preferences.category.extensions_skills.help"] = "L’attendibilità del workspace e il consenso alle capacità sono decisioni distinte.",
        ["preferences.category.context_sessions.title"] = "Contesto e sessioni",
        ["preferences.category.context_sessions.description"] = "Contesto, memoria e uso misurato della sessione.",
        ["preferences.category.context_sessions.help"] = "L’ispezione non esegue inferenze e non cambia quote o memoria.",
        ["preferences.category.diagnostics_data.title"] = "Diagnostica e manutenzione",
        ["preferences.category.diagnostics_data.description"] = "Diagnostica, fonti di configurazione e manutenzione.",
        ["preferences.category.diagnostics_data.help"] = "Gli effetti delle azioni di manutenzione sono spiegati prima dell’esecuzione.",
    };
}
