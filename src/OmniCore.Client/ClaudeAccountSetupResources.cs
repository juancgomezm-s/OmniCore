namespace OmniCore.Client;

public static class ClaudeAccountSetupResources
{
    private static readonly Dictionary<string, string[]> Rows = new(StringComparer.Ordinal)
    {
        ["configure"] = ["Configurar cuenta…", "Configure account…", "Configurer le compte…", "Configura account…"],
        ["configure_login"] = ["Configurar e iniciar sesión…", "Configure and sign in…", "Configurer et se connecter…", "Configura e accedi…"],
        ["empty"] = ["Aún no hay una cuenta Claude configurada. Elige Iniciar sesión para revisar y guardar su conexión OAuth; después se abrirá el navegador.", "No Claude account is configured yet. Choose Sign in to review and save the OAuth connection; the browser will then open.", "Aucun compte Claude n’est configuré. Choisissez Se connecter pour vérifier et enregistrer la connexion OAuth ; le navigateur s’ouvrira ensuite.", "Nessun account Claude è configurato. Scegli Accedi per verificare e salvare la connessione OAuth; poi si aprirà il browser."],
        ["intro"] = ["Revisa la conexión OAuth. Los valores iniciales proceden de la plantilla incluida; puedes modificarlos antes de guardar. No se usa una API key.", "Review the OAuth connection. Initial values come from the included template; you can edit them before saving. No API key is used.", "Vérifiez la connexion OAuth. Les valeurs initiales viennent du modèle inclus ; elles sont modifiables avant l’enregistrement. Aucune clé API n’est utilisée.", "Verifica la connessione OAuth. I valori iniziali provengono dal modello incluso e sono modificabili prima del salvataggio. Non viene usata una chiave API."],
        ["provider"] = ["Identificador de la conexión", "Connection identifier", "Identifiant de connexion", "Identificatore connessione"],
        ["base"] = ["URL de inferencia", "Inference URL", "URL d’inférence", "URL inferenza"],
        ["client"] = ["ID público del cliente OAuth", "Public OAuth client ID", "Identifiant public du client OAuth", "ID pubblico client OAuth"],
        ["agent"] = ["User-Agent (debe incluir {version})", "User-Agent (must include {version})", "User-Agent (doit inclure {version})", "User-Agent (deve includere {version})"],
        ["scopes"] = ["Scopes (separados por espacios)", "Scopes (space separated)", "Scopes (séparés par des espaces)", "Scope (separati da spazi)"],
        ["authorize"] = ["URL de autorización OAuth", "OAuth authorization URL", "URL d’autorisation OAuth", "URL autorizzazione OAuth"],
        ["token"] = ["URL de intercambio y renovación", "Token exchange and refresh URL", "URL d’échange et de renouvellement", "URL scambio e rinnovo token"],
        ["profile"] = ["URL de perfil (opcional)", "Profile URL (optional)", "URL du profil (facultative)", "URL profilo (opzionale)"],
        ["roles"] = ["URL de roles (opcional)", "Roles URL (optional)", "URL des rôles (facultative)", "URL ruoli (opzionale)"],
        ["advanced"] = ["Mostrar opciones de conexión", "Show connection options", "Afficher les options de connexion", "Mostra opzioni connessione"],
        ["less"] = ["Ocultar opciones de conexión", "Hide connection options", "Masquer les options de connexion", "Nascondi opzioni connessione"],
        ["save_login"] = ["Guardar e iniciar sesión", "Save and sign in", "Enregistrer et se connecter", "Salva e accedi"],
        ["save"] = ["Guardar configuración", "Save configuration", "Enregistrer la configuration", "Salva configurazione"],
        ["saved"] = ["Conexión guardada. Ya puedes iniciar sesión con esta cuenta.", "Connection saved. You can now sign in with this account.", "Connexion enregistrée. Vous pouvez maintenant vous connecter avec ce compte.", "Connessione salvata. Ora puoi accedere con questo account."],
        ["navigation"] = ["Enter avanza al siguiente campo · Espacio cambia las opciones · Tab / Shift+Tab recorren · Esc cancela sin guardar.", "Enter moves to the next field · Space toggles options · Tab / Shift+Tab navigate · Esc cancels without saving.", "Entrée passe au champ suivant · Espace bascule les options · Tab / Maj+Tab naviguent · Échap annule sans enregistrer.", "Invio passa al campo successivo · Spazio cambia opzioni · Tab / Maiusc+Tab navigano · Esc annulla senza salvare."],
        ["changed"] = ["La configuración cambió en otra ventana. Vuelve a abrir el formulario; no se sobrescribió nada.", "Configuration changed in another window. Reopen the form; nothing was overwritten.", "La configuration a changé ailleurs. Rouvrez le formulaire ; rien n’a été écrasé.", "La configurazione è cambiata altrove. Riapri il modulo; nulla è stato sovrascritto."],
        ["duplicate"] = ["Ese identificador ya existe. Elige otro para conservar la conexión actual.", "That identifier already exists. Choose another to preserve the current connection.", "Cet identifiant existe déjà. Choisissez-en un autre pour conserver la connexion actuelle.", "L’identificatore esiste già. Scegline un altro per conservare la connessione attuale."],
        ["invalid"] = ["Revisa los campos de conexión: URLs HTTPS, cliente y scopes no vacíos, y User-Agent con {version}.", "Check connection fields: HTTPS URLs, nonempty client ID and scopes, and User-Agent with {version}.", "Vérifiez les champs : URL HTTPS, client et scopes non vides, User-Agent avec {version}.", "Verifica i campi: URL HTTPS, client e scope non vuoti, User-Agent con {version}."],
        ["unavailable"] = ["No se pudo leer o guardar la conexión. Revisa la configuración de usuario y sus permisos de archivo.", "Could not read or save the connection. Check user configuration and file permissions.", "Impossible de lire ou d’enregistrer la connexion. Vérifiez la configuration utilisateur et les droits des fichiers.", "Impossibile leggere o salvare la connessione. Verifica la configurazione utente e i permessi dei file."],
    };
    public static string Get(string locale, string key) => Rows.TryGetValue(key, out var values)
        ? values[locale switch { "en" => 1, "fr" => 2, "it" => 3, _ => 0 }] : key;
}
