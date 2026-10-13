namespace OmniCore.Client;

using System.Collections.ObjectModel;

/// <summary>Localized copy for registering user model definitions; provider IDs and model IDs remain literal.</summary>
public static class ModelCatalogPreferencesResources
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs =
        new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["es"] = Build(
                    "Catálogo de modelos", "Catálogo de modelos", "Registra modelos que ya ofrece un proveedor configurado. No consulta la red ni ejecuta modelos.",
                    "Modelos", "Proveedor", "Familia", "Contexto", "Contexto utilizable", "Salida máx. (opcional)", "Parám. (miles M)",
                    "Aliases, uno por línea", "Agregar", "Editar", "Eliminar", "Guardar", "Cancelar", "Volver", "Nuevo modelo",
                    "ID del modelo", "ID y aliases literales · parámetros en miles de millones.\nSalida vacía: sin cota de OmniCore; rige el proveedor.",
                    "Eliminar sólo quita esta definición de User. Confirma que el modelo no se usa en otras carpetas; esas referencias no se pueden inspeccionar desde aquí.",
                    "Confirmar eliminación", "Eliminar modelo", "No se puede eliminar mientras haya referencias conocidas.",
                    "No hay modelos registrados.", "Selecciona un modelo para editar.", "La configuración no está disponible.",
                    "Configuración guardada.", "No se pudo guardar. Revisa los valores y vuelve a intentarlo.",
                    "La configuración cambió en otro lugar. Recarga antes de guardar.", "Proveedor desconocido.", "Ese identificador ya existe.",
                    "El modelo ya no existe.", "Identificador no válido.", "Proveedor no válido.", "Límites de contexto o salida no válidos.",
                    "Parámetros no válidos.", "Aliases vacíos o duplicados no están permitidos.", "Un alias coincide con más de un modelo.",
                    "El modelo está referenciado por la configuración actual.", "El catálogo de configuración no está disponible.",
                    "No se pudo revisar el ámbito local. Eliminar está deshabilitado.", "Referencia: modelo predeterminado de User.",
                    "Referencia: modelo predeterminado de Project.", "Referencia: modelo predeterminado de Workspace.",
                    "Referencia en una ruta de ejecución.", "En otras carpetas también podría haber referencias; no se modifican.", "Filtrar", "{index} de {total}", "inicio", "fin", "Ningún modelo coincide con el filtro.", "Modelo · Proveedor (familia) · Contexto"),
                ["en"] = Build(
                    "Model catalog", "Model catalog", "Register models already offered by a configured provider. No network or model execution is used.",
                    "Models", "Provider", "Family", "Context", "Usable context", "Max output (optional)", "Params (billions)",
                    "Aliases, one per line", "Add", "Edit", "Delete", "Save", "Cancel", "Back", "New model",
                    "Model ID", "Literal IDs and aliases · parameters in billions.\nEmpty output: no OmniCore cap; provider limits still apply.",
                    "Delete only removes this User definition. Confirm the model is unused in other folders; their references cannot be inspected here.",
                    "Confirm deletion", "Delete model", "Deletion is blocked while known references exist.",
                    "No models are registered.", "Select a model to edit.", "Configuration is unavailable.",
                    "Configuration saved.", "Could not save. Check the values and try again.",
                    "Configuration changed elsewhere. Reload before saving.", "Unknown provider.", "That identifier already exists.",
                    "The model no longer exists.", "Invalid identifier.", "Invalid provider.", "Invalid context or output limits.",
                    "Invalid parameter count.", "Empty or duplicate aliases are not allowed.", "An alias resolves to more than one model.",
                    "The model is referenced by current configuration.", "The configuration catalog is unavailable.",
                    "The local scope could not be checked. Deletion is disabled.", "Reference: User default model.",
                    "Reference: Project default model.", "Reference: Workspace default model.",
                    "Reference in a routing list.", "Other folders may also reference this model; they are not changed.", "Filter", "{index} of {total}", "start", "end", "No model matches the filter.", "Model · Provider (family) · Context"),
                ["fr"] = Build(
                    "Catalogue de modèles", "Catalogue de modèles", "Enregistre les modèles déjà proposés par un fournisseur configuré. Aucun accès réseau ni lancement de modèle.",
                    "Modèles", "Fournisseur", "Famille", "Contexte", "Contexte utilisable", "Sortie max. (option.)", "Param. (milliards)",
                    "Alias, un par ligne", "Ajouter", "Modifier", "Supprimer", "Enregistrer", "Annuler", "Retour", "Nouveau modèle",
                    "ID du modèle", "IDs et alias littéraux · paramètres en milliards.\nSortie vide : pas de plafond OmniCore ; limites du fournisseur.",
                    "La suppression retire uniquement cette définition User. Vérifiez l’absence d’usage dans les autres dossiers ; leurs références ne sont pas inspectées ici.",
                    "Confirmer la suppression", "Supprimer le modèle", "Suppression bloquée tant que des références connues existent.",
                    "Aucun modèle enregistré.", "Sélectionnez un modèle à modifier.", "Configuration indisponible.",
                    "Configuration enregistrée.", "Échec de l’enregistrement. Vérifiez les valeurs et réessayez.",
                    "La configuration a changé ailleurs. Rechargez avant d’enregistrer.", "Fournisseur inconnu.", "Cet identifiant existe déjà.",
                    "Le modèle n’existe plus.", "Identifiant non valide.", "Fournisseur non valide.", "Limites de contexte ou de sortie non valides.",
                    "Nombre de paramètres non valide.", "Les alias vides ou en double sont interdits.", "Un alias désigne plusieurs modèles.",
                    "Le modèle est référencé par la configuration actuelle.", "Catalogue de configuration indisponible.",
                    "Impossible de vérifier la portée locale. La suppression est désactivée.", "Référence : modèle par défaut User.",
                    "Référence : modèle par défaut Project.", "Référence : modèle par défaut Workspace.",
                    "Référence dans une liste de routage.", "D’autres dossiers peuvent référencer ce modèle ; ils ne sont pas modifiés.", "Filtrer", "{index} sur {total}", "début", "fin", "Aucun modèle ne correspond au filtre.", "Modèle · Fournisseur (famille) · Contexte"),
                ["it"] = Build(
                    "Catalogo modelli", "Catalogo modelli", "Registra modelli già offerti da un provider configurato. Nessuna rete o esecuzione del modello.",
                    "Modelli", "Provider", "Famiglia", "Contesto", "Contesto utilizzabile", "Output max. (opzionale)", "Param. (mld)",
                    "Alias, uno per riga", "Aggiungi", "Modifica", "Elimina", "Salva", "Annulla", "Indietro", "Nuovo modello",
                    "ID modello", "ID e alias letterali · parametri in miliardi.\nOutput vuoto: nessun limite OmniCore; restano i limiti del provider.",
                    "L’eliminazione rimuove solo questa definizione User. Verifica che il modello non sia usato in altre cartelle; i relativi riferimenti non sono ispezionati qui.",
                    "Conferma eliminazione", "Elimina modello", "Eliminazione bloccata finché esistono riferimenti noti.",
                    "Nessun modello registrato.", "Seleziona un modello da modificare.", "Configurazione non disponibile.",
                    "Configurazione salvata.", "Impossibile salvare. Controlla i valori e riprova.",
                    "La configurazione è cambiata altrove. Ricarica prima di salvare.", "Provider sconosciuto.", "L’identificatore esiste già.",
                    "Il modello non esiste più.", "Identificatore non valido.", "Provider non valido.", "Limiti di contesto o output non validi.",
                    "Numero di parametri non valido.", "Alias vuoti o duplicati non sono consentiti.", "Un alias corrisponde a più modelli.",
                    "Il modello è referenziato dalla configurazione corrente.", "Catalogo di configurazione non disponibile.",
                    "Impossibile verificare l’ambito locale. L’eliminazione è disabilitata.", "Riferimento: modello predefinito User.",
                    "Riferimento: modello predefinito Project.", "Riferimento: modello predefinito Workspace.",
                    "Riferimento in una lista di instradamento.", "Altre cartelle potrebbero riferire questo modello; non vengono modificate.", "Filtra", "{index} di {total}", "inizio", "fine", "Nessun modello corrisponde al filtro.", "Modello · Provider (famiglia) · Contesto")
            });

    private static IReadOnlyDictionary<string, string> Build(params string[] values)
    {
        string[] keys = ["title", "open", "intro", "models", "provider", "family", "context", "usableContext", "maxOutput", "parameters",
            "aliases", "add", "edit", "delete", "save", "cancel", "back", "newModel", "modelId", "literalHelp",
            "deleteWarning", "confirmTitle", "confirmDelete", "deleteBlocked", "empty", "selectHint", "unavailable", "saved",
            "saveFailed", "stale", "unknownProvider", "modelAlreadyExists", "modelNotFound", "invalidId", "invalidProvider",
            "invalidLimits", "invalidParameters", "invalidAliases", "ambiguousAlias", "referenced", "catalogUnavailable",
            "workspaceScopeUnavailable", "referenceUserDefault", "referenceProjectDefault", "referenceWorkspaceDefault",
            "referenceRouting", "otherFoldersWarning", "filter", "position", "atStart", "atEnd", "noMatches", "columns"];
        if (values.Length != keys.Length) throw new InvalidOperationException("Model catalog resource shape mismatch.");
        return new ReadOnlyDictionary<string, string>(keys.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second,
            StringComparer.Ordinal));
    }

    private static readonly string[] AllKeys = Catalogs.Values.SelectMany(catalog => catalog.Keys).Distinct(StringComparer.Ordinal)
        .OrderBy(key => key, StringComparer.Ordinal).ToArray();
    public static IReadOnlyCollection<string> Keys { get; } = Array.AsReadOnly(AllKeys.Select(key => "modelCatalogPreferences." + key).ToArray());
    public static bool ContainsKey(string key) => TryGetKey(key, out _);
    public static bool TryTranslate(string locale, string key, out string text)
    {
        text = "";
        return TryGetKey(key, out var localKey) && Catalogs.TryGetValue(locale, out var catalog)
            && catalog.TryGetValue(localKey, out text!);
    }

    private static bool TryGetKey(string key, out string localKey)
    {
        const string prefix = "modelCatalogPreferences.";
        localKey = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : "";
        var candidate = localKey;
        return candidate.Length > 0 && Catalogs.Values.Any(catalog => catalog.ContainsKey(candidate));
    }
}
