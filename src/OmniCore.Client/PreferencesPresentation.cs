namespace OmniCore.Client;

/// <summary>Pure navigation helpers for the preference center.</summary>
public static class PreferencesPresentation
{
    public static bool IsWideLayout(int width) => width >= 100;

    public static IReadOnlyList<LocalizedPreferenceCategory> Search(
        IReadOnlyList<LocalizedPreferenceCategory> categories, string? query, string locale)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term)) return categories;
        var synonyms = Synonyms(locale);
        return categories.Where(category =>
            (category.Title + " " + category.Description + " " + category.Help).Contains(term,
                StringComparison.CurrentCultureIgnoreCase)
            || synonyms.GetValueOrDefault(category.Id, "").Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private static IReadOnlyDictionary<string, string> Synonyms(string locale) => locale switch
    {
        "en" => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general"] = "language locale", ["accounts_connections"] = "chatgpt claude api provider",
            ["local_engines"] = "local server managed runtime", ["models_execution"] = "model policy route run",
            ["tools_security"] = "tools permissions trust", ["interface"] = "sidebar widgets animation accessibility",
            ["context_memory"] = "context tokens usage memory", ["extensions_skills"] = "extensions skills commands",
            ["diagnostics_data"] = "diagnostics config maintenance",
        },
        "fr" => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general"] = "langue locale", ["accounts_connections"] = "compte chatgpt claude api fournisseur",
            ["local_engines"] = "serveur local moteur géré", ["models_execution"] = "modèle politique route run",
            ["tools_security"] = "outils autorisations confiance", ["interface"] = "barre latérale widgets animations accessibilité",
            ["context_memory"] = "contexte jetons usage mémoire", ["extensions_skills"] = "extensions skills commandes",
            ["diagnostics_data"] = "diagnostics configuration maintenance",
        },
        "it" => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general"] = "lingua locale", ["accounts_connections"] = "account chatgpt claude api provider",
            ["local_engines"] = "server locale motore gestito", ["models_execution"] = "modello criteri percorso run",
            ["tools_security"] = "strumenti autorizzazioni attendibilità", ["interface"] = "barra laterale widget animazioni accessibilità",
            ["context_memory"] = "contesto token utilizzo memoria", ["extensions_skills"] = "estensioni skills comandi",
            ["diagnostics_data"] = "diagnostica configurazione manutenzione",
        },
        _ => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general"] = "idioma lengua", ["accounts_connections"] = "cuenta chatgpt claude api proveedor",
            ["local_engines"] = "servidor local motor administrado", ["models_execution"] = "modelo políticas ruta ejecución run",
            ["tools_security"] = "herramientas permisos confianza", ["interface"] = "panel lateral widgets animaciones accesibilidad",
            ["context_memory"] = "contexto tokens uso memoria sesión", ["extensions_skills"] = "extensiones skills comandos",
            ["diagnostics_data"] = "diagnóstico configuración mantenimiento",
        },
    };
}
