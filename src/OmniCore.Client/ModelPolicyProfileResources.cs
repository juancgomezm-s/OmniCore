namespace OmniCore.Client;


/// <summary>Standalone translations for exact model profile execution controls.</summary>
public sealed record ModelPolicyProfileLabels(string Title, string Steps, string ToolCalls,
    string Bounds, string ProviderReasoning, string Save, string Invalid, string Failed,
    string VisibleTools, string Capabilities, string Mutation, string MaxFiles, string MaxLines,
    string RewriteRatio, string MutationMode, string DeletePolicy, string MovePolicy,
    string PriorRead, string VersionToken, string Validation,
    string GenerationDeadline, string AllowOrchestration)
{
    public static ModelPolicyProfileLabels For(string? locale) => locale?.ToLowerInvariant() switch
    {
        "fr" => new("Limites du profil", "Étapes modèle (1–8)", "Appels d’outils (1–64)",
            "Limites appliquées par OmniCore à chaque tour.",
            "Le raisonnement reste contrôlé par le fournisseur.", "Enregistrer", "Valeurs hors limites.",
            "Échec de l’enregistrement des limites.", "Outils visibles", "Capacités autorisées",
            "Limites de modification", "Fichiers max.", "Lignes modifiées max.", "Ratio de réécriture max.",
            "Type de changements", "Suppression", "Déplacement", "Lire avant modification", "Vérifier la version", "Valider après modification",
            "Délai de génération max. (s)", "Autoriser la coordination"),
        "it" => new("Limiti del profilo", "Passaggi del modello (1–8)", "Chiamate agli strumenti (1–64)",
            "Limiti applicati da OmniCore per turno.",
            "Il ragionamento resta controllato dal provider.", "Salva", "Valori fuori intervallo.",
            "Impossibile salvare i limiti.", "Strumenti visibili", "Capacità consentite",
            "Limiti di modifica", "File max.", "Righe modificate max.", "Rapporto di riscrittura max.",
            "Tipo di modifiche", "Eliminazione", "Spostamento", "Leggere prima di modificare", "Verificare la versione", "Convalidare dopo la modifica",
            "Durata max. generazione (s)", "Consenti coordinamento"),
        "en" => new("Profile limits", "Model steps (1–8)", "Tool calls (1–64)",
            "Limits enforced by OmniCore for each turn.",
            "Reasoning remains controlled by the provider.", "Save", "Values are outside the allowed range.",
            "Could not save the limits.", "Visible tools", "Allowed capabilities",
            "File mutation limits", "Max files", "Max changed lines", "Max rewrite ratio",
            "File change mode", "Delete", "Move", "Read before editing", "Check file version", "Validate after editing",
            "Max generation time (s)", "Allow coordination"),
        _ => new("Límites del perfil", "Pasos del modelo (1–8)", "Llamadas a herramientas (1–64)",
            "Límites aplicados por OmniCore en cada turno.",
            "El proveedor sigue controlando el razonamiento.", "Guardar", "Los valores están fuera del rango permitido.",
            "No se pudieron guardar los límites.", "Herramientas visibles", "Capacidades permitidas",
            "Límites de mutación", "Máx. archivos", "Máx. líneas modificadas", "Ratio máx. de reescritura",
            "Tipo de cambio en archivos", "Borrar", "Mover", "Leer antes de editar", "Comprobar versión del archivo", "Validar después de editar",
            "Tiempo máx. de generación (s)", "Permitir coordinación"),
    };
}

/// <summary>Human-readable profile choices; values persisted by the UI remain canonical IDs.</summary>
public static class ModelPolicyProfileText
{
    private static readonly string[] Ids = [
        "basics", "advanced", "read", "search", "references", "plans", "mailbox", "patch",
        "create", "rewrite", "delete", "move", "validate", "process", "shell", "network",
        "mode_none", "mode_patch", "mode_patch_create", "mode_full", "deny", "confirm", "allow",
        "yes", "no", "parallel", "save_changes"];

    private static readonly string[][] Values =
    [
        ["Basic tools", "Advanced tools", "Read files", "Search workspace", "Resolve references", "Suggest plans", "Wait for assigned messages", "Apply targeted patches", "Create files", "Rewrite files", "Delete files", "Move or rename files", "Run build or tests", "Run approved processes", "Shell access", "Network access", "No changes", "Patch files", "Patch + create", "Full changes", "Block", "Ask first", "Permit", "Required", "Optional", "Parallel edits", "Save changes"],
        ["Herramientas básicas", "Herramientas avanzadas", "Leer archivos", "Buscar en el espacio de trabajo", "Resolver referencias", "Proponer planes", "Esperar mensajes asignados", "Aplicar parches puntuales", "Crear archivos", "Reescribir archivos", "Borrar archivos", "Mover o renombrar archivos", "Ejecutar compilaciones o pruebas", "Ejecutar procesos aprobados", "Acceso a la shell", "Acceso a la red", "Sin cambios", "Parchear", "Parchear + crear", "Cambios completos", "Bloquear", "Preguntar antes", "Permitir", "Obligatorio", "Opcional", "Ediciones paralelas", "Guardar cambios"],
        ["Outils de base", "Outils avancés", "Lire des fichiers", "Rechercher dans l’espace de travail", "Résoudre les références", "Proposer des plans", "Attendre les messages assignés", "Appliquer des correctifs ciblés", "Créer des fichiers", "Réécrire des fichiers", "Supprimer des fichiers", "Déplacer ou renommer des fichiers", "Lancer build ou tests", "Lancer les processus autorisés", "Accès au shell", "Accès réseau", "Aucun changement", "Correctifs", "Correctifs + création", "Modifications complètes", "Bloquer", "Demander avant", "Permettre", "Obligatoire", "Facultatif", "Modifications parallèles", "Enregistrer"],
        ["Strumenti di base", "Strumenti avanzati", "Leggere file", "Cercare nell’area di lavoro", "Risolvere riferimenti", "Proporre piani", "Attendere i messaggi assegnati", "Applicare patch mirate", "Creare file", "Riscrivere file", "Eliminare file", "Spostare o rinominare file", "Eseguire build o test", "Eseguire processi approvati", "Accesso alla shell", "Accesso alla rete", "Nessuna modifica", "Patch", "Patch + creazione", "Modifiche complete", "Bloccare", "Chiedere prima", "Consentire", "Obbligatoria", "Facoltativa", "Modifiche parallele", "Salva modifiche"],
    ];

    public static IReadOnlyCollection<string> Keys { get; } = Array.AsReadOnly(Ids);

    public static string Get(string? locale, string id)
    {
        var localeIndex = locale?.ToLowerInvariant() switch { "en" => 0, "fr" => 2, "it" => 3, _ => 1 };
        var index = Array.IndexOf(Ids, id);
        return index < 0 ? id : Values[localeIndex][index];
    }

    public static string Capability(string? locale, string canonicalId) => canonicalId switch
    {
        "WorkspaceRead" => Get(locale, "read"),
        "Search" => Get(locale, "search"),
        "ReferenceResolve" => Get(locale, "references"),
        "PlanProposal" => Get(locale, "plans"),
        "AgentMailboxWait" => Get(locale, "mailbox"),
        "PatchExisting" => Get(locale, "patch"),
        "CreateFile" => Get(locale, "create"),
        "ReplaceFile" => Get(locale, "rewrite"),
        "DeleteFile" => Get(locale, "delete"),
        "MoveOrRename" => Get(locale, "move"),
        "ValidationProcess" => Get(locale, "validate"),
        "GeneralProcess" => Get(locale, "process"),
        "Shell" => Get(locale, "shell"),
        "Network" => Get(locale, "network"),
        _ => canonicalId,
    };
}
