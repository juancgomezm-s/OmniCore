namespace OmniCore.Client;

/// <summary>Shared human command/tool copy. Existing Spanish/English legacy values stay authoritative.</summary>
public static class ClientCommandResources
{
    public static IReadOnlyCollection<string> Keys => Legacy.Keys.Concat(New.Keys).ToArray();
    public static IReadOnlyCollection<string> LegacyKeys => Legacy.Keys.ToArray();
    internal static bool ContainsKey(string key) => Legacy.ContainsKey(key) || New.ContainsKey(key);

    internal static bool TryTranslate(string locale, string key, out string text)
    {
        if (New.TryGetValue(key, out var added))
        {
            text = locale switch { "en" => added.En, "fr" => added.Fr, "it" => added.It, _ => added.Es };
            return true;
        }
        if (locale is "fr" or "it" && Legacy.TryGetValue(key, out var existing))
        {
            text = locale == "fr" ? existing.Fr : existing.It;
            return true;
        }
        text = "";
        return false;
    }

    private static readonly Dictionary<string, (string Fr, string It)> Legacy = new(StringComparer.Ordinal)
    {
        ["permissions.heading"] = ("Autorisations de l’espace de travail :", "Autorizzazioni dell’area di lavoro:"),
        ["permissions.empty"] = ("Aucune autorisation persistante pour cet espace de travail ou cette exécution.", "Nessuna autorizzazione persistente per questa area di lavoro o esecuzione."),
        ["permissions.revoked"] = ("Autorisation révoquée.", "Autorizzazione revocata."),
        ["permissions.not_found"] = ("Aucune autorisation portant cet identifiant dans cet espace de travail.", "Nessuna autorizzazione con questo identificatore nell’area di lavoro."),
        ["permissions.usage"] = ("Utilisation : omni permissions list | revoke <id>", "Uso: omni permissions list | revoke <id>"),
        ["permissions.help"] = ("  omni permissions list|revoke <id>   Autorisations de l’espace de travail actuel", "  omni permissions list|revoke <id>   Autorizzazioni dell’area di lavoro corrente"),
        ["client.command.help"] = ("affiche cette aide", "mostra questa guida"),
        ["client.command.exit"] = ("quitte l’application", "esce dall’applicazione"),
        ["client.command.plan"] = ("affiche le plan et l’état de travail de l’exécution", "mostra il piano e lo stato dell’esecuzione"),
        ["client.command.tasks"] = ("affiche les tâches et les branches d’exécution", "mostra le attività e i rami dell’esecuzione"),
        ["client.command.events"] = ("affiche les derniers événements du journal", "mostra gli ultimi eventi del registro"),
        ["client.command.context"] = ("affiche le contexte destiné au modèle", "mostra il contesto destinato al modello"),
        ["client.command.tools"] = ("affiche les outils et leurs décisions d’autorisation", "mostra gli strumenti e le relative decisioni di autorizzazione"),
        ["client.command.extensions"] = ("affiche les extensions découvertes et leur niveau de confiance", "mostra le estensioni rilevate e il livello di attendibilità"),
        ["client.command.commands"] = ("affiche les commandes disponibles, leur priorité et les conflits", "mostra i comandi disponibili, le priorità e i conflitti"),
        ["client.command.skills"] = ("consulte ou active une compétence du projet pour cette session", "consulta o attiva una competenza del progetto per questa sessione"),
        ["client.command.memory"] = ("consulte, ajoute ou supprime la mémoire de cette session", "consulta, aggiunge o elimina la memoria di questa sessione"),
        ["client.command.permissions"] = ("liste les autorisations accordées dans cet espace de travail", "elenca le autorizzazioni concesse nell’area di lavoro"),
        ["client.command.interrupt"] = ("interrompt l’action en cours ; un second /interrupt annule l’exécution", "interrompe l’azione corrente; un secondo /interrupt annulla l’esecuzione"),
        ["client.command.cancel"] = ("annule l’exécution en cours", "annulla l’esecuzione corrente"),
        ["client.exit.bye"] = ("À bientôt.", "A presto."),
        ["client.permissions.unavailable"] = ("Ce client ne peut pas lister les autorisations ; utilisez « omni permissions list ».", "Questo client non può elencare le autorizzazioni; usa «omni permissions list»."),
        ["client.help.title"] = ("Commandes disponibles :", "Comandi disponibili:"),
        ["client.help.footer"] = ("Le texte qui ne commence pas par / est envoyé au modèle.", "Il testo che non inizia con / viene inviato al modello."),
        ["client.no_data"] = ("Aucune donnée disponible.", "Nessun dato disponibile."),
        ["client.tools.invalid"] = ("Impossible de lire le catalogue d’outils du service hôte.", "Impossibile leggere il catalogo degli strumenti del servizio host."),
        ["client.tools.description.missing"] = ("Le fournisseur n’a pas décrit la fonction de cet outil.", "Il fornitore non ha descritto la funzione di questo strumento."),
        ["client.tools.description.original"] = ("Description du fournisseur (langue d’origine) : ", "Descrizione del fornitore (lingua originale): "),
        ["client.tools.policy.plan"] = ("Bloqué en mode Plan : cet outil ne se limite pas à la lecture.", "Bloccato in modalità Plan: lo strumento non è di sola lettura."),
        ["client.tools.policy.act_only"] = ("Disponible uniquement en ACT : le Run actuel ne l’inclut pas dans son catalogue.", "Disponibile solo in ACT: il Run attuale non lo include nel suo catalogo."),
        ["client.tools.policy.boundary"] = ("Bloqué par les limites de capacité du modèle.", "Bloccato dai limiti di capacità del modello."),
        ["client.tools.policy.boundary_ask"] = ("Les limites du modèle exigent une confirmation ; les autres autorisations restent applicables.", "I limiti del modello richiedono conferma; restano valide le altre autorizzazioni."),
        ["client.tools.policy.ask"] = ("La politique exige une confirmation ; d’autres limites peuvent empêcher l’exécution.", "La politica richiede conferma; altri limiti possono impedire l’esecuzione."),
        ["client.tools.policy.evaluate"] = ("Les autorisations sont évaluées à la demande d’exécution, selon les arguments et les ressources.", "Le autorizzazioni vengono valutate alla richiesta di esecuzione, in base ad argomenti e risorse."),
        ["client.tools.policy.unknown"] = ("Politique non reconnue par ce client ; consultez la valeur technique ci-dessous.", "Politica non riconosciuta da questo client; consulta il valore tecnico sottostante."),
        ["client.tool.user.ask.description"] = ("Vous demande des informations à l’aide de questions, de choix ou de texte libre.", "Ti chiede informazioni tramite domande, scelte o testo libero."),
        ["client.tool.filesystem.read.description"] = ("Lit une page limitée de lignes d’un fichier du projet sans le modifier. offset commence à la ligne 1 et limit va jusqu’à 500 ; le résultat indique les numéros, la suite et le hash complet du fichier.", "Legge una pagina limitata di righe di un file del progetto senza modificarlo. offset parte dalla riga 1 e limit arriva a 500; il risultato mostra numeri, continuazione e hash completo del file."),
        ["client.tool.tool.search.description"] = ("Recherche des outils enregistrés par identifiant, description ou tags. Les résultats alimentent le plan suivant sans accorder d’autorisation.", "Cerca gli strumenti registrati per ID, descrizione o tag. I risultati sono candidati per il piano successivo e non concedono autorizzazioni."),
        ["client.tool.filesystem.list.description"] = ("Liste les fichiers et dossiers du projet, y compris les sous-dossiers sur demande.", "Elenca file e cartelle del progetto, incluse le sottocartelle se richiesto."),
        ["client.tool.search.text.description"] = ("Recherche du texte ou des expressions régulières dans les fichiers du projet et renvoie les lignes correspondantes.", "Cerca testo o espressioni regolari nei file del progetto e restituisce le righe corrispondenti."),
        ["client.tool.reference.resolve.description"] = ("Résout les références @ vers les fichiers ou dossiers du projet sans les modifier.", "Risolve i riferimenti @ a file o cartelle del progetto senza modificarli."),
        ["client.tool.plan.propose.description"] = ("Propose des modifications du plan de travail pour validation et application par le moteur.", "Propone modifiche al piano di lavoro che il motore deve convalidare e applicare."),
        ["client.tool.mode.propose.description"] = ("Suggère de passer à Plan, Act ou Orq. Ne modifie pas directement le mode ni les autorisations.", "Suggerisce il passaggio a Plan, Act o Orq. Non modifica direttamente modalità o autorizzazioni."),
        ["client.tool.filesystem.write.description"] = ("Crée un fichier ou remplace son contenu, après vérification de la version attendue s’il existe déjà.", "Crea un file o ne sostituisce il contenuto, verificando la versione attesa se esiste già."),
        ["client.tool.filesystem.patch.description"] = ("Remplace un fragment de fichier existant après vérification de sa version attendue.", "Sostituisce un frammento di un file esistente dopo averne verificato la versione attesa."),
        ["client.tool.process.exec.description"] = ("Exécute un programme avec des arguments littéraux, sans interpréteur de commandes, depuis un dossier du projet.", "Esegue un programma con argomenti letterali, senza shell, da una cartella del progetto."),
        ["client.tool.shell.exec.description"] = ("Exécute une commande via l’interpréteur du système. Cette opération à haut risque exige une autorisation.", "Esegue un comando tramite la shell di sistema. È un’operazione ad alto rischio che richiede autorizzazione."),
        ["client.tool.artifact.read.description"] = ("Lit une page d’un artefact de la session à partir de son identifiant de contenu (hash).", "Legge una pagina di un artefatto della sessione tramite il suo identificatore di contenuto (hash)."),
        ["client.tool.core.agents.mailbox.receive.description"] = ("Attend un message du superviseur de cette exécution. Ce message est une information, pas une politique système.", "Attende un messaggio dal supervisore dell’esecuzione. Il messaggio è un’informazione, non una politica di sistema."),
        ["client.tool.worktree.integrate.description"] = ("Applique au projet une proposition d’intégration Git révisée après vérification des versions attendues des fichiers.", "Applica al progetto una proposta di integrazione Git revisionata dopo aver verificato le versioni attese dei file."),
        ["tool.tool.search.running"] = ("Recherche des outils correspondant à « {query} »", "Ricerca strumenti per «{query}»"),
        ["tool.tool.search.succeeded"] = ("Recherche d’outils terminée pour « {query} »", "Ricerca strumenti completata per «{query}»"),
        ["tool.tool.search.failed"] = ("Échec de la recherche d’outils pour « {query} » : {cause}", "Ricerca strumenti non riuscita per «{query}»: {cause}"),
        ["client.events.empty"] = ("Il n’y a pas encore d’événements.", "Non ci sono ancora eventi."),
        ["client.events.heading"] = ("Derniers {count} événements sur {total} :", "Ultimi {count} eventi su {total}:"),
        ["client.command.sent"] = ("Envoyé : {command}", "Inviato: {command}"),
        ["client.action.help.show"] = ("Afficher l’aide", "Mostra la guida"),
        ["client.action.app.exit"] = ("Quitter", "Esci"),
        ["client.action.view.plan"] = ("Voir le plan", "Visualizza il piano"),
        ["client.action.view.tasks"] = ("Voir les tâches", "Visualizza le attività"),
        ["client.action.view.events"] = ("Voir les événements récents", "Visualizza gli eventi recenti"),
        ["client.action.context.inspect"] = ("Examiner le contexte", "Esamina il contesto"),
        ["client.action.tools.inspect"] = ("Examiner les outils", "Esamina gli strumenti"),
        ["client.action.extensions.inspect"] = ("Examiner les extensions", "Esamina le estensioni"),
        ["client.action.commands.inspect"] = ("Examiner les commandes", "Esamina i comandi"),
        ["client.action.skills.inspect"] = ("Examiner les compétences", "Esamina le competenze"),
        ["client.action.memory.session"] = ("Gérer la mémoire de la session", "Gestisci la memoria della sessione"),
        ["client.action.permissions.list"] = ("Voir les autorisations accordées", "Visualizza le autorizzazioni concesse"),
        ["client.action.run.interrupt"] = ("Interrompre l’action en cours", "Interrompi l’azione corrente"),
        ["client.action.run.cancel"] = ("Annuler l’exécution", "Annulla l’esecuzione"),
    };

    private static readonly Dictionary<string, (string Es, string En, string Fr, string It)> New = new(StringComparer.Ordinal)
    {
        ["tool.error.read_page_bounds"] = ("Argumentos de página inválidos: offset={offset}, limit={limit}. Usa offset ≥ 1 y limit entre 1 y 500.", "Invalid page arguments: offset={offset}, limit={limit}. Use offset ≥ 1 and limit from 1 to 500.", "Arguments de page invalides : offset={offset}, limit={limit}. Utilisez offset ≥ 1 et limit de 1 à 500.", "Argomenti di pagina non validi: offset={offset}, limit={limit}. Usa offset ≥ 1 e limit da 1 a 500."),
        ["tool.error.search_bounds"] = ("Consulta inválida: longitud de query={queryLength}, maxResults={maxResults}. query admite 1–256 caracteres y maxResults de 1 a 8.", "Invalid search arguments: query length={queryLength}, maxResults={maxResults}. query accepts 1–256 characters and maxResults from 1 to 8.", "Arguments de recherche invalides : longueur de query={queryLength}, maxResults={maxResults}. query accepte 1 à 256 caractères et maxResults de 1 à 8.", "Argomenti di ricerca non validi: lunghezza query={queryLength}, maxResults={maxResults}. query accetta 1–256 caratteri e maxResults da 1 a 8."),
        ["tool.error.search_result_limit"] = ("El catálogo excede el límite de respuesta de {maxBytes} bytes.", "The catalog response exceeds the {maxBytes}-byte limit.", "La réponse du catalogue dépasse la limite de {maxBytes} octets.", "La risposta del catalogo supera il limite di {maxBytes} byte."),
        ["client.skills.usage"] = ("Uso: /skills [load|release <skill-id>]", "Usage: /skills [load|release <skill-id>]", "Utilisation : /skills [load|release <skill-id>]", "Uso: /skills [load|release <skill-id>]"),
        ["client.skills.rejected"] = ("Se rechazó el cambio de skill.", "The skill change was rejected.", "La modification de compétence a été refusée.", "La modifica della competenza è stata rifiutata."),
        ["client.skills.activated"] = ("Skill activada para esta sesión.", "Skill activated for this Session.", "Compétence activée pour cette session.", "Competenza attivata per questa sessione."),
        ["client.skills.released"] = ("Skill liberada de esta sesión.", "Skill released from this Session.", "Compétence retirée de cette session.", "Competenza rimossa da questa sessione."),
        ["client.memory.usage"] = ("Uso: /memory session [add <texto>|forget <memory-id>]", "Usage: /memory session [add <text>|forget <memory-id>]", "Utilisation : /memory session [add <texte>|forget <memory-id>]", "Uso: /memory session [add <testo>|forget <memory-id>]"),
        ["client.memory.trusted_required"] = ("Cambiar la memoria de sesión requiere una acción confiable del usuario dentro del proceso.", "Session memory changes require a trusted in-process user action.", "Modifier la mémoire de session exige une action utilisateur de confiance dans le processus.", "Modificare la memoria di sessione richiede un’azione utente attendibile all’interno del processo."),
        ["client.memory.rejected"] = ("Se rechazó el cambio de memoria de sesión.", "Session memory change was rejected.", "La modification de mémoire de session a été refusée.", "La modifica della memoria di sessione è stata rifiutata."),
        ["tools.ui.title"] = ("Herramientas", "Tools", "Outils", "Strumenti"),
        ["tools.ui.purpose"] = ("Qué hace cada herramienta del agente.\nConsulta: no ejecuta ni activa/desactiva.", "What each agent tool does.\nRead-only: no execution or on/off switches.", "Fonction de chaque outil de l’agent.\nConsultation : aucune exécution ni activation.", "Funzione di ogni strumento dell’agente.\nSola lettura: non esegue né attiva strumenti."),
        ["tools.ui.close"] = ("Cerrar", "Close", "Fermer", "Chiudi"),
        ["tools.ui.visible"] = ("Visible", "Visible", "Visible", "Visibile"),
        ["tools.ui.hidden"] = ("No visible", "Not visible", "Non visible", "Non visibile"),
        ["tools.ui.restriction"] = ("Restricción: ", "Restriction: ", "Restriction : ", "Restrizione: "),
        ["tools.ui.not_permission"] = ("no equivale a permiso de ejecución", "does not grant execution permission", "n’accorde pas le droit d’exécution", "non concede il permesso di esecuzione"),
        ["tools.ui.control"] = ("Control: las reglas de usuario en permissions.yaml permiten allow (permitir), ask (preguntar) y deny (denegar). No superan los límites del modo o del modelo. /permissions lista autorizaciones guardadas; no activa herramientas.", "Control: user rules in permissions.yaml support allow, ask and deny. They cannot override mode or model limits. /permissions lists saved grants; it does not enable tools.", "Contrôle : les règles utilisateur dans permissions.yaml acceptent allow (autoriser), ask (demander) et deny (refuser). Elles ne dépassent pas les limites du mode ou du modèle. /permissions liste les autorisations enregistrées ; il n’active aucun outil.", "Controllo: le regole utente in permissions.yaml supportano allow (consentire), ask (chiedere) e deny (negare). Non superano i limiti della modalità o del modello. /permissions elenca le autorizzazioni salvate; non attiva strumenti."),
        ["tools.ui.source"] = ("Origen: ", "Source: ", "Origine : ", "Origine: "),
        ["tools.ui.effect"] = ("Clase de efecto (técnica): ", "Effect class (technical): ", "Classe d’effet (technique) : ", "Classe di effetto (tecnica): "),
        ["tools.ui.policy"] = ("Política reportada (técnica): ", "Reported policy (technical): ", "Politique signalée (technique) : ", "Politica segnalata (tecnica): "),
        ["tools.ui.original"] = ("Definición original: ", "Original definition: ", "Définition d’origine : ", "Definizione originale: "),
        ["tools.ui.mode"] = ("Modo: ", "Mode: ", "Mode : ", "Modalità: "),
        ["tools.ui.unknown"] = ("no reportado", "not reported", "non indiqué", "non indicato"),
        ["tools.ui.read_only"] = ("sólo consulta", "read-only", "lecture seule", "sola lettura"),
        ["tools.ui.empty"] = ("El Host no reportó herramientas.", "The Host reported no tools.", "Le service hôte n’a signalé aucun outil.", "Il servizio host non ha segnalato strumenti."),
        ["tools.ui.no_matches"] = ("Sin coincidencias. Cambia el filtro.", "No matches. Change the filter.", "Aucun résultat. Modifiez le filtre.", "Nessun risultato. Modifica il filtro."),
        ["tools.ui.filter"] = ("Filtrar", "Filter", "Filtrer", "Filtra"),
        ["tools.ui.help"] = ("↑↓ · Enter detalle · Ctrl+F · Esc", "↑↓ · Enter details · Ctrl+F · Esc", "↑↓ · Entrée détails · Ctrl+F · Échap", "↑↓ · Invio dettagli · Ctrl+F · Esc"),
    };
}
