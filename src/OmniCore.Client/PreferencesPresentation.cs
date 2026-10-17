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
            ["general"] = "language locale interface sidebar widgets animation accessibility", ["accounts_connections"] = "chatgpt claude api provider openrouter anthropic credits",
            ["local_engines"] = "local server managed runtime endpoint executable arguments directory startup timeout", ["models_execution"] = "model policy route run profile capabilities orchestration output limit budget quota",
            ["tools_security"] = "tools permissions trust",
            ["extensions_skills"] = "extensions skills commands hooks mcp aliases",
            ["context_sessions"] = "context tokens usage memory session",
            ["diagnostics_data"] = "diagnostics config maintenance doctor",
        },
        "fr" => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general"] = "langue locale interface barre latérale widgets animations accessibilité", ["accounts_connections"] = "compte chatgpt claude api fournisseur openrouter anthropic crédits",
            ["local_engines"] = "serveur local moteur géré endpoint exécutable arguments répertoire délai démarrage", ["models_execution"] = "modèle politique route run profil capacités orchestration limite sortie budget quota",
            ["tools_security"] = "outils autorisations confiance",
            ["extensions_skills"] = "extensions skills commandes hooks mcp alias",
            ["context_sessions"] = "contexte jetons usage mémoire session",
            ["diagnostics_data"] = "diagnostics configuration maintenance doctor",
        },
        "it" => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general"] = "lingua locale interfaccia barra laterale widget animazioni accessibilità", ["accounts_connections"] = "account chatgpt claude api provider openrouter anthropic credits",
            ["local_engines"] = "server locale motore gestito endpoint eseguibile argomenti directory timeout avvio", ["models_execution"] = "modello criteri percorso run profilo capacità orchestrazione limite output budget quota",
            ["tools_security"] = "strumenti autorizzazioni attendibilità",
            ["extensions_skills"] = "estensioni skills comandi hooks mcp alias",
            ["context_sessions"] = "contesto token utilizzo memoria sessione",
            ["diagnostics_data"] = "diagnostica configurazione manutenzione doctor",
        },
        _ => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general"] = "idioma lengua interfaz panel lateral widgets animaciones accesibilidad", ["accounts_connections"] = "cuenta chatgpt claude api proveedor openrouter anthropic créditos",
            ["local_engines"] = "servidor local motor administrado endpoint ejecutable argumentos directorio timeout arranque", ["models_execution"] = "modelo políticas ruta ejecución run perfil capacidades orquestación límite salida presupuesto cuota",
            ["tools_security"] = "herramientas permisos confianza",
            ["extensions_skills"] = "extensiones habilidades skills comandos automatizaciones hooks mcp alias",
            ["context_sessions"] = "contexto tokens uso memoria sesión",
            ["diagnostics_data"] = "diagnóstico configuración mantenimiento doctor",
        },
    };
}
