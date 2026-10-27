namespace OmniCore.Client;

/// <summary>Keyboard configuration UI resources; action IDs, paths and expressions remain literal.</summary>
public static class KeyBindingUiResources
{
    private static readonly Dictionary<string, string[]> Values = new(StringComparer.Ordinal)
    {
        ["title"] = ["Atajos de teclado", "Keyboard shortcuts", "Raccourcis clavier", "Scorciatoie da tastiera"],
        ["intro"] = ["Configuración User · secuencias: 1 s · Esc conserva la opción predeterminada. Recarga después de editar el archivo.", "User configuration · sequences: 1 s · Esc keeps the default choice. Reload after editing the file.", "Configuration User · séquences : 1 s · Échap conserve le choix par défaut. Rechargez après modification du fichier.", "Configurazione User · sequenze: 1 s · Esc conserva la scelta predefinita. Ricarica dopo aver modificato il file."],
        ["keys"] = ["Teclas", "Keys", "Touches", "Tasti"],
        ["target"] = ["Acción / comando", "Action / command", "Action / commande", "Azione / comando"],
        ["source"] = ["Fuente", "Source", "Source", "Origine"],
        ["when"] = ["Condición", "Condition", "Condition", "Condizione"],
        ["priority"] = ["Prioridad", "Priority", "Priorité", "Priorità"],
        ["source.Default"] = ["Predeterminado", "Default", "Par défaut", "Predefinito"],
        ["source.Extension"] = ["Extensión", "Extension", "Extension", "Estensione"],
        ["source.User"] = ["Usuario", "User", "Utilisateur", "Utente"],
        ["diagnostics"] = ["Diagnósticos", "Diagnostics", "Diagnostics", "Diagnostica"],
        ["none"] = ["Sin incidencias de configuración.", "No configuration issues.", "Aucun problème de configuration.", "Nessun problema di configurazione."],
        ["configuration_notice"] = ["Revisar atajos", "Review shortcuts", "Vérifier les raccourcis", "Verifica scorciatoie"],
        ["reload"] = ["Recargar", "Reload", "Recharger", "Ricarica"],
        ["close"] = ["Cerrar", "Close", "Fermer", "Chiudi"],
        ["usage"] = ["Uso: /keybindings [reload]", "Usage: /keybindings [reload]", "Utilisation : /keybindings [reload]", "Uso: /keybindings [reload]"],
        ["error.conflict"] = ["Conflicto: no se ejecuta ninguna acción", "Conflict: no action is executed", "Conflit : aucune action exécutée", "Conflitto: nessuna azione eseguita"],
        ["error.protected_restored"] = ["Se restauró un atajo protegido", "A protected shortcut was restored", "Un raccourci protégé a été restauré", "Ripristinata una scorciatoia protetta"],
        ["error.protected_collision_removed"] = ["Se retiró una colisión con un atajo protegido", "A collision with a protected shortcut was removed", "Une collision avec un raccourci protégé a été supprimée", "Rimossa una collisione con una scorciatoia protetta"],
        ["error.unsupported_terminal_binding"] = ["Atajo no disponible en esta TUI (Esc o Meta)", "Shortcut unavailable in this TUI (Esc or Meta)", "Raccourci indisponible dans cette TUI (Échap ou Meta)", "Scorciatoia non disponibile in questa TUI (Esc o Meta)"],
        ["error.invalid_yaml"] = ["YAML inválido; se mantienen los atajos predeterminados", "Invalid YAML; default shortcuts remain available", "YAML invalide ; les raccourcis par défaut restent disponibles", "YAML non valido; restano disponibili le scorciatoie predefinite"],
        ["error.invalid_document"] = ["Estructura o versión inválida", "Invalid structure or version", "Structure ou version invalide", "Struttura o versione non valida"],
        ["error.invalid_binding"] = ["Definición de atajo inválida", "Invalid shortcut definition", "Définition de raccourci invalide", "Definizione della scorciatoia non valida"],
        ["error.invalid_keys"] = ["Secuencia de teclas inválida", "Invalid key sequence", "Séquence de touches invalide", "Sequenza di tasti non valida"],
        ["error.invalid_removal"] = ["Eliminación de atajo inválida", "Invalid shortcut removal", "Suppression de raccourci invalide", "Rimozione della scorciatoia non valida"],
        ["error.invalid_when"] = ["Condición inválida", "Invalid condition", "Condition invalide", "Condizione non valida"],
        ["error.unknown_context"] = ["Contexto desconocido", "Unknown context", "Contexte inconnu", "Contesto sconosciuto"],
        ["error.unknown_target"] = ["Acción o comando desconocido", "Unknown action or command", "Action ou commande inconnue", "Azione o comando sconosciuto"],
        ["error.extension_ownership"] = ["La extensión no es propietaria de la acción", "The extension does not own the action", "L’extension n’est pas propriétaire de l’action", "L’estensione non possiede l’azione"],
        ["error.prefix_shadowed"] = ["Secuencia oculta por un atajo más corto", "Sequence shadowed by a shorter shortcut", "Séquence masquée par un raccourci plus court", "Sequenza nascosta da una scorciatoia più breve"],
        ["error.too_many_bindings"] = ["Demasiados atajos", "Too many shortcuts", "Trop de raccourcis", "Troppe scorciatoie"],
        ["error.file_too_large"] = ["El archivo supera el tamaño permitido", "The file exceeds the size limit", "Le fichier dépasse la taille autorisée", "Il file supera la dimensione consentita"],
        ["error.file_unreadable"] = ["No se pudo leer el archivo", "The file could not be read", "Impossible de lire le fichier", "Impossibile leggere il file"],
    };

    public static IReadOnlyCollection<string> Keys => Values.Keys.Select(key => "keybindings." + key).ToArray();
    public static bool TryTranslate(string locale, string key, out string text)
    {
        if (key.StartsWith("keybindings.", StringComparison.Ordinal) && Values.TryGetValue(key[12..], out var row))
        {
            text = row[locale switch { "es" => 0, "fr" => 2, "it" => 3, _ => 1 }]; return true;
        }
        text = ""; return false;
    }
}
