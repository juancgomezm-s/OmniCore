namespace OmniCore.Client;

public static class ProjectEntryResources
{
    private static readonly IReadOnlyDictionary<string, string[]> Values = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["shell.conversation.open_title"] = ["Nueva conversación / Retomar existente", "New conversation / Resume existing", "Nouvelle conversation / Reprendre", "Nuova conversazione / Riprendi"],
        ["shell.conversation.continue_last"] = ["Continuar la última", "Continue last conversation", "Continuer la dernière", "Continua l’ultima"],
        ["project.initialize.usage"] = ["Uso: inicializar (sin argumentos).", "Usage: initialize (no arguments).", "Usage : initialiser (sans arguments).", "Uso: inizializza (senza argomenti)."],
        ["project.initialize.created"] = ["Archivo de proyecto creado:", "Project file created:", "Fichier de projet créé :", "File di progetto creato:"],
        ["project.initialize.updated"] = ["Archivo de proyecto actualizado:", "Project file updated:", "Fichier de projet mis à jour :", "File di progetto aggiornato:"],
        ["project.initialize.unchanged"] = ["Archivo de proyecto sin cambios:", "Project file unchanged:", "Fichier de projet inchangé :", "File di progetto invariato:"],
        ["project.initialize.sources"] = ["Fuentes leídas:", "Sources read:", "Sources lues :", "Fonti lette:"],
        ["project.initialize.diagnostics"] = ["Diagnósticos:", "Diagnostics:", "Diagnostics :", "Diagnostica:"],
        ["project.initialize.failed"] = ["No se pudo inicializar: comprueba permisos, exclusiones y el bloque administrado de agents.md.", "Initialization failed: check permissions, exclusions and the managed agents.md block.", "Échec de l'initialisation : vérifiez les autorisations, exclusions et le bloc géré agents.md.", "Inizializzazione non riuscita: controlla autorizzazioni, esclusioni e blocco gestito agents.md."],
        ["project.initialize.busy"] = ["Termina el turno activo antes de inicializar el proyecto.", "Finish the active turn before initializing the project.", "Terminez le tour actif avant d'initialiser le projet.", "Termina il turno attivo prima di inizializzare il progetto."],
        ["project.initialize.unavailable"] = ["Inicializar no está disponible en este cliente.", "Initialization is unavailable in this client.", "L'initialisation est indisponible dans ce client.", "L'inizializzazione non è disponibile in questo client."],
        ["project.initialize.trust_required"] = ["Concede confianza al proyecto con omni confianza antes de inicializar.", "Trust the project with omni trust before initializing.", "Accordez la confiance au projet avec omni trust avant l'initialisation.", "Autorizza il progetto con omni trust prima di inizializzarlo."],
        ["command.initialize"] = ["Descubre proyecto y memorias locales; crea o actualiza agents.md.", "Discover the project and local memories; create or update agents.md.", "Découvrir le projet et les mémoires locales ; créer ou actualiser agents.md.", "Scopri progetto e memorie locali; crea o aggiorna agents.md."],
        ["project.initialize.title"] = ["Inicialización del proyecto", "Project initialization", "Initialisation du projet", "Inizializzazione del progetto"],
        ["project.initialize.content"] = ["Bloque generado (las instrucciones manuales se conservan):", "Generated block (manual instructions are preserved):", "Bloc généré (les instructions manuelles sont conservées) :", "Blocco generato (le istruzioni manuali sono conservate):"],
    };
    internal static bool TryTranslate(string locale, string key, out string text)
    {
        if (Values.TryGetValue(key, out var variants))
        { text = variants[locale switch { "en" => 1, "fr" => 2, "it" => 3, _ => 0 }]; return true; }
        text = ""; return false;
    }
}
