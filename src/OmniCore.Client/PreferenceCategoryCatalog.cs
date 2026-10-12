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
        Category("interface"),
        Category("context_memory"),
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
        ["preferences.category.general.title"] = "General",
        ["preferences.category.general.description"] = "Idioma de interfaz y comportamiento general disponible.",
        ["preferences.category.general.help"] = "El idioma se guarda para el usuario; los idiomas de cobertura parcial se indican claramente.",
        ["preferences.category.accounts_connections.title"] = "Cuentas y conexiones",
        ["preferences.category.accounts_connections.description"] = "Cuentas por suscripción y conexiones de proveedores mediante API.",
        ["preferences.category.accounts_connections.help"] = "Ver el estado no inicia sesión ni prueba una conexión.",
        ["preferences.category.local_engines.title"] = "Motores locales",
        ["preferences.category.local_engines.description"] = "Servidores locales existentes y motores administrados por OmniCore.",
        ["preferences.category.local_engines.help"] = "Un motor administrado solo se controla mientras este runtime sea su propietario.",
        ["preferences.category.models_execution.title"] = "Modelos y ejecución",
        ["preferences.category.models_execution.description"] = "Modelos, rutas y preferencias para Runs futuros.",
        ["preferences.category.models_execution.help"] = "Los cambios de preferencias no modifican un Run que ya está en curso.",
        ["preferences.category.tools_security.title"] = "Herramientas y seguridad",
        ["preferences.category.tools_security.description"] = "Herramientas disponibles, permisos y confianza del workspace.",
        ["preferences.category.tools_security.help"] = "Consultar una capacidad no la activa ni concede permisos.",
        ["preferences.category.interface.title"] = "Interfaz",
        ["preferences.category.interface.description"] = "Panel lateral, widgets, indicadores y accesibilidad.",
        ["preferences.category.interface.help"] = "Los ajustes muestran su alcance y de dónde proviene el valor efectivo.",
        ["preferences.category.context_memory.title"] = "Contexto y memoria",
        ["preferences.category.context_memory.description"] = "Contexto de la sesión, uso medido y memoria con procedencia.",
        ["preferences.category.context_memory.help"] = "Los recuerdos son datos previos; puedes inspeccionarlos y olvidarlos.",
        ["preferences.category.extensions_skills.title"] = "Extensiones y skills",
        ["preferences.category.extensions_skills.description"] = "Extensiones del proyecto y skills activables para esta sesión.",
        ["preferences.category.extensions_skills.help"] = "La confianza del workspace y el consentimiento de capacidades son decisiones distintas.",
        ["preferences.category.diagnostics_data.title"] = "Diagnóstico y datos",
        ["preferences.category.diagnostics_data.description"] = "Diagnósticos, fuentes de configuración y mantenimiento disponible.",
        ["preferences.category.diagnostics_data.help"] = "Las acciones de mantenimiento explican sus efectos antes de ejecutarse.",
    };

    private static readonly Dictionary<string, string> _en = new(StringComparer.Ordinal)
    {
        ["preferences.category.general.title"] = "General",
        ["preferences.category.general.description"] = "Interface language and available general behavior.",
        ["preferences.category.general.help"] = "The language is saved for the user; partial language coverage is clearly identified.",
        ["preferences.category.accounts_connections.title"] = "Accounts and connections",
        ["preferences.category.accounts_connections.description"] = "Subscription accounts and API provider connections.",
        ["preferences.category.accounts_connections.help"] = "Viewing status does not sign in or test a connection.",
        ["preferences.category.local_engines.title"] = "Local engines",
        ["preferences.category.local_engines.description"] = "Existing local servers and engines managed by OmniCore.",
        ["preferences.category.local_engines.help"] = "A managed engine can only be controlled while this runtime owns it.",
        ["preferences.category.models_execution.title"] = "Models and execution",
        ["preferences.category.models_execution.description"] = "Models, routes, and preferences for future Runs.",
        ["preferences.category.models_execution.help"] = "Preference changes do not alter a Run already in progress.",
        ["preferences.category.tools_security.title"] = "Tools and security",
        ["preferences.category.tools_security.description"] = "Available tools, permissions, and workspace trust.",
        ["preferences.category.tools_security.help"] = "Viewing a capability does not activate it or grant permission.",
        ["preferences.category.interface.title"] = "Interface",
        ["preferences.category.interface.description"] = "Sidebar, widgets, indicators, and accessibility.",
        ["preferences.category.interface.help"] = "Settings show their scope and the source of the effective value.",
        ["preferences.category.context_memory.title"] = "Context and memory",
        ["preferences.category.context_memory.description"] = "Session context, measured usage, and memory with provenance.",
        ["preferences.category.context_memory.help"] = "Memories are prior data; you can inspect and forget them.",
        ["preferences.category.extensions_skills.title"] = "Extensions and skills",
        ["preferences.category.extensions_skills.description"] = "Project extensions and skills that can be activated for this session.",
        ["preferences.category.extensions_skills.help"] = "Workspace trust and capability consent are separate decisions.",
        ["preferences.category.diagnostics_data.title"] = "Diagnostics and data",
        ["preferences.category.diagnostics_data.description"] = "Diagnostics, configuration sources, and available maintenance.",
        ["preferences.category.diagnostics_data.help"] = "Maintenance actions explain their effects before they run.",
    };

    private static readonly Dictionary<string, string> _fr = new(StringComparer.Ordinal)
    {
        ["preferences.category.general.title"] = "Général",
        ["preferences.category.general.description"] = "Langue de l’interface et comportement général disponible.",
        ["preferences.category.general.help"] = "La langue est enregistrée pour l’utilisateur ; la couverture partielle est signalée clairement.",
        ["preferences.category.accounts_connections.title"] = "Comptes et connexions",
        ["preferences.category.accounts_connections.description"] = "Comptes par abonnement et connexions API aux fournisseurs.",
        ["preferences.category.accounts_connections.help"] = "Consulter l’état ne lance ni connexion ni test réseau.",
        ["preferences.category.local_engines.title"] = "Moteurs locaux",
        ["preferences.category.local_engines.description"] = "Serveurs locaux existants et moteurs gérés par OmniCore.",
        ["preferences.category.local_engines.help"] = "Un moteur géré n’est contrôlable que tant que ce runtime en est propriétaire.",
        ["preferences.category.models_execution.title"] = "Modèles et exécution",
        ["preferences.category.models_execution.description"] = "Modèles, routes et préférences pour les prochains Runs.",
        ["preferences.category.models_execution.help"] = "Une préférence ne modifie pas un Run déjà en cours.",
        ["preferences.category.tools_security.title"] = "Outils et sécurité",
        ["preferences.category.tools_security.description"] = "Outils disponibles, autorisations et confiance de l’espace de travail.",
        ["preferences.category.tools_security.help"] = "Consulter une capacité ne l’active pas et n’accorde aucune autorisation.",
        ["preferences.category.interface.title"] = "Interface",
        ["preferences.category.interface.description"] = "Barre latérale, widgets, indicateurs et accessibilité.",
        ["preferences.category.interface.help"] = "Chaque réglage indique sa portée et la source de sa valeur effective.",
        ["preferences.category.context_memory.title"] = "Contexte et mémoire",
        ["preferences.category.context_memory.description"] = "Contexte de la session, usage mesuré et mémoire avec provenance.",
        ["preferences.category.context_memory.help"] = "Les souvenirs sont des données antérieures que vous pouvez consulter et oublier.",
        ["preferences.category.extensions_skills.title"] = "Extensions et skills",
        ["preferences.category.extensions_skills.description"] = "Extensions du projet et skills activables pour cette session.",
        ["preferences.category.extensions_skills.help"] = "La confiance de l’espace de travail et le consentement aux capacités sont distincts.",
        ["preferences.category.diagnostics_data.title"] = "Diagnostic et données",
        ["preferences.category.diagnostics_data.description"] = "Diagnostics, sources de configuration et maintenance disponible.",
        ["preferences.category.diagnostics_data.help"] = "Les effets d’une action de maintenance sont expliqués avant son exécution.",
    };

    private static readonly Dictionary<string, string> _it = new(StringComparer.Ordinal)
    {
        ["preferences.category.general.title"] = "Generale",
        ["preferences.category.general.description"] = "Lingua dell’interfaccia e comportamento generale disponibile.",
        ["preferences.category.general.help"] = "La lingua viene salvata per l’utente; la copertura parziale è segnalata chiaramente.",
        ["preferences.category.accounts_connections.title"] = "Account e connessioni",
        ["preferences.category.accounts_connections.description"] = "Account in abbonamento e connessioni API ai provider.",
        ["preferences.category.accounts_connections.help"] = "Visualizzare lo stato non avvia l’accesso né una prova di connessione.",
        ["preferences.category.local_engines.title"] = "Motori locali",
        ["preferences.category.local_engines.description"] = "Server locali esistenti e motori gestiti da OmniCore.",
        ["preferences.category.local_engines.help"] = "Un motore gestito può essere controllato solo finché questo runtime ne è proprietario.",
        ["preferences.category.models_execution.title"] = "Modelli ed esecuzione",
        ["preferences.category.models_execution.description"] = "Modelli, percorsi e preferenze per i Run futuri.",
        ["preferences.category.models_execution.help"] = "Le preferenze non modificano un Run già in corso.",
        ["preferences.category.tools_security.title"] = "Strumenti e sicurezza",
        ["preferences.category.tools_security.description"] = "Strumenti disponibili, autorizzazioni e attendibilità del workspace.",
        ["preferences.category.tools_security.help"] = "Consultare una capacità non la attiva e non concede autorizzazioni.",
        ["preferences.category.interface.title"] = "Interfaccia",
        ["preferences.category.interface.description"] = "Barra laterale, widget, indicatori e accessibilità.",
        ["preferences.category.interface.help"] = "Le impostazioni mostrano il proprio ambito e l’origine del valore effettivo.",
        ["preferences.category.context_memory.title"] = "Contesto e memoria",
        ["preferences.category.context_memory.description"] = "Contesto della sessione, uso misurato e memoria con provenienza.",
        ["preferences.category.context_memory.help"] = "I ricordi sono dati precedenti che puoi consultare e dimenticare.",
        ["preferences.category.extensions_skills.title"] = "Estensioni e skill",
        ["preferences.category.extensions_skills.description"] = "Estensioni del progetto e skill attivabili per questa sessione.",
        ["preferences.category.extensions_skills.help"] = "L’attendibilità del workspace e il consenso alle capacità sono decisioni distinte.",
        ["preferences.category.diagnostics_data.title"] = "Diagnostica e dati",
        ["preferences.category.diagnostics_data.description"] = "Diagnostica, fonti di configurazione e manutenzione disponibile.",
        ["preferences.category.diagnostics_data.help"] = "Gli effetti delle azioni di manutenzione sono spiegati prima dell’esecuzione.",
    };
}
