namespace OmniCore.Client;

public static class CommandPaletteResources
{
    private static readonly Dictionary<string, string[]> Values = new(StringComparer.Ordinal)
    {
        ["title"] = ["Paleta de comandos", "Command palette", "Palette de commandes", "Tavolozza dei comandi"],
        ["help"] = ["Buscar · ↑↓ elegir · Enter ejecutar · Esc volver al borrador", "Search · ↑↓ choose · Enter execute · Esc return to draft", "Rechercher · ↑↓ choisir · Entrée exécuter · Échap revenir au brouillon", "Cerca · ↑↓ scegli · Invio esegui · Esc torna alla bozza"],
        ["search"] = ["Buscar", "Search", "Rechercher", "Cerca"],
        ["arguments"] = ["Argumentos", "Arguments", "Arguments", "Argomenti"],
        ["shortcut"] = ["Atajo", "Shortcut", "Raccourci", "Scorciatoia"],
        ["source"] = ["Fuente", "Source", "Source", "Origine"],
        ["item"] = ["Acción / comando", "Action / command", "Action / commande", "Azione / comando"],
        ["execute"] = ["Ejecutar", "Execute", "Exécuter", "Esegui"],
        ["close"] = ["Volver", "Back", "Retour", "Indietro"],
        ["none"] = ["Sin coincidencias", "No matches", "Aucun résultat", "Nessun risultato"],
        ["ready"] = ["Disponible", "Available", "Disponible", "Disponibile"],
        ["blocked"] = ["No disponible", "Unavailable", "Indisponible", "Non disponibile"],
        ["no_run"] = ["Requiere un Run activo", "Requires an active Run", "Nécessite une exécution active", "Richiede un’esecuzione attiva"],
        ["busy"] = ["Espera a que termine el trabajo activo", "Wait for active work to finish", "Attendez la fin du travail actif", "Attendi il completamento del lavoro attivo"],
        ["changed"] = ["Cambió el contexto o el comando. Abre la paleta de nuevo.", "The context or command changed. Reopen the palette.", "Le contexte ou la commande a changé. Rouvrez la palette.", "Il contesto o il comando è cambiato. Riapri la tavolozza."],
        ["invalid_arguments"] = ["Argumentos inválidos: revisa las comillas", "Invalid arguments: check quotes", "Arguments invalides : vérifiez les guillemets", "Argomenti non validi: controlla le virgolette"],
        ["action.sidebar.toggle"] = ["Mostrar u ocultar panel", "Toggle sidebar", "Afficher ou masquer le panneau", "Mostra o nascondi pannello"],
        ["action.model.select"] = ["Seleccionar modelo", "Select model", "Choisir un modèle", "Seleziona modello"],
        ["action.preferences.open"] = ["Abrir preferencias", "Open preferences", "Ouvrir les préférences", "Apri preferenze"],
        ["action.keybindings.open"] = ["Configurar atajos de teclado", "Keyboard shortcuts", "Raccourcis clavier", "Scorciatoie da tastiera"],
    };
    public static IReadOnlyCollection<string> Keys => Values.Keys.Select(key => "palette." + key).ToArray();
    public static bool TryTranslate(string locale, string key, out string text)
    {
        if (key.StartsWith("palette.", StringComparison.Ordinal) && Values.TryGetValue(key[8..], out var values))
        { text = values[locale switch { "es" => 0, "fr" => 2, "it" => 3, _ => 1 }]; return true; }
        text = ""; return false;
    }
}
