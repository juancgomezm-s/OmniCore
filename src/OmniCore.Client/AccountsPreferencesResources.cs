namespace OmniCore.Client;

/// <summary>Complete four-locale copy for the TUI account and provider-connection surfaces.</summary>
public static class AccountsPreferencesResources
{
    private static readonly IReadOnlyDictionary<string, string>[] Catalogs =
    [
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title.chatgpt"] = "Cuenta ChatGPT", ["title.claude"] = "Cuenta Claude", ["title.api"] = "Conexiones API", ["title.connect"] = "Conectar provider",
            ["label.chatgpt"] = "ChatGPT · suscripción", ["label.claude"] = "Claude · OAuth configurado", ["label.api"] = "Providers API",
            ["status.connected"] = "conectada", ["status.expired"] = "sesión expirada", ["status.disconnected"] = "Sin sesión iniciada",
            ["status.notConfigured"] = "Sin configurar", ["status.unavailable"] = "Estado no disponible", ["status.noRows"] = "No hay cuentas OAuth de Claude configuradas en User.", ["status.noProviders"] = "No hay providers configurados.",
            ["status.dead"] = "Credencial revocada",
            ["account.noApiKey"] = "La cuenta ChatGPT usa suscripción; no requiere API key.", ["account.configSource"] = "Identidad OAuth declarada en configuración User.",
            ["account.email"] = "Correo", ["account.name"] = "Nombre", ["account.plan"] = "Plan", ["account.expires"] = "Expira",
            ["account.logoutQuestion"] = "¿Cerrar sesión ChatGPT?", ["account.claudeCount"] = "Cuentas Claude configuradas: {0}",
            ["chatgpt.phase.preparing"] = "Preparando autorización…",
            ["action.manageChatgpt"] = "Administrar cuenta ChatGPT…", ["action.manageClaude"] = "Administrar cuentas Claude…", ["action.manageApi"] = "Administrar API keys…",
            ["action.login"] = "Iniciar sesión…", ["action.logout"] = "Cerrar sesión…", ["action.back"] = "Volver", ["action.cancel"] = "Cancelar",
            ["action.confirmLogout"] = "Sí, cerrar sesión", ["action.browserLogin"] = "Iniciar sesión · enlace de navegador", ["action.deviceLogin"] = "Iniciar sesión · código de dispositivo",
            ["action.connect"] = "Conectar…", ["action.test"] = "Probar", ["action.discover"] = "Descubrir modelos", ["action.disconnect"] = "Desconectar",
            ["action.save"] = "Guardar", ["action.validate"] = "Verificar ahora con la API del provider",
            ["help.providerState"] = "El estado y las fechas vienen del Host; abrir esta página no hace conexiones.",
            ["help.apiKey"] = "La API key se entrega directamente al Host.", ["help.keyboard"] = "↑↓ elegir · Esc volver",
            ["help.login"] = "Se abrirá el navegador para autorizar esta identidad configurada. Esc cancela.",
            ["help.loginProgress"] = "La autorización está activa. Esc cancela; no se guarda hasta completar el flujo.",
            ["help.noProviderAction"] = "Esta capacidad no está implementada para este provider; no se enviará a otro provider.",
            ["result.connecting"] = "Procesando conexión…", ["result.removed"] = "Conexión eliminada.", ["result.failed"] = "La operación falló. Revisa la configuración del provider.",
            ["result.enterKey"] = "Escribe una API key.", ["result.state"] = "Estado: ", ["result.validated"] = "verificada ", ["result.models"] = "modelos ",
            ["result.registered"] = "Modelos registrados: {0}", ["result.quota"] = "Ventanas de cuota reportadas: {0}", ["result.truncated"] = "El catálogo reportado fue acotado.",
            ["method.apiKey"] = "API key", ["method.subscription"] = "suscripción", ["method.none"] = "sin método",
            ["state.connected"] = "Conectado", ["state.notConfigured"] = "Sin configurar", ["state.expired"] = "Expirado", ["state.invalid"] = "Inválido", ["state.unreachable"] = "Sin conexión", ["state.unknown"] = "Desconocido",
            ["date.validated"] = "verificada ", ["date.lastOk"] = "último OK ", ["date.models"] = "modelos ", ["date.none"] = "Sin mediciones registradas.",
            ["error.readApi"] = "No se pudo leer el estado de las conexiones API.", ["error.readAccount"] = "No se pudo leer la sesión ChatGPT. Revisa omni doctor.",
            ["error.logout"] = "No se pudo cerrar la sesión.", ["error.login"] = "No se pudo iniciar sesión. Reintenta o usa código de dispositivo; revisa omni doctor.",
            ["result.loginOk"] = "Sesión iniciada correctamente.", ["result.loginFailed"] = "No se inició la sesión.", ["result.loginCancelled"] = "Inicio de sesión cancelado.",
            ["claude.identityUnavailable"] = "La identidad OAuth configurada no está disponible.", ["claude.statusUnavailable"] = "No se pudo inspeccionar esta cuenta localmente.",
            ["claude.phase.preparing"] = "Preparando autorización…", ["claude.phase.listening"] = "Esperando confirmación del navegador…", ["claude.phase.awaiting"] = "Completa la autorización en el navegador…",
            ["claude.phase.exchanging"] = "Completando autorización…", ["claude.phase.profile"] = "Leyendo el perfil autorizado…", ["claude.phase.persisting"] = "Guardando la sesión…",
            ["claude.phase.succeeded"] = "Sesión Claude conectada.", ["claude.phase.failed"] = "No se completó la sesión Claude.", ["claude.logoutConfirm"] = "¿Cerrar esta sesión Claude?",
            ["claude.oauth.failed.cancelled"] = "Inicio de sesión cancelado.", ["claude.oauth.failed.timeout"] = "El inicio de sesión expiró antes de recibir el código.",
            ["claude.oauth.failed.transport"] = "No se pudo abrir el puerto de retorno.", ["claude.oauth.failed.browser"] = "No se pudo abrir el navegador.",
            ["claude.oauth.failed.callback"] = "El código recibido no corresponde a este inicio de sesión.", ["claude.oauth.failed.exchange"] = "Anthropic rechazó el intercambio del código.",
            ["claude.oauth.failed.network"] = "No se pudo contactar al servidor de autenticación.", ["claude.oauth.failed.protocol"] = "La respuesta de autenticación no fue válida.",
            ["claude.oauth.failed.storage"] = "No se pudo guardar la sesión cifrada.",
            ["providers.notice.unreachable"] = "No se pudo contactar al provider; el estado no se midió.",
            ["providers.notice.rateLimited"] = "El provider respondió con límite de solicitudes; la credencial sigue sin verificar.",
            ["providers.notice.redirectNotFollowed"] = "El provider respondió con una redirección; no se siguió para proteger la credencial.",
            ["providers.notice.savedUnverified"] = "Credencial guardada sin verificación de red.",
            ["providers.notice.connectedValidated"] = "La API key de Anthropic fue aceptada por Models API.",
            ["providers.notice.storedKeyRejected"] = "El provider rechazó la credencial guardada en la última prueba.",
            ["providers.notice.storedKeyUnreachable"] = "No se pudo contactar al provider en la última prueba.",
            ["providers.notice.storedKeyUnverified"] = "Hay una credencial guardada que aún no se ha verificado.",
            ["providers.notice.missingKey"] = "Falta la credencial «{ref}».",
            ["providers.notice.discoveryTruncated"] = "El provider anunció más modelos; se registró una lista acotada.",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title.chatgpt"] = "ChatGPT account", ["title.claude"] = "Claude account", ["title.api"] = "API connections", ["title.connect"] = "Connect provider",
            ["label.chatgpt"] = "ChatGPT · subscription", ["label.claude"] = "Claude · configured OAuth", ["label.api"] = "API providers",
            ["status.connected"] = "connected", ["status.expired"] = "session expired", ["status.disconnected"] = "Not signed in",
            ["status.notConfigured"] = "Not configured", ["status.unavailable"] = "Status unavailable", ["status.noRows"] = "No Claude OAuth accounts are configured in User settings.", ["status.noProviders"] = "No providers are configured.",
            ["status.dead"] = "Credential revoked",
            ["account.noApiKey"] = "ChatGPT uses a subscription account; no API key is needed.", ["account.configSource"] = "OAuth identity declared in User configuration.",
            ["account.email"] = "Email", ["account.name"] = "Name", ["account.plan"] = "Plan", ["account.expires"] = "Expires",
            ["account.logoutQuestion"] = "Sign out of ChatGPT?", ["account.claudeCount"] = "Configured Claude accounts: {0}",
            ["chatgpt.phase.preparing"] = "Preparing authorization…",
            ["action.manageChatgpt"] = "Manage ChatGPT account…", ["action.manageClaude"] = "Manage Claude accounts…", ["action.manageApi"] = "Manage API keys…",
            ["action.login"] = "Sign in…", ["action.logout"] = "Sign out…", ["action.back"] = "Back", ["action.cancel"] = "Cancel",
            ["action.confirmLogout"] = "Yes, sign out", ["action.browserLogin"] = "Sign in · browser link", ["action.deviceLogin"] = "Sign in · device code",
            ["action.connect"] = "Connect…", ["action.test"] = "Test", ["action.discover"] = "Discover models", ["action.disconnect"] = "Disconnect",
            ["action.save"] = "Save", ["action.validate"] = "Validate now with the provider API",
            ["help.providerState"] = "Status and timestamps come from the Host; opening this page makes no connections.",
            ["help.apiKey"] = "The API key is sent directly to the Host.", ["help.keyboard"] = "↑↓ select · Esc back",
            ["help.login"] = "A browser will open to authorize this configured identity. Esc cancels.",
            ["help.loginProgress"] = "Authorization is active. Esc cancels; nothing is saved until the flow completes.",
            ["help.noProviderAction"] = "This provider does not implement this capability; it will not be routed to another provider.",
            ["result.connecting"] = "Processing connection…", ["result.removed"] = "Connection removed.", ["result.failed"] = "The operation failed. Check the provider configuration.",
            ["result.enterKey"] = "Enter an API key.", ["result.state"] = "Status: ", ["result.validated"] = "validated ", ["result.models"] = "models ",
            ["result.registered"] = "Models registered: {0}", ["result.quota"] = "Reported quota windows: {0}", ["result.truncated"] = "The reported catalog was capped.",
            ["method.apiKey"] = "API key", ["method.subscription"] = "subscription", ["method.none"] = "no method",
            ["state.connected"] = "Connected", ["state.notConfigured"] = "Not configured", ["state.expired"] = "Expired", ["state.invalid"] = "Invalid", ["state.unreachable"] = "Unreachable", ["state.unknown"] = "Unknown",
            ["date.validated"] = "validated ", ["date.lastOk"] = "last successful check ", ["date.models"] = "models ", ["date.none"] = "No measurements recorded.",
            ["error.readApi"] = "Could not read provider connection status.", ["error.readAccount"] = "Could not read the ChatGPT session. Check omni doctor.",
            ["error.logout"] = "Could not sign out.", ["error.login"] = "Sign-in failed. Retry or use device code; check omni doctor.",
            ["result.loginOk"] = "Signed in successfully.", ["result.loginFailed"] = "Sign-in did not complete.", ["result.loginCancelled"] = "Sign-in cancelled.",
            ["claude.identityUnavailable"] = "The configured OAuth identity is unavailable.", ["claude.statusUnavailable"] = "This account could not be inspected locally.",
            ["claude.phase.preparing"] = "Preparing authorization…", ["claude.phase.listening"] = "Waiting for browser confirmation…", ["claude.phase.awaiting"] = "Complete authorization in the browser…",
            ["claude.phase.exchanging"] = "Completing authorization…", ["claude.phase.profile"] = "Reading the authorized profile…", ["claude.phase.persisting"] = "Saving the session…",
            ["claude.phase.succeeded"] = "Claude session connected.", ["claude.phase.failed"] = "Claude sign-in did not complete.", ["claude.logoutConfirm"] = "Sign out of this Claude session?",
            ["claude.oauth.failed.cancelled"] = "Sign-in cancelled.", ["claude.oauth.failed.timeout"] = "Sign-in timed out before the code arrived.",
            ["claude.oauth.failed.transport"] = "Could not open the callback port.", ["claude.oauth.failed.browser"] = "Could not open the browser.",
            ["claude.oauth.failed.callback"] = "The code received does not belong to this sign-in.", ["claude.oauth.failed.exchange"] = "Anthropic rejected the code exchange.",
            ["claude.oauth.failed.network"] = "Could not reach the authentication server.", ["claude.oauth.failed.protocol"] = "The authentication response was invalid.",
            ["claude.oauth.failed.storage"] = "Could not save the encrypted session.",
            ["providers.notice.unreachable"] = "The provider could not be contacted; status was not measured.",
            ["providers.notice.rateLimited"] = "The provider rate-limited the request; the credential remains unverified.",
            ["providers.notice.redirectNotFollowed"] = "The provider redirected the request; it was not followed to protect the credential.",
            ["providers.notice.savedUnverified"] = "Credential saved without network verification.",
            ["providers.notice.connectedValidated"] = "The Anthropic API key was accepted by the Models API.",
            ["providers.notice.storedKeyRejected"] = "The provider rejected the stored credential on the last test.",
            ["providers.notice.storedKeyUnreachable"] = "The provider could not be contacted on the last test.",
            ["providers.notice.storedKeyUnverified"] = "A credential is stored but has not been verified.",
            ["providers.notice.missingKey"] = "Missing credential '{ref}'.",
            ["providers.notice.discoveryTruncated"] = "The provider reported more models; a capped list was registered.",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title.chatgpt"] = "Compte ChatGPT", ["title.claude"] = "Compte Claude", ["title.api"] = "Connexions API", ["title.connect"] = "Connecter un fournisseur",
            ["label.chatgpt"] = "ChatGPT · abonnement", ["label.claude"] = "Claude · OAuth configuré", ["label.api"] = "Fournisseurs API",
            ["status.connected"] = "connecté", ["status.expired"] = "session expirée", ["status.disconnected"] = "Non connecté",
            ["status.notConfigured"] = "Non configuré", ["status.unavailable"] = "État indisponible", ["status.noRows"] = "Aucun compte OAuth Claude n’est configuré dans User.", ["status.noProviders"] = "Aucun fournisseur configuré.",
            ["status.dead"] = "Identifiant révoqué",
            ["account.noApiKey"] = "ChatGPT utilise un compte par abonnement ; aucune clé API n’est nécessaire.", ["account.configSource"] = "Identité OAuth déclarée dans la configuration User.",
            ["account.email"] = "E-mail", ["account.name"] = "Nom", ["account.plan"] = "Offre", ["account.expires"] = "Expiration",
            ["account.logoutQuestion"] = "Se déconnecter de ChatGPT ?", ["account.claudeCount"] = "Comptes Claude configurés : {0}",
            ["chatgpt.phase.preparing"] = "Préparation de l’autorisation…",
            ["action.manageChatgpt"] = "Gérer le compte ChatGPT…", ["action.manageClaude"] = "Gérer les comptes Claude…", ["action.manageApi"] = "Gérer les clés API…",
            ["action.login"] = "Se connecter…", ["action.logout"] = "Se déconnecter…", ["action.back"] = "Retour", ["action.cancel"] = "Annuler",
            ["action.confirmLogout"] = "Oui, se déconnecter", ["action.browserLogin"] = "Connexion · lien navigateur", ["action.deviceLogin"] = "Connexion · code appareil",
            ["action.connect"] = "Connecter…", ["action.test"] = "Tester", ["action.discover"] = "Découvrir les modèles", ["action.disconnect"] = "Déconnecter",
            ["action.save"] = "Enregistrer", ["action.validate"] = "Vérifier maintenant avec l’API du fournisseur",
            ["help.providerState"] = "L’état et les dates viennent du Host ; ouvrir cette page ne crée aucune connexion.",
            ["help.apiKey"] = "La clé API est transmise directement au Host.", ["help.keyboard"] = "↑↓ choisir · Échap retour",
            ["help.login"] = "Un navigateur s’ouvrira pour autoriser cette identité configurée. Échap annule.",
            ["help.loginProgress"] = "Autorisation en cours. Échap annule ; rien n’est enregistré avant la fin.",
            ["help.noProviderAction"] = "Cette capacité n’est pas prise en charge par ce fournisseur ; aucun autre fournisseur ne sera utilisé.",
            ["result.connecting"] = "Traitement de la connexion…", ["result.removed"] = "Connexion supprimée.", ["result.failed"] = "Échec de l’opération. Vérifiez la configuration du fournisseur.",
            ["result.enterKey"] = "Saisissez une clé API.", ["result.state"] = "État : ", ["result.validated"] = "vérifiée ", ["result.models"] = "modèles ",
            ["result.registered"] = "Modèles enregistrés : {0}", ["result.quota"] = "Fenêtres de quota signalées : {0}", ["result.truncated"] = "Le catalogue signalé a été limité.",
            ["method.apiKey"] = "clé API", ["method.subscription"] = "abonnement", ["method.none"] = "aucune méthode",
            ["state.connected"] = "Connecté", ["state.notConfigured"] = "Non configuré", ["state.expired"] = "Expiré", ["state.invalid"] = "Invalide", ["state.unreachable"] = "Injoignable", ["state.unknown"] = "Inconnu",
            ["date.validated"] = "vérifiée ", ["date.lastOk"] = "dernier succès ", ["date.models"] = "modèles ", ["date.none"] = "Aucune mesure enregistrée.",
            ["error.readApi"] = "Impossible de lire l’état des connexions API.", ["error.readAccount"] = "Impossible de lire la session ChatGPT. Vérifiez omni doctor.",
            ["error.logout"] = "Impossible de se déconnecter.", ["error.login"] = "Échec de connexion. Réessayez ou utilisez le code appareil ; vérifiez omni doctor.",
            ["result.loginOk"] = "Connexion réussie.", ["result.loginFailed"] = "La connexion n’a pas abouti.", ["result.loginCancelled"] = "Connexion annulée.",
            ["claude.identityUnavailable"] = "L’identité OAuth configurée est indisponible.", ["claude.statusUnavailable"] = "Impossible d’inspecter ce compte localement.",
            ["claude.phase.preparing"] = "Préparation de l’autorisation…", ["claude.phase.listening"] = "En attente de confirmation du navigateur…", ["claude.phase.awaiting"] = "Terminez l’autorisation dans le navigateur…",
            ["claude.phase.exchanging"] = "Finalisation de l’autorisation…", ["claude.phase.profile"] = "Lecture du profil autorisé…", ["claude.phase.persisting"] = "Enregistrement de la session…",
            ["claude.phase.succeeded"] = "Session Claude connectée.", ["claude.phase.failed"] = "La connexion Claude n’a pas abouti.", ["claude.logoutConfirm"] = "Se déconnecter de cette session Claude ?",
            ["claude.oauth.failed.cancelled"] = "Connexion annulée.", ["claude.oauth.failed.timeout"] = "Le délai a expiré avant la réception du code.",
            ["claude.oauth.failed.transport"] = "Impossible d’ouvrir le port de retour.", ["claude.oauth.failed.browser"] = "Impossible d’ouvrir le navigateur.",
            ["claude.oauth.failed.callback"] = "Le code reçu ne correspond pas à cette connexion.", ["claude.oauth.failed.exchange"] = "Anthropic a refusé l’échange du code.",
            ["claude.oauth.failed.network"] = "Impossible de joindre le serveur d’authentification.", ["claude.oauth.failed.protocol"] = "La réponse d’authentification est invalide.",
            ["claude.oauth.failed.storage"] = "Impossible d’enregistrer la session chiffrée.",
            ["providers.notice.unreachable"] = "Impossible de contacter le fournisseur ; l’état n’a pas été mesuré.",
            ["providers.notice.rateLimited"] = "Le fournisseur a limité la requête ; l’identifiant reste non vérifié.",
            ["providers.notice.redirectNotFollowed"] = "La redirection du fournisseur n’a pas été suivie afin de protéger l’identifiant.",
            ["providers.notice.savedUnverified"] = "Identifiant enregistré sans vérification réseau.",
            ["providers.notice.connectedValidated"] = "La clé API Anthropic a été acceptée par l’API Models.",
            ["providers.notice.storedKeyRejected"] = "Le fournisseur a rejeté l’identifiant enregistré lors du dernier test.",
            ["providers.notice.storedKeyUnreachable"] = "Impossible de contacter le fournisseur lors du dernier test.",
            ["providers.notice.storedKeyUnverified"] = "Un identifiant est enregistré mais n’a pas été vérifié.",
            ["providers.notice.missingKey"] = "Identifiant manquant « {ref} ».",
            ["providers.notice.discoveryTruncated"] = "Le fournisseur a signalé plus de modèles ; une liste limitée a été enregistrée.",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title.chatgpt"] = "Account ChatGPT", ["title.claude"] = "Account Claude", ["title.api"] = "Connessioni API", ["title.connect"] = "Connetti provider",
            ["label.chatgpt"] = "ChatGPT · abbonamento", ["label.claude"] = "Claude · OAuth configurato", ["label.api"] = "Provider API",
            ["status.connected"] = "connesso", ["status.expired"] = "sessione scaduta", ["status.disconnected"] = "Accesso non effettuato",
            ["status.notConfigured"] = "Non configurato", ["status.unavailable"] = "Stato non disponibile", ["status.noRows"] = "Nessun account OAuth Claude configurato in User.", ["status.noProviders"] = "Nessun provider configurato.",
            ["status.dead"] = "Credenziale revocata",
            ["account.noApiKey"] = "ChatGPT usa un account in abbonamento; non serve una chiave API.", ["account.configSource"] = "Identità OAuth dichiarata nella configurazione User.",
            ["account.email"] = "E-mail", ["account.name"] = "Nome", ["account.plan"] = "Piano", ["account.expires"] = "Scadenza",
            ["account.logoutQuestion"] = "Uscire da ChatGPT?", ["account.claudeCount"] = "Account Claude configurati: {0}",
            ["chatgpt.phase.preparing"] = "Preparazione autorizzazione…",
            ["action.manageChatgpt"] = "Gestisci account ChatGPT…", ["action.manageClaude"] = "Gestisci account Claude…", ["action.manageApi"] = "Gestisci chiavi API…",
            ["action.login"] = "Accedi…", ["action.logout"] = "Esci…", ["action.back"] = "Indietro", ["action.cancel"] = "Annulla",
            ["action.confirmLogout"] = "Sì, esci", ["action.browserLogin"] = "Accedi · link browser", ["action.deviceLogin"] = "Accedi · codice dispositivo",
            ["action.connect"] = "Connetti…", ["action.test"] = "Verifica", ["action.discover"] = "Rileva modelli", ["action.disconnect"] = "Disconnetti",
            ["action.save"] = "Salva", ["action.validate"] = "Verifica ora con l’API del provider",
            ["help.providerState"] = "Stato e date provengono dall’Host; aprire questa pagina non crea connessioni.",
            ["help.apiKey"] = "La chiave API viene inviata direttamente all’Host.", ["help.keyboard"] = "↑↓ seleziona · Esc indietro",
            ["help.login"] = "Si aprirà il browser per autorizzare questa identità configurata. Esc annulla.",
            ["help.loginProgress"] = "Autorizzazione in corso. Esc annulla; nulla viene salvato prima del completamento.",
            ["help.noProviderAction"] = "Questa capacità non è implementata per il provider; non verrà usato un altro provider.",
            ["result.connecting"] = "Elaborazione connessione…", ["result.removed"] = "Connessione rimossa.", ["result.failed"] = "Operazione non riuscita. Controlla la configurazione del provider.",
            ["result.enterKey"] = "Inserisci una chiave API.", ["result.state"] = "Stato: ", ["result.validated"] = "verificata ", ["result.models"] = "modelli ",
            ["result.registered"] = "Modelli registrati: {0}", ["result.quota"] = "Finestre quota riportate: {0}", ["result.truncated"] = "Il catalogo riportato è stato limitato.",
            ["method.apiKey"] = "chiave API", ["method.subscription"] = "abbonamento", ["method.none"] = "nessun metodo",
            ["state.connected"] = "Connesso", ["state.notConfigured"] = "Non configurato", ["state.expired"] = "Scaduto", ["state.invalid"] = "Non valido", ["state.unreachable"] = "Non raggiungibile", ["state.unknown"] = "Sconosciuto",
            ["date.validated"] = "verificata ", ["date.lastOk"] = "ultimo OK ", ["date.models"] = "modelli ", ["date.none"] = "Nessuna misurazione registrata.",
            ["error.readApi"] = "Impossibile leggere lo stato delle connessioni API.", ["error.readAccount"] = "Impossibile leggere la sessione ChatGPT. Controlla omni doctor.",
            ["error.logout"] = "Impossibile uscire.", ["error.login"] = "Accesso non riuscito. Riprova o usa il codice dispositivo; controlla omni doctor.",
            ["result.loginOk"] = "Accesso effettuato.", ["result.loginFailed"] = "Accesso non completato.", ["result.loginCancelled"] = "Accesso annullato.",
            ["claude.identityUnavailable"] = "L’identità OAuth configurata non è disponibile.", ["claude.statusUnavailable"] = "Impossibile ispezionare localmente questo account.",
            ["claude.phase.preparing"] = "Preparazione autorizzazione…", ["claude.phase.listening"] = "In attesa della conferma del browser…", ["claude.phase.awaiting"] = "Completa l’autorizzazione nel browser…",
            ["claude.phase.exchanging"] = "Completamento autorizzazione…", ["claude.phase.profile"] = "Lettura del profilo autorizzato…", ["claude.phase.persisting"] = "Salvataggio sessione…",
            ["claude.phase.succeeded"] = "Sessione Claude connessa.", ["claude.phase.failed"] = "Accesso Claude non completato.", ["claude.logoutConfirm"] = "Uscire da questa sessione Claude?",
            ["claude.oauth.failed.cancelled"] = "Accesso annullato.", ["claude.oauth.failed.timeout"] = "Il codice non è arrivato entro il tempo previsto.",
            ["claude.oauth.failed.transport"] = "Impossibile aprire la porta di callback.", ["claude.oauth.failed.browser"] = "Impossibile aprire il browser.",
            ["claude.oauth.failed.callback"] = "Il codice ricevuto non corrisponde a questo accesso.", ["claude.oauth.failed.exchange"] = "Anthropic ha rifiutato lo scambio del codice.",
            ["claude.oauth.failed.network"] = "Impossibile raggiungere il server di autenticazione.", ["claude.oauth.failed.protocol"] = "La risposta di autenticazione non è valida.",
            ["claude.oauth.failed.storage"] = "Impossibile salvare la sessione crittografata.",
            ["providers.notice.unreachable"] = "Impossibile contattare il provider; lo stato non è stato misurato.",
            ["providers.notice.rateLimited"] = "Il provider ha limitato la richiesta; la credenziale resta non verificata.",
            ["providers.notice.redirectNotFollowed"] = "Il redirect del provider non è stato seguito per proteggere la credenziale.",
            ["providers.notice.savedUnverified"] = "Credenziale salvata senza verifica di rete.",
            ["providers.notice.connectedValidated"] = "La chiave API Anthropic è stata accettata dall’API Models.",
            ["providers.notice.storedKeyRejected"] = "Il provider ha rifiutato la credenziale salvata nell’ultimo test.",
            ["providers.notice.storedKeyUnreachable"] = "Impossibile contattare il provider nell’ultimo test.",
            ["providers.notice.storedKeyUnverified"] = "È salvata una credenziale che non è ancora stata verificata.",
            ["providers.notice.missingKey"] = "Credenziale «{ref}» mancante.",
            ["providers.notice.discoveryTruncated"] = "Il provider ha segnalato più modelli; è stato registrato un elenco limitato.",
        },
    ];

    public static IReadOnlyCollection<string> Keys { get; } = Catalogs[0].Keys
        .Select(key => "accounts.ui." + key)
        .Concat(Catalogs[0].Keys.Where(key => key.StartsWith("claude.oauth.failed.", StringComparison.Ordinal)))
        .Concat(["claude.account.identityUnavailable", "claude.account.statusUnavailable"])
        .Concat(Catalogs[0].Keys.Where(key => key.StartsWith("providers.notice.", StringComparison.Ordinal)))
        .ToArray();

    public static bool ContainsKey(string key) => key.StartsWith("accounts.ui.", StringComparison.Ordinal)
        ? Catalogs[0].ContainsKey(key["accounts.ui.".Length..])
        : key.StartsWith("claude.oauth.failed.", StringComparison.Ordinal)
            ? Catalogs[0].ContainsKey(key)
        : key.StartsWith("providers.notice.", StringComparison.Ordinal)
            ? Catalogs[0].ContainsKey(key)
            : key.StartsWith("claude.account.", StringComparison.Ordinal)
            && key is "claude.account.identityUnavailable" or "claude.account.statusUnavailable";

    public static bool TryTranslate(string locale, string key, out string text)
    {
        if (key.StartsWith("accounts.ui.", StringComparison.Ordinal)
            && Catalogs[LocaleIndex(locale)].TryGetValue(key["accounts.ui.".Length..], out text!)) return true;
        if (key.StartsWith("providers.notice.", StringComparison.Ordinal)
            && Catalogs[LocaleIndex(locale)].TryGetValue(key, out text!)) return true;
        if (key.StartsWith("claude.oauth.failed.", StringComparison.Ordinal)
            && Catalogs[LocaleIndex(locale)].TryGetValue(key, out text!)) return true;
        if (key.StartsWith("claude.account.", StringComparison.Ordinal))
        {
            var id = key["claude.account.".Length..];
            if (id == "identityUnavailable" && Catalogs[LocaleIndex(locale)].TryGetValue("claude.identityUnavailable", out text!)) return true;
            if (id == "statusUnavailable" && Catalogs[LocaleIndex(locale)].TryGetValue("claude.statusUnavailable", out text!)) return true;
        }
        text = "";
        return false;
    }

    private static int LocaleIndex(string locale) => OmniCore.Protocol.UiLocaleCodes.NormalizeOrDefault(locale) switch
    { "en" => 1, "fr" => 2, "it" => 3, _ => 0 };
}
