namespace OmniCore.Client;

public static class ConversationSearchResources
{
    private static readonly Dictionary<string, string[]> Values = new(StringComparer.Ordinal)
    {
        ["title"] = ["Buscar en esta conversación", "Search this conversation", "Rechercher dans cette conversation", "Cerca in questa conversazione"],
        ["scope"] = ["Sólo texto cargado de esta conversación · no consulta otras sesiones", "Only loaded text in this conversation · no other sessions are queried", "Texte chargé de cette conversation uniquement · aucune autre session consultée", "Solo testo caricato di questa conversazione · nessun’altra sessione consultata"],
        ["prompt"] = ["Escribe texto para buscar", "Enter text to search", "Saisissez le texte à rechercher", "Inserisci il testo da cercare"],
        ["empty"] = ["Sin coincidencias en el texto cargado", "No matches in loaded text", "Aucun résultat dans le texte chargé", "Nessun risultato nel testo caricato"],
        ["count"] = ["{count} coincidencias", "{count} matches", "{count} résultats", "{count} risultati"],
        ["count_one"] = ["1 coincidencia", "1 match", "1 résultat", "1 risultato"],
        ["limited"] = ["Búsqueda limitada: hasta 200 resultados, 2 M de caracteres y 10 000 mensajes", "Search limited: up to 200 results, 2 M characters and 10,000 messages", "Recherche limitée : 200 résultats, 2 M de caractères et 10 000 messages maximum", "Ricerca limitata: fino a 200 risultati, 2 M di caratteri e 10.000 messaggi"],
        ["query_limit"] = ["Busca con un máximo de 256 caracteres", "Search with up to 256 characters", "Recherchez avec 256 caractères maximum", "Cerca con un massimo di 256 caratteri"],
        ["changed"] = ["Cambió la conversación o el mensaje. Abre la búsqueda de nuevo.", "The conversation or message changed. Reopen search.", "La conversation ou le message a changé. Rouvrez la recherche.", "La conversazione o il messaggio è cambiato. Riapri la ricerca."],
        ["origin"] = ["Origen", "Source", "Origine", "Origine"],
        ["session"] = ["Conversación", "Conversation", "Conversation", "Conversazione"],
        ["message"] = ["Mensaje", "Message", "Message", "Messaggio"],
        ["open"] = ["Ir al mensaje", "Go to message", "Aller au message", "Vai al messaggio"],
        ["help"] = ["↑↓ elegir · Enter ir al mensaje · Esc volver al borrador", "↑↓ choose · Enter go to message · Esc return to draft", "↑↓ choisir · Entrée aller au message · Échap revenir au brouillon", "↑↓ scegli · Invio vai al messaggio · Esc torna alla bozza"],
        ["claim"] = ["Texto del asistente · no acredita verificación", "Assistant text · does not establish verification", "Texte de l’assistant · ne constitue pas une vérification", "Testo dell’assistente · non attesta una verifica"],
        ["role.User"] = ["Tú", "You", "Vous", "Tu"],
        ["role.Reasoning"] = ["Razonamiento visible", "Visible reasoning", "Raisonnement visible", "Ragionamento visibile"],
        ["role.Assistant"] = ["Asistente", "Assistant", "Assistant", "Assistente"],
        ["role.Tool"] = ["Herramienta", "Tool", "Outil", "Strumento"],
        ["role.System"] = ["Estado local", "Local status", "État local", "Stato locale"],
        ["role.Interaction"] = ["Interacción", "Interaction", "Interaction", "Interazione"],
    };

    public static IReadOnlyCollection<string> Keys => Values.Keys.Select(key => "search." + key).ToArray();
    public static bool TryTranslate(string locale, string key, out string text)
    {
        if (key.StartsWith("search.", StringComparison.Ordinal) && Values.TryGetValue(key[7..], out var values))
        { text = values[locale switch { "es" => 0, "fr" => 2, "it" => 3, _ => 1 }]; return true; }
        text = ""; return false;
    }
}
